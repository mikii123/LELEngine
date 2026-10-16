// Indirect lighting for surfaces that are not in the depth buffer: transparent materials (drawn after the opaques,
// blended), or anything shaded off the geometry prepass. The screen-space GI resolve (Engine/VoxelConeTracing.glsl)
// belongs to the opaque surface behind such a fragment, so this path lights from the GI volume instead:
//   giVolumeMode 1  voxel cone tracing: the cones are traced per fragment (the same cones the opaques trace);
//   giVolumeMode 2  Lumen: the radiance cache probes around the point (irradiance from their spherical harmonics,
//                   reflections from their maps), the way Lumen lights translucency from a probe volume.
// Uniforms are set by Lighting.SetVolumeGIUniforms. Include after #version: #include "Engine/VolumeLighting.glsl"
#ifndef LEL_VOLUME_LIGHTING_GLSL
#define LEL_VOLUME_LIGHTING_GLSL

#include "Engine/VoxelConeTracingCore.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"
#include "Engine/VolumeProbes.glsl"

#ifndef LEL_GI_ENABLED_UNIFORM
#define LEL_GI_ENABLED_UNIFORM
uniform int giEnabled;
#endif
uniform int giVolumeMode; // 0 none, 1 voxel cones, 2 radiance cache probes

// Same contract as GetIndirectLighting:
//   diffuse   : indirect irradiance / pi, multiply by albedo
//   occlusion : ambient occlusion factor for the flat ambient term (cones only)
//   specular  : indirect radiance along the reflection, multiply by the specular color / strength
void GetVolumeIndirectLighting(vec3 position, vec3 normal, vec3 viewDir, float roughness, out vec3 diffuse, out float occlusion, out vec3 specular)
{
	diffuse = vec3(0.0);
	occlusion = 1.0;
	specular = vec3(0.0);
	if (giEnabled == 0 || giVolumeMode == 0) return;

	if (giVolumeMode == 1)
	{
		vec4 diffuseCoverage = TraceDiffuseCones(position, normal);
		diffuse = diffuseCoverage.rgb * giDiffuseStrength;
		occlusion = clamp(1.0 - diffuseCoverage.a * giOcclusionStrength, 0.0, 1.0);
		specular = TraceSpecularCone(position, normal, viewDir, roughness) * giSpecularStrength;
		return;
	}

	vec3 c[9];
	if (!SampleRadianceCacheSH(position, c)) return;

	// Irradiance / pi: the engine's indirect diffuse convention (ProbeIntegrate.shader).
	vec3 irradiance = SHIrradiance(c, normal) / SAMPLING_PI;
	diffuse = irradiance * giDiffuseStrength;

	// Smooth surfaces look the probes up along the reflection (parallax corrected, with the probes' occlusion);
	// where that is too coarse (geometry closer than a probe spacing) or the surface is rough, the SH along the
	// reflection, damped towards the cosine-convolved value as roughness grows (as the opaque gather does).
	vec3 R = reflect(-viewDir, normal);
	float r = roughness > 0.0 ? roughness : 0.6;
	vec3 reflected;
	if (r < 0.5 && SampleRadianceCache(position + normal * 0.02, R, reflected))
	{
		specular = reflected;
	}
	else
	{
		specular = mix(SHEvaluateWindowed(c, R), irradiance, smoothstep(0.25, 0.6, r));
	}

	specular *= giSpecularStrength;
}

#endif
