//Compute

#version 430

#include "Engine/SceneObjects.glsl"

// Composes the global (world-space) distance field from the scene objects [objectStart, objectStart + objectCount):
// each texel takes the minimum over those objects, optionally starting from a base field (the static cache).
// Distances are clamped to maxDistance, so an object only influences texels within that band and
// dynamic updates can be limited to the object's padded bounds.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

uniform int objectStart;
uniform int objectCount;

layout(binding = 0, r16f) uniform readonly image3D BaseSdf;
layout(binding = 1, r16f) uniform writeonly image3D OutSdf;
uniform int useBase;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;
uniform vec3 gridMin;
uniform float gridSize;
uniform int sdfResolution;
uniform float maxDistance;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;
	ivec3 coord = dstOffset + local;

	float voxel = gridSize / float(sdfResolution);
	vec3 p = gridMin + (vec3(coord) + 0.5) * voxel;

	float d = useBase != 0 ? imageLoad(BaseSdf, coord).r : maxDistance;
	for (int i = 0; i < objectCount; i++)
	{
		SceneObject o = sceneObjects[objectStart + i];
		vec3 localPos = (o.worldToLocal * vec4(p, 1.0)).xyz;
		d = min(d, SceneObjectDistance(o, localPos));
	}

	imageStore(OutSdf, coord, vec4(min(d, maxDistance)));
}

/////Compute
