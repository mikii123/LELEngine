// The scene's directional lights (DirectionalLight components: suns, moons) for the lit materials, the particles and the
// GI: the list Rendering.DirectionalLightBuffer fills every frame, a uniform block on binding 6 (uploaded once a frame,
// whatever the number of programs that read it). Light 0 is the main light, the others follow by brightness; at most
// LEL_MAX_DIRECTIONAL_LIGHTS shine at once. Radiance units: a white surface facing a light of radiance 1 reflects 1.
// A light's shadows are Engine/Shadows.glsl's SampleLightShadowRGB(i, ...): the same index.
// Include after #version: #include "Engine/DirectionalLights.glsl"
#ifndef LEL_DIRECTIONAL_LIGHTS_GLSL
#define LEL_DIRECTIONAL_LIGHTS_GLSL

#define LEL_MAX_DIRECTIONAL_LIGHTS 4

struct LelDirectionalLight
{
	vec4 direction;  // xyz: the unit vector towards the light (world), w: flags (as a float)
	vec4 radiance;   // rgb: colour x strength, w: how much of it goes into the GI (0: none)
};

layout(std140, binding = 6) uniform LelDirectionalBlock
{
	ivec4 lelDirectionalCount;  // x: lights in the list
	LelDirectionalLight lelDirectional[LEL_MAX_DIRECTIONAL_LIGHTS];
};

int LelDirectionalCount()
{
	return min(lelDirectionalCount.x, LEL_MAX_DIRECTIONAL_LIGHTS);
}

// The unit vector from a surface towards light i.
vec3 LelDirectionalTowards(int i)
{
	return lelDirectional[i].direction.xyz;
}

// Light i's colour x strength.
vec3 LelDirectionalRadiance(int i)
{
	return lelDirectional[i].radiance.rgb;
}

// How much of light i goes into the GI (its indirect strength; 0: it stays out).
float LelDirectionalGIStrength(int i)
{
	return lelDirectional[i].radiance.w;
}

#endif
