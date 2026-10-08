using System.Reflection;
using LELCS;
using LELEngine.Rendering;
using OpenTK.Windowing.Common;

namespace LELEngine
{
	/// <summary>
	///     Standalone game host: the window, the renderer and the game loop that updates and renders the active
	///     scene (<see cref="SceneManager.ActiveScene" />). The scene's component lifecycle starts when the window
	///     loads, so a scene built in code or loaded from a file before <c>Run()</c> gets its Awake calls then.
	/// </summary>
	public sealed class GameHost : Window
	{
		#region PublicFields

		public Scene ActiveScene => SceneManager.ActiveScene;
		public bool Loaded { get; private set; }
		public RenderQueue RenderQueue { get; private set; }
		public ECSManager ECSManager { get; private set; }
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
			: base(width, height, title, apiVersion)
		{
			RenderQueue = RenderQueue.PerShader;
			Input.Host = this;

			// The GL context exists as soon as the window does, so the renderer can be created here.
			// This lets game code configure passes before Run().
			Renderer = new Renderer(ClientSize.X, ClientSize.Y);
			Renderer.AddDefaultPasses();
			Game.Renderer = Renderer;
		}

		#endregion

		#region PublicMethods

		public void InitializeECSScope(Assembly assembly)
		{
			ECSManager = new ECSManager(assembly);
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

			SceneManager.ActiveScene?.StartLifecycle(true);
			Loaded = true;
		}

		protected override void OnResize(ResizeEventArgs e)
		{
			base.OnResize(e);
			Renderer?.Resize(e.Width, e.Height);
		}

		protected override void OnUnload()
		{
			SceneManager.ActiveScene?.Unload();
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
			ECSManager?.Execute();
			SceneManager.ActiveScene?.Update(Time.deltaTime);
			Input.EndFrame();
		}

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

			Scene scene = SceneManager.ActiveScene;
			if (scene != null)
			{
				Renderer?.RenderFrame(scene.MainCamera, scene.Renderers, scene.ActiveBehaviours, RenderQueue);
			}
			else
			{
				Renderer?.RenderFrame(null, System.Array.Empty<MeshRenderer>(), System.Array.Empty<Behaviour>(), RenderQueue);
			}
			Time.cpuRenderMs = renderStopwatch.Elapsed.TotalMilliseconds;

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
