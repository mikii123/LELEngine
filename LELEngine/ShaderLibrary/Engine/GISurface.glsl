// What the GI captures (Engine/Voxelize.shader, VoxelizeCoverage.shader, CardCapture.shader) see of a material: its
// diffuse colour and its emission, from the conventional material values the passes upload per draw
// (LELEngine.Rendering.GISurface). Texture coordinates as the material shaders read them: the mesh's own (images are
// stored bottom row first), with the material's tiling and offset.
#ifndef LEL_GI_SURFACE_GLSL
#define LEL_GI_SURFACE_GLSL

uniform vec4 voxAlbedo;           // Color
uniform vec4 voxEmissive;         // Emissive: rgb color, a intensity (applied by the capture: cards keep it out)
uniform vec4 voxAlbedoST;         // DiffuseMap_ST: tiling xy, offset zw (the mask and specular maps share it)
uniform vec4 voxEmissionST;       // EmissionMap_ST
uniform sampler2D voxAlbedoMap;   // texture unit 0
uniform int voxUseAlbedoMap;
uniform sampler2D voxEmissionMap; // unit 1, multiplies the emission colour
uniform int voxUseEmissionMap;

// Physically based workflows of Engine/LitTextured.shader (1 Metallic, 2 Specular; 0 the albedo as it is). The GI's
// surfaces only scatter diffusely, so a material's specular reflectance is added to its diffuse colour the way a fully
// rough surface reflects it (Unreal's Lumen cards do the same, EnvBRDFApproxFullyRough): a metal keeps its colour in
// the bounce light instead of turning black.
uniform int voxWorkflow;
uniform vec4 voxSpecular;         // Metallic: x metallic; Specular: rgb specular colour
uniform sampler2D voxMaskMap;     // unit 2: r metallic
uniform int voxUseMaskMap;
uniform sampler2D voxSpecularMap; // unit 3: rgb, multiplies the specular colour
uniform int voxUseSpecularMap;

// The diffuse colour the GI bounces for a base colour (Color, clamped by the caller where it must be) at a texture
// coordinate of the mesh.
vec3 LelGIAlbedo(vec3 baseColor, vec2 uv)
{
	vec2 surfaceUV = uv * voxAlbedoST.xy + voxAlbedoST.zw;
	vec3 albedo = baseColor;
	if (voxUseAlbedoMap != 0)
	{
		albedo *= texture(voxAlbedoMap, surfaceUV).rgb;
	}

	if (voxWorkflow == 1)
	{
		float metallic = voxUseMaskMap != 0 ? texture(voxMaskMap, surfaceUV).r : voxSpecular.x;
		vec3 specColor = mix(vec3(0.04), albedo, metallic);
		return albedo * (0.96 - metallic * 0.96) + specColor * 0.45;
	}

	if (voxWorkflow == 2)
	{
		vec3 specColor = voxSpecular.rgb;
		if (voxUseSpecularMap != 0)
		{
			specColor *= texture(voxSpecularMap, surfaceUV).rgb;
		}

		return albedo * (1.0 - max(max(specColor.r, specColor.g), specColor.b)) + specColor * 0.45;
	}

	return albedo;
}

// The emission colour (Emissive rgb times the emission map) at a texture coordinate of the mesh, without the intensity.
vec3 LelGIEmission(vec2 uv)
{
	vec3 emission = voxEmissive.rgb;
	if (voxUseEmissionMap != 0)
	{
		emission *= texture(voxEmissionMap, uv * voxEmissionST.xy + voxEmissionST.zw).rgb;
	}

	return emission;
}

#endif
