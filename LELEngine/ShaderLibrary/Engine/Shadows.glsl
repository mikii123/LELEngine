// Directional shadow map sampling: the shadows of every directional light that casts them (Engine/DirectionalLights.glsl,
// the same light index). The maps' layout is a uniform block on binding 5, uploaded once a frame: the camera's maps
// (Rendering.Passes.ShadowPass through Lighting.SetShadowUniforms) or maps covering a region on their own (the GI
// passes' grid-covering maps, Rendering.GridShadowMap).
// Include after #version: #include "Engine/Shadows.glsl"
//
// One atlas holds every map: a light's cascades are tiles of it (lelShadowLights[light]: its first tile and how many;
// none: the light casts no shadows). The camera's cascades each cover a slice of the view: a tile's matrix takes a
// world position into the tile (xy, texture coordinates of the atlas) and depth (z, 0..1); a receiver uses the first
// cascade of the light whose sphere it is inside, the last one otherwise, and the shadows fade out toward the shadow
// distance. The biases are in texels of the cascade used, so the near, sharp cascades get small ones.
//
// The opaque casters are in the depth map (compared with a tent filter). The transparent casters are in the
// transmittance map (Rendering.TransparentShadows): rgb = the light they let through, a = the nearest one's depth;
// a receiver behind that depth gets its light multiplied by the rgb. SampleLightShadowRGB keeps the colour of tinted
// casters. SampleShadowRGB and SampleShadow (the original contract) are the main light's (light 0).
#ifndef LEL_SHADOWS_GLSL
#define LEL_SHADOWS_GLSL

#define SHADOW_MAX_CASCADES 4
#define SHADOW_MAX_LIGHTS 4
#define SHADOW_MAX_TILES 16

struct LelShadowCascade
{
	mat4 matrix;  // world -> the cascade's tile of the atlas (xy) and depth (z, 0..1)
	vec4 sphere;  // xyz center, w squared radius inside which a receiver uses the cascade (world)
	vec4 rect;    // the tile in the atlas's texture coordinates (min.xy, max.xy)
};

layout(std140, binding = 5) uniform LelShadowBlock
{
	vec4 lelShadowFade;                        // xyz camera position, w shadow distance (0: no fade)
	vec4 lelShadowBias;                        // x normal bias, y depth bias (texels)
	ivec4 lelShadowLights[SHADOW_MAX_LIGHTS];  // per directional light: x its first cascade, y its cascades (0: no shadows)
	LelShadowCascade lelShadowCascades[SHADOW_MAX_TILES];
};

uniform int shadowsEnabled;
uniform sampler2DShadow ShadowMap;
uniform int shadowTransmittanceEnabled;
uniform sampler2D ShadowTransmittance;

// Radius of the tent filter in texels. The biases scale with it: its outer taps compare the receiver's depth
// with texels that far away.
const float ShadowFilterRadius = 2.5;

// A light has a shadow map (and the draw receives shadows).
bool ShadowLightHasMap(int light)
{
	return shadowsEnabled != 0 && lelShadowLights[light].y > 0;
}

// The cascade of a receiver among a light's: the first whose sphere contains it, the last one otherwise.
int ShadowLightCascade(int light, vec3 worldPos)
{
	int first = lelShadowLights[light].x;
	int last = first + lelShadowLights[light].y - 1;
	for (int i = first; i < last; i++)
	{
		vec3 d = worldPos - lelShadowCascades[i].sphere.xyz;
		if (dot(d, d) < lelShadowCascades[i].sphere.w) return i;
	}
	return last;
}

// The main light's cascade of a receiver.
int ShadowCascade(vec3 worldPos)
{
	return ShadowLightCascade(0, worldPos);
}

// A cascade's tile of the atlas in texture coordinates (min.xy, max.xy).
vec4 ShadowTileRect(int cascade)
{
	return lelShadowCascades[cascade].rect;
}

// How much of the shadows is left at a receiver: 1 inside the shadow distance, fading to 0 at it.
float ShadowFade(vec3 worldPos)
{
	if (lelShadowFade.w <= 0.0) return 1.0;
	return clamp((lelShadowFade.w - distance(worldPos, lelShadowFade.xyz)) / (lelShadowFade.w * 0.1), 0.0, 1.0);
}

// Texture coordinates (xy) and biased depth (z) of a receiver in a cascade. The cascade's texel size and the depth
// change per world unit come from its matrix (an orthographic projection).
vec3 ShadowProjection(int cascade, vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	mat4 m = lelShadowCascades[cascade].matrix;
	float texel = 1.0 / (length(vec3(m[0].x, m[1].x, m[2].x)) * float(textureSize(ShadowMap, 0).x));
	float scale = texel * ShadowFilterRadius;
	float ndl = clamp(dot(worldNormal, toLight), 0.0, 1.0);
	vec3 offsetPos = worldPos + worldNormal * (lelShadowBias.x * scale * (1.0 - ndl));
	vec3 proj = (m * vec4(offsetPos, 1.0)).xyz;
	proj.z -= lelShadowBias.y * scale * length(vec3(m[0].z, m[1].z, m[2].z));
	return proj;
}

