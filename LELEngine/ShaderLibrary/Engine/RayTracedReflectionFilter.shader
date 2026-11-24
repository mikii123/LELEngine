//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/HistoryMeta.glsl"

// Traced reflections (Rendering.Passes.RayTracedReflectionPass), step 2: the frames' accumulation
// (Engine/RayTracedReflections.shader) smoothed over the neighbouring pixels of the same surface (a Gaussian up to two
// pixels wide on the roughest traced surfaces, none on a mirror; weighted by how well their view depth and normal match),
// then blended into the screen probes' reflection over the last quarter of the roughness range, written over the probe
// gather's specular (rgb radiance along the reflection, a linear view depth, kept) where the surface was traced; the
// rest of it is not touched. With LEL_REFLECTION_TILES only the listed tiles run (dispatched indirectly), and a
// neighbour counts only in a tile traced this frame.
layout(local_size_x = 8, local_size_y = 8) in;

// The meta's normal bits per axis (Engine/RayTracedReflections.shader).
const uint MetaNormalBits = 8u;

uniform sampler2D TracedReflection;   // rgb the accumulated traced reflection, a its frames
uniform usampler2D Meta;              // Engine/HistoryMeta.glsl (0: not traced)
uniform sampler2D NormalRoughness;    // the scene's: the surfaces' roughness
uniform ivec2 screenSize;
uniform ivec2 outputSize;
uniform float maxRoughness;

layout(binding = 0, rgba16f) uniform image2D Specular;  // the probe gather's specular

#ifdef LEL_REFLECTION_TILES
layout(std430, binding = 0) readonly buffer TileList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint tiles[];
};

uniform usampler2D TileFlags;  // this frame's: which tiles were traced
#endif

// The meta of a texel traced this frame, 0 elsewhere.
uint TracedMeta(ivec2 tap)
{
#ifdef LEL_REFLECTION_TILES
	if (texelFetch(TileFlags, tap >> 3, 0).r == 0u)
	{
		return 0u;
	}
#endif
	return texelFetch(Meta, tap, 0).r;
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

	uint centerMeta = texelFetch(Meta, texel, 0).r;
	if (centerMeta == 0u)
	{
		return;
	}

	vec4 own = texelFetch(TracedReflection, texel, 0);
	ivec2 pixel = min(ivec2((vec2(texel) + 0.5) * vec2(screenSize) / vec2(outputSize)), screenSize - 1);
	float roughness = texelFetch(NormalRoughness, pixel, 0).w;
	roughness = roughness > 0.0 ? roughness : 0.6;
	int radius = int(round(2.0 * clamp(roughness / maxRoughness, 0.0, 1.0)));
	vec3 filtered = own.rgb;
	if (radius > 0)
	{
		float centerDepth = HistoryMetaDepth(centerMeta);
		vec3 normal = HistoryMetaNormal(centerMeta, MetaNormalBits);
		float tolerance = 0.02 * centerDepth + 0.02;
		float sigma2 = float(radius * radius) * 0.5 + 0.25;
		vec3 sum = vec3(0.0);
		float weightSum = 0.0;
		for (int dy = -radius; dy <= radius; dy++)
		{
			for (int dx = -radius; dx <= radius; dx++)
			{
				ivec2 tap = texel + ivec2(dx, dy);
				if (any(lessThan(tap, ivec2(0))) || any(greaterThanEqual(tap, outputSize)))
				{
					continue;
				}

				uint meta = TracedMeta(tap);
				if (meta == 0u)
				{
					continue;
				}

				// The normals' agreement to the 32nd power, by squaring.
				float facing = max(dot(HistoryMetaNormal(meta, MetaNormalBits), normal), 0.0);
				facing *= facing;
				facing *= facing;
				facing *= facing;
				facing *= facing;
				float w = exp(-float(dx * dx + dy * dy) / sigma2)
					* clamp(1.0 - abs(HistoryMetaDepth(meta) - centerDepth) / tolerance, 0.0, 1.0)
					* facing * facing;
				sum += texelFetch(TracedReflection, tap, 0).rgb * w;
				weightSum += w;
			}
		}

		filtered = weightSum > 0.0 ? sum / weightSum : own.rgb;
	}

	// Into the probes' reflection over the last quarter of the range.
	vec4 probe = imageLoad(Specular, texel);
	float weight = 1.0 - smoothstep(0.75 * maxRoughness, maxRoughness, roughness);
	imageStore(Specular, texel, vec4(mix(probe.rgb, filtered, weight), probe.a));
}

/////Compute
