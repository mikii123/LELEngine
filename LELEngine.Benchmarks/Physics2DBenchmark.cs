using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LELEngine;
using OpenTK.Mathematics;

namespace LELEngine.Benchmarks
{
	/// <summary>
	///     2D physics through the engine's own API (Rigidbody2D, colliders, joints, Physics2D queries), with no window:
	///     each scenario builds a scene, steps it at 60 Hz by hand (<see cref="SimulationMode2D.Script" />) and times every
	///     step. Whatever backs PhysicsScene2D is measured as a game gets it, wrapper included. Each scenario runs once
	///     to warm the JIT, then is built again and measured.
	/// </summary>
	public static class Physics2DBenchmark
	{
		#region PrivateFields

		private const float Step = 1f / 60f;

		/// <summary>Bodies never sleep: every step simulates everything (the solver's cost, not the sleeping's).</summary>
		public static bool NoSleep;

		/// <summary>Box2D worker threads (Physics2DSettings.WorkerCount).</summary>
		public static int Workers = 1;

		/// <summary>
		///     The simulation plane. The scenes are the same in both (2D y along world Y in XY, along world Z in XZ),
		///     so every 2D result must match; the transform check compares the transforms with the bodies.
		/// </summary>
		public static SimulationPlane2D Plane = SimulationPlane2D.XY;

		#endregion

		#region PublicMethods

		public static void Run(string only, int steps)
		{
			var scenarios = new (string name, Action<Scene> build, Func<Scene, string> check, Action perStep)[]
			{
				("pyramid", BuildPyramid, CheckPyramid, null),
				("pyramids", BuildPyramids, CheckPyramid, null),
				("rain", BuildRain, CheckRain, null),
				("chains", BuildChains, CheckChains, null),
				("queries", BuildRain, null, Queries),
				("rays", BuildRain, null, Rays),
				("closest", BuildRain, null, ClosestRays),
				("overlaps", BuildRain, null, Overlaps),
			};

			Console.WriteLine($"2D physics benchmark, {steps} steps at 60 Hz, {Environment.ProcessorCount} logical CPUs, .NET {Environment.Version}, workers {Workers}, sleep {(NoSleep ? "off" : "on")}, plane {Plane}");
			foreach (var scenario in scenarios)
			{
				if (only != null && only != scenario.name)
				{
					continue;
				}

				Measure(scenario.name, scenario.build, scenario.check, scenario.perStep, Math.Min(steps, 120), false);
				Measure(scenario.name, scenario.build, scenario.check, scenario.perStep, steps, true);
			}
		}

		#endregion

		#region PrivateMethods

		private static void Measure(string name, Action<Scene> build, Func<Scene, string> check, Action perStep, int steps, bool report)
		{
			Scene scene = SceneManager.CreateScene(name);
			SceneManager.SetActiveScene(scene);
			Physics2DSettings settings = ProjectSettings.Current.Physics2D;
			settings.Gravity = new Vector2(0f, -10f);
			settings.WorkerCount = Workers;
			settings.SimulationPlane = Plane;
			Physics2D.simulationMode = SimulationMode2D.Script;
			build(scene);
			scene.StartLifecycle(true);

			// The queries scenario lets the rain settle first and times only the queries.
			if (perStep != null)
			{
				for (int i = 0; i < 300; i++)
				{
					Physics2D.Simulate(Step);
				}
			}

			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			var times = new double[steps];
			long allocated = GC.GetAllocatedBytesForCurrentThread();
			int gen0 = GC.CollectionCount(0);
			var watch = new Stopwatch();
			for (int i = 0; i < steps; i++)
			{
				watch.Restart();
				if (perStep != null)
				{
					perStep();
				}
				else
				{
					Physics2D.Simulate(Step);
				}

				watch.Stop();
				times[i] = watch.Elapsed.TotalMilliseconds;
			}

			allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
			gen0 = GC.CollectionCount(0) - gen0;

			if (report)
			{
				double[] sorted = times.OrderBy(t => t).ToArray();
				int bodies = scene.GameObjects.Count(g => g.GetComponent<Rigidbody2D>() != null && g.GetComponent<Rigidbody2D>().bodyType == RigidbodyType2D.Dynamic);
				Console.WriteLine($"{name,-8} bodies {bodies,5}  mean {times.Average(),7:0.000} ms  median {sorted[steps / 2],7:0.000}  p95 {sorted[(int)(steps * 0.95)],7:0.000}  max {sorted[^1],7:0.000}  " +
					$"first100 {times.Take(100).Average(),7:0.000}  total {times.Sum() / 1000,6:0.00} s  alloc {allocated / (double)steps / 1024,7:0.0} KB/step  gen0 {gen0}" +
					(check != null ? "  | " + check(scene) + "  | " + CheckTransforms(scene) : ""));
			}

			SceneManager.SetActiveScene(null);
			scene.Physics2DIfCreated?.Dispose();
		}

