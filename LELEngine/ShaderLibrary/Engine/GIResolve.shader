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

#include "Engine/VoxelConeTracing.glsl"

// Traces the cones once per (reduced resolution) pixel from the geometry prepass.
// Material shaders read the result through GetIndirectLighting() with a depth-aware upsample.

in vec2 fUV;

layout(location = 0) out vec4 OutDiffuse;   // rgb irradiance, a occlusion (cone coverage)
layout(location = 1) out vec4 OutSpecular;  // rgb specular radiance, a linear view depth

uniform sampler2D SceneDepth;
uniform sampler2D NormalRoughness;
uniform mat4 invViewProjection;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;

void main()
{
	float depth = texture(SceneDepth, fUV).r;
	if (depth >= 1.0)
	{
		// Sky: nothing to light, push the depth far so no surface pixel picks it during upsampling.
		OutDiffuse = vec4(0.0);
		OutSpecular = vec4(0.0, 0.0, 0.0, 1e9);
		return;
	}

	vec4 clip = vec4(fUV * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec4 world = invViewProjection * clip;
	vec3 position = world.xyz / world.w;

	vec4 nr = texture(NormalRoughness, fUV);
	vec3 N = normalize(nr.xyz);
	float roughness = nr.w > 0.0 ? nr.w : 0.6;
	vec3 V = normalize(cameraPosition - position);

	vec4 diffuse = TraceDiffuseCones(position, N);
	vec3 specular = TraceSpecularCone(position, N, V, roughness);
	float linearDepth = dot(position - cameraPosition, cameraForward);

	OutDiffuse = diffuse;
	OutSpecular = vec4(specular, linearDepth);
}

/////Fragment
