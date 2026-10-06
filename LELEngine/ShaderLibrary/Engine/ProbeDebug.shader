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
#include "Engine/AdaptiveProbes.glsl"

// Probe placement view (Lumen's probe visualization): white dots at the uniform probe anchors that lie on
// geometry, red dots at the adaptive probes placed this frame. Everything else keeps the scene colour.
in vec2 fUV;
out vec4 FragColor;

void main()
{
	ivec2 pixel = ivec2(gl_FragCoord.xy);

	ivec2 cell = clamp(ivec2(floor((vec2(pixel) - vec2(probeJitter)) / float(probeSpacing) + 0.5)), ivec2(0), probeCount - 1);
	if (length(vec2(pixel - ProbeAnchorPixel(cell))) <= 1.5 && texelFetch(ProbeAnchorPosition, cell, 0).w > 0.5)
	{
		FragColor = vec4(1.0);
		return;
	}

	int tileOffset = AdaptiveTileOffset(pixel);
	int count = AdaptiveTileCount(tileOffset);
	for (int k = 0; k < count; k++)
	{
		if (length(vec2(pixel - AdaptiveTilePixel(tileOffset, k))) <= 2.0)
		{
			FragColor = vec4(1.0, 0.15, 0.1, 1.0);
			return;
		}
	}

	discard;
}

/////Fragment
