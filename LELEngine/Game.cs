using System;

namespace LELEngine
{
	public sealed class Game
	{
		#region PublicFields

		/// <summary>The standalone game host (window, renderer, game loop) created by <see cref="CreateWindow(int, int, string)" />.</summary>
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
			CreateWindow(new GameWindowOptions { Width = width, Height = height, Title = title });
		}

		/// <summary>Creates a new window as <paramref name="options" /> say (hidden, fullscreen, without its renderer yet). Does not load a scene.</summary>
		public static void CreateWindow(GameWindowOptions options)
		{
			Console.WriteLine("LELEngine\nCopyright LELDev Studio\nInitializing...");

			// Ask for the newest OpenGL version first and step down to the 4.3 minimum when the driver refuses.
			Version version = Window.PreferredApiVersion;
			while (true)
			{
				try
				{
					Mono = new GameHost(options, version);
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

	/// <summary>How <see cref="Game.CreateWindow(GameWindowOptions)" /> opens the game window.</summary>
	public sealed class GameWindowOptions
	{
		#region PublicFields

		/// <summary>Client size of a windowed window.</summary>
		public int Width = 1280;

		public int Height = 720;
		public string Title = "LELEngine";

		/// <summary>Opens fullscreen on the primary monitor, at its current resolution.</summary>
		public bool Fullscreen;

		/// <summary>
		///     False: the window opens hidden and appears when <c>IsVisible</c> is set, so its first image can be drawn
		///     before anyone sees it (the player's splash screen). A fullscreen window always opens visible.
		/// </summary>
		public bool Visible = true;

		/// <summary>
		///     False: the window opens without its renderer (the engine's shaders are not compiled yet);
		///     <see cref="GameHost.CreateRenderer" /> creates it later, on the thread that holds the GL context then.
		/// </summary>
		public bool CreateRenderer = true;

		#endregion
	}
}
