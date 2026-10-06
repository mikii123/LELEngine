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

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"

// "Lumen Scene" view: paints every visible pixel with what the surface cache stores for that surface
// (final lighting, or albedo in mode 1). Differences from the main render show capture / lookup errors.
in vec2 fUV;
out vec4 FragColor;

uniform sampler2D SceneDepth;
uniform sampler2D NormalRoughness;
uniform sampler2D Lighting;      // final lighting atlas or albedo atlas
uniform mat4 invViewProjection;
uniform float searchDistance;

void main()
{
	float depth = texture(SceneDepth, fUV).r;
	if (depth >= 1.0) discard;

	vec4 clip = vec4(fUV * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec4 world = invViewProjection * clip;
	vec3 position = world.xyz / world.w;
	vec3 normal = normalize(texture(NormalRoughness, fUV).xyz);

	vec3 radiance;
	if (!SampleSurfaceCacheAtHit(position, normal, searchDistance, Lighting, radiance))
	{
		// Magenta marks pixels whose object could not be identified.
		FragColor = vec4(1.0, 0.0, 1.0, 1.0);
		return;
	}

	FragColor = vec4(radiance, 1.0);
}

/////Fragment
