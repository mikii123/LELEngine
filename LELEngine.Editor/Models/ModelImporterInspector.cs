using System;
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

		#endregion

		#region PublicMethods

		public static void Draw(string assetPath, InspectorContext context)
		{
			if (!string.Equals(path, assetPath, StringComparison.OrdinalIgnoreCase) || working == null)
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
			if (ImGui.Button("Revert", new NVector2(width, 0)))
			{
				Load(assetPath);
			}

			ImGui.SameLine();
			if (ImGui.Button("Apply", new NVector2(width, 0)))
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
			if (ImGui.Button("Add to Scene", new NVector2(-1, 0)))
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
		}

		#endregion

		#region PrivateMethods

		private static void Load(string assetPath)
		{
			path = assetPath;
			working = Clone(AssetDatabase.GetImporter<ModelImporter>(assetPath));
			dirty = false;
		}

		private static ModelImporter Clone(ModelImporter importer)
		{
			return Documents.ReadObject<ModelImporter>(Documents.WriteObject(importer, DocumentFormat.Binary)) ?? new ModelImporter();
		}

		private static void Apply(string assetPath, EditorApplication editor)
		{
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
			if (model.GetBounds(out OpenTK.Mathematics.Vector3 min, out OpenTK.Mathematics.Vector3 max))
			{
				OpenTK.Mathematics.Vector3 size = max - min;
				ImGui.TextDisabled(string.Format(System.Globalization.CultureInfo.InvariantCulture, "Size {0:0.###} x {1:0.###} x {2:0.###} m", size.X, size.Y, size.Z));
			}

			if (ImGui.CollapsingHeader("Meshes (" + model.Meshes.Count + ")"))
			{
				foreach (Mesh mesh in model.Meshes)
				{
					ImGui.Bullet();
					ImGui.SameLine();
					ImGui.TextUnformatted($"{mesh.SubAssetName}: {mesh.Vertices.Length} vertices, {mesh.TriangleCount} triangles, {mesh.SubMeshCount} sub-meshes");
				}
			}

			ModelData data = model.Data;
			if (ImGui.CollapsingHeader("Materials (" + data.Materials.Count + ")"))
			{
				foreach (ModelMaterial material in data.Materials)
				{
					ImGui.PushID(material.Name);
					if (material.AssetPath != null && File.Exists(AssetDatabase.ToAbsolute(material.AssetPath)))
					{
						if (ImGui.Selectable(material.Name + "  ->  " + material.AssetPath))
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

			if (ImGui.CollapsingHeader("Hierarchy"))
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
			bool open = ImGui.TreeNodeEx(label, flags);
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
