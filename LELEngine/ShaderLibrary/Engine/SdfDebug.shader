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

// Sphere-traces the global distance field from the camera and shades hits by their SDF normal.
// Opaque where a surface is hit, discards elsewhere (so the scene shows through the sky).
in vec2 fUV;
out vec4 FragColor;

uniform mat4 invViewProjection;
uniform vec3 cameraPosition;
uniform vec3 lightDirection; // direction the light travels

bool IntersectBox(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out float tmin, out float tmax)
{
	vec3 inv = 1.0 / rd;
	vec3 t0 = (bmin - ro) * inv;
	vec3 t1 = (bmax - ro) * inv;
	vec3 tsmall = min(t0, t1);
	vec3 tbig = max(t0, t1);
	tmin = max(max(tsmall.x, tsmall.y), tsmall.z);
	tmax = min(min(tbig.x, tbig.y), tbig.z);
	return tmax > max(tmin, 0.0);
}

void main()
{
	vec2 ndc = fUV * 2.0 - 1.0;
	vec4 farPoint = invViewProjection * vec4(ndc, 1.0, 1.0);
	vec3 target = farPoint.xyz / farPoint.w;
	vec3 rd = normalize(target - cameraPosition);

	float tmin, tmax;
	if (!IntersectBox(cameraPosition, rd, sdfGridMin, sdfGridMin + vec3(sdfGridSize), tmin, tmax)) discard;

	// Start just inside the grid when the camera is outside it.
	vec3 origin = cameraPosition + rd * max(tmin, 0.0);

	float hitT;
	if (!TraceSdf(origin, rd, tmax - max(tmin, 0.0), hitT)) discard;

	vec3 hit = origin + rd * hitT;
	vec3 n = SdfNormal(hit);
	vec3 L = normalize(-lightDirection);
	float diffuse = 0.25 + 0.75 * max(dot(n, L), 0.0);

	// Tint by normal so orientation errors are visible, lit so shape reads.
	vec3 albedo = n * 0.5 + 0.5;
	FragColor = vec4(albedo * diffuse, 1.0);
}

/////Fragment
