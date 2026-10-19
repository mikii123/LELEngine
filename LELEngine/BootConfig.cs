using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

		/// <summary>The project's settings document relative to the executable (physics, lighting quality; see <see cref="LELEngine.ProjectSettings" />).</summary>
		public string ProjectSettings { get; set; } = LELEngine.ProjectSettings.FileName;

		public string Title { get; set; } = "LELEngine";
		public int Width { get; set; } = 1280;
		public int Height { get; set; } = 720;
		public bool Fullscreen { get; set; }

		/// <summary>The game keeps running while its window is not focused.</summary>
		public bool RunInBackground { get; set; } = true;

		/// <summary>What the window shows while the start scene loads (null: black, no minimum duration).</summary>
		public SplashScreenConfig SplashScreen { get; set; }

		#endregion

		#region PublicMethods

		public static BootConfig Load(string path)
		{
			if (!File.Exists(path))
			{
				return new BootConfig();
			}

			// Source-generated metadata (BootConfigJson): the player reads this before its window can open, and the
			// reflection-based serializer spends ~30 ms of JIT there.
			return JsonSerializer.Deserialize(File.ReadAllText(path), BootConfigJson.Default.BootConfig) ?? new BootConfig();
		}

		public void Save(string path)
		{
			File.WriteAllText(path, JsonSerializer.Serialize(this, BootConfigJson.Default.BootConfig));
		}

		#endregion
	}

	[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true)]
	[JsonSerializable(typeof(BootConfig))]
	internal sealed partial class BootConfigJson : JsonSerializerContext
	{ }

	/// <summary>The splash screen in boot.json, baked by the build from <see cref="SplashScreenSettings" /> (see <see cref="Rendering.SplashScreen" />).</summary>
	public sealed class SplashScreenConfig
	{
		#region PublicFields

		/// <summary>Seconds the splash screen stays at least.</summary>
		public float Duration { get; set; } = 1f;

		/// <summary>Background color, display RGB 0..1.</summary>
		public float[] BackgroundColor { get; set; } = { 0f, 0f, 0f };

		/// <summary>The logo composited over the background color: an uncompressed 24-bit BMP relative to the executable (null: no logo).</summary>
		public string Image { get; set; }

		/// <summary>The image fits into this fraction of the window's width and height.</summary>
		public float ImageSize { get; set; } = 0.5f;

		#endregion
	}
}
