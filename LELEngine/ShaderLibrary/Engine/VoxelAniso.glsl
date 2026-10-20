// Shared by the directional mip shaders (Engine/VoxelAnisoBase.shader, Engine/VoxelAnisoMip.shader): six views of a
// 2x2x2 group of premultiplied radiance texels, one per direction a cone travels in (slab 0..5 = +X, -X, +Y, -Y, +Z,
// -Z). Along the view axis the near texel of each pair is composited over the far one; the four pairs are averaged.

#ifndef LEL_VOXEL_ANISO_VIEWS
#define LEL_VOXEL_ANISO_VIEWS

vec4 CompositeOver(vec4 nearTexel, vec4 farTexel)
{
	return nearTexel + (1.0 - nearTexel.a) * farTexel;
}

// children[x + 2 * y + 4 * z]
vec4 AnisoView(vec4 children[8], int slab)
{
	int axis = slab >> 1;
	bool negative = (slab & 1) != 0;
	vec4 sum = vec4(0.0);
	for (int i = 0; i < 2; i++)
	{
		for (int j = 0; j < 2; j++)
		{
			// The two texels along the axis for the pair at (i, j) across it.
			ivec3 lo = axis == 0 ? ivec3(0, i, j) : axis == 1 ? ivec3(i, 0, j) : ivec3(i, j, 0);
			ivec3 hi = lo;
			hi[axis] = 1;
			vec4 a = children[lo.x + 2 * lo.y + 4 * lo.z];
			vec4 b = children[hi.x + 2 * hi.y + 4 * hi.z];
			// A cone travelling along +axis meets the lower texel first.
			sum += negative ? CompositeOver(b, a) : CompositeOver(a, b);
		}
	}

	return sum * 0.25;
}

#endif
