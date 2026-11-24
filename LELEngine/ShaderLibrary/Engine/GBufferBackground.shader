//Vertex

#version 430

// The geometry prepass's background (LELEngine.Rendering.Passes.GeometryPrepass): a full-screen triangle at the far plane,
// drawn with the depth test Equal after the geometry, so it reaches only the pixels whose depth still holds its clear
// value (1, the far plane): nothing was drawn there.

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	gl_Position = vec4(pos * 2.0 - 1.0, 1.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// What the G-buffer holds where no geometry is: no normal, no roughness, no motion (the values a clear to zero gives).

layout(location = 0) out vec4 NormalRoughness;
layout(location = 1) out vec4 Motion;

void main()
{
	NormalRoughness = vec4(0.0);
	Motion = vec4(0.0);
}

/////Fragment
