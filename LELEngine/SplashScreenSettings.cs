using System;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     The splash screen of game builds, part of <see cref="ProjectSettings" />: the first image a build shows, a
	///     background color with an optional logo in the middle, drawn as soon as the window exists while the start scene
	///     loads in the background. It stays at least <see cref="Duration" /> seconds, then the start scene replaces it
	///     (once the scene has loaded, when that takes longer). The build bakes it into boot.json and Splash.bmp (see
	///     <see cref="Rendering.SplashScreen" />), so the player shows it before reading any asset.
	/// </summary>
	[Serializable]
	public sealed class SplashScreenSettings
	{
		#region PublicFields

		/// <summary>Off: no splash screen; the window stays black while the start scene loads and shows it as soon as it is ready.</summary>
		public bool Enabled = true;

		/// <summary>Seconds the splash screen stays at least (0: only while the start scene loads).</summary>
		public float Duration = 1f;

		/// <summary>Background color, shown as it is (no lighting or tonemapping).</summary>
		public Color4 BackgroundColor = new Color4(0.06f, 0.06f, 0.07f, 1f);

		/// <summary>GUID of the image drawn in the middle, over the background color by its alpha (null: the background only).</summary>
		public string Logo;

		/// <summary>The logo fits into this fraction of the window's width and height, keeping its aspect ratio.</summary>
		public float LogoSize = 0.5f;

		#endregion
	}
}
