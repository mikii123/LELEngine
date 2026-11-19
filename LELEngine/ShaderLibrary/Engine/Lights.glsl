// The scene's local lights (Light components: point and spot) for the lit materials, the GI and the volumetric objects:
// every light that shines, however many (Rendering.LightBuffer fills the list every frame: a storage buffer on binding
// 16, its header a uniform block on binding 7). Physical falloff: the inverse square of the distance (from a source of
// the light's radius), faded to nothing at its range (Frostbite's window); a spot's cone fades between its inner and
// outer angle; the clouds between the light and the point dim it (the volumes' transmittance from the light towards six
// directions, blended for the direction). Radiance units as the sun's: a white surface facing a light of radiance 1
// reflects 1. Include after #version: #include "Engine/Lights.glsl"
//
// A point visits only the lights that may reach it, so no light takes another's place and a light costs only where it
// reaches. Drawn surfaces: the view of the camera being drawn is cut into clusters (Rendering.LightClusterPass: tiles
// of the screen x slices of depth, a bit per light whose range reaches the cluster's box, binding 17), and a point
// visits its cluster's lights:
//     for (LelLightIterator lights = LelLightsAt(position); LelNextLight(lights);)
//     {
//         int i = lights.light;
//         ... LelLightAt(i, position, L, radiance) ...
//     }
// The cluster is found from the world position and the matrices of the camera the clusters were made for, so any draw
// finds the right one; a point outside that camera's view (another camera's draw, a preview) or drawn before any
// clusters were made visits every light (LelAllLights). Compute shaders that light points in the world (the GI) define
// LEL_LIGHT_GROUPS before the include: every thread of a work group calls LelLightGroupCullBox or LelLightGroupCullPoints
// in uniform control flow (before any return), the group finds the lights reaching it together, and each thread then
// visits LelGroupLights(). Such a shader declares its work group size (layout(local_size_x = ...) in;) before the
// include: the culling reads gl_WorkGroupSize, which GLSL allows only after that declaration (NVIDIA rejects it before;
// glslang does not notice).
#ifndef LEL_LIGHTS_GLSL
#define LEL_LIGHTS_GLSL

#define LEL_LIGHT_GI 1
#define LEL_LIGHT_VOLUMES 2
#define LEL_LIGHT_GI_SHADOWS 4

struct LelLight
{
	vec4 positionRange;   // world position, range
	vec4 radiance;        // colour x intensity, w: how much of it goes into the GI (0: none)
	vec4 spot;            // the spot's direction (world), w: cos of its outer half angle (-2: a point light)
	vec4 params;          // x: 1 / (cos inner - cos outer), y: flags (LEL_LIGHT_*), z: the source's radius
	vec4 transmittanceA;  // the clouds' transmittance from the light towards +x, -x, +y
	vec4 transmittanceB;  // towards -y, +z, -z
};

// The list's header (Rendering.LightBuffer): the count, and the clusters of the view being drawn
// (Rendering.LightClusterPass; none: y is 0).
layout(std140, binding = 7) uniform LelLightBlock
{
	ivec4 lelLightCount;                 // x: lights in lelLights; y: mask words of a cluster (0: no clusters); z, w: tiles across, down
	ivec4 lelLightClusterSlices;         // x: slices of depth
	mat4 lelLightClusterViewProjection;  // world -> clip space of the camera the clusters were made for
	vec4 lelLightClusterDepth;           // that camera's view depth of a world point p: dot(vec4(p, 1.0), lelLightClusterDepth)
	vec4 lelLightClusterSlicing;         // a depth's slice: floor(log(depth) * x + y); z, w: the nearest and furthest depth covered
};

layout(std430, binding = 16) readonly buffer LelLightList
{
	LelLight lelLights[];
};

#ifndef LEL_LIGHT_GROUPS
// A cluster's mask: lelLightCount.y words, light i is bit i % 32 of word i / 32.
layout(std430, binding = 17) readonly buffer LelLightClusterMasks
{
	uint lelLightClusterMasks[];
};
#else
// Mask words a work group keeps in shared memory: up to 32 x this many lights; with more, its threads visit every light.
#ifndef LEL_LIGHT_GROUP_WORDS
#define LEL_LIGHT_GROUP_WORDS 256
#endif
shared uint lelLightGroupMask[LEL_LIGHT_GROUP_WORDS];
shared uint lelLightGroupBounds[6];
#endif

int LelLightCount()
{
	return lelLightCount.x;
}

