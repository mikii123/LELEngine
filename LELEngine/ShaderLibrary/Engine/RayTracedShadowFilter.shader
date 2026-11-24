//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/HistoryMeta.glsl"

// Traced shadows (Rendering.Passes.RayTracedShadowPass), step 2: the frames' accumulation (Engine/RayTracedShadowTrace.shader)
// smoothed over the neighbouring pixels of the same surface (a Gaussian of radius pixels, weighted by how well their
// view depth and normal match the pixel's), into the mask the lit materials read (Engine/RayTracedShadows.glsl). What a
// pixel's few frames of single rays leave of noise in a penumbra goes; a shadow's sharp edge on another surface stays.
// A pixel whose rays all agreed (every channel exactly 0 or 1: fully lit or in shadow over its whole history) is kept
// as it is, so only penumbrae pay for the filter.
layout(local_size_x = 8, local_size_y = 8) in;

// The meta's normal bits per axis (Engine/RayTracedShadowTrace.shader).
const uint MetaNormalBits = 6u;

uniform sampler2DArray AccumulatedMask;
uniform usampler2D Meta;    // Engine/HistoryMeta.glsl: linear view depth (0: no surface the mask is read for), normal
uniform ivec2 screenSize;
uniform int layers;
uniform int radius;         // pixels (0: a copy)

layout(binding = 0, rgba8) uniform writeonly image2DArray OutMask;

void main()
{
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(pixel, screenSize)))
	{
		return;
	}

	// A pixel the trace left (no traced light reaches it): nothing reads the mask there.
	uint centerMeta = texelFetch(Meta, pixel, 0).r;
	float centerDepth = HistoryMetaDepth(centerMeta);
	if (centerDepth <= 0.0)
	{
		return;
	}

	vec4 own[3];
	bool settled = true;
	for (int layer = 0; layer < layers; layer++)
	{
		own[layer] = texelFetch(AccumulatedMask, ivec3(pixel, layer), 0);
		settled = settled && all(equal(own[layer] * (1.0 - own[layer]), vec4(0.0)));
	}

	if (radius <= 0 || settled)
	{
		for (int layer = 0; layer < layers; layer++)
		{
			imageStore(OutMask, ivec3(pixel, layer), own[layer]);
		}

		return;
	}

	vec3 normal = HistoryMetaNormal(centerMeta, MetaNormalBits);
	float tolerance = 0.02 * centerDepth + 0.02;
	float sigma2 = float(radius * radius) * 0.5 + 0.25;
	vec4 sum[3] = vec4[3](vec4(0.0), vec4(0.0), vec4(0.0));
	float weightSum = 0.0;
	for (int dy = -radius; dy <= radius; dy++)
	{
		for (int dx = -radius; dx <= radius; dx++)
		{
			ivec2 tap = pixel + ivec2(dx, dy);
			if (any(lessThan(tap, ivec2(0))) || any(greaterThanEqual(tap, screenSize)))
			{
				continue;
			}

			uint meta = texelFetch(Meta, tap, 0).r;
			float depth = HistoryMetaDepth(meta);
			if (depth <= 0.0)
			{
				continue;
			}

			// The normals' agreement to the 8th power, by squaring.
			float facing = max(dot(HistoryMetaNormal(meta, MetaNormalBits), normal), 0.0);
			facing *= facing;
			facing *= facing;
			float w = exp(-float(dx * dx + dy * dy) / sigma2)
				* clamp(1.0 - abs(depth - centerDepth) / tolerance, 0.0, 1.0)
				* facing * facing;
			if (w <= 0.0)
			{
				continue;
			}

			for (int layer = 0; layer < layers; layer++)
			{
				sum[layer] += texelFetch(AccumulatedMask, ivec3(tap, layer), 0) * w;
			}

			weightSum += w;
		}
	}

	for (int layer = 0; layer < layers; layer++)
	{
		imageStore(OutMask, ivec3(pixel, layer), weightSum > 0.0 ? sum[layer] / weightSum : texelFetch(AccumulatedMask, ivec3(pixel, layer), 0));
	}
}

/////Compute
