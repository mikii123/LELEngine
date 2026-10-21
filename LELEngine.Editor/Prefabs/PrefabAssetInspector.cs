using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector of a prefab file: open it (prefab mode), make a variant, place an instance; its base when it is a
	///     variant, and its hierarchy (nested prefabs marked) with each object's components.
	/// </summary>
	internal static class PrefabAssetInspector
	{
		#region PublicMethods

		public static void Draw(string assetPath, InspectorContext context)
		{
			EditorApplication editor = context.Editor;
			Prefab prefab = Prefab.Load(assetPath);
			ResolvedPrefab resolved = prefab?.Resolved;
			ImGui.BeginDisabled(editor.IsPlaying);
			if (ImGui.Button("Open Prefab", new NVector2(-1, 0)))
			{
				editor.Defer(() => editor.OpenPrefab(assetPath));
			}

			ImGui.EndDisabled();
			float half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			if (ImGui.Button("Create Variant", new NVector2(half, 0)))
			{
				editor.Defer(() => editor.CreatePrefabVariant(assetPath));
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(editor.Scene == null);
			if (ImGui.Button("Instantiate in Scene", new NVector2(half, 0)))
			{
				editor.Defer(() => editor.InstantiatePrefab(assetPath));
			}

			ImGui.EndDisabled();
			ImGui.Spacing();
			if (resolved?.Root == null)
			{
				ImGui.TextColored(EditorStyle.Error, "The prefab cannot be read: " + (resolved?.Error ?? "unknown error"));
				return;
			}

			if (resolved.Error != null)
			{
				ImGui.TextColored(EditorStyle.Warning, resolved.Error);
			}

			Prefab basePrefab = prefab.BasePrefab;
			if (basePrefab != null)
			{
				ImGui.AlignTextToFramePadding();
				ImGui.TextDisabled("Variant of");
				ImGui.SameLine();
				ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Prefab);
				if (ImGui.SmallButton(basePrefab.name + "##base"))
				{
					editor.SelectAsset(basePrefab.AssetPath);
					editor.Ping(basePrefab.AssetPath);
				}

				ImGui.PopStyleColor();
			}

			ImGui.SeparatorText("Hierarchy");
			var children = new Dictionary<ulong, List<PrefabObject>>();
			foreach (PrefabObject obj in resolved.Objects)
			{
				if (!children.TryGetValue(obj.Parent, out List<PrefabObject> list))
				{
					list = new List<PrefabObject>();
					children[obj.Parent] = list;
				}

				list.Add(obj);
			}

			DrawObject(resolved, resolved.Root, children, editor);
			int nested = 0;
			foreach (NestedPrefab item in resolved.Nested)
			{
				if (item.Parent == null && item.RootId != resolved.Root.Id)
				{
					nested++;
				}
			}

			ImGui.Spacing();
			ImGui.TextDisabled($"{resolved.Objects.Count} objects, {resolved.ComponentsById.Count} components, {nested} nested prefabs");
		}

		#endregion

		#region PrivateMethods

		private static void DrawObject(ResolvedPrefab resolved, PrefabObject obj, Dictionary<ulong, List<PrefabObject>> children, EditorApplication editor)
		{
			bool hasChildren = children.ContainsKey(obj.Id);
			resolved.NestedByRoot.TryGetValue(obj.Id, out NestedPrefab nested);
			ImGui.PushID(obj.Id.ToString());
			float icon = ImGui.GetTextLineHeight();
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth | (hasChildren ? ImGuiTreeNodeFlags.None : ImGuiTreeNodeFlags.Leaf);
			if (nested != null)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Prefab);
			}

			bool open = ImGui.TreeNodeEx("      " + obj.Name + "###node", flags);
			if (nested != null)
			{
				ImGui.PopStyleColor();
			}

			NVector2 min = ImGui.GetItemRectMin();
			EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(min.X + ImGui.GetTreeNodeToLabelSpacing(), min.Y + (ImGui.GetItemRectSize().Y - icon) * 0.5f), icon,
				nested != null ? (Prefab.Load(nested.PrefabPath)?.IsVariant == true ? IconKind.PrefabVariant : IconKind.Prefab) : IconKind.GameObject,
				ImGui.GetColorU32(nested != null ? EditorStyle.Prefab : new System.Numerics.Vector4(0.75f, 0.75f, 0.78f, 1f)));
			if (ImGui.IsItemHovered())
			{
				var lines = new List<string>();
				if (nested != null)
				{
					lines.Add("Instance of " + nested.PrefabPath);
				}

				foreach (PrefabComponent component in obj.Components)
				{
					lines.Add(GenericValues.Describe(component.Data, ValueTag.Object) + (component.Enabled ? "" : " (disabled)"));
				}

				ImGui.SetTooltip(string.Join("\n", lines));
			}

			if (nested != null && ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
			{
				string path = nested.PrefabPath;
				editor.Defer(() => editor.OpenPrefab(path));
			}

			if (open)
			{
				if (hasChildren)
				{
					foreach (PrefabObject child in children[obj.Id])
					{
						DrawObject(resolved, child, children, editor);
					}
				}

				ImGui.TreePop();
			}

			ImGui.PopID();
		}

		#endregion
	}
}
