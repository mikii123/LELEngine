//Compute

#version 430

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Radiance cache probe tracing. Lumen traces its world probes at a higher resolution than it stores them
// (32x32 rays, 16x16 stored): every probe scheduled this frame gets rcTraceResolution^2 jittered rays over
// the full sphere, one thread per ray, written to a scratch buffer of per-slot tiles.
// RadianceCacheResolve.shader then downsamples each tile into the probe's stored map and accumulates it, so
// every stored texel averages several rays per update instead of one.
//
// Work groups are 16x16 rays; a probe uses (rcTraceResolution / 16)^2 consecutive groups.
layout(local_size_x = 16, local_size_y = 16) in;

uniform sampler2D FinalLighting;
uniform vec3 giSkyRadiance;
uniform int frameIndex;
uniform int rcTraceResolution;   // rays per axis per probe, a multiple of 16
uniform int traceSlotsPerRow;    // tiles per scratch buffer row

// Sequence index -> probe: the first interiorCount entries walk the interior list (round robin from
// interiorStart), the rest walk the exterior list, which follows the interior one in the buffer.
uniform int sequenceOffset;
uniform int interiorStart;
uniform int interiorCount;
uniform int interiorTotal;
uniform int exteriorStart;
uniform int exteriorTotal;

layout(std430, binding = 3) readonly buffer RadianceCacheProbeList
{
	int probeList[];
};

layout(binding = 0, rgba16f) uniform writeonly image2D TraceBuffer; // rgb radiance, a = hit distance (-1: probe invalid)

int SequenceProbe(int q)
{
	return q < interiorCount
		? probeList[(interiorStart + q) % interiorTotal]
		: probeList[interiorTotal + (exteriorStart + q - interiorCount) % exteriorTotal];
}

void main()
{
	int tilesPerAxis = rcTraceResolution / 16;
	int groupsPerProbe = tilesPerAxis * tilesPerAxis;
	int slot = int(gl_WorkGroupID.x) / groupsPerProbe;
	int subTile = int(gl_WorkGroupID.x) % groupsPerProbe;
	ivec2 texel = ivec2(subTile % tilesPerAxis, subTile / tilesPerAxis) * 16 + ivec2(gl_LocalInvocationID.xy);
	ivec2 bufferTexel = ivec2(slot % traceSlotsPerRow, slot / traceSlotsPerRow) * rcTraceResolution + texel;

	int probeIndex = SequenceProbe(sequenceOffset + slot);
	vec3 position = RadianceCacheProbePosition(RadianceCacheProbeCoord(probeIndex));

	float voxel = SdfVoxelSize();
	if (!InsideSdfGrid(position) || SampleSdf(position) < voxel * 0.5)
	{
		imageStore(TraceBuffer, bufferTexel, vec4(0.0, 0.0, 0.0, -1.0));
		return;
	}

	uint seed = HashCoord(bufferTexel, uint(frameIndex));
	vec2 jitter = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));
	vec3 direction = OctahedralToDirection((vec2(texel) + jitter) / float(rcTraceResolution));

	vec3 radiance = giSkyRadiance;
	float depth = sdfGridSize; // misses: further than anything in the field
	float hitT;
	if (TraceSdf(position, direction, sdfGridSize, hitT))
	{
		vec3 hit = position + direction * hitT;
		if (!SampleSurfaceCacheAtHit(hit, SdfNormal(hit), voxel * 2.0, FinalLighting, radiance))
		{
			radiance = vec3(0.0);
		}
		depth = hitT;
	}

	imageStore(TraceBuffer, bufferTexel, vec4(radiance, depth));
}

/////Compute
