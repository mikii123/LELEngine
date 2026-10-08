using System;
using System.IO;
using System.Text.Json;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using OpenTK.Mathematics;
using NMatrix4 = System.Numerics.Matrix4x4;
using NQuaternion = System.Numerics.Quaternion;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editable view of the scene, rendered by the game's own renderer (same lighting and GI as the game)
	///     from an editor camera that is not part of the scene.
	///     Right mouse: look + WASD/QE fly (Shift faster, wheel while flying changes the speed); middle mouse: pan;
	///     wheel: dolly; F: frame the selection; W / E / R: move / rotate / scale gizmo; left click: select.
	/// </summary>
	internal sealed class SceneViewPanel : IDisposable
	{
		#region PublicFields

		public bool IsFocused { get; private set; }
		public ViewRenderer ViewRenderer { get; private set; }

		/// <summary>Views render every frame, not only on changes (animated shaders, time-based effects).</summary>
		public bool AlwaysRefresh { get; private set; }

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly GameObject cameraObject;
		private readonly Camera camera;
		private Vector3 position = new Vector3(0f, 3f, -9f);
		private float yaw;
		private float pitch = 15f;
		private float flySpeed = 6f;
		private readonly ViewRefresh refresh = new ViewRefresh();
		private bool visible;
		private bool focusRequested;
		private NVector2 viewMin;
		private NVector2 viewSize = new NVector2(640, 360);
		private ImGuizmoOperation operation = ImGuizmoOperation.Translate;
		private ImGuizmoMode mode = ImGuizmoMode.Local;
		private bool gizmoWasUsing;
		private bool flying;
		private NVector2 pressPosition;
		private bool pressCanPick;

		#endregion

		#region Constructors

		public SceneViewPanel(EditorApplication editor)
		{
			this.editor = editor;

			// Detached object: no scene, never saved, invisible to the game.
			cameraObject = new GameObject("Scene Camera", null) { hideFlags = HideFlags.HideAndDontSave };
			camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 60f;
			camera.NearClip = 0.05f;
			camera.FarClip = 1000f;
			ApplyCamera();
		}

		#endregion

		#region PublicMethods

		public void EnsureRenderer()
		{
			ViewRenderer ??= new ViewRenderer((int)viewSize.X, (int)viewSize.Y);
		}

		public void InvalidateCaches()
		{
			ViewRenderer?.Renderer.InvalidateSceneCaches();
		}

		/// <summary>Brings the Scene tab to the front on the next frame.</summary>
		public void Focus()
		{
			focusRequested = true;
		}

		/// <summary>Turns the camera around the vertical axis (tests).</summary>
		public void RotateView(float yawDegrees)
		{
			yaw += yawDegrees;
			ApplyCamera();
		}

		public void Draw()
		{
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			visible = ImGui.Begin("Scene");
			ImGui.PopStyleVar();
			IsFocused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			if (visible)
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

		/// <summary>Renders when the view shows something new (<see cref="ViewRefresh" />) or every frame when continuous.</summary>
		public void Render(bool continuous)
		{
			if (!visible || ViewRenderer == null || editor.Scene == null)
			{
				return;
			}

			ViewRenderer.EnsureSize((int)viewSize.X, (int)viewSize.Y);
			ApplyCamera();
			if (refresh.ShouldRender(camera, ViewRenderer.Width, ViewRenderer.Height, editor.SceneVersion, continuous))
			{
				ViewRenderer.Render(camera, editor.Scene);
			}
		}

		/// <summary>Renders the next frame even if nothing changed (tests, frame dumps).</summary>
		public void RequestRender()
		{
			refresh.Request();
		}

		/// <summary>Moves the camera so the object fills the view.</summary>
		public void FrameObject(GameObject go)
		{
			if (go == null)
			{
				return;
			}

			Vector3 center = go.transform.position;
			float radius = 1f;
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			if (renderer?.Mesh != null)
			{
				Vector3 min = renderer.Mesh.BoundsMin;
				Vector3 max = renderer.Mesh.BoundsMax;
				center = go.transform.TransformPoint((min + max) * 0.5f);
				radius = Math.Max(0.25f, ((max - min) * go.transform.lossyScale).Length * 0.5f);
			}

			position = center - Forward() * (radius * 2.5f + 0.5f);
			ApplyCamera();
		}

		/// <summary>Where new objects appear: in front of the scene camera.</summary>
		public Vector3 DefaultSpawnPoint()
		{
			return position + Forward() * 6f;
		}

		public void LoadState(ProjectInfo project)
		{
			try
			{
				string file = StateFile(project);
				if (!File.Exists(file))
				{
					return;
				}

				using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(file)))
				{
					JsonElement root = document.RootElement;
					JsonElement p = root.GetProperty("cameraPosition");
					position = new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle());
					yaw = root.GetProperty("cameraYaw").GetSingle();
					pitch = root.GetProperty("cameraPitch").GetSingle();
					flySpeed = root.TryGetProperty("flySpeed", out JsonElement speed) ? speed.GetSingle() : flySpeed;
					AlwaysRefresh = root.TryGetProperty("alwaysRefresh", out JsonElement always) && always.ValueKind == JsonValueKind.True;
				}

				ApplyCamera();
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot read the editor state: " + e.Message);
			}
		}

		public void SaveState(ProjectInfo project)
		{
			try
			{
				Directory.CreateDirectory(project.LibraryPath);
				string json = "{\n  \"cameraPosition\": [" + F(position.X) + ", " + F(position.Y) + ", " + F(position.Z) + "],\n  \"cameraYaw\": " + F(yaw) + ",\n  \"cameraPitch\": " + F(pitch) + ",\n  \"flySpeed\": " + F(flySpeed) + ",\n  \"alwaysRefresh\": " + (AlwaysRefresh ? "true" : "false") + "\n}\n";
				File.WriteAllText(StateFile(project), json);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot write the editor state: " + e.Message);
			}
		}

		public void Dispose()
		{
			ViewRenderer?.Dispose();
			ViewRenderer = null;
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			DrawToolbar();

			NVector2 available = ImGui.GetContentRegionAvail();
			viewSize = new NVector2(Math.Max(16, available.X), Math.Max(16, available.Y));
			viewMin = ImGui.GetCursorScreenPos();
			if (ViewRenderer == null)
			{
				return;
			}

			// The texture is bottom-up: flip the V coordinate.
			ImGui.Image(EditorGui.Texture(ViewRenderer.Texture), viewSize, new NVector2(0, 1), new NVector2(1, 0));
			bool hovered = ImGui.IsItemHovered();

			// Drop meshes / materials from the project panel.
			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && ProjectPanel.DraggedAsset != null)
				{
					DropAsset(ProjectPanel.DraggedAsset);
				}

				ImGui.EndDragDropTarget();
			}

			UpdateCameraControls(hovered);
			ApplyCamera();
			DrawSelectionBounds();
			bool gizmoUsed = DrawGizmo();

			// Click (press and release without dragging) selects; presses on the gizmo do not count.
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				pressPosition = ImGui.GetMousePos();
				pressCanPick = !gizmoUsed && !ImGuizmo.IsOver();
			}

			if (hovered && pressCanPick && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				pressCanPick = false;
				if (NVector2.DistanceSquared(pressPosition, ImGui.GetMousePos()) < 16f && !gizmoUsed)
				{
					editor.Select(Pick(ImGui.GetMousePos()));
				}
			}
		}

		private void DrawToolbar()
		{
			ImGui.SetCursorPos(ImGui.GetCursorPos() + new NVector2(6, 4));
			ToolButton("Move (W)", "Move", ImGuizmoOperation.Translate);
			ImGui.SameLine();
			ToolButton("Rotate (E)", "Rotate", ImGuizmoOperation.Rotate);
			ImGui.SameLine();
			ToolButton("Scale (R)", "Scale", ImGuizmoOperation.Scale);
			ImGui.SameLine();
			if (ImGui.Button(mode == ImGuizmoMode.Local ? "Local" : "World"))
			{
				mode = mode == ImGuizmoMode.Local ? ImGuizmoMode.World : ImGuizmoMode.Local;
			}

			ImGui.SameLine();
			bool alwaysRefresh = AlwaysRefresh;
			if (ImGui.Checkbox("Always Refresh", ref alwaysRefresh))
			{
				AlwaysRefresh = alwaysRefresh;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Render the views every frame. Off: they render when the camera or the scene changes,\n" +
					"then for a few seconds while the lighting settles, and keep the last image.");
			}

			ImGui.SameLine();
			ImGui.TextDisabled($"   speed {flySpeed:0.#} m/s");
			ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 2);
		}

		private void ToolButton(string tooltip, string label, ImGuizmoOperation target)
		{
			bool active = operation == target;
			if (active)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]);
			}

			if (ImGui.Button(label))
			{
				operation = target;
			}

			if (active)
			{
				ImGui.PopStyleColor();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip);
			}
		}

		private void UpdateCameraControls(bool hovered)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			float dt = Math.Min(io.DeltaTime, 0.1f);

			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
			{
				flying = true;
				ImGui.SetWindowFocus();
			}

			if (!ImGui.IsMouseDown(ImGuiMouseButton.Right))
			{
				flying = false;
			}

			if (flying)
			{
				// Same conventions as the FPS controller: screen-right is -X.
				yaw -= io.MouseDelta.X * 0.2f;
				pitch = Math.Clamp(pitch + io.MouseDelta.Y * 0.2f, -89f, 89f);

				Vector3 forward = Forward();
				Vector3 right = Vector3.Cross(forward, Vector3.UnitY).Normalized();
				Vector3 move = Vector3.Zero;
				if (ImGui.IsKeyDown(ImGuiKey.W)) move += forward;
				if (ImGui.IsKeyDown(ImGuiKey.S)) move -= forward;
				if (ImGui.IsKeyDown(ImGuiKey.D)) move += right;
				if (ImGui.IsKeyDown(ImGuiKey.A)) move -= right;
				if (ImGui.IsKeyDown(ImGuiKey.E)) move += Vector3.UnitY;
				if (ImGui.IsKeyDown(ImGuiKey.Q)) move -= Vector3.UnitY;
				if (io.MouseWheel != 0f)
				{
					flySpeed = Math.Clamp(flySpeed * (io.MouseWheel > 0 ? 1.25f : 0.8f), 0.25f, 200f);
				}

				if (move.LengthSquared > 1e-6f)
				{
					position += move.Normalized() * flySpeed * (io.KeyShift ? 3f : 1f) * dt;
				}

				return;
			}

			if (!hovered)
			{
				if (IsFocused && !io.WantTextInput)
				{
					HandleToolKeys();
				}

				return;
			}

			if (io.MouseWheel != 0f)
			{
				position += Forward() * io.MouseWheel * Math.Max(0.5f, flySpeed * 0.25f);
			}

			if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle))
			{
				Vector3 forward = Forward();
				Vector3 right = Vector3.Cross(forward, Vector3.UnitY).Normalized();
				Vector3 up = Vector3.Cross(right, forward).Normalized();
				float scale = 0.01f * Math.Max(1f, flySpeed * 0.3f);
				position += (-right * io.MouseDelta.X + up * io.MouseDelta.Y) * scale;
			}

			if (!io.WantTextInput)
			{
				HandleToolKeys();
			}
		}

		private void HandleToolKeys()
		{
			if (ImGui.GetIO().KeyCtrl)
			{
				return;
			}

			if (ImGui.IsKeyPressed(ImGuiKey.W, false)) operation = ImGuizmoOperation.Translate;
			if (ImGui.IsKeyPressed(ImGuiKey.E, false)) operation = ImGuizmoOperation.Rotate;
			if (ImGui.IsKeyPressed(ImGuiKey.R, false)) operation = ImGuizmoOperation.Scale;
			if (ImGui.IsKeyPressed(ImGuiKey.F, false)) FrameObject(editor.Selected);
		}

		private Vector3 Forward()
		{
			return (QuaternionHelper.Euler(pitch, yaw, 0f) * Vector3.UnitZ).Normalized();
		}

		private void ApplyCamera()
		{
			cameraObject.transform.position = position;
			cameraObject.transform.rotation = QuaternionHelper.Euler(pitch, yaw, 0f);
			camera.UpdateMatrices(viewSize.X / Math.Max(1f, viewSize.Y));
		}

		private bool DrawGizmo()
		{
			GameObject selected = editor.Selected;
			if (selected == null || selected.scene != editor.Scene)
			{
				gizmoWasUsing = false;
				return false;
			}

			ImGuizmo.SetOrthographic(false);
			ImGuizmo.SetDrawlist();
			ImGuizmo.SetRect(viewMin.X, viewMin.Y, viewSize.X, viewSize.Y);

			NMatrix4 view = ToNumerics(camera.ViewMatrix);
			NMatrix4 projection = ToNumerics(camera.ProjectionMatrix);
			NMatrix4 model = ToNumerics(selected.transform.LocalToWorld);
			bool changed = ImGuizmo.Manipulate(ref view, ref projection, operation, mode, ref model);
			bool using_ = ImGuizmo.IsUsing();

			if (using_ && !gizmoWasUsing)
			{
				editor.Edits.Begin(selected.transform);
			}

			if (changed)
			{
				ApplyWorldMatrix(selected.transform, model);
				editor.NotifyChanged(selected);
			}

			if (!using_ && gizmoWasUsing)
			{
				editor.Edits.RequestCommit();
			}

			gizmoWasUsing = using_;
			return using_ || changed;
		}

		private static void ApplyWorldMatrix(Transform transform, NMatrix4 world)
		{
			NMatrix4 local = world;
			if (transform.parent != null)
			{
				NMatrix4.Invert(ToNumerics(transform.parent.LocalToWorld), out NMatrix4 parentInverse);
				local = world * parentInverse;
			}

			if (!NMatrix4.Decompose(local, out NVector3 scale, out NQuaternion rotation, out NVector3 translation))
			{
				return;
			}

			transform.localPosition = new Vector3(translation.X, translation.Y, translation.Z);
			transform.localRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W).Normalized();
			transform.localScale = new Vector3(scale.X, scale.Y, scale.Z);
		}

		// Both libraries use the row-vector convention (v * M) with the same memory layout.
		private static NMatrix4 ToNumerics(Matrix4 m)
		{
			return new NMatrix4(m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44);
		}

		private void DrawSelectionBounds()
		{
			GameObject selected = editor.Selected;
			MeshRenderer renderer = selected?.GetComponent<MeshRenderer>();
			if (renderer?.Mesh == null || selected.scene != editor.Scene)
			{
				return;
			}

			Vector3 min = renderer.Mesh.BoundsMin;
			Vector3 max = renderer.Mesh.BoundsMax;
			Matrix4 localToWorld = selected.transform.LocalToWorld;
			Matrix4 viewProjection = camera.ViewProjectionMatrix;
			var corners = new NVector2[8];
			for (int i = 0; i < 8; i++)
			{
				Vector3 local = new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z);
				Vector4 clip = new Vector4(Vector3.TransformPosition(local, localToWorld), 1f) * viewProjection;
				if (clip.W <= 0.01f)
				{
					return;
				}

				corners[i] = new NVector2(
					viewMin.X + (clip.X / clip.W * 0.5f + 0.5f) * viewSize.X,
					viewMin.Y + (0.5f - clip.Y / clip.W * 0.5f) * viewSize.Y);
			}

			ImDrawListPtr drawList = ImGui.GetWindowDrawList();
			uint color = ImGui.GetColorU32(EditorStyle.Selection);
			int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
			for (int e = 0; e < edges.Length; e += 2)
			{
				drawList.AddLine(corners[edges[e]], corners[edges[e + 1]], color, 1.5f);
			}
		}

		/// <summary>Closest renderer under the mouse (ray against the meshes' triangles).</summary>
		private GameObject Pick(NVector2 mouse)
		{
			Scene scene = editor.Scene;
			if (scene == null)
			{
				return null;
			}

			float ndcX = (mouse.X - viewMin.X) / viewSize.X * 2f - 1f;
			float ndcY = 1f - (mouse.Y - viewMin.Y) / viewSize.Y * 2f;
			Matrix4 inverse = camera.ViewProjectionMatrix.Inverted();
			Vector3 near = Unproject(new Vector4(ndcX, ndcY, -1f, 1f), inverse);
			Vector3 far = Unproject(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
			Vector3 origin = near;
			Vector3 direction = far - near;

			GameObject best = null;
			float bestT = float.MaxValue;
			foreach (MeshRenderer renderer in scene.Renderers)
			{
				Mesh mesh = renderer.Mesh;
				if (mesh == null || renderer.gameObject.hideFlags != HideFlags.None)
				{
					continue;
				}

				// Test in the object's local space; the ray parameter t is the same in both spaces.
				Matrix4 worldToLocal = renderer.transform.WorldToLocal;
				Vector3 localOrigin = Vector3.TransformPosition(origin, worldToLocal);
				Vector3 localDirection = Vector3.TransformVector(direction, worldToLocal);
				if (!RayBox(localOrigin, localDirection, mesh.BoundsMin, mesh.BoundsMax, out float boxT) || boxT > bestT)
				{
					continue;
				}

				var vertices = mesh.Verticies;
				for (int i = 0; i + 2 < vertices.Count; i += 3)
				{
					if (RayTriangle(localOrigin, localDirection, vertices[i].position, vertices[i + 1].position, vertices[i + 2].position, out float t) && t < bestT && t > 0f)
					{
						bestT = t;
						best = renderer.gameObject;
					}
				}
			}

			// Selecting a child of a selected object's subtree cycles naturally by clicking again (Unity picks the root
			// first; here the object hit is selected directly).
			return best;
		}

		private static Vector3 Unproject(Vector4 ndc, Matrix4 inverseViewProjection)
		{
			Vector4 world = ndc * inverseViewProjection;
			return world.Xyz / world.W;
		}

		private static bool RayBox(Vector3 origin, Vector3 direction, Vector3 min, Vector3 max, out float t)
		{
			float tMin = 0f;
			float tMax = float.MaxValue;
			for (int axis = 0; axis < 3; axis++)
			{
				float o = origin[axis];
				float d = direction[axis];
				if (Math.Abs(d) < 1e-9f)
				{
					if (o < min[axis] - 1e-4f || o > max[axis] + 1e-4f)
					{
						t = 0f;
						return false;
					}

					continue;
				}

				float t1 = (min[axis] - o) / d;
				float t2 = (max[axis] - o) / d;
				if (t1 > t2)
				{
					(t1, t2) = (t2, t1);
				}

				tMin = Math.Max(tMin, t1);
				tMax = Math.Min(tMax, t2);
				if (tMin > tMax)
				{
					t = 0f;
					return false;
				}
			}

			t = tMin;
			return true;
		}

		// Möller-Trumbore, both faces.
		private static bool RayTriangle(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c, out float t)
		{
			t = 0f;
			Vector3 edge1 = b - a;
			Vector3 edge2 = c - a;
			Vector3 p = Vector3.Cross(direction, edge2);
			float determinant = Vector3.Dot(edge1, p);
			if (Math.Abs(determinant) < 1e-12f)
			{
				return false;
			}

			float inverse = 1f / determinant;
			Vector3 s = origin - a;
			float u = Vector3.Dot(s, p) * inverse;
			if (u < 0f || u > 1f)
			{
				return false;
			}

			Vector3 q = Vector3.Cross(s, edge1);
			float v = Vector3.Dot(direction, q) * inverse;
			if (v < 0f || u + v > 1f)
			{
				return false;
			}

			t = Vector3.Dot(edge2, q) * inverse;
			return true;
		}

		private void DropAsset(string assetPath)
		{
			if (assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
			{
				string name = Path.GetFileNameWithoutExtension(assetPath);
				editor.CreateObject(name, go =>
				{
					MeshRenderer renderer = DefaultScene.AddMesh(go, assetPath);
					renderer.SetMesh(assetPath);
				});
			}
			else if (assetPath.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
			{
				GameObject target = Pick(ImGui.GetMousePos());
				MeshRenderer renderer = target?.GetComponent<MeshRenderer>();
				if (renderer != null)
				{
					editor.Edits.Begin(renderer);
					renderer.SetMaterial(assetPath);
					editor.Edits.RequestCommit();
					editor.Select(target);
					editor.NotifyChanged(target);
				}
			}
			else if (assetPath.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
			{
				editor.OpenScene(assetPath);
			}
		}

		private static string StateFile(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, "EditorState.json");
		}

		private static string F(float value)
		{
			return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		#endregion
	}
}
