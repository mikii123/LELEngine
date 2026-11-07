//Compute

#version 430

// How the lights inside a volumetric object (VolumetricObjectBase.Lights, up to 8) light its cloud, per voxel of a grid
// at half the density's resolution: lights 0-3 in A, 4-7 in B, each sqrt(light / (1 + glow)) of the light at unit
// intensity. A light's core lights what it sees through the cloud; its glow is light scattered on through the cloud
// (it reaches far and gets through thick cloud), so a light deep in a cloud still lights the cloud around it up. Only
// where the lights are is baked: their colour and intensity are applied when the cloud is drawn (flashes flicker for free).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, rgba8) uniform writeonly image3D LightsA;
layout(binding = 1, rgba8) uniform writeonly image3D LightsB;

uniform sampler3D Density;       // r8 with mips; zero outside
uniform ivec3 volResolution;     // of this grid
uniform int volSliceOffset;
uniform vec3 volSize;
uniform float volSigma;
uniform float volVoxel;          // local units per voxel of the density
uniform vec4 volLights[8];       // xyz local position, w radius of the core (0: no light)
uniform float volLightGlow;

float LightAt(vec3 p, vec4 light)
{
	if (light.w <= 0.0)
	{
		return 0.0;
	}

	float dist = distance(p, light.xyz);
	if (dist > light.w * 8.0)
	{
		return 0.0;
	}

	// The cloud between the voxel and the light, five samples at a mip that fits their spacing.
	float len = dist / 5.0;
	float lod = max(log2(len / volVoxel) - 0.5, 0.0);
	float tau = 0.0;
	for (int k = 0; k < 5; k++)
	{
		vec3 q = mix(p, light.xyz, (float(k) + 0.5) / 5.0);
		tau += textureLod(Density, q / volSize + 0.5, lod).r;
	}

	tau *= len * volSigma;
	float core = 1.0 / (1.0 + (dist / light.w) * (dist / light.w));
	float wide = 1.0 / (1.0 + (dist / (light.w * 4.0)) * (dist / (light.w * 4.0)));
	return core * exp(-tau) + volLightGlow * wide * exp(-tau * 0.15);
}

void main()
{
	ivec3 v = ivec3(gl_GlobalInvocationID.xy, int(gl_GlobalInvocationID.z) + volSliceOffset);
	if (any(greaterThanEqual(v, volResolution)))
	{
		return;
	}

	vec3 p = ((vec3(v) + 0.5) / vec3(volResolution) - 0.5) * volSize;
	float scale = 1.0 / (1.0 + volLightGlow);
	vec4 a = vec4(LightAt(p, volLights[0]), LightAt(p, volLights[1]), LightAt(p, volLights[2]), LightAt(p, volLights[3]));
	vec4 b = vec4(LightAt(p, volLights[4]), LightAt(p, volLights[5]), LightAt(p, volLights[6]), LightAt(p, volLights[7]));
	imageStore(LightsA, v, sqrt(clamp(a * scale, 0.0, 1.0)));
	imageStore(LightsB, v, sqrt(clamp(b * scale, 0.0, 1.0)));
}

/////Compute
