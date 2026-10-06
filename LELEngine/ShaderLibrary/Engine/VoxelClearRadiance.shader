//Compute

#version 430

// Zeroes a sub-region of a radiance volume (level 0). Needed because light injection only visits
// occupied blocks, so voxels that became empty must be cleared explicitly.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform writeonly image3D RadianceOut;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	imageStore(RadianceOut, dstOffset + local, vec4(0.0));
}

/////Compute