		private static GameObject Body(Scene scene, string name, Vector2 position, RigidbodyType2D type)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = World(position, 0.5f);
			Rigidbody2D body = go.AddComponent<Rigidbody2D>();
			body.bodyType = type;
			if (NoSleep)
			{
				body.sleepMode = RigidbodySleepMode2D.NeverSleep;
			}

			return go;
		}

		private static void StaticBox(Scene scene, Vector2 center, Vector2 size)
		{
			GameObject go = Body(scene, "Static", center, RigidbodyType2D.Static);
			go.AddComponent<BoxCollider2D>().size = size;
		}

		// ---------------------------------------------------------------- pyramid

		private const int PyramidRows = 40;

		private static void BuildPyramid(Scene scene)
		{
			StaticBox(scene, new Vector2(0f, -0.5f), new Vector2(100f, 1f));
			for (int row = 0; row < PyramidRows; row++)
			{
				int count = PyramidRows - row;
				for (int i = 0; i < count; i++)
				{
					float x = (i - (count - 1) * 0.5f) * 1.05f;
					GameObject box = Body(scene, "Box", new Vector2(x, 0.5f + row * 1.0f), RigidbodyType2D.Dynamic);
					box.AddComponent<BoxCollider2D>().size = new Vector2(1f, 1f);
				}
			}
		}

		/// <summary>Eight pyramids of 30 rows side by side (3720 boxes): many independent islands, room for worker threads.</summary>
		private static void BuildPyramids(Scene scene)
		{
			StaticBox(scene, new Vector2(0f, -0.5f), new Vector2(400f, 1f));
			for (int p = 0; p < 8; p++)
			{
				float center = -168f + p * 48f;
				for (int row = 0; row < 30; row++)
				{
					int count = 30 - row;
					for (int i = 0; i < count; i++)
					{
						float x = center + (i - (count - 1) * 0.5f) * 1.05f;
						GameObject box = Body(scene, "Box", new Vector2(x, 0.5f + row * 1.0f), RigidbodyType2D.Dynamic);
						box.AddComponent<BoxCollider2D>().size = new Vector2(1f, 1f);
					}
				}
			}
		}

		private static string CheckPyramid(Scene scene)
		{
			// Standing: the top box still near its row height; asleep: the stack came to rest.
			Rigidbody2D[] boxes = Dynamic(scene);
			float top = boxes.Max(b => b.position.Y);
			int awake = boxes.Count(b => b.IsAwake());
			return $"top box y {top:0.00}, awake {awake}";
		}

		// ---------------------------------------------------------------- rain

		private const int RainColumns = 50;
		private const int RainRows = 40;

		private static void BuildRain(Scene scene)
		{
			StaticBox(scene, new Vector2(0f, -0.5f), new Vector2(42f, 1f));
			StaticBox(scene, new Vector2(-20.5f, 30f), new Vector2(1f, 60f));
			StaticBox(scene, new Vector2(20.5f, 30f), new Vector2(1f, 60f));
			var random = new Random(7);
			for (int row = 0; row < RainRows; row++)
			{
				for (int column = 0; column < RainColumns; column++)
				{
					var position = new Vector2(-19.5f + column * 0.78f + (float)random.NextDouble() * 0.1f, 2f + row * 0.9f);
					GameObject body = Body(scene, "Rain", position, RigidbodyType2D.Dynamic);
					switch ((row + column) % 3)
					{
						case 0:
							body.AddComponent<CircleCollider2D>().radius = 0.3f;
							break;
						case 1:
							body.AddComponent<BoxCollider2D>().size = new Vector2(0.6f, 0.5f);
							break;
						default:
							CapsuleCollider2D capsule = body.AddComponent<CapsuleCollider2D>();
							capsule.size = new Vector2(0.4f, 0.75f);
							break;
					}
				}
			}
		}

		private static string CheckRain(Scene scene)
		{
			// Nothing through the floor or out of the box; the pile's height says how much it settled.
			Rigidbody2D[] bodies = Dynamic(scene);
			int lost = bodies.Count(b => b.position.Y < -0.2f || Math.Abs(b.position.X) > 20.2f);
			return $"lost {lost}, mean height {bodies.Average(b => b.position.Y):0.00}, highest {bodies.Max(b => b.position.Y):0.00}, awake {bodies.Count(b => b.IsAwake())}";
		}

		// ---------------------------------------------------------------- chains

		private const int Chains = 30;
		private const int Links = 40;

		private static void BuildChains(Scene scene)
		{
			// Chains far enough apart (wider than a chain is long) that they never touch each other.
			for (int c = 0; c < Chains; c++)
			{
				float x0 = -330f + c * 22f;
				GameObject anchor = Body(scene, "Anchor", new Vector2(x0, 40f), RigidbodyType2D.Static);
				Rigidbody2D previous = anchor.GetComponent<Rigidbody2D>();
				for (int i = 0; i < Links; i++)
				{
					// Laid out sideways so the chains swing down.
					GameObject link = Body(scene, "Link", new Vector2(x0 + 0.25f + i * 0.5f, 40f), RigidbodyType2D.Dynamic);
					link.AddComponent<BoxCollider2D>().size = new Vector2(0.5f, 0.125f);
					HingeJoint2D hinge = link.AddComponent<HingeJoint2D>();
					hinge.connectedBody = previous;
					hinge.anchor = new Vector2(-0.25f, 0f);
					previous = link.GetComponent<Rigidbody2D>();
				}
			}
		}

