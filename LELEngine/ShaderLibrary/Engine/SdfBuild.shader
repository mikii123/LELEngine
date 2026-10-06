//Compute

#version 430

// Builds a mesh signed distance field by brute force: every texel finds its nearest triangle.
// The sign comes from the generalized winding number (sum of signed solid angles), which is exact
// for closed, consistently oriented meshes and degrades gracefully for small cracks.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(std430, binding = 0) readonly buffer Triangles
{
	vec4 vertices[]; // three consecutive entries per triangle, xyz used
};

layout(binding = 0, r16f) uniform writeonly image3D SdfOut;

uniform ivec3 atlasOffset;
uniform ivec3 sdfSize;
uniform vec3 boundsMin;
uniform vec3 sdfTexelSize;
uniform int triangleCount;

const float PI = 3.14159265359;

float dot2(vec3 v)
{
	return dot(v, v);
}

// Squared unsigned distance from p to triangle abc (Inigo Quilez).
float TriangleDistanceSq(vec3 p, vec3 a, vec3 b, vec3 c)
{
	vec3 ba = b - a; vec3 pa = p - a;
	vec3 cb = c - b; vec3 pb = p - b;
	vec3 ac = a - c; vec3 pc = p - c;
	vec3 nor = cross(ba, ac);

	bool insidePrism =
		sign(dot(cross(ba, nor), pa)) +
		sign(dot(cross(cb, nor), pb)) +
		sign(dot(cross(ac, nor), pc)) >= 2.0;

	if (insidePrism)
	{
		return dot(nor, pa) * dot(nor, pa) / dot2(nor);
	}

	return min(min(
		dot2(ba * clamp(dot(ba, pa) / dot2(ba), 0.0, 1.0) - pa),
		dot2(cb * clamp(dot(cb, pb) / dot2(cb), 0.0, 1.0) - pb)),
		dot2(ac * clamp(dot(ac, pc) / dot2(ac), 0.0, 1.0) - pc));
}

// Signed solid angle of triangle abc as seen from the origin (Van Oosterom & Strackee).
float SolidAngle(vec3 a, vec3 b, vec3 c)
{
	float la = length(a);
	float lb = length(b);
	float lc = length(c);
	float numerator = dot(a, cross(b, c));
	float denominator = la * lb * lc + dot(a, b) * lc + dot(b, c) * la + dot(c, a) * lb;
	return 2.0 * atan(numerator, denominator);
}

void main()
{
	ivec3 id = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(id, sdfSize))) return;

	vec3 p = boundsMin + (vec3(id) + 0.5) * sdfTexelSize;

	float bestSq = 1e30;
	float winding = 0.0;
	for (int i = 0; i < triangleCount; i++)
	{
		vec3 a = vertices[i * 3 + 0].xyz;
		vec3 b = vertices[i * 3 + 1].xyz;
		vec3 c = vertices[i * 3 + 2].xyz;

		bestSq = min(bestSq, TriangleDistanceSq(p, a, b, c));
		winding += SolidAngle(a - p, b - p, c - p);
	}

	// Interior points see the whole closed surface: 4 pi steradians.
	float sgn = winding / (4.0 * PI) > 0.5 ? -1.0 : 1.0;
	imageStore(SdfOut, atlasOffset + id, vec4(sgn * sqrt(bestSq)));
}

/////Compute
