// Noise for the VFX operators and turbulence blocks: classic 3D Perlin noise (Ashima Arts / Stefan Gustavson),
// value noise, fractal sums and curl noise (a divergence-free vector field from the gradients of three noises).
#ifndef VFX_NOISE_GLSL
#define VFX_NOISE_GLSL

vec3 VfxNoiseMod289(vec3 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 VfxNoiseMod289(vec4 x) { return x - floor(x * (1.0 / 289.0)) * 289.0; }
vec4 VfxNoisePermute(vec4 x) { return VfxNoiseMod289(((x * 34.0) + 10.0) * x); }
vec4 VfxNoiseTaylorInvSqrt(vec4 r) { return 1.79284291400159 - 0.85373472095314 * r; }
vec3 VfxNoiseFade(vec3 t) { return t * t * t * (t * (t * 6.0 - 15.0) + 10.0); }

// Classic Perlin noise in [-1, 1].
float VfxPerlinRaw(vec3 P)
{
	vec3 Pi0 = floor(P);
	vec3 Pi1 = Pi0 + vec3(1.0);
	Pi0 = VfxNoiseMod289(Pi0);
	Pi1 = VfxNoiseMod289(Pi1);
	vec3 Pf0 = fract(P);
	vec3 Pf1 = Pf0 - vec3(1.0);
	vec4 ix = vec4(Pi0.x, Pi1.x, Pi0.x, Pi1.x);
	vec4 iy = vec4(Pi0.yy, Pi1.yy);
	vec4 iz0 = Pi0.zzzz;
	vec4 iz1 = Pi1.zzzz;

	vec4 ixy = VfxNoisePermute(VfxNoisePermute(ix) + iy);
	vec4 ixy0 = VfxNoisePermute(ixy + iz0);
	vec4 ixy1 = VfxNoisePermute(ixy + iz1);

	vec4 gx0 = ixy0 * (1.0 / 7.0);
	vec4 gy0 = fract(floor(gx0) * (1.0 / 7.0)) - 0.5;
	gx0 = fract(gx0);
	vec4 gz0 = vec4(0.5) - abs(gx0) - abs(gy0);
	vec4 sz0 = step(gz0, vec4(0.0));
	gx0 -= sz0 * (step(0.0, gx0) - 0.5);
	gy0 -= sz0 * (step(0.0, gy0) - 0.5);

	vec4 gx1 = ixy1 * (1.0 / 7.0);
	vec4 gy1 = fract(floor(gx1) * (1.0 / 7.0)) - 0.5;
	gx1 = fract(gx1);
	vec4 gz1 = vec4(0.5) - abs(gx1) - abs(gy1);
	vec4 sz1 = step(gz1, vec4(0.0));
	gx1 -= sz1 * (step(0.0, gx1) - 0.5);
	gy1 -= sz1 * (step(0.0, gy1) - 0.5);

	vec3 g000 = vec3(gx0.x, gy0.x, gz0.x);
	vec3 g100 = vec3(gx0.y, gy0.y, gz0.y);
	vec3 g010 = vec3(gx0.z, gy0.z, gz0.z);
	vec3 g110 = vec3(gx0.w, gy0.w, gz0.w);
	vec3 g001 = vec3(gx1.x, gy1.x, gz1.x);
	vec3 g101 = vec3(gx1.y, gy1.y, gz1.y);
	vec3 g011 = vec3(gx1.z, gy1.z, gz1.z);
	vec3 g111 = vec3(gx1.w, gy1.w, gz1.w);

	vec4 norm0 = VfxNoiseTaylorInvSqrt(vec4(dot(g000, g000), dot(g010, g010), dot(g100, g100), dot(g110, g110)));
	g000 *= norm0.x;
	g010 *= norm0.y;
	g100 *= norm0.z;
	g110 *= norm0.w;
	vec4 norm1 = VfxNoiseTaylorInvSqrt(vec4(dot(g001, g001), dot(g011, g011), dot(g101, g101), dot(g111, g111)));
	g001 *= norm1.x;
	g011 *= norm1.y;
	g101 *= norm1.z;
	g111 *= norm1.w;

	float n000 = dot(g000, Pf0);
	float n100 = dot(g100, vec3(Pf1.x, Pf0.yz));
	float n010 = dot(g010, vec3(Pf0.x, Pf1.y, Pf0.z));
	float n110 = dot(g110, vec3(Pf1.xy, Pf0.z));
	float n001 = dot(g001, vec3(Pf0.xy, Pf1.z));
	float n101 = dot(g101, vec3(Pf1.x, Pf0.y, Pf1.z));
	float n011 = dot(g011, vec3(Pf0.x, Pf1.yz));
	float n111 = dot(g111, Pf1);

	vec3 fade_xyz = VfxNoiseFade(Pf0);
	vec4 n_z = mix(vec4(n000, n100, n010, n110), vec4(n001, n101, n011, n111), fade_xyz.z);
	vec2 n_yz = mix(n_z.xy, n_z.zw, fade_xyz.y);
	float n_xyz = mix(n_yz.x, n_yz.y, fade_xyz.x);
	return 2.2 * n_xyz;
}

// Fractal Perlin noise in [-1, 1]: octaves of doubling frequency, halving amplitude (roughness scales the fall-off).
float VfxPerlin(vec3 p, float frequency, int octaves, float roughness)
{
	float sum = 0.0;
	float amplitude = 1.0;
	float total = 0.0;
	vec3 q = p * frequency;
	for (int i = 0; i < 8; i++)
	{
		if (i >= octaves) break;
		sum += VfxPerlinRaw(q) * amplitude;
		total += amplitude;
		amplitude *= roughness;
		q = q * 2.0 + vec3(31.7, 17.3, 5.1);
	}

	return total > 0.0 ? sum / total : 0.0;
}

// Three independent fractal noises (a vector in [-1, 1]^3).
vec3 VfxPerlin3(vec3 p, float frequency, int octaves, float roughness)
{
	return vec3(
		VfxPerlin(p, frequency, octaves, roughness),
		VfxPerlin(p + vec3(123.4, 71.1, 9.7), frequency, octaves, roughness),
		VfxPerlin(p + vec3(-57.3, 211.8, 65.2), frequency, octaves, roughness));
}

float VfxValueHash(vec3 p)
{
	p = fract(p * 0.3183099 + vec3(0.1, 0.2, 0.3));
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Value noise in [-1, 1] (cheaper and blockier than Perlin).
float VfxValueNoiseRaw(vec3 p)
{
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n = mix(mix(mix(VfxValueHash(i), VfxValueHash(i + vec3(1.0, 0.0, 0.0)), f.x),
	                  mix(VfxValueHash(i + vec3(0.0, 1.0, 0.0)), VfxValueHash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
	              mix(mix(VfxValueHash(i + vec3(0.0, 0.0, 1.0)), VfxValueHash(i + vec3(1.0, 0.0, 1.0)), f.x),
	                  mix(VfxValueHash(i + vec3(0.0, 1.0, 1.0)), VfxValueHash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
	return n * 2.0 - 1.0;
}

float VfxValueNoise(vec3 p, float frequency, int octaves, float roughness)
{
	float sum = 0.0;
	float amplitude = 1.0;
	float total = 0.0;
	vec3 q = p * frequency;
	for (int i = 0; i < 8; i++)
	{
		if (i >= octaves) break;
		sum += VfxValueNoiseRaw(q) * amplitude;
		total += amplitude;
		amplitude *= roughness;
		q = q * 2.0 + vec3(7.1, 13.9, 3.3);
	}

	return total > 0.0 ? sum / total : 0.0;
}

// Classic Perlin noise (the same field as VfxPerlinRaw) with its analytic gradient: xyz the gradient, w the value.
vec4 VfxPerlinGradRaw(vec3 P)
{
	vec3 Pi0 = floor(P);
	vec3 Pi1 = Pi0 + vec3(1.0);
	Pi0 = VfxNoiseMod289(Pi0);
	Pi1 = VfxNoiseMod289(Pi1);
	vec3 Pf0 = fract(P);
	vec3 Pf1 = Pf0 - vec3(1.0);
	vec4 ix = vec4(Pi0.x, Pi1.x, Pi0.x, Pi1.x);
	vec4 iy = vec4(Pi0.yy, Pi1.yy);
	vec4 iz0 = Pi0.zzzz;
	vec4 iz1 = Pi1.zzzz;

	vec4 ixy = VfxNoisePermute(VfxNoisePermute(ix) + iy);
	vec4 ixy0 = VfxNoisePermute(ixy + iz0);
	vec4 ixy1 = VfxNoisePermute(ixy + iz1);

	vec4 gx0 = ixy0 * (1.0 / 7.0);
	vec4 gy0 = fract(floor(gx0) * (1.0 / 7.0)) - 0.5;
	gx0 = fract(gx0);
	vec4 gz0 = vec4(0.5) - abs(gx0) - abs(gy0);
	vec4 sz0 = step(gz0, vec4(0.0));
	gx0 -= sz0 * (step(0.0, gx0) - 0.5);
	gy0 -= sz0 * (step(0.0, gy0) - 0.5);

	vec4 gx1 = ixy1 * (1.0 / 7.0);
	vec4 gy1 = fract(floor(gx1) * (1.0 / 7.0)) - 0.5;
	gx1 = fract(gx1);
	vec4 gz1 = vec4(0.5) - abs(gx1) - abs(gy1);
	vec4 sz1 = step(gz1, vec4(0.0));
	gx1 -= sz1 * (step(0.0, gx1) - 0.5);
	gy1 -= sz1 * (step(0.0, gy1) - 0.5);

	vec4 norm0 = VfxNoiseTaylorInvSqrt(gx0 * gx0 + gy0 * gy0 + gz0 * gz0);
	vec4 norm1 = VfxNoiseTaylorInvSqrt(gx1 * gx1 + gy1 * gy1 + gz1 * gz1);
	vec3 g000 = vec3(gx0.x, gy0.x, gz0.x) * norm0.x;
	vec3 g100 = vec3(gx0.y, gy0.y, gz0.y) * norm0.y;
	vec3 g010 = vec3(gx0.z, gy0.z, gz0.z) * norm0.z;
	vec3 g110 = vec3(gx0.w, gy0.w, gz0.w) * norm0.w;
	vec3 g001 = vec3(gx1.x, gy1.x, gz1.x) * norm1.x;
	vec3 g101 = vec3(gx1.y, gy1.y, gz1.y) * norm1.y;
	vec3 g011 = vec3(gx1.z, gy1.z, gz1.z) * norm1.z;
	vec3 g111 = vec3(gx1.w, gy1.w, gz1.w) * norm1.w;

	float n000 = dot(g000, Pf0);
	float n100 = dot(g100, vec3(Pf1.x, Pf0.yz));
	float n010 = dot(g010, vec3(Pf0.x, Pf1.y, Pf0.z));
	float n110 = dot(g110, vec3(Pf1.xy, Pf0.z));
	float n001 = dot(g001, vec3(Pf0.xy, Pf1.z));
	float n101 = dot(g101, vec3(Pf1.x, Pf0.y, Pf1.z));
	float n011 = dot(g011, vec3(Pf0.x, Pf1.yz));
	float n111 = dot(g111, Pf1);

	// Quintic fade and its derivative.
	vec3 u = VfxNoiseFade(Pf0);
	vec3 du = 30.0 * Pf0 * Pf0 * (Pf0 * (Pf0 - 2.0) + 1.0);

	// The trilinear blend as a polynomial in u, for the value and the gradient.
	float k1 = n100 - n000;
	float k2 = n010 - n000;
	float k3 = n001 - n000;
	float k4 = n000 - n100 - n010 + n110;
	float k5 = n000 - n010 - n001 + n011;
	float k6 = n000 - n100 - n001 + n101;
	float k7 = -n000 + n100 + n010 - n110 + n001 - n101 - n011 + n111;
	float value = n000 + k1 * u.x + k2 * u.y + k3 * u.z + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z;

	vec3 gradient = g000 + u.x * (g100 - g000) + u.y * (g010 - g000) + u.z * (g001 - g000)
		+ u.x * u.y * (g000 - g100 - g010 + g110) + u.y * u.z * (g000 - g010 - g001 + g011)
		+ u.z * u.x * (g000 - g100 - g001 + g101) + u.x * u.y * u.z * (-g000 + g100 + g010 - g110 + g001 - g101 - g011 + g111)
		+ du * vec3(k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
		            k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
		            k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);
	return 2.2 * vec4(gradient, value);
}

// Fractal Perlin noise with its gradient (with respect to p): xyz the gradient, w the value, as VfxPerlin.
vec4 VfxPerlinGrad(vec3 p, float frequency, int octaves, float roughness)
{
	vec4 sum = vec4(0.0);
	float amplitude = 1.0;
	float total = 0.0;
	float scale = frequency;
	vec3 q = p * frequency;
	for (int i = 0; i < 8; i++)
	{
		if (i >= octaves) break;
		vec4 n = VfxPerlinGradRaw(q);
		sum += vec4(n.xyz * scale, n.w) * amplitude;
		total += amplitude;
		amplitude *= roughness;
		scale *= 2.0;
		q = q * 2.0 + vec3(31.7, 17.3, 5.1);
	}

	return total > 0.0 ? sum / total : vec4(0.0);
}

// Curl of a vector potential made of three Perlin noises: a divergence-free turbulence field. The noise gradients
// are analytic, so the curl costs three noise evaluations per octave (finite differences would take eighteen).
vec3 VfxCurlNoise(vec3 p, float frequency, int octaves)
{
	const float roughness = 0.5;
	vec3 gx = VfxPerlinGrad(p, frequency, octaves, roughness).xyz;
	vec3 gy = VfxPerlinGrad(p + vec3(123.4, 71.1, 9.7), frequency, octaves, roughness).xyz;
	vec3 gz = VfxPerlinGrad(p + vec3(-57.3, 211.8, 65.2), frequency, octaves, roughness).xyz;
	return vec3(gz.y - gy.z, gx.z - gz.x, gy.x - gx.y);
}

#endif
