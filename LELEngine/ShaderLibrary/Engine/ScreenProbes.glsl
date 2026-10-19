// Screen probe layout shared by the gather passes (Engine/ProbeAnchors, ProbePlacement, ProbeTrace,
// ProbeFilter, ProbeSH, ProbeIntegrate). Uniform probes sit on a grid of probeSpacing pixels; probe (x, y)
// is anchored at the pixel probe * probeSpacing + probeJitter and stores an 8x8 hemispherical octahedral
// map of radiance. Rows probeCount.y and up of the probe atlas hold adaptive probes (Lumen's adaptive
// placement): extra probes placed each frame where the uniform grid cannot be interpolated, addressed by
// index (AdaptiveProbeTexel) and listed per screen tile (Engine/AdaptiveProbes.glsl).
// ProbeAnchors.shader resolves every uniform probe's world position and normal once per frame into two
// small textures that the other passes read; ProbePlacement.shader appends the adaptive ones.

#define PROBE_RESOLUTION 8

uniform sampler2D SceneDepth;
uniform sampler2D NormalRoughness;
uniform sampler2D ProbeAnchorPosition; // xyz world position, w = 1 when the probe lies on geometry
uniform sampler2D ProbeAnchorNormal;
uniform mat4 invViewProjection;
uniform ivec2 screenSize;
uniform ivec2 probeCount;      // uniform probes per axis
uniform int probeAtlasRows;    // uniform rows plus adaptive rows
uniform int probeSpacing;
uniform ivec2 probeJitter;
uniform vec2 directionJitter;  // this frame's low-discrepancy sub-texel offset, shared base of every probe's jitter

ivec2 ProbeAnchorPixel(ivec2 probe)
{
	return clamp(probe * probeSpacing + probeJitter, ivec2(0), screenSize - 1);
}

// Sub-texel offset of this probe's ray directions this frame. One global value would make every probe's
// estimation error identical and the whole screen breathe together; a 3x3 dither (so the filter's
// neighbours sample different parts of their texels) plus a per-probe hash makes the error spatially white,
// which the spatial filter, the bilinear probe interpolation and the temporal blends then average away.
// Every probe still walks the frame sequence, so each converges over time like before.
vec2 ProbeDirectionJitter(ivec2 probe)
{
	uint h = uint(probe.x) * 73856093u ^ uint(probe.y) * 19349663u;
	h = (h ^ (h >> 13u)) * 0x5bd1e995u;
	h ^= h >> 15u;
	vec2 hashOffset = vec2(float(h & 0xFFFFu), float((h >> 16u) & 0xFFFFu)) / (65536.0 * 3.0);
	vec2 dither = vec2(float(probe.x % 3), float(probe.y % 3)) / 3.0;
	return fract(directionJitter + dither + hashOffset);
}

// Atlas texel of the adaptive probe with the given index.
ivec2 AdaptiveProbeTexel(uint index)
{
	return ivec2(int(index) % probeCount.x, probeCount.y + int(index) / probeCount.x);
}

uint PackPixel(ivec2 pixel)
{
	return uint(pixel.x) | (uint(pixel.y) << 16u);
}

ivec2 UnpackPixel(uint packedPixel)
{
	return ivec2(int(packedPixel & 0xFFFFu), int(packedPixel >> 16u));
}

vec3 ReconstructPosition(ivec2 pixel, float depth)
{
	vec2 uv = (vec2(pixel) + 0.5) / vec2(screenSize);
	vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec4 world = invViewProjection * clip;
	return world.xyz / world.w;
}

// World position and normal of a probe from the anchor textures. False when the probe falls on the sky.
bool ProbeAnchor(ivec2 probe, out vec3 position, out vec3 normal)
{
	vec4 p = texelFetch(ProbeAnchorPosition, probe, 0);
	position = p.xyz;
	normal = texelFetch(ProbeAnchorNormal, probe, 0).xyz;
	return p.w > 0.5;
}

// ---- Per-probe history / selection record (Engine/ProbeImportance.shader -> ProbeTrace / ProbeCompose)
// Per probe, a uvec4: x bits 0-15 = the 2x2 blocks of directions merged into one ray, x bit 16 = the probe's
// surface moved since the previous frame; y, z = the 64-bit mask of the directions refined into four rays; w = the
// previous frame's probe cell this probe reprojects to (bits 6-17 x, 18-29 y), a flag saying the probe's ray table
// is valid (bit 30) and a flag saying the reprojection is valid (bit 31). Without a valid table every direction is
// traced with one ray; without a valid reprojection no history is used at all.

bool ReprojectionValid(uvec4 selection)
{
	return (selection.w & 0x80000000u) != 0u;
}

bool SelectionValid(uvec4 selection)
{
	return (selection.w & 0xC0000000u) == 0xC0000000u;
}

bool SelectionMoving(uvec4 selection)
{
	return (selection.x & 0x10000u) != 0u;
}

