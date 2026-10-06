//Vertex

#version 430

out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"

// Spatial filter between neighbouring probes (Lumen final gather, step 2). For each octahedral texel the
// same direction is gathered from the neighbouring probes, but a neighbour's sample is only used when
// its hit point, seen from the centre probe, lies close to the centre probe's own ray direction: this
// keeps lighting from being blurred across occluders or depth discontinuities, while nearby probes that
// see the same surface are averaged to reduce variance.
out vec4 OutRadiance;

uniform sampler2D ProbeRadiance;
uniform float planeTolerance;   // world units
uniform int filterRadius;       // probes
uniform vec2 directionJitter;
uniform float angleThreshold;   // cosine of the maximal direction error

vec3 ProbeDirection(vec3 normal, ivec2 oct)
{
	vec2 uv = (vec2(oct) + directionJitter) / float(PROBE_RESOLUTION);
	vec3 h = HemiOctahedralToDirection(uv);
	vec3 tangent, bitangent;
	TangentFrame(normal, tangent, bitangent);
	return normalize(tangent * h.x + bitangent * h.y + normal * h.z);
}

void main()
{
	ivec2 texel = ivec2(gl_FragCoord.xy);
	ivec2 probe = texel / PROBE_RESOLUTION;
	ivec2 oct = texel - probe * PROBE_RESOLUTION;

	vec4 center = texelFetch(ProbeRadiance, texel, 0);
	if (center.a < -1.5)
	{
		OutRadiance = center;
		return;
	}

	vec3 position, normal;
	ProbeAnchor(probe, position, normal);
	vec3 direction = ProbeDirection(normal, oct);

	vec3 sum = center.rgb;
	float weightSum = 1.0;
	for (int dy = -filterRadius; dy <= filterRadius; dy++)
	{
		for (int dx = -filterRadius; dx <= filterRadius; dx++)
		{
			if (dx == 0 && dy == 0) continue;
			ivec2 neighbour = probe + ivec2(dx, dy);
			if (any(lessThan(neighbour, ivec2(0))) || any(greaterThanEqual(neighbour, probeCount))) continue;

			vec4 s = texelFetch(ProbeRadiance, neighbour * PROBE_RESOLUTION + oct, 0);
			if (s.a < -1.5) continue;

			vec3 nPosition, nNormal;
			if (!ProbeAnchor(neighbour, nPosition, nNormal)) continue;

			float planeDistance = abs(dot(normal, nPosition - position));
			float normalWeight = max(dot(normal, nNormal), 0.0);
			if (planeDistance > planeTolerance || normalWeight < 0.7) continue;

			// Where did the neighbour's ray end, and would our ray have gone there too? Misses count as hits
			// at a large distance so sky samples pass the same direction test as surface hits.
			float w = normalWeight * (1.0 - planeDistance / planeTolerance);
			float hitDistance = s.a >= 0.0 ? s.a : 1e4;
			vec3 nDirection = ProbeDirection(nNormal, oct);
			vec3 hit = nPosition + nDirection * hitDistance;
			vec3 toHit = hit - position;
			float distanceToHit = length(toHit);
			if (distanceToHit < 1e-3) continue;
			float alignment = dot(toHit / distanceToHit, direction);
			if (alignment < angleThreshold) continue;
			w *= (alignment - angleThreshold) / (1.0 - angleThreshold);

			sum += s.rgb * w;
			weightSum += w;
		}
	}

	OutRadiance = vec4(sum / weightSum, center.a);
}

/////Fragment
