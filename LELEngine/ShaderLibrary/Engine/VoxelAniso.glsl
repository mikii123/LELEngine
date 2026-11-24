// Shared by the directional mip shaders (Engine/VoxelAnisoBase.shader, Engine/VoxelAnisoMip.shader): six views of a
// 2x2x2 group of premultiplied radiance texels, one per direction a cone travels in (slab 0..5 = +X, -X, +Y, -Y, +Z,
// -Z). Along the view axis the near texel of each pair is composited over the far one; the four pairs are averaged.

#ifndef LEL_VOXEL_ANISO_VIEWS
#define LEL_VOXEL_ANISO_VIEWS

vec4 CompositeOver(vec4 nearTexel, vec4 farTexel)
{
	return nearTexel + (1.0 - nearTexel.a) * farTexel;
}

// A cone travelling along +axis meets the lower texel (a) of a pair first.
vec4 AnisoPair(vec4 a, vec4 b, bool negative)
{
	return negative ? CompositeOver(b, a) : CompositeOver(a, b);
}

// children[x + 2 * y + 4 * z]. The four pairs along the slab's axis (i, j across it, in that order), each axis with its
// own constant indices: an index worked out per thread kept the eight texels in scratch memory (Vulkan on AMD).
vec4 AnisoView(vec4 children[8], int slab)
{
	int axis = slab >> 1;
	bool negative = (slab & 1) != 0;
	vec4 sum = vec4(0.0);
	if (axis == 0)
	{
		sum += AnisoPair(children[0], children[1], negative);
		sum += AnisoPair(children[4], children[5], negative);
		sum += AnisoPair(children[2], children[3], negative);
		sum += AnisoPair(children[6], children[7], negative);
	}
	else if (axis == 1)
	{
		sum += AnisoPair(children[0], children[2], negative);
		sum += AnisoPair(children[4], children[6], negative);
		sum += AnisoPair(children[1], children[3], negative);
		sum += AnisoPair(children[5], children[7], negative);
	}
	else
	{
		sum += AnisoPair(children[0], children[4], negative);
		sum += AnisoPair(children[2], children[6], negative);
		sum += AnisoPair(children[1], children[5], negative);
		sum += AnisoPair(children[3], children[7], negative);
	}

	return sum * 0.25;
}

#endif
