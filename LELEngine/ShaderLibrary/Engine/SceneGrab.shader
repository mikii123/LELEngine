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

// The transparent pass's grab (LELEngine.Rendering.SceneGrab, read through Engine/GrabPass.glsl): the scene color drawn
// so far and, in alpha, the linear eye depth of the opaque surface, texel for texel inside the scissor rectangles of the
// objects that read it.

uniform sampler2D SceneColor;
uniform sampler2D SceneDepth;
uniform vec4 depthParams; // linear eye depth = (z * x + y) / (z * z + w), z the NDC depth

out vec4 FragColor;

void main()
{
	ivec2 texel = ivec2(gl_FragCoord.xy);
	float z = texelFetch(SceneDepth, texel, 0).r * 2.0 - 1.0;
	FragColor = vec4(texelFetch(SceneColor, texel, 0).rgb, (z * depthParams.x + depthParams.y) / (z * depthParams.z + depthParams.w));
}

/////Fragment
