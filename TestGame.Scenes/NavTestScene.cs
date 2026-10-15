using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Navigation;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Navigation test: two touching floors (the second rotated 90 degrees, with its own surface), pillars, a wall
	///     with a gap, wandering agents and moving obstacles that carve the navmesh. Each surface bakes itself when
	///     the scene starts. Run TestGame with nav=1 (navagents=N, navdebug=1 draws the navmesh in the game).
	/// </summary>
	public static class NavTestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, int agents, bool debugDraw, bool animate)
		{
			NavMesh.DrawDebug = debugDraw;

			// Surface A: 20 x 20 floor at the origin with props as children.
			GameObject surfaceA = scene.CreateGameObject("SurfaceA");
			NavMeshSurface navA = surfaceA.AddComponent<NavMeshSurface>();
			navA.collectObjects = CollectObjects.Children;
			navA.buildSettings.AgentRadius = 0.4f;
			navA.buildSettings.AgentHeight = 1.8f;
			navA.buildSettings.AgentClimb = 0.4f;
			navA.buildSettings.VoxelSize = 0.15f;
			surfaceA.AddComponent<BakeOnStart>();

			Box(scene, "FloorA", "LitWhite.material", new Vector3(0f, -0.1f, 0f), new Vector3(10f, 0.1f, 10f), surfaceA.transform);
			Box(scene, "WallBack", "LitGray.material", new Vector3(0f, 1f, 10f), new Vector3(10f, 1f, 0.2f), surfaceA.transform);
			Box(scene, "WallLeft", "LitRed.material", new Vector3(-10f, 1f, 0f), new Vector3(0.2f, 1f, 10f), surfaceA.transform);
			// Inner wall with a gap in the middle.
			Box(scene, "InnerWall1", "LitBlue.material", new Vector3(-6f, 0.75f, 2f), new Vector3(3f, 0.75f, 0.2f), surfaceA.transform);
			Box(scene, "InnerWall2", "LitBlue.material", new Vector3(4f, 0.75f, 2f), new Vector3(4f, 0.75f, 0.2f), surfaceA.transform);
			for (int i = 0; i < 4; i++)
			{
				Box(scene, "Pillar" + i, "LitGray.material", new Vector3(-6f + i * 4f, 1f, -5f), new Vector3(0.5f, 1f, 0.5f), surfaceA.transform);
			}

			// A low step and a ramp (walkable slopes).
			Box(scene, "Step", "LitYellow.material", new Vector3(6f, 0.15f, -2f), new Vector3(2f, 0.15f, 2f), surfaceA.transform);
			GameObject ramp = Box(scene, "Ramp", "LitYellow.material", new Vector3(6f, 0.6f, 6f), new Vector3(2f, 0.1f, 2.5f), surfaceA.transform);
			ramp.transform.rotation = QuaternionHelper.Euler(-14f, 0f, 0f);

			// Surface B: a 20 x 10 floor rotated 90 degrees, touching A along x = 10.
			GameObject surfaceB = scene.CreateGameObject("SurfaceB");
			surfaceB.transform.position = new Vector3(15f, 0f, 0f);
			surfaceB.transform.rotation = QuaternionHelper.Euler(0f, 90f, 0f);
			NavMeshSurface navB = surfaceB.AddComponent<NavMeshSurface>();
			navB.collectObjects = CollectObjects.Children;
			navB.buildSettings = navA.buildSettings.Clone();
			surfaceB.AddComponent<BakeOnStart>();
			GameObject floorB = Box(scene, "FloorB", "LitGreen.material", Vector3.Zero, new Vector3(10f, 0.1f, 5f), surfaceB.transform);
			floorB.transform.localPosition = new Vector3(0f, -0.1f, 0f);
			floorB.transform.localRotation = Quaternion.Identity;
			GameObject pillarB = Box(scene, "PillarB", "LitGray.material", Vector3.Zero, new Vector3(0.6f, 1f, 0.6f), surfaceB.transform);
			pillarB.transform.localPosition = new Vector3(3f, 1f, 1f);
			pillarB.transform.localRotation = Quaternion.Identity;

			// Moving obstacles (carving).
			for (int i = 0; i < 2; i++)
			{
				GameObject mover = Box(scene, "Mover" + i, "LitRed.material", new Vector3(-3f + i * 6f, 0.75f, -2f + i * 4f), new Vector3(0.8f, 0.75f, 0.8f), null);
				NavMeshObstacle obstacle = mover.AddComponent<NavMeshObstacle>();
				obstacle.shape = NavMeshObstacleShape.Box;
				obstacle.size = new Vector3(2f, 2f, 2f);
				obstacle.carving = true;
				obstacle.carveOnlyStationary = true;
				obstacle.carvingTimeToStationary = 0.3f;
				NavObstacleMover motion = mover.AddComponent<NavObstacleMover>();
				motion.Axis = i == 0 ? Vector3.UnitX : Vector3.UnitZ;
				motion.Amplitude = 4f;
				motion.Period = 9f + i * 3f;
				motion.PauseSeconds = 2.5f;
				motion.Animate = animate;
			}

			// Agents.
			var random = new Random(7);
			for (int i = 0; i < agents; i++)
			{
				GameObject go = scene.CreateGameObject("Agent" + i);
				go.transform.position = new Vector3(-8f + (float)random.NextDouble() * 16f, 0.8f, -8f + (float)random.NextDouble() * 8f);
				go.transform.scale = new Vector3(0.3f, 0.8f, 0.3f);
				MeshRenderer renderer = go.AddComponent<MeshRenderer>();
				renderer.SetMaterial(i % 3 == 0 ? "LitBlue.material" : i % 3 == 1 ? "LitYellow.material" : "LitGreen.material");
				renderer.SetMesh("cube.obj");
				renderer.ContributesToGI = false;
				NavMeshAgent agent = go.AddComponent<NavMeshAgent>();
				agent.radius = 0.35f;
				agent.height = 1.6f;
				agent.baseOffset = 0.8f;
				agent.speed = 2.5f + (float)random.NextDouble() * 1.5f;
				agent.acceleration = 10f;
				agent.stoppingDistance = 0.2f;
				NavWanderer wanderer = go.AddComponent<NavWanderer>();
				wanderer.Seed = i * 131 + 7;
				wanderer.Animate = animate;
			}

			// Camera above the scene.
			GameObject cameraObject = scene.CreateGameObject("MainCamera");
			cameraObject.transform.position = new Vector3(4f, 22f, -16f);
			cameraObject.transform.rotation = QuaternionHelper.LookRotation((new Vector3(4f, 0f, 1f) - cameraObject.transform.position).Normalized(), Vector3.UnitY);
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 60f;
			camera.NearClip = 0.1f;
			camera.FarClip = 300f;
			cameraObject.AddComponent<NavStats>();

			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(new Vector3(-0.45f, -0.75f, 0.35f).Normalized(), Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			sun.Strength = 1.25f;

			Lighting.Environment.AmbientColor = new Color4(0.55f, 0.6f, 0.75f, 1f);
			Lighting.Environment.AmbientStrength = 0.25f;
			Lighting.Environment.BackgroundColor = new Color4(0.45f, 0.6f, 0.85f, 1f);
			Lighting.Shadows.Enabled = true;
			Lighting.Shadows.Distance = 40f;
			Lighting.GI.Enabled = false;
		}

		#endregion

		#region PrivateMethods

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize, Transform parent)
		{
			GameObject go = scene.CreateGameObject(name);
			if (parent != null)
			{
				go.transform.SetParent(parent, false);
			}

			go.transform.position = center;
			go.transform.localScale = halfSize;
			MeshRenderer renderer = go.AddComponent<MeshRenderer>();
			renderer.SetMaterial(material);
			renderer.SetMesh("cube.obj");
			renderer.ContributesToGI = false;
			return go;
		}

		#endregion
	}

	/// <summary>Bakes the NavMeshSurface on the same object when the scene starts (scenes built in code).</summary>
	public sealed class BakeOnStart : Behaviour
	{
		public override void Start()
		{
			NavMeshSurface surface = GetComponent<NavMeshSurface>();
			if (surface != null && (surface.NavMeshData == null || surface.NavMeshData.IsEmpty))
			{
				surface.BuildNavMesh();
			}
		}
	}

	/// <summary>Sends the agent to random navmesh points, picking a new one when it arrives or fails.</summary>
	public sealed class NavWanderer : Behaviour
	{
		public int Seed;
		public float IdleSeconds = 0.5f;
		public bool Animate = true;
		public int Arrivals { get; private set; }

		private NavMeshAgent agent;
		private Random random;
		private float idle;
		private bool counted;

		public override void Start()
		{
			agent = GetComponent<NavMeshAgent>();
			random = new Random(Seed);
		}

		public override void Update()
		{
			if (agent == null || !Animate)
			{
				return;
			}

			if (!agent.isOnNavMesh)
			{
				return;
			}

			if (agent.pathPending)
			{
				return;
			}

			bool arrived = agent.hasPath && agent.remainingDistance <= agent.stoppingDistance + 0.05f;
			if (!agent.hasPath || arrived || agent.isPathStale)
			{
				if (arrived && !counted)
				{
					counted = true;
					Arrivals++;
				}

				idle += Time.deltaTime;
				if (idle < IdleSeconds)
				{
					return;
				}

				idle = 0f;
				if (NavMesh.FindRandomPoint(out Vector3 point))
				{
					counted = false;
					agent.SetDestination(point);
				}
			}
		}
	}

	/// <summary>Moves an object back and forth along an axis, pausing at the ends (carving obstacles).</summary>
	public sealed class NavObstacleMover : Behaviour
	{
		public Vector3 Axis = Vector3.UnitX;
		public float Amplitude = 3f;
		public float Period = 8f;
		public float PauseSeconds = 2f;
		public bool Animate = true;

		private Vector3 origin;
		private float clock;

		public override void Start()
		{
			origin = transform.position;
		}

		public override void Update()
		{
			if (!Animate)
			{
				return;
			}

			clock += Time.deltaTime;
			float cycle = Period + PauseSeconds * 2f;
			float t = clock % cycle;
			float offset;
			if (t < PauseSeconds)
			{
				offset = -1f;
			}
			else if (t < PauseSeconds + Period * 0.5f)
			{
				offset = -1f + (t - PauseSeconds) / (Period * 0.5f) * 2f;
			}
			else if (t < PauseSeconds * 2f + Period * 0.5f)
			{
				offset = 1f;
			}
			else
			{
				offset = 1f - (t - PauseSeconds * 2f - Period * 0.5f) / (Period * 0.5f) * 2f;
			}

			transform.position = origin + Axis * (offset * Amplitude);
		}
	}

	/// <summary>Prints navigation statistics every second.</summary>
	public sealed class NavStats : Behaviour
	{
		public float Interval = 1f;
		private float timer;
		private int frames;

		public override void Update()
		{
			timer += Time.deltaTime;
			frames++;
			if (timer < Interval)
			{
				return;
			}

			int agents = 0, onMesh = 0, moving = 0, pending = 0, failed = 0, arrivals = 0;
			foreach (NavMeshAgent agent in gameObject.scene.FindObjectsOfType<NavMeshAgent>())
			{
				agents++;
				if (agent.isOnNavMesh) onMesh++;
				if (agent.velocity.Length > 0.1f) moving++;
				if (agent.pathPending) pending++;
				if (agent.isPathStale) failed++;
				NavWanderer wanderer = agent.GetComponent<NavWanderer>();
				if (wanderer != null) arrivals += wanderer.Arrivals;
			}

			int carved = 0;
			foreach (NavMeshObstacle obstacle in gameObject.scene.FindObjectsOfType<NavMeshObstacle>())
			{
				if (obstacle.IsCarving) carved++;
			}

			Console.WriteLine($"[Nav] {frames / timer:0} fps | nav step {NavMesh.LastUpdateMilliseconds:0.00} ms | surfaces {NavMesh.SurfaceCount} | agents {agents}: on navmesh {onMesh}, moving {moving}, pending {pending}, failed {failed}, arrivals {arrivals} | carved obstacles {carved}");
			timer = 0f;
			frames = 0;
		}
	}
}
