// Where a ray leaving a surface starts, for every shader that traces the scene's triangles with ray queries
// (Engine/SceneRayQuery.glsl, Engine/RayTracedShadowTrace.shader, Engine/RayTracedReflections.shader).
#ifndef LEL_RAY_ORIGIN_GLSL
#define LEL_RAY_ORIGIN_GLSL

// A point to start a ray from on a surface: bias world units off it along its normal, then a few units in the last
// place further (Waechter and Binder, "A Fast and Robust Method for Avoiding Self-Intersection", Ray Tracing Gems 6),
// so the ray never finds the surface it leaves whatever the size of the coordinates. The bias covers what the point
// itself is off the triangles by (a depth buffer's precision, a card texel, a normal map's tilt).
vec3 SceneRayOrigin(vec3 position, vec3 normal, float bias)
{
	vec3 p = position + normal * bias;
	ivec3 offset = ivec3(256.0 * normal);
	vec3 moved = intBitsToFloat(floatBitsToInt(p) + ivec3(p.x < 0.0 ? -offset.x : offset.x, p.y < 0.0 ? -offset.y : offset.y, p.z < 0.0 ? -offset.z : offset.z));
	const float nearOrigin = 1.0 / 32.0;
	const float floatScale = 1.0 / 65536.0;
	return vec3(abs(p.x) < nearOrigin ? p.x + floatScale * normal.x : moved.x,
		abs(p.y) < nearOrigin ? p.y + floatScale * normal.y : moved.y,
		abs(p.z) < nearOrigin ? p.z + floatScale * normal.z : moved.z);
}

// How far off a point of the depth buffer its rays start (world units), by its view depth: above the depth's error and
// a normal map's tilt, far below a distance field voxel.
float DepthRayBias(float viewDepth)
{
	return 0.01 + 0.001 * viewDepth;
}

#endif
