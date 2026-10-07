//Compute

#version 430

#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Freezes every tile's adaptive probe count before a placement level runs and clears the "not flat" flags
// before the first level (see AdaptiveLevelCounts).
layout(local_size_x = 64) in;

uniform int clearFlags;

void main()
{
	uint id = gl_GlobalInvocationID.x;
	uint tiles = uint(probeCount.x * probeCount.y);
	if (id < tiles)
	{
		adaptiveLevelCount[id] = adaptiveTileData[id * uint(ADAPTIVE_TILE_STRIDE)];
		if (clearFlags != 0) adaptiveLevelCount[tiles + id] = 0u;
	}
}

/////Compute
