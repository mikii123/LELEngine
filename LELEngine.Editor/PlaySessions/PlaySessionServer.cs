using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace LELEngine.Editor
{
	/// <summary>
	///     The play session server (Preferences > Play Sessions > Server): where the recorded play sessions go, so the team
	///     and the AI agents driving editors see each other's runs (a problem seen once on another machine, an agent's own
	///     test plays). A portal project serves it (the lelware portal's play sessions endpoint), and puts each session's
	///     console messages into the project's debug tracing.
	///     <para>
	///         Sending (<see cref="Send" />): session.json goes first (<c>POST {url}</c> <c>{id, session}</c>), then each file
	///         gzipped (<c>PUT {url}/{id}/files/{name}.gz</c>, its SHA-256 in <c>X-Content-SHA256</c>), then
	///         <c>POST {url}/{id}/complete</c> (the server traces the messages, once per session). A session is named by the
	///         id in its session.json, so sending it again replaces it. A sent session gets <see cref="MarkerFile" /> in its
	///         folder (the address and when); with Upload on, every ended session not sent to the current address is sent by a
	///         thread of its own (<see cref="OfferPending" />: after each session and when a project opens).
	///     </para>
	///     <para>
	///         Reading (the automation's <c>sessions --remote</c>, <c>session --remote</c>, <c>session fetch</c>):
	///         <c>GET {url}?limit=&amp;machine=&amp;scene=&amp;errors=true</c>, <c>GET {url}/{id}</c>,
	///         <c>GET {url}/{id}/files/{name}</c>. <see cref="Fetch" /> writes a remote session's files, unpacked and checked,
	///         into the project's Library/RemoteSessions/{id}/ for an agent to read like a local session.
	///     </para>
	/// </summary>
	internal sealed class PlaySessionServer
	{
		#region PublicFields

		public static readonly PlaySessionServer Instance = new PlaySessionServer();

		/// <summary>Written into a session's folder once it was sent: <c>{"url": ..., "id": ..., "sent": ..., "traceEntries": n}</c>.</summary>
		public const string MarkerFile = "server.json";

		/// <summary>The folder under the project's Library that <see cref="Fetch" /> writes remote sessions into.</summary>
		public const string RemoteFolderName = "RemoteSessions";

		public const string AccessKeyHeader = "X-Api-Key";
		public const string HashHeader = "X-Content-SHA256";

		/// <summary>After the server could not be reached, sessions are not sent for this long (the next offer tries again).</summary>
		public const int RetryAfterSeconds = 60;

		/// <summary>A server address is set (reading works without Upload).</summary>
		public bool Configured => Current() != null;

		/// <summary>Sessions are sent as they end.</summary>
		public bool UploadsOn
		{
			get
			{
				Settings settings = Current();
				return settings != null && settings.Upload && !settings.Refused && System.Diagnostics.Stopwatch.GetTimestamp() >= Volatile.Read(ref settings.UnreachableUntil);
			}
		}

		public int Sent => Volatile.Read(ref sent);
		public int Failures => Volatile.Read(ref failures);

		/// <summary>Sessions queued or being sent.</summary>
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

		/// <summary>The last failure (null: none since the preferences changed).</summary>
		public string LastError => Current()?.LastError;

		#endregion

		#region PrivateFields

		private static readonly string[] SessionFiles = { PlaySessionWriter.HeaderFile, PlaySessionWriter.EventsFile, PlaySessionWriter.FramesFile };
		private static readonly HttpClient Client = CreateClient();

		private readonly Queue<(string Folder, Func<string, bool> InUse)> queue = new Queue<(string, Func<string, bool>)>();
		private readonly HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private bool sending;
		private Settings settings;
		private int settingsVersion = -1;
		private int sent;
		private int failures;

		#endregion

		#region PublicMethods

		/// <summary>
		///     The session's id: session.json's "id"; for one recorded before sessions had ids, a stable one made from where and
		///     when it ran (machine, start, process), so sending it twice still names it once.
		/// </summary>
		public static string IdOf(JsonObject header)
		{
			string id = (string)header["id"];
			if (id != null && id.Length == 32 && IsHex(id))
			{
				return id;
			}

			string identity = $"{(string)header["machine"]?["name"]}|{(string)header["started"]}|{header["editor"]?["pid"]}";
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
		}

		/// <summary>The address the session in <paramref name="folder" /> was sent to (its <see cref="MarkerFile" />), or null.</summary>
		public static string SentTo(string folder)
		{
			try
			{
				string file = Path.Combine(folder, MarkerFile);
				return File.Exists(file) ? (string)JsonNode.Parse(File.ReadAllText(file))?["url"] : null;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is InvalidOperationException)
			{
				return null;
			}
		}

		/// <summary>
		///     With Upload on: every ended session under <paramref name="root" /> not sent to the current address, started in
		///     the last <paramref name="keepDays" />, is queued for the sender's thread (which waits while one is still being
		///     written). Any thread.
		/// </summary>
		public void OfferPending(string root, Func<string, bool> inUse, int keepDays)
		{
			Settings current = Current();
			if (current == null || !UploadsOn || root == null || !Directory.Exists(root))
			{
				return;
			}

			DateTime since = DateTime.Now.AddDays(-keepDays);
			foreach (string folder in Directory.GetDirectories(root))
			{
				if (Directory.GetCreationTime(folder) < since || !File.Exists(Path.Combine(folder, PlaySessionWriter.HeaderFile))
					|| string.Equals(SentTo(folder), current.Url, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				// Ended (or marked interrupted), or being finished by this editor's writer (the thread waits for it); not one
				// another editor is recording now.
				bool writing = inUse != null && inUse(folder);
				if (!writing)
				{
					try
					{
						JsonNode header = JsonNode.Parse(ReadShared(Path.Combine(folder, PlaySessionWriter.HeaderFile)));
						if (header?["ended"] == null && header?["interrupted"] == null)
						{
							continue;
						}
					}
					catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
					{
						continue;
					}
				}

				lock (queue)
				{
					if (!queued.Add(folder))
					{
						continue;
					}

					queue.Enqueue((folder, inUse));
					if (!sending)
					{
						sending = true;
						new Thread(SendQueued) { IsBackground = true, Name = "Play session server", Priority = ThreadPriority.BelowNormal }.Start();
					}
				}
			}
		}

		/// <summary>
		///     Sends the session in <paramref name="folder" /> now (any thread; it waits for the transfer): its session.json, then its
		///     files gzipped, then the completion. Not a session still recording, nor one sent to this address already, unless
		///     <paramref name="force" />. Returns what happened, for a person or an agent.
		/// </summary>
		public (bool Ok, string Message) Send(string folder, bool force)
		{
			Settings current = Current();
			if (current == null)
			{
				return (false, "No play session server is set (Preferences > Play Sessions > Server address).");
			}

			string headerFile = Path.Combine(folder, PlaySessionWriter.HeaderFile);
			if (!File.Exists(headerFile))
			{
				return (false, folder + " is not a play session (no session.json).");
			}

			JsonObject header;
			try
			{
				header = JsonNode.Parse(ReadShared(headerFile)) as JsonObject;
			}
			catch (JsonException e)
			{
				return (false, "Its session.json cannot be read: " + e.Message);
			}

			if (header == null)
			{
				return (false, "Its session.json is not an object.");
			}

			if (header["ended"] == null && header["interrupted"] == null && !force)
			{
				return (false, "The session has not ended (still recording, or its editor stopped and the next start has not marked it yet).");
			}

			string id = IdOf(header);
			if (!force && string.Equals(SentTo(folder), current.Url, StringComparison.OrdinalIgnoreCase))
			{
				return (true, $"{Path.GetFileName(folder)} was sent to {current.Url} already (id {id}).");
			}

			try
			{
				var body = new JsonObject { ["id"] = id, ["session"] = header.DeepClone() };
				using (var request = new HttpRequestMessage(HttpMethod.Post, current.Url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") })
				using (HttpResponseMessage response = SendRequest(current, request, TimeSpan.FromSeconds(30)))
				{
					if (!Answered(current, response, out string error))
					{
						return (false, error);
					}
				}

				long bytes = 0;
				foreach (string name in SessionFiles)
				{
					string file = Path.Combine(folder, name);
					if (!File.Exists(file))
					{
						continue;
					}

					byte[] packed = Gzip(file);
					bytes += packed.Length;
					using var request = new HttpRequestMessage(HttpMethod.Put, current.Url + "/" + id + "/files/" + Uri.EscapeDataString(name + ".gz")) { Content = new ByteArrayContent(packed) };
					request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
					request.Headers.TryAddWithoutValidation(HashHeader, Convert.ToHexString(SHA256.HashData(packed)).ToLowerInvariant());
					using HttpResponseMessage response = SendRequest(current, request, TimeSpan.FromMinutes(10));
					if (!Answered(current, response, out string error))
					{
						return (false, name + ": " + error);
					}
				}

				int traced = -1;
				using (var request = new HttpRequestMessage(HttpMethod.Post, current.Url + "/" + id + "/complete"))
				using (HttpResponseMessage response = SendRequest(current, request, TimeSpan.FromMinutes(5)))
				{
					if (!Answered(current, response, out string error))
					{
						return (false, "complete: " + error);
					}

					traced = (int?)JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())?["traceEntries"] ?? -1;
				}

				var marker = new JsonObject
				{
					["url"] = current.Url,
					["id"] = id,
					["sent"] = DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
					["traceEntries"] = traced
				};
				File.WriteAllText(Path.Combine(folder, MarkerFile), marker.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
				Interlocked.Increment(ref sent);
				current.LastError = null;
				return (true, $"Sent {Path.GetFileName(folder)} as {id} ({bytes / 1024.0:0.#} KB gzipped; {traced} messages in the project's debug tracing).");
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(current, e);
				return (false, current.LastError);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Interlocked.Increment(ref failures);
				return (false, "The session's files cannot be read: " + e.Message);
			}
		}

		/// <summary>
		///     The server's sessions, newest first (at most <paramref name="count" />), filtered by machine, scene (a part of its
		///     path) and those with errors (or a crash); a line each. Any thread.
		/// </summary>
		public (bool Ok, string Text) List(int count, string machine, string scene, bool errors)
		{
			Settings current = Current();
			if (current == null)
			{
				return (false, "No play session server is set (Preferences > Play Sessions > Server address).");
			}

			var query = new StringBuilder("?limit=" + Math.Clamp(count, 1, 500).ToString(CultureInfo.InvariantCulture));
			if (!string.IsNullOrEmpty(machine))
			{
				query.Append("&machine=").Append(Uri.EscapeDataString(machine));
			}

			if (!string.IsNullOrEmpty(scene))
			{
				query.Append("&scene=").Append(Uri.EscapeDataString(scene));
			}

			if (errors)
			{
				query.Append("&errors=true");
			}

			if (!TryGetJson(current, current.Url + query, out JsonNode answer, out string error))
			{
				return (false, error);
			}

			var text = new StringBuilder();
			JsonArray sessions = answer?["sessions"] as JsonArray ?? new JsonArray();
			text.Append($"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")} on {current.Url} (newest first; 'session <id> --remote' for one, 'session fetch <id>' for its files):\n");
			foreach (JsonNode row in sessions)
			{
				text.Append(Row(row)).Append('\n');
			}

			return (true, text.ToString());
		}

		/// <summary>One session of the server: its row, the files it has and its whole session.json, as text. Any thread.</summary>
		public (bool Ok, string Text) Describe(string id)
		{
			Settings current = Current();
			if (current == null)
			{
				return (false, "No play session server is set (Preferences > Play Sessions > Server address).");
			}

			if (!TryGetJson(current, current.Url + "/" + Uri.EscapeDataString(id), out JsonNode answer, out string error))
			{
				return (false, error);
			}

			var text = new StringBuilder();
			text.Append(Row(answer?["session"])).Append('\n');
			text.Append("files:");
			foreach (JsonNode file in answer?["session"]?["files"] as JsonArray ?? new JsonArray())
			{
				text.Append($" {(string)file?["name"]} ({(long?)file?["size"] ?? 0} bytes)");
			}

			text.Append("\nsession.json:\n").Append(answer?["header"]?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })).Append('\n');
			return (true, text.ToString());
		}

		/// <summary>
		///     Downloads a server session's files into <paramref name="libraryFolder" />/RemoteSessions/{id}/, each checked
		///     against its hash and unpacked (session.json, events.jsonl, frames.csv: the layout of a local session). Returns the
		///     folder. Any thread.
		/// </summary>
		public (bool Ok, string Text) Fetch(string id, string libraryFolder)
		{
			Settings current = Current();
			if (current == null)
			{
				return (false, "No play session server is set (Preferences > Play Sessions > Server address).");
			}

			if (!TryGetJson(current, current.Url + "/" + Uri.EscapeDataString(id), out JsonNode answer, out string error))
			{
				return (false, error);
			}

			string folder = Path.Combine(libraryFolder, RemoteFolderName, id);
			Directory.CreateDirectory(folder);
			var text = new StringBuilder();
			try
			{
				foreach (JsonNode file in answer?["session"]?["files"] as JsonArray ?? new JsonArray())
				{
					string name = (string)file?["name"];
					string expected = (string)file?["sha256"];
					if (string.IsNullOrEmpty(name) || !name.EndsWith(".gz", StringComparison.Ordinal) || name.Contains('/') || name.Contains('\\') || name.Contains(".."))
					{
						continue;
					}

					using var request = new HttpRequestMessage(HttpMethod.Get, current.Url + "/" + Uri.EscapeDataString(id) + "/files/" + Uri.EscapeDataString(name));
					using HttpResponseMessage response = SendRequest(current, request, TimeSpan.FromMinutes(10));
					if (!Answered(current, response, out error))
					{
						return (false, name + ": " + error);
					}

					byte[] packed = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
					if (!string.Equals(Convert.ToHexString(SHA256.HashData(packed)), expected, StringComparison.OrdinalIgnoreCase))
					{
						return (false, name + " arrived damaged (its SHA-256 differs from the server's).");
					}

					string target = Path.Combine(folder, name[..^3]);
					using (var input = new GZipStream(new MemoryStream(packed), CompressionMode.Decompress))
					using (var output = new FileStream(target + ".tmp", FileMode.Create, FileAccess.Write))
					{
						input.CopyTo(output);
					}

					File.Move(target + ".tmp", target, true);
					text.Append($"  {Path.GetFileName(target)}  {new FileInfo(target).Length} bytes\n");
				}
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(current, e);
				return (false, current.LastError);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
			{
				return (false, "The session's files cannot be written: " + e.Message);
			}

			return (true, $"{folder}\n{Row(answer?["session"])}\n{text}");
		}

		#endregion

		#region PrivateMethods

		private static HttpClient CreateClient()
		{
			var handler = new SocketsHttpHandler
			{
				ConnectTimeout = TimeSpan.FromSeconds(5),
				PooledConnectionLifetime = TimeSpan.FromMinutes(5),
				AllowAutoRedirect = false
			};
			var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
			client.DefaultRequestHeaders.UserAgent.ParseAdd("LELEngine-Editor");
			return client;
		}

		/// <summary>The preferences as the requests use them; rebuilt when they changed (null: no address).</summary>
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
				Settings made = Settings.From(EditorPreferences.Current.PlaySessions);
				if (made != null && known != null && made.Identity == known.Identity)
				{
					known.Upload = made.Upload;
					made = known;
				}

				Volatile.Write(ref settings, made);
				Volatile.Write(ref settingsVersion, version);
				return made;
			}
		}

		private void SendQueued()
		{
			while (true)
			{
				(string Folder, Func<string, bool> InUse) item;
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
					// A session that just ended is still being finished by its writer (the summary): wait for it.
					for (int i = 0; i < 240 && item.InUse != null && item.InUse(item.Folder); i++)
					{
						Thread.Sleep(500);
					}

					if (UploadsOn)
					{
						(bool ok, string message) = Send(item.Folder, false);
						if (ok)
						{
							Console.WriteLine("[PlaySessions] " + message);
						}
						else if (UploadsOn)
						{
							// Unreachable and refused keys warn once themselves.
							Interlocked.Increment(ref failures);
							Current()?.SetError(message);
							Debug.LogWarning("[PlaySessions] Cannot send " + Path.GetFileName(item.Folder) + ": " + message);
						}
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning("[PlaySessions] Cannot send " + Path.GetFileName(item.Folder) + ": " + e.Message);
				}
				finally
				{
					lock (queue)
					{
						queued.Remove(item.Folder);
					}
				}
			}
		}

		private static HttpResponseMessage SendRequest(Settings settings, HttpRequestMessage request, TimeSpan timeout)
		{
			if (settings.Key.Length > 0)
			{
				request.Headers.TryAddWithoutValidation(AccessKeyHeader, settings.Key);
			}

			using var cancel = new CancellationTokenSource(timeout);
			return Client.Send(request, HttpCompletionOption.ResponseContentRead, cancel.Token);
		}

		private bool TryGetJson(Settings settings, string url, out JsonNode answer, out string error)
		{
			answer = null;
			try
			{
				using var request = new HttpRequestMessage(HttpMethod.Get, url);
				using HttpResponseMessage response = SendRequest(settings, request, TimeSpan.FromSeconds(30));
				if (!Answered(settings, response, out error))
				{
					return false;
				}

				answer = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
				return true;
			}
			catch (JsonException)
			{
				error = "The server's answer is not JSON: is the address the play sessions endpoint (it ends in .../play-sessions)?";
				return false;
			}
			catch (Exception e) when (IsTransport(e))
			{
				Unreachable(settings, e);
				error = settings.LastError;
				return false;
			}
		}

		/// <summary>A 2xx answer; else its meaning in <paramref name="error" /> (a refused key stops the sending until the preferences change).</summary>
		private bool Answered(Settings settings, HttpResponseMessage response, out string error)
		{
			if (response.IsSuccessStatusCode)
			{
				error = null;
				return true;
			}

			string body = "";
			try
			{
				body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Replace('\n', ' ').Trim();
				body = body.Length > 200 ? body[..200] + "..." : body;
			}
			catch (Exception)
			{
				// No body to show.
			}

			if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
			{
				error = $"The server refused the access key ({(int)response.StatusCode}) {body}".TrimEnd();
				if (!settings.Refused && response.StatusCode == HttpStatusCode.Unauthorized)
				{
					settings.Refused = true;
					Debug.LogWarning("[PlaySessions] " + error + "; sessions are not sent until the preferences change");
				}
			}
			else
			{
				error = $"{(int)response.StatusCode} {response.ReasonPhrase} {body}".TrimEnd();
			}

			settings.LastError = error;
			Interlocked.Increment(ref failures);
			return false;
		}

		private void Unreachable(Settings settings, Exception e)
		{
			Interlocked.Increment(ref failures);
			bool first = System.Diagnostics.Stopwatch.GetTimestamp() >= Volatile.Read(ref settings.UnreachableUntil);
			Volatile.Write(ref settings.UnreachableUntil, System.Diagnostics.Stopwatch.GetTimestamp() + RetryAfterSeconds * System.Diagnostics.Stopwatch.Frequency);
			settings.LastError = $"{settings.Url} cannot be reached ({(e is OperationCanceledException ? "no answer in time" : e.Message)})";
			if (first)
			{
				Debug.LogWarning($"[PlaySessions] {settings.LastError}: sessions are sent later");
			}

			lock (queue)
			{
				// What waits goes: the next offer (a session's end, a project's open) queues it again.
				queue.Clear();
			}
		}

		private static bool IsTransport(Exception e)
		{
			return e is HttpRequestException || e is HttpIOException || e is OperationCanceledException || e is TimeoutException
				|| (e is IOException && e.InnerException is System.Net.Sockets.SocketException);
		}

		/// <summary>A file the recorder may still hold open, read as it is now.</summary>
		private static string ReadShared(string file)
		{
			using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}

		private static byte[] Gzip(string file)
		{
			using var output = new MemoryStream();
			using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
			using (var gzip = new GZipStream(output, CompressionLevel.Optimal, true))
			{
				input.CopyTo(gzip);
			}

			return output.ToArray();
		}

		/// <summary>A server session as one line: when, id, scene, length, frames, frame times, messages, machine, how it ended.</summary>
		private static string Row(JsonNode row)
		{
			if (row == null)
			{
				return "(no session)";
			}

			string started = DateTimeOffset.TryParse((string)row["started"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
				? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
				: "?";
			int errors = ((int?)row["errors"] ?? 0) + ((int?)row["exceptions"] ?? 0);
			bool interrupted = (bool?)row["interrupted"] ?? false;
			return string.Format(CultureInfo.InvariantCulture, "{0}  {1}  {2}  {3:0.0} s  {4} frames  avg {5:0.0} ms  worst {6:0} ms  {7} hitches  {8} errors  {9} warnings  {10}{11}  {12}",
				started, (string)row["id"], (string)row["scene"], (double?)row["seconds"] ?? 0, (long?)row["frames"] ?? 0, (double?)row["averageFrameMs"] ?? 0,
				(double?)row["worstFrameMs"] ?? 0, (int?)row["hitches"] ?? 0, errors, (int?)row["warnings"] ?? 0, (string)row["machine"],
				(bool?)row["automated"] == true ? " (automated)" : "", interrupted ? "INTERRUPTED: " + (string)row["endReason"] : (string)row["endReason"]);
		}

		private static bool IsHex(string text)
		{
			foreach (char c in text)
			{
				if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
				{
					return false;
				}
			}

			return true;
		}

		#endregion

		#region Nested types

		private sealed class Settings
		{
			public string Url;
			public string Key;
			public bool Upload;
			public string Identity;
			public long UnreachableUntil;
			public volatile bool Refused;
			public volatile string LastError;

			public void SetError(string error)
			{
				LastError = error;
			}

			public static Settings From(PlaySessionPreferences preferences)
			{
				string url = preferences?.ServerUrl?.Trim().TrimEnd('/') ?? "";
				if (preferences == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
				{
					return null;
				}

				string key = preferences.AccessKey?.Trim() ?? "";
				return new Settings { Url = url, Key = key, Upload = preferences.Upload, Identity = url + "\n" + key };
			}
		}

		#endregion
	}
}
