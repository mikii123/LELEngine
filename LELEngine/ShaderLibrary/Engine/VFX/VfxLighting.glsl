// Lighting of lit particle outputs: the sun with its shadow map, the ambient term and the GI volume
// (Engine/VolumeLighting.glsl: cones or radiance cache probes), the way the engine lights transparent materials.
// Include after Engine/Shadows.glsl and Engine/VolumeLighting.glsl.
#ifndef VFX_LIGHTING_GLSL
#define VFX_LIGHTING_GLSL

struct VfxDirectional
{
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};

struct VfxAmbient
{
	vec4 ambColor;
	float ambStrength;
};

uniform VfxDirectional LDirectional;
uniform VfxAmbient LAmbient;

// Diffuse shading of a particle surface: albedo times the direct light (shadowed), ambient and indirect light.
vec3 VfxShade(vec3 albedo, vec3 N, vec3 worldPos, float roughness)
{
	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(vfxCameraPosition - worldPos);
	vec3 lightColor = LDirectional.dirColor.rgb * LDirectional.dirStrength;
	// Half-Lambert softens the terminator of thin billboards (smoke, dust) instead of cutting them in two.
	float ndl = dot(N, L) * 0.5 + 0.5;
	ndl *= ndl;
	vec3 shadow = SampleShadowRGB(worldPos, N, L);
	vec3 direct = albedo * lightColor * ndl * shadow;
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetVolumeIndirectLighting(worldPos, N, V, roughness > 0.0 ? roughness : 0.8, indirectDiffuse, occlusion, indirectSpecular);
	return direct + ambient * occlusion + indirectDiffuse * albedo;
}

#endif
