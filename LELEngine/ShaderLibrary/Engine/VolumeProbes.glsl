// Lighting from the radiance cache probes for points that are not in the depth buffer (transparent surfaces):
// the second-order spherical harmonics of the eight probes around a point (RadianceCacheSH.shader), weighted
// trilinearly and by each probe's visibility of the point. Requires Engine/Sampling.glsl and
// Engine/RadianceCache.glsl; uniforms are set by Lighting.SetVolumeGIUniforms.
#ifndef LEL_VOLUME_PROBES_GLSL
#define LEL_VOLUME_PROBES_GLSL

uniform sampler2DArray RadianceCacheSH; // 7 RGBA layers per probe tile: 27 coefficient values + valid flag

void ReadRadianceCacheSH(int probeIndex, out vec3 c[9], out float valid)
{
	ivec2 texel = ivec2(probeIndex % rcProbesPerRow, probeIndex / rcProbesPerRow);
	vec4 l0 = texelFetch(RadianceCacheSH, ivec3(texel, 0), 0);
	vec4 l1 = texelFetch(RadianceCacheSH, ivec3(texel, 1), 0);
	vec4 l2 = texelFetch(RadianceCacheSH, ivec3(texel, 2), 0);
	vec4 l3 = texelFetch(RadianceCacheSH, ivec3(texel, 3), 0);
	vec4 l4 = texelFetch(RadianceCacheSH, ivec3(texel, 4), 0);
	vec4 l5 = texelFetch(RadianceCacheSH, ivec3(texel, 5), 0);
	vec4 l6 = texelFetch(RadianceCacheSH, ivec3(texel, 6), 0);
	c[0] = l0.rgb;
	c[1] = vec3(l0.a, l1.rg);
	c[2] = vec3(l1.ba, l2.r);
	c[3] = l2.gba;
	c[4] = l3.rgb;
	c[5] = vec3(l3.a, l4.rg);
	c[6] = vec3(l4.ba, l5.r);
	c[7] = l5.gba;
	c[8] = l6.rgb;
	valid = l6.a;
}

// Radiance around world point p as spherical harmonics, interpolated from the eight surrounding probes. A probe
// whose stored hit distance towards p is shorter than the distance to p has a wall between them and is skipped
// (the occlusion test of SampleRadianceCache); probes not traced yet are skipped. False when none remains.
bool SampleRadianceCacheSH(vec3 p, out vec3 c[9])
{
	for (int i = 0; i < 9; i++) c[i] = vec3(0.0);
	if (rcProbeSpacing <= 0.0) return false;

	vec3 g = (p - rcGridMin) / rcProbeSpacing;
	ivec3 base = clamp(ivec3(floor(g)), ivec3(0), ivec3(rcProbesPerAxis - 2));
	vec3 f = clamp(g - vec3(base), vec3(0.0), vec3(1.0));
	float occlusionMargin = rcProbeSpacing * 0.25;

	float weightSum = 0.0;
	for (int i = 0; i < 8; i++)
	{
		ivec3 corner = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
		vec3 w3 = mix(1.0 - f, f, vec3(corner));
		float w = w3.x * w3.y * w3.z;
		if (w < 1e-4) continue;

		ivec3 coord = base + corner;
		int probeIndex = RadianceCacheProbeIndex(coord);
		vec3 toPoint = p - RadianceCacheProbePosition(coord);
		float distanceToPoint = length(toPoint);
		if (distanceToPoint > 1e-3)
		{
			vec4 towards = RadianceCacheProbeSampleOct(probeIndex, DirectionToOctahedral(toPoint / distanceToPoint));
			if (towards.a < 0.5) continue;
			float depth = towards.a - 1.0;
			w *= clamp((depth - (distanceToPoint - occlusionMargin)) / occlusionMargin, 0.0, 1.0);
			if (w < 1e-4) continue;
		}

		vec3 probeSH[9];
		float valid;
		ReadRadianceCacheSH(probeIndex, probeSH, valid);
		if (valid < 0.5) continue;

		for (int k = 0; k < 9; k++) c[k] += probeSH[k] * w;
		weightSum += w;
	}

	if (weightSum <= 1e-3) return false;
	for (int k = 0; k < 9; k++) c[k] /= weightSum;
	return true;
}

#endif
