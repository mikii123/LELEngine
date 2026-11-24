using System;

namespace LELEngine
{
	/// <summary>
	///     What the lighting traces through the scene's triangles besides Lumen's rays (those are
	///     <see cref="GlobalIlluminationSettings.HardwareRayTracing" />): the shadows of local and directional lights on
	///     opaque surfaces and the reflections of glossy surfaces. Each needs a graphics device with ray queries (Vulkan on a
	///     GPU that has them, <see cref="Rendering.GraphicsCapabilities.RayQueries" />); elsewhere the engine does as
	///     without it. Project Settings > Lighting > Ray Tracing; read every frame (<see cref="Lighting.RayTracedLocalShadows" />,
	///     <see cref="Lighting.RayTracedDirectionalShadows" />, <see cref="Lighting.RayTracedReflections" />).
	/// </summary>
	[Serializable]
	public sealed class RayTracingSettings
	{
		#region PublicFields

		/// <summary>Shadows of the most local lights that may be traced at once (the shaders' mask holds this many).</summary>
		public const int MaxShadowedLocalLights = 8;

		[Header("Shadows")]
		[Tooltip("Point and spot lights cast shadows on opaque surfaces, traced through the scene's triangles: a ray from every pixel to each light that reaches it (lights with Cast Shadows on, the nearest to the camera first), soft by the light's Source Radius. Without ray tracing local lights cast no shadows on surfaces")]
		public bool LocalLightShadows = true;

		[Tooltip("How many local lights cast traced shadows at once: those nearest to the camera (a light whose range holds the camera first); the others reach through walls")]
		[Range(1, MaxShadowedLocalLights)]
		public int ShadowedLocalLights = MaxShadowedLocalLights;

		[Tooltip("The shadowed directional lights' shadows on opaque surfaces traced through the scene's triangles instead of read from the shadow maps (soft by each light's Angular Diameter, sharp where the caster touches). The maps are still drawn for what reads them besides: transparent surfaces, particles, clouds")]
		public bool DirectionalLightShadows = false;

		[Tooltip("Shadows as soft as the lights' size makes them: one ray a pixel and light towards a point of the light, averaged over frames and over neighbouring pixels. Off: hard shadows from the lights' centres, nothing averaged")]
		public bool SoftShadows = true;

		[Header("Reflections")]
		[Tooltip("Glossy surfaces trace their reflections through the scene's triangles (with Lumen's hardware ray tracing): a ray a pixel along the reflection, lit where it lands from Lumen's surface cache, averaged over frames; rougher surfaces keep the screen probes' reflection")]
		public bool Reflections = true;

		[Tooltip("The roughness up to which a surface traces its reflection; it blends into the screen probes' over the last quarter below it")]
		[Range(0.05f, 1f)]
		public float ReflectionMaxRoughness = 0.4f;

		#endregion
	}
}
