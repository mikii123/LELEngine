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
	///     Right mouse: look + WASD/QE fly (Shift faster, wheel while flying changes the speed); middle mouse: pan (the
	///     scene follows the mouse at the depth of what is under or near it); both drags last until released, over the view
	///     or not; wheel: dolly a fifth of the way to what the mouse points at (a mesh, else the depth of what is drawn near
	///     the cursor or in the view: <see cref="NavigationDepth" />), as fast far away as close up; F: frame the selection;
	///     W / E / R: move / rotate / scale gizmo; left click: select; handles of the selection's 2D colliders
	///     (<see cref="Physics2DGizmos" />).
	/// </summary>
	internal sealed class SceneViewPanel : IDisposable, IEditorPanel
	{
		#region PublicFields

		public bool IsFocused { get; private set; }
		public ViewRenderer ViewRenderer { get; private set; }

		/// <summary>The scene camera's position and view direction (the audio listener in edit mode).</summary>
		public Vector3 CameraPosition => position;

		public Vector3 CameraForward => Forward();

		/// <summary>Depth the view looks at (pans move the scene at it).</summary>
		public float FocusDistance => focusDistance;

		/// <summary>The view's image in the main window's client coordinates (tests).</summary>
		public (NVector2 Min, NVector2 Size) ViewRect => (viewMin, viewSize);

		/// <summary>The camera drag's state and the last frame's motion (tests: logcamera=file).</summary>
		public string DescribeDrag()
		{
			string typical = TryViewDepth(out float median) ? median.ToString("0.##") : "-";
			return $"drag {cameraDrag} delta ({lastDragDelta.X:0.#}, {lastDragDelta.Y:0.#}) camera ({position.X:0.###}, {position.Y:0.###}, {position.Z:0.###}) yaw {yaw:0.##} pitch {pitch:0.##} focus {focusDistance:0.##} drawn {typical} source {navigationSource} at ({navigationAt.X:0.###}, {navigationAt.Y:0.###})";
		}

		// The last frame's drag motion (tests).
		private NVector2 lastDragDelta;

		// Where the last navigation depth came from and the point of the view it was asked for (tests).
		private string navigationSource = "-";
		private Vector2 navigationAt;

		/// <summary>Views render every frame, not only on changes (animated shaders, time-based effects).</summary>
		public bool AlwaysRefresh { get; private set; }

		/// <summary>Draw the navmesh, agents and obstacles in the view.</summary>
		public bool ShowNavigation { get; private set; } = true;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly GameObject cameraObject;
		private readonly Camera camera;
		private readonly Physics2DGizmos physicsGizmos;
		private Vector3 position = new Vector3(0f, 3f, -9f);
		private float yaw;
		private float pitch = 15f;
		private float flySpeed = 6f;

		/// <summary>
		///     The frame time flying moves by, averaged over about ten frames (Unity's smoothDeltaTime). An editor bound by the
		///     GPU (a volume filling the view) measures frame times that alternate, 11 / 23 ms, while the display shows its
		///     frames at an even rate: moving by the raw time made every flight sideways judder, the gizmo with it
		///     (2026-11-07). Turning follows the mouse per frame and never had it.
		/// </summary>
		private float smoothDeltaTime = 1f / 60f;

		private const float DeltaTimeSmoothing = 0.1f;

		/// <summary>Depth of what the view looks at (last mouse pick or framed object): pans move the scene at it.</summary>
		private float focusDistance = 10f;

		private const float WheelShare = 0.2f;
		private const float MinFocusDistance = 0.05f;
		private readonly ViewRefresh refresh = new ViewRefresh();
		private bool visible;
		private bool focusRequested;
		private NVector2 viewMin;
		private NVector2 viewSize = new NVector2(640, 360);
		private string savedState;
		private ImGuizmoOperation operation = ImGuizmoOperation.Translate;
		private ImGuizmoMode mode = ImGuizmoMode.Local;
		private bool gizmoWasUsing;
		private NVector2 pressPosition;

		// Camera drags: look (right button, with WASD flight) and pan (middle button). One starts on a press over the view
		// and lasts while its button is held, over the view or not, moving by the cursor's motion as the UI sees it. Before
		// 2026-11-08 a pan stopped when the cursor left the view and started only past ImGui's 6 px drag threshold, while
		// looking did neither: the user found panning and turning inconsistent. The cursor is never captured (hidden with
		// relative motion): GLFW's warps came in as jumps, and remote desktops and tablets send absolute positions, which a
		// captured cursor turns into motion towards one side only (see ImGuiController.CapturedMouseDelta's history in
		// the engine's CLAUDE.md, "Camera drags").
		private enum CameraDrag
		{
			None,
			Look,
			Pan
		}

		private CameraDrag cameraDrag;

		private bool flying => cameraDrag == CameraDrag.Look;

		private const float LookDegreesPerPixel = 0.2f;

		private bool pressCanPick;

		// The view's render as depths on the CPU: the depth pans and zooms work at where the cursor is over no mesh.
		private readonly SceneDepthProbe depthProbe = new SceneDepthProbe();

		#endregion

		#region Constructors

		public SceneViewPanel(EditorApplication editor)
		{
			this.editor = editor;
			physicsGizmos = new Physics2DGizmos(editor);

			// Detached object: no scene, never saved, invisible to the game.
			cameraObject = new GameObject("Scene Camera", null) { hideFlags = HideFlags.HideAndDontSave };
			camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 60f;
			camera.NearClip = 0.05f;
			// Far enough for backdrops kilometres away (a storm thousands of units across); the depth's precision limits it.
			camera.FarClip = 100000f;
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
			Open = true;
			focusRequested = true;
		}

		/// <summary>Turns the camera around the vertical axis (tests).</summary>
		public void RotateView(float yawDegrees)
		{
			yaw += yawDegrees;
			ApplyCamera();
		}

		/// <summary>
		///     Moves the selected object by a world offset the way a drag of its transform gizmo does (the world matrix
		///     applied, the change notified; the caller opens and commits the edit). Tests of dragging.
		/// </summary>
		internal void MoveSelectedAsGizmo(Vector3 offset)
		{
			GameObject selected = SelectedInScene();
			if (selected == null)
			{
				return;
			}

			ApplyWorldMatrix(selected.transform, ToNumerics(selected.transform.LocalToWorld * Matrix4.CreateTranslation(offset)));
			editor.NotifyChanged(selected);
		}

		/// <summary>Moves the camera along its own axes (x screen right, y up, z forward; tests of camera motion).</summary>
		public void MoveView(Vector3 local)
		{
			Vector3 forward = Forward();
			Vector3 right = Vector3.Cross(forward, Vector3.UnitY).Normalized();
			Vector3 up = Vector3.Cross(right, forward).Normalized();
			position += right * local.X + up * local.Y + forward * local.Z;
			ApplyCamera();
		}

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		string IEditorPanel.PanelName => "Scene";
		string IEditorPanel.ImGuiName => "Scene";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1280, 760);
		void IEditorPanel.Focus() => Focus();

		public void Draw()
		{
			if (Automation.WindowCapture.Active)
			{
				DrawCapture();
				return;
			}

			ReleaseCameraDragIfAbandoned();
			if (!Open)
			{
				// Closed: nothing to render either (see Render).
				visible = false;
				IsFocused = false;
				return;
			}

			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			visible = ImGui.Begin("Scene", ref Open);
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

		/// <summary>
		///     The window in a capture (<see cref="Automation.WindowCapture" />): the toolbar and the view at the capture's
		///     size, rendered for it, with the physics gizmos. No camera controls, transform gizmo, picking or selection
		///     outline, and nothing the editor's own frames keep (view size, focus, visibility) changes.
		/// </summary>
		private void DrawCapture()
		{
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			bool shown = ImGui.Begin("Scene", ref Open);
			ImGui.PopStyleVar();
			if (shown)
			{
				DrawToolbar();
				NVector2 available = ImGui.GetContentRegionAvail();
				var size = new NVector2(Math.Max(16, available.X), Math.Max(16, available.Y));
				NVector2 min = ImGui.GetCursorScreenPos();
				TestAnchors.Record("sceneview", min, min + size);
				if (editor.Scene != null)
				{
					int texture = Automation.WindowCapture.ViewTexture(this, ViewRenderer, visible, (int)size.X, (int)size.Y, RenderCapture);
					ImGui.Image(EditorGui.Texture(texture), size, new NVector2(0, 1), new NVector2(1, 0));
					camera.UpdateMatrices(size.X / size.Y);
					physicsGizmos.SetView(camera.ViewProjectionMatrix, min, size);
					physicsGizmos.Draw(SelectedInScene());
				}
			}

			ImGui.End();
		}

		/// <summary>One frame of the view into a capture's renderer (the editor camera, gizmos, debug primitives).</summary>
		private void RenderCapture(ViewRenderer target)
		{
			(int, int) mark = Rendering.DebugDraw.Mark;
			ApplyCamera();
			LELEngine.VFX.VfxRuntime.FallbackCamera = camera;
			target.Renderer.Settings.DebugPrimitives = true;
			target.Renderer.Settings.RenderUI = false;
			DrawGizmos();
			target.Render(camera, editor.Scene);
			// The gizmos queued for this image only: the editor's own frame queues its own.
			Rendering.DebugDraw.Truncate(mark);
		}

		/// <summary>Renders when the view shows something new (<see cref="ViewRefresh" />) or every frame when continuous.</summary>
		public void Render(bool continuous)
		{
			if (!visible || ViewRenderer == null || editor.Scene == null)
			{
				return;
			}

			ViewRenderer.EnsureSize((int)viewSize.X, (int)viewSize.Y);
			depthProbe.Update();
			ApplyCamera();
			// Effects orient and sort for the scene camera when the scene has no camera of its own.
			LELEngine.VFX.VfxRuntime.FallbackCamera = camera;
			if (refresh.ShouldRender(camera, ViewRenderer.Width, ViewRenderer.Height, editor.SceneVersion, continuous))
			{
				ViewRenderer.Renderer.Settings.DebugPrimitives = true;
				// Game UI belongs to the game view only.
				ViewRenderer.Renderer.Settings.RenderUI = false;
				DrawGizmos();
				ViewRenderer.Render(camera, editor.Scene);
				depthProbe.Capture(ViewRenderer.Renderer, camera);
			}
		}

		/// <summary>World-space gizmos drawn into the view through <see cref="Rendering.DebugDraw" />.</summary>
		private void DrawGizmos()
		{
			GameObject selected = SelectedInScene();
			if (selected != null)
			{
				VFX.VisualEffectInspector.DrawGizmos(selected);
				VolumetricObjectInspector.DrawGizmos(editor, selected);
				LightInspector.DrawGizmos(editor, selected);
				editor.Extensions.DrawSceneGUI(selected);
			}

			if (!ShowNavigation)
			{
				return;
			}

			Navigation.NavMeshDebug.DrawAll();
			if (selected != null)
			{
				Navigation.NavMeshDebug.DrawSurfaceBounds(selected.GetComponent<Navigation.NavMeshSurface>());
			}
		}

		/// <summary>The selected object when this view shows its scene.</summary>
		private GameObject SelectedInScene()
		{
			GameObject selected = editor.Selected;
			return selected != null && selected.scene == editor.Scene ? selected : null;
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

			focusDistance = radius * 2.5f + 0.5f;
			position = center - Forward() * focusDistance;
			ApplyCamera();
		}

		/// <summary>Where new objects appear: in front of the scene camera.</summary>
		public Vector3 DefaultSpawnPoint()
		{
			return position + Forward() * 6f;
		}

		public void LoadState(ProjectInfo project)
		{
			savedState = null;
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
					focusDistance = root.TryGetProperty("focusDistance", out JsonElement focus) ? Math.Max(MinFocusDistance, focus.GetSingle()) : focusDistance;
					AlwaysRefresh = root.TryGetProperty("alwaysRefresh", out JsonElement always) && always.ValueKind == JsonValueKind.True;
					ShowNavigation = !root.TryGetProperty("showNavigation", out JsonElement navigation) || navigation.ValueKind != JsonValueKind.False;
					if (root.TryGetProperty("gizmoOperation", out JsonElement tool) && Enum.TryParse(tool.GetString(), out ImGuizmoOperation savedOperation))
					{
						operation = savedOperation;
					}

					if (root.TryGetProperty("gizmoMode", out JsonElement space) && Enum.TryParse(space.GetString(), out ImGuizmoMode savedMode))
					{
						mode = savedMode;
					}
				}

				ApplyCamera();
				savedState = StateText();
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
				string json = StateText();
				File.WriteAllText(StateFile(project), json);
				savedState = json;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot write the editor state: " + e.Message);
			}
		}

		/// <summary>Writes the state when it differs from what the file holds.</summary>
		public void SaveStateIfChanged(ProjectInfo project)
		{
			if (StateText() != savedState)
			{
				SaveState(project);
			}
		}

		public void Dispose()
		{
			depthProbe.Dispose();
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
			if (TestAnchors.Enabled)
			{
				TestAnchors.Record("sceneview", viewMin, viewMin + viewSize);
			}
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
			GameObject selected = SelectedInScene();
			physicsGizmos.SetView(camera.ViewProjectionMatrix, viewMin, viewSize);
			physicsGizmos.Update(selected, hovered && !flying && !gizmoWasUsing && !ImGui.IsMouseDown(ImGuiMouseButton.Middle));
			bool gizmoUsed = DrawGizmo();
			physicsGizmos.Draw(selected);

			// Click (press and release without dragging) selects; presses on the gizmos do not count.
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				pressPosition = ImGui.GetMousePos();
				pressCanPick = !gizmoUsed && !ImGuizmo.IsOver() && !physicsGizmos.IsHot;
			}

			if (hovered && pressCanPick && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				pressCanPick = false;
				if (NVector2.DistanceSquared(pressPosition, ImGui.GetMousePos()) < 16f && !gizmoUsed)
				{
					editor.Select(PickSelection(Pick(ImGui.GetMousePos())));
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
			if (EditorGui.Button(mode == ImGuizmoMode.Local ? "Local" : "World"))
			{
				mode = mode == ImGuizmoMode.Local ? ImGuizmoMode.World : ImGuizmoMode.Local;
			}

			ImGui.SameLine();
			bool alwaysRefresh = AlwaysRefresh;
			if (EditorGui.Checkbox("Always Refresh", ref alwaysRefresh))
			{
				AlwaysRefresh = alwaysRefresh;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Render the views every frame. Off: they render when the camera or the scene changes,\n" +
					"then for a few seconds while the lighting settles, and keep the last image.");
			}

			ImGui.SameLine();
			bool showNavigation = ShowNavigation;
			if (EditorGui.Checkbox("Nav", ref showNavigation))
			{
				ShowNavigation = showNavigation;
				editor.NotifySceneChanged();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Show the navmesh, agents and obstacles.");
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

			if (EditorGui.Button(label))
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
			controlsFrame = ImGui.GetFrameCount();
			ImGuiIOPtr io = ImGui.GetIO();
			smoothDeltaTime += (Math.Min(io.DeltaTime, 0.1f) - smoothDeltaTime) * DeltaTimeSmoothing;
			float dt = smoothDeltaTime;

			bool began = false;
			if (cameraDrag == CameraDrag.None && hovered)
			{
				if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
				{
					BeginCameraDrag(CameraDrag.Look);
					ImGui.SetWindowFocus();
					began = true;
				}
				else if (ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
				{
					BeginCameraDrag(CameraDrag.Pan);
					SetPanDepth();
					began = true;
				}
			}

			// This frame's motion first: the frame whose release ends the drag still moves the camera by it (it was dropped,
			// and quick strokes lost their last steps). Not the press's frame: ImGui takes the positions that came after a
			// button's press in the next frame, so this frame's motion is all from before the press.
			NVector2 mouseDelta = CameraDragDelta(io);
			if (began)
			{
				mouseDelta = NVector2.Zero;
				lastDragDelta = NVector2.Zero;
			}
			bool released = cameraDrag != CameraDrag.None && !ImGui.IsMouseDown(cameraDrag == CameraDrag.Look ? ImGuiMouseButton.Right : ImGuiMouseButton.Middle);
			if (cameraDrag == CameraDrag.Pan)
			{
				// The scene follows the mouse at the depth chosen when the drag started.
				Vector3 forward = Forward();
				Vector3 right = Vector3.Cross(forward, Vector3.UnitY).Normalized();
				Vector3 up = Vector3.Cross(right, forward).Normalized();
				float worldPerPixel = 2f * focusDistance * MathF.Tan(camera.FoV * 0.5f * MathF.PI / 180f) / Math.Max(1f, viewSize.Y);
				position += (-right * mouseDelta.X + up * mouseDelta.Y) * worldPerPixel;
				if (released)
				{
					EndCameraDrag();
				}

				return;
			}

			if (flying && released)
			{
				yaw -= mouseDelta.X * LookDegreesPerPixel;
				pitch = Math.Clamp(pitch + mouseDelta.Y * LookDegreesPerPixel, -89f, 89f);
				EndCameraDrag();
				return;
			}

			if (flying)
			{
				// Same conventions as the FPS controller: screen-right is -X.
				yaw -= mouseDelta.X * LookDegreesPerPixel;
				pitch = Math.Clamp(pitch + mouseDelta.Y * LookDegreesPerPixel, -89f, 89f);

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

			// Wheel: towards what the mouse points at by a share of the distance, so a notch moves as much of the view far away
			// as close up, and never through the point.
			if (io.MouseWheel != 0f)
			{
				// Towards the point of the cursor's ray at the depth there is something to look at (NavigationDepth): the mesh
				// hit itself, or the depth of what is drawn near the cursor, or of the view's scene.
				NVector2 mouse = ImGui.GetMousePos();
				float depth = NavigationDepth(mouse);
				Vector3 ray = MouseRay(mouse);
				Vector3 target = position + ray * (depth / Math.Max(0.05f, Vector3.Dot(ray, Forward())));
				Vector3 toTarget = target - position;
				if (toTarget.LengthSquared > 1e-8f)
				{
					position += toTarget * Math.Clamp(io.MouseWheel * WheelShare, -1f, 0.9f);
					focusDistance = Math.Max(MinFocusDistance, Vector3.Dot(target - position, Forward()));
				}
			}

			if (!io.WantTextInput)
			{
				HandleToolKeys();
			}
		}

		private void BeginCameraDrag(CameraDrag drag)
		{
			cameraDrag = drag;
		}

		private void EndCameraDrag()
		{
			cameraDrag = CameraDrag.None;
		}

		/// <summary>
		///     The view stopped drawing during a camera drag (tab switched, window closed): its controls missed last frame, and
		///     the cursor is released. A drag the controls ran last frame ends there, after the frame's motion (ending it here
		///     on the release dropped the last frame's motion).
		/// </summary>
		private void ReleaseCameraDragIfAbandoned()
		{
			if (cameraDrag != CameraDrag.None && controlsFrame < ImGui.GetFrameCount() - 1)
			{
				EndCameraDrag();
			}
		}

		// The UI frame the camera controls last ran in.
		private int controlsFrame;

		/// <summary>
		///     The mouse's motion this frame for a camera drag: the cursor's, as the UI sees it (real input of any device, local
		///     or remote, and scripted input in tests alike). ImGui sets it unknown (huge) when the window lost the focus.
		/// </summary>
		private NVector2 CameraDragDelta(ImGuiIOPtr io)
		{
			NVector2 delta = io.MouseDelta;
			if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y) || Math.Abs(delta.X) > 1e5f || Math.Abs(delta.Y) > 1e5f)
			{
				delta = NVector2.Zero;
			}

			lastDragDelta = cameraDrag != CameraDrag.None ? delta : NVector2.Zero;
			return delta;
		}

		/// <summary>The depth a pan moves the scene at (the scene follows the mouse there): <see cref="NavigationDepth" />.</summary>
		private void SetPanDepth()
		{
			focusDistance = NavigationDepth(ImGui.GetMousePos());
		}

		/// <summary>
		///     The depth (along the view) a pan or a zoom works at for a point of the view, from what is there now:
		///     <list type="number">
		///         <item>under the cursor: the nearer of the mesh it is over (the exact pick) and a cloud (where the light through it
		///         falls to half, <see cref="Volumetrics.VolumetricObjectBase.Raycast" />);</item>
		///         <item>the surface drawn nearest to the cursor within <see cref="NearSurfaceReach" /> of the view's height
		///         (<see cref="SceneDepthProbe" />: the view's own render, reduced on the GPU and read back without a stall);</item>
		///         <item>the median depth of what the view shows: per ray of a 16 x 9 grid the nearer of the render's surface and a
		///         cloud;</item>
		///         <item>the last depth used.</item>
		///     </list>
		///     Before 2026-11-08 it was the depth of whatever the last zoom hit, or of the ground plane (y = 0) where nothing was
		///     hit, kept until the next zoom: after zooms here and there a pan over empty space barely moved or raced (the user's
		///     report). The GI's global distance field was not used: see <see cref="SceneDepthProbe" />.
		/// </summary>
		private float NavigationDepth(NVector2 mouse)
		{
			Vector3 forward = Forward();
			var view = new Vector2((mouse.X - viewMin.X) / Math.Max(1f, viewSize.X), (mouse.Y - viewMin.Y) / Math.Max(1f, viewSize.Y));
			navigationAt = view;
			float under = float.MaxValue;
			if (Pick(mouse, out _, out Vector3? point) != null && point.HasValue)
			{
				under = Vector3.Dot(point.Value - position, forward);
				navigationSource = "mesh";
			}

			Vector3 ray = MouseRay(mouse);
			float depthPerDistance = Math.Max(0.05f, Vector3.Dot(ray, forward));
			if (TryCloudHit(ray, under < float.MaxValue ? under / depthPerDistance : camera.FarClip, out float cloud) && cloud * depthPerDistance < under)
			{
				under = cloud * depthPerDistance;
				navigationSource = "cloud";
			}

			if (under < float.MaxValue && under > MinFocusDistance)
			{
				return under;
			}

			if (depthProbe.TryDepthNear(view, NearSurfaceReach, position, forward, out float near))
			{
				navigationSource = "near";
				return Math.Max(MinFocusDistance, near);
			}

			if (TryViewDepth(out float typical))
			{
				navigationSource = "view";
				return Math.Max(MinFocusDistance, typical);
			}

			if (TryNearestObjectDistance(out float around))
			{
				navigationSource = "around";
				return Math.Max(MinFocusDistance, around);
			}

			navigationSource = "last";
			return focusDistance;
		}

		/// <summary>
		///     The view shows nothing: the distance from the camera to the nearest object of the scene (a renderer's or a cloud's
		///     box), the scale of what is around it (the user's idea: the occupancy around the camera). Boxes holding the camera
		///     do not count: whatever of them lies where the camera looks would have been drawn.
		/// </summary>
		private bool TryNearestObjectDistance(out float distance)
		{
			distance = float.MaxValue;
			Scene scene = editor.Scene;
			if (scene == null)
			{
				return false;
			}

			foreach (MeshRenderer renderer in scene.Renderers)
			{
				Mesh mesh = renderer.Mesh;
				if (mesh != null && renderer.isActiveAndEnabled && renderer.gameObject.hideFlags == HideFlags.None)
				{
					NearestBox(mesh.BoundsMin, mesh.BoundsMax, renderer.transform.LocalToWorld, ref distance);
				}
			}

			foreach (Volumetrics.VolumetricObjectBase volume in Volumetrics.VolumetricRuntime.Objects)
			{
				if (volume.isActiveAndEnabled && volume.gameObject?.scene == scene)
				{
					NearestBox(volume.Size * -0.5f, volume.Size * 0.5f, volume.transform.LocalToWorld, ref distance);
				}
			}

			return distance < float.MaxValue;
		}

		// The distance from the camera to a local box placed in the world (its world-aligned bounds), kept when nearer; a box
		// holding the camera is left out.
		private void NearestBox(Vector3 localMin, Vector3 localMax, Matrix4 localToWorld, ref float distance)
		{
			var min = new Vector3(float.MaxValue);
			var max = new Vector3(float.MinValue);
			for (int i = 0; i < 8; i++)
			{
				var corner = new Vector3((i & 1) != 0 ? localMax.X : localMin.X, (i & 2) != 0 ? localMax.Y : localMin.Y, (i & 4) != 0 ? localMax.Z : localMin.Z);
				Vector3 world = Vector3.TransformPosition(corner, localToWorld);
				min = Vector3.ComponentMin(min, world);
				max = Vector3.ComponentMax(max, world);
			}

			Vector3 outside = Vector3.ComponentMax(Vector3.ComponentMax(min - position, position - max), Vector3.Zero);
			float d = outside.Length;
			if (d > 0f && float.IsFinite(d))
			{
				distance = Math.Min(distance, d);
			}
		}

		/// <summary>The nearest cloud of the scene a ray from the camera meets (its distance along the ray), within a distance.</summary>
		private bool TryCloudHit(Vector3 ray, float maxDistance, out float distance)
		{
			distance = float.MaxValue;
			foreach (Volumetrics.VolumetricObjectBase volume in Volumetrics.VolumetricRuntime.Objects)
			{
				if (volume.isActiveAndEnabled && volume.gameObject?.scene == editor.Scene && volume.Raycast(position, ray, Math.Min(maxDistance, distance), out float t))
				{
					distance = Math.Min(distance, t);
				}
			}

			return distance < float.MaxValue;
		}

		/// <summary>The median depth of what the view shows: per ray of a 16 x 9 grid, the nearer of the render's surface and a cloud.</summary>
		private bool TryViewDepth(out float depth)
		{
			const int Columns = 16;
			const int Rows = 9;
			viewDepths.Clear();
			Vector3 forward = Forward();
			for (int row = 0; row < Rows; row++)
			{
				for (int column = 0; column < Columns; column++)
				{
					var view = new Vector2((column + 0.5f) / Columns, (row + 0.5f) / Rows);
					float nearest = depthProbe.TryDepthAt(view, position, forward, out float drawn) ? drawn : float.MaxValue;
					Vector3 ray = MouseRay(viewMin + new NVector2(view.X * viewSize.X, view.Y * viewSize.Y));
					float depthPerDistance = Math.Max(0.05f, Vector3.Dot(ray, forward));
					if (TryCloudHit(ray, nearest < float.MaxValue ? nearest / depthPerDistance : camera.FarClip, out float cloud))
					{
						nearest = Math.Min(nearest, cloud * depthPerDistance);
					}

					if (nearest < float.MaxValue)
					{
						viewDepths.Add(nearest);
					}
				}
			}

			depth = 0f;
			if (viewDepths.Count == 0)
			{
				return false;
			}

			viewDepths.Sort();
			depth = viewDepths[viewDepths.Count / 2];
			return true;
		}

		private readonly System.Collections.Generic.List<float> viewDepths = new System.Collections.Generic.List<float>();

		/// <summary>How far from the cursor (a share of the view's height) a drawn surface still sets the depth to navigate at.</summary>
		private const float NearSurfaceReach = 0.1f;

		/// <summary>The direction of the ray through a point of the view (unit length).</summary>
		private Vector3 MouseRay(NVector2 mouse)
		{
			float ndcX = (mouse.X - viewMin.X) / viewSize.X * 2f - 1f;
			float ndcY = 1f - (mouse.Y - viewMin.Y) / viewSize.Y * 2f;
			Matrix4 inverse = camera.ViewProjectionMatrix.Inverted();
			Vector3 direction = Unproject(new Vector4(ndcX, ndcY, 1f, 1f), inverse) - Unproject(new Vector4(ndcX, ndcY, -1f, 1f), inverse);
			return direction.LengthSquared > 1e-12f ? direction.Normalized() : Forward();
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
			// The window lost the focus during a drag: ImGui's mouse position is invalid and ImGuizmo's result not a number.
			if (!float.IsFinite(world.GetDeterminant()))
			{
				return;
			}

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
			return Pick(mouse, out _, out _);
		}

		/// <summary>
		///     What a click on <paramref name="hit" /> selects (as in Unity): the root of the prefab instance it is part of,
		///     unless the selection is in that instance already (then the object itself, so a second click goes deeper).
		/// </summary>
		private GameObject PickSelection(GameObject hit)
		{
			GameObject root = hit?.prefabInstance?.Root;
			if (root == null || root == hit || root.dead)
			{
				return hit;
			}

			GameObject selected = editor.Selected;
			bool insideInstance = selected != null && !selected.dead && (selected == root || selected.transform.IsChildOf(root.transform));
			return insideInstance ? hit : root;
		}

		/// <summary>Closest renderer under the mouse, the sub-mesh of the triangle hit and the world point hit.</summary>
		private GameObject Pick(NVector2 mouse, out int subMesh)
		{
			return Pick(mouse, out subMesh, out _);
		}

		private GameObject Pick(NVector2 mouse, out int subMesh, out Vector3? point)
		{
			subMesh = -1;
			point = null;
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

				// Triangle positions follow the index buffer, so a triangle's first index tells its sub-mesh.
				Vector3[] triangles = mesh.GetTrianglePositions();
				for (int i = 0; i + 2 < triangles.Length; i += 3)
				{
					if (RayTriangle(localOrigin, localDirection, triangles[i], triangles[i + 1], triangles[i + 2], out float t) && t < bestT && t > 0f)
					{
						bestT = t;
						best = renderer.gameObject;
						subMesh = SubMeshOf(mesh, i);
					}
				}
			}

			if (best != null)
			{
				point = origin + direction * bestT;
			}
			else if (Math.Abs(direction.Y) > 1e-6f && -origin.Y / direction.Y > 0f)
			{
				// Nothing hit: the ground plane.
				point = origin + direction * (-origin.Y / direction.Y);
			}

			return best;
		}

		private static int SubMeshOf(Mesh mesh, int firstIndex)
		{
			for (int s = 0; s < mesh.SubMeshCount; s++)
			{
				SubMesh range = mesh.SubMeshes[s];
				if (firstIndex >= range.FirstIndex && firstIndex < range.FirstIndex + range.IndexCount)
				{
					return s;
				}
			}

			return 0;
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
			if (ModelAssets.IsModelFile(assetPath))
			{
				editor.InstantiateModel(assetPath);
			}
			else if (assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase))
			{
				// The instance goes where the mouse points (on the surface under it, else on the ground plane).
				Pick(ImGui.GetMousePos(), out _, out Vector3? point);
				editor.InstantiatePrefab(assetPath, null, point);
			}
			else if (assetPath.EndsWith(".material", StringComparison.OrdinalIgnoreCase))
			{
				// The material goes into the slot of the sub-mesh under the mouse.
				GameObject target = Pick(ImGui.GetMousePos(), out int subMesh);
				MeshRenderer renderer = target?.GetComponent<MeshRenderer>();
				if (renderer != null)
				{
					editor.Edits.Begin(renderer);
					renderer.SetMaterial(Math.Max(0, subMesh), InternalStorage.GetOrCreateMaterial(assetPath));
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

		private string StateText()
		{
			return "{\n  \"cameraPosition\": [" + F(position.X) + ", " + F(position.Y) + ", " + F(position.Z) + "],\n  \"cameraYaw\": " + F(yaw) + ",\n  \"cameraPitch\": " + F(pitch) + ",\n  \"flySpeed\": " + F(flySpeed) + ",\n  \"focusDistance\": " + F(focusDistance) + ",\n  \"alwaysRefresh\": " + (AlwaysRefresh ? "true" : "false") + ",\n  \"showNavigation\": " + (ShowNavigation ? "true" : "false") + ",\n  \"gizmoOperation\": \"" + operation + "\",\n  \"gizmoMode\": \"" + mode + "\"\n}\n";
		}

		/// <summary>
		///     editorstatefile=path: the view's state (camera, tool) is read from and written to that file instead of the
		///     project's Library/EditorState.json (automated runs that place the camera leave the user's alone).
		/// </summary>
		public static string StateFileOverride;

		private static string StateFile(ProjectInfo project)
		{
			return StateFileOverride ?? Path.Combine(project.LibraryPath, "EditorState.json");
		}

		private static string F(float value)
		{
			return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		#endregion
	}
}
