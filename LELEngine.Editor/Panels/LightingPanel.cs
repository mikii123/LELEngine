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

			if (ImGui.CollapsingHeader("Physics 2D"))
			{
				ImGui.PushID("physics2d");
				changed |= FieldDrawer.DrawFields(scene.Settings.Physics2D, context);
				changed |= DrawLayerCollisionMatrix(scene.Settings.Physics2D);
				ImGui.PopID();
			}

			if (changed)
			{
				editor.MarkDirty();
			}
		}

		private static readonly string[] GIModeNames = { "Standard (no GI)", "Voxel (cone tracing)", "Lumen (screen probes)" };

		/// <summary>Which layers collide with which (the used layers: named ones and those of the scene's objects).</summary>
		private bool DrawLayerCollisionMatrix(Physics2DSettings physics)
		{
			if (!ImGui.TreeNodeEx("Layer Collision Matrix", ImGuiTreeNodeFlags.SpanAvailWidth))
			{
				return false;
			}

			var layers = new System.Collections.Generic.List<int>();
			for (int i = 0; i < 32; i++)
			{
				if (LayerMask.LayerToName(i).Length > 0)
				{
					layers.Add(i);
				}
			}

			Scene scene = editor.Scene;
			if (scene != null)
			{
				foreach (GameObject go in scene.GameObjects)
				{
					if (!layers.Contains(go.layer))
					{
						layers.Add(go.layer);
					}
				}
			}

			layers.Sort();
			bool changed = false;
			if (ImGui.BeginTable("##matrix", layers.Count + 1, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInner))
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach (int column in layers)
				{
					ImGui.TableNextColumn();
					ImGui.TextDisabled(LayerMask.DisplayName(column));
				}

				foreach (int row in layers)
				{
					ImGui.TableNextRow();
					ImGui.TableSetColumnIndex(0);
					ImGui.TextUnformatted(LayerMask.DisplayName(row));
					foreach (int column in layers)
					{
						ImGui.TableNextColumn();
						if (column < row)
						{
							continue;
						}

						bool collide = !physics.GetIgnoreLayerCollision(row, column);
						ImGui.PushID(row * 32 + column);
						if (ImGui.Checkbox("##c", ref collide))
						{
							editor.Edits.Begin(context.UndoTarget);
							physics.IgnoreLayerCollision(row, column, !collide);
							editor.Edits.RequestCommit();
							changed = true;
						}

						ImGui.PopID();
					}
				}

				ImGui.EndTable();
			}

			ImGui.TreePop();
			return changed;
		}

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
