//Compute

#version 430

#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Resets this frame's adaptive probe lists: the total count and every tile's count. One thread per tile.
// (A buffer clear from the CPU side stalls on the previous frame's readers; this runs in the GPU timeline.)
layout(local_size_x = 64) in;

void main()
{
	uint id = gl_GlobalInvocationID.x;
	if (id == 0u)
	{
		adaptiveProbeCount = 0u;
	}
	uint tiles = uint(probeCount.x * probeCount.y);
	if (id < tiles)
	{
		adaptiveTileData[id * uint(ADAPTIVE_TILE_STRIDE)] = 0u;
	}
}

/////Compute
