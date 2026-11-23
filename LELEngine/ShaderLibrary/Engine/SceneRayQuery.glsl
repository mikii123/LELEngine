// Lumen's rays through the scene's triangles with ray queries: the GI objects (Engine/SceneObjects.glsl) as a top level
// acceleration structure (SceneRayTracing: instance custom index = the object's row of the object table, each object's
// mesh placed in its scaled-local space). Only in the LEL_RAY_QUERY variants (the engine's preamble enables
// GL_EXT_ray_query with it, on a device that has ray queries); the other variants sphere trace the global distance field.
//
// What a hit gives the caller (SceneHit): how far along the ray, the object (no search: the instance says it), whether
// the ray met the triangle's front or its back (a ray that starts inside an object, or behind a single-sided surface,
// sees back faces: Lumen counts them as black), and the surface's normal there: the hit triangle's vertex normals
// interpolated at the hit (smooth over a mesh's facets, as the mesh is shaded), read from the geometry pool through the
// object's mesh range and the instance's transform. The GI volume bounds the rays: a ray from outside it hits nothing,
// and one that leaves it ends there. With VK_KHR_ray_tracing_position_fetch (LEL_RAY_POSITION_FETCH) the hit
// triangle's corners come from the structure itself instead of the pool.
//
// Requires Engine/DistanceField.glsl (the volume's uniforms), Engine/SceneObjects.glsl and Engine/SurfaceCache.glsl.

#include "Engine/RayOrigin.glsl"

layout(binding = 0) uniform accelerationStructureEXT GIScene;

// The geometry pool (GeometryPool): vertices of 20 floats (position, colour, uv, normal, ...), 32 bit indices; per
// object the first index and the base vertex of its mesh.
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

// Where a vertex's normal starts among its floats (after the position, the colour and the uv: LELEngine.Vertex).
const uint GISceneNormalFloat = 9u;

vec3 GISceneVertex(uint vertex)
{
	uint at = vertex * GISceneVertexFloats;
	return vec3(giSceneVertices[at], giSceneVertices[at + 1u], giSceneVertices[at + 2u]);
}

vec3 GISceneVertexNormal(uint vertex)
{
	uint at = vertex * GISceneVertexFloats + GISceneNormalFloat;
	return vec3(giSceneVertices[at], giSceneVertices[at + 1u], giSceneVertices[at + 2u]);
}

