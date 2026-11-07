//Compute

#version 430

// The cloud density of a volumetric object (LELEngine.Volumetrics.VolumetricData), 0..1 per voxel of its shape's bounding
// box, in the object's local space (the shape centred on the origin). The noise is set in local units around the
// origin, so a bigger shape holds more of the same cloud instead of a stretched one; how the cloud fades out towards
// the shape's boundary is set in shares of the shape. Dispatched over a range of z slices at a time (short dispatches).
// VOL_POINTS: the same density at local points instead (VolumetricData.EvaluateDensity), exactly where asked.

#ifdef VOL_POINTS
layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;
layout(std430, binding = 0) readonly buffer PointsIn { vec4 points[]; };
layout(std430, binding = 1) writeonly buffer DensitiesOut { float densities[]; };
uniform int volPointCount;
#else
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, r8) uniform writeonly image3D Density;
#endif

#include "Engine/Volumetrics/VolumetricNoise.glsl"

uniform ivec3 volResolution;
uniform int volSliceOffset;
uniform vec3 volSize;            // the shape's extents (local units)
uniform int volShape;            // 0 box, 1 cylinder (axis y), 2 sphere (ellipsoid)
uniform vec3 volNoiseOffset;     // the seed, as an offset in noise space
uniform float volNoiseSize;      // local size of the large cloud shapes
uniform int volNoiseType;
uniform int volOctaves;
uniform float volGain;
uniform float volWarp;
uniform float volThreshold;      // where the noise starts to be cloud (1 - coverage)
uniform float volSoftness;       // how soon the cloud is dense past its edge
uniform float volClumpScale;     // local size of the clumps (0: none)
uniform float volClumpThreshold;
uniform float volClumpSoftness;
uniform float volProfileScale;   // local size of the boundary's wandering
uniform float volSideSoftness;   // fade towards the side, a share of the half size
uniform float volCapSoftness;    // fade towards the top and bottom, a share of the height
uniform float volTopRoughness;
uniform float volBottomRoughness;
uniform float volSideRoughness;

// How much of the shape holds cloud at p: 1 inside, fading out towards the boundary. The boundary wanders (towers on
// top, a ragged base, an uneven side), so the cloud does not read as the shape it fills.
float Profile(vec3 p)
{
	vec3 q = p / (volSize * 0.5);
	if (volShape == 2)
	{
		// Sphere: one radial fade, its limit pulled in by noise over the directions.
		vec3 dir = q / max(length(q), 1e-5);
		float rim = clamp(VolFbm(dir * 2.2 + volNoiseOffset * 0.29, 3, 0.5, 0) * 1.5 + 0.5, 0.0, 1.0);
		float limit = 1.0 - volSideRoughness * rim;
		return 1.0 - smoothstep(limit - volSideSoftness, limit, length(q));
	}

	vec3 plane = vec3(p.x, 0.0, p.z) / volProfileScale + volNoiseOffset * 0.53;
	float towers = clamp(VolFbm(plane * 1.9, 3, 0.5, 0) * 1.5 + 0.5, 0.0, 1.0);
	float base = clamp(VolFbm(plane * 2.3 + 19.7, 2, 0.5, 0) * 1.5 + 0.5, 0.0, 1.0);

	// Around the axis by direction (cos, sin), so the side has no seam.
	float angle = atan(q.z, q.x);
	float rim = clamp(VolFbm(vec3(cos(angle), p.y / volSize.y, sin(angle)) * 2.2 + volNoiseOffset * 0.29, 3, 0.5, 0) * 1.5 + 0.5, 0.0, 1.0);
	float s = volShape == 1 ? length(q.xz) : max(abs(q.x), abs(q.z));
	float limit = 1.0 - volSideRoughness * rim;
	float side = 1.0 - smoothstep(limit - volSideSoftness, limit, s);

	float upper = 1.0 - 2.0 * volTopRoughness * (1.0 - towers);
	float lower = -1.0 + 2.0 * volBottomRoughness * base;
	float capFade = max(2.0 * volCapSoftness, 1e-4);
	float caps = smoothstep(lower, lower + capFade, q.y) * (1.0 - smoothstep(upper - capFade, upper, q.y));
	return side * caps;
}

// Large clumps: which parts of the shape hold cloud at all, so it is not one even slab.
float Clumps(vec3 p)
{
	if (volClumpScale <= 0.0)
	{
		return 1.0;
	}

	float n = VolFbm(p / volClumpScale + volNoiseOffset * 0.37, 3, 0.5, 0) * 1.6 + 0.5;
	return smoothstep(volClumpThreshold - volClumpSoftness, volClumpThreshold + volClumpSoftness, n);
}

float Shape(vec3 p)
{
	float mask = Profile(p);
	if (mask <= 0.0)
	{
		return 0.0;
	}

	mask *= Clumps(p);
	if (mask <= 0.0)
	{
		return 0.0;
	}

	vec3 q = p / volNoiseSize + volNoiseOffset;
	vec3 warp = vec3(VolGradientNoise(q * 0.45 + 11.3, 0), VolGradientNoise(q * 0.45 + 27.1, 0), VolGradientNoise(q * 0.45 + 41.7, 0));
	q += warp * volWarp;

	float n = VolNoiseOfType(q, volNoiseType, volOctaves, volGain);
	return clamp((n - volThreshold) / volSoftness, 0.0, 1.0) * mask;
}

void main()
{
#ifdef VOL_POINTS
	uint i = gl_GlobalInvocationID.x;
	if (i >= uint(volPointCount))
	{
		return;
	}

	// Outside the shape's box there is no cloud (the voxels end there too).
	vec3 p = points[i].xyz;
	densities[i] = any(greaterThan(abs(p), volSize * 0.5)) ? 0.0 : Shape(p);
#else
	ivec3 v = ivec3(gl_GlobalInvocationID.xy, int(gl_GlobalInvocationID.z) + volSliceOffset);
	if (any(greaterThanEqual(v, volResolution)))
	{
		return;
	}

	vec3 p = ((vec3(v) + 0.5) / vec3(volResolution) - 0.5) * volSize;
	imageStore(Density, v, vec4(Shape(p), 0.0, 0.0, 0.0));
#endif
}

/////Compute
