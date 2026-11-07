//Compute

#version 430

// The light the scene's GI brings to a volumetric object (VolumetricObjectBase.ReceiveGI), per cell of a coarse grid in
// the object's local space. From each of the six local axis directions: the GI's incoming radiance (voxel cone tracing:
// one wide cone; Lumen: the radiance cache probes' spherical harmonics; a cone leaving the GI's volume sees the sky),
// dimmed by the cloud towards that side (the ambient openness, 0.08 + 0.92 exp(-tau / 2): some light always scatters
// in). Stored as a first order spherical harmonic of that light: A = (the mean radiance, 1), B = (the luminance's
// directional term, local axes). The march scatters it towards the eye with the cloud's phase: for incoming radiance
// a + b . d (d: where the light comes from), Henyey-Greenstein scatters a + g (b . ray) along a ray.

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, rgba16f) uniform writeonly image3D GIA;
layout(binding = 1, rgba16f) uniform writeonly image3D GIB;

#include "Engine/VolumeLighting.glsl"

uniform sampler3D Density;       // r8 with mips; zero outside
uniform ivec3 volResolution;     // of this grid
uniform int volSliceOffset;
uniform vec3 volSize;
uniform float volSigma;          // extinction per local unit at density 1
uniform float volVoxel;          // local units per voxel of the density
uniform float volReach;          // how far the openness looks (local units)
uniform mat4 volLocalToWorld;

const vec3 Axes[6] = vec3[6](vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, -1.0));

// How much light from a side gets in: the cloud towards it within the reach, in doubling segments (four samples).
float Openness(vec3 p, vec3 dir)
{
	float tau = 0.0;
	float start = 0.0;
	float len = volReach / 15.0;
	for (int k = 0; k < 4; k++)
	{
		float lod = max(log2(len / volVoxel) - 0.5, 0.0);
		tau += textureLod(Density, (p + dir * (start + len * 0.5)) / volSize + 0.5, lod).r * len;
		start += len;
		len *= 2.0;
	}

	return 0.08 + 0.92 * exp(-0.5 * tau * volSigma);
}

// The GI's radiance arriving at a point from a direction (world).
vec3 Incoming(vec3 world, vec3 dir)
{
	if (giVolumeMode == 1)
	{
		// Six cones of 45 degrees (aperture 1) around the axes cover the sphere.
		return TraceCone(world, dir, 1.0, ConeMaxDistance()).rgb * giDiffuseStrength;
	}

	vec3 c[9];
	if (giVolumeMode == 2 && SampleRadianceCacheSH(world, c))
	{
		return SHEvaluate(c, dir) * giDiffuseStrength;
	}

	return SkyRadiance(dir, 1.0);
}

void main()
{
	ivec3 v = ivec3(gl_GlobalInvocationID.xy, int(gl_GlobalInvocationID.z) + volSliceOffset);
	if (any(greaterThanEqual(v, volResolution)))
	{
		return;
	}

	vec3 local = ((vec3(v) + 0.5) / vec3(volResolution) - 0.5) * volSize;
	vec3 world = (volLocalToWorld * vec4(local, 1.0)).xyz;
	mat3 toWorld = mat3(volLocalToWorld);
	vec3 sum = vec3(0.0);
	vec3 directional = vec3(0.0);
	for (int i = 0; i < 6; i++)
	{
		vec3 radiance = Incoming(world, normalize(toWorld * Axes[i])) * Openness(local, Axes[i]);
		sum += radiance;
		directional += Axes[i] * dot(radiance, vec3(0.2126, 0.7152, 0.0722));
	}

	// The mean, and the first order term of six cones of a sixth of the sphere each: 3 / (4 pi) x 5 pi / 9 = 5 / 12.
	imageStore(GIA, v, vec4(sum / 6.0, 1.0));
	imageStore(GIB, v, vec4(directional * (5.0 / 12.0), 0.0));
}

/////Compute
