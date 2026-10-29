namespace LELEngine
{
	/// <summary>The running game as a whole (Unity's Application): quitting, where it runs.</summary>
	public static class Application
	{
		/// <summary>True while a game host runs the scenes (also in the editor's play mode).</summary>
		public static bool isPlaying => Game.Mono != null || SceneManager.ActiveScene != null;

		/// <summary>
		///     Closes the game window at the end of the frame. In the editor (no game host) this only logs: play mode is
		///     left through the editor's controls.
		/// </summary>
		public static void Quit()
		{
			GameHost host = Game.Mono;
			if (host == null)
			{
				Debug.Log("[Application] Quit requested (ignored in the editor)");
				return;
			}

			host.Close();
		}
	}
}
