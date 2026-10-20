//Compute

#version 430

// Zeroes a sub-region of the coverage voxelization's accumulators (Engine/VoxelizeCoverage.shader) before a
// voxelization writes into it.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, r32ui) uniform writeonly uimage3D AccAlbedoRG;
layout(binding = 1, r32ui) uniform writeonly uimage3D AccAlbedoBW;
layout(binding = 2, r32ui) uniform writeonly uimage3D AccNormalXY;
layout(binding = 3, r32ui) uniform writeonly uimage3D AccNormalZ;
layout(binding = 4, r32ui) uniform writeonly uimage3D AccEmission;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	ivec3 coord = dstOffset + local;
	imageStore(AccAlbedoRG, coord, uvec4(0u));
	imageStore(AccAlbedoBW, coord, uvec4(0u));
	imageStore(AccNormalXY, coord, uvec4(0u));
	imageStore(AccNormalZ, coord, uvec4(0u));
	imageStore(AccEmission, coord, uvec4(0u));
}

/////Compute
