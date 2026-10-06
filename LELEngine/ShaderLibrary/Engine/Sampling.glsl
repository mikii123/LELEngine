// Low-discrepancy sampling, hemisphere mappings and spherical harmonics helpers shared by the
// Lumen-style passes (surface cache radiosity, screen probe gather).

const float SAMPLING_PI = 3.14159265359;

// 32-bit integer hash (Wang) -> [0,1)
float HashToFloat(uint x)
{
	x = (x ^ 61u) ^ (x >> 16u);
	x *= 9u;
	x = x ^ (x >> 4u);
	x *= 0x27d4eb2du;
	x = x ^ (x >> 15u);
	return float(x) / 4294967296.0;
}

uint HashCoord(ivec2 coord, uint seed)
{
	return uint(coord.x) * 1973u + uint(coord.y) * 9277u + seed * 26699u;
}

float RadicalInverse(uint bits)
{
	bits = (bits << 16u) | (bits >> 16u);
	bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
	bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
	bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
	bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
	return float(bits) * 2.3283064365386963e-10;
}

vec2 Hammersley(uint i, uint count)
{
	return vec2(float(i) / float(count), RadicalInverse(i));
}

// Orthonormal frame around n (Duff et al.).
void TangentFrame(vec3 n, out vec3 t, out vec3 b)
{
	float s = n.z >= 0.0 ? 1.0 : -1.0;
	float a = -1.0 / (s + n.z);
	float c = n.x * n.y * a;
	t = vec3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
	b = vec3(c, s + n.y * n.y * a, -n.y);
}

// Cosine-weighted hemisphere direction (z up) from a 2D sample; pdf = cos(theta) / pi.
vec3 CosineSampleHemisphere(vec2 u)
{
	float r = sqrt(u.x);
	float phi = 2.0 * SAMPLING_PI * u.y;
	return vec3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u.x)));
}

// Hemispherical octahedral mapping (z >= 0 hemisphere), uv in [0,1]^2. Nearly equal area.
vec3 HemiOctahedralToDirection(vec2 uv)
{
	vec2 e = uv * 2.0 - 1.0;
	vec2 t = vec2(e.x + e.y, e.x - e.y) * 0.5;
	vec3 v = vec3(t.x, t.y, 1.0 - abs(t.x) - abs(t.y));
	return normalize(v);
}

vec2 DirectionToHemiOctahedral(vec3 d)
{
	d /= (abs(d.x) + abs(d.y) + abs(d.z));
	vec2 e = vec2(d.x + d.y, d.x - d.y);
	return e * 0.5 + 0.5;
}

// Full-sphere octahedral mapping (world-space radiance cache probes), uv in [0,1]^2.
vec2 DirectionToOctahedral(vec3 d)
{
	d /= (abs(d.x) + abs(d.y) + abs(d.z));
	vec2 e = d.xy;
	if (d.z < 0.0)
	{
		e = (1.0 - abs(d.yx)) * vec2(d.x >= 0.0 ? 1.0 : -1.0, d.y >= 0.0 ? 1.0 : -1.0);
	}
	return e * 0.5 + 0.5;
}

vec3 OctahedralToDirection(vec2 uv)
{
	vec2 e = uv * 2.0 - 1.0;
	vec3 v = vec3(e, 1.0 - abs(e.x) - abs(e.y));
	float t = max(-v.z, 0.0);
	v.x += v.x >= 0.0 ? -t : t;
	v.y += v.y >= 0.0 ? -t : t;
	return normalize(v);
}

// Real spherical harmonics basis up to l = 2 (9 coefficients).
void SHBasis9(vec3 d, out float sh[9])
{
	sh[0] = 0.282095;
	sh[1] = 0.488603 * d.y;
	sh[2] = 0.488603 * d.z;
	sh[3] = 0.488603 * d.x;
	sh[4] = 1.092548 * d.x * d.y;
	sh[5] = 1.092548 * d.y * d.z;
	sh[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
	sh[7] = 1.092548 * d.x * d.z;
	sh[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}

// Irradiance from SH radiance coefficients (Ramamoorthi & Hanrahan): convolution with the clamped cosine.
// Band factors A0 = pi, A1 = 2pi/3, A2 = pi/4.
vec3 SHIrradiance(vec3 c[9], vec3 n)
{
	float sh[9];
	SHBasis9(n, sh);
	vec3 e = c[0] * sh[0] * 3.14159265;
	e += (c[1] * sh[1] + c[2] * sh[2] + c[3] * sh[3]) * 2.09439510;
	e += (c[4] * sh[4] + c[5] * sh[5] + c[6] * sh[6] + c[7] * sh[7] + c[8] * sh[8]) * 0.78539816;
	return max(e, vec3(0.0));
}

vec3 SHEvaluate(vec3 c[9], vec3 d)
{
	float sh[9];
	SHBasis9(d, sh);
	vec3 r = vec3(0.0);
	for (int i = 0; i < 9; i++) r += c[i] * sh[i];
	return max(r, vec3(0.0));
}

// Radiance along d with the higher bands attenuated (Hanning-style window). Second-order SH fitted to a
// hemisphere rings; raw evaluation along a moving reflection vector sweeps those ringing lobes across
// surfaces as the camera turns, so view-dependent lookups use this damped version.
vec3 SHEvaluateWindowed(vec3 c[9], vec3 d)
{
	float sh[9];
	SHBasis9(d, sh);
	vec3 r = c[0] * sh[0];
	r += (c[1] * sh[1] + c[2] * sh[2] + c[3] * sh[3]) * 0.75;
	r += (c[4] * sh[4] + c[5] * sh[5] + c[6] * sh[6] + c[7] * sh[7] + c[8] * sh[8]) * 0.25;
	return max(r, vec3(0.0));
}
