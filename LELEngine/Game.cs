using System;

namespace LELEngine
{
	public sealed class Game
	{
		#region PublicFields

		public static MonoBehaviour Mono;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Creates a new window.
		///     Does not load scene.
		/// </summary>
		public static void CreateWindow(int width, int height, string title)
		{
			Console.WriteLine("LELEngine\nCopyright LELDev Studio\nInitializing...");
			Mono = new MonoBehaviour(width, height, title);
		}

		#endregion
	}
}
