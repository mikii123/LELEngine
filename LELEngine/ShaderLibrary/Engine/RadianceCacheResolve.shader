//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Radiance cache resolve: downsamples each probe's traced tile (RadianceCacheTrace.shader) into its stored
// octahedral map and accumulates it over updates. Border texels copy the octahedral neighbour they mirror,
// which keeps the bilinear lookup seamless. One work group per probe slot; threads loop over the tile.
//
// Accumulation is sample-count based (as for the screen probes): the history weight grows with the number
// of updates a probe holds, up to maxHistorySamples, so a converged probe stops changing instead of being
// replaced by a fresh noisy estimate whenever the budget reaches it (with a reduced idle budget that would
// be a visible slow breathing of the far field). A per-probe change detector compares the summed radiance
// of the fresh estimate with the summed history: a real lighting change (emitter moved or pulsed) restarts
// the probe's accumulation, so it follows the change within a few updates.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D TraceBuffer;   // rgb radiance, a = hit distance (-1: probe invalid)
uniform int rcTraceResolution;
uniform int traceSlotsPerRow;
uniform int frameIndex;
uniform float historyFrames;     // time-constant fallback (exp(-dt / tau)) when maxHistorySamples is 0
uniform float maxHistorySamples; // cap of the per-probe update count (0 = time-constant blend)
uniform float changeThreshold;   // relative change of the summed radiance that restarts the accumulation
uniform int rcProbeCount;        // probes in the grid (probeFrames holds a last frame and an update count per probe)

uniform int sequenceOffset;
uniform int interiorStart;
uniform int interiorCount;
uniform int interiorTotal;
uniform int exteriorStart;
uniform int exteriorTotal;

layout(std430, binding = 3) readonly buffer RadianceCacheProbeList
{
	int probeList[];
};

// [probe] = frame of the probe's last update (-1 = never), [rcProbeCount + probe] = updates accumulated.
layout(std430, binding = 4) buffer RadianceCacheProbeFrames
{
	int probeFrames[];
};

// The lookup atlas (fp16, bandwidth friendly) and the accumulation copy (fp32): with history weights up to
// 31/32 an update moves a texel by a fraction of its value, which fp16 would round away (the map would
// stick to whatever it held when the accumulation started).
layout(binding = 0, rgba16f) uniform writeonly image2D RadianceCache;
layout(binding = 1, rgba32f) uniform image2D RadianceCacheAccumulator;

#define MAX_TILE 18

shared vec4 freshTile[MAX_TILE * MAX_TILE];   // rgb radiance, a = hit distance
shared vec4 historyTile[MAX_TILE * MAX_TILE]; // stored map (a = hit distance + 1, 0 = no data)
shared int freshFixed;                        // probe sums in 24.8 fixed point (luminance capped at 1e4)
shared int historyFixed;
shared int comparedCount;

int SequenceProbe(int q)
{
	return q < interiorCount
		? probeList[(interiorStart + q) % interiorTotal]
		: probeList[interiorTotal + (exteriorStart + q - interiorCount) % exteriorTotal];
}

// Border texels mirror across the octahedral seams; corners take the diagonally opposite corner.
ivec2 WrapOctahedral(ivec2 t, int resolution)
{
	int last = resolution - 1;
	bool xOut = t.x < 0 || t.x > last;
	bool yOut = t.y < 0 || t.y > last;
	if (xOut && yOut)
	{
		return ivec2(t.x < 0 ? last : 0, t.y < 0 ? last : 0);
	}
	if (xOut)
	{
		return ivec2(clamp(t.x, 0, last), last - t.y);
	}
	if (yOut)
	{
		return ivec2(last - t.x, clamp(t.y, 0, last));
	}
	return t;
}

