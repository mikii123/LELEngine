using System.Collections.Generic;
using Hexa.NET.ImGui;

namespace LELEngine.Editor
{
	/// <summary>
	///     The camera stack below a camera's fields, as URP shows it. A base camera: its overlay cameras (add, reorder,
	///     remove) and the camera of the stack that lights the world. An overlay camera: whether it clears the depth and
	///     which base cameras draw it (it renders only in a stack).
	/// </summary>
	internal static class CameraInspector
	{
		#region PrivateFields

		private const string LightingHelp = "Its view gets the screen-space GI; the shadow cascades and the GI volume follow it, and its layers\n" +
			"cast the shadows and make up the GI. The other cameras of the stack use the same shadows and light\n" +
			"their objects from the GI volume.";

		#endregion

		#region PublicMethods

		public static bool Draw(Camera camera, InspectorContext context)
		{
			return camera.RenderType == CameraRenderType.Base ? DrawBase(camera, context) : DrawOverlay(camera, context);
		}

		#endregion

		#region PrivateMethods

		private static bool DrawOverlay(Camera camera, InspectorContext context)
		{
			FieldDrawer.Label("Clear Depth", null, context);
			bool clear = camera.ClearDepth;
			bool changed = EditorGui.Checkbox("##cleardepth", ref clear);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Clear the depth before drawing: this camera's objects draw over everything the cameras before it drew.");
			}

			context.Track(changed);
			if (changed)
			{
				camera.ClearDepth = clear;
			}

			string stackedBy = null;
			foreach (Camera other in SceneCameras(context))
			{
				if (other != camera && other.RenderType == CameraRenderType.Base && other.Stack != null && other.Stack.Contains(camera))
				{
					stackedBy = stackedBy == null ? other.gameObject.Name : stackedBy + ", " + other.gameObject.Name;
				}
			}

			if (stackedBy == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "In no base camera's stack: it does not render.");
				ImGui.TextDisabled("Add it to the Stack of a base camera.");
			}
			else
			{
				ImGui.TextDisabled("Drawn in the stack of " + stackedBy + ".");
			}

			return changed;
		}

		private static bool DrawBase(Camera camera, InspectorContext context)
		{
			bool changed = false;
			ImGui.SeparatorText("Stack");
			List<Camera> stack = camera.Stack;
			int count = stack?.Count ?? 0;
			if (count == 0)
			{
				ImGui.TextDisabled("No overlay cameras: this camera renders alone.");
			}

			int remove = -1;
			int swap = -1;
			for (int i = 0; i < count; i++)
			{
				Camera overlay = stack[i];
				ImGui.PushID(i);
				ImGui.BeginDisabled(i == 0);
				if (EditorGui.ArrowButton("##up", ImGuiDir.Up))
				{
					swap = i - 1;
				}

				ImGui.EndDisabled();
				ImGui.SameLine();
				ImGui.BeginDisabled(i == count - 1);
				if (EditorGui.ArrowButton("##down", ImGuiDir.Down))
				{
					swap = i;
				}

				ImGui.EndDisabled();
				ImGui.SameLine();
				if (EditorGui.Button("X"))
				{
					remove = i;
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Remove from the stack");
				}

				ImGui.SameLine();
				ImGui.AlignTextToFramePadding();
				if (overlay == null || overlay.dead)
				{
					ImGui.TextColored(EditorStyle.Warning, "(missing camera)");
				}
				else if (!Camera.RendersInStack(overlay))
				{
					string reason = overlay.RenderType == CameraRenderType.Base ? "a base camera, skipped" : "disabled";
					ImGui.TextColored(EditorStyle.Warning, overlay.gameObject.Name + " (" + reason + ")");
				}
				else
				{
					ImGui.TextUnformatted((i + 1) + ". " + overlay.gameObject.Name + (overlay == camera.GetStackLightingCamera() ? "   (lights the world)" : ""));
				}

				ImGui.PopID();
			}

			// The overlay cameras of the scene not in the stack yet.
			Camera added = null;
			ImGui.SetNextItemWidth(-1);
			if (EditorGui.BeginCombo("##addoverlay", "Add Overlay Camera..."))
			{
				bool any = false;
				foreach (Camera candidate in SceneCameras(context))
				{
					if (candidate == camera || candidate.RenderType != CameraRenderType.Overlay || (stack != null && stack.Contains(candidate)))
					{
						continue;
					}

					any = true;
					if (EditorGui.Selectable(candidate.gameObject.Name + "##" + candidate.Id))
					{
						added = candidate;
					}
				}

				if (!any)
				{
					ImGui.TextDisabled("No overlay camera to add (set a camera's Render Type to Overlay).");
				}

				EditorGui.EndCombo();
			}

			if (remove >= 0 || swap >= 0 || added != null)
			{
				context.Editor.Edits.Begin(context.UndoTarget);
				camera.Stack ??= new List<Camera>();
				if (remove >= 0)
				{
					if (camera.LightingCamera == camera.Stack[remove])
					{
						camera.LightingCamera = null;
					}

					camera.Stack.RemoveAt(remove);
				}
				else if (swap >= 0)
				{
					(camera.Stack[swap], camera.Stack[swap + 1]) = (camera.Stack[swap + 1], camera.Stack[swap]);
				}
				else
				{
					camera.Stack.Add(added);
				}

				context.Editor.Edits.RequestCommit();
				changed = true;
			}

			changed |= DrawLightingCamera(camera, context);
			return changed;
		}

		/// <summary>Which camera of the stack lights the world: this one, or one of its rendering overlays.</summary>
		private static bool DrawLightingCamera(Camera camera, InspectorContext context)
		{
			ImGui.Spacing();
			FieldDrawer.Label("Lighting Camera", null, context);
			Camera lighting = camera.GetStackLightingCamera();
			string thisCamera = camera.gameObject.Name + " (this camera)";
			bool changed = false;
			Camera chosen = camera.LightingCamera;
			if (EditorGui.BeginCombo("##lighting", lighting == camera ? thisCamera : lighting.gameObject.Name))
			{
				if (EditorGui.Selectable(thisCamera, lighting == camera))
				{
					chosen = null;
					changed = true;
				}

				foreach (Camera overlay in camera.Stack ?? new List<Camera>())
				{
					if (Camera.RendersInStack(overlay) && EditorGui.Selectable(overlay.gameObject.Name + "##" + overlay.Id, lighting == overlay))
					{
						chosen = overlay;
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(LightingHelp);
			}

			if (changed && chosen != camera.LightingCamera)
			{
				context.Editor.Edits.Begin(context.UndoTarget);
				camera.LightingCamera = chosen;
				context.Editor.Edits.RequestCommit();
			}
			else
			{
				changed = false;
			}

			if (camera.LightingCamera != null && lighting == camera)
			{
				ImGui.TextColored(EditorStyle.Warning, camera.LightingCamera.gameObject?.Name + " does not render in the stack: this camera lights the world.");
			}

			return changed;
		}

		/// <summary>The cameras of the inspected scene.</summary>
		private static IEnumerable<Camera> SceneCameras(InspectorContext context)
		{
			if (context.Scene == null)
			{
				yield break;
			}

			foreach (GameObject go in context.Scene.GameObjects)
			{
				Camera camera = go.GetComponent<Camera>();
				if (camera != null && !camera.dead)
				{
					yield return camera;
				}
			}
		}

		#endregion
	}
}
