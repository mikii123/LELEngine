//Compute

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracingCore.glsl"

// Voxel light injection: turns the cached geometry volumes into a radiance volume.
//   radiance = albedo * (direct sun light with shadows + bounce traced from the source volume) + emission
// VoxelGIPass runs it several times per frame: first without the bounce, then each time tracing the result of the
// pass before (one more bounce per pass, nothing carried over between frames). Where static and dynamic geometry
// share a voxel their attributes are averaged by occupancy. The radiance is stored premultiplied by the occupancy.
//
// Dispatched indirectly over the occupied 8^3 blocks listed by Engine/VoxelCompactBlocks.shader:
// one work group per block, so empty space is never visited. Blocks that became empty are
// zeroed separately by VoxelGIPass (Engine/VoxelClearRadiance.shader).
layout(local_size_x = 8, local_size_y = 8, local_size_z = 8) in;

layout(std430, binding = 0) readonly buffer BlockList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint blocks[];
};

layout(binding = 0, rgba8) uniform readonly image3D StaticAlbedo;
layout(binding = 1, rgba8) uniform readonly image3D StaticNormal;
layout(binding = 2, r11f_g11f_b10f) uniform readonly image3D StaticEmissive;
layout(binding = 3, rgba8) uniform readonly image3D DynamicAlbedo;
layout(binding = 4, rgba8) uniform readonly image3D DynamicNormal;
layout(binding = 5, r11f_g11f_b10f) uniform readonly image3D DynamicEmissive;
layout(binding = 6, rgba16f) uniform writeonly image3D RadianceOut;

// VoxelRadiance (from the core include) is bound to the volume of the previous pass here (unused in the first one).

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};
uniform Directional LDirectional;

uniform float giBounceStrength; // 0 = no bounce (the first pass of the frame)
uniform int giBounceCones;      // 6 = hemisphere layout, otherwise a single wide cone
uniform int injectShadowTaps;   // 4 = spread over the voxel's surface, otherwise 1

// The bounce per 2x2x2 voxels (Engine/VoxelBounceCells.shader): irradiance / pi of each cell, traced around the
// cell's average normal. Off (giBounceCells 0): cones from every voxel.
uniform sampler3D VoxelBounceCells;
uniform int giBounceCells;

// Sun light reaching the voxel's surface, per colour channel. Four taps a quarter voxel from its centre along the
// surface: a voxel straddling a shadow edge gets the covered fraction, so the edge in the GI is a ramp, not a step
// that crawls voxel by voxel when the sun moves.
vec3 VoxelShadow(vec3 surface, vec3 N, vec3 L, float voxelSize)
{
	if (injectShadowTaps < 4)
	{
		return SampleShadowRGB(surface, N, L);
	}

	float s = N.z >= 0.0 ? 1.0 : -1.0;
	float a = -1.0 / (s + N.z);
	float b = N.x * N.y * a;
	vec3 tangent = vec3(1.0 + s * N.x * N.x * a, s * b, -s * N.x) * (voxelSize * 0.25);
	vec3 bitangent = vec3(b, s + N.y * N.y * a, -N.y) * (voxelSize * 0.25);
	return 0.25 * (SampleShadowRGB(surface + tangent + bitangent, N, L) + SampleShadowRGB(surface + tangent - bitangent, N, L)
		+ SampleShadowRGB(surface - tangent + bitangent, N, L) + SampleShadowRGB(surface - tangent - bitangent, N, L));
}

void main()
{
	uint packedBlock = blocks[gl_WorkGroupID.x];
	ivec3 block = ivec3(int(packedBlock & 0x3FFu), int((packedBlock >> 10) & 0x3FFu), int((packedBlock >> 20) & 0x3FFu));
	ivec3 coord = block * 8 + ivec3(gl_LocalInvocationID);

	vec4 dynamicAlbedo = imageLoad(DynamicAlbedo, coord);
	vec4 staticAlbedo = imageLoad(StaticAlbedo, coord);
	float occupancy = min(1.0, dynamicAlbedo.a + staticAlbedo.a);
	if (occupancy <= 0.0)
	{
		imageStore(RadianceOut, coord, vec4(0.0));
		return;
	}

	// Both sets weighted by their occupancy (a voxel of either alone reads just that one).
	float dynamicWeight = dynamicAlbedo.a / (dynamicAlbedo.a + staticAlbedo.a);
	vec3 albedo = mix(staticAlbedo.rgb, dynamicAlbedo.rgb, dynamicWeight);
	vec3 encodedNormal = mix(imageLoad(StaticNormal, coord).xyz * 2.0 - 1.0, imageLoad(DynamicNormal, coord).xyz * 2.0 - 1.0, dynamicWeight);
	vec3 emissive = mix(imageLoad(StaticEmissive, coord).rgb, imageLoad(DynamicEmissive, coord).rgb, dynamicWeight);
	vec3 N = length(encodedNormal) > 1e-3 ? normalize(encodedNormal) : vec3(0.0, 1.0, 0.0);

	float voxelSize = VoxelSize();
	vec3 position = voxelGridMin + (vec3(coord) + 0.5) * voxelSize;

	// Direct light. The shadow map is sampled on the surface side of the voxel, not at its center.
	vec3 L = normalize(-LDirectional.dirDirection);
	float ndl = max(dot(N, L), 0.0);
	// Per channel: light through tinted transparent casters keeps their colour in the bounce.
	vec3 shadow = ndl > 0.0 && LDirectional.dirStrength > 0.0 ? VoxelShadow(position + N * voxelSize * 0.5, N, L, voxelSize) : vec3(1.0);
	vec3 direct = LDirectional.dirColor.rgb * LDirectional.dirStrength * ndl * shadow;

	// Indirect light arriving at this voxel, from the previous pass's volume.
	vec3 bounce = vec3(0.0);
	if (giBounceStrength > 0.0)
	{
		if (giBounceCells != 0)
		{
			bounce = texelFetch(VoxelBounceCells, coord >> 1, 0).rgb * giBounceStrength;
		}
		else
		{
			// The position is the voxel center, inside the geometry: start past the voxel's surface side.
			float offset = voxelSize * 1.5;
			vec4 irradiance = giBounceCones >= 6 ? TraceDiffuseConesFrom(position, N, offset) : TraceWideConeFrom(position, N, offset);
			bounce = irradiance.rgb * giBounceStrength;
		}
	}

	vec3 radiance = albedo * (direct + bounce) + emissive;
	imageStore(RadianceOut, coord, vec4(radiance * occupancy, occupancy));
}

/////Compute
