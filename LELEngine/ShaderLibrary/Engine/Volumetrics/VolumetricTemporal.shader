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

// The temporal filter of a high quality volumetric object (VolumetricObjectHQ): this frame's march (its jitter changes
// every frame) blended with the history, the result of the frames before, read where the light of this pixel was last
// frame. That point is the light's mean distance along the ray, in the object's local space (so a moving object takes
// its light along), through the object's transform and the camera of the last frame. The history is kept inside the
// range of this frame's 3 x 3 neighbourhood: what changed or came into view does not leave a ghost.

#include "Engine/Volumetrics/VolumetricCommon.glsl"

uniform sampler2D volCurrent;          // this frame's march: premultiplied light, 1 - transmittance
uniform sampler2D volCurrentDistance;  // the light's mean distance along the ray (world units; 0: the ray missed)
uniform sampler2D volHistory;
uniform mat4 volPreviousViewProjection;
uniform mat4 volPreviousLocalToWorld;
uniform float volHistoryWeight;
uniform int volHistoryValid;

out vec4 FragColor;

void main()
{
	ivec2 pixel = ivec2(gl_FragCoord.xy);
	vec4 current = texelFetch(volCurrent, pixel, 0);
	float dist = texelFetch(volCurrentDistance, pixel, 0).r;
	if (volHistoryValid == 0 || dist <= 0.0)
	{
		FragColor = current;
		return;
	}

	VolRay ray = VolMakeRay(gl_FragCoord.xy);
	vec3 world = volCameraPosition + ray.worldDir * dist;
	vec3 local = (volWorldToLocal * vec4(world, 1.0)).xyz;
	vec4 clip = volPreviousViewProjection * (volPreviousLocalToWorld * vec4(local, 1.0));
	if (clip.w <= 1e-5)
	{
		FragColor = current;
		return;
	}

	vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
	if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
	{
		FragColor = current;
		return;
	}

	ivec2 size = textureSize(volCurrent, 0);
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

	vec4 history = clamp(texture(volHistory, uv), lo, hi);
	FragColor = mix(current, history, volHistoryWeight);
}

/////Fragment
