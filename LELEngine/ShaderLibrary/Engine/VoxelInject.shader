//Compute

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracingCore.glsl"

// Voxel light injection: turns the cached geometry volumes into this frame's radiance volume.
//   radiance = albedo * (direct sun light with shadows + bounce from the previous frame's volume) + emission
// Feeding the previous frame back gives multi-bounce lighting that converges over a few frames.
// Dynamic geometry overrides static geometry where both occupy a voxel.
layout(local_size_x = 8, local_size_y = 8, local_size_z = 8) in;

layout(binding = 0, rgba8) uniform readonly image3D StaticAlbedo;
layout(binding = 1, rgba8) uniform readonly image3D StaticNormal;
layout(binding = 2, r11f_g11f_b10f) uniform readonly image3D StaticEmissive;
layout(binding = 3, rgba8) uniform readonly image3D DynamicAlbedo;
layout(binding = 4, rgba8) uniform readonly image3D DynamicNormal;
layout(binding = 5, r11f_g11f_b10f) uniform readonly image3D DynamicEmissive;
layout(binding = 6, rgba16f) uniform writeonly image3D RadianceOut;

// VoxelRadiance (from the core include) is bound to the PREVIOUS frame's volume here.

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};
uniform Directional LDirectional;

uniform float giBounceStrength; // 0 disables the feedback (also used on the first frame)
uniform int giBounceCones;      // 6 = hemisphere layout, otherwise a single wide cone

void main()
{
	ivec3 coord = ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(coord, ivec3(voxelResolution)))) return;

	vec4 albedoOcc = imageLoad(DynamicAlbedo, coord);
	bool dynamic = albedoOcc.a > 0.5;
	if (!dynamic)
	{
		albedoOcc = imageLoad(StaticAlbedo, coord);
	}
	if (albedoOcc.a < 0.5)
	{
		imageStore(RadianceOut, coord, vec4(0.0));
		return;
	}

	vec3 encodedNormal = dynamic ? imageLoad(DynamicNormal, coord).xyz : imageLoad(StaticNormal, coord).xyz;
	vec3 emissive = dynamic ? imageLoad(DynamicEmissive, coord).rgb : imageLoad(StaticEmissive, coord).rgb;
	vec3 N = normalize(encodedNormal * 2.0 - 1.0);

	float voxelSize = VoxelSize();
	vec3 position = voxelGridMin + (vec3(coord) + 0.5) * voxelSize;

	// Direct light. The shadow map is sampled on the surface side of the voxel, not at its center.
	vec3 L = normalize(-LDirectional.dirDirection);
	float ndl = max(dot(N, L), 0.0);
	float shadow = ndl > 0.0 ? SampleShadow(position + N * voxelSize * 0.5, N, L) : 1.0;
	vec3 direct = LDirectional.dirColor.rgb * LDirectional.dirStrength * ndl * shadow;

	// Indirect light arriving at this voxel, from last frame's radiance volume.
	vec3 bounce = vec3(0.0);
	if (giBounceStrength > 0.0)
	{
		vec4 irradiance = giBounceCones >= 6 ? TraceDiffuseCones(position, N) : TraceWideCone(position, N);
		bounce = irradiance.rgb * giBounceStrength;
	}

	vec3 radiance = albedoOcc.rgb * (direct + bounce) + emissive;
	imageStore(RadianceOut, coord, vec4(radiance, 1.0));
}

/////Compute
