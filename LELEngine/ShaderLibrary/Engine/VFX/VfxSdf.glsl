// The scene's surfaces for particle operators and blocks: distance and normal at a world position, with a "not
// available" answer outside the GI volume. From the GI's global signed distance field (Engine/DistanceField.glsl), or in
// a kernel's LEL_RAY_QUERY variant (while the GI's rays trace the scene's triangles and no field is built) from rays
// through the GI's scene (its top level: acceleration structure 0, the objects' triangles in the geometry pool).
#ifndef VFX_SDF_GLSL
#define VFX_SDF_GLSL

#ifdef LEL_RAY_QUERY
layout(binding = 0) uniform accelerationStructureEXT VfxScene;

#ifndef LEL_RAY_POSITION_FETCH
// The geometry pool (SceneRayTracing): vertices of 20 floats, 32-bit indices, per object its first index and base vertex.
layout(std430, binding = 19) readonly buffer VfxSceneVertices
{
	float vfxSceneVertices[];
};

layout(std430, binding = 20) readonly buffer VfxSceneIndices
{
	uint vfxSceneIndices[];
};

layout(std430, binding = 21) readonly buffer VfxSceneRanges
{
	uvec2 vfxSceneRanges[];
};

vec3 VfxSceneVertex(uint vertex)
{
	uint at = vertex * 20u;
	return vec3(vfxSceneVertices[at], vfxSceneVertices[at + 1u], vfxSceneVertices[at + 2u]);
}
#endif

// The nearest triangle of the GI's scene along a ray up to tMax: its distance, its plane's normal (world, unit), and
// whether the ray met its front.
bool VfxSceneRay(vec3 origin, vec3 direction, float tMax, out float t, out vec3 normal, out bool front)
{
	t = tMax;
	normal = -direction;
	front = true;
	rayQueryEXT query;
	rayQueryInitializeEXT(query, VfxScene, gl_RayFlagsOpaqueEXT, 0xFFu, origin, 0.0, direction, tMax);
	while (rayQueryProceedEXT(query))
	{
	}

	if (rayQueryGetIntersectionTypeEXT(query, true) != gl_RayQueryCommittedIntersectionTriangleEXT)
	{
		return false;
	}

	t = rayQueryGetIntersectionTEXT(query, true);
	front = rayQueryGetIntersectionFrontFaceEXT(query, true);
#ifdef LEL_RAY_POSITION_FETCH
	vec3 corners[3];
	rayQueryGetIntersectionTriangleVertexPositionsEXT(query, true, corners);
	vec3 p0 = corners[0];
	vec3 p1 = corners[1];
	vec3 p2 = corners[2];
#else
	uvec2 range = vfxSceneRanges[rayQueryGetIntersectionInstanceCustomIndexEXT(query, true)];
	uint first = range.x + uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, true)) * 3u;
	vec3 p0 = VfxSceneVertex(range.y + vfxSceneIndices[first]);
	vec3 p1 = VfxSceneVertex(range.y + vfxSceneIndices[first + 1u]);
	vec3 p2 = VfxSceneVertex(range.y + vfxSceneIndices[first + 2u]);
#endif
	mat4x3 worldToObject = rayQueryGetIntersectionWorldToObjectEXT(query, true);
	vec3 g = cross(p1 - p0, p2 - p0);
	g = vec3(dot(worldToObject[0], g), dot(worldToObject[1], g), dot(worldToObject[2], g));
	float g2 = dot(g, g);
	g = g2 > 1e-30 ? g * inversesqrt(g2) : -direction;
	// Facing the side the ray came from.
	normal = dot(g, direction) > 0.0 ? -g : g;
	return true;
}

// The last position the distance was asked for and the answer (a block asks for the distance and the normal at one
// point: six rays once).
vec3 vfxSdfQueryAt = vec3(1e30);
float vfxSdfQueryDistance = 1e6;
vec3 vfxSdfQueryNormal = vec3(0.0, 1.0, 0.0);

// The distance to the nearest surface by six rays along the axes, each the distance to the plane of the triangle it met
// (exact for walls and floors, an upper bound elsewhere), within the field's band; negative inside an object (most rays
// meet back faces), and the normal of the nearest surface.
void VfxSceneDistance(vec3 world)
{
	if (all(equal(world, vfxSdfQueryAt)))
	{
		return;
	}

	vfxSdfQueryAt = world;
	float band = sdfMaxDistance > 0.0 ? sdfMaxDistance : 1.0;
	float nearest = band;
	vec3 normal = vec3(0.0, 1.0, 0.0);
	int backFaces = 0;
	float nearestBack = band;
	for (int axis = 0; axis < 6; axis++)
	{
		vec3 direction = vec3(0.0);
		direction[axis >> 1] = (axis & 1) == 0 ? 1.0 : -1.0;
		float t;
		vec3 n;
		bool front;
		if (!VfxSceneRay(world, direction, band, t, n, front))
		{
			continue;
		}

		float planeDistance = t * abs(dot(direction, n));
		if (!front)
		{
			backFaces++;
			nearestBack = min(nearestBack, planeDistance);
			continue;
		}

		if (planeDistance < nearest)
		{
			nearest = planeDistance;
			normal = n;
		}
	}

	vfxSdfQueryDistance = backFaces >= 3 ? -nearestBack : nearest;
	vfxSdfQueryNormal = normal;
}
#endif

// Distance to the nearest surface (world units, clamped to the field's band); a large value outside the grid.
float VfxSampleGlobalSdf(vec3 world)
{
	if (sdfResolution <= 0 || !InsideSdfGrid(world)) return 1e6;
#ifdef LEL_RAY_QUERY
	VfxSceneDistance(world);
	return vfxSdfQueryDistance;
#else
	return SampleSdf(world);
#endif
}

vec3 VfxGlobalSdfNormal(vec3 world)
{
	if (sdfResolution <= 0 || !InsideSdfGrid(world)) return vec3(0.0, 1.0, 0.0);
#ifdef LEL_RAY_QUERY
	VfxSceneDistance(world);
	return vfxSdfQueryNormal;
#else
	return SdfNormal(world);
#endif
}

bool VfxGlobalSdfAvailable(vec3 world)
{
	return sdfResolution > 0 && InsideSdfGrid(world);
}

#ifdef LEL_RAY_QUERY
// A particle's collision with the GI's scene over this frame's step (world units: its position, the step its velocity
// makes before the update integrates it, its radius): a ray along the step finds the surface it would reach, and the
// answer is the signed distance it would have to that surface's plane after the step, with the plane's normal (facing
// the particle), so the collision response pushes it out and turns its velocity before it passes a wall, however fast it
// moves. Without motion, the nearest surface by the six rays. False when nothing is in reach.
bool VfxSceneCollision(vec3 world, vec3 step, float radius, out float distanceAfter, out vec3 normal)
{
	distanceAfter = 1e6;
	normal = vec3(0.0, 1.0, 0.0);
	if (sdfResolution <= 0 || !InsideSdfGrid(world))
	{
		return false;
	}

	float stepLength = length(step);
	if (stepLength < 1e-6)
	{
		VfxSceneDistance(world);
		distanceAfter = vfxSdfQueryDistance;
		normal = vfxSdfQueryNormal;
		return distanceAfter < radius;
	}

	vec3 direction = step / stepLength;
	float t;
	bool front;
	if (!VfxSceneRay(world, direction, stepLength + max(radius, 0.0) + 1e-3, t, normal, front) || !front)
	{
		return false;
	}

	float facing = abs(dot(direction, normal));
	distanceAfter = (t - stepLength) * facing;
	return true;
}
#endif

#endif
