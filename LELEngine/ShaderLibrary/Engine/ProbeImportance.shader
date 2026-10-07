//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Per-probe history record and structured importance sampling (Lumen final gather). One thread per probe:
//   1. reproject the probe's surface point into the previous frame and find the probe it came from: the
//      uniform cell there, or, when that probe lay on another surface, the closest adaptive probe of that
//      tile (the history source for the temporal probe filter and for untraced directions);
//   2. rank the 64 octahedral directions by last frame's filtered radiance times the cosine term and pick
//      the most important ones for 2x2 rays. A few slots rotate through all directions so every direction
//      is retraced within 16 frames and stale history cannot linger (Lumen refines the ray budget the same
//      way: supersample bright directions, keep the rest from history).
// The ranking is deterministic: a random tie-breaker would reshuffle the sampled set every frame and the
// probe's colour with it.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D PreviousProbeRadiance;
uniform sampler2D PreviousProbeAnchorPosition;
uniform mat4 previousViewProjection;
uniform ivec2 previousProbeJitter;
uniform int historyValid;
uniform int importanceEnabled;
uniform float historyDistanceThreshold; // world units
uniform int frameIndex;

layout(binding = 0, rgba32ui) uniform writeonly uimage2D ProbeSelection;

#define ROTATING_DIRECTIONS 4

float Luminance(vec3 c)
{
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
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
	vec4 clip = previousViewProjection * vec4(position, 1.0);
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

	vec4 previousAnchor = texelFetch(PreviousProbeAnchorPosition, previous, 0);
	float bestDistance = previousAnchor.w > 0.5 ? length(previousAnchor.xyz - position) : 1e9;
	if (bestDistance > historyDistanceThreshold)
	{
		// The uniform probe there lay on another surface: try last frame's adaptive probes of that tile.
		ivec2 previousPixel = clamp(ivec2(pixel), ivec2(0), screenSize - 1);
		int tileOffset = AdaptiveTileOffset(previousPixel);
		int count = PreviousAdaptiveTileCount(tileOffset);
		for (int k = 0; k < count; k++)
		{
			ivec2 candidate = AdaptiveProbeTexel(PreviousAdaptiveTileProbe(tileOffset, k));
			vec4 candidateAnchor = texelFetch(PreviousProbeAnchorPosition, candidate, 0);
			if (candidateAnchor.w < 0.5) continue;
			float d = length(candidateAnchor.xyz - position);
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

	int selected[IMPORTANT_DIRECTIONS];
	for (int k = 0; k < IMPORTANT_DIRECTIONS; k++) selected[k] = 0;

	if (importanceEnabled == 0)
	{
		imageStore(ProbeSelection, probe, PackSelection(selected, previous, false));
		return;
	}

	ivec2 previousBase = previous * PROBE_RESOLUTION;

	float importance[64];
	for (int i = 0; i < 64; i++)
	{
		ivec2 oct = ivec2(i % PROBE_RESOLUTION, i / PROBE_RESOLUTION);
		vec4 prev = texelFetch(PreviousProbeRadiance, previousBase + oct, 0);
		float cosTheta = max(HemiOctahedralToDirection((vec2(oct) + 0.5) / float(PROBE_RESOLUTION)).z, 0.05);
		importance[i] = (Luminance(prev.rgb) + 0.02) * cosTheta;
	}

	// Rotating slots: bit-reversed frame counter spreads consecutive frames over the hemisphere. Each probe
	// starts the 16-frame cycle at its own phase, so the screen never refreshes the same direction in unison.
	uint phase = HashCoord(probe, 7u) & 63u;
	for (int k = 0; k < ROTATING_DIRECTIONS; k++)
	{
		int d = int(bitfieldReverse(uint(frameIndex * ROTATING_DIRECTIONS + k) + phase * uint(ROTATING_DIRECTIONS)) >> 26u);
		selected[k] = d;
		importance[d] = -2.0;
	}

	for (int k = ROTATING_DIRECTIONS; k < IMPORTANT_DIRECTIONS; k++)
	{
		int best = 0;
		float bestValue = -1.0;
		for (int i = 0; i < 64; i++)
		{
			if (importance[i] > bestValue)
			{
				bestValue = importance[i];
				best = i;
			}
		}
		selected[k] = best;
		importance[best] = -2.0;
	}

	imageStore(ProbeSelection, probe, PackSelection(selected, previous, true));
}

/////Compute
