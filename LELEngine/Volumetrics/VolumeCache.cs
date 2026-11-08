using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Volumetrics
{
	/// <summary>
	///     Made clouds kept, so an object made of the same settings does not make its cloud again:
	///     <list type="bullet">
	///         <item>
	///             On the GPU: the textures of an object that went away (its scene unloaded) wait <see cref="ParkSeconds" />
	///             for the next object of the same settings (<see cref="Park" />, <see cref="TryAdopt" />). The editor unloads the
	///             scene to play a copy of it and loads it again after: New Frontier's storm was made twice per play, ~180 ms
	///             of the GPU each time and the storm missing for a second.
	///         </item>
	///         <item>
	///             On disk: files of made clouds (<see cref="VolumeFile" />) in <see cref="Folder" />, read and unpacked on worker
	///             threads as soon as a scene's object is read (<see cref="Prefetch" />, from its OnAfterDeserialize), so the
	///             reading runs in parallel with the rest of the scene's loading; the cloud is then uploaded instead of made.
	///             The editor's Library/VolumeCache (it writes what it makes: <see cref="Writable" />) and a build's
	///             Data/Volumes (the build copies them there, or makes the missing ones).
	///         </item>
	///     </list>
	/// </summary>
	public static class VolumeCache
	{
		#region PublicFields

		/// <summary>Seconds a parked cloud waits for an object of the same settings before its textures are deleted.</summary>
		public const double ParkSeconds = 120.0;

		/// <summary>Seconds a file read ahead waits to be taken by its object before it is dropped.</summary>
		public const double PrefetchSeconds = 30.0;

		/// <summary>GPU memory parked clouds may hold at most (the oldest go first).</summary>
		public static long MaxParkedBytes = 768L << 20;

		/// <summary>Files on disk at most (bytes; the least recently used go first when a folder is set).</summary>
		public static long MaxFolderBytes = 2L << 30;

		/// <summary>A build's folder of them, inside its Data folder.</summary>
		public const string BuildFolder = "Volumes";

		/// <summary>This thread reads volumetric objects without starting their files' reads (a build listing its clouds).</summary>
		[ThreadStatic]
		public static bool SuppressPrefetch;

		/// <summary>Clouds taken from the GPU, files read, files written (statistics, tests).</summary>
		public static int Adopted => adopted;

		public static int FilesRead => filesRead;
		public static int FilesWritten => filesWritten;

		#endregion

		#region PrivateFields

		internal sealed class ParkedCloud
		{
			public string DensityKey;
			public string LitKey;
			public Vector3 Sun;
			public int Density;
			public int Occupancy;
			public int Lit;
			public Vector3i Resolution;
			public Vector3i Blocks;
			public Vector3 Size;
			public VolumeShape Shape;
			public float Voxel;
			public byte[] CpuDensity;
			public long Bytes;
			public double ParkedAt;
		}

		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static readonly List<ParkedCloud> parked = new List<ParkedCloud>();
		private static readonly object sync = new object();
		private static readonly Dictionary<string, (Task<VolumeFile> task, double started)> loads = new Dictionary<string, (Task<VolumeFile>, double)>();
		private static readonly HashSet<string> writing = new HashSet<string>();
		private static string folder;
		private static int adopted;
		private static int filesRead;
		private static int filesWritten;

		#endregion

		#region PublicProperties

		/// <summary>
		///     The folder of made clouds' files: the editor sets the project's Library/VolumeCache, a player its Data/Volumes;
		///     null: no files. Setting it drops the files unused for 30 days and the oldest past <see cref="MaxFolderBytes" />.
		/// </summary>
		public static string Folder
		{
			get => folder;
			set
			{
				if (string.Equals(folder, value, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				folder = value;
				lock (sync)
				{
					loads.Clear();
				}

				if (value != null && Writable)
				{
					Task.Run(() => Prune(value));
				}
			}
		}

		/// <summary>The editor writes the clouds it makes into <see cref="Folder" />; players only read.</summary>
		public static bool Writable { get; set; }

		#endregion

		#region PublicMethods

		/// <summary>
		///     Starts reading the file of a cloud on a worker thread, unless its textures are parked or it is being read
		///     already (any thread: a scene's objects call it as they are read).
		/// </summary>
		public static void Prefetch(string densityKey)
		{
			if (folder == null || densityKey == null || SuppressPrefetch)
			{
				return;
			}

			lock (sync)
			{
				if (loads.ContainsKey(densityKey) || parked.Exists(p => p.DensityKey == densityKey))
				{
					return;
				}

				string path = Path.Combine(folder, VolumeFile.FileName(densityKey));
				loads[densityKey] = (Task.Run(() => ReadFile(path, densityKey)), Clock.Elapsed.TotalSeconds);
			}
		}

		/// <summary>The file of a cloud being read or read (started now when it was not): its task gives null when there is none.</summary>
		internal static Task<VolumeFile> Load(string densityKey)
		{
			if (folder == null || densityKey == null)
			{
				return Task.FromResult<VolumeFile>(null);
			}

			Prefetch(densityKey);
			lock (sync)
			{
				return loads.TryGetValue(densityKey, out (Task<VolumeFile> task, double started) load) ? load.task : Task.FromResult<VolumeFile>(null);
			}
		}

		/// <summary>The object took the file it asked for (the next object of the same cloud reads it again if it needs it).</summary>
		public static void Taken(string densityKey)
		{
			lock (sync)
			{
				loads.Remove(densityKey);
			}
		}

		/// <summary>
		///     Writes a made cloud's file on a worker thread (the editor, <see cref="Writable" />); the arrays are the file's from
		///     now on. Skipped while the same cloud is being written.
		/// </summary>
		internal static void Write(VolumeFile file)
		{
			if (folder == null || !Writable)
			{
				return;
			}

			string path = Path.Combine(folder, VolumeFile.FileName(file.DensityKey));
			lock (sync)
			{
				if (!writing.Add(path))
				{
					return;
				}
			}

			Task.Run(() =>
			{
				try
				{
					file.Write(path);
					System.Threading.Interlocked.Increment(ref filesWritten);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					Console.WriteLine("[Volumetrics] Cannot write " + path + ": " + e.Message);
				}
				finally
				{
					lock (sync)
					{
						writing.Remove(path);
					}
				}
			});
		}

		/// <summary>
		///     True when the folder holds a file of this cloud lit with these settings for (about) this sun: nothing to write.
		///     Reads the file's header only.
		/// </summary>
		public static bool HasFile(string densityKey, string litKey, Vector3 sun)
		{
			if (folder == null)
			{
				return false;
			}

			string path = Path.Combine(folder, VolumeFile.FileName(densityKey));
			lock (sync)
			{
				if (writing.Contains(path))
				{
					return true;
				}
			}

			return VolumeFile.ReadHeader(path, out string fileDensity, out string fileLit, out Vector3 fileSun)
				&& fileDensity == densityKey && (litKey == null || fileLit == litKey && Vector3.Dot(fileSun, sun) >= VolumetricData.SunTolerance);
		}

		/// <summary>The GL thread: parked clouds and files read ahead that waited too long go (every frame).</summary>
		public static void Update()
		{
			double now = Clock.Elapsed.TotalSeconds;
			lock (sync)
			{
				for (int i = parked.Count - 1; i >= 0; i--)
				{
					if (now - parked[i].ParkedAt > ParkSeconds)
					{
						Delete(parked[i]);
						parked.RemoveAt(i);
					}
				}

				List<string> stale = null;
				foreach (KeyValuePair<string, (Task<VolumeFile> task, double started)> load in loads)
				{
					if (load.Value.task.IsCompleted && now - load.Value.started > PrefetchSeconds)
					{
						(stale ??= new List<string>()).Add(load.Key);
					}
				}

				if (stale != null)
				{
					foreach (string key in stale)
					{
						loads.Remove(key);
					}
				}
			}
		}

		/// <summary>Deletes the parked clouds' textures (the GL thread).</summary>
		public static void Clear()
		{
			lock (sync)
			{
				foreach (ParkedCloud cloud in parked)
				{
					Delete(cloud);
				}

				parked.Clear();
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>Keeps a complete cloud's textures for the next object of the same settings (the GL thread).</summary>
		internal static void Park(ParkedCloud cloud)
		{
			lock (sync)
			{
				parked.RemoveAll(p =>
				{
					if (p.DensityKey != cloud.DensityKey)
					{
						return false;
					}

					Delete(p);
					return true;
				});
				cloud.ParkedAt = Clock.Elapsed.TotalSeconds;
				parked.Add(cloud);
				long total = 0;
				foreach (ParkedCloud p in parked)
				{
					total += p.Bytes;
				}

				while (total > MaxParkedBytes && parked.Count > 0)
				{
					total -= parked[0].Bytes;
					Delete(parked[0]);
					parked.RemoveAt(0);
				}
			}
		}

		/// <summary>The parked cloud of this density, taken (its textures are the caller's now), or null (the GL thread).</summary>
		internal static ParkedCloud TryAdopt(string densityKey)
		{
			lock (sync)
			{
				int index = parked.FindIndex(p => p.DensityKey == densityKey);
				if (index < 0)
				{
					return null;
				}

				ParkedCloud cloud = parked[index];
				parked.RemoveAt(index);
				adopted++;
				return cloud;
			}
		}

		#endregion

		#region PrivateMethods

		private static VolumeFile ReadFile(string path, string densityKey)
		{
			VolumeFile file = VolumeFile.Read(path, densityKey);
			if (file != null)
			{
				System.Threading.Interlocked.Increment(ref filesRead);
				// The editor's cache: recently used (pruning goes by the write time). A build's files stay as shipped.
				if (Writable)
				{
					try
					{
						File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
					}
					catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
					{
						// In use elsewhere: it keeps its time.
					}
				}
			}

			return file;
		}

		private static void Delete(ParkedCloud cloud)
		{
			if (cloud.Density != 0) GL.DeleteTexture(cloud.Density);
			if (cloud.Occupancy != 0) GL.DeleteTexture(cloud.Occupancy);
			if (cloud.Lit != 0) GL.DeleteTexture(cloud.Lit);
			cloud.Density = cloud.Occupancy = cloud.Lit = 0;
		}

		// Files unused for 30 days go, then the least recently used past MaxFolderBytes.
		private static void Prune(string directory)
		{
			try
			{
				if (!Directory.Exists(directory))
				{
					return;
				}

				var files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles("*" + VolumeFile.Extension));
				files.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
				long total = 0;
				foreach (FileInfo file in files)
				{
					total += file.Length;
					if (total > MaxFolderBytes || DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromDays(30))
					{
						file.Delete();
					}
				}

				foreach (FileInfo leftover in new DirectoryInfo(directory).GetFiles("*.tmp"))
				{
					if (DateTime.UtcNow - leftover.LastWriteTimeUtc > TimeSpan.FromHours(1))
					{
						leftover.Delete();
					}
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Console.WriteLine("[Volumetrics] Cannot prune " + directory + ": " + e.Message);
			}
		}

		#endregion
	}
}
