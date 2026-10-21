using LELEngine.Rendering;
using OpenTK.Windowing.Common;

namespace LELEngine
{
	/// <summary>
	///     Standalone game host: the window, the renderer and the game loop that updates and renders the loaded
	///     scenes (<see cref="SceneManager.LoadedScenes" />; background loads advance first each frame, see
	///     <see cref="SceneManager.Update" />). The scenes' component lifecycle starts when the window loads, so a scene
	///     built in code or loaded from a file before <c>Run()</c> gets its Awake calls then.
	/// </summary>
	public sealed class GameHost : Window
	{
		#region PublicFields

		public Scene ActiveScene => SceneManager.ActiveScene;
		public bool Loaded { get; private set; }
		public RenderQueue RenderQueue { get; private set; }
		public Renderer Renderer { get; private set; }

		/// <summary>
		///     The game keeps running (updating and rendering) while its window is not focused. Off: the scene pauses
		///     until the window is focused again; the last image stays.
		/// </summary>
		public bool RunInBackground { get; set; } = true;

		#endregion

		#region Constructors

		public GameHost(int width, int height, string title)
			: this(width, height, title, PreferredApiVersion)
		{ }

		public GameHost(int width, int height, string title, System.Version apiVersion)
			: this(new GameWindowOptions { Width = width, Height = height, Title = title }, apiVersion)
		{ }

		public GameHost(GameWindowOptions options, System.Version apiVersion)
			: base(options.Width, options.Height, options.Title, apiVersion, options.Visible, options.Fullscreen)
		{
			RenderQueue = RenderQueue.PerShader;
			Input.Host = this;

			if (options.CreateRenderer)
			{
				// The GL context exists as soon as the window does, so the renderer can be created here.
				// This lets game code configure passes before Run().
				CreateRenderer(ClientSize.X, ClientSize.Y);
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Creates the renderer with the default passes (compiles the engine's shaders). The constructor does it
		///     unless <see cref="GameWindowOptions.CreateRenderer" /> is off; call it on the thread that holds the GL
		///     context, with the window's size read on the main thread (a resize meanwhile reaches the renderer when
		///     <c>Run()</c> starts).
		/// </summary>
		public void CreateRenderer(int width, int height)
		{
			if (Renderer != null)
			{
				return;
			}

			Renderer = new Renderer(width, height);
			Renderer.AddDefaultPasses();
			Game.Renderer = Renderer;
		}

		/// <summary>
		///     Renders the active scene <paramref name="frames" /> times into an offscreen target, nothing presented: the
		///     passes allocate their per-scene resources (GI caches, distance fields) and compile what they build on first
		///     use, so the first frame the window shows does not wait for that. The player does it on its loading thread
		///     while the splash screen is up, before the scene's lifecycle starts (as the editor renders a scene in edit
		///     mode).
		/// </summary>
		public void WarmUp(int frames)
		{
			Camera camera = SceneManager.MainCamera;
			if (Renderer == null || camera == null || frames <= 0)
			{
				return;
			}

			var target = new Framebuffer(Renderer.Width, Renderer.Height);
			target.AddColorAttachment(RenderTextureFormat.RGBA8);
			target.Validate();
			Framebuffer output = Renderer.OutputTarget;
			Renderer.OutputTarget = target;
			try
			{
				for (int i = 0; i < frames; i++)
				{
					SceneManager.GetRenderLists(out var renderers, out var behaviours);
					Renderer.RenderFrame(camera, renderers, behaviours, RenderQueue);
				}
			}
			finally
			{
				Renderer.OutputTarget = output;
				target.Delete();
				DebugDraw.Clear();
				GLState.Reset();
			}
		}

		/// <summary>
		///     Creates an empty scene and makes it active.
		/// </summary>
		public Scene LoadEmptyScene()
		{
			Scene scene = SceneManager.CreateScene("Untitled");
			SceneManager.SetActiveScene(scene);
			if (Loaded)
			{
				scene.StartLifecycle(true);
			}

			return scene;
		}

		public void SetRenderQueue(RenderQueue queue)
		{
			RenderQueue = queue;
		}

		public void ResetDrawState()
		{
			GLState.Reset();
		}

		#endregion

		#region ProtectedMethods

		protected override void OnLoad()
		{
			base.OnLoad();

			Time.fixedDeltaTimeD = 0.02f;
			KeyDown += Input.Input_KeyDown;
			KeyUp += Input.Input_KeyUp;
			MouseMove += Input.Input_MouseMove;
			MouseDown += Input.Input_MouseDown;
			MouseUp += Input.Input_MouseUp;
			MouseWheel += Input.Input_MouseWheel;
			TextInput += Input.Input_TextInput;
			UIElements.UIRuntime.GetClipboard = () => ClipboardString;
			UIElements.UIRuntime.SetClipboard = text => ClipboardString = text;

			SceneManager.StartLifecycle(true);
			Loaded = true;
		}

		protected override void OnResize(ResizeEventArgs e)
		{
			base.OnResize(e);

			// Not while another thread holds the context (the player's loading thread may be creating the renderer);
			// Run() resizes it to the window when it starts. The same size again (that call) keeps the GI history.
			if (Context.IsCurrent && Renderer != null && (Renderer.Width != System.Math.Max(1, e.Width) || Renderer.Height != System.Math.Max(1, e.Height)))
			{
				Renderer.Resize(e.Width, e.Height);
			}
		}

		protected override void OnUnload()
		{
			SceneManager.UnloadAll();
			Audio.AudioSystem.Shutdown();
			Renderer?.Dispose();
			Renderer = null;
			base.OnUnload();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);
			if (!IsFocused && !RunInBackground)
			{
				return;
			}

			Input.BeginFrame();
			long updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
			SceneManager.Update();
			SceneManager.UpdateScenes(Time.deltaTime);
			Audio.AudioSystem.Update();
			Time.cpuUpdateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - updateStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			Input.EndFrame();
			ApplyUICursor();
		}

		/// <summary>Shows the cursor the hovered UI element asks for (text caret over fields, ...).</summary>
		private void ApplyUICursor()
		{
			if (Input.cursorLocked)
			{
				return;
			}

			OpenTK.Windowing.Common.Input.MouseCursor wanted = UIElements.UIRuntime.ToWindowCursor(UIElements.UIRuntime.DesiredCursor);
			if (wanted != appliedCursor)
			{
				appliedCursor = wanted;
				Cursor = wanted;
			}
		}

		private OpenTK.Windowing.Common.Input.MouseCursor appliedCursor = OpenTK.Windowing.Common.Input.MouseCursor.Default;

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			// Show last frame first: its GPU work overlapped this frame's update, so the wait is short.
			PresentPendingFrame();
			if (!IsFocused && !RunInBackground)
			{
				// Paused in the background: keep the last image, do not spin.
				System.Threading.Thread.Sleep(50);
				return;
			}

			renderStopwatch.Reset();
			renderStopwatch.Start();

			SceneManager.GetRenderLists(out var renderers, out var behaviours);
			Renderer?.RenderFrame(SceneManager.MainCamera, renderers, behaviours, RenderQueue);
			Time.cpuRenderMs = renderStopwatch.Elapsed.TotalMilliseconds;
			DebugDraw.Clear();

			base.OnRenderFrame(e);
		}

		#endregion
	}

	public enum RenderQueue
	{
		PerShader,
		PerObject,
		ApproxFrontToBack,
		ApproxBackToFront
	}
}