// A walk over lights: LelNextLight moves it to the next one (false when there are no more), whose index is light.
struct LelLightIterator
{
	int light;  // the light the walk is at (an index of lelLights)
	int word;   // the mask word it is in
	uint bits;  // that word's lights not visited yet
	int first;  // where the mask starts: a cluster's first word in lelLightClusterMasks; -1: every light; -2: the work group's mask
	int words;  // the mask's words
};

// Every light of the list (whether one reaches a point is the caller's range test: LelLightAt).
LelLightIterator LelAllLights()
{
	LelLightIterator it;
	it.light = -1;
	it.word = -1;
	it.bits = 0u;
	it.first = -1;
	it.words = 0;
	return it;
}

bool LelNextLight(inout LelLightIterator it)
{
	if (it.first == -1)
	{
		it.light++;
		return it.light < LelLightCount();
	}

	while (it.bits == 0u)
	{
		it.word++;
		if (it.word >= it.words)
		{
			return false;
		}

#ifdef LEL_LIGHT_GROUPS
		it.bits = lelLightGroupMask[it.word];
#else
		it.bits = lelLightClusterMasks[it.first + it.word];
#endif
	}

	int bit = findLSB(it.bits);
	it.bits &= it.bits - 1u;
	it.light = it.word * 32 + bit;
	return true;
}

#ifndef LEL_LIGHT_GROUPS
// The lights that may reach a world point: its cluster's, in the view the clusters were made for; every light when
// there are none, or the point is outside that view.
LelLightIterator LelLightsAt(vec3 position)
{
	LelLightIterator it = LelAllLights();
	int words = lelLightCount.y;
	if (words <= 0)
	{
		return it;
	}

	vec4 clip = lelLightClusterViewProjection * vec4(position, 1.0);
	float depth = dot(vec4(position, 1.0), lelLightClusterDepth);
	if (clip.w <= 0.0 || depth <= lelLightClusterSlicing.z || depth > lelLightClusterSlicing.w)
	{
		return it;
	}

	vec2 ndc = clip.xy / clip.w;
	if (any(greaterThan(abs(ndc), vec2(1.001))))
	{
		return it;
	}

	ivec2 tiles = lelLightCount.zw;
	ivec2 tile = clamp(ivec2((ndc * 0.5 + 0.5) * vec2(tiles)), ivec2(0), tiles - 1);
	int slice = clamp(int(floor(log(depth) * lelLightClusterSlicing.x + lelLightClusterSlicing.y)), 0, lelLightClusterSlices.x - 1);
	it.first = ((slice * tiles.y + tile.y) * tiles.x + tile.x) * words;
	it.words = words;
	return it;
}
#else
// Whether light i's range reaches a box (world).
bool LelLightReachesBox(int i, vec3 boxMin, vec3 boxMax)
{
	vec4 positionRange = lelLights[i].positionRange;
	vec3 outside = positionRange.xyz - clamp(positionRange.xyz, boxMin, boxMax);
	return dot(outside, outside) < positionRange.w * positionRange.w;
}

// The work group's lights: those whose range reaches the box (world; lights false: the group lights nothing), with
// giOnly only those that go into the GI. Every thread of the group calls it in uniform control flow (no return before
// it); LelGroupLights() then visits them. (Not "active": a reserved word of GLSL.)
void LelLightGroupCullBox(bool lights, vec3 boxMin, vec3 boxMax, bool giOnly)
{
	uint threads = gl_WorkGroupSize.x * gl_WorkGroupSize.y * gl_WorkGroupSize.z;
	uint count = uint(LelLightCount());
	uint words = (count + 31u) / 32u;
	bool fits = words <= uint(LEL_LIGHT_GROUP_WORDS);
	if (fits)
	{
		for (uint w = gl_LocalInvocationIndex; w < words; w += threads)
		{
			lelLightGroupMask[w] = 0u;
		}
	}

	memoryBarrierShared();
	barrier();
	if (fits && lights)
	{
		for (uint i = gl_LocalInvocationIndex; i < count; i += threads)
		{
			if ((!giOnly || lelLights[i].radiance.w > 0.0) && LelLightReachesBox(int(i), boxMin, boxMax))
			{
				atomicOr(lelLightGroupMask[i / 32u], 1u << (i % 32u));
			}
		}
	}

	memoryBarrierShared();
	barrier();
}

// A float's bits ordered as the floats are (unsigned comparisons, atomicMin / atomicMax), and back.
uint LelOrderedBits(float value)
{
	uint bits = floatBitsToUint(value);
	return (bits & 0x80000000u) != 0u ? ~bits : bits | 0x80000000u;
}

float LelFromOrderedBits(uint bits)
{
	return uintBitsToFloat((bits & 0x80000000u) != 0u ? bits & 0x7FFFFFFFu : ~bits);
}

