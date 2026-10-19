//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Per-probe history record and structured importance sampling (Lumen final gather). One thread per probe:
//   1. find the probe's surface point in the previous frame (its motion vector, then the previous camera) and the
//      probe it came from: the uniform cell there or, when that probe lay on another surface, the closest adaptive
//      probe of that tile. A history probe must lie on the same surface: close, on the same plane, facing the same
//      way (its octahedral directions are relative to its normal). It is the source of the temporal probe filter.
//   2. rank the 64 octahedral directions by last frame's filtered radiance times the cosine term and assign the 64
//      rays so that every direction keeps being traced (Lumen's structured importance sampling): the dimmest 2x2
//      blocks of directions (below mergeFraction of the mean block) get one ray per block, and every ray saved that
//      way supersamples one of the brightest directions (four rays, one per quadrant). The others get one ray each.
//      A light that appears in a dark direction is therefore seen the next frame, and one that leaves is retraced.
// The ranking is deterministic: a random tie-breaker would reshuffle the sampled set every frame and the
// probe's colour with it.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D PreviousProbeRadiance;
uniform sampler2D PreviousProbeAnchorPosition;
uniform sampler2D PreviousProbeAnchorNormal;
uniform sampler2D Motion;                 // world-space motion since the previous frame (geometry prepass)
uniform usampler2D AdaptivePixel;         // packed anchor pixel per adaptive probe
uniform mat4 previousViewProjection;
uniform ivec2 previousProbeJitter;
uniform int historyValid;
uniform int importanceEnabled;
uniform float historyDistanceThreshold; // world units
uniform float mergeFraction;            // blocks below this fraction of the mean block importance get one ray

layout(binding = 0, rgba32ui) uniform writeonly uimage2D ProbeSelection;
layout(binding = 1, r16ui) uniform writeonly uimage2D ProbeRayTable;

#define MAX_MERGED_BLOCKS 12

