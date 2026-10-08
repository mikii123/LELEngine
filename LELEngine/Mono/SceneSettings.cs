using System;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Per-scene look: environment (ambient, specular, background, exposure), shadows and global illumination.
	///     Saved with the scene. <see cref="Activate" /> makes these objects the live settings
	///     (<see cref="Lighting.Environment" />, <see cref="Lighting.Shadows" />, <see cref="Lighting.GI" />), so
	///     edits made at runtime or in the editor apply to the scene directly.
	/// </summary>
	[Serializable]
	public sealed class SceneSettings
	{
		#region PublicFields

		public EnvironmentSettings Environment = new EnvironmentSettings();
		public ShadowSettings Shadows = new ShadowSettings();
		public GlobalIlluminationSettings GI = new GlobalIlluminationSettings();

		#endregion

		#region PublicMethods

		public void Activate()
		{
			Lighting.Environment = Environment;
			Lighting.Shadows = Shadows;
			Lighting.GI = GI;
			GI.InvalidateStatic();
		}

		#endregion
	}

	/// <summary>Ambient and specular terms, background color and the camera exposure / tonemapping.</summary>
	[Serializable]
	public sealed class EnvironmentSettings
	{
		#region PublicFields

		public Color4 AmbientColor = Color4.White;
		public float AmbientStrength;

		public float SpecularStrength;
		public float SpecularShine;

		/// <summary>Clear color of the scene target (what the camera sees where nothing is drawn).</summary>
		public Color4 BackgroundColor = new Color4(0f, 0f, 0f, 1f);

		public float Exposure = 1f;

		/// <summary>ACES tonemapping; off clamps the HDR color.</summary>
		public bool Tonemap = true;

		#endregion

		#region PublicMethods

		/// <summary>Copies the values into the uniform carriers and the renderer settings (every frame).</summary>
		public void ApplyTo(RenderSettings settings)
		{
			Lighting.Ambient.Color = AmbientColor;
			Lighting.Ambient.Strength = AmbientStrength;
			Lighting.Specular.Strength = SpecularStrength;
			Lighting.Specular.Shine = SpecularShine;
			if (settings != null)
			{
				settings.ClearColor = BackgroundColor;
				settings.Exposure = Exposure;
				settings.Tonemap = Tonemap;
			}
		}

		#endregion
	}
}
