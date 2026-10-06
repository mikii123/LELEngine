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

#include "Engine/ScreenProbes.glsl"

// Resolves each screen probe's anchor (jittered pixel of its cell) to a world position and normal.
layout(location = 0) out vec4 OutPosition;
layout(location = 1) out vec4 OutNormal;

void main()
{
	ivec2 probe = ivec2(gl_FragCoord.xy);
	ivec2 pixel = ProbeAnchorPixel(probe);
	float depth = texelFetch(SceneDepth, pixel, 0).r;
	if (depth >= 1.0)
	{
		OutPosition = vec4(0.0);
		OutNormal = vec4(0.0, 1.0, 0.0, 0.0);
		return;
	}

	OutPosition = vec4(ReconstructPosition(pixel, depth), 1.0);
	OutNormal = vec4(normalize(texelFetch(NormalRoughness, pixel, 0).xyz), 1.0);
}

/////Fragment
