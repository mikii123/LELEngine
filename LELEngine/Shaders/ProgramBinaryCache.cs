using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Linked programs kept as the driver's own binaries (glGetProgramBinary, GL 4.1) on this machine: a program whose
	///     stage sources this driver has linked before loads its binary instead of compiling (a game's later starts
	///     compile nothing). A file per program, named by a hash of its stage sources (preamble included, so every
	///     variant and GPU feature has its own), in a folder per driver (vendor, renderer, version); a binary the driver
	///     refuses (an update the name does not show) is compiled again and replaced. Files unused for
	///     <see cref="UnusedDays" /> days and the folders of all but the last few drivers are deleted when the cache starts.
	///     Off until <see cref="Folder" /> is set (the player sets it; <see cref="ShaderProgram" /> uses it for programs
	///     loaded from files and built from sources).
	/// </summary>
	public static class ProgramBinaryCache
	{
		#region PublicFields

		/// <summary>Days a binary stays without being loaded.</summary>
		public const int UnusedDays = 60;

		/// <summary>The cache's root folder (null: no cache). Set before the first program is built.</summary>
		public static string Folder { get; set; }

		/// <summary>Programs loaded from the cache since the start.</summary>
		public static int Loaded { get; private set; }

		/// <summary>Programs compiled and saved to the cache since the start.</summary>
		public static int Saved { get; private set; }

		/// <summary>Binaries the driver no longer accepted (compiled again).</summary>
		public static int Refused { get; private set; }

		/// <summary>The current driver's folder once the cache started (null: off or not available).</summary>
		public static string DriverFolder { get; private set; }

		#endregion

		#region PrivateFields

		private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LELPBIN1");

		// Link state a binary carries besides the sources: the fixed attribute locations (VertexLayout). Bump when it changes.
		private const string LinkState = "attributes 1";

		// Driver folders kept (a laptop switching between its GPUs uses two).
		private const int KeptDrivers = 3;

		private static bool started;
		private static bool warned;

		#endregion

		#region PublicMethods

		/// <summary>The cache works: a folder is set and the driver offers a binary format (call with the GL context current).</summary>
		public static bool Enabled
		{
			get
			{
				Start();
				return DriverFolder != null;
			}
		}

		/// <summary>The cache key of a program: a hash of its stages' types and final sources.</summary>
		public static string Key(IEnumerable<KeyValuePair<ShaderType, string>> stages)
		{
			using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
			{
				hash.AppendData(Encoding.UTF8.GetBytes(LinkState));
				foreach (KeyValuePair<ShaderType, string> stage in stages)
				{
					hash.AppendData(Encoding.UTF8.GetBytes("\n" + (int)stage.Key + "\n"));
					hash.AppendData(Encoding.UTF8.GetBytes(stage.Value));
				}

				return Convert.ToHexString(hash.GetHashAndReset());
			}
		}

		/// <summary>
		///     Loads the binary saved under <paramref name="key" /> into <paramref name="program" /> (a new program object);
		///     true when it linked. False when there is none or the driver refused it (the file is deleted).
		/// </summary>
		public static bool TryLoad(int program, string key)
		{
			string file = FileOf(key);
			if (file == null || !File.Exists(file))
			{
				return false;
			}

			try
			{
				byte[] data = File.ReadAllBytes(file);
				if (data.Length < Magic.Length + 8 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
				{
					File.Delete(file);
					return false;
				}

				var format = (BinaryFormat)BitConverter.ToInt32(data, Magic.Length);
				int length = BitConverter.ToInt32(data, Magic.Length + 4);
				if (length <= 0 || length > data.Length - Magic.Length - 8)
				{
					File.Delete(file);
					return false;
				}

				byte[] binary = new byte[length];
				Array.Copy(data, Magic.Length + 8, binary, 0, length);
				GL.ProgramBinary(program, format, binary, length);
				GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
				if (linked == 0)
				{
					Refused++;
					File.Delete(file);
					return false;
				}

				// Used now: kept by the clean-up of old binaries.
				File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
				Loaded++;
				return true;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Warn("cannot read " + file + ": " + e.Message);
				return false;
			}
		}

		/// <summary>Before a program links: asks the driver to keep its binary retrievable (when the cache works).</summary>
		public static void PrepareLink(int program)
		{
			if (Enabled)
			{
				GL.ProgramParameter(program, ProgramParameterName.ProgramBinaryRetrievableHint, 1);
			}
		}

		/// <summary>Saves a linked program's binary under <paramref name="key" />.</summary>
		public static void Save(int program, string key)
		{
			string file = FileOf(key);
			if (file == null)
			{
				return;
			}

			try
			{
				GL.GetProgram(program, GetProgramParameterName.ProgramBinaryLength, out int length);
				if (length <= 0)
				{
					return;
				}

				byte[] binary = new byte[length];
				GL.GetProgramBinary(program, length, out int written, out BinaryFormat format, binary);
				if (written <= 0)
				{
					return;
				}

				using (var stream = new MemoryStream(Magic.Length + 8 + written))
				{
					stream.Write(Magic, 0, Magic.Length);
					stream.Write(BitConverter.GetBytes((int)format), 0, 4);
					stream.Write(BitConverter.GetBytes(written), 0, 4);
					stream.Write(binary, 0, written);

					// Written aside and moved in: another instance of the game never reads half a file.
					string temporary = file + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
					File.WriteAllBytes(temporary, stream.ToArray());
					File.Move(temporary, file, true);
				}

				Saved++;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Warn("cannot write " + file + ": " + e.Message);
			}
		}

		#endregion

		#region PrivateMethods

		private static string FileOf(string key)
		{
			return Enabled && key != null ? Path.Combine(DriverFolder, key + ".bin") : null;
		}

		// On first use (with the GL context): the driver's folder, and the clean-up of old binaries and drivers.
		private static void Start()
		{
			if (started || Folder == null)
			{
				return;
			}

			started = true;
			try
			{
				GL.GetInteger(GetPName.NumProgramBinaryFormats, out int formats);
				if (formats <= 0)
				{
					Console.WriteLine("[ShaderCache] The driver offers no program binary format: shaders compile at every start");
					return;
				}

				string driver = GL.GetString(StringName.Vendor) + "\n" + GL.GetString(StringName.Renderer) + "\n" + GL.GetString(StringName.Version) + "\n" + GL.GetString(StringName.ShadingLanguageVersion);
				string folder = Path.Combine(Folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(driver))).Substring(0, 16));
				Directory.CreateDirectory(folder);
				File.WriteAllText(Path.Combine(folder, "driver.txt"), driver + "\n");
				DriverFolder = folder;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Warn("cannot use " + Folder + ": " + e.Message);
				DriverFolder = null;
				return;
			}

			try
			{
				CleanUp();
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[ShaderCache] Cannot clean up " + Folder + ": " + e.Message);
			}
		}

		private static void CleanUp()
		{
			DateTime oldest = DateTime.UtcNow.AddDays(-UnusedDays);
			foreach (string file in Directory.EnumerateFiles(DriverFolder, "*.bin"))
			{
				if (File.GetLastWriteTimeUtc(file) < oldest)
				{
					File.Delete(file);
				}
			}

			foreach (string temporary in Directory.EnumerateFiles(DriverFolder, "*.tmp"))
			{
				if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddHours(-1))
				{
					File.Delete(temporary);
				}
			}

			// Only the cache's own driver folders (with a driver.txt, written at every start, which dates their last use);
			// this driver's counts as just used.
			IEnumerable<string> others = Directory.GetDirectories(Folder)
				.Where(folder => File.Exists(Path.Combine(folder, "driver.txt")))
				.Where(folder => !string.Equals(Path.GetFullPath(folder), Path.GetFullPath(DriverFolder), StringComparison.OrdinalIgnoreCase))
				.OrderByDescending(folder => File.GetLastWriteTimeUtc(Path.Combine(folder, "driver.txt")))
				.Skip(KeptDrivers - 1);
			foreach (string folder in others)
			{
				Directory.Delete(folder, true);
			}
		}

		private static void Warn(string message)
		{
			if (!warned)
			{
				warned = true;
				Debug.LogWarning("[ShaderCache] " + message + " (shaders compile instead)");
			}
		}

		#endregion
	}
}
