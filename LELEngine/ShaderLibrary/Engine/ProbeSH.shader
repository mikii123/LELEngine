//Compute

#version 430

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"

// Projects each probe's 8x8 hemispherical radiance map onto second-order spherical harmonics
// (Lumen converts screen probes to SH for cheap, smooth per-pixel interpolation and integration).
// One thread per probe; the 27 coefficient values plus the valid flag (which carries the probe's change amount) are
// packed into 7 RGBA layers (ProbeIntegrate.shader unpacks them: 7 fetches per probe instead of 9).
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D ProbeRadiance;
uniform sampler2D ProbeChange; // per probe: fraction of its radiance that changed this frame (ProbeCompose.shader)
layout(binding = 0, rgba16f) uniform writeonly image2DArray ProbeSH;

#define SH_LAYERS 7

void main()
{
	ivec2 probe = ivec2(gl_GlobalInvocationID.xy);
	if (probe.x >= probeCount.x || probe.y >= probeAtlasRows) return;

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

	imageStore(ProbeSH, ivec3(probe, 0), vec4(c[0], c[1].r));
	imageStore(ProbeSH, ivec3(probe, 1), vec4(c[1].gb, c[2].rg));
	imageStore(ProbeSH, ivec3(probe, 2), vec4(c[2].b, c[3]));
	imageStore(ProbeSH, ivec3(probe, 3), vec4(c[4], c[5].r));
	imageStore(ProbeSH, ivec3(probe, 4), vec4(c[5].gb, c[6].rg));
	imageStore(ProbeSH, ivec3(probe, 5), vec4(c[6].b, c[7]));
	// Layer 6 alpha: 0 = no probe, else 1 + the probe's change amount (the integration reads it with the coefficients).
	imageStore(ProbeSH, ivec3(probe, 6), vec4(c[8], valid ? 1.0 + clamp(texelFetch(ProbeChange, probe, 0).r, 0.0, 1.0) : 0.0));
}

/////Compute
