using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LELEngine.Editor
{
	/// <summary>A recorded session as the Preferences window lists it (read from its session.json).</summary>
	internal sealed class PlaySessionListing
	{
		public string Folder;
		public string Name;

		/// <summary>The session's id (session.json's "id"; for a session recorded before ids, one made from where and when it ran).</summary>
		public string Id;

		/// <summary>The server address it was sent to (<see cref="PlaySessionServer.MarkerFile" />), or null.</summary>
		public string SentTo;

		public DateTime Started;
		public string Scene;

		/// <summary>Seconds from the start to the end (or to the last frame written, for an interrupted one).</summary>
		public double Seconds;

		public long Frames;
		public int Warnings;
		public int Errors;
		public int Exceptions;
		public int Hitches;
		public double AverageFrameMs;
		public double WorstFrameMs;
		public string EndReason;

		/// <summary>Still being recorded (by this editor or another one running now).</summary>
		public bool Recording;

		/// <summary>The editor stopped while recording it (a crash, the process killed).</summary>
		public bool Interrupted;

		public long Bytes;
	}

	/// <summary>
	///     The project's recorded play sessions: Library/PlaySessions, a folder per session named by when it started and its
	///     scene ("2026-11-26_14-03-22 Flight"). <see cref="Maintain" /> keeps them within the preferences (days, total size)
	///     and marks the sessions an editor never ended (it crashed or was killed while recording); <see cref="List" />
	///     reads them for the Preferences window. Both read and delete files: call them off the main thread.
	/// </summary>
	internal static class PlaySessionStore
	{
		#region PublicFields

		public const string FolderName = "PlaySessions";

		private const string NameFormat = "yyyy-MM-dd_HH-mm-ss";

		#endregion

		#region PublicMethods

		public static string RootOf(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, FolderName);
		}

		/// <summary>A new session's folder under <paramref name="root" /> (not made yet): its start and the scene's name.</summary>
		public static string NewFolder(string root, DateTime started, string scenePath)
		{
			string scene = string.IsNullOrEmpty(scenePath) ? "Untitled" : Path.GetFileNameWithoutExtension(scenePath);
			var name = new StringBuilder(started.ToString(NameFormat, CultureInfo.InvariantCulture)).Append(' ');
			char[] invalid = Path.GetInvalidFileNameChars();
			foreach (char c in scene)
			{
				name.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
			}

			string folder = Path.Combine(root, name.ToString().TrimEnd(' ', '.'));
			string unique = folder;
			for (int n = 2; Directory.Exists(unique); n++)
			{
				unique = folder + " (" + n + ")";
			}

			return unique;
		}

		/// <summary>
		///     Sessions never ended are marked interrupted (unless their editor still runs), then the ones older than
		///     <see cref="PlaySessionPreferences.KeepDays" /> go, then the oldest while all take more than
		///     <see cref="PlaySessionPreferences.MaxTotalMB" />. <paramref name="inUse" /> names folders being written now:
		///     never touched. Returns how many were deleted.
		/// </summary>
		public static int Maintain(string root, PlaySessionPreferences preferences, Func<string, bool> inUse)
		{
			if (!Directory.Exists(root))
			{
				return 0;
			}

			List<PlaySessionListing> sessions = List(root, inUse);
			int deleted = 0;
			DateTime oldest = DateTime.Now.AddDays(-preferences.KeepDays);
			long total = 0;
			foreach (PlaySessionListing session in sessions)
			{
				total += session.Bytes;
			}

			// Oldest first.
			sessions.Sort((a, b) => a.Started.CompareTo(b.Started));
			long limit = preferences.MaxTotalMB > 0 ? preferences.MaxTotalMB * 1024L * 1024L : long.MaxValue;
			foreach (PlaySessionListing session in sessions)
			{
				if (session.Recording || inUse(session.Folder))
				{
					continue;
				}

				if (session.Started >= oldest && total <= limit)
				{
					// Newer ones are newer still; the size is within the limit.
					break;
				}

				if (Delete(session.Folder))
				{
					deleted++;
					total -= session.Bytes;
				}
			}

			return deleted;
		}

		/// <summary>Every session under <paramref name="root" />, newest first; interrupted ones are marked in their file on the way.</summary>
		public static List<PlaySessionListing> List(string root, Func<string, bool> inUse)
		{
			var sessions = new List<PlaySessionListing>();
			if (!Directory.Exists(root))
			{
				return sessions;
			}

			string[] folders;
			try
			{
				folders = Directory.GetDirectories(root);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return sessions;
			}

			foreach (string folder in folders)
			{
				PlaySessionListing session = Read(folder, inUse(folder));
				if (session != null)
				{
					sessions.Add(session);
				}
			}

			sessions.Sort((a, b) => b.Started.CompareTo(a.Started));
			return sessions;
		}

		/// <summary>Deletes every session not being written now; returns how many went.</summary>
		public static int DeleteAll(string root, Func<string, bool> inUse)
		{
			int deleted = 0;
			foreach (PlaySessionListing session in List(root, inUse))
			{
				if (!session.Recording && Delete(session.Folder))
				{
					deleted++;
				}
			}

			return deleted;
		}

		#endregion

		#region PrivateMethods

		private static PlaySessionListing Read(string folder, bool writtenNow)
		{
			var session = new PlaySessionListing { Folder = folder, Name = Path.GetFileName(folder), Recording = writtenNow };
			session.Bytes = Size(folder);
			string name = session.Name;
			session.Started = name.Length >= NameFormat.Length && DateTime.TryParseExact(name.Substring(0, NameFormat.Length), NameFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime fromName)
				? fromName
				: Directory.GetCreationTime(folder);
			string file = Path.Combine(folder, PlaySessionWriter.HeaderFile);
			if (!File.Exists(file))
			{
				// A folder without a header is not a session (or one that never got its first write).
				session.Scene = "";
				session.EndReason = writtenNow ? null : "no session.json";
				return session;
			}

			try
			{
				JsonNode root = JsonNode.Parse(File.ReadAllText(file));
				if (root is not JsonObject header)
				{
					return session;
				}

				if (DateTimeOffset.TryParse((string)header["started"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset started))
				{
					session.Started = started.LocalDateTime;
				}

				session.Id = PlaySessionServer.IdOf(header);
				session.SentTo = PlaySessionServer.SentTo(folder);
				session.Scene = (string)header["scene"] ?? "";
				session.Seconds = (double?)header["seconds"] ?? 0;
				session.EndReason = (string)header["endReason"];
				bool ended = header["ended"] != null;
				if (!ended && !writtenNow)
				{
					if (StillRecording(header))
					{
						session.Recording = true;
					}
					else
					{
						MarkInterrupted(folder, header, session);
					}
				}

				session.Interrupted = header["interrupted"] != null && (bool)header["interrupted"];
				if (header["summary"] is JsonObject summary)
				{
					session.Frames = (long?)summary["frames"] ?? 0;
					session.Hitches = (int?)summary["hitches"] ?? 0;
					if (summary["frameMs"] is JsonObject frameMs)
					{
						session.AverageFrameMs = (double?)frameMs["average"] ?? 0;
						session.WorstFrameMs = (double?)frameMs["max"] ?? 0;
					}

					if (summary["messages"] is JsonObject messages)
					{
						session.Warnings = (int?)messages["warning"] ?? 0;
						session.Errors = (int?)messages["error"] ?? 0;
						session.Exceptions = (int?)messages["exception"] ?? 0;
					}
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException || e is InvalidOperationException || e is FormatException)
			{
				session.EndReason ??= "session.json unreadable: " + e.Message;
			}

			return session;
		}

		/// <summary>The editor that wrote the header still runs (the same process id, started at the same time).</summary>
		private static bool StillRecording(JsonObject header)
		{
			if (header["editor"] is not JsonObject editor || editor["pid"] == null)
			{
				return false;
			}

			try
			{
				int pid = (int)editor["pid"];
				if (!DateTimeOffset.TryParse((string)editor["processStarted"], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset started))
				{
					return false;
				}

				using Process process = Process.GetProcessById(pid);
				return Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2.0;
			}
			catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is System.ComponentModel.Win32Exception || e is FormatException)
			{
				// No such process (or none that can be asked): its editor is gone.
				return false;
			}
		}

		/// <summary>
		///     A session its editor never ended: session.json gets "interrupted", the reason and how far it got (the last row of
		///     frames.csv), so a crash shows in the list and the files say where to look.
		/// </summary>
		private static void MarkInterrupted(string folder, JsonObject header, PlaySessionListing session)
		{
			(long row, double seconds) = LastFrame(Path.Combine(folder, PlaySessionWriter.FramesFile));
			header["interrupted"] = true;
			header["endReason"] = "interrupted: the editor stopped while recording (it crashed or was closed from outside)";
			header["lastRow"] = row;
			header["lastFrameSeconds"] = Math.Round(seconds, 3);
			session.EndReason = (string)header["endReason"];
			session.Seconds = seconds;
			session.Frames = row + 1;
			try
			{
				string file = Path.Combine(folder, PlaySessionWriter.HeaderFile);
				File.WriteAllText(file + ".tmp", header.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
				File.Move(file + ".tmp", file, true);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
			}
		}

		/// <summary>The row and time of the last whole line of frames.csv (-1, 0: none).</summary>
		private static (long Row, double Seconds) LastFrame(string file)
		{
			try
			{
				using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				long start = Math.Max(0, stream.Length - 16384);
				stream.Seek(start, SeekOrigin.Begin);
				var buffer = new byte[stream.Length - start];
				int read = stream.Read(buffer, 0, buffer.Length);
				string tail = Encoding.UTF8.GetString(buffer, 0, read);
				// Whole lines end with a newline: the text after the last one was being written.
				int end = tail.LastIndexOf('\n');
				if (end <= 0)
				{
					return (-1, 0);
				}

				int begin = tail.LastIndexOf('\n', end - 1) + 1;
				string[] fields = tail.Substring(begin, end - begin).TrimEnd('\r').Split(',');
				if (fields.Length > 1 && long.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long row)
					&& double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
				{
					return (row, seconds);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
			}

			return (-1, 0);
		}

		private static long Size(string folder)
		{
			long bytes = 0;
			try
			{
				foreach (FileInfo file in new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories))
				{
					bytes += file.Length;
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
			}

			return bytes;
		}

		private static bool Delete(string folder)
		{
			try
			{
				Directory.Delete(folder, true);
				return true;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// Open in another program (a viewer, a text editor): it goes next time.
				return false;
			}
		}

		#endregion
	}
}
