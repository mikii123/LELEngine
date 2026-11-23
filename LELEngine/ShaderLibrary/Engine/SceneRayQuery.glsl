// Lumen's rays through the scene's triangles with ray queries: the GI objects (Engine/SceneObjects.glsl) as a top level
// acceleration structure (SceneRayTracing: instance custom index = the object's row of the object table, each object's
// mesh placed in its scaled-local space). Only in the LEL_RAY_QUERY variants (the engine's preamble enables
// GL_EXT_ray_query with it, on a device that has ray queries); the other variants sphere trace the global distance field.
//
// What a hit gives the caller is what the distance field's hit leads to: how far along the ray, the object (no search:
// the instance says it) and the surface's normal there (the hit triangle's, from the geometry pool: the object's mesh
// range, its three indices, their positions through the instance's transform), facing back along the ray so the
// surface cache reads the side the ray arrived at. The GI volume bounds the rays as it bounds the field: a ray from
// outside it hits nothing, and one that leaves it ends there.
//
// Requires Engine/DistanceField.glsl (the volume), Engine/SceneObjects.glsl and Engine/SurfaceCache.glsl.

layout(binding = 0) uniform accelerationStructureEXT GIScene;

// The geometry pool (GeometryPool): vertices of 20 floats with the position first, 32 bit indices; per object the
// first index and the base vertex of its mesh.
layout(std430, binding = 19) readonly buffer GISceneVertices
{
	float giSceneVertices[];
};

layout(std430, binding = 20) readonly buffer GISceneIndices
{
	uint giSceneIndices[];
};

layout(std430, binding = 21) readonly buffer GISceneRanges
{
	uvec2 giSceneRanges[];
};

const uint GISceneVertexFloats = 20u;

vec3 GISceneVertex(uint vertex)
{
	uint at = vertex * GISceneVertexFloats;
	return vec3(giSceneVertices[at], giSceneVertices[at + 1u], giSceneVertices[at + 2u]);
}

// The part of a ray inside the GI volume, from tMin: false when the ray is outside it there.
bool GISceneRayInterval(vec3 origin, vec3 direction, float tMin, inout float tMax)
{
	vec3 safeDirection = mix(vec3(1e-6), direction, greaterThan(abs(direction), vec3(1e-6)));
	vec3 invDirection = 1.0 / safeDirection;
	vec3 t0 = (sdfGridMin - origin) * invDirection;
	vec3 t1 = (sdfGridMin + vec3(sdfGridSize) - origin) * invDirection;
	vec3 tNear = min(t0, t1);
	vec3 tFar = max(t0, t1);
	float tEntry = max(max(tNear.x, tNear.y), tNear.z);
	float tExit = min(min(tFar.x, tFar.y), tFar.z);
	if (tEntry > tMin || tExit < tMin)
	{
		return false;
	}

	tMax = min(tMax, tExit);
	return tMax > tMin;
}

// The nearest surface along a ray between tMin and tMax (inside the GI volume): its distance, its object and the hit
// triangle's normal facing back along the ray. False (hitT tMax, object -1) when the ray reaches nothing.
bool TraceSceneRay(vec3 origin, vec3 direction, float tMin, float tMax, out float hitT, out int objectIndex, out vec3 normal)
{
	hitT = tMax;
	objectIndex = -1;
	normal = -direction;
	if (!GISceneRayInterval(origin, direction, tMin, tMax))
	{
		return false;
	}

	rayQueryEXT query;
	rayQueryInitializeEXT(query, GIScene, gl_RayFlagsOpaqueEXT, 0xFFu, origin, tMin, direction, tMax);
	while (rayQueryProceedEXT(query))
	{
	}

	if (rayQueryGetIntersectionTypeEXT(query, true) != gl_RayQueryCommittedIntersectionTriangleEXT)
	{
		return false;
	}

	hitT = rayQueryGetIntersectionTEXT(query, true);
	objectIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(query, true);
	uvec2 range = giSceneRanges[objectIndex];
	uint first = range.x + uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, true)) * 3u;
	vec3 p0 = GISceneVertex(range.y + giSceneIndices[first]);
	vec3 p1 = GISceneVertex(range.y + giSceneIndices[first + 1u]);
	vec3 p2 = GISceneVertex(range.y + giSceneIndices[first + 2u]);
	mat4x3 toWorld = rayQueryGetIntersectionObjectToWorldEXT(query, true);
	vec3 n = cross(toWorld * vec4(p1 - p0, 0.0), toWorld * vec4(p2 - p0, 0.0));
	float length2 = dot(n, n);
	if (length2 > 1e-24)
	{
		n *= inversesqrt(length2);
		normal = dot(n, direction) > 0.0 ? -n : n;
	}

	return true;
}

// Whether nothing of the scene lies along a ray between tMin and tMax (inside the GI volume; a ray from outside it is
// clear): shadow rays, which end at the first surface they meet.
bool SceneRayClear(vec3 origin, vec3 direction, float tMin, float tMax)
{
	if (!GISceneRayInterval(origin, direction, tMin, tMax))
	{
		return true;
	}

	rayQueryEXT query;
	rayQueryInitializeEXT(query, GIScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, origin, tMin, direction, tMax);
	while (rayQueryProceedEXT(query))
	{
	}

	return rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT;
}

// The surface cache's lighting where a ray hit an object (Engine/SurfaceCache.glsl): the object is known, its
// scaled-local point and normal come from the hit.
vec3 SampleSurfaceCacheAtSceneHit(int objectIndex, vec3 hitPos, vec3 hitNormal, sampler2D lighting)
{
	SceneObject o = sceneObjects[objectIndex];
	vec3 localPos = (o.worldToLocal * vec4(hitPos, 1.0)).xyz;
	return SampleSurfaceCache(objectIndex, localPos, SceneObjectWorldToLocalDir(o, hitNormal), lighting);
}
