using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>The scene through its main camera, as the game shows it. Receives the game's input while playing.</summary>
	internal sealed class GameViewPanel : IDisposable
	{
		#region PublicFields

		public ViewRenderer ViewRenderer { get; private set; }

		/// <summary>The game view is focused: keyboard and mouse go to the game (while it plays).</summary>
		public bool HasInputFocus { get; private set; }

		/// <summary>Top-left corner of the game image in window coordinates (game mouse positions are relative to it).</summary>
		public NVector2 ImageOrigin { get; private set; }

		/// <summary>The mouse is over the game image.</summary>
		public bool IsMouseOverImage { get; private set; }

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly ViewRefresh refresh = new ViewRefresh();
		private readonly List<Camera> stack = new List<Camera>();
		private bool visible;
		private bool focusRequested;
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

		public void Draw()
		{
			if (!Open)
			{
				// Closed: nothing to render, no game input.
				visible = false;
				HasInputFocus = false;
				IsMouseOverImage = false;
				return;
			}

			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			visible = ImGui.Begin("Game", ref Open);
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
			if (refresh.ShouldRender(camera, signature, ViewRenderer.Width, ViewRenderer.Height, editor.SceneVersion, continuous))
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
			NVector2 available = ImGui.GetContentRegionAvail();
			viewSize = new NVector2(Math.Max(16, available.X), Math.Max(16, available.Y));
			Scene scene = editor.Scene;
			if (ViewRenderer == null || scene == null)
			{
				return;
			}

			if (SceneManager.MainCamera == null)
			{
				ImGui.SetCursorPos(new NVector2(12, 12));
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
				ImGui.SetCursorPos(new NVector2(10, 8));
				ImGui.TextDisabled("Edit mode - press Play (Ctrl+P) to run the game");
			}
		}

		#endregion
	}
}
