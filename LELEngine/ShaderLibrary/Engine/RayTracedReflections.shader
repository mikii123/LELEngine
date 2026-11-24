//Compute

#version 430

layout(local_size_x = 8, local_size_y = 8) in;

// Only the LEL_RAY_QUERY variant traces (on a device with ray queries); the other is an empty stand-in, never dispatched.
#ifdef LEL_RAY_QUERY
#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/SceneRayQuery.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/HistoryMeta.glsl"
#include "Engine/Sky.glsl"

// Traced reflections of glossy surfaces (Rendering.Passes.RayTracedReflectionPass, after the screen probe gather; only in
// the LEL_RAY_QUERY variant, with Lumen's rays on the scene's triangles): per pixel of the resolve whose roughness is
// below maxRoughness, one ray along a direction of the surface's GGX lobe (the visible normals' distribution, Heitz 2018;
// the mirror direction for a smooth one), through the GI's objects as Lumen's rays go: a front face is lit from the
// surface cache (Lumen's hit lighting from the cards), a back face gives nothing, a ray that leaves the GI volume sees the
// sky. A sample is held to a few times the probes' reflection there (a ray that finds a small bright emitter would stay a
// speck for many frames). The result is accumulated over frames with the reprojected history of the same surface point
// (fewer frames the smoother the surface: a mirror's reflection moves with the view); Engine/RayTracedReflectionFilter.shader
// then smooths it over the neighbours of the same surface (wider the rougher) and blends it into the screen probes'
// reflection over the last quarter of the roughness range, so rougher surfaces keep theirs.
// With LEL_REFLECTION_TILES the work groups are the 8 x 8 tiles Engine/RayTracedReflectionTiles.shader listed (those
// with a glossy pixel), dispatched indirectly; a tile left out writes nothing, so history and neighbours are only taken
// from tiles flagged in their frame. Without it every texel of the resolve runs.
// History: rgb the accumulated reflection, a the frames it holds; meta: Engine/HistoryMeta.glsl (view depth, the normal in
// 8 bits an axis; 0: not traced, the history there is not this frame's).

const uint MetaNormalBits = 8u;

uniform sampler2D NormalRoughness;
uniform sampler2D Motion;              // world-space motion since the previous frame (xyz), w = 1 where the surface moved
uniform sampler2D ProbeSpecular;       // the screen probes' reflection (rgb), a linear view depth
uniform sampler2D FinalLighting;       // the surface cache's lighting
uniform sampler2D HistoryReflection;   // the previous frame's traced reflection (rgb), a its frames
uniform usampler2D HistoryMeta;        // the previous frame's meta (0: none)
uniform sampler2D SceneDepth;

uniform mat4 invViewProjection;
uniform mat4 previousViewProjection;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform vec3 previousCameraPosition;
uniform vec3 previousCameraForward;
uniform ivec2 screenSize;              // the scene's pixels (depth, normals, motion)
uniform ivec2 outputSize;              // the resolve's (this pass's) pixels
uniform float maxRoughness;
uniform int frameIndex;
uniform int historyValid;

layout(binding = 1, rgba16f) uniform writeonly image2D OutHistory;
layout(binding = 2, r32ui) uniform writeonly uimage2D OutMeta;

#ifdef LEL_REFLECTION_TILES
layout(std430, binding = 0) readonly buffer TileList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint tiles[];
};

uniform usampler2D PreviousTileFlags;  // the previous frame's flags: which tiles it traced
#endif

// A sample at most this many times the probes' reflection there (luminance, plus one unit of radiance).
const float SampleClamp = 4.0;

// A half vector of the GGX distribution of visible normals (tangent space, z the normal) seen from Ve.
vec3 SampleGGXVisibleNormal(vec3 Ve, float alpha, vec2 u)
{
	vec3 Vh = normalize(vec3(alpha * Ve.x, alpha * Ve.y, Ve.z));
	float lengthSquared = Vh.x * Vh.x + Vh.y * Vh.y;
	vec3 T1 = lengthSquared > 0.0 ? vec3(-Vh.y, Vh.x, 0.0) * inversesqrt(lengthSquared) : vec3(1.0, 0.0, 0.0);
	vec3 T2 = cross(Vh, T1);
	float r = sqrt(u.x);
	float phi = 2.0 * SAMPLING_PI * u.y;
	float t1 = r * cos(phi);
	float t2 = r * sin(phi);
	float s = 0.5 * (1.0 + Vh.z);
	t2 = (1.0 - s) * sqrt(max(1.0 - t1 * t1, 0.0)) + s * t2;
	vec3 Nh = t1 * T1 + t2 * T2 + sqrt(max(1.0 - t1 * t1 - t2 * t2, 0.0)) * Vh;
	return normalize(vec3(alpha * Nh.x, alpha * Nh.y, max(Nh.z, 0.0)));
}

// The previous frame traced this texel (its tile was listed then).
bool TracedLastFrame(ivec2 tap)
{
#ifdef LEL_REFLECTION_TILES
	return texelFetch(PreviousTileFlags, tap >> 3, 0).r != 0u;
#else
	return true;
#endif
}

