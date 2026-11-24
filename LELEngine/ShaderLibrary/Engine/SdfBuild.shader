//Compute

#version 430

// Builds a mesh signed distance field by brute force: every texel finds its nearest triangle.
// The sign comes from the generalized winding number (sum of signed solid angles), which is exact
// for closed, consistently oriented meshes and degrades gracefully for small cracks.
//
// One work group per texel: its 64 threads take every 64th triangle each and fold their results together in shared
// memory, so a field of few texels (a flat mesh) still fills the GPU. A dispatch tests one chunk of the triangles
// (DistanceFieldAtlas.PairsPerDispatch texel x triangle pairs at most): one pass over all of them took seconds for a
// mesh of millions of triangles and the driver reset the GPU (TDR). Between chunks each texel keeps its nearest
// squared distance and its winding so far in Partial; the last chunk writes the signed distance into the atlas.
layout(local_size_x = 64) in;

layout(std430, binding = 0) readonly buffer Triangles
{
	vec4 vertices[]; // this chunk's triangles: three consecutive entries per triangle, xyz used
};

layout(std430, binding = 1) buffer Partial
{
	vec2 partial[]; // per texel, x fastest: x the nearest squared distance so far, y the winding so far
};

layout(binding = 0, r16f) uniform writeonly image3D SdfOut;

uniform ivec3 atlasOffset;
uniform ivec3 sdfSize;
uniform vec3 boundsMin;
uniform vec3 sdfTexelSize;
uniform int chunkTriangles;
uniform int firstChunk;
uniform int lastChunk;

const float PI = 3.14159265359;

shared float nearestShared[64];
shared float windingShared[64];

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

// Folds lanes [width, 2 x width) onto lanes [0, width).
void Fold(uint lane, uint width)
{
	if (lane < width)
	{
		nearestShared[lane] = min(nearestShared[lane], nearestShared[lane + width]);
		windingShared[lane] += windingShared[lane + width];
	}
}

void main()
{
	ivec3 id = ivec3(gl_WorkGroupID);
	uint lane = gl_LocalInvocationIndex;
	vec3 p = boundsMin + (vec3(id) + 0.5) * sdfTexelSize;

	float bestSq = 1e30;
	float winding = 0.0;
	for (int i = int(lane); i < chunkTriangles; i += 64)
	{
		vec3 a = vertices[i * 3 + 0].xyz;
		vec3 b = vertices[i * 3 + 1].xyz;
		vec3 c = vertices[i * 3 + 2].xyz;

		bestSq = min(bestSq, TriangleDistanceSq(p, a, b, c));
		winding += SolidAngle(a - p, b - p, c - p);
	}

#if defined(LEL_VULKAN) && defined(LEL_SUBGROUPS)
	// Vulkan: each subgroup folds its lanes at once (one subgroup for the whole group in 64-wide waves), then the first
	// lane the subgroups' results: one barrier instead of five. The same field every time on a GPU (the subgroups' order
	// of addition is the hardware's own, fixed).
	float subgroupNearest = subgroupMin(bestSq);
	float subgroupWinding = subgroupAdd(winding);
	if (subgroupElect())
	{
		nearestShared[gl_SubgroupID] = subgroupNearest;
		windingShared[gl_SubgroupID] = subgroupWinding;
	}

	barrier();
	if (lane != 0u)
	{
		return;
	}

	bestSq = nearestShared[0];
	winding = windingShared[0];
	for (uint s = 1u; s < gl_NumSubgroups; s++)
	{
		bestSq = min(bestSq, nearestShared[s]);
		winding += windingShared[s];
	}
#else
	// The lanes folded together in a fixed order (the same field every time), unrolled: a barrier inside a loop makes
	// Intel's compiler warn.
	nearestShared[lane] = bestSq;
	windingShared[lane] = winding;
	barrier();
	Fold(lane, 32u);
	barrier();
	Fold(lane, 16u);
	barrier();
	Fold(lane, 8u);
	barrier();
	Fold(lane, 4u);
	barrier();
	Fold(lane, 2u);
	barrier();
	if (lane != 0u)
	{
		return;
	}

	bestSq = min(nearestShared[0], nearestShared[1]);
	winding = windingShared[0] + windingShared[1];
#endif
	int index = (id.z * sdfSize.y + id.y) * sdfSize.x + id.x;
	if (firstChunk == 0)
	{
		vec2 before = partial[index];
		bestSq = min(bestSq, before.x);
		winding += before.y;
	}

	if (lastChunk == 0)
	{
		partial[index] = vec2(bestSq, winding);
		return;
	}

	// Interior points see the whole closed surface: 4 pi steradians.
	float sgn = winding / (4.0 * PI) > 0.5 ? -1.0 : 1.0;
	imageStore(SdfOut, atlasOffset + id, vec4(sgn * sqrt(bestSq)));
}

/////Compute
