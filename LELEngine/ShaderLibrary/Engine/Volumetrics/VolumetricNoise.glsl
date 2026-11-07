// Procedural noise of the volumetric objects (LELEngine.Volumetrics): gradient (Perlin) noise and cellular (Worley)
// noise on an integer lattice hashed with PCG3D, their fractal sums, and the noise types a volume chooses from. With a
// period (a power of two, in lattice cells) the lattice wraps, so a texture made of it tiles without a seam (the shared
// detail texture); period 0 never wraps.
#ifndef LEL_VOLUMETRIC_NOISE_GLSL
#define LEL_VOLUMETRIC_NOISE_GLSL

// The noise types (VolumeNoise): fractal gradient noise, its absolute value (puffs), its ridges (filaments), fractal
// cellular noise (bubbles), and gradient noise carved by cellular billows (cumulus clouds, the default).
#define VOL_NOISE_PERLIN 0
#define VOL_NOISE_BILLOW 1
#define VOL_NOISE_RIDGED 2
#define VOL_NOISE_WORLEY 3
#define VOL_NOISE_PERLIN_WORLEY 4

uvec3 VolPcg3d(uvec3 v)
{
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	return v;
}

vec3 VolHash01(ivec3 c)
{
	return vec3(VolPcg3d(uvec3(c))) * (1.0 / 4294967295.0);
}

// A lattice cell wrapped to a tile of period cells (a power of two; 0: no wrapping). The mask works on negative cells too.
ivec3 VolWrap(ivec3 c, int period)
{
	return period > 0 ? (c & (period - 1)) : c;
}

float VolGradientNoise(vec3 p, int period)
{
	vec3 i = floor(p);
	vec3 f = p - i;
	vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
	ivec3 c = ivec3(i);

	float n000 = dot(VolHash01(VolWrap(c + ivec3(0, 0, 0), period)) * 2.0 - 1.0, f - vec3(0.0, 0.0, 0.0));
	float n100 = dot(VolHash01(VolWrap(c + ivec3(1, 0, 0), period)) * 2.0 - 1.0, f - vec3(1.0, 0.0, 0.0));
	float n010 = dot(VolHash01(VolWrap(c + ivec3(0, 1, 0), period)) * 2.0 - 1.0, f - vec3(0.0, 1.0, 0.0));
	float n110 = dot(VolHash01(VolWrap(c + ivec3(1, 1, 0), period)) * 2.0 - 1.0, f - vec3(1.0, 1.0, 0.0));
	float n001 = dot(VolHash01(VolWrap(c + ivec3(0, 0, 1), period)) * 2.0 - 1.0, f - vec3(0.0, 0.0, 1.0));
	float n101 = dot(VolHash01(VolWrap(c + ivec3(1, 0, 1), period)) * 2.0 - 1.0, f - vec3(1.0, 0.0, 1.0));
	float n011 = dot(VolHash01(VolWrap(c + ivec3(0, 1, 1), period)) * 2.0 - 1.0, f - vec3(0.0, 1.0, 1.0));
	float n111 = dot(VolHash01(VolWrap(c + ivec3(1, 1, 1), period)) * 2.0 - 1.0, f - vec3(1.0, 1.0, 1.0));

	return mix(
		mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
		mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y),
		u.z);
}

// Distance to the nearest feature point (0 at a point, about 1 between them).
float VolWorley(vec3 p, int period)
{
	vec3 i = floor(p);
	vec3 f = p - i;
	ivec3 c = ivec3(i);
	float best = 8.0;
	for (int z = -1; z <= 1; z++)
	{
		for (int y = -1; y <= 1; y++)
		{
			for (int x = -1; x <= 1; x++)
			{
				ivec3 o = ivec3(x, y, z);
				vec3 r = vec3(o) + VolHash01(VolWrap(c + o, period)) - f;
				best = min(best, dot(r, r));
			}
		}
	}

	return sqrt(best);
}

// Octaves of gradient noise, each twice the frequency and gain times the amplitude of the one before (with a period, each
// octave tiles over it: frequency and period double together). The sum is scaled so its range stays the same for any
// gain: at gain 0.5 it is the plain sum.
float VolFbm(vec3 p, int octaves, float gain, int period)
{
	// ("half" is a reserved word in GLSL.)
	float sum = 0.0;
	float amplitude = 0.5;
	float total = 0.0;
	float plain = 0.0;
	float plainAmplitude = 0.5;
	for (int o = 0; o < octaves; o++)
	{
		sum += amplitude * VolGradientNoise(p, period);
		total += amplitude;
		plain += plainAmplitude;
		p = period > 0 ? p * 2.0 : p * 2.03 + vec3(17.1, 31.7, 7.3);
		period *= 2;
		amplitude *= gain;
		plainAmplitude *= 0.5;
	}

	return total > 0.0 ? sum / total * plain : 0.0;
}

// The chosen noise type at p, about 0..1 (the coverage threshold cuts the cloud out of it).
float VolNoiseOfType(vec3 p, int type, int octaves, float gain)
{
	if (type == VOL_NOISE_PERLIN)
	{
		return VolFbm(p, octaves, gain, 0) * 0.9 + 0.5;
	}

	if (type == VOL_NOISE_PERLIN_WORLEY)
	{
		float shape = VolFbm(p, octaves, gain, 0) * 0.9 + 0.5;
		float billow = 1.0 - VolWorley(p * 1.8, 0);
		return shape * (0.55 + 0.6 * billow);
	}

	float sum = 0.0;
	float amplitude = 1.0;
	float total = 0.0;
	for (int o = 0; o < octaves; o++)
	{
		float n;
		if (type == VOL_NOISE_BILLOW)
		{
			n = clamp(abs(VolGradientNoise(p, 0)) * 2.2, 0.0, 1.0);
		}
		else if (type == VOL_NOISE_RIDGED)
		{
			float r = 1.0 - clamp(abs(VolGradientNoise(p, 0)) * 2.2, 0.0, 1.0);
			n = r * r;
		}
		else
		{
			n = 1.0 - VolWorley(p, 0);
		}

		sum += amplitude * n;
		total += amplitude;
		p = p * 2.03 + vec3(17.1, 31.7, 7.3);
		amplitude *= gain;
	}

	return total > 0.0 ? sum / total : 0.0;
}

#endif
