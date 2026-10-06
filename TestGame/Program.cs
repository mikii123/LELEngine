using System;
using System.Reflection;
using LELCS;
using LELCS.Model;
using LELEngine;
using TestGame.Scenes;

namespace TestGame
{
	internal class Program
	{
		#region PrivateMethods

		/// <summary>
		///     Usage: TestGame [ship] [gi=0|1] [voxels=0|1] [emitters=0|1] [stats=0|1]
		///     Default: GI test room with FPS controller. "ship": the original ship demo.
		/// </summary>
		private static void Main(string[] args)
		{
			Game.CreateWindow(1280, 720, "LELEngine");

			bool shipDemo = HasFlag(args, "ship");
			if (shipDemo)
			{
				ShipScene.Load();
			}
			else
			{
				GITestScene.Options options = new GITestScene.Options
				{
					GI = GetBool(args, "gi", true),
					VoxelView = GetBool(args, "voxels", false),
					Emitters = GetBool(args, "emitters", true),
					Stats = GetBool(args, "stats", false)
				};
				GITestScene.Load(Game.Mono.LoadEmptyScene(), options);
			}

			Game.Mono.InitializeECSScope(Assembly.GetExecutingAssembly());
			ECSManager manager = Game.Mono.ECSManager;
			ECSEntity ecsEntity = manager.CreateEntity();
			manager.SetComponent(ecsEntity, new FrameRateCounterComponent());

			Game.Mono.Run();
			// Main function is frozen until game window closes
		}

		private static bool HasFlag(string[] args, string flag)
		{
			return Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
		}

		// Parses "name=0" / "name=1" style switches.
		private static bool GetBool(string[] args, string name, bool defaultValue)
		{
			foreach (string arg in args)
			{
				int eq = arg.IndexOf('=');
				if (eq <= 0 || !string.Equals(arg.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string value = arg.Substring(eq + 1).Trim();
				return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "on", StringComparison.OrdinalIgnoreCase);
			}

			return defaultValue;
		}

		#endregion
	}
}
