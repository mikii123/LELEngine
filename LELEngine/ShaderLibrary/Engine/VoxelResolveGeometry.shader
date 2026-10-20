//Compute

#version 430

// Turns the coverage voxelization's accumulators (Engine/VoxelizeCoverage.shader) into a geometry volume set for
// a sub-region: weighted averages of albedo, normal and emission, and the occupancy from the summed weight. A
// surface splats its weight over the two voxels nearest along its axis, so the occupancy is twice the weight
// (clamped): wherever the surface sits between two voxel centres, the nearer voxel is opaque and the pair as a whole
// stops a cone as one opaque layer would.
layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba8) uniform writeonly image3D AlbedoOut;            // rgb albedo, a occupancy
layout(binding = 1, rgba8) uniform writeonly image3D NormalOut;            // xyz normal * 0.5 + 0.5, a coherence
layout(binding = 2, r11f_g11f_b10f) uniform writeonly image3D EmissiveOut; // rgb emitted radiance

uniform usampler3D AccAlbedoRG;
uniform usampler3D AccAlbedoBW;
uniform usampler3D AccNormalXY;
uniform usampler3D AccNormalZ;
uniform usampler3D AccEmission;

uniform ivec3 dstOffset;
uniform ivec3 dstSize;

const float FixedPoint = 256.0;

vec3 DecodeRGB9E5(uint v)
{
	return vec3(uvec3(v, v >> 9, v >> 18) & 511u) * exp2(float(int(v >> 27) - 24));
}

void main()
{
	ivec3 local = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(local, dstSize))) return;

	ivec3 coord = dstOffset + local;
	uint albedoRG = texelFetch(AccAlbedoRG, coord, 0).r;
	uint albedoBW = texelFetch(AccAlbedoBW, coord, 0).r;
	uint weightSum = albedoBW >> 16;
	if (weightSum == 0u)
	{
		imageStore(AlbedoOut, coord, vec4(0.0));
		imageStore(NormalOut, coord, vec4(0.0));
		imageStore(EmissiveOut, coord, vec4(0.0));
		return;
	}

	float weight = float(weightSum) / FixedPoint;
	float inverse = 1.0 / float(weightSum);
	vec3 albedo = vec3(float(albedoRG & 0xFFFFu), float(albedoRG >> 16), float(albedoBW & 0xFFFFu)) * inverse;

	uint normalXY = texelFetch(AccNormalXY, coord, 0).r;
	uint normalZ = texelFetch(AccNormalZ, coord, 0).r;
	vec3 normal = vec3(float(normalXY & 0xFFFFu), float(normalXY >> 16), float(normalZ)) * inverse * 2.0 - 1.0;
	// Opposite faces in one voxel (a wall thinner than a voxel) cancel: the length says how much the normals agree.
	float coherence = length(normal);
	normal = coherence > 1e-3 ? normal / coherence : vec3(0.0, 1.0, 0.0);

	vec3 emission = DecodeRGB9E5(texelFetch(AccEmission, coord, 0).r) / weight;

	imageStore(AlbedoOut, coord, vec4(albedo, clamp(weight * 2.0, 0.0, 1.0)));
	imageStore(NormalOut, coord, vec4(normal * 0.5 + 0.5, clamp(coherence, 0.0, 1.0)));
	imageStore(EmissiveOut, coord, vec4(emission, 0.0));
}

/////Compute