// How far off a surface cache texel's point its rays start: the point is stored in half floats (scaled-local, so up to
// a thousandth of its distance from the object's origin off the surface: two units in the last place), and a few
// millimetres for the card's quantized normal.
float CardRayBias(vec3 localPos)
{
	vec3 a = abs(localPos);
	return 0.005 + 0.001 * max(max(a.x, a.y), a.z);
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

// The nearest surface a ray met (TraceSceneRay).
struct SceneHit
{
	float t;          // distance along the ray
	int objectIndex;  // the object's row of the object table
	vec3 normal;      // the surface's normal there (world, unit): the vertex normals interpolated, on the side the ray came from
	bool frontFace;   // the ray met the triangle's front; a back face means the ray started inside or behind the surface
};

// The nearest surface along a ray between tMin and tMax (inside the GI volume). False (t = tMax, object -1) when the ray
// reaches nothing.
bool TraceSceneRay(vec3 origin, vec3 direction, float tMin, float tMax, out SceneHit hit)
{
	hit.t = tMax;
	hit.objectIndex = -1;
	hit.normal = -direction;
	hit.frontFace = true;
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
		hit.t = tMax;
		return false;
	}

	hit.t = rayQueryGetIntersectionTEXT(query, true);
	hit.objectIndex = rayQueryGetIntersectionInstanceCustomIndexEXT(query, true);
	hit.frontFace = rayQueryGetIntersectionFrontFaceEXT(query, true);
	uvec2 range = giSceneRanges[hit.objectIndex];
	uint first = range.x + uint(rayQueryGetIntersectionPrimitiveIndexEXT(query, true)) * 3u;
	uint i0 = range.y + giSceneIndices[first];
	uint i1 = range.y + giSceneIndices[first + 1u];
	uint i2 = range.y + giSceneIndices[first + 2u];

#ifdef LEL_RAY_POSITION_FETCH
	vec3 corners[3];
	rayQueryGetIntersectionTriangleVertexPositionsEXT(query, true, corners);
	vec3 p0 = corners[0];
	vec3 p1 = corners[1];
	vec3 p2 = corners[2];
#else
	vec3 p0 = GISceneVertex(i0);
	vec3 p1 = GISceneVertex(i1);
	vec3 p2 = GISceneVertex(i2);
#endif

	// Normals go to the world by the inverse transpose of the instance's 3x3: the transpose of world -> object.
	mat4x3 worldToObject = rayQueryGetIntersectionWorldToObjectEXT(query, true);
	vec3 g = cross(p1 - p0, p2 - p0);
	g = vec3(dot(worldToObject[0], g), dot(worldToObject[1], g), dot(worldToObject[2], g));
	float g2 = dot(g, g);
	g = g2 > 1e-30 ? g * inversesqrt(g2) : -direction;
	// The side the ray came from.
	g = dot(g, direction) > 0.0 ? -g : g;

	vec2 barycentrics = rayQueryGetIntersectionBarycentricsEXT(query, true);
	vec3 n = GISceneVertexNormal(i0) * (1.0 - barycentrics.x - barycentrics.y) + GISceneVertexNormal(i1) * barycentrics.x + GISceneVertexNormal(i2) * barycentrics.y;
	n = vec3(dot(worldToObject[0], n), dot(worldToObject[1], n), dot(worldToObject[2], n));
	float n2 = dot(n, n);
	// The interpolated normal, on the side the ray came from; where it turns away from the ray (a silhouette of a
	// smooth mesh, a mesh without normals) the triangle's own.
	n = n2 > 1e-30 ? n * inversesqrt(n2) : g;
	n = dot(n, g) < 0.0 ? -n : n;
	hit.normal = dot(n, direction) < 0.0 ? n : g;
	return true;
}

// Whether nothing of the scene lies along a ray between tMin and tMax (inside the GI volume; a ray from outside it is
// clear): shadow rays, which end at the first surface they meet, front or back.
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

// The object whose surface a point lies on (a point of the depth buffer, with its normal): a short ray back along the
// normal from reach in front of the point finds the triangle under it. -1 when no GI object is there (a surface not in
// the GI, or the point is off every triangle by more than reach).
int SceneObjectAtSurface(vec3 position, vec3 normal, float reach)
{
	SceneHit hit;
	if (!TraceSceneRay(position + normal * reach, -normal, 0.0, 2.0 * reach, hit))
	{
		return -1;
	}

	return hit.objectIndex;
}

// The surface cache's lighting where a ray hit an object (Engine/SurfaceCache.glsl): the object is known, its
// scaled-local point and normal come from the hit.
vec3 SampleSurfaceCacheAtSceneHit(int objectIndex, vec3 hitPos, vec3 hitNormal, sampler2D lighting)
{
	SceneObject o = sceneObjects[objectIndex];
	vec3 localPos = (o.worldToLocal * vec4(hitPos, 1.0)).xyz;
	return SampleSurfaceCache(objectIndex, localPos, SceneObjectWorldToLocalDir(o, hitNormal), lighting);
}

// What a hit gives the GI: the surface cache's lighting at a front face, nothing at a back face (the ray left from
// inside geometry or from behind a single-sided surface: Lumen's rule, which keeps light from leaking through walls).
vec3 SceneHitRadiance(SceneHit hit, vec3 origin, vec3 direction, sampler2D lighting)
{
	if (!hit.frontFace)
	{
		return vec3(0.0);
	}

	return SampleSurfaceCacheAtSceneHit(hit.objectIndex, origin + direction * hit.t, hit.normal, lighting);
}
