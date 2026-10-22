// Sky radiance for rays and cones that leave the scene: the environment's skybox cubemap when one is set
// (Lighting.SetSkyUniforms), else the constant GI sky color. Included by every GI shader that resolves misses.
// With no skybox the function is exactly the old constant, so lighting without one is unchanged.
#ifndef LEL_SKY_GLSL
#define LEL_SKY_GLSL

uniform vec3 giSkyRadiance;    // constant sky when no skybox is set
uniform int giSkyboxEnabled;
uniform samplerCube giSkybox;
uniform vec4 giSkyboxTint;     // rgb: the skybox material's tint times its exposure
uniform vec2 giSkyboxRotation; // cos, sin of the skybox's yaw rotation (same convention as Engine/Skybox.shader)
uniform vec2 giSkyboxTilt;     // cos, sin of its tilt about X (applied before the yaw)
uniform float giSkyboxLod;     // mip level sampled: diffuse rays read a blurred sky

vec3 SkyRadiance(vec3 direction)
{
	if (giSkyboxEnabled == 0)
	{
		return giSkyRadiance;
	}

	vec3 t = vec3(direction.x,
		giSkyboxTilt.x * direction.y - giSkyboxTilt.y * direction.z,
		giSkyboxTilt.y * direction.y + giSkyboxTilt.x * direction.z);
	float c = giSkyboxRotation.x;
	float s = giSkyboxRotation.y;
	vec3 d = vec3(c * t.x + s * t.z, t.y, -s * t.x + c * t.z);
	return textureLod(giSkybox, d, giSkyboxLod).rgb * giSkyboxTint.rgb;
}

#endif
