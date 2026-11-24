// What the traced shadows' and reflections' histories keep per pixel about its surface, in one 32-bit word (an R32UI
// image, a quarter of the RGBA16F it took): the linear view depth as a half float (bits 0..15; 0: no surface), the
// normal's octahedral coordinates in normalBits bits each, and a small count above them (the frames a shadow history
// holds). Include after Engine/Sampling.glsl.
#ifndef LEL_HISTORY_META_GLSL
#define LEL_HISTORY_META_GLSL

uint PackHistoryMeta(float viewDepth, vec3 normal, uint normalBits, uint extra)
{
	uint depthBits = packHalf2x16(vec2(viewDepth, 0.0)) & 0xFFFFu;
	float scale = float((1u << normalBits) - 1u);
	uvec2 octahedral = uvec2(round(clamp(DirectionToOctahedral(normal), 0.0, 1.0) * scale));
	// No count with 8 bits an axis (a shift by the word's 32 bits is undefined).
	uint count = normalBits < 8u ? extra << (16u + 2u * normalBits) : 0u;
	return depthBits | (octahedral.x << 16) | (octahedral.y << (16u + normalBits)) | count;
}

float HistoryMetaDepth(uint meta)
{
	return unpackHalf2x16(meta & 0xFFFFu).x;
}

vec3 HistoryMetaNormal(uint meta, uint normalBits)
{
	uint mask = (1u << normalBits) - 1u;
	float scale = float(mask);
	return OctahedralToDirection(vec2(float((meta >> 16) & mask), float((meta >> (16u + normalBits)) & mask)) / scale);
}

uint HistoryMetaExtra(uint meta, uint normalBits)
{
	return normalBits < 8u ? meta >> (16u + 2u * normalBits) : 0u;
}

#endif
