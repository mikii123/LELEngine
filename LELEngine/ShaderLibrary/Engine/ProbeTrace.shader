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

// Screen probe ray tracing (Lumen final gather, step 1) through the global distance field: one ray per fragment of the
// ray atlas (Engine/ProbeTraceCore.glsl). Rays through the scene's triangles are Engine/ProbeTraceRays.shader's.
#include "Engine/ProbeTraceCore.glsl"

out vec4 OutRay; // rgb radiance, a = hit distance (-1 sky / far field, -2 probe on sky / invalid, moving hits encoded)

void main()
{
	OutRay = TraceProbeTexel(ivec2(gl_FragCoord.xy));
}

/////Fragment