float RcLuminance(vec3 c)
{
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

void main()
{
	int slot = int(gl_WorkGroupID.x);
	int probeIndex = SequenceProbe(sequenceOffset + slot);
	int resolution = rcProbeResolution;
	int tile = RadianceCacheTileSize();
	int factor = rcTraceResolution / resolution;
	ivec2 tileOrigin = RadianceCacheTileOrigin(probeIndex);
	ivec2 traceOrigin = ivec2(slot % traceSlotsPerRow, slot / traceSlotsPerRow) * rcTraceResolution;

	bool valid = texelFetch(TraceBuffer, traceOrigin, 0).a >= 0.0;
	int lastFrame = probeFrames[probeIndex];
	int updateCount = probeFrames[rcProbeCount + probeIndex];

	if (gl_LocalInvocationIndex == 0u)
	{
		freshFixed = 0;
		historyFixed = 0;
		comparedCount = 0;
	}
	barrier();

	// ---- 1. downsample into shared memory; sum the fresh and stored radiance of the interior texels
	for (int ty = int(gl_LocalInvocationID.y); ty < tile; ty += 8)
	{
		for (int tx = int(gl_LocalInvocationID.x); tx < tile; tx += 8)
		{
			int index = ty * MAX_TILE + tx;
			ivec2 atlasTexel = tileOrigin + ivec2(tx, ty);
			vec4 history = imageLoad(RadianceCacheAccumulator, atlasTexel);
			historyTile[index] = history;
			if (!valid)
			{
				freshTile[index] = vec4(0.0);
				continue;
			}

			ivec2 oct = WrapOctahedral(ivec2(tx, ty) - 1, resolution);
			vec3 radiance = vec3(0.0);
			float depth = 0.0;
			for (int y = 0; y < factor; y++)
			{
				for (int x = 0; x < factor; x++)
				{
					vec4 s = texelFetch(TraceBuffer, traceOrigin + oct * factor + ivec2(x, y), 0);
					radiance += s.rgb;
					depth += s.a;
				}
			}
			float count = float(factor * factor);
			radiance /= count;
			depth /= count;
			freshTile[index] = vec4(radiance, depth);

			bool interior = tx >= 1 && tx <= resolution && ty >= 1 && ty <= resolution;
			if (interior && history.a > 0.5)
			{
				atomicAdd(freshFixed, int(min(RcLuminance(radiance), 1e4) * 256.0));
				atomicAdd(historyFixed, int(min(RcLuminance(history.rgb), 1e4) * 256.0));
				atomicAdd(comparedCount, 1);
			}
		}
	}
	barrier();

	// ---- 2. history weight for the whole probe
	float historyWeight = 0.0;
	int newCount = 0;
	if (valid)
	{
		if (maxHistorySamples > 0.0)
		{
			float freshSum = float(freshFixed) / 256.0;
			float historySum = float(historyFixed) / 256.0;
			bool changed = comparedCount > 0 && abs(freshSum - historySum) > changeThreshold * (historySum + 0.05 * float(comparedCount));
			int n = changed ? 0 : min(updateCount, max(int(maxHistorySamples), 1) - 1);
			historyWeight = float(n) / float(n + 1);
			newCount = n + 1;
		}
		else
		{
			// Time since the last update: probes updated rarely blend faster (exp(-dt / tau)).
			if (lastFrame >= 0 && historyFrames > 0.0)
			{
				historyWeight = exp(-float(max(frameIndex - lastFrame, 1)) / historyFrames);
			}
			newCount = updateCount + 1;
		}
	}
	if (gl_LocalInvocationIndex == 0u)
	{
		probeFrames[probeIndex] = frameIndex;
		probeFrames[rcProbeCount + probeIndex] = newCount;
	}

	// ---- 3. accumulate and store. Alpha stores hit distance + 1 (0 = no data).
	for (int ty = int(gl_LocalInvocationID.y); ty < tile; ty += 8)
	{
		for (int tx = int(gl_LocalInvocationID.x); tx < tile; tx += 8)
		{
			int index = ty * MAX_TILE + tx;
			ivec2 atlasTexel = tileOrigin + ivec2(tx, ty);
			if (!valid)
			{
				imageStore(RadianceCache, atlasTexel, vec4(0.0));
				imageStore(RadianceCacheAccumulator, atlasTexel, vec4(0.0));
				continue;
			}

			vec4 fresh = freshTile[index];
			vec4 history = historyTile[index];
			float weight = history.a > 0.5 ? historyWeight : 0.0;
			float historyDepth = max(history.a - 1.0, 0.0);
			vec4 result = vec4(mix(fresh.rgb, history.rgb, weight), mix(fresh.a, historyDepth, weight) + 1.0);
			imageStore(RadianceCache, atlasTexel, result);
			imageStore(RadianceCacheAccumulator, atlasTexel, result);
		}
	}
}

/////Compute
