//Compute

#version 430

#include "Engine/VoxelAniso.glsl"

// One coarser level of the directional mips: every slab is filtered from the same slab one level finer, compositing
// along the slab's own direction. Dispatched over the whole level (six slabs side by side along x).
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform readonly image3D Source;       // level L - 1
layout(binding = 1, rgba16f) uniform writeonly image3D Destination; // level L

// Texels per slab along x at the destination level (the source has twice as many).
uniform int slabSize;

void main()
{
	ivec3 dst = ivec3(gl_GlobalInvocationID);
	if (dst.x >= slabSize * 6 || dst.y >= slabSize || dst.z >= slabSize) return;

	int slab = dst.x / slabSize;
	ivec3 local = ivec3(dst.x - slab * slabSize, dst.y, dst.z);
	ivec3 src = ivec3(slab * slabSize * 2, 0, 0) + local * 2;

	vec4 children[8];
	for (int i = 0; i < 8; i++)
	{
		children[i] = imageLoad(Source, src + ivec3(i & 1, (i >> 1) & 1, i >> 2));
	}

	imageStore(Destination, dst, AnisoView(children, slab));
}

/////Compute
