//Compute

#version 430

// Back-to-front order of a particle output for one view (LELEngine.VFX.VfxRuntime.RunSorts): a bitonic sort of
// packed keys over a power-of-two list at least as long as the alive particles. A key is one uint: the depth along
// the view, quantized over the system's depth range (far first) in the high bits, the particle index in the low
// bits, so the sort moves 4 bytes per particle and the draw unpacks the index with a mask. Slots past the alive
// count hold 0xFFFFFFFF and end up last. Mode 0 builds the keys of a chunk and sorts it in shared memory; longer
// lists merge with mode 3 (two global compare-and-swap steps per pass, four elements per thread), mode 1 (one
// global step) and mode 2 (the steps of a merge stage that stay inside a chunk). Every system shares the kernel:
// the position planes, the alive list and the alive count are parameters.

#define CHUNK 4096u
#define THREADS 512u

layout(local_size_x = 512) in;

layout(std430, binding = 0) readonly buffer VfxSortAttributeBuffer { float vfxAttr[]; };
layout(std430, binding = 1) readonly buffer VfxSortAliveBuffer { uint vfxAlive[]; };
layout(std430, binding = 2) buffer VfxSortKeyBuffer { uint vfxKeys[]; };
layout(std430, binding = 4) readonly buffer VfxSortCounterBuffer { uint vfxCounters[]; };

uniform int vfxSortMode;
uniform uint vfxSortCount;
uniform uint vfxSortK;
uniform uint vfxSortJ;
uniform uint vfxSortAliveCounter;
uniform uint vfxSortPositionPlane;
uniform uint vfxSortCapacity;
uniform int vfxSortWorldSpace;
uniform mat4 vfxSortLocalToWorld;
uniform vec3 vfxSortViewPosition;
uniform vec3 vfxSortViewForward;
// Bits of the particle index, and the depth range the key spans (nearest depth, 1 / span).
uniform uint vfxSortIndexBits;
uniform vec2 vfxSortDepthRange;

shared uint local[CHUNK];

uint Key(uint index, uint alive)
{
	if (index >= alive)
	{
		return 0xFFFFFFFFu;
	}

	uint particle = vfxAlive[index];
	uint p = vfxSortPositionPlane * vfxSortCapacity + particle;
	vec3 position = vec3(vfxAttr[p], vfxAttr[p + vfxSortCapacity], vfxAttr[p + 2u * vfxSortCapacity]);
	vec3 world = vfxSortWorldSpace != 0 ? position : (vfxSortLocalToWorld * vec4(position, 1.0)).xyz;
	float depth = dot(world - vfxSortViewPosition, vfxSortViewForward);
	float t = clamp((depth - vfxSortDepthRange.x) * vfxSortDepthRange.y, 0.0, 1.0);
	uint keyBits = 32u - vfxSortIndexBits;
	uint levels = keyBits >= 32u ? 0xFFFFFFFFu : (1u << keyBits) - 1u;
	// Far particles first: the largest depth gets the smallest key.
	uint quantized = uint((1.0 - t) * float(levels) + 0.5);
	return (quantized << vfxSortIndexBits) | particle;
}

// The compare-and-swap of pair p in step j of stage k, in shared memory (indices relative to the chunk).
void Step(uint groupBase, uint p, uint j, uint k)
{
	uint i = ((p & ~(j - 1u)) << 1u) | (p & (j - 1u));
	uint l = i | j;
	bool ascending = ((groupBase + i) & k) == 0u;
	uint a = local[i];
	uint b = local[l];
	if ((a > b) == ascending)
	{
		local[i] = b;
		local[l] = a;
	}
}

void main()
{
	uint n = vfxSortCount;
	if (vfxSortMode == 3)
	{
		// Two global steps at once (j, then j / 2): a thread owns the four elements the two steps exchange, which
		// share the direction of stage k. Halves the passes over the list.
		uint t = gl_GlobalInvocationID.x;
		if (t >= n / 4u)
		{
			return;
		}

		uint j = vfxSortJ;
		uint h = j >> 1u;
		uint low = t & (h - 1u);
		uint i0 = ((t - low) << 2u) | low;
		uint i1 = i0 + h;
		uint i2 = i0 + j;
		uint i3 = i2 + h;
		bool ascending = (i0 & vfxSortK) == 0u;
		uint a = vfxKeys[i0];
		uint b = vfxKeys[i1];
		uint c = vfxKeys[i2];
		uint d = vfxKeys[i3];
		uint s;
		if ((a > c) == ascending) { s = a; a = c; c = s; }
		if ((b > d) == ascending) { s = b; b = d; d = s; }
		if ((a > b) == ascending) { s = a; a = b; b = s; }
		if ((c > d) == ascending) { s = c; c = d; d = s; }
		vfxKeys[i0] = a;
		vfxKeys[i1] = b;
		vfxKeys[i2] = c;
		vfxKeys[i3] = d;
		return;
	}

	if (vfxSortMode == 1)
	{
		uint p = gl_GlobalInvocationID.x;
		if (p >= n / 2u)
		{
			return;
		}

		uint j = vfxSortJ;
		uint i = ((p & ~(j - 1u)) << 1u) | (p & (j - 1u));
		uint l = i | j;
		bool ascending = (i & vfxSortK) == 0u;
		uint a = vfxKeys[i];
		uint b = vfxKeys[l];
		if ((a > b) == ascending)
		{
			vfxKeys[i] = b;
			vfxKeys[l] = a;
		}

		return;
	}

	uint chunk = min(n, CHUNK);
	uint groupBase = gl_WorkGroupID.x * chunk;
	uint t = gl_LocalInvocationID.x;
	if (vfxSortMode == 0)
	{
		uint alive = min(vfxCounters[vfxSortAliveCounter], vfxSortCapacity);
		for (uint e = t; e < chunk; e += THREADS)
		{
			local[e] = Key(groupBase + e, alive);
		}
	}
	else
	{
		for (uint e = t; e < chunk; e += THREADS)
		{
			local[e] = vfxKeys[groupBase + e];
		}
	}

	barrier();
	uint pairs = chunk / 2u;
	if (vfxSortMode == 0)
	{
		// Every stage up to the chunk.
		for (uint k = 2u; k <= chunk; k <<= 1u)
		{
			for (uint j = k >> 1u; j > 0u; j >>= 1u)
			{
				for (uint p = t; p < pairs; p += THREADS)
				{
					Step(groupBase, p, j, k);
				}

				barrier();
			}
		}
	}
	else
	{
		// The steps of stage vfxSortK below the chunk size.
		for (uint j = vfxSortJ; j > 0u; j >>= 1u)
		{
			for (uint p = t; p < pairs; p += THREADS)
			{
				Step(groupBase, p, j, vfxSortK);
			}

			barrier();
		}
	}

	for (uint e = t; e < chunk; e += THREADS)
	{
		vfxKeys[groupBase + e] = local[e];
	}
}

/////Compute
