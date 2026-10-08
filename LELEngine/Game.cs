using System;

namespace LELEngine
{
	public sealed class Game
	{
		#region PublicFields

		/// <summary>The standalone game host (window, renderer, game loop) created by <see cref="CreateWindow" />.</summary>
		public static GameHost Mono;

		/// <summary>
		///     Renderer that draws the game (the standalone host's, or the editor's game view). Scripts use this
		///     rather than <see cref="Mono" />, which does not exist inside the editor.
		/// </summary>
		public static Rendering.Renderer Renderer { get; set; }

		#endregion

		#region PublicMethods

		/// <summary>
		///     Creates a new window.
		///     Does not load scene.
		/// </summary>
		public static void CreateWindow(int width, int height, string title)
		{
			Console.WriteLine("LELEngine\nCopyright LELDev Studio\nInitializing...");

			// Ask for the newest OpenGL version first and step down to the 4.3 minimum when the driver refuses.
			Version version = Window.PreferredApiVersion;
			while (true)
			{
				try
				{
					Mono = new GameHost(width, height, title, version);
					return;
				}
				catch (Exception e) when (version > Window.MinimumApiVersion)
				{
					Console.WriteLine($"[Window] OpenGL {version} context unavailable ({e.GetType().Name}), trying an older version");
					version = version.Minor > 0 ? new Version(version.Major, version.Minor - 1) : Window.MinimumApiVersion;
					if (version < Window.MinimumApiVersion)
					{
						version = Window.MinimumApiVersion;
					}
				}
			}
		}

		#endregion
	}
}
