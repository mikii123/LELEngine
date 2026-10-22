using System;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>
	///     Project-wide settings: the names of the user layers, 2D physics, the lighting quality (the shadow map and the
	///     global illumination method with its tunables) and the splash screen of builds. One document: ProjectSettings/ProjectSettings.asset in a
	///     project (written by the editor's Project Settings window), next to boot.json in a game build. Per-scene
	///     lighting (environment, shadow reach, the GI volume) stays in <see cref="SceneSettings" />. <see cref="Use" />
	///     makes a settings object the live one (<see cref="Current" />, <see cref="Lighting.Shadows" />,
	///     <see cref="Lighting.GI" />).
	/// </summary>
	[Serializable]
	public sealed class ProjectSettings
	{
		#region PublicFields

		public const string FileName = "ProjectSettings.asset";

		/// <summary>The live settings (defaults until a project or a build loads its own).</summary>
		public static ProjectSettings Current { get; private set; } = new ProjectSettings();

		/// <summary>Names of the user layers 8..31 (<see cref="LayerMask.LayerToName" /> reads them from the live settings).</summary>
		public LayerSettings Layers = new LayerSettings();

		public Physics2DSettings Physics2D = new Physics2DSettings();
		public ShadowSettings Shadows = new ShadowSettings();
		public GlobalIlluminationSettings GI = new GlobalIlluminationSettings();

		/// <summary>What a build shows while its start scene loads (baked into the build's boot.json; the player does not read it from here).</summary>
		public SplashScreenSettings SplashScreen = new SplashScreenSettings();

		/// <summary>The audio system: the audio project, master volume, voice count, device buffer, banks loaded at start.</summary>
		public LELEngine.Audio.AudioSettings Audio = new LELEngine.Audio.AudioSettings();

		/// <summary>The input system: the configuration the game's players read their actions through.</summary>
		public InputSystem.InputProjectSettings Input = new InputSystem.InputProjectSettings();

		#endregion

		#region PublicMethods

		/// <summary>Makes <paramref name="settings" /> the live settings (null: defaults).</summary>
		public static ProjectSettings Use(ProjectSettings settings)
		{
			Current = settings ?? new ProjectSettings();
			Current.GI.InvalidateStatic();
			return Current;
		}

		/// <summary>Reads the settings document at <paramref name="path" /> (defaults when it does not exist) and uses it.</summary>
		public static ProjectSettings Load(string path)
		{
			var settings = new ProjectSettings();
			if (File.Exists(path))
			{
				try
				{
					settings = Documents.ReadObject(File.ReadAllBytes(path), typeof(ProjectSettings), settings) as ProjectSettings ?? settings;
				}
				catch (Exception e)
				{
					Debug.LogError($"[ProjectSettings] Cannot read {path}: {e.Message}; using the defaults");
				}
			}

			return Use(settings);
		}

		/// <summary>Writes the settings as a document (readable JSON unless <paramref name="format" /> says otherwise).</summary>
		public void Save(string path, DocumentFormat? format = null)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
			Documents.WriteObjectFile(path, this, format);
		}

		#endregion
	}
}