		private static string CheckChains(Scene scene)
		{
			// The chains hang (lowest link about Links * 0.5 below the anchor) without stretching apart.
			Rigidbody2D[] links = Dynamic(scene);
			return $"lowest link y {links.Min(b => b.position.Y):0.00} (anchor 40, length {Links * 0.5f:0.0})";
		}

		// ---------------------------------------------------------------- queries

		private static readonly List<RaycastHit2D> rayHits = new List<RaycastHit2D>();
		private static readonly List<Collider2D> overlapHits = new List<Collider2D>();
		private static int queryRound;

		private static void Queries()
		{
			// 500 rays down through the pile and 500 circle overlaps inside it, no allocation per query.
			ContactFilter2D filter = ContactFilter2D.NoFilter();
			queryRound++;
			for (int i = 0; i < 500; i++)
			{
				float x = -19.8f + (i * 39.6f / 500f) + (queryRound % 7) * 0.01f;
				Physics2D.Raycast(new Vector2(x, 60f), new Vector2(0f, -1f), filter, rayHits, 70f);
				Physics2D.OverlapCircle(new Vector2(x, 3f + (i % 10)), 0.75f, filter, overlapHits);
			}
		}

		/// <summary>1000 rays down through the pile, every hit.</summary>
		private static void Rays()
		{
			ContactFilter2D filter = ContactFilter2D.NoFilter();
			queryRound++;
			for (int i = 0; i < 1000; i++)
			{
				float x = -19.8f + (i * 39.6f / 1000f) + (queryRound % 7) * 0.01f;
				Physics2D.Raycast(new Vector2(x, 60f), new Vector2(0f, -1f), filter, rayHits, 70f);
			}
		}

		/// <summary>1000 rays down onto the pile, the first hit only (the common game raycast).</summary>
		private static void ClosestRays()
		{
			queryRound++;
			for (int i = 0; i < 1000; i++)
			{
				float x = -19.8f + (i * 39.6f / 1000f) + (queryRound % 7) * 0.01f;
				Physics2D.Raycast(new Vector2(x, 60f), new Vector2(0f, -1f), 70f);
			}
		}

		/// <summary>1000 circle overlaps inside the pile.</summary>
		private static void Overlaps()
		{
			ContactFilter2D filter = ContactFilter2D.NoFilter();
			queryRound++;
			for (int i = 0; i < 1000; i++)
			{
				float x = -19.8f + (i * 39.6f / 1000f) + (queryRound % 7) * 0.01f;
				Physics2D.OverlapCircle(new Vector2(x, 3f + (i % 10)), 0.75f, filter, overlapHits);
			}
		}

		private static Rigidbody2D[] Dynamic(Scene scene)
		{
			return scene.GameObjects.Select(g => g.GetComponent<Rigidbody2D>()).Where(b => b != null && b.bodyType == RigidbodyType2D.Dynamic).ToArray();
		}

		/// <summary>A 2D point in the world: (x, y, depth) in the XY plane, (x, depth, y) in the XZ plane.</summary>
		private static Vector3 World(Vector2 position, float depth)
		{
			return Plane == SimulationPlane2D.XZ ? new Vector3(position.X, depth, position.Y) : new Vector3(position.X, position.Y, depth);
		}

		/// <summary>
		///     A checksum of the bodies' 2D state (equal bits in both planes) and the transforms against the bodies: the
		///     position in the plane, the depth kept, rotated about the plane's normal only.
		/// </summary>
		private static string CheckTransforms(Scene scene)
		{
			Vector3 normal = Plane == SimulationPlane2D.XZ ? Vector3.UnitY : Vector3.UnitZ;
			float position = 0f;
			float tilt = 0f;
			float turn = 0f;
			double state = 0.0;
			foreach (Rigidbody2D body in Dynamic(scene))
			{
				Transform t = body.transform;
				state += body.position.X + 3.0 * body.position.Y + 7.0 * body.rotation + 11.0 * body.velocity.X + 13.0 * body.velocity.Y;
				position = Math.Max(position, (t.position - World(body.position, 0.5f)).Length);
				// The local normal stays the world normal; the local x axis is turned by the body's rotation about it.
				tilt = Math.Max(tilt, (t.rotation * normal - normal).Length);
				float radians = body.rotation * MathF.PI / 180f;
				Vector3 right = World(new Vector2(MathF.Cos(radians), MathF.Sin(radians)), 0f);
				turn = Math.Max(turn, (t.rotation * Vector3.UnitX - right).Length);
			}

			return $"state {state:R}  transform error: position {position:0.0e0}, tilt {tilt:0.0e0}, turn {turn:0.0e0}";
		}

		#endregion
	}
}
