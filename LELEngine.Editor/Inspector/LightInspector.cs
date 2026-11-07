using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below a <see cref="Light" />'s fields: whether it shines this frame and how bright it is across its range; in the
	///     scene view the selected light's reach as a wire outline (a point light's range sphere, a spot's cone), while the
	///     component is expanded in the inspector (as the physics gizmos).
	/// </summary>
	internal static class LightInspector
	{
		#region PrivateFields

		private const int Segments = 48;

		#endregion

		#region PublicMethods

		public static bool Draw(Light light, InspectorContext context)
		{
			ImGui.Spacing();
			ImGui.PushTextWrapPos(0f);
			int index = -1;
			for (int i = 0; i < LightBuffer.Shining.Count; i++)
			{
				if (LightBuffer.Shining[i] == light)
				{
					index = i;
				}
			}

			string state = index >= 0 ? "Shining (" + (index + 1) + " of " + LightBuffer.Shining.Count + " lights)" : light.isActiveAndEnabled && light.Intensity > 0f ? "Not shining: more than " + Light.MaxLights + " lights reach nearer the camera" : "Off";
			ImGui.TextDisabled(state);
			float range = MathF.Max(light.Range, 1e-3f);
			Vector3 radiance = light.Radiance;
			float peak = MathF.Max(radiance.X, MathF.Max(radiance.Y, radiance.Z));
			ImGui.TextDisabled($"A white surface facing it reflects {peak * light.Falloff(range * 0.25f):0.###} at a quarter of its range, {peak * light.Falloff(range * 0.5f):0.###} at half of it.");
			ImGui.PopTextWrapPos();
			return false;
		}

		/// <summary>Scene view gizmo: the reach of each expanded light on the selected object.</summary>
		public static void DrawGizmos(EditorApplication editor, GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (Light light in selected.GetComponents<Light>())
			{
				if (light.IsDestroyed || !light.enabled || !editor.IsExpandedInInspector(light))
				{
					continue;
				}

				var color = new Color4(light.Color.R * 0.5f + 0.5f, light.Color.G * 0.5f + 0.5f, light.Color.B * 0.5f + 0.5f, 1f);
				Vector3 position = light.transform.position;
				float range = MathF.Max(light.Range, 1e-3f);
				if (light.Type == LightType.Point)
				{
					Circle(position, Vector3.UnitX * range, Vector3.UnitY * range, color);
					Circle(position, Vector3.UnitY * range, Vector3.UnitZ * range, color);
					Circle(position, Vector3.UnitX * range, Vector3.UnitZ * range, color);
					continue;
				}

				// A spot: the cone to the end of its range, its rim and four lines along it.
				Vector3 forward = light.transform.forward;
				Vector3 right = light.transform.right;
				Vector3 up = light.transform.up;
				float half = MathHelper.DegreesToRadians(Math.Clamp(light.SpotAngle, 1f, 179f) * 0.5f);
				Vector3 center = position + forward * (range * MathF.Cos(half));
				float radius = range * MathF.Sin(half);
				Circle(center, right * radius, up * radius, color);
				foreach (Vector3 side in new[] { right, -right, up, -up })
				{
					DebugDraw.Line(position, center + side * radius, color);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void Circle(Vector3 center, Vector3 axisA, Vector3 axisB, Color4 color)
		{
			Vector3 previous = center + axisA;
			for (int i = 1; i <= Segments; i++)
			{
				float angle = i * MathF.PI * 2f / Segments;
				Vector3 point = center + axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle);
				DebugDraw.Line(previous, point, color);
				previous = point;
			}
		}

		#endregion
	}
}
