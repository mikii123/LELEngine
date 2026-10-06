//Compute

#version 430

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Radiance cache update: one work group per probe, one thread per tile texel (the 8x8 octahedral map plus
// a one texel border whose texels trace the direction of the octahedral neighbour they mirror, so the
// bilinear lookup is seamless). Every texel traces one jittered ray through the global distance field;
// hits read the surface cache, misses the sky. The estimate is accumulated exponentially, so over a few
// updates each texel converges to the radiance integrated over its solid angle. Probes inside geometry
// are marked invalid.
// Must equal RC_TILE (the layout qualifier has to be a literal on some drivers).
layout(local_size_x = 10, local_size_y = 10) in;

uniform sampler2D FinalLighting;
uniform vec3 giSkyRadiance;
uniform int frameIndex;
uniform float historyWeight; // 0 restarts the accumulation

// Work group -> probe: the first interiorCount groups walk the interior list (round robin from
// interiorStart), the rest walk the exterior list, which follows the interior one in the buffer.
uniform int interiorStart;
uniform int interiorCount;
uniform int interiorTotal;
uniform int exteriorStart;
uniform int exteriorTotal;

layout(std430, binding = 3) readonly buffer RadianceCacheProbeList
{
	int probeList[];
};

layout(binding = 0, rgba16f) uniform image2D RadianceCache;

// Border texels mirror across the octahedral seams; corners take the diagonally opposite corner.
ivec2 WrapOctahedral(ivec2 t)
{
	const int R = RC_RESOLUTION;
	bool xOut = t.x < 0 || t.x >= R;
	bool yOut = t.y < 0 || t.y >= R;
	if (xOut && yOut)
	{
		return ivec2(t.x < 0 ? R - 1 : 0, t.y < 0 ? R - 1 : 0);
	}
	if (xOut)
	{
		return ivec2(clamp(t.x, 0, R - 1), R - 1 - t.y);
	}
	if (yOut)
	{
		return ivec2(R - 1 - t.x, clamp(t.y, 0, R - 1));
	}
	return t;
}

void main()
{
	int group = int(gl_WorkGroupID.x);
	int probeIndex = group < interiorCount
		? probeList[(interiorStart + group) % interiorTotal]
		: probeList[interiorTotal + (exteriorStart + group - interiorCount) % exteriorTotal];
	ivec3 coord = RadianceCacheProbeCoord(probeIndex);
	vec3 position = RadianceCacheProbePosition(coord);
	ivec2 tileTexel = ivec2(gl_LocalInvocationID.xy);
	ivec2 atlasTexel = RadianceCacheTileOrigin(probeIndex) + tileTexel;

	float voxel = SdfVoxelSize();
	if (!InsideSdfGrid(position) || SampleSdf(position) < voxel * 0.5)
	{
		imageStore(RadianceCache, atlasTexel, vec4(0.0));
		return;
	}

	ivec2 oct = WrapOctahedral(tileTexel - 1);
	uint seed = HashCoord(atlasTexel, uint(frameIndex));
	vec2 jitter = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));
	vec3 direction = OctahedralToDirection((vec2(oct) + jitter) / float(RC_RESOLUTION));

	vec3 radiance = giSkyRadiance;
	float hitT;
	float depth = sdfGridSize; // misses: further than anything in the field
	if (TraceSdf(position, direction, sdfGridSize, hitT))
	{
		vec3 hit = position + direction * hitT;
		if (!SampleSurfaceCacheAtHit(hit, SdfNormal(hit), voxel * 2.0, FinalLighting, radiance))
		{
			radiance = vec3(0.0);
		}
		depth = hitT;
	}

	// Alpha stores hit distance + 1 (0 = no data); both radiance and depth are accumulated over updates.
	vec4 history = imageLoad(RadianceCache, atlasTexel);
	float weight = history.a > 0.5 ? historyWeight : 0.0;
	float historyDepth = max(history.a - 1.0, 0.0);
	imageStore(RadianceCache, atlasTexel, vec4(mix(radiance, history.rgb, weight), mix(depth, historyDepth, weight) + 1.0));
}

/////Compute
