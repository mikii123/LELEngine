// Directional shadow map sampling. Uniforms are set by Lighting.SetShadowUniforms (the camera's cascaded map) or
// Lighting.SetShadowMapUniforms (one map covering a region: the GI passes' grid-covering maps).
// Include after #version: #include "Engine/Shadows.glsl"
//
// The camera's map is an atlas of up to 4 cascades, each a slice of the view (one fills the map, two sit side by
// side, four in 2 x 2). shadowMatrices[i] takes a world position into cascade i's tile (xy) and depth (z, 0..1); a
// receiver uses the first cascade whose sphere it is inside, the last one otherwise, and the shadows fade out toward
// the shadow distance. The biases are in texels of the cascade used, so the near, sharp cascades get small ones.
//
// The opaque casters are in the depth map (compared with a tent filter). The transparent casters are in the
// transmittance map (Rendering.TransparentShadows): rgb = the light they let through, a = the nearest one's depth;
// a receiver behind that depth gets its light multiplied by the rgb. SampleShadowRGB keeps the colour of tinted
// casters, SampleShadow (the original float contract) their luminance.

#define SHADOW_MAX_CASCADES 4

uniform int shadowsEnabled;
uniform int shadowCascadeCount;
uniform mat4 shadowMatrices[SHADOW_MAX_CASCADES];
uniform vec4 shadowCascadeSpheres[SHADOW_MAX_CASCADES]; // xyz center, w squared radius (world)
uniform vec4 shadowFade;                                // xyz camera position, w shadow distance (0: no fade)
uniform sampler2DShadow ShadowMap;
uniform float shadowNormalBias;                         // texels
uniform float shadowDepthBias;                          // texels
uniform int shadowTransmittanceEnabled;
uniform sampler2D ShadowTransmittance;

// Radius of the tent filter in texels. The biases scale with it: its outer taps compare the receiver's depth
// with texels that far away.
const float ShadowFilterRadius = 2.5;

// The cascade of a receiver: the first whose sphere contains it, the last one otherwise.
int ShadowCascade(vec3 worldPos)
{
	for (int i = 0; i < shadowCascadeCount - 1; i++)
	{
		vec3 d = worldPos - shadowCascadeSpheres[i].xyz;
		if (dot(d, d) < shadowCascadeSpheres[i].w) return i;
	}
	return shadowCascadeCount - 1;
}

// A cascade's tile of the atlas in texture coordinates (min.xy, max.xy).
vec4 ShadowTileRect(int cascade)
{
	if (shadowCascadeCount == 1) return vec4(0.0, 0.0, 1.0, 1.0);
	vec2 size = vec2(0.5, shadowCascadeCount == 4 ? 0.5 : 1.0);
	vec2 origin = vec2(float(cascade & 1), float(cascade >> 1)) * size;
	return vec4(origin, origin + size);
}

// Texture coordinates (xy) and biased depth (z) of a receiver in a cascade. The cascade's texel size and the depth
// change per world unit come from its matrix (an orthographic projection).
vec3 ShadowProjection(int cascade, vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	mat4 m = shadowMatrices[cascade];
	float texel = 1.0 / (length(vec3(m[0].x, m[1].x, m[2].x)) * float(textureSize(ShadowMap, 0).x));
	float scale = texel * ShadowFilterRadius;
	float ndl = clamp(dot(worldNormal, toLight), 0.0, 1.0);
	vec3 offsetPos = worldPos + worldNormal * (shadowNormalBias * scale * (1.0 - ndl));
	vec3 proj = (m * vec4(offsetPos, 1.0)).xyz;
	proj.z -= shadowDepthBias * scale * length(vec3(m[0].z, m[1].z, m[2].z));
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

// Light reaching the receiver per colour channel: 1.0 fully lit, 0.0 fully shadowed.
vec3 SampleShadowRGB(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	if (shadowsEnabled == 0 || shadowCascadeCount <= 0) return vec3(1.0);

	float fade = 1.0;
	if (shadowFade.w > 0.0)
	{
		fade = clamp((shadowFade.w - distance(worldPos, shadowFade.xyz)) / (shadowFade.w * 0.1), 0.0, 1.0);
		if (fade <= 0.0) return vec3(1.0);
	}

	int cascade = ShadowCascade(worldPos);
	vec4 rect = ShadowTileRect(cascade);
	vec3 proj = ShadowProjection(cascade, worldPos, worldNormal, toLight);
	if (proj.z > 1.0 || any(lessThan(proj.xy, rect.xy)) || any(greaterThan(proj.xy, rect.zw))) return vec3(1.0);
	return mix(vec3(1.0), ShadowOpaque(proj, rect) * ShadowTransmitted(proj), fade);
}

// Returns 1.0 when fully lit, 0.0 when fully shadowed (the luminance of SampleShadowRGB).
float SampleShadow(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	return dot(SampleShadowRGB(worldPos, worldNormal, toLight), vec3(0.2126, 0.7152, 0.0722));
}
