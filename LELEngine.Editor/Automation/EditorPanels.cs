using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Automation
{
	/// <summary>The editor's main window as a whole ("Editor"): the menu bar, the dock space and every window in it.</summary>
	internal sealed class MainWindowPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;

		#endregion

		#region Constructors

		public MainWindowPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public string PanelName => "Editor";
		public string ImGuiName => null;

		public bool PanelOpen
		{
			get => true;
			set { }
		}

		public bool IsWholeContext => true;

		public NVector2 DefaultCaptureSize => new NVector2(1600, 900);

		public void Draw()
		{
			editor.DrawUI(true);
		}

		public void Focus()
		{
			if (editor.HasUI)
			{
				editor.Window.Focus();
			}
		}

		#endregion
	}

	/// <summary>The UI Builder's OS window (open it first: "UI Builder" opens it).</summary>
	internal sealed class UIBuilderPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;

		#endregion

		#region Constructors

		public UIBuilderPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public string PanelName => "UI Builder";
		public string ImGuiName => null;

		public bool PanelOpen
		{
			get => editor.UIBuilderWindow != null;
			set
			{
				if (value && editor.UIBuilderWindow == null)
				{
					editor.OpenUIBuilder(null);
				}
				else if (!value)
				{
					editor.UIBuilderWindow?.Close();
				}
			}
		}

		public bool IsWholeContext => true;
		public ImGuiController Owner => editor.UIBuilderWindow?.Controller;
		public NVector2 DefaultCaptureSize => new NVector2(1440, 860);

		public void Draw()
		{
			editor.UIBuilderWindow?.Builder.Draw();
		}

		public void AfterCaptureFrame()
		{
			editor.UIBuilderWindow?.Builder.RenderPreview();
		}

		public void Focus()
		{
			editor.OpenUIBuilder(null);
		}

		#endregion
	}

	/// <summary>The Audio Studio's OS window (open it first: "Audio Studio" opens it).</summary>
	internal sealed class AudioStudioPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;

		#endregion

		#region Constructors

		public AudioStudioPanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public string PanelName => "Audio Studio";
		public string ImGuiName => null;

		public bool PanelOpen
		{
			get => editor.AudioStudioWindow != null;
			set
			{
				if (value && editor.AudioStudioWindow == null)
				{
					editor.OpenAudioStudio(null, null);
				}
				else if (!value)
				{
					editor.AudioStudioWindow?.Close();
				}
			}
		}

		public bool IsWholeContext => true;
		public ImGuiController Owner => editor.AudioStudioWindow?.Controller;
		public NVector2 DefaultCaptureSize => new NVector2(1500, 900);

		public void Draw()
		{
			editor.AudioStudioWindow?.Studio.Draw();
		}

		public void Focus()
		{
			editor.OpenAudioStudio(null, null);
		}

		#endregion
	}

	/// <summary>An open window of the project's editor scripts (<see cref="EditorWindow" />).</summary>
	internal sealed class ScriptWindowPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorExtensions host;

		#endregion

		#region Constructors

		public ScriptWindowPanel(EditorExtensions host, EditorWindow window)
		{
			this.host = host;
			Window = window;
		}

		#endregion

		#region PublicMethods

		public EditorWindow Window { get; }

		public string PanelName => Window.title;

		// The window's ImGui id comes from its key (the part after ###): the title may change.
		public string ImGuiName => "###" + Window.Key;

		public bool PanelOpen
		{
			get => Window.IsOpen;
			set
			{
				if (value)
				{
					host.OpenWindow(Window, null);
				}
				else
				{
					host.CloseWindow(Window);
				}
			}
		}

		public NVector2 DefaultCaptureSize => Window.defaultSize;

		public void Draw()
		{
			host.DrawWindow(Window);
		}

		public void Focus()
		{
			host.OpenWindow(Window, null);
			Window.Focus();
		}

		#endregion
	}
}