// As LelLightGroupCullBox, the box being the bounds of the points the group's threads light (valid: this thread lights
// point). Every thread calls it in uniform control flow.
void LelLightGroupCullPoints(bool valid, vec3 point, bool giOnly)
{
	if (gl_LocalInvocationIndex < 3u)
	{
		lelLightGroupBounds[gl_LocalInvocationIndex] = 0xFFFFFFFFu;
		lelLightGroupBounds[gl_LocalInvocationIndex + 3u] = 0u;
	}

	memoryBarrierShared();
	barrier();
	if (valid)
	{
		atomicMin(lelLightGroupBounds[0], LelOrderedBits(point.x));
		atomicMin(lelLightGroupBounds[1], LelOrderedBits(point.y));
		atomicMin(lelLightGroupBounds[2], LelOrderedBits(point.z));
		atomicMax(lelLightGroupBounds[3], LelOrderedBits(point.x));
		atomicMax(lelLightGroupBounds[4], LelOrderedBits(point.y));
		atomicMax(lelLightGroupBounds[5], LelOrderedBits(point.z));
	}

	memoryBarrierShared();
	barrier();
	// Nothing to light: the maximum kept its start (every float's ordered bits are above 0).
	bool lights = lelLightGroupBounds[3] != 0u;
	vec3 boxMin = vec3(LelFromOrderedBits(lelLightGroupBounds[0]), LelFromOrderedBits(lelLightGroupBounds[1]), LelFromOrderedBits(lelLightGroupBounds[2]));
	vec3 boxMax = vec3(LelFromOrderedBits(lelLightGroupBounds[3]), LelFromOrderedBits(lelLightGroupBounds[4]), LelFromOrderedBits(lelLightGroupBounds[5]));
	LelLightGroupCullBox(lights, boxMin, boxMax, giOnly);
}

// The lights the work group found (LelLightGroupCullBox / LelLightGroupCullPoints): every light when the list is
// longer than its mask.
LelLightIterator LelGroupLights()
{
	LelLightIterator it = LelAllLights();
	int words = (LelLightCount() + 31) / 32;
	if (words <= LEL_LIGHT_GROUP_WORDS)
	{
		it.first = -2;
		it.words = words;
	}

	return it;
}
#endif

bool LelLightHas(int i, int flag)
{
	return (int(lelLights[i].params.y) & flag) != 0;
}

// 1 / d^2 from the source's radius on, faded to 0 at the range: (1 - (d / range)^4)^2.
float LelLightFalloff(float d2, float range, float sourceRadius)
{
	float r2 = range * range;
	float fade = clamp(1.0 - (d2 * d2) / (r2 * r2), 0.0, 1.0);
	return fade * fade / max(d2, sourceRadius * sourceRadius);
}

// How much of light i gets through the clouds towards a direction away from it (unit vector).
float LelLightCloudTransmittance(int i, vec3 away)
{
	vec3 w = away * away;
	vec4 a = lelLights[i].transmittanceA;
	vec4 b = lelLights[i].transmittanceB;
	return (away.x >= 0.0 ? a.x : a.y) * w.x + (away.y >= 0.0 ? a.z : b.x) * w.y + (away.z >= 0.0 ? b.y : b.z) * w.z;
}

// Light i's falloff and cone at a point (no colour, no clouds): the direction towards it and the factor; false outside
// its range or cone.
bool LelLightShape(int i, vec3 position, out vec3 L, out float attenuation)
{
	vec4 positionRange = lelLights[i].positionRange;
	vec3 toLight = positionRange.xyz - position;
	float d2 = dot(toLight, toLight);
	L = vec3(0.0, 1.0, 0.0);
	attenuation = 0.0;
	if (d2 >= positionRange.w * positionRange.w)
	{
		return false;
	}

	L = toLight * inversesqrt(max(d2, 1e-12));
	attenuation = LelLightFalloff(d2, positionRange.w, lelLights[i].params.z);
	vec4 spot = lelLights[i].spot;
	if (spot.w > -1.5)
	{
		float cone = clamp((dot(-L, spot.xyz) - spot.w) * lelLights[i].params.x, 0.0, 1.0);
		attenuation *= cone * cone;
	}

	return attenuation > 0.0;
}

// Light i at a point: the direction towards it and its radiance there (falloff, cone, clouds); false when it does not
// reach the point.
bool LelLightAt(int i, vec3 position, out vec3 L, out vec3 radiance)
{
	float attenuation;
	radiance = vec3(0.0);
	if (!LelLightShape(i, position, L, attenuation))
	{
		return false;
	}

	radiance = lelLights[i].radiance.rgb * (attenuation * LelLightCloudTransmittance(i, -L));
	return true;
}

#endif
