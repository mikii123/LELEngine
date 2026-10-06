// Adaptive screen probes (Lumen's adaptive probe placement): extra probes placed every frame where the
// uniform probe grid cannot be interpolated (thin geometry, silhouettes, depth discontinuities). Each
// 16-pixel screen tile keeps a short list of the adaptive probes placed inside it; the probes themselves
// live in the rows below the uniform grid of the probe atlas (see AdaptiveProbeTexel in ScreenProbes.glsl).
//
// Buffer layout (std430): adaptiveProbeCount, then per tile: count, followed by MAX_ADAPTIVE_PER_TILE pairs
// of (probe index, packed pixel). Cleared to zero every frame before placement.
// Requires Engine/ScreenProbes.glsl.

#define MAX_ADAPTIVE_PER_TILE 16
#define ADAPTIVE_TILE_STRIDE (1 + 2 * MAX_ADAPTIVE_PER_TILE)

layout(std430, binding = 5) buffer AdaptiveProbeBuffer
{
	uint adaptiveProbeCount;
	uint adaptiveTileData[];
};

// Last frame's lists (double buffered): the temporal history of an adaptive probe is searched here.
layout(std430, binding = 6) readonly buffer PreviousAdaptiveProbeBuffer
{
	uint previousAdaptiveProbeCount;
	uint previousAdaptiveTileData[];
};

int AdaptiveTileOffset(ivec2 pixel)
{
	ivec2 tile = pixel / probeSpacing;
	return (tile.y * probeCount.x + tile.x) * ADAPTIVE_TILE_STRIDE;
}

int AdaptiveTileCount(int tileOffset)
{
	return int(min(adaptiveTileData[tileOffset], uint(MAX_ADAPTIVE_PER_TILE)));
}

uint AdaptiveTileProbe(int tileOffset, int slot)
{
	return adaptiveTileData[tileOffset + 1 + 2 * slot];
}

ivec2 AdaptiveTilePixel(int tileOffset, int slot)
{
	return UnpackPixel(adaptiveTileData[tileOffset + 2 + 2 * slot]);
}

int PreviousAdaptiveTileCount(int tileOffset)
{
	return int(min(previousAdaptiveTileData[tileOffset], uint(MAX_ADAPTIVE_PER_TILE)));
}

uint PreviousAdaptiveTileProbe(int tileOffset, int slot)
{
	return previousAdaptiveTileData[tileOffset + 1 + 2 * slot];
}

// Tent kernel over one probe spacing: how much an adaptive probe at probePixel says about `pixel`.
float AdaptiveSpatialWeight(ivec2 pixel, ivec2 probePixel)
{
	return max(0.0, 1.0 - length(vec2(pixel - probePixel)) / float(probeSpacing));
}
