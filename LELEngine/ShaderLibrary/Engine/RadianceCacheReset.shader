//Compute

#version 430

// Empties the radiance cache probes in a list (RadianceCachePass.ScrollGrid): the slots the probes that left a scrolled
// grid had, now given to the probes that entered it. Their atlas and accumulator tiles go to "no data" (alpha 0: the
// lookups skip them and trace on), their spherical harmonics to invalid and their frame counters to "never updated",
// so the first update replaces the history instead of blending into a probe that stood somewhere else. One work group
// per listed probe.
layout(local_size_x = 8, local_size_y = 8) in;

uniform int probesPerRow; // tiles per atlas row
uniform int tileSize;     // stored texels per axis plus the border
uniform int probeCount;   // probes in the cache (the frame counters' stride)
uniform int resetCount;

layout(binding = 0, rgba16f) uniform writeonly image2D Atlas;
layout(binding = 1, rgba32f) uniform writeonly image2D Accumulator;
layout(binding = 2, rgba16f) uniform writeonly image2DArray SH;

layout(std430, binding = 4) buffer RadianceCacheProbeFrames
{
	int probeFrames[]; // [probe] last update frame, [count + probe] update count, [2 count + probe] moving cells
};

layout(std430, binding = 5) readonly buffer ResetList
{
	int resetProbes[];
};

void main()
{
	int listed = int(gl_WorkGroupID.x);
	if (listed >= resetCount) return;

	int probe = resetProbes[listed];
	ivec2 tile = ivec2(probe % probesPerRow, probe / probesPerRow);
	ivec2 origin = tile * tileSize;
	for (int y = int(gl_LocalInvocationID.y); y < tileSize; y += 8)
	{
		for (int x = int(gl_LocalInvocationID.x); x < tileSize; x += 8)
		{
			imageStore(Atlas, origin + ivec2(x, y), vec4(0.0));
			imageStore(Accumulator, origin + ivec2(x, y), vec4(0.0));
		}
	}

	if (gl_LocalInvocationIndex == 0u)
	{
		for (int layer = 0; layer < 7; layer++)
		{
			imageStore(SH, ivec3(tile, layer), vec4(0.0));
		}

		probeFrames[probe] = -1;
		probeFrames[probeCount + probe] = 0;
		probeFrames[2 * probeCount + probe] = 0;
	}
}

/////Compute
