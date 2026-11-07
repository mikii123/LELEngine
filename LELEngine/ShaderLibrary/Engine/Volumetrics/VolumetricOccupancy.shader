//Compute

#version 430

// Where a volumetric object's cloud is (LELEngine.Volumetrics.VolumetricData): per block of 4 x 4 x 4 voxels the densest
// voxel of the block and of the one voxel around it (trilinear reads near the block's faces reach into it). The march
// reads it to jump over empty blocks in one step instead of sampling through them.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, r8) uniform writeonly image3D Occupancy;

uniform sampler3D Density;      // level 0 of the density
uniform ivec3 volResolution;    // voxels of the density
uniform ivec3 volBlocks;        // blocks of the occupancy

void main()
{
	ivec3 block = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(block, volBlocks)))
	{
		return;
	}

	ivec3 first = max(block * 4 - 1, ivec3(0));
	ivec3 last = min(block * 4 + 4, volResolution - 1);
	float densest = 0.0;
	for (int z = first.z; z <= last.z; z++)
	{
		for (int y = first.y; y <= last.y; y++)
		{
			for (int x = first.x; x <= last.x; x++)
			{
				densest = max(densest, texelFetch(Density, ivec3(x, y, z), 0).r);
			}
		}
	}

	imageStore(Occupancy, block, vec4(densest, 0.0, 0.0, 0.0));
}

/////Compute
