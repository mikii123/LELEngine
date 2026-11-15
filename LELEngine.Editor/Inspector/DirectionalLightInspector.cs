using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below a <see cref="DirectionalLight" />'s fields: whether it shines this frame (at most
	///     <see cref="DirectionalLight.MaxLights" /> do), where it stands among the lights and whether it casts shadows (the
	///     project lets only so many lights cast them); in the scene view the selected light's direction as a sheaf of
	///     rays, while the component is expanded in the inspector (as the local lights' reach).
	/// </summary>
	internal static class DirectionalLightInspector
	{
		#region PublicMethods

		public static bool Draw(DirectionalLight light, InspectorContext context)
		{
			ImGui.Spacing();
			ImGui.PushTextWrapPos(0f);
			int index = DirectionalLightBuffer.IndexOf(light);
			int count = DirectionalLightBuffer.Shining.Count;
			string state;
			if (index >= 0)
			{
				state = (index == 0 ? "The main light" : "Shining") + " (" + (index + 1) + " of " + count + " directional lights)";
			}
			else if (light.Shines)
			{
				state = "Not shining: more than " + DirectionalLight.MaxLights + " directional lights are on, the brightest shine";
			}
			else
			{
				state = light.isActiveAndEnabled ? "Off: no strength or a black colour" : "Off";
			}

			ImGui.TextDisabled(state);
			if (index >= 0)
			{
				ShadowSettings shadows = Lighting.Shadows;
				string shadow;
				if (DirectionalLightBuffer.CastsShadows(index))
				{
					shadow = "Casts shadows";
				}
				else if (!shadows.Enabled)
				{
					shadow = "No shadows: they are off in Project Settings > Lighting > Shadows";
				}
				else if (!light.CastShadows)
				{
					shadow = "No shadows: Cast Shadows is off";
				}
				else
				{
					shadow = "No shadows: " + Math.Clamp(shadows.ShadowedLights, 0, DirectionalLight.MaxLights) + " of the lights before it cast them already (Shadowed Lights in Project Settings > Lighting > Shadows)";
				}

				ImGui.TextDisabled(shadow);
			}

			ImGui.PopTextWrapPos();
			return false;
		}

		/// <summary>Scene view gizmo: the direction of each expanded directional light on the selected object.</summary>
		public static void DrawGizmos(EditorApplication editor, GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (DirectionalLight light in selected.GetComponents<DirectionalLight>())
			{
				if (light.IsDestroyed || !light.enabled || !editor.IsExpandedInInspector(light))
				{
					continue;
				}

				// A ring around the object and rays from it along the light: where it goes, whatever the object's place.
				var color = new Color4(light.Color.R * 0.5f + 0.5f, light.Color.G * 0.5f + 0.5f, light.Color.B * 0.5f + 0.5f, 1f);
				Vector3 position = light.transform.position;
				Vector3 forward = -light.Towards;
				Vector3 right = light.transform.right;
				Vector3 up = light.transform.up;
				const float radius = 0.5f;
				const float length = 2.5f;
				const int rays = 8;
				Vector3 previous = position + right * radius;
				for (int i = 1; i <= 32; i++)
				{
					float angle = i * MathF.PI * 2f / 32f;
					Vector3 point = position + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * radius;
					DebugDraw.Line(previous, point, color);
					previous = point;
				}

				for (int i = 0; i < rays; i++)
				{
					float angle = i * MathF.PI * 2f / rays;
					Vector3 start = position + (right * MathF.Cos(angle) + up * MathF.Sin(angle)) * radius;
					DebugDraw.Line(start, start + forward * length, color);
				}

				DebugDraw.Line(position, position + forward * length * 1.4f, color);
			}
		}

		#endregion
	}
}
