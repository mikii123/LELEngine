using System;
using System.Threading;
using System.Diagnostics;
using System.IO;

namespace LELEngine
{
    using OpenTK;
    public sealed class Game
    {
        public static MonoBahaviour MainWindow;

        /// <summary>
        /// Creates a new window.
        /// Does not load scene.
        /// </summary>
        public static void CreateWindow(int width, int height, string title)
        {
            MainWindow = new MonoBahaviour(width, height, title);
        }
    }
}
