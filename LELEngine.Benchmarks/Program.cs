using System;
using System.Linq;

namespace LELEngine.Benchmarks
{
	/// <summary>
	///     Headless benchmarks: <c>dotnet run --project LELEngine.Benchmarks -c Release -- physics2d [scenario] [steps=N]</c>.
	///     Scenarios: pyramid, pyramids, rain, chains, queries, rays, closest, overlaps (all when none is named).
	/// </summary>
	public static class Program
	{
		public static int Main(string[] args)
		{
			if (args.Length > 0 && args[0] == "physics2d-scene")
			{
				RunPhysicsScene();
				return 0;
			}

			if (args.Length == 0 || args[0] != "physics2d")
			{
				Console.WriteLine("usage: LELEngine.Benchmarks physics2d [pyramid|pyramids|rain|chains|queries|rays|closest|overlaps] [steps=600] [workers=1] [nosleep=1] [plane=xz] | physics2d-scene");
				return 1;
			}

			string only = args.Skip(1).FirstOrDefault(a => !a.Contains('='));
			string stepsArg = args.FirstOrDefault(a => a.StartsWith("steps="));
			int steps = stepsArg != null ? int.Parse(stepsArg.Substring(6)) : 600;
			string workersArg = args.FirstOrDefault(a => a.StartsWith("workers="));
			Physics2DBenchmark.Workers = workersArg != null ? int.Parse(workersArg.Substring(8)) : 1;
			Physics2DBenchmark.NoSleep = args.Contains("nosleep=1");
			Physics2DBenchmark.Plane = args.Contains("plane=xz") ? SimulationPlane2D.XZ : SimulationPlane2D.XY;
			Physics2DBenchmark.Run(only, steps);
			return 0;
		}

		/// <summary>
		///     The Box2D test scene of the test game (stack, ball, pendulum, chain, capsules, spring, platform, trigger,
		///     queries) run without a window for 4 seconds of 60 Hz frames: its harness logs the callbacks and a report.
		/// </summary>
		private static void RunPhysicsScene()
		{
			Scene scene = SceneManager.CreateScene("Physics2D");
			SceneManager.SetActiveScene(scene);
			Physics2D.simulationMode = SimulationMode2D.FixedUpdate;
			TestGame.Scenes.Physics2DTestScene.Load(scene, true);
			scene.StartLifecycle(true);
			const double frame = 1.0 / 60.0;
			for (int i = 0; i < 240; i++)
			{
				Time.deltaTimeD = frame;
				Time.timeD += frame;
				scene.Update((float)frame);
			}
		}
	}
}
