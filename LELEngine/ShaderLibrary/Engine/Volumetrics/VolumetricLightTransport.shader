//Compute

#version 430

// How the lights of a volumetric object (up to 8: its own, VolumetricObjectBase.Lights, and the scene's Light components
// that reach it) light its cloud, per voxel of a grid at half the density's resolution: lights 0-3 in A, 4-7 in B, linear
// light at unit brightness (times the slot's factor). The cloud between the voxel and the light dims it; the glow is light
// scattered on through the cloud (it gets through thick cloud), so a light deep in a cloud still lights the cloud around
// it up. Only where the lights are is worked out here: their colour and intensity are applied when the cloud is drawn
// (flashes flicker for free).
//   own light    a core of the slot's radius and a glow four times as wide (1 / (1 + (d / r)^2) each)
//   scene light  the Light's physical falloff in world units (1 / d^2 from its source's radius, faded to 0 at its range,
//                Engine/Lights.glsl) and its cone, times (range / 4)^2 so half floats keep it from the light to the range

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, rgba16f) uniform writeonly image3D LightsA;
layout(binding = 1, rgba16f) uniform writeonly image3D LightsB;

uniform sampler3D Density;       // r8 with mips; zero outside
uniform ivec3 volResolution;     // of this grid
uniform int volSliceOffset;
uniform vec3 volSize;
uniform float volSigma;
uniform float volVoxel;          // local units per voxel of the density
uniform float volWorldScale;     // world units per local unit
uniform vec4 volLights[8];       // xyz local position; w own: the core's radius (local, 0: no light), scene: the range (world)
uniform vec4 volLightParams[8];  // x 0 own / 1 scene, y the source's radius (world), z cos outer (-2: no cone), w spot scale
uniform vec4 volLightSpots[8];   // xyz the spot's direction (local), w the factor the light is held times
uniform float volLightGlow;

// The cloud between the voxel and the light: five samples at a mip that fits their spacing.
float OpticalDepth(vec3 p, vec3 light, float dist)
{
	float len = dist / 5.0;
	float lod = max(log2(len / volVoxel) - 0.5, 0.0);
	float tau = 0.0;
	for (int k = 0; k < 5; k++)
	{
		vec3 q = mix(p, light, (float(k) + 0.5) / 5.0);
		tau += textureLod(Density, q / volSize + 0.5, lod).r;
	}

	return tau * len * volSigma;
}

float LightAt(vec3 p, int i)
{
	vec4 light = volLights[i];
	vec4 params = volLightParams[i];
	if (light.w <= 0.0)
	{
		return 0.0;
	}

	float dist = distance(p, light.xyz);
	if (params.x < 0.5)
	{
		// A light of its own: core and glow.
		if (dist > light.w * 8.0)
		{
			return 0.0;
		}

		float tau = OpticalDepth(p, light.xyz, dist);
		float core = 1.0 / (1.0 + (dist / light.w) * (dist / light.w));
		float wide = 1.0 / (1.0 + (dist / (light.w * 4.0)) * (dist / (light.w * 4.0)));
		return core * exp(-tau) + volLightGlow * wide * exp(-tau * 0.15);
	}

	// A scene light: its falloff in world units, its cone, the cloud on the way (and its glow).
	float d = dist * volWorldScale;
	float range = light.w;
	if (d >= range)
	{
		return 0.0;
	}

	float d2 = d * d;
	float r2 = range * range;
	float fade = clamp(1.0 - (d2 * d2) / (r2 * r2), 0.0, 1.0);
	float falloff = fade * fade / max(d2, params.y * params.y);
	if (params.z > -1.5)
	{
		vec3 towards = (light.xyz - p) / max(dist, 1e-6);
		float cone = clamp((dot(-towards, volLightSpots[i].xyz) - params.z) * params.w, 0.0, 1.0);
		falloff *= cone * cone;
	}

	if (falloff <= 0.0)
	{
		return 0.0;
	}

	float tau = OpticalDepth(p, light.xyz, dist);
	return falloff * volLightSpots[i].w * (exp(-tau) + volLightGlow * exp(-tau * 0.15));
}

void main()
{
	ivec3 v = ivec3(gl_GlobalInvocationID.xy, int(gl_GlobalInvocationID.z) + volSliceOffset);
	if (any(greaterThanEqual(v, volResolution)))
	{
		return;
	}

	vec3 p = ((vec3(v) + 0.5) / vec3(volResolution) - 0.5) * volSize;
	imageStore(LightsA, v, vec4(LightAt(p, 0), LightAt(p, 1), LightAt(p, 2), LightAt(p, 3)));
	imageStore(LightsB, v, vec4(LightAt(p, 4), LightAt(p, 5), LightAt(p, 6), LightAt(p, 7)));
}

/////Compute
