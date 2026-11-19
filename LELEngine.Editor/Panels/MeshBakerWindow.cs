using System;
using System.Collections.Generic;
using System.Text;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Window > Mesh Baker: merges the meshes of the selected objects (each with everything under it) into one mesh
	///     asset and puts one object drawing it in their place. It shows what the selection holds (meshes, vertices,
	///     triangles, the materials that become the mesh's sub-meshes, what is left out and why) and where the mesh will
	///     be written; Bake does it as one undo step (<see cref="MeshBaking" />).
	/// </summary>
	internal sealed class MeshBakerWindow : IEditorPanel
	{
		#region PublicFields

		public const string Title = "Mesh Baker";

		/// <summary>The window is shown (Window menu, close button; saved with the editor's windows).</summary>
		public bool Open;

		string IEditorPanel.PanelName => Title;
		string IEditorPanel.ImGuiName => ImGuiId;
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(460, 520);
		void IEditorPanel.Focus() => Show();

		#endregion

		#region PrivateFields

		private const string ImGuiId = Title + "###meshbaker";
		private const string TransparentToggle = "MeshBaker.IncludeTransparent";

		// With a large selection the summary is not worked out again for every frame the scene changes in.
		private const double SummarySeconds = 0.3;

		private static readonly string[] OriginalsLabels = { "Delete", "Deactivate", "Keep" };

		private static readonly string[] OriginalsTooltips =
		{
			"The selected objects are deleted: the baked object stands in their place",
			"The selected objects stay in the scene, switched off",
			"The selected objects stay as they are, next to the baked object"
		};

		private readonly EditorApplication editor;
		private readonly MeshBakeOptions options = new MeshBakeOptions();
		private string name = "";
		private string folder = "";

		// The summary shown and what it was worked out for.
		private MeshBakeSummary summary;
		private string summaryKey;
		private int summaryVersion = -1;
		private double summaryTime;

		// The last bake's outcome, shown under the button until the selection is baked again.
		private string lastBake;
		private bool lastBakeFailed;

		#endregion

		#region Constructors

		public MeshBakerWindow(EditorApplication editor)
		{
			this.editor = editor;
			options.IncludeTransparent = EditorPreferences.GetToggle(TransparentToggle, true);
		}

		#endregion

		#region PublicMethods

		public void Show()
		{
			Open = true;
			editor.RequestWindowFocus(ImGuiId);
		}

		/// <summary>The project closed.</summary>
		public void Reset()
		{
			summary = null;
			summaryKey = null;
			lastBake = null;
			name = "";
			folder = "";
		}

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(440, 480), ImGuiCond.FirstUseEver);
			if (ImGui.Begin(ImGuiId, ref Open))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled("Merges the meshes of the selected objects, and of everything under them, into one mesh asset, and puts one object in their place: one draw call per material.");
			ImGui.PopTextWrapPos();

			options.Name = name;
			options.Folder = folder;
			IReadOnlyList<GameObject> selection = editor.SelectedObjects;
			MeshBakeSummary current = Summarize(selection);

			ImGui.SeparatorText("Selection");
			if (current.Objects == 0)
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted("Nothing selected. Select the objects to bake in the Hierarchy or the Scene view: Ctrl or Shift click selects several.");
				ImGui.PopTextWrapPos();
			}
			else
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(DescribeObjects(selection, current.Objects));
				ImGui.TextUnformatted(MeshBaking.Count(current.Meshes, "mesh", "meshes") + ", " + MeshBaking.Geometry(current));
				if (current.OtherLevels > 0)
				{
					ImGui.TextDisabled(MeshBaking.Count(current.OtherLevels, "renderer", "renderers") + " of other LOD levels left out");
				}

				if (current.Transparent > 0)
				{
					ImGui.TextDisabled(MeshBaking.Count(current.Transparent, "transparent renderer", "transparent renderers") + " left out");
				}

				ImGui.PopTextWrapPos();
			}

			ImGui.SeparatorText("Options");
			float labelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.36f);
			Label("Name", "Name of the baked object and of its mesh asset (empty: the selected object's name, or \"Baked Mesh\" for several)", labelWidth);
			EditorGui.InputTextWithHint("##Name", current.Objects > 0 && string.IsNullOrWhiteSpace(name) ? current.Name ?? "" : "", ref name, 128);

			Label("Folder", "Folder of the mesh asset, relative to Assets (empty: next to the scene, or the prefab being edited)", labelWidth);
			EditorGui.InputTextWithHint("##Folder", "next to the scene", ref folder, 260);

			Label("LOD Level", "The level baked of objects under a LOD Group (0: the most detailed); the other levels' renderers are left out", labelWidth);
			int level = options.LodLevel;
			if (EditorGui.InputInt("##LOD Level", ref level))
			{
				options.LodLevel = Math.Clamp(level, 0, 31);
			}

			Label("Include Transparent", "Bakes renderers with a transparent material too. Their parts then sort as one object: where two of them overlap on screen, they blend in the mesh's order, not by distance", labelWidth);
			bool transparent = options.IncludeTransparent;
			if (EditorGui.Checkbox("##Include Transparent", ref transparent))
			{
				options.IncludeTransparent = transparent;
				EditorPreferences.SetToggle(TransparentToggle, transparent);
			}

			Label("Originals", "What becomes of the selected objects once they are baked", labelWidth);
			if (EditorGui.BeginCombo("##Originals", OriginalsLabels[(int)options.Originals]))
			{
				for (int i = 0; i < OriginalsLabels.Length; i++)
				{
					if (EditorGui.Selectable(OriginalsLabels[i], i == (int)options.Originals))
					{
						options.Originals = (MeshBakeOriginals)i;
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(OriginalsTooltips[i]);
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.Spacing();
			ImGui.PushTextWrapPos(0f);
			if (current.Objects > 0 && current.Problem != null)
			{
				ImGui.TextColored(EditorStyle.Warning, current.Problem);
			}
			else if (current.Objects > 0)
			{
				ImGui.TextDisabled("Mesh asset: " + current.AssetPath);
			}

			ImGui.PopTextWrapPos();

			ImGui.BeginDisabled(current.Problem != null);
			bool bake = EditorGui.Button("Bake", new NVector2(ImGui.GetContentRegionAvail().X, 0f));
			ImGui.EndDisabled();
			if (bake)
			{
				// After the frame: the bake changes the scene the editor is drawing.
				var objects = new List<GameObject>(selection);
				editor.Defer(() => Bake(objects));
			}

			if (lastBake != null)
			{
				ImGui.PushTextWrapPos(0f);
				if (lastBakeFailed)
				{
					ImGui.TextColored(EditorStyle.Warning, lastBake);
				}
				else
				{
					ImGui.TextDisabled(lastBake);
				}

				ImGui.PopTextWrapPos();
			}
		}

		private void Bake(List<GameObject> objects)
		{
			MeshBakeSummary before = MeshBaking.Describe(editor, objects, options);
			GameObject baked = MeshBaking.Bake(editor, objects, options);
			lastBakeFailed = baked == null;
			lastBake = baked == null
				? "Nothing baked" + (before.Problem != null ? ": " + before.Problem : " (see the Console).")
				: MeshBaking.Outcome(before) + ". Undo brings the objects back.";
			// The name was this bake's: the next selection gets its own.
			name = "";
			summaryKey = null;
		}

		/// <summary>The summary of the selection with the options as they are: worked out when one of them changes, and a moment after the scene does.</summary>
		private MeshBakeSummary Summarize(IReadOnlyList<GameObject> selection)
		{
			var key = new StringBuilder();
			key.Append(options.Name).Append('|').Append(options.Folder).Append('|').Append(options.LodLevel).Append('|').Append(options.IncludeTransparent).Append('|')
				.Append(editor.IsPlaying).Append('|').Append(editor.Stage?.AssetPath).Append('|').Append(editor.Scene?.Path).Append('|').Append(selection.Count).Append('|');
			ulong ids = 0;
			foreach (GameObject go in selection)
			{
				ids = unchecked(ids * 31 + go.Id);
			}

			key.Append(ids);
			string text = key.ToString();
			int version = unchecked(editor.SceneVersion * 31 + editor.AssetsVersion);
			double now = ImGui.GetTime();
			bool changed = text != summaryKey;
			if (summary == null || changed || version != summaryVersion && now - summaryTime >= SummarySeconds)
			{
				summary = MeshBaking.Describe(editor, selection, options);
				summaryKey = text;
				summaryVersion = version;
				summaryTime = now;
			}

			return summary;
		}

		/// <summary>"3 objects: Rock A, Rock B, Rock C" (the first few names; the objects under a selected one are part of it).</summary>
		private string DescribeObjects(IReadOnlyList<GameObject> selection, int roots)
		{
			const int shown = 4;
			var text = new StringBuilder();
			text.Append(roots).Append(roots == 1 ? " object: " : " objects: ");
			int listed = 0;
			foreach (GameObject go in selection)
			{
				if (listed == shown)
				{
					break;
				}

				bool under = false;
				for (Transform parent = go.transform.parent; parent != null && !under; parent = parent.parent)
				{
					under = editor.IsSelected(parent.gameObject);
				}

				if (!under)
				{
					text.Append(listed > 0 ? ", " : "").Append(go.Name);
					listed++;
				}
			}

			if (roots > listed)
			{
				text.Append(" and ").Append(roots - listed).Append(" more");
			}

			return text.ToString();
		}

		/// <summary>A label on the left (tooltip on hover); the next item fills the rest of the line.</summary>
		private static void Label(string label, string tooltip, float width)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip);
			}

			ImGui.SameLine(width);
			ImGui.SetNextItemWidth(-1f);
		}

		#endregion
	}
}
