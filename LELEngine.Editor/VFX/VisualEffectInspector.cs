using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using LELEngine.VFX;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     The custom part of the VisualEffect component inspector: open the graph, playback buttons, the exposed
	///     properties of the graph with per-component overrides, and live statistics.
	/// </summary>
	internal static class VisualEffectInspector
	{
		#region PublicMethods

		public static bool Draw(VisualEffect effect, InspectorContext context)
		{
			bool changed = false;
			EditorApplication editor = context.Editor;
			VfxGraph graph = effect.visualEffectAsset;
			ImGui.Spacing();
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 3f) * 0.25f;
			ImGui.BeginDisabled(graph == null);
			if (ImGui.Button("Edit", new NVector2(width, 0)) && graph?.AssetPath != null)
			{
				editor.OpenVfxGraph(graph.AssetPath);
			}

			ImGui.SameLine();
			if (ImGui.Button("Play", new NVector2(width, 0)))
			{
				effect.Play();
				editor.NotifySceneChanged();
			}

			ImGui.SameLine();
			if (ImGui.Button("Stop", new NVector2(width, 0)))
			{
				effect.Stop();
			}

			ImGui.SameLine();
			if (ImGui.Button("Restart", new NVector2(width, 0)))
			{
				effect.Reinit();
				editor.NotifySceneChanged();
			}

			ImGui.EndDisabled();
			if (graph == null)
			{
				ImGui.TextDisabled("Assign a .vfx graph asset (Project panel: right click > Create Visual Effect Graph).");
				return false;
			}

			// Exposed properties with overrides.
			var exposed = new List<VfxProperty>();
			foreach (VfxProperty property in graph.Properties)
			{
				if (property.Exposed)
				{
					exposed.Add(property);
				}
			}

			if (exposed.Count > 0)
			{
				ImGui.SeparatorText("Properties");
				foreach (VfxProperty property in exposed)
				{
					ImGui.PushID(property.Name);
					changed |= DrawProperty(effect, property, context);
					ImGui.PopID();
				}
			}

			ImGui.Spacing();
			VfxInstance instance = effect.Instance;
			if (instance != null)
			{
				VfxProgramSet programs = instance.Programs;
				string state = programs == null ? "not compiled" : programs.HasErrors ? "compile errors (open the graph)" : "compiled";
				ImGui.TextDisabled($"{instance.AliveCount} / {instance.Capacity} particles, {instance.Time:0.0} s, {state}{(instance.Frozen ? ", frozen (not seen)" : instance.Culled ? ", culled" : "")}");
			}

			return changed;
		}

		/// <summary>Scene view gizmo: the bounds of the effect's systems.</summary>
		public static void DrawGizmos(GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (VisualEffect effect in selected.GetComponents<VisualEffect>())
			{
				Vector3 min;
				Vector3 max;
				if (effect.GetWorldBounds(out min, out max))
				{
					DebugDraw.WireBox((min + max) * 0.5f, (max - min) * 0.5f, Quaternion.Identity, new Color4(0.5f, 0.9f, 1f, 1f));
				}
				else
				{
					DebugDraw.WireBox(effect.transform.position, new Vector3(0.25f), Quaternion.Identity, new Color4(0.5f, 0.9f, 1f, 0.6f));
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static bool DrawProperty(VisualEffect effect, VfxProperty property, InspectorContext context)
		{
			VfxPropertyOverride entry = null;
			foreach (VfxPropertyOverride candidate in effect.propertyOverrides)
			{
				if (candidate != null && candidate.Name == property.Name)
				{
					entry = candidate;
					break;
				}
			}

			bool overridden = entry != null && entry.Enabled && entry.Value != null;
			bool toggle = overridden;
			if (ImGui.Checkbox("##override", ref toggle))
			{
				context.Editor.Edits.Begin(effect);
				if (toggle)
				{
					if (entry == null)
					{
						entry = new VfxPropertyOverride { Name = property.Name };
						effect.propertyOverrides.Add(entry);
					}

					entry.Enabled = true;
					entry.Value = entry.Value ?? property.Value.Clone();
				}
				else if (entry != null)
				{
					entry.Enabled = false;
				}

				effect.Instance?.SetOverrides(effect.propertyOverrides);
				context.Editor.Edits.RequestCommit();
				context.Editor.NotifySceneChanged();
				return true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Override the graph's value for this component.");
			}

			ImGui.SameLine();
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(property.Name);
			if (!string.IsNullOrEmpty(property.Tooltip) && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(property.Tooltip);
			}

			ImGui.SameLine(context.LabelWidth);
			ImGui.SetNextItemWidth(-1);
			if (!overridden)
			{
				ImGui.BeginDisabled();
				VfxValue preview = property.Value.Clone();
				VfxValueEditors.Draw("value", preview);
				ImGui.EndDisabled();
				return false;
			}

			if (entry.Value.Type != property.Value.Type)
			{
				entry.Value = VfxValueTypes.IsNumeric(entry.Value.Type) && VfxValueTypes.IsNumeric(property.Value.Type) ? entry.Value.ConvertTo(property.Value.Type) : property.Value.Clone();
			}

			bool changed = VfxValueEditors.Draw("value", entry.Value);
			VfxValueEditors.EditState state = VfxValueEditors.LastEditState;
			if (state == VfxValueEditors.EditState.Began)
			{
				context.Editor.Edits.Begin(effect);
			}

			if (changed)
			{
				context.Editor.Edits.Begin(effect);
				effect.Instance?.SetOverrides(effect.propertyOverrides);
				context.Editor.NotifySceneChanged();
			}

			if (state == VfxValueEditors.EditState.Ended)
			{
				context.Editor.Edits.RequestCommit();
			}

			return changed;
		}

		#endregion
	}
}
