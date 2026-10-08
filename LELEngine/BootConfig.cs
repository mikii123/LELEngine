using System;
using System.IO;
using System.Text.Json;

namespace LELEngine
{
	/// <summary>
	///     boot.json of a game build, read by LELEngine.Player: where the assets are, which assembly holds the game
	///     scripts, the first scene and the window. Written by the editor's build pipeline.
	/// </summary>
	public sealed class BootConfig
	{
		#region PublicFields

		public const string FileName = "boot.json";

		/// <summary>Asset root relative to the executable.</summary>
		public string DataFolder { get; set; } = "Data";

		/// <summary>Game scripts assembly relative to the executable (null: no scripts).</summary>
		public string GameAssembly { get; set; } = "Game.dll";

		/// <summary>Asset path (relative to the data folder) of the scene loaded at startup.</summary>
		public string StartScene { get; set; }

		public string Title { get; set; } = "LELEngine";
		public int Width { get; set; } = 1280;
		public int Height { get; set; } = 720;
		public bool Fullscreen { get; set; }

		/// <summary>The game keeps running while its window is not focused.</summary>
		public bool RunInBackground { get; set; } = true;

		#endregion

		#region PublicMethods

		public static BootConfig Load(string path)
		{
			if (!File.Exists(path))
			{
				return new BootConfig();
			}

			return JsonSerializer.Deserialize<BootConfig>(File.ReadAllText(path), Options) ?? new BootConfig();
		}

		public void Save(string path)
		{
			File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
		}

		#endregion

		#region PrivateFields

		private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNameCaseInsensitive = true
		};

		#endregion
	}
}
