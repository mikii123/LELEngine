using System;
using Hexa.NET.ImGui;
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

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly ViewRefresh refresh = new ViewRefresh();
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
			focusRequested = true;
		}

		public void Draw()
		{
			if (focusRequested)
			{
				ImGui.SetNextWindowFocus();
				focusRequested = false;
			}

			ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVector2(0, 0));
			visible = ImGui.Begin("Game");
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
			Scene scene = editor.Scene;
			Camera camera = scene?.MainCamera;
			if (!visible || ViewRenderer == null || camera == null)
			{
				return;
			}

			ViewRenderer.EnsureSize((int)viewSize.X, (int)viewSize.Y);
			camera.UpdateMatrices(ViewRenderer.Width / (float)ViewRenderer.Height);
			if (refresh.ShouldRender(camera, ViewRenderer.Width, ViewRenderer.Height, editor.SceneVersion, continuous))
			{
				ViewRenderer.Render(camera, scene);
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

			if (scene.MainCamera == null)
			{
				ImGui.SetCursorPos(new NVector2(12, 12));
				ImGui.TextColored(EditorStyle.Warning, "No camera in the scene (add a GameObject with a Camera component).");
				return;
			}

			ImGui.Image(EditorGui.Texture(ViewRenderer.Texture), viewSize, new NVector2(0, 1), new NVector2(1, 0));
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
