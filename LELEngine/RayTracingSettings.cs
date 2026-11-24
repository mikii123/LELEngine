using System;

namespace LELEngine
{
	/// <summary>
	///     What the lighting traces through the scene's triangles besides Lumen's rays (those are
	///     <see cref="GlobalIlluminationSettings.HardwareRayTracing" />): the shadows of local and directional lights on
	///     opaque and transparent surfaces, the reflections of glossy surfaces, and how voxel cone tracing's cones see the
	///     triangles. Each needs a graphics device with ray queries (Vulkan on a GPU that has them,
	///     <see cref="Rendering.GraphicsCapabilities.RayQueries" />); elsewhere the engine does as without it. Project
	///     Settings > Lighting > Ray Tracing; read every frame (<see cref="Lighting.RayTracedLocalShadows" />,
	///     <see cref="Lighting.RayTracedDirectionalShadows" />, <see cref="Lighting.RayTracedReflections" />).
	/// </summary>
	[Serializable]
	public sealed class RayTracingSettings
	{
		#region PublicFields

		/// <summary>Shadows of the most local lights that may be traced at once (the shaders' mask holds this many).</summary>
		public const int MaxShadowedLocalLights = 8;

		/// <summary>Rays a voxel cone traces at most (<see cref="ConeRays" />).</summary>
		public const int MaxConeRays = 4;

		[Header("Shadows")]
		[Tooltip("Point and spot lights cast shadows on opaque surfaces, traced through the scene's triangles: a ray from every pixel to each light that reaches it (lights with Cast Shadows on, the nearest to the camera first), soft by the light's Source Radius. Without ray tracing local lights cast no shadows on surfaces")]
		public bool LocalLightShadows = true;

		[Tooltip("How many local lights cast traced shadows at once: those nearest to the camera (a light whose range holds the camera first); the others reach through walls")]
		[Range(1, MaxShadowedLocalLights)]
		public int ShadowedLocalLights = MaxShadowedLocalLights;

		[Tooltip("The shadowed directional lights' shadows on opaque surfaces traced through the scene's triangles instead of read from the shadow maps (soft by each light's Angular Diameter, sharp where the caster touches), on every GPU with ray queries. The maps are drawn only while something still reads them (see Skip Unneeded Shadow Maps)")]
		public bool DirectionalLightShadows = false;

		[Tooltip("The same on a discrete GPU (memory of its own) even with Directional Light Shadows off: such a GPU traces several times the rays of an integrated one, so the rays' cost (a ray a pixel and light, whatever the casters) stays small next to what they give (soft by the light's size, no cascades, no acne) and the maps' casters leave the CPU's work. Integrated GPUs keep the maps unless Directional Light Shadows is on (there the rays cost a few tenths of a millisecond more than the maps)")]
		public bool DirectionalLightShadowsOnDiscreteGpus = true;

		[Tooltip("With the directional lights' shadows traced: their shadow maps hold every caster only in frames that still read them so (clouds, lit particles, transparent or custom shaders that read only the maps, the other cameras of a stack), only the casters the rays do not see where there are such (transparent casters' tint, particle casters, impostors: the traced surfaces take their shadows on top), and are not drawn otherwise. Off: the maps are drawn every frame as without tracing")]
		public bool SkipUnneededShadowMaps = true;

		[Tooltip("Transparent surfaces (shaders with RayQueries On: Lit Transparent) trace their shadows per fragment: a ray to each local light with a traced shadow and along each traced directional light (from its centre: hard). Off: local lights reach them unshadowed, directional lights through their maps")]
		public bool TransparentShadows = true;

		[Tooltip("Shadows as soft as the lights' size makes them: one ray a pixel and light towards a point of the light, averaged over frames and over neighbouring pixels. Off: hard shadows from the lights' centres, nothing averaged")]
		public bool SoftShadows = true;

		[Tooltip("With Lumen's hardware ray tracing: the surface cache traces the directional lights' shadows on its cards through the scene's triangles (a ray per texel and frame) instead of reading the shadow maps that cover the GI volume; those maps then hold only what transparent casters tint, and are not drawn without them")]
		public bool SurfaceCacheShadows = true;

		[Header("Reflections")]
		[Tooltip("Glossy surfaces trace their reflections through the scene's triangles (with Lumen's hardware ray tracing): a ray a pixel along the reflection, lit where it lands from Lumen's surface cache, averaged over frames; rougher surfaces keep the screen probes' reflection")]
		public bool Reflections = true;

		[Tooltip("The roughness up to which a surface traces its reflection; it blends into the screen probes' over the last quarter below it")]
		[Range(0.05f, 1f)]
		public float ReflectionMaxRoughness = 0.4f;

		[Header("Voxel GI")]
		[Tooltip("With voxel cone tracing in an SDF mode on the scene's triangles: the rays each cone traces for its visibility, spread over the cone's opening (turned per surface cell, as the cones are). More rays see more of what lies beside the cone's axis, as the distance field did, at their cost")]
		[Range(1, MaxConeRays)]
		public int ConeRays = 1;

		[Tooltip("With voxel cone tracing on the scene's triangles: the cones the materials trace themselves (transparent surfaces, the other cameras of a stack, no screen-space resolve) trace rays as the GI's own do, through the shaders' ray query variants. Otherwise they are voxel cones there (cheaper; light comes through walls the voxels average away). Discrete GPUs: only on a discrete GPU (every transparent pixel traces seven rays)")]
		public RayTracingUse MaterialConeRays = RayTracingUse.DiscreteGpus;

		#endregion
	}

	/// <summary>Where a ray traced feature that costs much on integrated GPUs runs (<see cref="RayTracingSettings" />).</summary>
	public enum RayTracingUse
	{
		Off,

		/// <summary>On a discrete GPU only (<see cref="Graphics.DeviceCapabilities.DiscreteGpu" />).</summary>
		DiscreteGpus,

		Always
	}
}