float Luminance(vec3 c)
{
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

// Distance of a previous probe from the point, or a large value when it lies on another surface.
float HistoryDistance(vec4 anchorPosition, vec3 anchorNormal, vec3 point, vec3 normal)
{
	if (anchorPosition.w < 0.5) return 1e9;
	vec3 offset = anchorPosition.xyz - point;
	if (dot(anchorNormal, normal) < 0.9 || abs(dot(normal, offset)) > 0.5 * historyDistanceThreshold) return 1e9;
	return length(offset);
}

void WriteRay(ivec2 probe, int ray, uint entry)
{
	imageStore(ProbeRayTable, probe * PROBE_RESOLUTION + ivec2(ray % PROBE_RESOLUTION, ray / PROBE_RESOLUTION), uvec4(entry));
}

void main()
{
	ivec2 probe = ivec2(gl_GlobalInvocationID.xy);
	if (probe.x >= probeCount.x || probe.y >= probeAtlasRows) return;

	uvec4 invalid = uvec4(0u);

	vec3 position, normal;
	if (historyValid == 0 || !ProbeAnchor(probe, position, normal))
	{
		imageStore(ProbeSelection, probe, invalid);
		return;
	}

	// Where was this surface point last frame?
	ivec2 anchorPixel = probe.y < probeCount.y ? ProbeAnchorPixel(probe) : UnpackPixel(texelFetch(AdaptivePixel, ivec2(probe.x, probe.y - probeCount.y), 0).r);
	vec4 motion = texelFetch(Motion, anchorPixel, 0);
	bool moving = motion.w > 0.5;
	vec3 previousPosition = position - motion.xyz;
	vec4 clip = previousViewProjection * vec4(previousPosition, 1.0);
	if (clip.w <= 0.0)
	{
		imageStore(ProbeSelection, probe, invalid);
		return;
	}
	vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
	{
		imageStore(ProbeSelection, probe, invalid);
		return;
	}
	vec2 pixel = uv * vec2(screenSize);
	ivec2 previous = clamp(ivec2(floor((pixel - vec2(previousProbeJitter)) / float(probeSpacing) + 0.5)), ivec2(0), probeCount - 1);

	float bestDistance = HistoryDistance(texelFetch(PreviousProbeAnchorPosition, previous, 0), texelFetch(PreviousProbeAnchorNormal, previous, 0).xyz, previousPosition, normal);
	if (bestDistance > historyDistanceThreshold)
	{
		// The uniform probe there lay on another surface: try last frame's adaptive probes of that tile.
		ivec2 previousPixel = clamp(ivec2(pixel), ivec2(0), screenSize - 1);
		int tileOffset = AdaptiveTileOffset(previousPixel);
		int count = PreviousAdaptiveTileCount(tileOffset);
		for (int k = 0; k < count; k++)
		{
			ivec2 candidate = AdaptiveProbeTexel(PreviousAdaptiveTileProbe(tileOffset, k));
			float d = HistoryDistance(texelFetch(PreviousProbeAnchorPosition, candidate, 0), texelFetch(PreviousProbeAnchorNormal, candidate, 0).xyz, previousPosition, normal);
			if (d < bestDistance)
			{
				bestDistance = d;
				previous = candidate;
			}
		}
	}

	if (bestDistance > historyDistanceThreshold)
	{
		imageStore(ProbeSelection, probe, invalid);
		return;
	}

	if (importanceEnabled == 0)
	{
		imageStore(ProbeSelection, probe, PackSelection(previous, false, moving, 0u, uvec2(0u)));
		return;
	}

	// ---- importance of every direction and of every 2x2 block of directions
	ivec2 previousBase = previous * PROBE_RESOLUTION;
	float importance[64];
	float blockImportance[16];
	for (int b = 0; b < 16; b++) blockImportance[b] = 0.0;
	float total = 0.0;
	for (int i = 0; i < 64; i++)
	{
		ivec2 oct = ivec2(i % PROBE_RESOLUTION, i / PROBE_RESOLUTION);
		vec4 prev = texelFetch(PreviousProbeRadiance, previousBase + oct, 0);
		float cosTheta = max(HemiOctahedralToDirection((vec2(oct) + 0.5) / float(PROBE_RESOLUTION)).z, 0.05);
		importance[i] = (Luminance(prev.rgb) + 0.02) * cosTheta;
		blockImportance[TexelBlock(i)] += importance[i];
		total += importance[i];
	}

	// ---- the dimmest blocks, in increasing order, while below the threshold
	float threshold = mergeFraction * total / 16.0;
	uint mergedMask = 0u;
	int merged = 0;
	for (int k = 0; k < MAX_MERGED_BLOCKS; k++)
	{
		int dimmest = -1;
		float dimmestValue = threshold;
		for (int b = 0; b < 16; b++)
		{
			if ((mergedMask & (1u << uint(b))) == 0u && blockImportance[b] < dimmestValue)
			{
				dimmestValue = blockImportance[b];
				dimmest = b;
			}
		}
		if (dimmest < 0) break;
		mergedMask |= 1u << uint(dimmest);
		merged++;
	}

	// ---- as many of the brightest directions outside the merged blocks get four rays
	uvec2 refinedMask = uvec2(0u);
	for (int k = 0; k < merged; k++)
	{
		int brightest = 0;
		float brightestValue = -1.0;
		for (int i = 0; i < 64; i++)
		{
			bool taken = (i < 32 ? (refinedMask.x >> uint(i)) : (refinedMask.y >> uint(i - 32))) % 2u != 0u;
			if (!taken && (mergedMask & (1u << uint(TexelBlock(i)))) == 0u && importance[i] > brightestValue)
			{
				brightestValue = importance[i];
				brightest = i;
			}
		}
		if (brightest < 32) refinedMask.x |= 1u << uint(brightest);
		else refinedMask.y |= 1u << uint(brightest - 32);
	}

	// ---- the table: merged blocks, then every other direction (quadrant 0 of a refined one), then the extra quadrants
	// of the refined directions, all in index order (ProbeCompose finds a direction's rays from the masks alone, see
	// TexelRays in ScreenProbes.glsl).
	int ray = 0;
	for (int b = 0; b < 16; b++)
	{
		if ((mergedMask & (1u << uint(b))) != 0u) WriteRay(probe, ray++, PackRay(BlockFirstTexel(b), RAY_BLOCK, 0));
	}
	for (int i = 0; i < 64; i++)
	{
		if ((mergedMask & (1u << uint(TexelBlock(i)))) != 0u) continue;
		bool isRefined = (i < 32 ? (refinedMask.x >> uint(i)) : (refinedMask.y >> uint(i - 32))) % 2u != 0u;
		WriteRay(probe, ray++, PackRay(i, isRefined ? RAY_QUADRANT : RAY_WHOLE_TEXEL, 0));
	}
	for (int i = 0; i < 64; i++)
	{
		bool isRefined = (i < 32 ? (refinedMask.x >> uint(i)) : (refinedMask.y >> uint(i - 32))) % 2u != 0u;
		if (!isRefined) continue;
		for (int q = 1; q < 4; q++) WriteRay(probe, ray++, PackRay(i, RAY_QUADRANT, q));
	}

	imageStore(ProbeSelection, probe, PackSelection(previous, true, moving, mergedMask, refinedMask));
}

/////Compute
