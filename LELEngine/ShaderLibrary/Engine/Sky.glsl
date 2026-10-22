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
uniform float giSkyboxLod;     // mip level rays read: about 8 texels across a face (a diffuse sky)
uniform float giSkyboxFaceSize; // texels across a face of the GI's sky cubemap (its mip 0)

// The sky at a mip level of the GI's own sky cubemap (SkyEnvironment: a box-filtered copy of the skybox with mips).
vec3 SkyRadianceLod(vec3 direction, float lod)
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
	return textureLod(giSkybox, d, lod).rgb * giSkyboxTint.rgb;
}

// What a ray that left the scene sees (rays average many directions: a diffuse sky).
vec3 SkyRadiance(vec3 direction)
{
	return SkyRadianceLod(direction, giSkyboxLod);
}

// What a cone that left the scene sees: the sky averaged over the cone's footprint. aperture = tan of the half angle;
// the cone spans about 2 * aperture radians, a face texel at mip m (pi / 2) / (size / 2^m) radians. A rough surface's
// wide specular cone reads a blurred sky, a mirror's narrow one a sharp sky.
vec3 SkyRadiance(vec3 direction, float aperture)
{
	float lod = log2(max(4.0 * aperture * giSkyboxFaceSize / 3.14159265, 1.0));
	return SkyRadianceLod(direction, lod);
}

#endif
