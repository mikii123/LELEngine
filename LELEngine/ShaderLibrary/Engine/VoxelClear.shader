//Compute

#version 430

// Zeroes the voxel radiance volume before re-voxelization (glClearTexImage needs GL 4.4).
layout(local_size_x = 8, local_size_y = 8, local_size_z = 8) in;
layout(binding = 0, rgba16f) uniform writeonly image3D VoxelOutput;

void main()
{
	imageStore(VoxelOutput, ivec3(gl_GlobalInvocationID), vec4(0.0));
}

/////Compute
