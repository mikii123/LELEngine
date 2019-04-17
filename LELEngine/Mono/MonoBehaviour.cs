using System;
using System.Collections.Generic;
using System.Reflection;
using LELCS;
using LELEngine.Shaders;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
	/// <summary>
	///     Handles scripts logic and behaviours
	/// </summary>
	public sealed class MonoBehaviour : Window
	{
		#region PublicFields

		public Scene ActiveScene { get; private set; }
		public bool Loaded { get; private set; }
		public RenderQueue RenderQueue { get; private set; }
		public ECSManager ECSManager { get; private set; }

		#endregion

		#region PrivateFields

		private List<Behaviour> toInit = new List<Behaviour>();
		private List<Behaviour> behaviours = new List<Behaviour>();
		private List<MeshRenderer> meshRenderers = new List<MeshRenderer>();
		private float fixedTime;

		#endregion

		#region Constructors

		public MonoBehaviour(int width, int height, string title)
			: base(width, height, title)
		{
			RenderQueue = RenderQueue.PerShader;
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

		public void SetRenderQueue(RenderQueue queue)
		{
			RenderQueue = queue;
		}

		public void ResetDrawState()
		{
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.UseProgram(0);
		}

		public void InitBehaviour(Behaviour behaviour)
		{
			if (behaviour is MeshRenderer)
			{
				meshRenderers.Add((MeshRenderer)behaviour);
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

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);

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

		protected override void OnUpdateFrame(FrameEventArgs e)
		{
			base.OnUpdateFrame(e);

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

			// clear the screen
			GL.ClearColor(0, 0, 0, 255);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

			// Enable depth test
			GL.Enable(EnableCap.DepthTest);
			GL.Enable(EnableCap.CullFace);
			// Cull Back
			GL.CullFace(CullFaceMode.Back);

			Render();

			PostRender();

			base.OnRenderFrame(e);
		}

		#endregion

		#region PrivateMethods

		private void Render()
		{
			switch (RenderQueue)
			{
				case RenderQueue.PerObject:
				{
					// Per object render queue
					// very basic - for demo purposes
					PerObjectRenderQueue();

					break;
				}
				case RenderQueue.PerShader:
				{
					// Per shader render queue
					// useful for small render queues
					PerShaderRenderQueue();

					break;
				}
				default:
				{
					PerShaderRenderQueue();

					break;
				}
			}
		}

		private void PostRender()
		{
			foreach (Behaviour ob in behaviours)
			{
				ob.PostRender();
				ResetDrawState();
			}
		}

		private void PerObjectRenderQueue()
		{
			foreach (MeshRenderer obj in meshRenderers)
			{
				obj.UsingShader.Use();
				obj.Render();

				// reset state for potential further draw calls
				ResetDrawState();
			}
		}

		// TODO: Expand for blending
		private void PerShaderRenderQueue()
		{
			// Internal storage contains only used shaders
			foreach (KeyValuePair<string, ShaderProgram> shader in InternalStorage.Shaders)
			{
				// Activate the shader program
				shader.Value.Use();

				foreach (MeshRenderer renderer in meshRenderers)
				{
					if (renderer.UsingShader == shader.Value)
					{
						// Call render if it's using this shader
						renderer.Render();
					}
				}

				// reset state for potential further draw calls
				ResetDrawState();
			}

			foreach (Behaviour ob in behaviours)
			{
				ob.PostRender();
			}
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
