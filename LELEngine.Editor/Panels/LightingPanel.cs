using System;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>
	///     The active scene's lighting: environment, shadow reach, the GI volume and strengths (those the selected GI
	///     method reads). How shadows and GI are computed is project-wide (<see cref="ProjectSettingsWindow" />); the GI
	///     method can be chosen here too.
	/// </summary>
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

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			if (ImGui.Begin("Lighting", ref Open))
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

			bool changed = false;
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

				// The method is project-wide: choosing it here is a project settings edit (undo, saved with them).
				context.UndoTarget = ProjectSettings.Current;
				if (ProjectSettingsWindow.DrawGIMethod(Lighting.GI, context))
				{
					editor.NotifySceneChanged();
				}

				context.UndoTarget = scene.Settings;
				ImGui.TextDisabled("Its quality settings: Edit > Project Settings > Lighting.");
				ImGui.Spacing();

				GIMode mode = Lighting.GI.Mode;
				if (FieldDrawer.DrawFields(scene.Settings.GI, context, filter: field => ProjectSettingsWindow.IsUsedBy(field, mode)))
				{
					Lighting.GI.InvalidateStatic();
					changed = true;
				}

				ImGui.PopID();
			}

			if (changed)
			{
				editor.MarkDirty();
			}
		}

		#endregion
	}
}
