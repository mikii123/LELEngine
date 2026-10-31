using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine
{
	/// <summary>
	///     The files of an asset cache as they were read or written last (write time and length), by the cache's keys:
	///     which loaded assets changed on disk since (a checkout, a text editor, another program), for the caches that
	///     read them again into the same objects (the editor's asset refresh).
	/// </summary>
	internal sealed class AssetFileStamps
	{
		#region PrivateFields

		private readonly Dictionary<string, long> stamps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		/// <summary>The file of <paramref name="key" /> as it is now was read or written (call before reading it).</summary>
		public void Note(string key, string absolutePath = null)
		{
			long stamp = StampOf(absolutePath ?? AssetDatabase.ToAbsolute(key));
			lock (stamps)
			{
				stamps[key] = stamp;
			}
		}

		public void Forget(string key)
		{
			lock (stamps)
			{
				stamps.Remove(key);
			}
		}

		public void Clear()
		{
			lock (stamps)
			{
				stamps.Clear();
			}
		}

		public void Moved(string fromPath, string toPath)
		{
			lock (stamps)
			{
				AssetDatabase.RekeyMoved(stamps, fromPath, toPath);
			}
		}

		/// <summary>
		///     Reads again the files of <paramref name="loaded" /> (the cache's entries) that changed since they were noted
		///     and hands each to <paramref name="apply" /> with its object; returns the objects reloaded. A file being
		///     written is left for its next change; one that cannot be applied is reported once.
		/// </summary>
		public List<T> ReloadChanged<T>(IEnumerable<KeyValuePair<string, T>> loaded, Action<T, byte[]> apply, string tag)
		{
			var reloaded = new List<T>();
			foreach (KeyValuePair<string, T> entry in loaded)
			{
				string file = AssetDatabase.ToAbsolute(entry.Key);
				long known;
				lock (stamps)
				{
					if (!stamps.TryGetValue(entry.Key, out known))
					{
						continue;
					}
				}

				long stamp = StampOf(file);
				if (stamp == known || stamp == 0)
				{
					continue;
				}

				byte[] data;
				try
				{
					data = File.ReadAllBytes(file);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					continue;
				}

				lock (stamps)
				{
					stamps[entry.Key] = stamp;
				}

				try
				{
					apply(entry.Value, data);
					reloaded.Add(entry.Value);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"{tag} {entry.Key} changed on disk but cannot be read ({e.Message}); it stays as it was");
				}
			}

			return reloaded;
		}

		/// <summary>Write time and length of a file in one number (0: no file).</summary>
		public static long StampOf(string absolutePath)
		{
			try
			{
				var info = new FileInfo(absolutePath);
				return info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0;
			}
			catch (Exception)
			{
				return 0;
			}
		}

		#endregion
	}
}
