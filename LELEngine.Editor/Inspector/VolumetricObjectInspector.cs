using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using LELEngine.Volumetrics;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below a volumetric object's fields (both presets): its voxels, memory and state, what its preset does, and
	///     Rebuild. In the scene view the selected object's shape as a wire outline (box, cylinder or ellipsoid, as its
	///     transform places it), while the component is expanded in the inspector (as the physics gizmos).
	/// </summary>
	internal static class VolumetricObjectInspector
	{
		#region PrivateFields

		private const int Segments = 48;

		private static readonly Color4 GizmoColor = new Color4(0.72f, 0.86f, 1f, 1f);

		#endregion

		#region PublicMethods

		public static bool Draw(VolumetricObjectBase volume, InspectorContext context)
		{
			ImGui.Spacing();
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled(volume.Describe());
			ImGui.TextDisabled(volume is VolumetricObjectHQ
				? "High quality: full resolution with a temporal filter, steps that shrink near the camera, the scene's shadows; looks right from inside too."
				: "Seen from outside: marched at the chosen resolution, upsampled against the scene's depth.");
			ImGui.PopTextWrapPos();
			if (EditorGui.Button("Rebuild", new NVector2(ImGui.GetContentRegionAvail().X, 0)))
			{
				volume.Rebuild();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Makes the cloud again now (it is made again by itself after its settings change).");
			}

			return false;
		}

		/// <summary>Scene view gizmo: the shape of each expanded volumetric object on the selected object.</summary>
		public static void DrawGizmos(EditorApplication editor, GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (VolumetricObjectBase volume in selected.GetComponents<VolumetricObjectBase>())
			{
				if (volume.IsDestroyed || !volume.enabled || !editor.IsExpandedInInspector(volume))
				{
					continue;
				}

				Matrix4 toWorld = volume.transform.LocalToWorld;
				Vector3 half = volume.Size * 0.5f;
				switch (volume.Shape)
				{
					case VolumeShape.Box:
						DrawBox(toWorld, half);
						break;
					case VolumeShape.Cylinder:
						// The axis is local y: the two caps and four lines down the side.
						DrawEllipse(toWorld, new Vector3(0f, half.Y, 0f), new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, half.Z));
						DrawEllipse(toWorld, new Vector3(0f, -half.Y, 0f), new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, half.Z));
						foreach (Vector3 side in new[] { new Vector3(half.X, 0f, 0f), new Vector3(-half.X, 0f, 0f), new Vector3(0f, 0f, half.Z), new Vector3(0f, 0f, -half.Z) })
						{
							Line(toWorld, side + new Vector3(0f, half.Y, 0f), side - new Vector3(0f, half.Y, 0f));
						}

						break;
					default:
						// An ellipsoid: its three main sections.
						DrawEllipse(toWorld, Vector3.Zero, new Vector3(half.X, 0f, 0f), new Vector3(0f, half.Y, 0f));
						DrawEllipse(toWorld, Vector3.Zero, new Vector3(0f, half.Y, 0f), new Vector3(0f, 0f, half.Z));
						DrawEllipse(toWorld, Vector3.Zero, new Vector3(half.X, 0f, 0f), new Vector3(0f, 0f, half.Z));
						break;
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void DrawBox(Matrix4 toWorld, Vector3 half)
		{
			for (int axis = 0; axis < 3; axis++)
			{
				// The four edges along this axis.
				for (int corner = 0; corner < 4; corner++)
				{
					Vector3 a = Vector3.Zero;
					int u = (axis + 1) % 3;
					int v = (axis + 2) % 3;
					a[u] = (corner & 1) != 0 ? half[u] : -half[u];
					a[v] = (corner & 2) != 0 ? half[v] : -half[v];
					Vector3 b = a;
					a[axis] = -half[axis];
					b[axis] = half[axis];
					Line(toWorld, a, b);
				}
			}
		}

		private static void DrawEllipse(Matrix4 toWorld, Vector3 center, Vector3 axisA, Vector3 axisB)
		{
			Vector3 previous = center + axisA;
			for (int i = 1; i <= Segments; i++)
			{
				float angle = i * MathF.PI * 2f / Segments;
				Vector3 point = center + axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle);
				Line(toWorld, previous, point);
				previous = point;
			}
		}

		private static void Line(Matrix4 toWorld, Vector3 a, Vector3 b)
		{
			DebugDraw.Line(Vector3.TransformPosition(a, toWorld), Vector3.TransformPosition(b, toWorld), GizmoColor);
		}

		#endregion
	}
}
