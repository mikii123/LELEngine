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
// Per probe, a uvec4 packs the 16 most important octahedral texel indices (6 bits each: five in x, five in
// y, five in z, one in w bits 0-5), the previous frame's probe cell this probe reprojects to (w bits 6-17 x,
// 18-29 y), a flag saying the importance selection is meaningful (w bit 30) and a flag saying the
// reprojection is valid (w bit 31). Without a valid selection every direction is traced with one ray;
// without a valid reprojection no history is used at all.

#define IMPORTANT_DIRECTIONS 16

bool ReprojectionValid(uvec4 selection)
{
	return (selection.w & 0x80000000u) != 0u;
}

bool SelectionValid(uvec4 selection)
{
	return (selection.w & 0xC0000000u) == 0xC0000000u;
}

ivec2 SelectionPreviousProbe(uvec4 selection)
{
	return ivec2(int((selection.w >> 6u) & 0xFFFu), int((selection.w >> 18u) & 0xFFFu));
}

int SelectedTexelIndex(uvec4 selection, int slot)
{
	uint word = slot < 5 ? selection.x : (slot < 10 ? selection.y : (slot < 15 ? selection.z : selection.w));
	int shift = (slot < 15 ? slot % 5 : 0) * 6;
	return int((word >> uint(shift)) & 0x3Fu);
}

uvec4 PackSelection(int indices[IMPORTANT_DIRECTIONS], ivec2 previousProbe, bool selectionValid)
{
	uvec4 s = uvec4(0u);
	for (int k = 0; k < 5; k++) s.x |= uint(indices[k]) << uint(k * 6);
	for (int k = 5; k < 10; k++) s.y |= uint(indices[k]) << uint((k - 5) * 6);
	for (int k = 10; k < 15; k++) s.z |= uint(indices[k]) << uint((k - 10) * 6);
	s.w = uint(indices[15]) | (uint(previousProbe.x) << 6u) | (uint(previousProbe.y) << 18u) | 0x80000000u;
	if (selectionValid) s.w |= 0x40000000u;
	return s;
}
