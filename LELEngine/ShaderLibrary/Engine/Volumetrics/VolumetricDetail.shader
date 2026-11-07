//Compute

#version 430

// The tiling detail noise every volumetric object shares (LELEngine.Volumetrics.VolumetricRuntime): how much of a
// cloud's edge survives at each point of one tile (puffs, grain and finer grain). Every octave's period divides the
// tile, so it repeats without a seam. Made once per GL context.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, r8) uniform writeonly image3D Detail;

#include "Engine/Volumetrics/VolumetricNoise.glsl"

uniform int volResolution;

void main()
{
	ivec3 v = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(v, ivec3(volResolution))))
	{
		return;
	}

	vec3 u = (vec3(v) + 0.5) / float(volResolution);
	float puffs = 1.0 - VolWorley(u * 4.0, 4);
	float grain = VolFbm(u * 8.0 + vec3(17.0, 31.0, 7.0), 2, 0.5, 8) + 0.5;
	float fine = VolFbm(u * 16.0 + vec3(5.0, 11.0, 23.0), 2, 0.5, 16) + 0.5;
	imageStore(Detail, v, vec4(clamp(puffs * 0.65 + grain * 0.22 + fine * 0.13, 0.0, 1.0), 0.0, 0.0, 0.0));
}

/////Compute
