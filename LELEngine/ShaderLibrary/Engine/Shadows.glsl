// Directional shadow map sampling. Uniforms are set by Lighting.SetShadowUniforms.
// Include after #version: #include "Engine/Shadows.glsl"

uniform int shadowsEnabled;
uniform mat4 lightSpaceMatrix;
uniform sampler2DShadow ShadowMap;
uniform float shadowNormalBias;
uniform float shadowDepthBias;

// Returns 1.0 when fully lit, 0.0 when fully shadowed.
float SampleShadow(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	if (shadowsEnabled == 0) return 1.0;

	float ndl = clamp(dot(worldNormal, toLight), 0.0, 1.0);
	vec3 offsetPos = worldPos + worldNormal * shadowNormalBias * (1.5 - ndl);
	vec4 ls = lightSpaceMatrix * vec4(offsetPos, 1.0);
	vec3 proj = ls.xyz / ls.w * 0.5 + 0.5;
	if (proj.z > 1.0) return 1.0;
	proj.z -= shadowDepthBias;

	// 3x3 PCF on top of hardware 2x2 comparison filtering.
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
