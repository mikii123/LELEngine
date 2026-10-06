//Compute

#version 430

#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Adaptive probe placement (Lumen): one thread per candidate pixel of the current hierarchy level (every
// placementFactor pixels; the pass runs for probeSpacing / 2, then probeSpacing / 4). The candidate's
// coverage is the plane- and normal-weighted interpolation weight it would get from the four surrounding
// uniform probes plus the adaptive probes already placed in its tile. Below the minimum coverage a new probe
// is allocated: its anchor goes to the adaptive rows of the probe atlas and its index to the tile list, so
// ProbeIntegrate.shader can interpolate it and ProbeFilter.shader can find its neighbours.
layout(local_size_x = 8, local_size_y = 8) in;

uniform int placementFactor;
uniform float planeTolerance;
uniform float minCoverage;
uniform int maxAdaptiveProbes;

layout(binding = 0, rgba32f) uniform writeonly image2D OutAnchorPosition;
layout(binding = 1, rgba16f) uniform writeonly image2D OutAnchorNormal;
layout(binding = 2, r32ui) uniform writeonly uimage2D OutAdaptivePixel; // packed pixel per adaptive probe

float ProbeWeight(ivec2 probe, vec3 position, vec3 N)
{
	vec3 pPosition, pNormal;
	if (!ProbeAnchor(probe, pPosition, pNormal)) return 0.0;
	float planeDistance = abs(dot(N, pPosition - position));
	float normalWeight = max(dot(N, pNormal), 0.0);
	if (planeDistance > planeTolerance || normalWeight < 0.5) return 0.0;
	return normalWeight * (1.0 - planeDistance / planeTolerance);
}

void main()
{
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy) * placementFactor + placementFactor / 2;
	if (any(greaterThanEqual(pixel, screenSize))) return;

	float depth = texelFetch(SceneDepth, pixel, 0).r;
	if (depth >= 1.0) return;

	vec3 position = ReconstructPosition(pixel, depth);
	vec3 N = normalize(texelFetch(NormalRoughness, pixel, 0).xyz);

	// Coverage from the uniform grid: bilinear weights times the plane / normal agreement.
	vec2 probeCoord = (vec2(pixel) - vec2(probeJitter)) / float(probeSpacing);
	ivec2 base = ivec2(floor(probeCoord));
	vec2 f = probeCoord - vec2(base);
	float coverage = 0.0;
	for (int i = 0; i < 4; i++)
	{
		ivec2 offset = ivec2(i & 1, i >> 1);
		ivec2 probe = clamp(base + offset, ivec2(0), probeCount - 1);
		float bilinear = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y);
		coverage += bilinear * ProbeWeight(probe, position, N);
	}

	// Coverage from adaptive probes already placed in this tile (previous hierarchy levels).
	int tileOffset = AdaptiveTileOffset(pixel);
	int count = AdaptiveTileCount(tileOffset);
	for (int k = 0; k < count; k++)
	{
		ivec2 probe = AdaptiveProbeTexel(AdaptiveTileProbe(tileOffset, k));
		coverage += AdaptiveSpatialWeight(pixel, AdaptiveTilePixel(tileOffset, k)) * ProbeWeight(probe, position, N);
	}

	if (coverage >= minCoverage) return;

	uint index = atomicAdd(adaptiveProbeCount, 1u);
	if (index >= uint(maxAdaptiveProbes)) return;

	uint slot = atomicAdd(adaptiveTileData[tileOffset], 1u);
	if (slot >= uint(MAX_ADAPTIVE_PER_TILE)) return;

	uint packedPixel = PackPixel(pixel);
	adaptiveTileData[tileOffset + 1 + 2 * int(slot)] = index;
	adaptiveTileData[tileOffset + 2 + 2 * int(slot)] = packedPixel;

	ivec2 texel = AdaptiveProbeTexel(index);
	imageStore(OutAnchorPosition, texel, vec4(position, 1.0));
	imageStore(OutAnchorNormal, texel, vec4(N, 1.0));
	imageStore(OutAdaptivePixel, ivec2(texel.x, texel.y - probeCount.y), uvec4(packedPixel, 0u, 0u, 0u));
}

/////Compute
