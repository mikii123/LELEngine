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
		///     Usage: TestGame [ship]
		///     Default: GI test room with FPS controller. "ship": the original ship demo.
		/// </summary>
		private static void Main(string[] args)
		{
			Game.CreateWindow(1280, 720, "LELEngine");

			bool shipDemo = Array.Exists(args, a => string.Equals(a, "ship", StringComparison.OrdinalIgnoreCase));
			if (shipDemo)
			{
				ShipScene.Load();
			}
			else
			{
				GITestScene.Load(Game.Mono.LoadEmptyScene());
			}

			Game.Mono.InitializeECSScope(Assembly.GetExecutingAssembly());
			ECSManager manager = Game.Mono.ECSManager;
			ECSEntity ecsEntity = manager.CreateEntity();
			manager.SetComponent(ecsEntity, new FrameRateCounterComponent());

			Game.Mono.Run();
			// Main function is frozen until game window closes
		}

		#endregion
	}
}
