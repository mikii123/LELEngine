//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Radiance cache resolve: downsamples each probe's traced tile (RadianceCacheTrace.shader) into its stored
// octahedral map and accumulates it exponentially. Border texels copy the octahedral neighbour they mirror,
// which keeps the bilinear lookup seamless. One work group per probe slot; threads loop over the tile.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D TraceBuffer;   // rgb radiance, a = hit distance (-1: probe invalid)
uniform int rcTraceResolution;
uniform int traceSlotsPerRow;
uniform int frameIndex;
uniform float historyFrames;     // accumulation time constant in frames; 0 restarts every update

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

// Frame of each probe's last update (-1 = never): probes updated rarely blend faster so every probe
// converges in the same wall-clock time regardless of how often the budget reaches it.
layout(std430, binding = 4) buffer RadianceCacheProbeFrames
{
	int probeLastFrame[];
};

layout(binding = 0, rgba16f) uniform image2D RadianceCache;

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

	// History weight from the time since this probe's last update: exp(-dt / tau).
	int lastFrame = probeLastFrame[probeIndex];
	float historyWeight = 0.0;
	if (lastFrame >= 0 && historyFrames > 0.0)
	{
		historyWeight = exp(-float(max(frameIndex - lastFrame, 1)) / historyFrames);
	}
	barrier();
	if (gl_LocalInvocationIndex == 0u)
	{
		probeLastFrame[probeIndex] = frameIndex;
	}

	for (int ty = int(gl_LocalInvocationID.y); ty < tile; ty += 8)
	{
		for (int tx = int(gl_LocalInvocationID.x); tx < tile; tx += 8)
		{
			ivec2 atlasTexel = tileOrigin + ivec2(tx, ty);
			if (!valid)
			{
				imageStore(RadianceCache, atlasTexel, vec4(0.0));
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

			// Alpha stores hit distance + 1 (0 = no data); radiance and depth accumulate over updates.
			vec4 history = imageLoad(RadianceCache, atlasTexel);
			float weight = history.a > 0.5 ? historyWeight : 0.0;
			float historyDepth = max(history.a - 1.0, 0.0);
			imageStore(RadianceCache, atlasTexel, vec4(mix(radiance, history.rgb, weight), mix(depth, historyDepth, weight) + 1.0));
		}
	}
}

/////Compute
