//Compute

#version 430

// Level 0 of the SH L1 gradients (the volume's mip 1, see Engine/VoxelRadiance.glsl) for occupied blocks only
// (indirect dispatch over the block list of Engine/VoxelCompactBlocks.shader, 4^3 texels per block): for every 2x2x2
// voxels the average of -(radiance, occupancy) * n per axis, with n the normal the voxel was lit with (static and
// dynamic geometry averaged by occupancy, as in Engine/VoxelInject.shader). Together with the isotropic mip 1 (the
// average of (radiance, occupancy)) this is the average of (radiance, occupancy) * (1 - n . d). Empty blocks stay zero:
// VoxelGIPass clears the level where geometry left.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(std430, binding = 0) readonly buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

layout(binding = 0, rgba16f) uniform readonly image3D Radiance; // voxels: radiance * occupancy, occupancy
layout(binding = 1, rgba16f) uniform writeonly image3D GradientX;
layout(binding = 2, rgba16f) uniform writeonly image3D GradientY;
layout(binding = 3, rgba16f) uniform writeonly image3D GradientZ;

uniform sampler3D StaticAlbedo;  // a = occupancy
uniform sampler3D StaticNormal;  // xyz = normal * 0.5 + 0.5
uniform sampler3D DynamicAlbedo;
uniform sampler3D DynamicNormal;

void main()
{
	uint packedBlock = blocks[gl_WorkGroupID.x];
	ivec3 block = ivec3(int(packedBlock & 0x3FFu), int((packedBlock >> 10) & 0x3FFu), int((packedBlock >> 20) & 0x3FFu));
	ivec3 cell = block * 4 + ivec3(gl_LocalInvocationID);
	ivec3 voxel = cell * 2;

	vec4 gx = vec4(0.0);
	vec4 gy = vec4(0.0);
	vec4 gz = vec4(0.0);
	for (int i = 0; i < 8; i++)
	{
		ivec3 coord = voxel + ivec3(i & 1, (i >> 1) & 1, i >> 2);
		vec4 value = imageLoad(Radiance, coord);
		if (value.a <= 0.0)
		{
			continue;
		}

		float staticOccupancy = texelFetch(StaticAlbedo, coord, 0).a;
		float dynamicOccupancy = texelFetch(DynamicAlbedo, coord, 0).a;
		float dynamicWeight = dynamicOccupancy / max(staticOccupancy + dynamicOccupancy, 1e-6);
		vec3 n = mix(texelFetch(StaticNormal, coord, 0).xyz * 2.0 - 1.0, texelFetch(DynamicNormal, coord, 0).xyz * 2.0 - 1.0, dynamicWeight);
		n = length(n) > 1e-3 ? normalize(n) : vec3(0.0);
		gx -= value * n.x;
		gy -= value * n.y;
		gz -= value * n.z;
	}

	imageStore(GradientX, cell, gx * 0.125);
	imageStore(GradientY, cell, gy * 0.125);
	imageStore(GradientZ, cell, gz * 0.125);
}

/////Compute
