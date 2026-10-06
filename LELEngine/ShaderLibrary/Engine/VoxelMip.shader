//Compute

#version 430

// One mip level step (2x2x2 box filter) for a sub-region of the voxel volume.
// Source is level L-1, Destination is level L of the same texture.
// Dispatched over the destination region only, so dynamic objects re-filter just their footprint.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform readonly image3D Source;
layout(binding = 1, rgba16f) uniform writeonly image3D Destination;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	ivec3 dst = dstOffset + local;
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
