//Compute

#version 430

#include "Engine/VoxelAniso.glsl"

// Level 0 of the directional mips (the volume's mip 1) for occupied blocks only (indirect dispatch over the block list
// of Engine/VoxelCompactBlocks.shader): an 8^3 block of voxels is a 4^3 block here, one work group each. Every
// invocation reads its 2x2x2 voxels once and writes the six views into the slabs of the atlas. Empty blocks stay zero:
// VoxelGIPass clears the level where geometry left.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform readonly image3D Source;       // voxels (radiance level 0)
layout(binding = 1, rgba16f) uniform writeonly image3D Destination; // directional atlas, level 0

layout(std430, binding = 0) readonly buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

// Texels per slab along x at this level.
uniform int slabSize;

void main()
{
	uint packedBlock = blocks[gl_WorkGroupID.x];
	ivec3 block = ivec3(int(packedBlock & 0x3FFu), int((packedBlock >> 10) & 0x3FFu), int((packedBlock >> 20) & 0x3FFu));
	ivec3 dst = block * 4 + ivec3(gl_LocalInvocationID);
	ivec3 src = dst * 2;

	vec4 children[8];
	for (int i = 0; i < 8; i++)
	{
		children[i] = imageLoad(Source, src + ivec3(i & 1, (i >> 1) & 1, i >> 2));
	}

	for (int slab = 0; slab < 6; slab++)
	{
		imageStore(Destination, ivec3(dst.x + slab * slabSize, dst.y, dst.z), AnisoView(children, slab));
	}
}

/////Compute
