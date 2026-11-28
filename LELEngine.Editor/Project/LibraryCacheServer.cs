using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace LELEngine.Editor
{
	/// <summary>
	///     The library cache server (Edit > Preferences > Cache Server): a store of import results shared by the editors of
	///     a team, so what one of them made the others take instead of making it again (a fresh checkout's full import, an
	///     image baked at the high quality setting for minutes). It holds the Library's caches that depend only on the
	///     project's files: model imports (Library/ModelCache), images baked as builds ship them (Library/TextureCache) and
	///     the meshes' distance fields (Library/DistanceFieldCache), each under a kind and a key computed from the source
	///     file's content, its import settings and the importer's version (<see cref="IImportResultCache" />), never from
	///     paths or file times.
	///     <para>
	///         An import whose result is missing from the Library asks the server first (<see cref="TryFetch" />, on the
	///         importing thread; the bytes are checked against the hash the server stored); a result made here is queued and
	///         sent by a thread of its own (<see cref="Offer" />), after asking whether the server has it already. Files up to
	///         the server's part size go in one request, bigger ones in parts (a proxy in front of the server may cap a
	///         request's size). When the server cannot be reached, imports make their results here and the server is not
	///         asked again for <see cref="RetryAfterSeconds" />; a refused access key stops it until the preferences change.
	///     </para>
	///     <para>
	///         The protocol (relative to <see cref="CacheServerPreferences.Url" />, the access key in <c>X-Api-Key</c>):
	///         <c>GET status</c>; <c>GET|HEAD files/{kind}/{key}</c> (the bytes, their SHA-256 in <c>X-Content-SHA256</c>);
	///         <c>PUT files/{kind}/{key}</c> (one request, its SHA-256 in that header); <c>POST uploads</c>
	///         <c>{path, size, sha256}</c>, <c>PUT uploads/{id}/{part}</c>, <c>POST uploads/{id}/complete</c>,
	///         <c>DELETE uploads/{id}</c> (in parts); <c>GET list</c>, <c>DELETE files/{kind}/{key}</c>.
	///     </para>
	/// </summary>
	internal sealed class LibraryCacheServer : IImportResultCache
	{
		#region PublicFields

		public enum ServerState
		{
			/// <summary>Turned off in the preferences, or no address.</summary>
			Off,

			/// <summary>Set up; nothing has been asked yet, or the last request worked.</summary>
			Ready,

			/// <summary>The last request could not reach the server: it is not asked again for a while.</summary>
			Unreachable,

			/// <summary>The server refused the access key: not asked again until the preferences change.</summary>
			Refused
		}

		/// <summary>The one store; <see cref="ModelAssets.ResultCache" /> is set to it.</summary>
		public static readonly LibraryCacheServer Instance = new LibraryCacheServer();

		public const string AccessKeyHeader = "X-Api-Key";
		public const string HashHeader = "X-Content-SHA256";

		/// <summary>After a request could not reach the server, imports do not ask it for this long.</summary>
		public const int RetryAfterSeconds = 60;

		public bool CanFetch => Usable(out Settings settings) && settings.Download;
		public bool CanOffer => Usable(out Settings settings) && settings.Upload && settings.ServerUpload != false;

		public ServerState State
		{
			get
			{
				Settings settings = Current();
				if (settings == null)
				{
					return ServerState.Off;
				}

				if (settings.Refused)
				{
					return ServerState.Refused;
				}

				return System.Diagnostics.Stopwatch.GetTimestamp() < Volatile.Read(ref settings.UnreachableUntil) ? ServerState.Unreachable : ServerState.Ready;
			}
		}

		/// <summary>The last failure's text (null: none since the preferences changed).</summary>
		public string LastError => Current()?.LastError;

		/// <summary>Results sent or waiting to be sent.</summary>
		public int Waiting
		{
			get
			{
				lock (queue)
				{
					return queue.Count + (sending ? 1 : 0);
				}
			}
		}

		// This editor's session, for the preferences window and the import's summary.
		public long Fetched => Interlocked.Read(ref fetched);
		public long FetchedBytes => Interlocked.Read(ref fetchedBytes);
		public long Misses => Interlocked.Read(ref misses);
		public long Sent => Interlocked.Read(ref sent);
		public long SentBytes => Interlocked.Read(ref sentBytes);
		public long AlreadyStored => Interlocked.Read(ref alreadyStored);
		public long Failures => Interlocked.Read(ref failures);

		#endregion

		#region PrivateFields

		private const int HeadersTimeoutSeconds = 20;
		private const int ReadTimeoutSeconds = 60;
		private const long DefaultPartSize = 32L * 1024 * 1024;

		private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

		// One client for every request: its connection pool is shared by the importing threads and the sender.
		private static readonly HttpClient Client = CreateClient();

		private readonly Queue<(string Kind, string Key, string File)> queue = new Queue<(string, string, string)>();
		private readonly HashSet<string> queued = new HashSet<string>(StringComparer.Ordinal);
		private bool sending;

		private Settings settings;
		private int settingsVersion = -1;

		private long fetched;
		private long fetchedBytes;
		private long misses;
		private long sent;
		private long sentBytes;
		private long alreadyStored;
		private long failures;

		private volatile string connectionTest;

		#endregion

		#region PublicMethods

		/// <summary>The result of the last <see cref="TestConnection" /> (null: none, "..." while it runs).</summary>
		public string ConnectionTest => connectionTest;

		public bool TryFetch(string kind, string key, string file)
		{
			return TryFetch(kind, key, file, null);
		}

		/// <summary>
		///     <see cref="TryFetch(string,string,string)" /> with <paramref name="accept" />: the downloaded file (a temporary
		///     one beside <paramref name="file" />) is only moved into place when it passes (a format's own check).
		/// </summary>
		public bool TryFetch(string kind, string key, string file, Func<string, bool> accept)
		{
			if (!Usable(out Settings settings) || !settings.Download)
			{
				return false;
			}

			string temporary = file + "." + Environment.CurrentManagedThreadId + ".download";
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, settings.FileUrl(kind, key));
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HeadersTimeoutSeconds));
				using HttpResponseMessage response = Send(settings, request, timeout.Token);
				if (response.StatusCode == HttpStatusCode.NotFound)
				{
					Interlocked.Increment(ref misses);
					return false;
				}

				if (!Answered(settings, response, "download " + kind + "/" + key))
				{
					return false;
				}

				string expected = HeaderValue(response, HashHeader);
				long? length = response.Content.Headers.ContentLength;
				if (string.IsNullOrEmpty(expected))
				{
					Fail(settings, $"The server sent {kind}/{key} without its hash; not used");
					return false;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(file));
				string hash;
				long written;
				using (Stream body = response.Content.ReadAsStream(timeout.Token))
				using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
				using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
				{
					var buffer = new byte[1 << 20];
					written = 0;
					while (true)
					{
						// Each read gets its own time: a big file over a slow line takes long, a stalled one does not.
						timeout.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds));
						int read = body.ReadAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult();
						if (read == 0)
						{
							break;
						}

						output.Write(buffer, 0, read);
						sha.AppendData(buffer, 0, read);
						written += read;
					}

					hash = Convert.ToHexString(sha.GetHashAndReset());
				}

				if ((length != null && length.Value != written) || !string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
				{
					Fail(settings, $"{kind}/{key} arrived damaged ({written} bytes, hash {hash[..12]}..., the server's {expected[..Math.Min(12, expected.Length)]}...); not used");
					return false;
				}

				if (accept != null && !accept(temporary))
				{
					Fail(settings, $"The server's {kind}/{key} is not a file of its kind; not used");
					return false;
				}

				File.Move(temporary, file, true);
				Interlocked.Increment(ref fetched);
				Interlocked.Add(ref fetchedBytes, written);
				Reached(settings);
				if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds > 5)
				{
					Debug.Log($"[CacheServer] {kind}/{key}: {written / (1024.0 * 1024.0):0.0} MB in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:0.0} s");
				}

				return true;
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(settings, e);
				return false;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// The local side (disk full, a file in use): not the server's fault.
				Fail(settings, $"{kind}/{key} could not be written to {file}: {e.Message}", false);
				return false;
			}
			finally
			{
				TryDelete(temporary);
			}
		}

		public void Offer(string kind, string key, string file)
		{
			if (!CanOffer)
			{
				return;
			}

			lock (queue)
			{
				if (!queued.Add(kind + "/" + key))
				{
					return;
				}

				queue.Enqueue((kind, key, file));
				if (!sending)
				{
					sending = true;
					var thread = new Thread(SendQueued) { IsBackground = true, Name = "Library cache server", Priority = ThreadPriority.BelowNormal };
					thread.Start();
				}
			}
		}

		/// <summary>Asks the server for its status now (Preferences > Cache Server > Test Connection); <see cref="ConnectionTest" /> gets the answer.</summary>
		public void TestConnection()
		{
			Settings current = Current();
			if (current == null)
			{
				connectionTest = "Turned off, or no server address.";
				return;
			}

			connectionTest = "...";
			// A refused key or an unreachable server is asked again: that is what the button is for.
			current.Refused = false;
			Volatile.Write(ref current.UnreachableUntil, 0);
			System.Threading.Tasks.Task.Run(() =>
			{
				ServerStatus status = GetStatus(current, true, out string error);
				connectionTest = status == null
					? error
					: $"Connected: {status.Files.ToString("N0", CultureInfo.InvariantCulture)} results, {Megabytes(status.Bytes)} on the server; "
					+ (status.Upload ? "this key may send results." : "this key may only download (results made here are not sent).");
			});
		}

		/// <summary>"12.3 MB" / "1.2 GB".</summary>
		public static string Megabytes(long bytes)
		{
			return bytes >= 1L << 30
				? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
				: (bytes / (double)(1L << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
		}

		/// <summary>
		///     A key from the text of everything a result depends on (the inputs' hashes, the importer's version, ...): its
		///     SHA-256 in lower-case hex, the form the server's paths take.
		/// </summary>
		public static string Key(string identity)
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
		}

		/// <summary>
		///     The SHA-256 of a text file the way every machine has it: line ends as LF (a checkout may turn them to CRLF), so
		///     a meta file gives the same key on Windows, Linux and with any line-end setting.
		/// </summary>
		public static string TextHash(byte[] bytes)
		{
			int length = 0;
			var normalized = new byte[bytes.Length];
			for (int i = 0; i < bytes.Length; i++)
			{
				if (bytes[i] == (byte)'\r' && i + 1 < bytes.Length && bytes[i + 1] == (byte)'\n')
				{
					continue;
				}

				normalized[length++] = bytes[i];
			}

			return Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, length)));
		}

		#endregion

		#region PrivateMethods

		private static HttpClient CreateClient()
		{
			var handler = new SocketsHttpHandler
			{
				ConnectTimeout = TimeSpan.FromSeconds(5),
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				AutomaticDecompression = DecompressionMethods.None,
				AllowAutoRedirect = false
			};
			var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("LELEngine-Editor");
			return client;
		}

		/// <summary>The preferences as the requests use them; rebuilt when the preferences changed (null: off).</summary>
		private Settings Current()
		{
			int version = EditorPreferences.Version;
			Settings known = Volatile.Read(ref settings);
			if (version == Volatile.Read(ref settingsVersion))
			{
				return known;
			}

			lock (queue)
			{
				CacheServerPreferences preferences = EditorPreferences.Current.CacheServer;
				Settings made = Settings.From(preferences);
				// The same server and key keep what was learned about them (unreachable, refused, its part size).
				if (made != null && known != null && made.Identity == known.Identity)
				{
					known.Download = made.Download;
					known.Upload = made.Upload;
					made = known;
				}

				Volatile.Write(ref settings, made);
				Volatile.Write(ref settingsVersion, version);
				return made;
			}
		}

		private bool Usable(out Settings current)
		{
			current = Current();
			return current != null && !current.Refused && System.Diagnostics.Stopwatch.GetTimestamp() >= Volatile.Read(ref current.UnreachableUntil);
		}

		private static HttpResponseMessage Send(Settings settings, HttpRequestMessage request, CancellationToken token)
		{
			if (settings.AccessKey.Length > 0)
			{
				request.Headers.TryAddWithoutValidation(AccessKeyHeader, settings.AccessKey);
			}

			return Client.Send(request, HttpCompletionOption.ResponseHeadersRead, token);
		}

		/// <summary>A response the caller can use (2xx); else what it means is noted (refused key, server error) and false.</summary>
		private bool Answered(Settings settings, HttpResponseMessage response, string what)
		{
			if (response.IsSuccessStatusCode)
			{
				return true;
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
			{
				if (!settings.Refused)
				{
					settings.Refused = true;
					settings.LastError = $"The server refused the access key ({(int)response.StatusCode}); it is not asked again until the preferences change";
					Debug.LogWarning("[CacheServer] " + settings.LastError);
				}

				Interlocked.Increment(ref failures);
				return false;
			}

			string text = $"{what}: {(int)response.StatusCode} {response.ReasonPhrase} {ShortBody(response)}".TrimEnd();
			if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode == 429)
			{
				// The server (or a proxy in front of it) is in trouble: not asked again for a while.
				Unreachable(settings, new HttpRequestException(text));
				return false;
			}

			Fail(settings, text);
			return false;
		}

		private void Reached(Settings settings)
		{
			settings.LastError = null;
		}

		private void Unreachable(Settings settings, Exception e)
		{
			Interlocked.Increment(ref failures);
			string text = e is OperationCanceledException ? "no answer in time" : e.Message;
			bool first = System.Diagnostics.Stopwatch.GetTimestamp() >= Volatile.Read(ref settings.UnreachableUntil);
			Volatile.Write(ref settings.UnreachableUntil, System.Diagnostics.Stopwatch.GetTimestamp() + RetryAfterSeconds * System.Diagnostics.Stopwatch.Frequency);
			settings.LastError = $"{settings.Url} cannot be reached ({text})";
			if (first)
			{
				Debug.LogWarning($"[CacheServer] {settings.LastError}: imports make their results here; it is asked again in {RetryAfterSeconds} s");
			}
		}

		private void Fail(Settings settings, string text, bool log = true)
		{
			Interlocked.Increment(ref failures);
			settings.LastError = text;
			if (log)
			{
				Debug.LogWarning("[CacheServer] " + text);
			}
		}

		private static bool IsTransport(Exception e)
		{
			// HttpIOException: the response ended early (the connection dropped in the middle of a body).
			return e is HttpRequestException || e is HttpIOException || e is OperationCanceledException || e is TimeoutException
				|| (e is IOException && e.InnerException is System.Net.Sockets.SocketException);
		}

		private static string HeaderValue(HttpResponseMessage response, string name)
		{
			return response.Headers.TryGetValues(name, out IEnumerable<string> values) || response.Content.Headers.TryGetValues(name, out values)
				? string.Join(",", values).Trim()
				: null;
		}

		private static string ShortBody(HttpResponseMessage response)
		{
			try
			{
				string text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
				text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
				return text.Length > 200 ? text[..200] + "..." : text;
			}
			catch (Exception)
			{
				return "";
			}
		}

		private static void TryDelete(string file)
		{
			try
			{
				if (File.Exists(file))
				{
					File.Delete(file);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// A leftover temporary file: the next transfer of that result replaces it.
			}
		}

		/// <summary>The server's status (its part size, whether this key may send), asked once per server and key unless <paramref name="force" />.</summary>
		private ServerStatus GetStatus(Settings settings, bool force, out string error)
		{
			error = null;
			if (!force && settings.Status != null)
			{
				return settings.Status;
			}

			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, settings.Base + "status");
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HeadersTimeoutSeconds));
				using HttpResponseMessage response = Send(settings, request, timeout.Token);
				if (!Answered(settings, response, "status"))
				{
					error = settings.LastError ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
					return null;
				}

				string text = response.Content.ReadAsStringAsync(timeout.Token).GetAwaiter().GetResult();
				ServerStatus status = JsonSerializer.Deserialize<ServerStatus>(text, Json);
				if (status == null)
				{
					error = "The server's status could not be read: is the address the library cache's (it ends in .../library-cache)?";
					return null;
				}

				settings.Status = status;
				settings.ServerUpload = status.Upload;
				Reached(settings);
				return status;
			}
			catch (JsonException)
			{
				error = "The server's answer is not a library cache's status: is the address right (it ends in .../library-cache)?";
				return null;
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(settings, e);
				error = settings.LastError;
				return null;
			}
		}

		/// <summary>The sender's thread: the queued results in turn, until none waits.</summary>
		private void SendQueued()
		{
			while (true)
			{
				(string Kind, string Key, string File) item;
				lock (queue)
				{
					if (queue.Count == 0)
					{
						sending = false;
						return;
					}

					item = queue.Dequeue();
				}

				try
				{
					if (Usable(out Settings settings) && settings.Upload)
					{
						Upload(settings, item.Kind, item.Key, item.File);
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[CacheServer] Cannot send {item.Kind}/{item.Key}: {e.Message}");
				}
				finally
				{
					lock (queue)
					{
						queued.Remove(item.Kind + "/" + item.Key);
					}
				}
			}
		}

		private void Upload(Settings settings, string kind, string key, string file)
		{
			var info = new FileInfo(file);
			if (!info.Exists)
			{
				return;
			}

			long length = info.Length;
			DateTime time = info.LastWriteTimeUtc;
			ServerStatus status = GetStatus(settings, false, out _);
			if (status == null)
			{
				return;
			}

			if (!status.Upload)
			{
				return;
			}

			if (status.MaxFileBytes > 0 && length > status.MaxFileBytes)
			{
				return;
			}

			string url = settings.FileUrl(kind, key);
			try
			{
				// Asked first: a result another editor sent already is not sent again (a bake can be hundreds of MB).
				using (var head = new HttpRequestMessage(HttpMethod.Head, url))
				using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HeadersTimeoutSeconds)))
				using (HttpResponseMessage response = Send(settings, head, timeout.Token))
				{
					if (response.IsSuccessStatusCode)
					{
						Interlocked.Increment(ref alreadyStored);
						return;
					}

					if (response.StatusCode != HttpStatusCode.NotFound && !Answered(settings, response, "look for " + kind + "/" + key))
					{
						return;
					}
				}

				string hash = FileHash(file);
				long partSize = status.PartSize > 0 ? status.PartSize : DefaultPartSize;
				bool stored = length <= partSize
					? UploadWhole(settings, url, kind, key, file, length, time, hash)
					: UploadParts(settings, kind, key, file, length, time, hash, partSize);
				if (stored)
				{
					Interlocked.Increment(ref sent);
					Interlocked.Add(ref sentBytes, length);
					Reached(settings);
				}
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(settings, e);
			}
			catch (ChangedException)
			{
				// Made again while it was being sent: the new one is offered by the import that made it.
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Fail(settings, $"{kind}/{key} could not be read from {file}: {e.Message}", false);
			}
		}

		private bool UploadWhole(Settings settings, string url, string kind, string key, string file, long length, DateTime time, string hash)
		{
			byte[] bytes = ReadPart(file, 0, (int)length, length, time);
			using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(bytes) };
			request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
			request.Headers.TryAddWithoutValidation(HashHeader, hash);
			using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
			using HttpResponseMessage response = Send(settings, request, timeout.Token);
			// 409: another editor is sending the same result right now; 200: the server had it (201: stored now).
			if (response.StatusCode == HttpStatusCode.Conflict)
			{
				return false;
			}

			if (response.StatusCode == HttpStatusCode.OK)
			{
				Interlocked.Increment(ref alreadyStored);
				return false;
			}

			return Answered(settings, response, "send " + kind + "/" + key);
		}

		private bool UploadParts(Settings settings, string kind, string key, string file, long length, DateTime time, string hash, long partSize)
		{
			string uploadId;
			using (var begin = new HttpRequestMessage(HttpMethod.Post, settings.Base + "uploads"))
			{
				begin.Content = new StringContent(JsonSerializer.Serialize(new { path = kind + "/" + key, size = length, sha256 = hash }), Encoding.UTF8, "application/json");
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HeadersTimeoutSeconds));
				using HttpResponseMessage response = Send(settings, begin, timeout.Token);
				if (response.StatusCode == HttpStatusCode.Conflict || !Answered(settings, response, "begin sending " + kind + "/" + key))
				{
					return false;
				}

				UploadStart start = JsonSerializer.Deserialize<UploadStart>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Json);
				if (start == null || start.Exists)
				{
					Interlocked.Increment(ref alreadyStored);
					return false;
				}

				uploadId = start.UploadId;
				partSize = start.PartSize > 0 ? start.PartSize : partSize;
			}

			bool completed = false;
			try
			{
				int parts = (int)((length + partSize - 1) / partSize);
				for (int part = 0; part < parts; part++)
				{
					long offset = part * partSize;
					byte[] bytes = ReadPart(file, offset, (int)Math.Min(partSize, length - offset), length, time);
					using var request = new HttpRequestMessage(HttpMethod.Put, settings.Base + "uploads/" + Uri.EscapeDataString(uploadId) + "/" + part.ToString(CultureInfo.InvariantCulture))
					{
						Content = new ByteArrayContent(bytes)
					};
					request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
					request.Headers.TryAddWithoutValidation(HashHeader, Convert.ToHexString(SHA256.HashData(bytes)));
					using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
					using HttpResponseMessage response = Send(settings, request, timeout.Token);
					if (!Answered(settings, response, $"send part {part + 1} of {parts} of {kind}/{key}"))
					{
						return false;
					}
				}

				using (var complete = new HttpRequestMessage(HttpMethod.Post, settings.Base + "uploads/" + Uri.EscapeDataString(uploadId) + "/complete"))
				{
					using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
					using HttpResponseMessage response = Send(settings, complete, timeout.Token);
					completed = Answered(settings, response, "finish sending " + kind + "/" + key);
					return completed;
				}
			}
			finally
			{
				if (!completed)
				{
					// The parts sent so far go (the server also drops uploads left unfinished for a day).
					try
					{
						using var abort = new HttpRequestMessage(HttpMethod.Delete, settings.Base + "uploads/" + Uri.EscapeDataString(uploadId));
						using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(HeadersTimeoutSeconds));
						using HttpResponseMessage response = Send(settings, abort, timeout.Token);
					}
					catch (Exception e) when (IsTransport(e))
					{
						// Left to the server's cleanup.
					}
				}
			}
		}

		/// <summary>
		///     A part of the file, opened only for the read (an import may replace the file meanwhile: it is not kept open);
		///     <see cref="ChangedException" /> when the file is no longer the one being sent.
		/// </summary>
		private static byte[] ReadPart(string file, long offset, int count, long length, DateTime time)
		{
			using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length != length || File.GetLastWriteTimeUtc(file) != time)
			{
				throw new ChangedException();
			}

			var bytes = new byte[count];
			stream.Seek(offset, SeekOrigin.Begin);
			stream.ReadExactly(bytes);
			return bytes;
		}

		private static string FileHash(string file)
		{
			using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
			return Convert.ToHexString(SHA256.HashData(stream));
		}

		#endregion

		#region Nested types

		/// <summary>What the requests use of the preferences, with what was learned about the server.</summary>
		private sealed class Settings
		{
			public string Url;
			public string Base;
			public string AccessKey;
			public bool Download;
			public bool Upload;

			/// <summary>Server address and key: what was learned is kept while they stay the same.</summary>
			public string Identity;

			public long UnreachableUntil;
			public volatile bool Refused;
			public volatile string LastError;
			public volatile ServerStatus Status;

			/// <summary>The server's word on whether this key may send (null: not asked yet).</summary>
			public bool? ServerUpload;

			public static Settings From(CacheServerPreferences preferences)
			{
				string url = preferences?.Url?.Trim() ?? "";
				if (preferences == null || !preferences.Enabled || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
				{
					return null;
				}

				string key = preferences.AccessKey?.Trim() ?? "";
				string trimmed = url.TrimEnd('/');
				return new Settings
				{
					Url = trimmed,
					Base = trimmed + "/",
					AccessKey = key,
					Download = preferences.Download,
					Upload = preferences.Upload,
					Identity = trimmed + "\n" + key
				};
			}

			public string FileUrl(string kind, string key)
			{
				return Base + "files/" + Uri.EscapeDataString(kind) + "/" + Uri.EscapeDataString(key);
			}
		}

		private sealed class ServerStatus
		{
			public bool Upload { get; set; }
			public long PartSize { get; set; }
			public long MaxFileBytes { get; set; }
			public long Files { get; set; }
			public long Bytes { get; set; }
		}

		private sealed class UploadStart
		{
			public string UploadId { get; set; }
			public long PartSize { get; set; }
			public bool Exists { get; set; }
		}

		private sealed class ChangedException : Exception
		{
		}

		#endregion
	}
}