// One axis of the tent filter: the six texels -2..3 around the receiver's texel (f = its position past that texel's
// center, 0..1) weighted by a tent 2.5 texels wide on each side, paired into three bilinear fetches. Each fetch sits
// between its two texels where linear filtering weighs them as the tent does.
void ShadowTentAxis(float f, out vec3 weights, out vec3 offsets)
{
	float w0 = max(0.5 - f, 0.0);
	float w1 = 1.5 - f;
	float w2 = 2.5 - f;
	float w3 = 1.5 + f;
	float w4 = 0.5 + f;
	float w5 = max(f - 0.5, 0.0);
	weights = vec3(w0 + w1, w2 + w3, w4 + w5);
	offsets = vec3(-2.0, 0.0, 2.0) + vec3(w1, w3, w5) / weights;
}

// Tent filtered comparison (3x3 hardware 2x2 comparisons, 6x6 texels): 1.0 fully lit, 0.0 fully shadowed by opaque
// casters. The fetches stay inside the cascade's tile.
float ShadowOpaque(vec3 proj, vec4 rect)
{
	vec2 size = vec2(textureSize(ShadowMap, 0));
	vec2 st = proj.xy * size - 0.5; // texel space, texel k centered at k
	vec2 base = floor(st);
	vec3 wx, ox, wy, oy;
	ShadowTentAxis(st.x - base.x, wx, ox);
	ShadowTentAxis(st.y - base.y, wy, oy);

	vec2 lo = rect.xy + 0.5 / size;
	vec2 hi = rect.zw - 0.5 / size;
	float sum = 0.0;
	for (int y = 0; y < 3; y++)
	{
		for (int x = 0; x < 3; x++)
		{
			vec2 uv = clamp((base + vec2(ox[x], oy[y]) + 0.5) / size, lo, hi);
			sum += wx[x] * wy[y] * texture(ShadowMap, vec3(uv, proj.z));
		}
	}
	return sum / ((wx.x + wx.y + wx.z) * (wy.x + wy.y + wy.z));
}

// The light the transparent casters in front of the receiver let through (1 when there are none).
vec3 ShadowTransmitted(vec3 proj)
{
	if (shadowTransmittanceEnabled == 0) return vec3(1.0);
	vec4 t = texture(ShadowTransmittance, proj.xy);
	return proj.z > t.a ? t.rgb : vec3(1.0);
}

// Light of directional light `light` reaching the receiver per colour channel: 1.0 fully lit, 0.0 fully shadowed
// (1.0 for a light without a shadow map).
vec3 SampleLightShadowRGB(int light, vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	if (!ShadowLightHasMap(light)) return vec3(1.0);

	float fade = ShadowFade(worldPos);
	if (fade <= 0.0) return vec3(1.0);

	int cascade = ShadowLightCascade(light, worldPos);
	vec4 rect = lelShadowCascades[cascade].rect;
	vec3 proj = ShadowProjection(cascade, worldPos, worldNormal, toLight);
	if (proj.z > 1.0 || any(lessThan(proj.xy, rect.xy)) || any(greaterThan(proj.xy, rect.zw))) return vec3(1.0);
	return mix(vec3(1.0), ShadowOpaque(proj, rect) * ShadowTransmitted(proj), fade);
}

// Only the transparent casters' part of directional light `light` at the receiver, per colour channel (1.0 where none,
// or without a map): for a receiver whose opaque casters are traced instead (ray queries), the maps holding the
// transparent ones alone.
vec3 SampleLightTransmittanceRGB(int light, vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	if (!ShadowLightHasMap(light) || shadowTransmittanceEnabled == 0) return vec3(1.0);

	float fade = ShadowFade(worldPos);
	if (fade <= 0.0) return vec3(1.0);

	int cascade = ShadowLightCascade(light, worldPos);
	vec4 rect = lelShadowCascades[cascade].rect;
	vec3 proj = ShadowProjection(cascade, worldPos, worldNormal, toLight);
	if (proj.z > 1.0 || any(lessThan(proj.xy, rect.xy)) || any(greaterThan(proj.xy, rect.zw))) return vec3(1.0);
	return mix(vec3(1.0), ShadowTransmitted(proj), fade);
}

// The main light's (light 0): 1.0 fully lit, 0.0 fully shadowed, per colour channel.
vec3 SampleShadowRGB(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	return SampleLightShadowRGB(0, worldPos, worldNormal, toLight);
}

// Returns 1.0 when fully lit, 0.0 when fully shadowed (the luminance of SampleShadowRGB).
float SampleShadow(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	return dot(SampleShadowRGB(worldPos, worldNormal, toLight), vec3(0.2126, 0.7152, 0.0722));
}

#endif
