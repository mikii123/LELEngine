//Compute

#version 430

// The light of a volumetric object, per voxel (LELEngine.Volumetrics.VolumetricData), in one RGBA8 volume the march reads
// with one fetch a step:
//   r  the cloud's density (a copy of the density volume's level 0)
//   g  sqrt(optical depth towards the sun / volTauMax): the sun's light reaching the voxel through the cloud
//   b  how much of the ambient light reaches it: the cloud's optical depth towards the six sides, averaged as
//      transmittance (some always gets in: light scattered on)
// Optical depths are marched through the density's mip chain: each sample twice as long as the last and read at the mip
// that fits its length (near the voxel the detail counts, far out the average does), so a march costs a few fetches.
// Only the direction is baked: the sun's colour and the sky's are applied when the cloud is drawn.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, rgba8) uniform writeonly image3D Lit;

uniform sampler3D Density;       // r8 with mips; zero outside (clamp to border)
uniform ivec3 volResolution;
uniform int volSliceOffset;
uniform vec3 volSize;            // the shape's extents (local units)
uniform vec3 volSunDirection;    // local, towards the sun, unit length
uniform float volSigma;          // extinction per local unit at density 1
uniform float volShadowReach;    // how far the optical depths are marched (local units)
uniform float volVoxel;          // local units per voxel
uniform float volTauMax;

// Optical depth from p along dir out to the reach: samples twice as long as the one before, at the middle of each.
float OpticalDepth(vec3 p, vec3 dir, int samples)
{
	float unit = volShadowReach / (exp2(float(samples)) - 1.0);
	float dist = 0.0;
	float tau = 0.0;
	for (int k = 0; k < samples; k++)
	{
		float len = unit * exp2(float(k));
		vec3 q = p + dir * (dist + len * 0.5);
		float lod = max(log2(len / volVoxel) - 0.5, 0.0);
		tau += textureLod(Density, q / volSize + 0.5, lod).r * len;
		dist += len;
	}

	return tau * volSigma;
}

void main()
{
	ivec3 v = ivec3(gl_GlobalInvocationID.xy, int(gl_GlobalInvocationID.z) + volSliceOffset);
	if (any(greaterThanEqual(v, volResolution)))
	{
		return;
	}

	vec3 p = ((vec3(v) + 0.5) / vec3(volResolution) - 0.5) * volSize;
	float density = texelFetch(Density, v, 0).r;

	// Every voxel gets its light, empty ones too: they filter into the cloud next to them.
	float sun = OpticalDepth(p, volSunDirection, 7);

	const vec3 sides[6] = vec3[6](vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, -1.0));
	float open = 0.0;
	for (int i = 0; i < 6; i++)
	{
		open += 0.08 + 0.92 * exp(-0.5 * OpticalDepth(p, sides[i], 4));
	}

	imageStore(Lit, v, vec4(density, sqrt(clamp(sun / volTauMax, 0.0, 1.0)), open / 6.0, 1.0));
}

/////Compute
