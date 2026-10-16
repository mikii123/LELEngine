// Directional shadow map sampling. Uniforms are set by Lighting.SetShadowUniforms (the GI passes bind their
// grid-covering maps the same way). Include after #version: #include "Engine/Shadows.glsl"
//
// The opaque casters are in the depth map (compared with PCF). The transparent casters are in the transmittance
// map (Rendering.TransparentShadows): rgb = the light they let through, a = the nearest one's depth; a receiver
// behind that depth gets its light multiplied by the rgb. SampleShadowRGB keeps the colour of tinted casters,
// SampleShadow (the original float contract) their luminance.

uniform int shadowsEnabled;
uniform mat4 lightSpaceMatrix;
uniform sampler2DShadow ShadowMap;
uniform float shadowNormalBias;
uniform float shadowDepthBias;
uniform int shadowTransmittanceEnabled;
uniform sampler2D ShadowTransmittance;

// Light-space texture coordinates and biased depth of a receiver; z > 1 lies beyond the map (lit).
vec3 ShadowProjection(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	float ndl = clamp(dot(worldNormal, toLight), 0.0, 1.0);
	vec3 offsetPos = worldPos + worldNormal * shadowNormalBias * (1.5 - ndl);
	vec4 ls = lightSpaceMatrix * vec4(offsetPos, 1.0);
	vec3 proj = ls.xyz / ls.w * 0.5 + 0.5;
	proj.z -= shadowDepthBias;
	return proj;
}

// 3x3 PCF on top of hardware 2x2 comparison filtering: 1.0 fully lit, 0.0 fully shadowed by opaque casters.
float ShadowOpaque(vec3 proj)
{
	vec2 texel = 1.0 / vec2(textureSize(ShadowMap, 0));
	float sum = 0.0;
	for (int x = -1; x <= 1; x++)
	{
		for (int y = -1; y <= 1; y++)
		{
			sum += texture(ShadowMap, vec3(proj.xy + vec2(x, y) * texel, proj.z));
		}
	}
	return sum / 9.0;
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
	if (shadowsEnabled == 0) return vec3(1.0);
	vec3 proj = ShadowProjection(worldPos, worldNormal, toLight);
	if (proj.z > 1.0) return vec3(1.0);
	return ShadowOpaque(proj) * ShadowTransmitted(proj);
}

// Returns 1.0 when fully lit, 0.0 when fully shadowed (the luminance of SampleShadowRGB).
float SampleShadow(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	return dot(SampleShadowRGB(worldPos, worldNormal, toLight), vec3(0.2126, 0.7152, 0.0722));
}
