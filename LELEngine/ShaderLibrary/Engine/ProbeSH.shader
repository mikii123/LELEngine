//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"

// Projects each probe's 8x8 hemispherical radiance map onto second-order spherical harmonics
// (Lumen converts screen probes to SH for cheap, smooth per-pixel interpolation and integration).
// One thread per probe; 9 RGB coefficients written to the layers of an image array.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D ProbeRadiance;
layout(binding = 0, rgba16f) uniform writeonly image2DArray ProbeSH;

void main()
{
	ivec2 probe = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(probe, probeCount))) return;

	vec3 c[9];
	for (int i = 0; i < 9; i++) c[i] = vec3(0.0);

	vec3 position, normal;
	bool valid = ProbeAnchor(probe, position, normal);
	if (valid)
	{
		vec3 tangent, bitangent;
		TangentFrame(normal, tangent, bitangent);

		// The hemispherical octahedral map is close to equal area: each texel covers 2 pi / 64 steradians.
		const float texelSolidAngle = 2.0 * SAMPLING_PI / float(PROBE_RESOLUTION * PROBE_RESOLUTION);

		for (int y = 0; y < PROBE_RESOLUTION; y++)
		{
			for (int x = 0; x < PROBE_RESOLUTION; x++)
			{
				vec4 s = texelFetch(ProbeRadiance, probe * PROBE_RESOLUTION + ivec2(x, y), 0);
				vec2 uv = (vec2(x, y) + 0.5) / float(PROBE_RESOLUTION);
				vec3 h = HemiOctahedralToDirection(uv);
				vec3 d = normalize(tangent * h.x + bitangent * h.y + normal * h.z);

				float sh[9];
				SHBasis9(d, sh);
				for (int i = 0; i < 9; i++) c[i] += s.rgb * sh[i] * texelSolidAngle;
			}
		}
	}

	for (int i = 0; i < 9; i++)
	{
		imageStore(ProbeSH, ivec3(probe, i), vec4(c[i], valid ? 1.0 : 0.0));
	}
}

/////Compute
