//Compute

#version 430

// Copies a sub-region of the static voxel cache into the working volume (same level).
// Replaces glCopyImageSubData, whose per-call driver overhead dominates for many small regions.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform readonly image3D Source;
layout(binding = 1, rgba16f) uniform writeonly image3D Destination;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	ivec3 coord = dstOffset + local;
	imageStore(Destination, coord, imageLoad(Source, coord));
}

/////Compute
