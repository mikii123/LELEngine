//Compute

#version 430

#include "Engine/ScreenProbes.glsl"

// Turns the per-probe ray results back into the octahedral radiance map and applies Lumen's temporal probe
// filter: directions traced this frame (2x2 rays when importance sampled) are blended with the reprojected
// previous radiance of the same direction; directions skipped by importance sampling keep that history.
//
// The history is the previous frame's *unfiltered* composed radiance. Feeding the spatially filtered map
// back would filter the carried-over directions again every frame, blurring lighting along surfaces without
// bound and leaking bright sky samples across whole walls.
//
// Accumulation is sample-count based: a texel's history weight grows with the number of samples it holds
// (up to probeMaxHistorySamples), so lighting that stays put converges to a noise-free value. One workgroup
// is one probe, which lets the lighting-change detector run in the same pass: the radiance of this frame's
// traced directions is summed over the probe and compared with the reprojected history of the same
// directions (summing divides the per-direction sampling noise by ~4), and when they disagree the probe
// really changed (an emitter moved or pulsed, an occluder passed): its texels restart their counts and
// follow the change within a few frames instead of averaging the old lighting in for many frames.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D RayResults;
uniform sampler2D PreviousProbeRadiance;
uniform sampler2D PreviousProbeCount;
uniform usampler2D ProbeSelection;
uniform vec3 giSkyRadiance;
uniform float probeHistoryWeight;     // constant history weight when probeMaxHistorySamples is 0
uniform float probeMaxHistorySamples; // cap of the per-texel sample count (0 = constant weight)
uniform float changeThreshold;        // relative change of the summed radiance that counts as a lighting change

// fp16 is enough here: unlike the radiance cache, the fresh samples are noisy (one to four rays per texel), so
// the per-update change is far above the fp16 quantum and the accumulation does not stall. fp32 was measured
// to cost 0.35 ms (compose write + filter reads) for no visible change.
layout(binding = 0, rgba16f) uniform writeonly image2D OutRadiance;
layout(binding = 1, r32f) uniform writeonly image2D OutCount;

// Probe sums in 24.8 fixed point (shared-memory atomics; luminance capped at 1e4 keeps 64 lanes in range).
shared int freshFixed;
shared int historyFixed;
shared int comparedCount;

float Luminance(vec3 c)
{
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

void main()
{
	ivec2 probe = ivec2(gl_WorkGroupID.xy);
	ivec2 oct = ivec2(gl_LocalInvocationID.xy);
	ivec2 texel = probe * PROBE_RESOLUTION + oct;
	int octIndex = oct.y * PROBE_RESOLUTION + oct.x;

	if (gl_LocalInvocationIndex == 0u)
	{
		freshFixed = 0;
		historyFixed = 0;
		comparedCount = 0;
	}
	barrier();

	uvec4 selection = texelFetch(ProbeSelection, probe, 0);

	vec4 history = vec4(0.0, 0.0, 0.0, -2.0);
	float previousCount = 0.0;
	if (ReprojectionValid(selection))
	{
		ivec2 previous = SelectionPreviousProbe(selection);
		history = texelFetch(PreviousProbeRadiance, previous * PROBE_RESOLUTION + oct, 0);
		previousCount = texelFetch(PreviousProbeCount, previous * PROBE_RESOLUTION + oct, 0).r;
	}

	vec4 fresh = vec4(0.0, 0.0, 0.0, -2.0);
	bool traced = false;
	if (!SelectionValid(selection))
	{
		fresh = texelFetch(RayResults, texel, 0);
		traced = true;
	}
	else
	{
		ivec2 rayBase = probe * PROBE_RESOLUTION;
		for (int slot = 0; slot < IMPORTANT_DIRECTIONS; slot++)
		{
			if (SelectedTexelIndex(selection, slot) != octIndex) continue;

			vec3 sum = vec3(0.0);
			float hitSum = 0.0;
			int hits = 0;
			for (int s = 0; s < 4; s++)
			{
				int ray = slot * 4 + s;
				vec4 r = texelFetch(RayResults, rayBase + ivec2(ray % PROBE_RESOLUTION, ray / PROBE_RESOLUTION), 0);
				sum += r.rgb;
				if (r.a >= 0.0)
				{
					hitSum += r.a;
					hits++;
				}
			}
			fresh = vec4(sum * 0.25, hits > 0 ? hitSum / float(hits) : -1.0);
			traced = true;
			break;
		}
	}

	// ---- change detection over the probe: traced directions with a history take part
	if (traced && history.a > -1.5)
	{
		atomicAdd(freshFixed, int(min(Luminance(fresh.rgb), 1e4) * 256.0));
		atomicAdd(historyFixed, int(min(Luminance(history.rgb), 1e4) * 256.0));
		atomicAdd(comparedCount, 1);
	}
	barrier();
	float freshSum = float(freshFixed) / 256.0;
	float historySum = float(historyFixed) / 256.0;
	if (comparedCount > 0 && abs(freshSum - historySum) > changeThreshold * (historySum + 0.05 * float(comparedCount)))
	{
		previousCount = 0.0;
	}

	if (!traced)
	{
		// Not traced this frame: history, or sky when there is none.
		bool hasHistory = history.a > -1.5;
		imageStore(OutRadiance, texel, hasHistory ? history : vec4(giSkyRadiance, -1.0));
		imageStore(OutCount, texel, vec4(hasHistory ? previousCount : 0.0));
		return;
	}

	float count = 1.0;
	if (fresh.a > -1.5 && history.a > -1.5)
	{
		float weight = probeHistoryWeight;
		if (probeMaxHistorySamples > 0.0)
		{
			float n = min(previousCount, probeMaxHistorySamples - 1.0);
			weight = n / (n + 1.0);
			count = n + 1.0;
		}
		fresh.rgb = mix(fresh.rgb, history.rgb, weight);
	}
	imageStore(OutRadiance, texel, fresh);
	imageStore(OutCount, texel, vec4(count));
}

/////Compute
