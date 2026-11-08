//Vertex

#version 430

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// The temporal filter of a volumetric object: this frame's march (its jitter changes every frame) blended with the
// history, the result of the frames before, read where the light of this pixel was last frame. That point is the
// light's mean distance along the ray, in the object's local space (so a moving object takes its light along), through
// the object's transform and the camera of the last frame.
// As the GI's screen probe history (ProbeIntegrate.shader), the history is checked, not trusted: of the four history
// texels around that point only those whose light lay at the distance this pixel's light had from last frame's camera
// count, weighted bilinearly. A texel that an object in front hid last frame held no light there, so it never leaks in
// (it drew see-through outlines beside asteroids moving over a storm, 2026-11-08); a texel of other cloud (a wisp in
// front, the cut-off part behind an object) is too far off as well. The history counts its own frames: a new value
// weighs 1 / n, n growing up to volMaxFrames, so light that just came into view converges in a few frames instead of
// keeping its first noisy sample; a history that matched only partly counts for fewer frames. The history is also
// kept inside the range of this frame's 3 x 3 neighbourhood (light that changed, a flash, leaves no ghost).

#include "Engine/Volumetrics/VolumetricCommon.glsl"

uniform sampler2D volCurrent;          // this frame's march: premultiplied light, 1 - transmittance
uniform sampler2D volCurrentDistance;  // the light's mean distance along the ray (world units; 0: no light on the ray)
uniform sampler2D volHistory;          // the last result
uniform sampler2D volHistoryMeta;      // x: log2(1 + the last result's light distance), 0: no light; y: its frames
uniform mat4 volPreviousViewProjection;
uniform mat4 volPreviousLocalToWorld;
uniform vec3 volPreviousCameraPosition;
uniform float volMaxFrames;            // the steady state takes 1 / this of each new frame (1 / (1 - HistoryWeight))
uniform float volDistanceTolerance;    // log2(1 + share): how far off the light's distance may be and still match
uniform int volHistoryValid;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out vec2 FragMeta;

void main()
{
	ivec2 pixel = ivec2(gl_FragCoord.xy);
	vec4 current = texelFetch(volCurrent, pixel, 0);
	float dist = texelFetch(volCurrentDistance, pixel, 0).r;
	FragColor = current;
	FragMeta = dist > 0.0 ? vec2(log2(1.0 + dist), 1.0) : vec2(0.0);
	if (volHistoryValid == 0 || dist <= 0.0)
	{
		return;
	}

	VolRay ray = VolMakeRay(gl_FragCoord.xy);
	vec3 world = volCameraPosition + ray.worldDir * dist;
	vec3 local = (volWorldToLocal * vec4(world, 1.0)).xyz;
	vec3 previousWorld = (volPreviousLocalToWorld * vec4(local, 1.0)).xyz;
	vec4 clip = volPreviousViewProjection * vec4(previousWorld, 1.0);
	if (clip.w <= 1e-5)
	{
		return;
	}

	ivec2 size = textureSize(volHistory, 0);
	vec2 texel = (clip.xy / clip.w * 0.5 + 0.5) * vec2(size) - 0.5;
	ivec2 base = ivec2(floor(texel));
	if (any(lessThan(base, ivec2(0))) || any(greaterThanEqual(base + 1, size)))
	{
		return;
	}

	// The four texels around the point: bilinear weights times whether their light lay where this pixel's was.
	vec2 f = texel - vec2(base);
	float expected = log2(1.0 + distance(previousWorld, volPreviousCameraPosition));
	vec4 history = vec4(0.0);
	float weight = 0.0;
	float frames = 0.0;
	for (int y = 0; y <= 1; y++)
	{
		for (int x = 0; x <= 1; x++)
		{
			ivec2 tap = base + ivec2(x, y);
			vec2 meta = texelFetch(volHistoryMeta, tap, 0).xy;
			float bilinear = (x == 0 ? 1.0 - f.x : f.x) * (y == 0 ? 1.0 - f.y : f.y);
			float match = meta.y < 0.5 ? 0.0 : clamp(1.0 - abs(meta.x - expected) / volDistanceTolerance, 0.0, 1.0);
			float w = bilinear * match;
			if (w > 0.0)
			{
				history += texelFetch(volHistory, tap, 0) * w;
				frames += meta.y * w;
				weight += w;
			}
		}
	}

	if (weight < 0.05)
	{
		return;
	}

	history /= weight;
	// A history that matched only partly (an edge: half the taps were something else) counts for fewer frames.
	float previousFrames = frames / weight * clamp(weight * 2.0, 0.0, 1.0);

	vec4 lo = current;
	vec4 hi = current;
	for (int y = -1; y <= 1; y++)
	{
		for (int x = -1; x <= 1; x++)
		{
			vec4 c = texelFetch(volCurrent, clamp(pixel + ivec2(x, y), ivec2(0), size - 1), 0);
			lo = min(lo, c);
			hi = max(hi, c);
		}
	}

	history = clamp(history, lo, hi);
	float n = min(previousFrames + 1.0, volMaxFrames);
	FragColor = mix(history, current, 1.0 / n);
	FragMeta = vec2(log2(1.0 + dist), n);
}

/////Fragment
