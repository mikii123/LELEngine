using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     Assets > Force Reserialize All Assets (Unity's AssetDatabase.ForceReserializeAssets): every document of the project
	///     (scenes, prefabs, materials, ScriptableObjects, meta files, VFX graphs, audio projects, clips, controllers, timelines,
	///     physics materials, ProjectSettings.asset) read from its file with the current types and written again as the
	///     editor writes it, so files written by other tools, older engines or by hand (one-line objects, another order of the
	///     type table, CRLF, fields a class gained since) stop changing when the editor next saves them. A file read the
	///     same as it is written is not touched.
	///     Each file is read fresh (never the editor's loaded copy, which may hold unsaved edits) on the main thread, one
	///     at a time within a frame budget, so the editor's own writes never interleave with it. Textures are read from their
	///     headers only (<see cref="TextureImportBatch.BeginOnDemand" />): New Frontier's images would be tens of GB.
	/// </summary>
	internal sealed class AssetReserializer
	{
		#region PublicFields

		public int Total => files.Count;
		public int Done => next;
		public bool Finished => next >= files.Count;

		/// <summary>For the menu bar: "Reserializing 120 / 4455: Prefabs/Ship.prefab".</summary>
		public string Status => Finished ? null : $"Reserializing {next} / {files.Count}: {Display(files[next])}";

		/// <summary>Files written again (their bytes changed), project-relative.</summary>
		public readonly List<string> Changed = new List<string>();

		/// <summary>Files left as they are, with the reason (unreadable, a type that no longer exists, open with unsaved changes).</summary>
		public readonly List<string> Skipped = new List<string>();

		public readonly List<string> Failed = new List<string>();

		#endregion

		#region PrivateFields

		private const double FrameBudgetMilliseconds = 40;

		private readonly ProjectInfo project;
		private readonly List<string> files;
		private readonly HashSet<string> unsaved;
		private readonly Stopwatch clock = Stopwatch.StartNew();
		private int next;
		private int unchanged;

		#endregion

		#region Constructors

		/// <param name="unsavedAssets">Asset paths a tool holds unsaved changes of (VFX Graph, Audio Studio): left alone.</param>
		public AssetReserializer(ProjectInfo project, IEnumerable<string> unsavedAssets)
		{
			this.project = project;
			files = CollectFiles(project);
			unsaved = new HashSet<string>(unsavedAssets, StringComparer.OrdinalIgnoreCase);
		}

		#endregion

		#region PublicMethods

		/// <summary>The project's document files in a fixed order: Assets (every folder but hidden ones) and ProjectSettings.asset.</summary>
		public static List<string> CollectFiles(ProjectInfo project)
		{
			var result = new List<string>();
			if (Directory.Exists(project.AssetsPath))
			{
				foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*", SearchOption.AllDirectories))
				{
					if (Documents.IsDocumentFile(file) && !IsHidden(Path.GetRelativePath(project.AssetsPath, file)))
					{
						result.Add(Path.GetFullPath(file));
					}
				}
			}

			result.Sort(StringComparer.Ordinal);
			if (File.Exists(project.ProjectSettingsFile))
			{
				result.Add(Path.GetFullPath(project.ProjectSettingsFile));
			}

			return result;
		}

		/// <summary>Works through the files for about <see cref="FrameBudgetMilliseconds" /> (at least one); true when all are done.</summary>
		public bool Step()
		{
			long start = Stopwatch.GetTimestamp();
			do
			{
				if (Finished)
				{
					return true;
				}

				string file = files[next++];
				try
				{
					Reserialize(file);
				}
				catch (Exception e)
				{
					Failed.Add(Display(file) + ": " + e.Message);
				}
			}
			while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < FrameBudgetMilliseconds);

			return Finished;
		}

		/// <summary>One console line for the whole run, the changed files' count by kind; failures and skips as warnings.</summary>
		/// <summary>Logs the run's summary (and the files skipped or failed); returns the summary line.</summary>
		public string Report()
		{
			var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
			foreach (string path in Changed)
			{
				string kind = Path.GetExtension(path);
				byKind[kind] = byKind.TryGetValue(kind, out int count) ? count + 1 : 1;
			}

			var kinds = new List<string>();
			foreach (KeyValuePair<string, int> entry in byKind)
			{
				kinds.Add(entry.Value + " " + entry.Key);
			}

			string summary = $"Force Reserialize All Assets: {files.Count} files in {clock.Elapsed.TotalSeconds:F1} s; {Changed.Count} written again"
				+ (kinds.Count > 0 ? " (" + string.Join(", ", kinds) + ")" : "") + $", {unchanged} already as the editor writes them"
				+ (Skipped.Count > 0 ? $", {Skipped.Count} left as they are" : "") + (Failed.Count > 0 ? $", {Failed.Count} failed" : "");
			Debug.Log("[Editor] " + summary);
			if (Skipped.Count > 0)
			{
				Debug.LogWarning("[Editor] Force Reserialize All Assets left these files as they are:\n" + string.Join("\n", Skipped));
			}

			if (Failed.Count > 0)
			{
				Debug.LogError("[Editor] Force Reserialize All Assets could not reserialize:\n" + string.Join("\n", Failed));
			}

			return summary;
		}

		/// <summary>The bytes the editor writes for this document file (null with a reason when it should be left alone).</summary>
		public static byte[] Reserialized(string file, byte[] data, out string skipReason)
		{
			skipReason = null;
			if (!Documents.IsDocument(data))
			{
				skipReason = "not a document";
				return null;
			}

			string name = Path.GetFileNameWithoutExtension(file);
			string kind = Documents.KindOf(data);
			// Images are read from their headers only; their texels wait for whoever draws them.
			using (TextureImportBatch.BeginOnDemand())
			{
				if (kind == BinaryDocument.KindScene || kind == BinaryDocument.KindPrefab)
				{
					Scene scene = SceneSerializer.LoadFromBytes(data, name);
					try
					{
						if (kind == BinaryDocument.KindScene)
						{
							scene.Name = name;
							return SceneSerializer.SaveToBytes(scene, Documents.FileFormat);
						}

						// As prefab mode saves it (PrefabUtility.SavePrefabScene): the one root.
						List<GameObject> roots = scene.GetRootGameObjects();
						roots.RemoveAll(go => (go.hideFlags & HideFlags.DontSave) != 0 || go.dead);
						if (roots.Count != 1)
						{
							skipReason = $"a prefab with {roots.Count} roots";
							return null;
						}

						return SceneSerializer.SavePrefabToBytes(roots[0], Documents.FileFormat);
					}
					finally
					{
						scene.Unload();
					}
				}

				string typeName = Documents.RootTypeName(data);
				Type type = typeName != null ? TypeRegistry.Resolve(typeName) : null;
				if (type == null)
				{
					// Writing it through no type would drop nothing, but a missing script's asset waits for its script.
					skipReason = $"its type {typeName ?? "(none)"} is not loaded";
					return null;
				}

				object fresh = Documents.ReadObject(data, type, null, new SerializationContext { Location = name });
				if (fresh == null)
				{
					skipReason = "it reads as nothing";
					return null;
				}

				// Every asset's Save names it after its file (Material, clips, graphs, ...).
				SetName(fresh, name);
				return Documents.WriteObject(fresh, Documents.FileFormat, new SerializationContext { Location = name });
			}
		}

		#endregion

		#region PrivateMethods

		private void Reserialize(string file)
		{
			string display = Display(file);
			string assetPath = AssetDatabase.ToAssetPath(file);
			if (assetPath != null && unsaved.Contains(assetPath))
			{
				Skipped.Add(display + ": open with unsaved changes");
				return;
			}

			byte[] data = File.ReadAllBytes(file);
			byte[] written = Reserialized(file, data, out string skipReason);
			if (written == null)
			{
				Skipped.Add(display + ": " + skipReason);
				return;
			}

			if (written.AsSpan().SequenceEqual(data))
			{
				unchanged++;
				return;
			}

			File.WriteAllBytes(file, written);
			Changed.Add(display);
		}

		private static void SetName(object asset, string name)
		{
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
			Type type = asset.GetType();
			FieldInfo field = type.GetField("name", flags);
			if (field != null && field.FieldType == typeof(string) && !field.IsInitOnly)
			{
				field.SetValue(asset, name);
				return;
			}

			PropertyInfo property = type.GetProperty("name", flags);
			if (property != null && property.PropertyType == typeof(string) && property.CanWrite)
			{
				property.SetValue(asset, name);
			}
		}

		private string Display(string file)
		{
			return Path.GetRelativePath(project.RootPath, file).Replace('\\', '/');
		}

		/// <summary>Folders and files the asset database ignores: names starting with '.' or ending with '~'.</summary>
		private static bool IsHidden(string relative)
		{
			foreach (string part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
			{
				if (part.StartsWith(".", StringComparison.Ordinal) || part.EndsWith("~", StringComparison.Ordinal))
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
