//Properties
Cubemap ("Cubemap", Cube) = "" {}
[HDR] Tint ("Tint", Color) = (1, 1, 1, 1)
Exposure ("Exposure", Range(0, 8)) = 1
Rotation ("Rotation", Range(0, 360)) = 0
/////Properties

//Vertex

#version 430

// Fullscreen triangle at the far plane: only pixels nothing was drawn to pass the LEQUAL depth test.
out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 1.0, 1.0);
}

/////Vertex

//Fragment

#version 430

in vec2 fUV;

// Camera (set by the skybox pass).
uniform mat4 invViewProjection;

// Material.
uniform samplerCube Cubemap;
uniform vec4 Tint;
uniform float Exposure;
uniform float Rotation;

out vec4 FragColor;

void main()
{
	vec4 clip = vec4(fUV * 2.0 - 1.0, 1.0, 1.0);
	vec4 world = invViewProjection * clip;
	vec3 dir = normalize(world.xyz / world.w);

	float a = radians(Rotation);
	float c = cos(a);
	float s = sin(a);
	dir = vec3(c * dir.x + s * dir.z, dir.y, -s * dir.x + c * dir.z);

	vec3 color = texture(Cubemap, dir).rgb * Tint.rgb * Exposure;
	FragColor = vec4(color, 1.0);
}

/////Fragment
