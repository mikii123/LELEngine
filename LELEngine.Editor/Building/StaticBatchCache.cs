using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     What a static batch's object in a scene is made of (<see cref="StaticBatcher" />): kept in the project's Library
	///     with the batch's mesh, put into a build's scene document or into the scene the editor's play mode loads.
	/// </summary>
	internal sealed class StaticBatchData
	{
		public string Name;

		/// <summary>The ids of the object and its components in a document (from the scene's path and the batch's number).</summary>
		public ulong ObjectId;

		public ulong TransformId;
		public ulong RendererId;
		public ulong BatchId;
		public int Layer;
		public bool CastShadows;
		public bool ReceiveShadows;
		public bool ReceiveGI;

		/// <summary>Where the object stands (the middle of its parts); the mesh is in its space.</summary>
		public Vector3 Pivot;

		/// <summary>The merged mesh ("Library/StaticBatches/&lt;hash&gt;.mesh" and its name) and the material of each of its sub-meshes.</summary>
		public AssetReference Mesh;

		public AssetReference[] Materials;

		/// <summary>The parts' renderers (ids in the scene) and their triangles' places in the mesh (<see cref="StaticBatch" />'s layout).</summary>
		public ulong[] Renderers;

		public int[] PartRanges;
		public int[] Ranges;

		/// <summary>Per part: the centre of its mesh's bounds in the batch object's space, and whether its triangles all face one way.</summary>
		public Vector3[] Centres;

		public bool[] Flat;

		/// <summary>Per part: half the size of the box around it in the batch object's space (about its centre).</summary>
		public Vector3[] Extents;

		public void Write(BinaryWriter writer)
		{
			writer.Write(Name ?? "");
			writer.Write(ObjectId);
			writer.Write(TransformId);
			writer.Write(RendererId);
			writer.Write(BatchId);
			writer.Write(Layer);
			writer.Write(CastShadows);
			writer.Write(ReceiveShadows);
			writer.Write(ReceiveGI);
			writer.Write(Pivot.X);
			writer.Write(Pivot.Y);
			writer.Write(Pivot.Z);
			WriteReference(writer, Mesh);
			writer.Write(Materials.Length);
			foreach (AssetReference material in Materials)
			{
				WriteReference(writer, material);
			}

			writer.Write(Renderers.Length);
			foreach (ulong renderer in Renderers)
			{
				writer.Write(renderer);
			}

			WriteInts(writer, PartRanges);
			WriteInts(writer, Ranges);
			writer.Write(Centres.Length);
			for (int i = 0; i < Centres.Length; i++)
			{
				writer.Write(Centres[i].X);
				writer.Write(Centres[i].Y);
				writer.Write(Centres[i].Z);
				writer.Write(i < Flat.Length && Flat[i]);
				Vector3 extent = Extents != null && i < Extents.Length ? Extents[i] : Vector3.Zero;
				writer.Write(extent.X);
				writer.Write(extent.Y);
				writer.Write(extent.Z);
			}
		}

		public static StaticBatchData Read(BinaryReader reader)
		{
			var data = new StaticBatchData
			{
				Name = reader.ReadString(),
				ObjectId = reader.ReadUInt64(),
				TransformId = reader.ReadUInt64(),
				RendererId = reader.ReadUInt64(),
				BatchId = reader.ReadUInt64(),
				Layer = reader.ReadInt32(),
				CastShadows = reader.ReadBoolean(),
				ReceiveShadows = reader.ReadBoolean(),
				ReceiveGI = reader.ReadBoolean(),
				Pivot = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
				Mesh = ReadReference(reader)
			};

			data.Materials = new AssetReference[reader.ReadInt32()];
			for (int i = 0; i < data.Materials.Length; i++)
			{
				data.Materials[i] = ReadReference(reader);
			}

			data.Renderers = new ulong[reader.ReadInt32()];
			for (int i = 0; i < data.Renderers.Length; i++)
			{
				data.Renderers[i] = reader.ReadUInt64();
			}

			data.PartRanges = ReadInts(reader);
			data.Ranges = ReadInts(reader);
			int parts = reader.ReadInt32();
			data.Centres = new Vector3[parts];
			data.Flat = new bool[parts];
			data.Extents = new Vector3[parts];
			for (int i = 0; i < parts; i++)
			{
				data.Centres[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
				data.Flat[i] = reader.ReadBoolean();
				data.Extents[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
			}

			return data;
		}

		private static void WriteReference(BinaryWriter writer, AssetReference reference)
		{
			writer.Write(reference.Guid ?? "");
			writer.Write(reference.Path ?? "");
			writer.Write(reference.Name ?? "");
		}

		private static AssetReference ReadReference(BinaryReader reader)
		{
			string guid = reader.ReadString();
			string path = reader.ReadString();
			string name = reader.ReadString();
			return new AssetReference { Guid = guid.Length > 0 ? guid : null, Path = path.Length > 0 ? path : null, Name = name.Length > 0 ? name : null };
		}

		private static void WriteInts(BinaryWriter writer, int[] values)
		{
			writer.Write(values.Length);
			foreach (int value in values)
			{
				writer.Write(value);
			}
		}

		private static int[] ReadInts(BinaryReader reader)
		{
			var values = new int[reader.ReadInt32()];
			for (int i = 0; i < values.Length; i++)
			{
				values[i] = reader.ReadInt32();
			}

			return values;
		}
	}

	/// <summary>
	///     The static batches of scene documents, kept in the project's Library (Library/StaticBatches; the user's ask,
	///     2026-11-20: the Library cache, as the baked textures and distance fields): the merged meshes as mesh files the
	///     batches' renderers name by path ("Library/StaticBatches/&lt;hash&gt;.mesh": <see cref="AssetDatabase.StaticBatchPrefix" />,
	///     which the editor reads from there and a build ships under its Data), and per scene an entry with what its
	///     batches' objects are made of (<see cref="StaticBatchData" />). The editor's play mode and a build ask for a
	///     scene's batches and get them from here: a scene is worked out again only when it changed.
	///     <para>
	///         An entry belongs to a scene's path and the document's kind (the scene's file, and the snapshot play mode
	///         takes of the open scene: the two a scene has at a time). It holds the hash of the document it was made for
	///         (with the batcher's version and the engine's and the editor's builds) and the asset files the result
	///         depends on besides the document (prefabs, models, materials, shaders: <see cref="StaticBatcher.Analyze" />)
	///         with their sizes and times: it is good while all of that is the same and its meshes are there, else it is
	///         made again in its place. So entries do not pile up as a scene is edited and played.
	///     </para>
	///     <para>
	///         A mesh file is named by the hash of its bytes: a batch that comes out the same for another version of the
	///         scene (or for its file and for its snapshot) is the same file, and a file never gets other content while
	///         something has it loaded. Meshes no entry names any more are deleted, <see cref="UnusedMeshHours" /> after
	///         their last use (what got them a moment ago may still be reading them); entries nothing asked for in
	///         <see cref="KeepDays" /> days too (looked at once per run of the editor).
	///     </para>
	/// </summary>
	internal static class StaticBatchCache
	{
		#region PublicFields

		/// <summary>Entries not used for this many days are deleted.</summary>
		public const int KeepDays = 30;

		/// <summary>A mesh no entry names stays this long after its last use.</summary>
		public const double UnusedMeshHours = 1.0;

		/// <summary>The batches of one scene document and how they were got.</summary>
		public sealed class Result
		{
			public readonly List<StaticBatchData> Batches = new List<StaticBatchData>();

			/// <summary>What was merged and what was left, for a log line (<see cref="StaticBatcher.Summary" />).</summary>
			public string Summary = "";

			/// <summary>Static renderers the scene has (0: nothing to say about it).</summary>
			public int StaticRenderers;

			public bool FromCache;
			public double Milliseconds;

			/// <summary>The batches' mesh files (absolute paths in the Library) by the asset path the scene names them by.</summary>
			public readonly List<KeyValuePair<string, string>> MeshFiles = new List<KeyValuePair<string, string>>();
		}

		#endregion

		#region PrivateFields

		// Bumped when the batcher gives another result for the same input (the engine's and the editor's builds are in
		// the document's hash besides).
		private const int Version = 4;
		private const string EntryExtension = ".batches";
		private const string WritingExtension = ".writing";
		private const int HashBytes = 32;
		private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LELSB004");
		private static readonly object sync = new object();
		private static string prunedFolder;

		#endregion

		#region PublicMethods

		/// <summary>The folder of a project's cache (the files of <see cref="AssetDatabase.StaticBatchPrefix" /> in the editor).</summary>
		public static string FolderOf(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, "StaticBatches");
		}

		/// <summary>
		///     The batches of a scene document (its bytes as a build reads them from the file, or as play mode snapshots the
		///     scene): from the cache when the document and what it uses are as they were, else worked out and kept. Any
		///     thread (one at a time).
		/// </summary>
		public static Result Get(ProjectInfo project, byte[] scene, string sceneAssetPath)
		{
			long started = Stopwatch.GetTimestamp();
			lock (sync)
			{
				string folder = FolderOf(project);
				Prune(folder);
				byte[] documentHash = DocumentHash(scene, sceneAssetPath);
				string entryFile = Path.Combine(folder, SlotOf(scene, sceneAssetPath) + EntryExtension);
				Result result = TryRead(entryFile, folder, documentHash);
				if (result == null)
				{
					result = Make(scene, sceneAssetPath, documentHash, entryFile, folder);

					// What the entry named before may have nothing naming it now.
					DeleteUnusedMeshes(folder);
				}

				result.Milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
				return result;
			}
		}

		#endregion

		#region PrivateMethods

		private static Result Make(byte[] scene, string sceneAssetPath, byte[] documentHash, string entryFile, string folder)
		{
			var result = new Result();
			var inputs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
			var meshNames = new List<string>();
			DateTime before = DateTime.UtcNow;
			StaticBatcher.MeshStore store = (mesh, index, name) =>
			{
				byte[] bytes;
				using (var stream = new MemoryStream())
				{
					ModelAssets.WriteMesh(stream, mesh, name);
					bytes = stream.ToArray();
				}

				// Named by what is in it (the batch's name is: two batches of one scene never share a file, and with it
				// the triangles one of them hides).
				string fileName = Convert.ToHexString(SHA256.HashData(bytes), 0, 12).ToLowerInvariant() + ModelAssets.MeshExtension;
				string file = Path.Combine(folder, fileName);
				WriteOnce(file, bytes);
				string assetPath = AssetDatabase.StaticBatchPrefix + fileName;
				result.MeshFiles.Add(new KeyValuePair<string, string>(assetPath, file));
				meshNames.Add(fileName);
				return new AssetReference { Path = assetPath, Name = name };
			};

			result.Batches.AddRange(StaticBatcher.Analyze(scene, sceneAssetPath, store, inputs, out StaticBatcher.Summary summary));
			result.Summary = summary.ToString();
			result.StaticRenderers = summary.StaticRenderers;

			// The inputs as they are now. One written while the scene was worked out may have been read before or after
			// the write: the result is used this once and not kept.
			var stamps = new List<KeyValuePair<string, long>>();
			foreach (string input in inputs)
			{
				stamps.Add(new KeyValuePair<string, long>(input, Stamp(input)));
				if (WrittenSince(input, before))
				{
					return result;
				}
			}

			try
			{
				Directory.CreateDirectory(folder);
				string temporary = entryFile + WritingExtension;
				using (var writer = new BinaryWriter(File.Create(temporary)))
				{
					writer.Write(Magic);
					writer.Write(documentHash);
					writer.Write(meshNames.Count);
					foreach (string meshName in meshNames)
					{
						writer.Write(meshName);
					}

					writer.Write(stamps.Count);
					foreach (KeyValuePair<string, long> stamp in stamps)
					{
						writer.Write(stamp.Key);
						writer.Write(stamp.Value);
					}

					writer.Write(result.Summary);
					writer.Write(result.StaticRenderers);
					writer.Write(result.Batches.Count);
					foreach (StaticBatchData batch in result.Batches)
					{
						batch.Write(writer);
					}
				}

				File.Move(temporary, entryFile, true);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[StaticBatching] The batches could not be kept in the Library (" + e.Message + "); they are made again next time.");
			}

			return result;
		}

		// The entry of a scene when it is still good: made for this document, what it depends on unchanged, its meshes there.
		private static Result TryRead(string entryFile, string folder, byte[] documentHash)
		{
			if (!File.Exists(entryFile))
			{
				return null;
			}

			try
			{
				var result = new Result { FromCache = true };
				using (var reader = new BinaryReader(File.OpenRead(entryFile)))
				{
					if (!ReadHeader(reader, null, out byte[] madeFor) || !madeFor.AsSpan().SequenceEqual(documentHash))
					{
						return null;
					}

					int inputs = reader.ReadInt32();
					for (int i = 0; i < inputs; i++)
					{
						string path = reader.ReadString();
						long stamp = reader.ReadInt64();
						if (Stamp(path) != stamp)
						{
							return null;
						}
					}

					result.Summary = reader.ReadString();
					result.StaticRenderers = reader.ReadInt32();
					int count = reader.ReadInt32();
					for (int i = 0; i < count; i++)
					{
						result.Batches.Add(StaticBatchData.Read(reader));
					}
				}

				DateTime now = DateTime.UtcNow;
				foreach (StaticBatchData batch in result.Batches)
				{
					string path = batch.Mesh.Path ?? "";
					if (!path.StartsWith(AssetDatabase.StaticBatchPrefix, StringComparison.OrdinalIgnoreCase))
					{
						return null;
					}

					string file = Path.Combine(folder, path.Substring(AssetDatabase.StaticBatchPrefix.Length));
					if (!File.Exists(file))
					{
						return null;
					}

					// Used now: the entry and its meshes stay.
					File.SetLastWriteTimeUtc(file, now);
					result.MeshFiles.Add(new KeyValuePair<string, string>(path, file));
				}

				File.SetLastWriteTimeUtc(entryFile, now);
				return result;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is FormatException)
			{
				return null;
			}
		}

		// An entry's start: the magic, the hash of the document it was made for, the names of its mesh files (added to
		// meshNames when given). False for a file of another format.
		private static bool ReadHeader(BinaryReader reader, ICollection<string> meshNames, out byte[] documentHash)
		{
			documentHash = null;
			byte[] magic = reader.ReadBytes(Magic.Length);
			if (!magic.AsSpan().SequenceEqual(Magic))
			{
				return false;
			}

			documentHash = reader.ReadBytes(HashBytes);
			if (documentHash.Length != HashBytes)
			{
				return false;
			}

			int count = reader.ReadInt32();
			if (count < 0 || count > 1 << 20)
			{
				return false;
			}

			for (int i = 0; i < count; i++)
			{
				string name = reader.ReadString();
				meshNames?.Add(name);
			}

			return true;
		}

		// A file named by its content: written when it is not there (or not whole), else marked as used now.
		private static void WriteOnce(string file, byte[] bytes)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			var info = new FileInfo(file);
			if (info.Exists && info.Length == bytes.Length)
			{
				File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
				return;
			}

			string temporary = file + WritingExtension;
			File.WriteAllBytes(temporary, bytes);
			File.Move(temporary, file, true);
		}

		// The hash an entry is good for: the batcher, the engine and the editor as built, the scene's path and the document.
		private static byte[] DocumentHash(byte[] scene, string sceneAssetPath)
		{
			using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
			{
				hash.AppendData(Encoding.UTF8.GetBytes(Version + "|" + typeof(MeshCombiner).Assembly.ManifestModule.ModuleVersionId + "|"
					+ typeof(StaticBatcher).Assembly.ManifestModule.ModuleVersionId + "|" + PathKey(sceneAssetPath) + "|"));
				hash.AppendData(scene);
				return hash.GetHashAndReset();
			}
		}

		// The name of a scene's entry: one for its file (text) and one for its snapshot (binary).
		private static string SlotOf(byte[] scene, string sceneAssetPath)
		{
			string text = PathKey(sceneAssetPath) + (Documents.IsBinary(scene) ? "|binary" : "|text");
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 12).ToLowerInvariant();
		}

		private static string PathKey(string sceneAssetPath)
		{
			return (sceneAssetPath ?? "").Replace('\\', '/').ToLowerInvariant();
		}

		// An asset file as it is now: the size and time of the file and of its meta file (import settings) in one number.
		private static long Stamp(string assetPath)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			return unchecked(AssetFileStamps.StampOf(file) * 31 + AssetFileStamps.StampOf(file + AssetDatabase.MetaExtension));
		}

		private static bool WrittenSince(string assetPath, DateTime time)
		{
			try
			{
				string file = AssetDatabase.ToAbsolute(assetPath);
				return (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= time)
					|| (File.Exists(file + AssetDatabase.MetaExtension) && File.GetLastWriteTimeUtc(file + AssetDatabase.MetaExtension) >= time);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return true;
			}
		}

		// Once per folder and run: the entries nothing used for KeepDays days go, files left half written, and the meshes
		// nothing names.
		private static void Prune(string folder)
		{
			if (prunedFolder == folder)
			{
				return;
			}

			prunedFolder = folder;
			try
			{
				if (!Directory.Exists(folder))
				{
					return;
				}

				DateTime now = DateTime.UtcNow;
				foreach (string file in Directory.GetFiles(folder))
				{
					DateTime written = File.GetLastWriteTimeUtc(file);
					bool entry = file.EndsWith(EntryExtension, StringComparison.OrdinalIgnoreCase);
					bool leftover = file.EndsWith(WritingExtension, StringComparison.OrdinalIgnoreCase);
					if ((entry && written < now.AddDays(-KeepDays)) || (leftover && written < now.AddHours(-UnusedMeshHours)))
					{
						File.Delete(file);
					}
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// Left for the next run.
			}

			DeleteUnusedMeshes(folder);
		}

		// The mesh files no entry names, UnusedMeshHours after their last use. Entries of another format (an older
		// editor's) are deleted on the way. Nothing is deleted when an entry cannot be read now (another editor may be
		// writing it: what it names is not known).
		private static void DeleteUnusedMeshes(string folder)
		{
			try
			{
				if (!Directory.Exists(folder))
				{
					return;
				}

				var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string entry in Directory.GetFiles(folder, "*" + EntryExtension))
				{
					bool known;
					try
					{
						using (var reader = new BinaryReader(File.OpenRead(entry)))
						{
							known = ReadHeader(reader, named, out _);
						}
					}
					catch (Exception e) when (e is EndOfStreamException || e is FormatException)
					{
						known = false;
					}

					if (!known)
					{
						File.Delete(entry);
					}
				}

				DateTime limit = DateTime.UtcNow.AddHours(-UnusedMeshHours);
				foreach (string mesh in Directory.GetFiles(folder, "*" + ModelAssets.MeshExtension))
				{
					if (!named.Contains(Path.GetFileName(mesh)) && File.GetLastWriteTimeUtc(mesh) < limit)
					{
						File.Delete(mesh);
					}
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// Left for the next time.
			}
		}

		#endregion
	}
}