void main()
{
#ifdef LEL_REFLECTION_TILES
	uint packedTile = tiles[gl_WorkGroupID.x];
	ivec2 texel = ivec2(int(packedTile & 0xFFFFu), int(packedTile >> 16)) * 8 + ivec2(gl_LocalInvocationID.xy);
#else
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
#endif
	if (any(greaterThanEqual(texel, outputSize)))
	{
		return;
	}

	// Nothing else is read or written for a surface too rough to trace: its meta says so.
	ivec2 pixel = min(ivec2((vec2(texel) + 0.5) * vec2(screenSize) / vec2(outputSize)), screenSize - 1);
	vec4 nr = texelFetch(NormalRoughness, pixel, 0);
	float roughness = nr.w > 0.0 ? nr.w : 0.6;
	float depth = roughness < maxRoughness ? texelFetch(SceneDepth, pixel, 0).r : 1.0;
	if (depth >= 1.0)
	{
		imageStore(OutMeta, texel, uvec4(0u));
		return;
	}

	vec4 probe = texelFetch(ProbeSpecular, texel, 0);

	vec2 uv = (vec2(pixel) + 0.5) / vec2(screenSize);
	vec4 world = invViewProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec3 position = world.xyz / world.w;
	vec3 N = normalize(nr.xyz);
	vec3 V = normalize(cameraPosition - position);
	float viewDepth = dot(position - cameraPosition, cameraForward);

	// ---- one ray of the surface's lobe (the mirror direction below a roughness of 0.05)
	vec3 tangent, bitangent;
	TangentFrame(N, tangent, bitangent);
	float alpha = roughness * roughness;
	vec3 direction = reflect(-V, N);
	if (roughness > 0.05)
	{
		uint seed = HashCoord(texel, uint(frameIndex));
		vec2 u = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));
		vec3 Ve = vec3(dot(V, tangent), dot(V, bitangent), max(dot(V, N), 1e-4));
		vec3 H = SampleGGXVisibleNormal(Ve, alpha, u);
		vec3 h = normalize(tangent * H.x + bitangent * H.y + N * H.z);
		vec3 sampled = reflect(-V, h);
		direction = dot(sampled, N) > 0.0 ? sampled : direction;
	}

	vec3 origin = SceneRayOrigin(position, N, DepthRayBias(viewDepth));
	SceneHit hit;
	vec3 traced = TraceSceneRay(origin, direction, 0.0, 2.0 * sdfGridSize, hit) ? SceneHitRadiance(hit, origin, direction, FinalLighting) : SkyRadiance(direction);
	float limit = SampleClamp * (dot(probe.rgb, vec3(0.2126, 0.7152, 0.0722)) + 1.0);
	float luminance = dot(traced, vec3(0.2126, 0.7152, 0.0722));
	traced *= luminance > limit ? limit / luminance : 1.0;

	// ---- the history of this surface point (the four texels around its previous place, by depth and normal)
	float cap = mix(1.0, 24.0, clamp(roughness / maxRoughness, 0.0, 1.0));
	vec4 motion = texelFetch(Motion, pixel, 0);
	if (motion.w > 0.5)
	{
		cap = min(cap, 3.0);
	}

	float frames = 1.0;
	if (historyValid != 0 && cap > 1.0)
	{
		vec3 previousPosition = position - motion.xyz;
		vec4 previousClip = previousViewProjection * vec4(previousPosition, 1.0);
		if (previousClip.w > 0.0)
		{
			vec2 previousTexel = (previousClip.xy / previousClip.w * 0.5 + 0.5) * vec2(outputSize) - 0.5;
			ivec2 base = ivec2(floor(previousTexel));
			vec2 f = previousTexel - vec2(base);
			float expectedDepth = dot(previousPosition - previousCameraPosition, previousCameraForward);
			float tolerance = 0.03 * expectedDepth + 0.03;
			vec3 sum = vec3(0.0);
			float weightSum = 0.0;
			float framesSum = 0.0;
			for (int k = 0; k < 4; k++)
			{
				ivec2 offset = ivec2(k & 1, k >> 1);
				ivec2 tap = base + offset;
				if (any(lessThan(tap, ivec2(0))) || any(greaterThanEqual(tap, outputSize)) || !TracedLastFrame(tap))
				{
					continue;
				}

				uint meta = texelFetch(HistoryMeta, tap, 0).r;
				if (meta == 0u)
				{
					continue;
				}

				vec4 previous = texelFetch(HistoryReflection, tap, 0);

				float w = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y)
					* clamp(1.0 - abs(HistoryMetaDepth(meta) - expectedDepth) / tolerance, 0.0, 1.0)
					* smoothstep(0.9, 0.98, dot(HistoryMetaNormal(meta, MetaNormalBits), N));
				if (w <= 0.0)
				{
					continue;
				}

				sum += previous.rgb * w;
				weightSum += w;
				framesSum += previous.a * w;
			}

			if (weightSum > 0.05)
			{
				float previousFrames = framesSum / weightSum * clamp(weightSum * 2.0, 0.0, 1.0);
				frames = min(previousFrames + 1.0, cap);
				traced = mix(sum / weightSum, traced, 1.0 / frames);
			}
		}
	}

	// a: the frames accumulated (the filter reads the roughness from the normals).
	imageStore(OutHistory, texel, vec4(traced, frames));
	imageStore(OutMeta, texel, uvec4(PackHistoryMeta(viewDepth, N, MetaNormalBits, 0u)));
}
#else
void main()
{
}
#endif

/////Compute
