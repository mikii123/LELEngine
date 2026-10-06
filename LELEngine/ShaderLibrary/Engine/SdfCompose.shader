//Compute

#version 430

// Composes the global (world-space) distance field from object distance fields: each texel takes the
// minimum over all listed objects, optionally starting from a base field (the static cache).
// Distances are clamped to maxDistance, so an object only influences texels within that band and
// dynamic updates can be limited to the object's padded bounds.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

struct SdfObject
{
	mat4 worldToLocal;  // world -> scaled-local (rotation + translation only, scale is baked into the field)
	vec4 boundsMin;     // scaled-local bounds covered by the field (mesh bounds + padding)
	vec4 boundsMax;
	vec4 atlasOrigin;   // texel offset of the field in the atlas
	vec4 atlasSize;     // texels per axis
	vec4 padding;       // per-axis width of the padding ring around the mesh bounds
};

layout(std430, binding = 0) readonly buffer Objects
{
	SdfObject objects[];
};

uniform int objectCount;
uniform sampler3D SdfAtlas;
uniform vec3 atlasTexels;

layout(binding = 0, r16f) uniform readonly image3D BaseSdf;
layout(binding = 1, r16f) uniform writeonly image3D OutSdf;
uniform int useBase;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;
uniform vec3 gridMin;
uniform float gridSize;
uniform int sdfResolution;
uniform float maxDistance;

// Conservative (never larger than the true) distance from world point p to the object's surface.
float ObjectDistance(SdfObject o, vec3 p)
{
	vec3 local = (o.worldToLocal * vec4(p, 1.0)).xyz;
	vec3 bmin = o.boundsMin.xyz;
	vec3 bmax = o.boundsMax.xyz;

	vec3 clamped = clamp(local, bmin, bmax);
	float outside = length(local - clamped);

	// Keep the lookup half a texel inside the slab so trilinear filtering never reads a neighbour field.
	vec3 uvw = (clamped - bmin) / (bmax - bmin);
	vec3 texel = o.atlasOrigin.xyz + clamp(uvw * o.atlasSize.xyz, vec3(0.5), o.atlasSize.xyz - 0.5);
	float sampled = texture(SdfAtlas, texel / atlasTexels).r;

	if (outside <= 0.0) return sampled;

	// Outside the field: the mesh lies inside its (unpadded) bounds, so the distance to that box is a
	// lower bound that is exact for box-like meshes; the sampled distance minus the gap is another one.
	vec3 meshMin = bmin + o.padding.xyz;
	vec3 meshMax = bmax - o.padding.xyz;
	float outsideMesh = length(local - clamp(local, meshMin, meshMax));
	return max(outsideMesh, sampled - outside);
}

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
		d = min(d, ObjectDistance(objects[i], p));
	}

	imageStore(OutSdf, coord, vec4(min(d, maxDistance)));
}

/////Compute
