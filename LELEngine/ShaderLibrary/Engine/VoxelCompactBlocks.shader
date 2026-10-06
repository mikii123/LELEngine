//Compute

#version 430

// Builds the list of occupied 8^3 voxel blocks (static or dynamic geometry present) and the
// indirect dispatch arguments for Engine/VoxelInject.shader, so empty space costs nothing there.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

uniform sampler3D StaticBlocks;   // r > 0.5 where a block holds static geometry
uniform sampler3D DynamicBlocks;  // r > 0.5 where a block holds dynamic geometry
uniform int blockResolution;

// Header doubles as the glDispatchComputeIndirect argument block; numGroupsY/Z stay 1.
layout(std430, binding = 0) buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

void main()
{
	ivec3 block = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(block, ivec3(blockResolution)))) return;

	bool occupied = texelFetch(StaticBlocks, block, 0).r > 0.5 || texelFetch(DynamicBlocks, block, 0).r > 0.5;
	if (!occupied) return;

	uint index = atomicAdd(numGroupsX, 1u);
	blocks[index] = uint(block.x) | (uint(block.y) << 10) | (uint(block.z) << 20);
}

/////Compute
