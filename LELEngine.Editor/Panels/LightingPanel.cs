using System;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>The active scene's settings: environment, shadows, global illumination.</summary>
	internal sealed class LightingPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;

		#endregion

		#region Constructors

		public LightingPanel(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			if (ImGui.Begin("Lighting"))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					context.Release();
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				ImGui.TextDisabled("No scene");
				return;
			}

			context.Scene = scene;
			context.UndoTarget = scene.Settings;
			context.LabelWidth = Math.Max(150f, ImGui.GetContentRegionAvail().X * 0.5f);

			bool changed = DrawGIMode(scene.Settings.GI);
			if (ImGui.CollapsingHeader("Environment", ImGuiTreeNodeFlags.DefaultOpen))
			{
				ImGui.PushID("environment");
				changed |= FieldDrawer.DrawFields(scene.Settings.Environment, context);
				ImGui.PopID();
			}

			if (ImGui.CollapsingHeader("Shadows"))
			{
				ImGui.PushID("shadows");
				changed |= FieldDrawer.DrawFields(scene.Settings.Shadows, context);
				ImGui.PopID();
			}

			if (ImGui.CollapsingHeader("Global Illumination"))
			{
				ImGui.PushID("gi");
				changed |= FieldDrawer.DrawFields(scene.Settings.GI, context);
				ImGui.PopID();
			}

			if (changed)
			{
				editor.MarkDirty();
			}
		}

		private static readonly string[] GIModeNames = { "Standard (no GI)", "Voxel (cone tracing)", "Lumen (screen probes)" };

		/// <summary>Standard lighting, voxel cone tracing or Lumen for the scene; undoable like any setting.</summary>
		private bool DrawGIMode(GlobalIlluminationSettings gi)
		{
			int current = !gi.Enabled ? 0 : gi.Mode == GIMode.VoxelConeTracing ? 1 : 2;
			int selected = current;
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted("Global Illumination");
			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
			bool changed = ImGui.Combo("##gimode", ref selected, GIModeNames, GIModeNames.Length) && selected != current;
			context.Track(changed);
			if (changed)
			{
				gi.Enabled = selected != 0;
				if (selected != 0)
				{
					gi.Mode = selected == 1 ? GIMode.VoxelConeTracing : GIMode.Lumen;
				}

				gi.InvalidateStatic();
			}

			ImGui.Spacing();
			return changed;
		}

		#endregion
	}
}
