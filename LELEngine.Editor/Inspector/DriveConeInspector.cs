using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below a drive cone's fields: a preset menu (sets the look of a kind of engine, undoable), Play Ignition, and the
	///     state the plume shows now (throttle, burn, length, brightness). In the scene view the selected cone's outline:
	///     rings at the nozzle, along the plume and at its end.
	/// </summary>
	internal static class DriveConeInspector
	{
		#region PrivateFields

		private static readonly string[] PresetNames = { "Afterburner", "Rocket", "Fusion Drive", "Ion", "Torch Drive" };

		private static readonly Color4 OutlineColor = new Color4(1f, 0.7f, 0.3f, 0.9f);

		#endregion

		#region PublicMethods

		public static bool Draw(DriveCone cone, InspectorContext context)
		{
			bool changed = false;
			ImGui.SeparatorText("Drive Cone");
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.SetNextItemWidth(width);
			if (EditorGui.BeginCombo("##Preset", "Apply Preset..."))
			{
				for (int i = 0; i < PresetNames.Length; i++)
				{
					if (EditorGui.Selectable(PresetNames[i]))
					{
						context.Editor.Edits.Begin(context.UndoTarget);
						cone.ApplyPreset((DriveConePreset)i);
						context.Editor.Edits.RequestCommit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Sets the look of a kind of engine (the nozzle's size stays, the length follows it).");
			}

			ImGui.SameLine();
			if (EditorGui.Button("Play Ignition", new NVector2(width, 0)))
			{
				cone.PlayIgnition();
			}

			ImGui.TextDisabled($"Now: throttle {cone.CurrentThrottle:0.00}, burn {cone.CurrentBurn:0.00}, length {cone.CurrentLength:0.##}, brightness {cone.CurrentBrightness:0.00}");
			if (cone.Material == null)
			{
				ImGui.TextColored(EditorStyle.Error, "No material: the plume is not drawn (use Engine/Materials/Default-DriveCone).");
			}

			return changed;
		}

		/// <summary>The outline of the selected object's drive cones (scene view).</summary>
		public static void DrawGizmos(GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (DriveCone cone in selected.GetComponents<DriveCone>())
			{
				Matrix4 world = cone.transform.LocalToWorld;
				float plumeLength = MathF.Max(cone.CurrentLength, 1e-3f);
				const int Rings = 4;
				const int Sides = 24;
				Vector3 previous = default;
				for (int ring = 0; ring <= Rings; ring++)
				{
					float share = ring / (float)Rings;
					float radius = cone.RadiusAt(share);
					for (int side = 0; side <= Sides; side++)
					{
						float angle = side / (float)Sides * MathF.Tau;
						Vector3 point = Vector3.TransformPosition(new Vector3(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius, share * plumeLength), world);
						if (side > 0)
						{
							DebugDraw.Line(previous, point, OutlineColor);
						}

						previous = point;
					}
				}

				// Four lines along the plume from the nozzle's rim to the end's.
				for (int side = 0; side < 4; side++)
				{
					float angle = side * MathF.PI * 0.5f;
					var direction = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
					for (int ring = 0; ring < Rings; ring++)
					{
						float a = ring / (float)Rings;
						float b = (ring + 1) / (float)Rings;
						Vector2 rimA = direction * cone.RadiusAt(a);
						Vector2 rimB = direction * cone.RadiusAt(b);
						Vector3 from = Vector3.TransformPosition(new Vector3(rimA.X, rimA.Y, a * plumeLength), world);
						Vector3 to = Vector3.TransformPosition(new Vector3(rimB.X, rimB.Y, b * plumeLength), world);
						DebugDraw.Line(from, to, OutlineColor);
					}
				}
			}
		}

		#endregion
	}
}
