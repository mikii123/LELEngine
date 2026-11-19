using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using LELEngine.Serialization;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     The Mesh Baker's work (the window and <see cref="MeshBaker" /> call it): which renderers of the given objects are
	///     merged, where the result goes, and the bake itself as one undo step.
	/// </summary>
	internal static class MeshBaking
	{
		#region PrivateFields

		private const string DefaultName = "Baked Mesh";

		// The largest arrays a mesh can hold (MeshCombiner refuses more).
		private const long MaxElements = int.MaxValue - 64;

		/// <summary>What a bake takes, worked out before anything changes.</summary>
		private sealed class Plan
		{
			public readonly List<GameObject> Roots = new List<GameObject>();
			public readonly List<MeshRenderer> Renderers = new List<MeshRenderer>();
			public readonly MeshBakeSummary Summary = new MeshBakeSummary();
		}

		#endregion

		#region PublicMethods

		public static MeshBakeSummary Describe(EditorApplication editor, IEnumerable<GameObject> objects, MeshBakeOptions options)
		{
			return Prepare(editor, objects, options).Summary;
		}

		public static GameObject Bake(EditorApplication editor, IEnumerable<GameObject> objects, MeshBakeOptions options)
		{
			if (editor == null)
			{
				return null;
			}

			Plan plan = Prepare(editor, objects, options);
			MeshBakeSummary summary = plan.Summary;
			if (summary.Problem != null)
			{
				Debug.LogWarning("[Mesh Baker] Nothing baked. " + summary.Problem);
				return null;
			}

			var watch = Stopwatch.StartNew();
			Scene scene = editor.Scene;
			editor.Edits.Commit();
			byte[] before = SceneSerializer.SaveToBytes(scene);
			ulong[] selectionBefore = editor.CaptureSelection();

			GameObject baked = null;
			try
			{
				baked = scene.CreateGameObject(summary.Name);
				Place(editor, plan, baked);

				// The mesh lives in the baked object's space: the object can be moved like any other afterwards.
				Matrix4 toBaked = baked.transform.WorldToLocal;
				var instances = new List<CombineInstance>(plan.Renderers.Count);
				foreach (MeshRenderer renderer in plan.Renderers)
				{
					instances.Add(new CombineInstance(renderer.Mesh, renderer.transform.LocalToWorld * toBaked, renderer.Materials));
				}

				CombinedMesh combined = MeshCombiner.Combine(instances, summary.Name);
				Mesh mesh = ModelAssets.SaveMesh(summary.AssetPath, combined.Mesh);
				MeshRenderer first = plan.Renderers[0];
				MeshRenderer output = baked.AddComponent<MeshRenderer>();
				output.SetMesh(mesh);
				output.SetMaterials(combined.Materials);
				output.CastShadows = first.CastShadows;
				output.ReceiveShadows = first.ReceiveShadows;
				output.ContributesToGI = first.ContributesToGI;
				output.ReceiveGI = first.ReceiveGI;
				output.IsStatic = first.IsStatic;
			}
			catch (Exception e)
			{
				// Nothing else has changed yet: the scene is as it was.
				if (baked != null)
				{
					GameObject.DestroyImmediate(baked);
				}

				Debug.LogError("[Mesh Baker] Nothing baked: " + e.Message);
				return null;
			}

			switch (options.Originals)
			{
				case MeshBakeOriginals.Delete:
					foreach (GameObject root in plan.Roots)
					{
						GameObject.DestroyImmediate(root);
					}

					break;
				case MeshBakeOriginals.Deactivate:
					foreach (GameObject root in plan.Roots)
					{
						root.SetActive(false);
					}

					break;
			}

			editor.Select(baked);
			editor.Undo.Push(new MeshBakeRecord
			{
				Name = "Bake " + summary.Name,
				AssetPath = summary.AssetPath,
				SceneBefore = before,
				SceneAfter = SceneSerializer.SaveToBytes(scene),
				SelectionBefore = selectionBefore,
				SelectionAfter = editor.CaptureSelection()
			});
			editor.MarkDirty();
			Lighting.GI.InvalidateStatic();
			editor.NotifyAssetsChanged();
			Debug.Log("[Mesh Baker] " + Outcome(summary) + " (" + watch.ElapsedMilliseconds + " ms)");
			return baked;
		}

		/// <summary>"Baked 6 meshes of 3 objects into Scenes/Rocks.mesh: 576 vertices, 208 triangles, 2 materials".</summary>
		public static string Outcome(MeshBakeSummary summary)
		{
			return "Baked " + Count(summary.Meshes, "mesh", "meshes") + " of " + Count(summary.Objects, "object", "objects") + " into " + summary.AssetPath + ": " + Geometry(summary);
		}

		/// <summary>"576 vertices, 208 triangles, 2 materials".</summary>
		public static string Geometry(MeshBakeSummary summary)
		{
			return Count(summary.Vertices, "vertex", "vertices") + ", " + Count(summary.Triangles, "triangle", "triangles") + ", " + Count(summary.Materials, "material", "materials");
		}

		/// <summary>A number with its noun: "1 mesh", "1,204 meshes".</summary>
		public static string Count(long count, string one, string many)
		{
			return count.ToString("N0", CultureInfo.InvariantCulture) + " " + (count == 1 ? one : many);
		}

		#endregion

		#region PrivateMethods

		private static Plan Prepare(EditorApplication editor, IEnumerable<GameObject> objects, MeshBakeOptions options)
		{
			var plan = new Plan();
			MeshBakeSummary summary = plan.Summary;
			Scene scene = editor?.Scene;
			if (scene == null)
			{
				summary.Problem = "No scene is open.";
				return plan;
			}

			// The objects given, without those under another of them (they are part of it).
			var given = new HashSet<GameObject>(ReferenceEqualityComparer.Instance);
			var ordered = new List<GameObject>();
			foreach (GameObject go in objects ?? Array.Empty<GameObject>())
			{
				if (go != null && !go.dead && go.scene == scene && given.Add(go))
				{
					ordered.Add(go);
				}
			}

			foreach (GameObject go in ordered)
			{
				bool under = false;
				for (Transform parent = go.transform.parent; parent != null && !under; parent = parent.parent)
				{
					under = given.Contains(parent.gameObject);
				}

				if (!under)
				{
					plan.Roots.Add(go);
				}
			}

			summary.Objects = plan.Roots.Count;
			if (plan.Roots.Count == 0)
			{
				summary.Problem = "Select the objects to bake in the Hierarchy or the Scene view (Ctrl or Shift click selects several).";
				return plan;
			}

			string name = string.IsNullOrWhiteSpace(options.Name) ? plan.Roots.Count == 1 ? plan.Roots[0].Name : DefaultName : options.Name.Trim();
			summary.Name = name;
			Collect(plan, options);

			string folder = FolderOf(editor, options, out string folderProblem);
			if (folder != null)
			{
				summary.AssetPath = UniqueAssetPath((folder.Length > 0 ? folder + "/" : "") + SafeFileName(name) + ModelAssets.MeshExtension);
			}

			if (editor.IsPlaying)
			{
				summary.Problem = "Meshes are baked in edit mode: stop the game first.";
			}
			else if (plan.Roots.Exists(root => root == editor.StageRoot))
			{
				summary.Problem = "The root of the edited prefab stays: select the objects under it.";
			}
			else if (plan.Roots.Find(TempObjects.IsReadOnly) is GameObject kept)
			{
				summary.Problem = "'" + kept.Name + "' is not editable (a script keeps it): it cannot be baked.";
			}
			else if (plan.Renderers.Count == 0)
			{
				summary.Problem = "The selection has no meshes to bake" + (summary.Transparent > 0 ? ": " + Count(summary.Transparent, "transparent renderer is", "transparent renderers are") + " left out (Include Transparent is off)." : ".");
			}
			else if (summary.Vertices > MaxElements || summary.Triangles * 3 > MaxElements)
			{
				summary.Problem = "Too much for one mesh (" + summary.Vertices.ToString("N0", CultureInfo.InvariantCulture) + " vertices): bake fewer objects at once.";
			}
			else if (folderProblem != null)
			{
				summary.Problem = folderProblem;
			}

			return plan;
		}

		/// <summary>The renderers a bake takes of the plan's objects: active, enabled, with triangles; of a LOD Group one level; transparent ones unless left out.</summary>
		private static void Collect(Plan plan, MeshBakeOptions options)
		{
			MeshBakeSummary summary = plan.Summary;

			// LOD groups among the objects (and above them: an object selected inside a group): one level is baked, the
			// renderers of the other levels are left out. A disabled group leaves its renderers alone, as when drawing.
			var wanted = new HashSet<MeshRenderer>(ReferenceEqualityComparer.Instance);
			var otherLevels = new HashSet<MeshRenderer>(ReferenceEqualityComparer.Instance);
			var groups = new HashSet<LODGroup>(ReferenceEqualityComparer.Instance);
			foreach (GameObject root in plan.Roots)
			{
				groups.UnionWith(root.GetComponentsInChildren<LODGroup>());
				groups.UnionWith(root.GetComponentsInParent<LODGroup>());
			}

			foreach (LODGroup group in groups)
			{
				if (!group.enabled)
				{
					continue;
				}

				LOD[] levels = group.GetLODs();
				int level = Math.Clamp(options.LodLevel, 0, Math.Max(0, levels.Length - 1));
				for (int i = 0; i < levels.Length; i++)
				{
					foreach (MeshRenderer renderer in levels[i].renderers)
					{
						if (renderer != null)
						{
							(i == level ? wanted : otherLevels).Add(renderer);
						}
					}
				}
			}

			var seen = new HashSet<MeshRenderer>(ReferenceEqualityComparer.Instance);
			var materials = new HashSet<Material>(ReferenceEqualityComparer.Instance);
			bool withoutMaterial = false;
			foreach (GameObject root in plan.Roots)
			{
				foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
				{
					// What is not saved with the scene (a script's temporary objects) is not baked into an asset.
					if (!seen.Add(renderer) || !renderer.enabled || renderer.Mesh == null || renderer.Mesh.TriangleCount == 0 || TempObjects.IsTemporary(renderer.gameObject))
					{
						continue;
					}

					if (otherLevels.Contains(renderer) && !wanted.Contains(renderer))
					{
						summary.OtherLevels++;
						continue;
					}

					if (!options.IncludeTransparent && renderer.HasTransparentParts)
					{
						summary.Transparent++;
						continue;
					}

					plan.Renderers.Add(renderer);
					summary.Meshes++;
					summary.Vertices += renderer.Mesh.Vertices.Length;
					summary.Triangles += renderer.Mesh.TriangleCount;
					for (int s = 0; s < renderer.SubMeshCount; s++)
					{
						Material material = renderer.GetMaterial(s);
						if (material == null)
						{
							withoutMaterial = true;
						}
						else
						{
							materials.Add(material);
						}
					}
				}
			}

			summary.Materials = materials.Count + (withoutMaterial ? 1 : 0);
		}

		/// <summary>
		///     Where the baked object stands. One object baked: its place exactly (parent, position, rotation, scale, its
		///     row in the hierarchy), so the pivot stays. Several: under their parent when they share one, at the middle of
		///     the meshes' bounds.
		/// </summary>
		private static void Place(EditorApplication editor, Plan plan, GameObject baked)
		{
			List<GameObject> roots = plan.Roots;
			Transform parent = roots[0].transform.parent;
			foreach (GameObject root in roots)
			{
				if (root.transform.parent != parent)
				{
					parent = null;
					break;
				}
			}

			// A prefab has one root: what would stand beside it goes under it.
			parent ??= editor.StageRoot?.transform;
			baked.layer = roots[0].layer;
			if (parent != null)
			{
				baked.transform.SetParent(parent, false);
			}

			if (roots.Count == 1)
			{
				Transform source = roots[0].transform;
				baked.transform.localPosition = source.localPosition;
				baked.transform.localRotation = source.localRotation;
				baked.transform.localScale = source.localScale;
				if (parent != null)
				{
					baked.transform.SetSiblingIndex(source.GetSiblingIndex());
				}

				return;
			}

			Vector3 min = new Vector3(float.MaxValue);
			Vector3 max = new Vector3(float.MinValue);
			foreach (MeshRenderer renderer in plan.Renderers)
			{
				Matrix4 localToWorld = renderer.transform.LocalToWorld;
				Vector3 a = renderer.Mesh.BoundsMin;
				Vector3 b = renderer.Mesh.BoundsMax;
				for (int corner = 0; corner < 8; corner++)
				{
					Vector3 point = Vector3.TransformPosition(new Vector3((corner & 1) != 0 ? b.X : a.X, (corner & 2) != 0 ? b.Y : a.Y, (corner & 4) != 0 ? b.Z : a.Z), localToWorld);
					min = Vector3.ComponentMin(min, point);
					max = Vector3.ComponentMax(max, point);
				}
			}

			baked.transform.position = (min + max) * 0.5f;
		}

		/// <summary>The mesh asset's folder (relative to Assets, "" for Assets itself): the one asked for, else the scene's (the edited prefab's); null with a problem.</summary>
		private static string FolderOf(EditorApplication editor, MeshBakeOptions options, out string problem)
		{
			problem = null;
			if (string.IsNullOrWhiteSpace(options.Folder))
			{
				string document = editor.Stage?.AssetPath ?? editor.Scene?.Path;
				return string.IsNullOrEmpty(document) ? "" : Path.GetDirectoryName(document.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
			}

			string folder = options.Folder.Trim().Replace('\\', '/').Trim('/');
			if (folder.Equals("Assets", StringComparison.OrdinalIgnoreCase))
			{
				return "";
			}

			if (folder.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
			{
				folder = folder.Substring("Assets/".Length);
			}

			bool outside = folder.Contains(':') || (folder + "/").StartsWith(AssetDatabase.EnginePrefix, StringComparison.OrdinalIgnoreCase) || folder.IndexOfAny(Path.GetInvalidPathChars()) >= 0;
			foreach (string part in folder.Split('/'))
			{
				outside |= part == ".." || part == "." || part.Length == 0 || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;
			}

			if (outside)
			{
				problem = "The folder is one of the project's, relative to Assets (as Meshes/Baked); '" + options.Folder.Trim() + "' is not.";
				return null;
			}

			return folder;
		}

		/// <summary>The path, or with " 1", " 2"... before the extension when a file has it: a bake never writes over a file.</summary>
		private static string UniqueAssetPath(string assetPath)
		{
			if (!File.Exists(AssetDatabase.ToAbsolute(assetPath)))
			{
				return assetPath;
			}

			string extension = Path.GetExtension(assetPath);
			string stem = assetPath.Substring(0, assetPath.Length - extension.Length);
			for (int i = 1;; i++)
			{
				string candidate = stem + " " + i + extension;
				if (!File.Exists(AssetDatabase.ToAbsolute(candidate)))
				{
					return candidate;
				}
			}
		}

		private static string SafeFileName(string name)
		{
			foreach (char c in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(c, '_');
			}

			name = name.Trim().TrimEnd('.');
			return name.Length == 0 ? DefaultName : name;
		}

		#endregion
	}

	/// <summary>
	///     A bake: the scene before and after it, and the mesh asset it wrote. Undo brings the scene back and takes the
	///     asset out of the project (its bytes kept by the record while it is undone); redo puts the asset back under its
	///     GUID and the baked scene with it.
	/// </summary>
	internal sealed class MeshBakeRecord : UndoRecord
	{
		#region PublicFields

		public string AssetPath;
		public byte[] SceneBefore;
		public byte[] SceneAfter;
		public ulong[] SelectionBefore;
		public ulong[] SelectionAfter;

		#endregion

		#region PrivateFields

		private byte[] file;
		private byte[] meta;

		#endregion

		#region PublicMethods

		public override void Undo(EditorApplication editor)
		{
			// The scene first: nothing draws the mesh when its file goes.
			editor.RestoreSnapshot(SceneBefore, SelectionBefore);
			string path = AssetDatabase.ToAbsolute(AssetPath);
			string metaPath = path + AssetDatabase.MetaExtension;
			try
			{
				file = File.Exists(path) ? File.ReadAllBytes(path) : null;
				meta = file != null && File.Exists(metaPath) ? File.ReadAllBytes(metaPath) : null;
				if (file != null)
				{
					File.Delete(path);
					File.Delete(metaPath);
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[Mesh Baker] " + AssetPath + " stays in the project (" + e.Message + ")");
				file = null;
				meta = null;
			}

			ModelAssets.Unload(AssetPath);
			AssetDatabase.Refresh(true);
			editor.NotifyAssetsChanged();
		}

		public override void Redo(EditorApplication editor)
		{
			if (file != null)
			{
				string path = AssetDatabase.ToAbsolute(AssetPath);
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					// The meta file first: the asset comes back under the GUID the scene references.
					if (meta != null)
					{
						File.WriteAllBytes(path + AssetDatabase.MetaExtension, meta);
					}

					File.WriteAllBytes(path, file);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					Debug.LogError("[Mesh Baker] Cannot write " + AssetPath + " back (" + e.Message + ")");
				}

				file = null;
				meta = null;
			}

			ModelAssets.Unload(AssetPath);
			AssetDatabase.Refresh(true);
			editor.RestoreSnapshot(SceneAfter, SelectionAfter);
			editor.NotifyAssetsChanged();
		}

		#endregion
	}
}
