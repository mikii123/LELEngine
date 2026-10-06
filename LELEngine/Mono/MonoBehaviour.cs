using System;
using System.Collections.Generic;
using System.Reflection;
using LELCS;
using LELEngine.Rendering;
using OpenTK.Windowing.Common;

namespace LELEngine
{
	/// <summary>
	///     Handles scripts logic and behaviours. Rendering is delegated to <see cref="Rendering.Renderer" />.
	/// </summary>
	public sealed class MonoBehaviour : Window
	{
		#region PublicFields

		public Scene ActiveScene { get; private set; }
		public bool Loaded { get; private set; }
		public RenderQueue RenderQueue { get; private set; }
		public ECSManager ECSManager { get; private set; }
		public Renderer Renderer { get; private set; }

		#endregion

		#region PrivateFields

		private readonly List<Behaviour> toInit = new List<Behaviour>();
		private readonly List<Behaviour> behaviours = new List<Behaviour>();
		private readonly List<MeshRenderer> meshRenderers = new List<MeshRenderer>();
		private float fixedTime;

		#endregion

		#region Constructors

		public MonoBehaviour(int width, int height, string title)
			: this(width, height, title, PreferredApiVersion)
		{ }

		public MonoBehaviour(int width, int height, string title, System.Version apiVersion)
			: base(width, height, title, apiVersion)
		{
			RenderQueue = RenderQueue.PerShader;

			// The GL context exists as soon as the window does, so the renderer can be created here.
			// This lets game code configure passes before Run().
			Renderer = new Renderer(ClientSize.X, ClientSize.Y);
			Renderer.AddDefaultPasses();
		}

		#endregion

		#region UnityMethods

		private void Awake()
		{
			foreach (Behaviour ob in toInit)
			{
				ob.Awake();
			}
		}

		private void Start()
		{
			foreach (Behaviour ob in toInit)
			{
				ob.Start();
			}
		}

		private void Update()
		{
			ECSManager?.Execute();

			foreach (Behaviour ob in behaviours)
			{
				ob.Update();
			}
		}

		private void LateUpdate()
		{
			foreach (Behaviour ob in behaviours)
			{
				ob.LateUpdate();
			}
		}

		private void FixedUpdate()
		{
			if (fixedTime >= 0.02f)
			{
				foreach (Behaviour ob in behaviours)
				{
					ob.FixedUpdate();
				}

				fixedTime = 0;
			}
		}

		#endregion

		#region PublicMethods

		public void InitializeECSScope(Assembly assembly)
		{
			ECSManager = new ECSManager(assembly);
		}

		public void LoadDefaultScene()
		{
			Console.WriteLine("Loading default scene...");
			ActiveScene = new Scene(new[] { "Floor" });

			ResetDrawState();
		}

		/// <summary>
		///     Creates an empty scene and makes it active.
		/// </summary>
		public Scene LoadEmptyScene()
		{
			ActiveScene = new Scene();
			return ActiveScene;
		}

		public void SetRenderQueue(RenderQueue queue)
		{
			RenderQueue = queue;
		}

		public void ResetDrawState()
		{
			GLState.Reset();
		}

		public void InitBehaviour(Behaviour behaviour)
		{
			if (behaviour is MeshRenderer renderer)
			{
				meshRenderers.Add(renderer);
			}

			toInit.Add(behaviour);
		}

		public void InitBehaviour(ICollection<Behaviour> behaviours)
		{
			foreach (Behaviour behaviour in behaviours)
			{
				InitBehaviour(behaviour);
			}
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

			foreach (Behaviour ob in toInit)
			{
				ob.Awake();
			}
			foreach (Behaviour ob in toInit)
			{
				ob.Start();
			}

			behaviours.AddRange(toInit);
			toInit.Clear();

			Loaded = true;
		}

		protected override void OnResize(ResizeEventArgs e)
		{
			base.OnResize(e);
			Renderer?.Resize(e.Width, e.Height);
		}

		protected override void OnUnload()
		{
			Renderer?.Dispose();
			Renderer = null;
			base.OnUnload();
		}

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);
			if (!IsFocused) return;

			fixedTime += Time.deltaTime;
			Input.BeginFrame();

			if (toInit.Count > 0)
			{
				Awake();
				Start();
				behaviours.AddRange(toInit);
				toInit.Clear();
			}

			FixedUpdate();
			Update();
			LateUpdate();

			Input.EndFrame();
		}

		protected override void OnRenderFrame(FrameEventArgs e)
		{
			// Start stopwatch to measure render time
			renderStopwatch.Reset();
			renderStopwatch.Start();

			Renderer?.RenderFrame(Camera.main, meshRenderers, behaviours, RenderQueue);

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
