//Compute

#version 430

// Bookkeeping of every particle system of the frame at once (LELEngine.VFX.VfxRuntime), one thread per system,
// on the counter blocks of the shared pool (layout in VfxCommon.glsl / VfxCodegen.Counter*):
//  mode 0 (frame start): last frame's survivors become this frame's input, the survivor count, the bounds, the
//          event counts and the draw counts restart at zero; a CPU-spawned system takes its spawn count off the
//          dead stack (clamped to the free slots) and gets the dispatch sizes of its initialize and update kernels;
//  mode 1 (after the parents' update): a GPU-event child spawns as many particles as its parent raised events.
// The kernels then run with indirect dispatches and append without per-particle atomics on these counters.

layout(local_size_x = 64) in;

layout(std430, binding = 4) coherent buffer VfxCounterBuffer { uint vfxCounters[]; };

// Per system: x counter block base, y capacity, z CPU spawn count (mode 0) or the absolute index of the parent's
// event counter (mode 1), w flags (bits 0-3 output count, bits 4-11 outputs counting instances, bit 12 GPU-event child).
layout(std430, binding = 11) readonly buffer VfxFrameEntryBuffer { uvec4 vfxEntries[]; };

uniform uint vfxEntryOffset;
uniform uint vfxEntryCount;
uniform int vfxMode;

#define GROUP 64u
#define CHILD 4096u

#define C_INIT_DISPATCH 0u
#define C_SPAWN 3u
#define C_UPDATE_DISPATCH 4u
#define C_ALIVE_IN 7u
#define C_ALIVE_OUT 8u
#define C_DEAD 9u
#define C_INIT_ALIVE_BASE 10u
#define C_INIT_DEAD_TOP 11u
#define C_BOUNDS 12u
#define C_ID_NEXT 18u
#define C_ID_BASE 19u
#define C_EVENTS 20u
#define C_DRAW 24u
#define C_DRAW_STRIDE 5u

void Spawn(uint b, uint spawnRequested, uint inCount)
{
	uint dead = vfxCounters[b + C_DEAD];
	uint spawn = min(spawnRequested, dead);
	vfxCounters[b + C_SPAWN] = spawn;
	vfxCounters[b + C_INIT_ALIVE_BASE] = inCount;
	vfxCounters[b + C_INIT_DEAD_TOP] = dead;
	vfxCounters[b + C_DEAD] = dead - spawn;
	vfxCounters[b + C_INIT_DISPATCH] = (spawn + GROUP - 1u) / GROUP;
	vfxCounters[b + C_INIT_DISPATCH + 1u] = 1u;
	vfxCounters[b + C_INIT_DISPATCH + 2u] = 1u;
	uint idBase = vfxCounters[b + C_ID_NEXT];
	vfxCounters[b + C_ID_BASE] = idBase;
	vfxCounters[b + C_ID_NEXT] = idBase + spawn;
	inCount += spawn;
	vfxCounters[b + C_ALIVE_IN] = inCount;
	vfxCounters[b + C_UPDATE_DISPATCH] = (inCount + GROUP - 1u) / GROUP;
	vfxCounters[b + C_UPDATE_DISPATCH + 1u] = 1u;
	vfxCounters[b + C_UPDATE_DISPATCH + 2u] = 1u;
}

void main()
{
	uint e = gl_GlobalInvocationID.x;
	if (e >= vfxEntryCount)
	{
		return;
	}

	uvec4 entry = vfxEntries[vfxEntryOffset + e];
	uint b = entry.x;
	uint capacity = entry.y;
	uint flags = entry.w;
	if (vfxMode == 0)
	{
		uint inCount = min(vfxCounters[b + C_ALIVE_OUT], capacity);
		vfxCounters[b + C_ALIVE_OUT] = 0u;
		for (uint i = 0u; i < 3u; i++)
		{
			vfxCounters[b + C_BOUNDS + i] = 0xFFFFFFFFu;
			vfxCounters[b + C_BOUNDS + 3u + i] = 0u;
		}

		vfxCounters[b + C_EVENTS] = 0u;
		vfxCounters[b + C_EVENTS + 1u] = 0u;
		uint outputs = flags & 15u;
		for (uint o = 0u; o < outputs; o++)
		{
			uint field = (flags >> (4u + o)) & 1u;
			vfxCounters[b + C_DRAW + o * C_DRAW_STRIDE + field] = 0u;
		}

		// A GPU-event child spawns in mode 1, once its parent raised this frame's events.
		Spawn(b, (flags & CHILD) != 0u ? 0u : entry.z, inCount);
	}
	else
	{
		uint events = min(vfxCounters[entry.z], capacity);
		Spawn(b, events, vfxCounters[b + C_ALIVE_IN]);
	}
}

/////Compute