ivec2 SelectionPreviousProbe(uvec4 selection)
{
	return ivec2(int((selection.w >> 6u) & 0xFFFu), int((selection.w >> 18u) & 0xFFFu));
}

uvec4 PackSelection(ivec2 previousProbe, bool tableValid, bool moving, uint mergedMask, uvec2 refinedMask)
{
	uvec4 s = uvec4((mergedMask & 0xFFFFu) | (moving ? 0x10000u : 0u), refinedMask.x, refinedMask.y, 0u);
	s.w = (uint(previousProbe.x) << 6u) | (uint(previousProbe.y) << 18u) | 0x80000000u;
	if (tableValid) s.w |= 0x40000000u;
	return s;
}

uint SelectionMergedMask(uvec4 selection)
{
	return selection.x & 0xFFFFu;
}

bool SelectionRefined(uvec4 selection, int texel)
{
	return ((texel < 32 ? selection.y >> uint(texel) : selection.z >> uint(texel - 32)) & 1u) != 0u;
}

// Number of set bits of the 64-bit mask below `texel`.
int RefinedBefore(uvec4 selection, int texel)
{
	if (texel < 32) return bitCount(selection.y & ((1u << uint(texel)) - 1u));
	return bitCount(selection.y) + (texel == 32 ? 0 : bitCount(selection.z & ((1u << uint(texel - 32)) - 1u)));
}

// ---- Ray table (Engine/ProbeImportance.shader -> ProbeTrace / ProbeCompose): structured importance sampling with
// full coverage, as Lumen does it. Every ray of a probe (64, one per atlas texel of the probe's tile in the ray
// table, same layout as the trace target) has an entry: the octahedral texel it samples (bits 0-5) and how (bits
// 6-7): the whole texel; the 2x2 block of texels starting at it (one ray for four dim texels); or one quadrant of
// it (bits 8-9; four rays for a bright texel). Every direction gets at least a block ray each frame.

#define RAY_WHOLE_TEXEL 0u
#define RAY_BLOCK 1u
#define RAY_QUADRANT 2u

uint PackRay(int texel, uint mode, int quadrant)
{
	return uint(texel) | (mode << 6u) | (uint(quadrant) << 8u);
}

int RayTexel(uint ray)
{
	return int(ray & 63u);
}

uint RayMode(uint ray)
{
	return (ray >> 6u) & 3u;
}

int RayQuadrant(uint ray)
{
	return int((ray >> 8u) & 3u);
}

// The 2x2 block (0..15) an octahedral texel (0..63) belongs to, and the first texel of a block.
int TexelBlock(int texel)
{
	return (texel % PROBE_RESOLUTION) / 2 + 4 * ((texel / PROBE_RESOLUTION) / 2);
}

int BlockFirstTexel(int block)
{
	return (block / 4) * 2 * PROBE_RESOLUTION + (block % 4) * 2;
}

// ---- Ray results: rgb radiance, a = hit distance (>= 0), -1 sky / far field, -2 probe invalid; a hit on an object
// that moved since the previous frame is stored as -(distance + 10), so the history of that direction is kept short.

#define RAY_MOVING_OFFSET 10.0

float EncodeMovingHit(float distance)
{
	return -(distance + RAY_MOVING_OFFSET);
}

bool RayHitMoving(float a)
{
	return a <= -(RAY_MOVING_OFFSET - 0.5);
}

// The hit distance of a ray result (>= 0), or the miss / invalid codes as they are.
float RayHitDistance(float a)
{
	return RayHitMoving(a) ? -a - RAY_MOVING_OFFSET : a;
}

// The rays a direction gets, from the table's layout (see ProbeImportance.shader): merged blocks first, in block
// order; then one ray per direction outside them, in direction order; then three more per refined direction, in
// direction order. `first` is the block's ray or the direction's own ray; a refined direction has three more rays
// from `extra` on (count 4).
void TexelRays(uvec4 selection, int texel, out int first, out int extra, out int count, out bool blockRay)
{
	uint merged = SelectionMergedMask(selection);
	int block = TexelBlock(texel);
	extra = 0;
	count = 1;
	if ((merged & (1u << uint(block))) != 0u)
	{
		first = bitCount(merged & ((1u << uint(block)) - 1u));
		blockRay = true;
		return;
	}

	// Directions before this one that belong to merged blocks.
	int mergedBlocks = bitCount(merged);
	int mergedBefore = 0;
	uint remaining = merged;
	while (remaining != 0u)
	{
		int b = findLSB(remaining);
		remaining &= remaining - 1u;
		int t0 = BlockFirstTexel(b);
		mergedBefore += int(t0 < texel) + int(t0 + 1 < texel) + int(t0 + PROBE_RESOLUTION < texel) + int(t0 + PROBE_RESOLUTION + 1 < texel);
	}

	first = mergedBlocks + texel - mergedBefore;
	blockRay = false;
	if (SelectionRefined(selection, texel))
	{
		extra = mergedBlocks + (64 - 4 * mergedBlocks) + 3 * RefinedBefore(selection, texel);
		count = 4;
	}
}
