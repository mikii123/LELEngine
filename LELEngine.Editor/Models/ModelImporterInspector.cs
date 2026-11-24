using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Import settings of a model file (scale, units, rotation, normals, UVs, materials) and what the import holds:
	///     nodes, meshes with their sub-meshes, materials. Edits go to a working copy until Apply writes the meta file,
	///     imports the model again and reloads the scene, so renderers show the new import; Revert discards them.
	/// </summary>
	internal static class ModelImporterInspector
	{
		#region PrivateFields

		private static string path;
		private static ModelImporter working;
		private static bool dirty;

		// Several models edited together: the others' paths and working copies (the shown one is path / working).
		private static readonly List<string> otherPaths = new List<string>();
		private static readonly List<ModelImporter> otherWorking = new List<ModelImporter>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     The import settings of several models edited together: the shared settings, "—" where they differ; an edit
		///     sets that setting on all, Apply writes each one's meta file and imports them again, Revert discards it all.
		/// </summary>
		public static void Draw(IReadOnlyList<string> assetPaths, InspectorContext context)
		{
			if (assetPaths.Count == 1)
			{
				Draw(assetPaths[0], context);
				return;
			}

			if (!IsLoaded(assetPaths))
			{
				Load(assetPaths);
			}

			context.UndoTarget = null;
			ImGui.SeparatorText("Import Settings");
			SerializedNode before = MultiEdit.NodeOf(working, new SerializationContext());
			context.Mixed = MixedValues.Compare(working, otherWorking, new SerializationContext());
			try
			{
				dirty |= FieldDrawer.DrawFields(working, context);
			}
			finally
			{
				context.Mixed = null;
			}

			dirty |= MultiEdit.Propagate(working, before, otherWorking, () => new SerializationContext(), null, context.Touched) > 0;
			context.Touched.Clear();

			ImGui.Spacing();
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(!dirty);
			if (EditorGui.Button("Revert", new NVector2(width, 0)))
			{
				Load(assetPaths);
			}

			ImGui.SameLine();
			if (EditorGui.Button("Apply to " + assetPaths.Count + " models##apply", new NVector2(width, 0)))
			{
				ApplyAll(context.Editor);
			}

			ImGui.EndDisabled();
		}

		public static void Draw(string assetPath, InspectorContext context)
		{
			if (ModelAssets.IsMeshFile(assetPath))
			{
				DrawMeshAsset(assetPath, context);
				return;
			}

			if (!string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase) || working == null || otherPaths.Count > 0)
			{
				Load(assetPath);
			}

			// Importer edits are not undo steps (Apply / Revert instead).
			context.UndoTarget = null;
			ImGui.SeparatorText("Import Settings");
			dirty |= FieldDrawer.DrawFields(working, context);

			ImGui.Spacing();
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(!dirty);
			if (EditorGui.Button("Revert", new NVector2(width, 0)))
			{
				Load(assetPath);
			}

			ImGui.SameLine();
			if (EditorGui.Button("Apply", new NVector2(width, 0)))
			{
				Apply(assetPath, context.Editor);
			}

			ImGui.EndDisabled();

			Model model;
			try
			{
				model = ModelAssets.Load(assetPath);
			}
			catch (Exception e)
			{
				ImGui.Spacing();
				ImGui.TextColored(EditorStyle.Error, "Cannot import: " + e.Message);
				return;
			}

			ImGui.Spacing();
			if (EditorGui.Button("Add to Scene", new NVector2(-1, 0)))
			{
				context.Editor.InstantiateModel(assetPath);
			}

			DrawContents(model, context.Editor);
			AssetInspectors.DrawFileInfo(AssetDatabase.ToAbsolute(assetPath));
		}

		/// <summary>Forgets the working copy (selection changed to something else).</summary>
		public static void Reset()
		{
			path = null;
			working = null;
			dirty = false;
			otherPaths.Clear();
			otherWorking.Clear();
		}

		#endregion

		#region PrivateMethods

		/// <summary>A mesh asset (.mesh, made in the engine: a baked mesh): nothing to import, so only what it holds.</summary>
		private static void DrawMeshAsset(string assetPath, InspectorContext context)
		{
			context.UndoTarget = null;
			Model model;
			try
			{
				model = ModelAssets.Load(assetPath);
			}
			catch (Exception e)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot read: " + e.Message);
				return;
			}

			ImGui.TextDisabled("A mesh made in the engine (no source file, no import settings); its materials are the renderer's.");
			ImGui.Spacing();
			if (EditorGui.Button("Add to Scene", new NVector2(-1, 0)))
			{
				context.Editor.InstantiateModel(assetPath);
			}

			foreach (Mesh mesh in model.Meshes)
			{
				ImGui.Spacing();
				ImGui.TextUnformatted($"{mesh.Vertices.Length:N0} vertices, {mesh.TriangleCount:N0} triangles, {mesh.SubMeshCount} sub-mesh(es)");
				LELEngine.Mathematics.Vector3 size = mesh.BoundsMax - mesh.BoundsMin;
				ImGui.TextDisabled(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Size {0:0.###} x {1:0.###} x {2:0.###} m", size.X, size.Y, size.Z));
				for (int i = 0; i < mesh.SubMeshCount; i++)
				{
					ImGui.Bullet();
					ImGui.SameLine();
					ImGui.TextDisabled($"Sub-mesh {i}: {mesh.SubMeshes[i].IndexCount / 3:N0} triangles");
				}
			}

			AssetInspectors.DrawFileInfo(AssetDatabase.ToAbsolute(assetPath));
		}

		private static void Load(string assetPath)
		{
			path = assetPath;
			working = Clone(AssetDatabase.GetImporter<ModelImporter>(assetPath));
			otherPaths.Clear();
			otherWorking.Clear();
			dirty = false;
		}

		private static void Load(IReadOnlyList<string> assetPaths)
		{
			Load(assetPaths[0]);
			for (int i = 1; i < assetPaths.Count; i++)
			{
				otherPaths.Add(assetPaths[i]);
				otherWorking.Add(Clone(AssetDatabase.GetImporter<ModelImporter>(assetPaths[i])));
			}
		}

		private static bool IsLoaded(IReadOnlyList<string> assetPaths)
		{
			if (working == null || !string.Equals(path, assetPaths[0], StringComparison.OrdinalIgnoreCase) || otherPaths.Count != assetPaths.Count - 1)
			{
				return false;
			}

			for (int i = 1; i < assetPaths.Count; i++)
			{
				if (!string.Equals(otherPaths[i - 1], assetPaths[i], StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>Every model's settings into its meta file, then one scene reload for all of them.</summary>
		private static void ApplyAll(EditorApplication editor)
		{
			var paths = new List<string> { path };
			paths.AddRange(otherPaths);
			var settings = new List<ModelImporter> { working };
			settings.AddRange(otherWorking);
			// The import itself happens as the reloaded scene reads the models.
			using Progress.Scope progress = Progress.Begin("Importing " + paths.Count + " models");
			for (int i = 0; i < paths.Count; i++)
			{
				AssetMeta meta = AssetDatabase.GetMeta(paths[i]);
				settings[i].version++;
				meta.importer = Clone(settings[i]);
				AssetDatabase.SaveMeta(paths[i], meta);
			}

			dirty = false;
			editor.ReloadScene(() =>
			{
				foreach (string model in paths)
				{
					ModelAssets.Unload(model);
				}
			});
			editor.NotifyAssetsChanged();
			Lighting.GI.InvalidateStatic();
		}

		private static ModelImporter Clone(ModelImporter importer)
		{
			return Documents.ReadObject<ModelImporter>(Documents.WriteObject(importer, DocumentFormat.Binary)) ?? new ModelImporter();
		}

		private static void Apply(string assetPath, EditorApplication editor)
		{
			using Progress.Scope progress = Progress.Begin("Importing " + assetPath);
			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			working.version++;
			meta.importer = Clone(working);
			AssetDatabase.SaveMeta(assetPath, meta);
			dirty = false;
			// Renderers hold the old meshes: the scene is rebuilt from its saved state with the new import.
			editor.ReloadScene(() => ModelAssets.Unload(assetPath));
			editor.NotifyAssetsChanged();
			Lighting.GI.InvalidateStatic();
		}

		private static void DrawContents(Model model, EditorApplication editor)
		{
			int vertices = 0;
			int triangles = 0;
			foreach (Mesh mesh in model.Meshes)
			{
				vertices += mesh.Vertices.Length;
				triangles += mesh.TriangleCount;
			}

			ImGui.Spacing();
			ImGui.TextDisabled($"{model.Nodes.Count} nodes, {model.Meshes.Count} meshes, {vertices} vertices, {triangles} triangles");
			if (model.GetBounds(out LELEngine.Mathematics.Vector3 min, out LELEngine.Mathematics.Vector3 max))
			{
				LELEngine.Mathematics.Vector3 size = max - min;
				ImGui.TextDisabled(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Size {0:0.###} x {1:0.###} x {2:0.###} m", size.X, size.Y, size.Z));
			}

			if (EditorGui.CollapsingHeader("Meshes (" + model.Meshes.Count + ")"))
			{
				foreach (Mesh mesh in model.Meshes)
				{
					ImGui.Bullet();
					ImGui.SameLine();
					ImGui.TextUnformatted($"{mesh.SubAssetName}: {mesh.Vertices.Length} vertices, {mesh.TriangleCount} triangles, {mesh.SubMeshCount} sub-meshes");
				}
			}

			ModelData data = model.Data;
			if (EditorGui.CollapsingHeader("Materials (" + data.Materials.Count + ")"))
			{
				foreach (ModelMaterial material in data.Materials)
				{
					ImGui.PushID(material.Name);
					if (material.AssetPath != null && File.Exists(AssetDatabase.ToAbsolute(material.AssetPath)))
					{
						if (EditorGui.Selectable(material.Name + "  ->  " + material.AssetPath))
						{
							editor.SelectAsset(material.AssetPath);
						}
					}
					else
					{
						ImGui.Bullet();
						ImGui.SameLine();
						ImGui.TextUnformatted(material.Name);
						ImGui.SameLine();
						ImGui.TextDisabled("(default material)");
					}

					ImGui.PopID();
				}
			}

			if (EditorGui.CollapsingHeader("Hierarchy"))
			{
				DrawNode(model, 0, 0);
			}
		}

		private static void DrawNode(Model model, int index, int depth)
		{
			if (depth > 64)
			{
				return;
			}

			ModelNode node = model.Nodes[index];
			string label = index == 0 ? model.name : node.Name;
			if (node.Mesh >= 0)
			{
				label += "  [" + model.Meshes[node.Mesh].SubAssetName + "]";
			}

			bool hasChildren = false;
			for (int i = index + 1; i < model.Nodes.Count && !hasChildren; i++)
			{
				hasChildren = model.Nodes[i].Parent == index;
			}

			ImGuiTreeNodeFlags flags = hasChildren ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen;
			ImGui.PushID(index);
			bool open = EditorGui.TreeNodeEx(label, flags);
			ImGui.PopID();
			if (!hasChildren || !open)
			{
				return;
			}

			for (int i = index + 1; i < model.Nodes.Count; i++)
			{
				if (model.Nodes[i].Parent == index)
				{
					DrawNode(model, i, depth + 1);
				}
			}

			ImGui.TreePop();
		}

		#endregion
	}
}
