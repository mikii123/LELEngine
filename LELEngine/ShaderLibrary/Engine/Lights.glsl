// The scene's local lights (Light components: point and spot) for the lit materials, the GI and the volumetric objects:
// the list Rendering.LightBuffer fills every frame, a uniform block on binding 7. Physical falloff: the inverse square
// of the distance (from a source of the light's radius), faded to nothing at its range (Frostbite's window); a spot's
// cone fades between its inner and outer angle; the clouds between the light and the point dim it (the volumes'
// transmittance from the light towards six directions, blended for the direction). Radiance units as the sun's: a white
// surface facing a light of radiance 1 reflects 1. Include after #version: #include "Engine/Lights.glsl"
#ifndef LEL_LIGHTS_GLSL
#define LEL_LIGHTS_GLSL

#define LEL_MAX_LIGHTS 32
#define LEL_LIGHT_GI 1
#define LEL_LIGHT_VOLUMES 2
#define LEL_LIGHT_GI_SHADOWS 4

struct LelLight
{
	vec4 positionRange;   // world position, range
	vec4 radiance;        // colour x intensity, w: how much of it goes into the GI (0: none)
	vec4 spot;            // the spot's direction (world), w: cos of its outer half angle (-2: a point light)
	vec4 params;          // x: 1 / (cos inner - cos outer), y: flags (LEL_LIGHT_*), z: the source's radius
	vec4 transmittanceA;  // the clouds' transmittance from the light towards +x, -x, +y
	vec4 transmittanceB;  // towards -y, +z, -z
};

layout(std140, binding = 7) uniform LelLightBlock
{
	ivec4 lelLightCount;  // x: lights in the list
	LelLight lelLights[LEL_MAX_LIGHTS];
};

int LelLightCount()
{
	return min(lelLightCount.x, LEL_MAX_LIGHTS);
}

bool LelLightHas(int i, int flag)
{
	return (int(lelLights[i].params.y) & flag) != 0;
}

// 1 / d^2 from the source's radius on, faded to 0 at the range: (1 - (d / range)^4)^2.
float LelLightFalloff(float d2, float range, float sourceRadius)
{
	float r2 = range * range;
	float fade = clamp(1.0 - (d2 * d2) / (r2 * r2), 0.0, 1.0);
	return fade * fade / max(d2, sourceRadius * sourceRadius);
}

// How much of light i gets through the clouds towards a direction away from it (unit vector).
float LelLightCloudTransmittance(int i, vec3 away)
{
	vec3 w = away * away;
	vec4 a = lelLights[i].transmittanceA;
	vec4 b = lelLights[i].transmittanceB;
	return (away.x >= 0.0 ? a.x : a.y) * w.x + (away.y >= 0.0 ? a.z : b.x) * w.y + (away.z >= 0.0 ? b.y : b.z) * w.z;
}

// Light i's falloff and cone at a point (no colour, no clouds): the direction towards it and the factor; false outside
// its range or cone.
bool LelLightShape(int i, vec3 position, out vec3 L, out float attenuation)
{
	vec4 positionRange = lelLights[i].positionRange;
	vec3 toLight = positionRange.xyz - position;
	float d2 = dot(toLight, toLight);
	L = vec3(0.0, 1.0, 0.0);
	attenuation = 0.0;
	if (d2 >= positionRange.w * positionRange.w)
	{
		return false;
	}

	L = toLight * inversesqrt(max(d2, 1e-12));
	attenuation = LelLightFalloff(d2, positionRange.w, lelLights[i].params.z);
	vec4 spot = lelLights[i].spot;
	if (spot.w > -1.5)
	{
		float cone = clamp((dot(-L, spot.xyz) - spot.w) * lelLights[i].params.x, 0.0, 1.0);
		attenuation *= cone * cone;
	}

	return attenuation > 0.0;
}

// Light i at a point: the direction towards it and its radiance there (falloff, cone, clouds); false when it does not
// reach the point.
bool LelLightAt(int i, vec3 position, out vec3 L, out vec3 radiance)
{
	float attenuation;
	radiance = vec3(0.0);
	if (!LelLightShape(i, position, L, attenuation))
	{
		return false;
	}

	radiance = lelLights[i].radiance.rgb * (attenuation * LelLightCloudTransmittance(i, -L));
	return true;
}

#endif
