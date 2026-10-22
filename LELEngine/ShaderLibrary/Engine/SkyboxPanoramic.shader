//Properties
Panorama ("Panorama (equirectangular)", 2D) = "black" {}
[HDR] Tint ("Tint", Color) = (1, 1, 1, 1)
Exposure ("Exposure", Range(0, 8)) = 1
Rotation ("Rotation", Range(0, 360)) = 0
Tilt ("Tilt (about X, before Rotation)", Range(-180, 180)) = 0
/////Properties

//Vertex

#version 430

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

uniform mat4 invViewProjection;

uniform sampler2D Panorama;
uniform vec4 Tint;
uniform float Exposure;
uniform float Rotation;
uniform float Tilt;

out vec4 FragColor;

const float PI = 3.14159265358979;

void main()
{
	vec4 clip = vec4(fUV * 2.0 - 1.0, 1.0, 1.0);
	vec4 world = invViewProjection * clip;
	vec3 dir = normalize(world.xyz / world.w);

	// Tilt about X first (a sky authored for another up axis), then the yaw.
	float t = radians(Tilt);
	float ct = cos(t);
	float st = sin(t);
	dir = vec3(dir.x, ct * dir.y - st * dir.z, st * dir.y + ct * dir.z);

	float a = radians(Rotation);
	float c = cos(a);
	float s = sin(a);
	dir = vec3(c * dir.x + s * dir.z, dir.y, -s * dir.x + c * dir.z);

	// Latitude / longitude lookup: the panorama's center faces +Z, u grows towards -X (screen right); textures are
	// stored bottom-up, so the top of the image (+Y) is v = 1.
	float u = 0.5 + atan(-dir.x, dir.z) / (2.0 * PI);
	float v = 1.0 - acos(clamp(dir.y, -1.0, 1.0)) / PI;
	vec3 color = texture(Panorama, vec2(u, v)).rgb * Tint.rgb * Exposure;
	FragColor = vec4(color, 1.0);
}

/////Fragment
