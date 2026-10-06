//Compute

#version 430

// Mip level 1 of the radiance volume for occupied blocks only (indirect dispatch over the block
// list built by Engine/VoxelCompactBlocks.shader). An 8^3 block at level 0 is a 4^3 block at level 1,
// so one work group filters exactly one block. Empty blocks stay zero because VoxelGIPass clears
// levels 0 and 1 of regions that became empty.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform readonly image3D Source;       // level 0
layout(binding = 1, rgba16f) uniform writeonly image3D Destination; // level 1

layout(std430, binding = 0) readonly buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

void main()
{
	uint packedBlock = blocks[gl_WorkGroupID.x];
	ivec3 block = ivec3(int(packedBlock & 0x3FFu), int((packedBlock >> 10) & 0x3FFu), int((packedBlock >> 20) & 0x3FFu));
	ivec3 dst = block * 4 + ivec3(gl_LocalInvocationID);
	ivec3 src = dst * 2;

	vec4 sum = vec4(0.0);
	for (int z = 0; z < 2; z++)
	{
		for (int y = 0; y < 2; y++)
		{
			for (int x = 0; x < 2; x++)
			{
				sum += imageLoad(Source, src + ivec3(x, y, z));
			}
		}
	}

	imageStore(Destination, dst, sum * 0.125);
}

/////Compute
