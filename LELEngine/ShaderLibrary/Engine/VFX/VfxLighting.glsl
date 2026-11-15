// Lighting of lit particle outputs: the directional lights with their shadow maps, the ambient term and the GI volume
// (Engine/VolumeLighting.glsl: cones or radiance cache probes), the way the engine lights transparent materials.
// Include after Engine/Shadows.glsl and Engine/VolumeLighting.glsl.
#ifndef VFX_LIGHTING_GLSL
#define VFX_LIGHTING_GLSL

#include "Engine/DirectionalLights.glsl"

struct VfxAmbient
{
	vec4 ambColor;
	float ambStrength;
};

uniform VfxAmbient LAmbient;

// Diffuse shading of a particle surface: albedo times the direct light (shadowed), ambient and indirect light.
vec3 VfxShade(vec3 albedo, vec3 N, vec3 worldPos, float roughness)
{
	vec3 V = normalize(vfxCameraPosition - worldPos);
	// Every directional light (Engine/DirectionalLights.glsl) behind its own shadows.
	vec3 direct = vec3(0.0);
	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		// Half-Lambert softens the terminator of thin billboards (smoke, dust) instead of cutting them in two.
		float ndl = dot(N, L) * 0.5 + 0.5;
		ndl *= ndl;
		direct += albedo * LelDirectionalRadiance(i) * ndl * SampleLightShadowRGB(i, worldPos, N, L);
	}

	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetVolumeIndirectLighting(worldPos, N, V, roughness > 0.0 ? roughness : 0.8, indirectDiffuse, occlusion, indirectSpecular);
	return direct + ambient * occlusion + indirectDiffuse * albedo;
}

#endif
