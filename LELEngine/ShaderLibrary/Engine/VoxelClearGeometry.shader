//Compute

#version 430

// Zeroes a sub-region of a geometry volume set (albedo/occupancy, normal, emissive) and the
// 8^3 block occupancy flags covering it. Used for full clears on cache rebuild and for the
// regions dynamic objects occupied last frame (all dynamic objects are re-voxelized afterwards,
// which sets the flags again).
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba8) uniform writeonly image3D AlbedoOut;
layout(binding = 1, rgba8) uniform writeonly image3D NormalOut;
layout(binding = 2, r11f_g11f_b10f) uniform writeonly image3D EmissiveOut;
layout(binding = 3, r8) uniform writeonly image3D BlocksOut;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	ivec3 coord = dstOffset + local;
	imageStore(AlbedoOut, coord, vec4(0.0));
	imageStore(NormalOut, coord, vec4(0.0));
	imageStore(EmissiveOut, coord, vec4(0.0));
	imageStore(BlocksOut, coord >> 3, vec4(0.0));
}

/////Compute
