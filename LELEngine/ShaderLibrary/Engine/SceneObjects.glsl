// Lumen scene: the per-object table shared by the global distance field composition, the surface cache
// and the probe gather. Mirrors LumenScene.SceneObjectData (std430, 256 bytes) and CardData (80 bytes).
//
// Object space is "scaled local": the renderer's scale is baked into the mesh SDF and the cards, so
// worldToLocal / localToWorld contain rotation and translation only (distances are preserved).

struct SceneObject
{
	mat4 worldToLocal;
	mat4 localToWorld;
	vec4 boundsMin;     // mesh SDF field bounds (mesh bounds + padding)
	vec4 boundsMax;
	vec4 atlasOrigin;   // SDF atlas texel origin
	vec4 atlasSize;     // SDF texels per axis
	vec4 padding;       // SDF padding ring width per axis
	vec4 meshBoundsMin; // scaled-local mesh bounds
	vec4 meshBoundsMax;
	ivec4 cardInfo;     // x first card index, y card count, z 1 = dynamic
};

struct Card
{
	ivec4 rect;   // x, y, width, height in surface cache atlas texels
	vec4 axisX;   // local U axis, w = extent along it
	vec4 axisY;   // local V axis, w = extent
	vec4 axisZ;   // local facing direction (outward), w = depth extent
	vec4 origin;  // local card center, w = object index
};

layout(std430, binding = 1) readonly buffer SceneObjectBuffer
{
	SceneObject sceneObjects[];
};

layout(std430, binding = 2) readonly buffer CardBuffer
{
	Card cards[];
};

uniform int sceneObjectCount;
uniform sampler3D SdfAtlas;
uniform vec3 sdfAtlasTexels;

// Conservative distance from a scaled-local point to the object's surface (see SdfCompose.shader).
float SceneObjectDistance(SceneObject o, vec3 local)
{
	vec3 bmin = o.boundsMin.xyz;
	vec3 bmax = o.boundsMax.xyz;
	vec3 clamped = clamp(local, bmin, bmax);
	float outside = length(local - clamped);

	vec3 uvw = (clamped - bmin) / (bmax - bmin);
	vec3 texel = o.atlasOrigin.xyz + clamp(uvw * o.atlasSize.xyz, vec3(0.5), o.atlasSize.xyz - 0.5);
	float sampled = texture(SdfAtlas, texel / sdfAtlasTexels).r;

	if (outside <= 0.0) return sampled;

	vec3 meshMin = bmin + o.padding.xyz;
	vec3 meshMax = bmax - o.padding.xyz;
	float outsideMesh = length(local - clamp(local, meshMin, meshMax));
	return max(outsideMesh, sampled - outside);
}

// Index of the object whose surface is nearest to world point p (within maxDistance), or -1.
// Used to identify what a global SDF ray hit, like Lumen's mesh SDF traces know their object.
int SceneObjectAtPoint(vec3 p, float maxDistance, out vec3 localPos)
{
	int best = -1;
	float bestDistance = maxDistance;
	localPos = vec3(0.0);

	for (int i = 0; i < sceneObjectCount; i++)
	{
		SceneObject o = sceneObjects[i];
		vec3 local = (o.worldToLocal * vec4(p, 1.0)).xyz;
		if (any(lessThan(local, o.boundsMin.xyz - maxDistance)) || any(greaterThan(local, o.boundsMax.xyz + maxDistance))) continue;

		float d = abs(SceneObjectDistance(o, local));
		if (d < bestDistance)
		{
			bestDistance = d;
			best = i;
			localPos = local;
		}
	}

	return best;
}

vec3 SceneObjectWorldToLocalDir(SceneObject o, vec3 worldDir)
{
	return normalize(mat3(o.worldToLocal) * worldDir);
}

vec3 SceneObjectLocalToWorldDir(SceneObject o, vec3 localDir)
{
	return normalize(mat3(o.localToWorld) * localDir);
}
