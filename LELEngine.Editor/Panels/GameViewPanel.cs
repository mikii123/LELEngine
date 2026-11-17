using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>The scene through its main camera, as the game shows it. Receives the game's input while playing.</summary>
	internal sealed class GameViewPanel : IDisposable, IEditorPanel
	{
		#region PublicFields

		public ViewRenderer ViewRenderer { get; private set; }

		/// <summary>The game view is focused: keyboard and mouse go to the game (while it plays).</summary>
		public bool HasInputFocus { get; private set; }

		/// <summary>Top-left corner of the game image in window coordinates (game mouse positions are relative to it).</summary>
		public NVector2 ImageOrigin { get; private set; }

		/// <summary>The mouse is over the game image.</summary>
		public bool IsMouseOverImage { get; private set; }

		/// <summary>
		///     Unity's Maximize On Play (the toolbar's toggle, kept in the editor's preferences): while the game plays (or is
		///     paused) the Game window fills the editor below the menu bar and the other windows are not drawn; stopping gives
		///     the layout back as it was (nothing of it changes: the maximized window is a window of its own, not docked).
		/// </summary>
		public static bool MaximizeOnPlay
		{
			get => EditorPreferences.GetToggle(MaximizeOnPlayToggle, false);
			set => EditorPreferences.SetToggle(MaximizeOnPlayToggle, value);
		}

		/// <summary>The Game window fills the editor now (see <see cref="MaximizeOnPlay" />).</summary>
		public bool Maximized => Open && MaximizeOnPlay && editor.IsPlaying;

		#endregion

		#region PrivateFields

		private const string MaximizeOnPlayToggle = "Game/Maximize On Play";

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly ViewRefresh refresh = new ViewRefresh();
		private readonly List<Camera> stack = new List<Camera>();
		private bool visible;
		private bool focusRequested;
		private bool wasMaximized;
		private float renderedFactor = 1f;
		private NVector2 viewSize = new NVector2(640, 360);

		#endregion

		#region Constructors

		public GameViewPanel(EditorApplication editor)
		{
			this.editor = editor;
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

		public void Focus()
		{
			Open = true;
			focusRequested = true;
		}

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		string IEditorPanel.PanelName => "Game";
		string IEditorPanel.ImGuiName => "Game";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1280, 720);
		void IEditorPanel.Focus() => Focus();

		public void Draw()
		{
			if (Automation.WindowCapture.Active)
			{
				DrawCapture();
				return;
			}

			if (!Open)
			{
				// Closed: nothing to render, no game input.
				visible = false;
				HasInputFocus = false;
				IsMouseOverImage = false;
				return;
			}

			bool maximized = Maximized;
			if (focusRequested || (maximized && !wasMaximized))
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			wasMaximized = maximized;
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			if (maximized)
			{
				// Over the dock space below the menu bar (Play stays there), undocked and unsaved: the docked "Game" window
				// is simply not drawn meanwhile, so the layout is untouched.
				ImGuiViewportPtr viewport = ImGui.GetMainViewport();
				ImGui.SetNextWindowPos(viewport.WorkPos);
				ImGui.SetNextWindowSize(viewport.WorkSize);
				ImGui.SetNextWindowViewport(viewport.ID);
				visible = ImGui.Begin("Game###GameMaximized", ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize
					| ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings);
			}
			else
			{
				visible = ImGui.Begin("Game", ref Open);
			}

			ImGui.PopStyleVar();
			HasInputFocus = visible && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
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

			// The game grabs the cursor (mouse look); losing focus gives it back.
			if (!HasInputFocus && Input.cursorLocked)
			{
				Input.SetCursorLocked(false);
			}
		}

		/// <summary>
		///     The window in a capture (<see cref="Automation.WindowCapture" />): the game at the capture's size, rendered for
		///     it. Nothing the editor's frames keep (view size, input focus, the mouse's place) changes.
		/// </summary>
		private void DrawCapture()
		{
			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			bool shown = ImGui.Begin("Game", ref Open);
			ImGui.PopStyleVar();
			if (shown && editor.Scene != null)
			{
				DrawToolbar();
				NVector2 available = ImGui.GetContentRegionAvail();
				var size = new NVector2(Math.Max(16, available.X), Math.Max(16, available.Y));
				if (SceneManager.MainCamera == null)
				{
					ImGui.SetCursorPos(ImGui.GetCursorPos() + new NVector2(12, 8));
					ImGui.TextColored(EditorStyle.Warning, "No base camera in the scene (add a GameObject with a Camera component, Render Type Base).");
				}
				else
				{
					int texture = Automation.WindowCapture.ViewTexture(this, ViewRenderer, visible, (int)size.X, (int)size.Y, RenderCapture);
					ImGui.Image(EditorGui.Texture(texture), size, new NVector2(0, 1), new NVector2(1, 0));
					if (!editor.IsPlaying)
					{
						ImGui.SetCursorPos(ImGui.GetItemRectMin() - ImGui.GetWindowPos() + new NVector2(10, 8));
						ImGui.TextDisabled("Edit mode - press Play (Ctrl+P) to run the game");
					}
				}
			}

			ImGui.End();
		}

		/// <summary>One frame of every loaded scene through the main camera's stack into a capture's renderer.</summary>
		private void RenderCapture(ViewRenderer target)
		{
			Camera camera = SceneManager.MainCamera;
			if (camera == null)
			{
				return;
			}

			camera.GetRenderStack(stack);
			foreach (Camera view in stack)
			{
				view.UpdateMatrices(target.Width / (float)target.Height);
			}

			stack.Clear();
			target.Renderer.Settings.SuperSampling = ProjectSettings.Current.Graphics.SuperSampling;
			target.RenderLoadedScenes(camera);
		}

		/// <summary>Renders when the view shows something new (<see cref="ViewRefresh" />) or every frame when continuous.</summary>
		public void Render(bool continuous)
		{
			// Every loaded scene (play mode can load more, additively or kept by DontDestroyOnLoad).
			Camera camera = SceneManager.MainCamera;
			if (!visible || ViewRenderer == null || camera == null)
			{
				return;
			}

			ViewRenderer.EnsureSize((int)viewSize.X, (int)viewSize.Y);

			// The view shows the camera's whole stack: an overlay camera moving changes it too.
			camera.GetRenderStack(stack);
			Matrix4 signature = Matrix4.Zero;
			foreach (Camera view in stack)
			{
				view.UpdateMatrices(ViewRenderer.Width / (float)ViewRenderer.Height);
				signature += view.ViewProjectionMatrix;
			}

			// No camera kept between frames (it would hold an unloaded scene).
			stack.Clear();
			// The game's super sampling (Project Settings > Graphics): the view shows what the player will.
			SuperSamplingSettings superSampling = ProjectSettings.Current.Graphics.SuperSampling;
			bool superSamplingChanged = superSampling.Factor != renderedFactor;
			renderedFactor = superSampling.Factor;
			ViewRenderer.Renderer.Settings.SuperSampling = superSampling;
			if (refresh.ShouldRender(camera, signature, ViewRenderer.Width, ViewRenderer.Height, editor.SceneVersion, continuous) || superSamplingChanged)
			{
				ViewRenderer.RenderLoadedScenes(camera);
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
			Scene scene = editor.Scene;
			if (ViewRenderer == null || scene == null)
			{
				return;
			}

			if (SceneManager.MainCamera == null)
			{
				ImGui.SetCursorPos(ImGui.GetCursorPos() + new NVector2(12, 8));
				ImGui.TextColored(EditorStyle.Warning, "No base camera in the scene (add a GameObject with a Camera component, Render Type Base).");
				return;
			}

			ImGui.Image(EditorGui.Texture(ViewRenderer.Texture), viewSize, new NVector2(0, 1), new NVector2(1, 0));
			ImageOrigin = ImGui.GetItemRectMin();
			IsMouseOverImage = ImGui.IsItemHovered();
			if (Input.Host is GameInputHost host)
			{
				host.ViewSize = new OpenTK.Mathematics.Vector2i((int)viewSize.X, (int)viewSize.Y);
			}

			if (editor.IsPlaying && ImGui.IsItemHovered() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				ImGui.SetWindowFocus();
			}

			if (!editor.IsPlaying)
			{
				ImGui.SetCursorPos(ImageOrigin - ImGui.GetWindowPos() + new NVector2(10, 8));
				ImGui.TextDisabled("Edit mode - press Play (Ctrl+P) to run the game");
			}
		}

		/// <summary>The row above the game's image (its height is not the game's).</summary>
		private void DrawToolbar()
		{
			ImGui.SetCursorPos(ImGui.GetCursorPos() + new NVector2(6, 3));
			bool maximize = MaximizeOnPlay;
			if (EditorGui.Checkbox("Maximize On Play", ref maximize))
			{
				MaximizeOnPlay = maximize;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("While the game plays, the Game window fills the editor; stopping brings the windows back.");
			}

			// A gap as an item (moving the cursor alone, with nothing drawn after it while the scene loads, is ImGui's assert).
			ImGui.Dummy(new NVector2(0, 1));
		}

		#endregion
	}
}
