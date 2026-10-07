//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"

// Spatial filter between neighbouring probes (Lumen final gather, step 2). For each octahedral texel the
// same direction is gathered from the neighbouring probes, but a neighbour's sample is only used when
// its hit point, seen from the centre probe, lies close to the centre probe's own ray direction: this
// keeps lighting from being blurred across occluders or depth discontinuities, while nearby probes that
// see the same surface are averaged to reduce variance. Uniform probes gather from the 3x3 grid around
// them; adaptive probes from the four uniform probes around their pixel.
//
// One workgroup per probe: the neighbours' anchors, plane / normal weights and tangent frames are resolved
// once into shared memory instead of once per texel (the 64 texels of a probe share them).
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D ProbeRadiance;
uniform usampler2D AdaptivePixel; // packed pixel per adaptive probe (rows below the uniform grid)
uniform float planeTolerance;   // world units
uniform float angleThreshold;   // cosine of the maximal direction error

layout(binding = 0, rgba16f) uniform writeonly image2D OutRadiance;

#define MAX_NEIGHBOURS 8

shared bool centreValid;
shared vec3 centrePosition;
shared vec3 centreNormal;
shared vec3 centreTangent;
shared vec3 centreBitangent;
shared int neighbourCount;
shared ivec2 neighbourProbe[MAX_NEIGHBOURS];
shared vec3 neighbourPosition[MAX_NEIGHBOURS];
shared vec3 neighbourNormal[MAX_NEIGHBOURS];
shared vec3 neighbourTangent[MAX_NEIGHBOURS];
shared vec3 neighbourBitangent[MAX_NEIGHBOURS];
shared float neighbourWeight[MAX_NEIGHBOURS]; // plane / normal agreement, 0 = rejected

void main()
{
	ivec2 probe = ivec2(gl_WorkGroupID.xy);
	ivec2 oct = ivec2(gl_LocalInvocationID.xy);
	ivec2 texel = probe * PROBE_RESOLUTION + oct;
	int lane = int(gl_LocalInvocationIndex);

	// ---- per-probe setup: lane 0 the centre, lanes 1..8 one neighbour each
	vec3 position, normal;
	bool valid = ProbeAnchor(probe, position, normal);
	if (lane == 0)
	{
		centreValid = valid;
		centrePosition = position;
		centreNormal = normal;
		TangentFrame(normal, centreTangent, centreBitangent);
		neighbourCount = probe.y < probeCount.y ? 8 : 4;
	}
	else if (lane <= MAX_NEIGHBOURS)
	{
		int n = lane - 1;
		ivec2 neighbour = ivec2(-1);
		if (probe.y < probeCount.y)
		{
			// 3x3 ring around a uniform probe (skipping the centre).
			int index = n < 4 ? n : n + 1;
			neighbour = probe + ivec2(index % 3, index / 3) - 1;
			if (any(lessThan(neighbour, ivec2(0))) || any(greaterThanEqual(neighbour, probeCount))) neighbour = ivec2(-1);
		}
		else if (n < 4)
		{
			ivec2 pixel = UnpackPixel(texelFetch(AdaptivePixel, ivec2(probe.x, probe.y - probeCount.y), 0).r);
			ivec2 base = ivec2(floor((vec2(pixel) - vec2(probeJitter)) / float(probeSpacing)));
			neighbour = clamp(base + ivec2(n & 1, n >> 1), ivec2(0), probeCount - 1);
		}

		float w = 0.0;
		vec3 nPosition = vec3(0.0), nNormal = vec3(0.0, 1.0, 0.0);
		if (neighbour.x >= 0 && valid && ProbeAnchor(neighbour, nPosition, nNormal))
		{
			float planeDistance = abs(dot(normal, nPosition - position));
			float normalWeight = max(dot(normal, nNormal), 0.0);
			if (planeDistance <= planeTolerance && normalWeight >= 0.7)
			{
				w = normalWeight * (1.0 - planeDistance / planeTolerance);
			}
		}
		neighbourProbe[n] = neighbour;
		neighbourPosition[n] = nPosition;
		neighbourNormal[n] = nNormal;
		TangentFrame(nNormal, neighbourTangent[n], neighbourBitangent[n]);
		neighbourWeight[n] = w;
	}
	barrier();

	vec4 center = texelFetch(ProbeRadiance, texel, 0);
	if (center.a < -1.5 || !centreValid)
	{
		imageStore(OutRadiance, texel, center);
		return;
	}

	// Centre direction of the texel. The stored radiance is the accumulated average over the texel, so the
	// neighbour test uses texel centres rather than this frame's jittered sub-positions: otherwise the set
	// of accepted neighbours would change every frame and the filtered value would flicker even where the
	// probes themselves have converged.
	vec3 h = HemiOctahedralToDirection((vec2(oct) + 0.5) / float(PROBE_RESOLUTION));
	vec3 direction = normalize(centreTangent * h.x + centreBitangent * h.y + centreNormal * h.z);

	vec3 sum = center.rgb;
	float weightSum = 1.0;
	for (int n = 0; n < neighbourCount; n++)
	{
		float w = neighbourWeight[n];
		if (w <= 0.0) continue;

		vec4 s = texelFetch(ProbeRadiance, neighbourProbe[n] * PROBE_RESOLUTION + oct, 0);
		if (s.a < -1.5) continue;

		// Where did the neighbour's ray end, and would our ray have gone there too? Misses count as hits
		// at a large distance so sky samples pass the same direction test as surface hits.
		float hitDistance = s.a >= 0.0 ? s.a : 1e4;
		vec3 nDirection = normalize(neighbourTangent[n] * h.x + neighbourBitangent[n] * h.y + neighbourNormal[n] * h.z);
		vec3 toHit = neighbourPosition[n] + nDirection * hitDistance - centrePosition;
		float distanceToHit = length(toHit);
		if (distanceToHit < 1e-3) continue;
		float alignment = dot(toHit / distanceToHit, direction);
		if (alignment < angleThreshold) continue;
		w *= (alignment - angleThreshold) / (1.0 - angleThreshold);

		sum += s.rgb * w;
		weightSum += w;
	}

	imageStore(OutRadiance, texel, vec4(sum / weightSum, center.a));
}

/////Compute
