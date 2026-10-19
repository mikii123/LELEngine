//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Projects the stored octahedral map of every probe updated this frame onto second-order spherical harmonics
// (the probe schedule of RadianceCacheResolve.shader). Surfaces that are not in the depth buffer (transparent
// materials) light themselves from these: Engine/VolumeProbes.glsl interpolates the eight probes around a point,
// the way Lumen lights translucency from a probe volume. The 27 coefficient values and a valid flag are packed into
// 7 RGBA layers (the layout of ProbeSH.shader), one texel per probe tile. One work group per probe slot.
layout(local_size_x = 8, local_size_y = 8) in;

uniform int sequenceOffset;
uniform int interiorStart;
uniform int interiorCount;
uniform int interiorTotal;
uniform int exteriorStart;
uniform int exteriorTotal;
uniform int priorityCount;  // probes around this frame's changes, taken first (RadianceCachePass.BuildPriority)
uniform int priorityOffset; // where they are in the list (after the interior and exterior lists)

layout(std430, binding = 3) readonly buffer RadianceCacheProbeList
{
	int probeList[];
};

layout(binding = 0, rgba16f) uniform writeonly image2DArray RadianceCacheSH;

shared vec3 partial[64 * 9];

int SequenceProbe(int q)
{
	if (q < priorityCount) return probeList[priorityOffset + q];
	q -= priorityCount;
	return q < interiorCount
		? probeList[(interiorStart + q) % interiorTotal]
		: probeList[interiorTotal + (exteriorStart + q - interiorCount) % exteriorTotal];
}

void ReduceStep(uint thread, uint stride)
{
	if (thread < stride)
	{
		for (int i = 0; i < 9; i++) partial[thread * 9u + uint(i)] += partial[(thread + stride) * 9u + uint(i)];
	}
}

void main()
{
	int probeIndex = SequenceProbe(sequenceOffset + int(gl_WorkGroupID.x));
	int resolution = rcProbeResolution;
	ivec2 interior = RadianceCacheTileOrigin(probeIndex) + 1;
	uint thread = gl_LocalInvocationIndex;

	// The full-sphere octahedral map is close to equal area: each texel covers 4 pi / n^2 steradians.
	float texelSolidAngle = 4.0 * SAMPLING_PI / float(resolution * resolution);
	vec3 c[9];
	for (int i = 0; i < 9; i++) c[i] = vec3(0.0);
	for (int y = int(gl_LocalInvocationID.y); y < resolution; y += 8)
	{
		for (int x = int(gl_LocalInvocationID.x); x < resolution; x += 8)
		{
			vec4 s = texelFetch(RadianceCacheAtlas, interior + ivec2(x, y), 0);
			if (s.a < 0.5) continue; // no data yet
			vec3 d = OctahedralToDirection((vec2(x, y) + 0.5) / float(resolution));
			float sh[9];
			SHBasis9(d, sh);
			for (int i = 0; i < 9; i++) c[i] += s.rgb * sh[i] * texelSolidAngle;
		}
	}

	for (int i = 0; i < 9; i++) partial[thread * 9u + uint(i)] = c[i];
	barrier();

	// Tree reduction over the 64 threads, unrolled: GLSL 4.30 compilers warn about barrier() inside a loop.
	ReduceStep(thread, 32u);
	barrier();
	ReduceStep(thread, 16u);
	barrier();
	ReduceStep(thread, 8u);
	barrier();
	ReduceStep(thread, 4u);
	barrier();
	ReduceStep(thread, 2u);
	barrier();
	ReduceStep(thread, 1u);
	barrier();

	if (thread == 0u)
	{
		for (int i = 0; i < 9; i++) c[i] = partial[i];
		// A probe with data has every texel resolved (the resolve writes whole tiles): test one.
		bool valid = texelFetch(RadianceCacheAtlas, interior + ivec2(resolution / 2), 0).a >= 0.5;
		ivec2 texel = ivec2(probeIndex % rcProbesPerRow, probeIndex / rcProbesPerRow);
		imageStore(RadianceCacheSH, ivec3(texel, 0), vec4(c[0], c[1].r));
		imageStore(RadianceCacheSH, ivec3(texel, 1), vec4(c[1].gb, c[2].rg));
		imageStore(RadianceCacheSH, ivec3(texel, 2), vec4(c[2].b, c[3]));
		imageStore(RadianceCacheSH, ivec3(texel, 3), vec4(c[4], c[5].r));
		imageStore(RadianceCacheSH, ivec3(texel, 4), vec4(c[5].gb, c[6].rg));
		imageStore(RadianceCacheSH, ivec3(texel, 5), vec4(c[6].b, c[7]));
		imageStore(RadianceCacheSH, ivec3(texel, 6), vec4(c[8], valid ? 1.0 : 0.0));
	}
}

/////Compute
