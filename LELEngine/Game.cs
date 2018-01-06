using System;

namespace LELEngine
{
    public sealed class Game
    {
        public static MonoBehaviour Mono;

        /// <summary>
        /// Creates a new window.
        /// Does not load scene.
        /// </summary>
        public static void CreateWindow(int width, int height, string title)
        {
			Console.WriteLine("LELEngine\nCopyright LELDev Studio\nInitializing...");
			Mono = new MonoBehaviour(width, height, title);
        }
    }
}
