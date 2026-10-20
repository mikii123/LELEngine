//Compute

#version 430

#include "Engine/VoxelConeTracingCore.glsl"

// The bounce of a light injection pass per 2x2x2 voxels (a cell) instead of per voxel: the hemisphere cones of the
// materials traced once per cell, around the occupancy-weighted average normal of its voxels, from the weighted
// average of their centres (where the surface is). Engine/VoxelInject.shader lights every voxel of the cell with the
// result. On flat surfaces this is what each voxel would trace; at edges the cell's voxels share the averaged normal.
// The cone set of each cell is turned and its samples moved by the cell's hash when giConeJitter is set.
// A cell without geometry stores zero and traces nothing.
//
// Dispatched indirectly over the occupied 8^3 blocks (4^3 cells each) listed by Engine/VoxelCompactBlocks.shader.
// VoxelRadiance (from the core include) is the volume of the previous pass.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;

layout(std430, binding = 0) readonly buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

layout(binding = 0, rgba16f) uniform writeonly image3D BounceOut; // rgb irradiance / pi, a 1 where traced

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

	vec3 normalSum = vec3(0.0);
	vec3 positionSum = vec3(0.0);
	float occupancy = 0.0;
	for (int i = 0; i < 8; i++)
	{
		ivec3 offset = ivec3(i & 1, (i >> 1) & 1, i >> 2);
		ivec3 coord = voxel + offset;
		float staticOccupancy = texelFetch(StaticAlbedo, coord, 0).a;
		float dynamicOccupancy = texelFetch(DynamicAlbedo, coord, 0).a;
		float w = staticOccupancy + dynamicOccupancy;
		if (w <= 0.0)
		{
			continue;
		}

		normalSum += staticOccupancy * (texelFetch(StaticNormal, coord, 0).xyz * 2.0 - 1.0) + dynamicOccupancy * (texelFetch(DynamicNormal, coord, 0).xyz * 2.0 - 1.0);
		positionSum += w * (vec3(offset) + 0.5);
		occupancy += w;
	}

	if (occupancy <= 0.0)
	{
		imageStore(BounceOut, cell, vec4(0.0));
		return;
	}

	vec3 N = length(normalSum) > 1e-3 ? normalize(normalSum) : vec3(0.0, 1.0, 0.0);
	float voxelSize = VoxelSize();
	vec3 surface = voxelGridMin + (vec3(voxel) + positionSum / occupancy) * voxelSize;
	// As from a voxel: start past the surface's side of the voxels. With giConeJitter every cell turns its cone set
	// and moves its samples by its own hash: neighbouring cells see an emitter through different cones, and the mips
	// average the prints of the six cones away.
	vec2 jitter = giConeJitter != 0 ? ConeJitterAt(cell) : vec2(0.0);
	vec4 irradiance = TraceDiffuseConesJittered(surface, N, voxelSize * 1.5, jitter);
	imageStore(BounceOut, cell, vec4(irradiance.rgb, 1.0));
}

/////Compute
