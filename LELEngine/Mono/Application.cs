using System;
using System.IO;

namespace LELEngine
{
	/// <summary>The running game as a whole (Unity's Application): quitting, where it runs, where it keeps its data.</summary>
	public static class Application
	{
		private static string persistentPath;

		/// <summary>True while a game host runs the scenes (also in the editor's play mode).</summary>
		public static bool isPlaying => Game.Mono != null || SceneManager.ActiveScene != null;

		/// <summary>
		///     The folder the game keeps its own files in between runs (<see cref="PlayerPrefs" />, remapped controls, saves),
		///     on disk, never the registry. A built game: the user's local app data, LELEngine/&lt;game title&gt;
		///     (<see cref="PersistentDataPathFor" />, set by the player from boot.json; its log and shader cache live there
		///     too). The editor: the project's own PersistentData folder beside Assets (not in git), so play mode never
		///     reads or writes a built game's data. Other hosts: LELEngine/&lt;executable's name&gt; in the local app data.
		///     The folder is made when something writes into it. Hosts set it before the game's scripts run; a change
		///     saves what <see cref="PlayerPrefs" /> holds to the old folder and reads the new one when next asked.
		/// </summary>
		public static string persistentDataPath
		{
			get => persistentPath ??= PersistentDataPathFor(ExecutableName());
			set
			{
				string path = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
				if (string.Equals(path, persistentPath, StringComparison.OrdinalIgnoreCase))
				{
					return;
				}

				PlayerPrefs.Save();
				persistentPath = path;
				PlayerPrefs.Unload();
			}
		}

		/// <summary>A built game's <see cref="persistentDataPath" />: LELEngine/&lt;title&gt; in the user's local app data.</summary>
		public static string PersistentDataPathFor(string title)
		{
			string name = string.IsNullOrWhiteSpace(title) ? "Game" : title.Trim();
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(invalid, '_');
			}

			return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LELEngine", name);
		}

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

		private static string ExecutableName()
		{
			try
			{
				string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
				return string.IsNullOrEmpty(name) ? "Game" : name;
			}
			catch (Exception)
			{
				return "Game";
			}
		}
	}
}
