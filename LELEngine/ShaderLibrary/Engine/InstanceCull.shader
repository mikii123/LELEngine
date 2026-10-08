//Compute

#version 430

// GPU-driven culling (LELEngine.Rendering.GpuScene). One work group per segment: a contiguous range of the instance
// table drawn by one multi-draw call. Each instance's world bounds are tested against the view frustum, then the
// draw commands are written in instance order (a work group prefix sum, so the draw order is deterministic).
// compactCommands = 1 (native, the GPU reads the draw count): visible commands packed at the segment start, their
// number in drawCounts[segment]. compactCommands = 0 (fallback): one command per instance, culled ones with
// instance count 0.

#include "Engine/Instancing.glsl"

#define GROUP_SIZE 256u

layout(local_size_x = 256) in;

struct LelDrawCommand
{
	uint count;
	uint instanceCount;
	uint firstIndex;
	int baseVertex;
	uint baseInstance;
};

struct LelCullSegment
{
	uint instanceStart;
	uint instanceCount;
	uint commandOffset;
	uint requiredFlags;
};

layout(std430, binding = 12) writeonly buffer LelDrawCommands { LelDrawCommand commands[]; };
layout(std430, binding = 13) writeonly buffer LelDrawCounts { uint drawCounts[]; };
layout(std430, binding = 14) readonly buffer LelCullSegments { LelCullSegment segments[]; };

uniform vec4 frustumPlanes[6];
uniform int compactCommands;

// Subgroup totals (native) or the per-thread scan values (fallback).
shared uint scanValues[GROUP_SIZE];
shared uint scanTotal;

// Branch-free on purpose: every invocation must reach the subgroup operations of the scan together (an early exit
// here left Intel's subgroups unconverged, and the sums covered only part of them).
bool IsVisible(uint index, uint requiredFlags)
{
	LelInstance instance = lelInstances[index];
	bool flags = (instance.draw.w & requiredFlags) == requiredFlags;

	vec3 localCenter = (instance.boundsMin.xyz + instance.boundsMax.xyz) * 0.5;
	vec3 localExtent = (instance.boundsMax.xyz - instance.boundsMin.xyz) * 0.5;
	vec3 center = (instance.model * vec4(localCenter, 1.0)).xyz;
	vec3 extent = abs(instance.model[0].xyz) * localExtent.x + abs(instance.model[1].xyz) * localExtent.y + abs(instance.model[2].xyz) * localExtent.z;
	float nearest = 1e30;
	for (int p = 0; p < 6; p++)
	{
		vec4 plane = frustumPlanes[p];
		nearest = min(nearest, dot(plane.xyz, center) + plane.w + dot(abs(plane.xyz), extent));
	}

	return flags && nearest >= 0.0;
}

// Exclusive prefix sum over the work group; total = the group's sum. Every invocation must call it.
uint GroupExclusiveSum(uint value, out uint total)
{
#ifdef LEL_SUBGROUPS
	// Only subgroup arithmetic, no subgroup built-ins: Intel's OpenGL driver reports gl_SubgroupSize 32 for shaders it
	// compiles 16 wide, and gl_SubgroupID / gl_NumSubgroups did not match the real subgroups of this shader either.
	// Each subgroup is keyed by its smallest invocation index, which writes the subgroup's total; one invocation
	// then turns the keyed totals into offsets.
	uint local = gl_LocalInvocationIndex;
	uint inclusive = subgroupInclusiveAdd(value);
	uint subgroupTotal = subgroupAdd(value);
	uint key = subgroupMin(local);
	scanValues[local] = 0u;
	barrier();
	if (local == key)
	{
		scanValues[key] = subgroupTotal;
	}

	barrier();
	if (local == 0u)
	{
		uint running = 0u;
		for (uint s = 0u; s < GROUP_SIZE; s++)
		{
			uint keyed = scanValues[s];
			scanValues[s] = running;
			running += keyed;
		}

		scanTotal = running;
	}

	barrier();
	uint result = scanValues[key] + inclusive - value;
	total = scanTotal;
	barrier();
	return result;
#else
	uint local = gl_LocalInvocationIndex;
	scanValues[local] = value;
	barrier();
	for (uint stride = 1u; stride < GROUP_SIZE; stride <<= 1u)
	{
		uint add = local >= stride ? scanValues[local - stride] : 0u;
		barrier();
		scanValues[local] += add;
		barrier();
	}

	uint inclusive = scanValues[local];
	total = scanValues[GROUP_SIZE - 1u];
	barrier();
	return inclusive - value;
#endif
}

void main()
{
	LelCullSegment segment = segments[gl_WorkGroupID.x];
	uint written = 0u;
	for (uint start = 0u; start < segment.instanceCount; start += GROUP_SIZE)
	{
		uint i = start + gl_LocalInvocationIndex;
		bool inRange = i < segment.instanceCount;
		// Out-of-range invocations test the last instance (no divergence) and discard the result.
		uint index = segment.instanceStart + min(i, segment.instanceCount - 1u);
		uint visibleValue = uint(IsVisible(index, segment.requiredFlags)) * uint(inRange);
		bool visible = visibleValue != 0u;
		uint total;
		uint slot = GroupExclusiveSum(visibleValue, total);
		if (inRange)
		{
			uvec4 draw = lelInstances[index].draw;
			LelDrawCommand command = LelDrawCommand(draw.x, visible ? 1u : 0u, draw.y, int(draw.z), index);
			if (compactCommands != 0)
			{
				if (visible)
				{
					commands[segment.commandOffset + written + slot] = command;
				}
			}
			else
			{
				commands[segment.commandOffset + i] = command;
			}
		}

		written += total;
	}

	if (gl_LocalInvocationIndex == 0u)
	{
		drawCounts[gl_WorkGroupID.x] = written;
	}
}

/////Compute
