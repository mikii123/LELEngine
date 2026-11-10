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

// The depth of a half-resolution transparent layer (LELEngine.Rendering.LowResolutionTransparency): the farthest of the
// 2 x 2 scene depths under each texel, so what is drawn into the layer still reaches the edges of the things in front of
// it; the composite keeps those edges sharp by comparing depths at the full resolution.

uniform sampler2D SceneDepth;   // window depth of the opaque scene, full resolution

void main()
{
	ivec2 last = textureSize(SceneDepth, 0) - 1;
	ivec2 p = ivec2(gl_FragCoord.xy) * 2;
	float a = texelFetch(SceneDepth, min(p, last), 0).r;
	float b = texelFetch(SceneDepth, min(p + ivec2(1, 0), last), 0).r;
	float c = texelFetch(SceneDepth, min(p + ivec2(0, 1), last), 0).r;
	float d = texelFetch(SceneDepth, min(p + ivec2(1, 1), last), 0).r;
	gl_FragDepth = max(max(a, b), max(c, d));
}

/////Fragment
