//Properties
Color ("Color", Color) = (1, 1, 1, 1)
DiffuseMap ("Albedo", 2D) = "white" {}
// Tiling (xy) and offset (zw) of the albedo map, shared by the normal, mask and specular maps (Unity's main texture
// scale / offset); the emission map has its own.
DiffuseMap_ST ("Albedo Tiling Offset", Vector) = (1, 1, 0, 0)
[Toggle(NORMAL_MAP)] NormalMap ("Normal Map", 2D) = "bump" {}
NormalScale ("Normal Scale", Float) = 1
[HDR] Emissive ("Emissive (rgb color, a intensity)", Color) = (0, 0, 0, 0)
[Toggle(EMISSION_MAP)] EmissionMap ("Emission Map (rgb, multiplies Emissive)", 2D) = "white" {}
EmissionMap_ST ("Emission Tiling Offset", Vector) = (1, 1, 0, 0)
// Scene: the highlight and reflectance every lit shader takes from the scene (Environment Specular Strength / Shine).
// Metallic, Specular: Unity's Standard and Standard (Specular setup) lighting, highlights shaped by the roughness and
// reflectance from the material (Metallic, or Specular Color with its map).
[KeywordEnum(Scene, Metallic, Specular)] Workflow ("Workflow", Float) = 0
Roughness ("Roughness", Range(0, 1)) = 0.6
Metallic ("Metallic", Range(0, 1)) = 0
SpecularColor ("Specular Color", Color) = (0.04, 0.04, 0.04, 1)
[Toggle(SPECULAR_MAP)] SpecularMap ("Specular Map (rgb, multiplies Specular Color)", 2D) = "white" {}
// HDRP's mask map: r metallic (in place of Metallic), g ambient occlusion, a smoothness (x Smoothness Scale, in place of
// Roughness). One texture for the three, linear (no sRGB).
[Toggle(MASK_MAP)] MaskMap ("Mask Map (r metallic, g occlusion, a smoothness)", 2D) = "white" {}
SmoothnessScale ("Smoothness Scale", Range(0, 1)) = 1
OcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1
// Unity's secondary maps (Standard, Detail Albedo x2): the detail albedo multiplies the albedo by twice its value in
// gamma space (a mid grey leaves it as it is), the detail normal map adds its tilt to the normal map's (only with a
// normal map, as Unity). Both tiled by DetailAlbedoMap_ST on the mesh's first UV set.
[Toggle(DETAIL_ALBEDO_MAP)] DetailAlbedoMap ("Detail Albedo x2", 2D) = "gray" {}
DetailAlbedoMap_ST ("Detail Tiling Offset", Vector) = (1, 1, 0, 0)
[Toggle(DETAIL_NORMAL_MAP)] DetailNormalMap ("Detail Normal Map", 2D) = "bump" {}
DetailNormalScale ("Detail Normal Scale", Float) = 1
// The LEL_SURFACE_CAPTURE variant writes the surface instead of lighting it (impostor bakes).
SurfaceCapture On
/////Properties

//Vertex

#version 430
invariant gl_Position;

// Instance data: per draw (modelMatrix) or, in the instanced variant, from the GPU scene tables.
#include "Engine/Instancing.glsl"

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform vec4 DiffuseMap_ST;
#ifdef EMISSION_MAP
uniform vec4 EmissionMap_ST;
#endif
#if defined(DETAIL_ALBEDO_MAP) || (defined(DETAIL_NORMAL_MAP) && defined(NORMAL_MAP))
#define LEL_DETAIL_UV 1
uniform vec4 DetailAlbedoMap_ST;
#endif

in vec3 vPosition;
in vec4 vColor;
in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vTangent;
in vec3 vBitangent;

out vec3 fNormal;
#ifdef NORMAL_MAP
out vec3 fTangent;
out vec3 fBitangent;
#endif
out vec3 fPosition;
out vec2 fTexCoord;
#ifdef EMISSION_MAP
out vec2 fEmissionTexCoord;
#endif
#ifdef LEL_DETAIL_UV
out vec2 fDetailTexCoord;
#endif

void main()
{
	mat4 model = LelModelMatrix();
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	fPosition = vec3(model * vec4(vPosition, 1.0));
	fNormal = LelNormalMatrix() * vNormal;
#ifdef NORMAL_MAP
	fTangent = mat3(model) * vTangent;
	fBitangent = mat3(model) * vBitangent;
#endif
	fTexCoord = vTexCoord * DiffuseMap_ST.xy + DiffuseMap_ST.zw;
#ifdef EMISSION_MAP
	fEmissionTexCoord = vTexCoord * EmissionMap_ST.xy + EmissionMap_ST.zw;
#endif
#ifdef LEL_DETAIL_UV
	fDetailTexCoord = vTexCoord * DetailAlbedoMap_ST.xy + DetailAlbedoMap_ST.zw;
#endif
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

// LEL_SURFACE_CAPTURE (ShaderPass.SurfaceCapture): the surface is written, not lit, so nothing of the lighting is read.
#ifndef LEL_SURFACE_CAPTURE
#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"
#endif
#include "Engine/StandardBRDF.glsl"
#ifndef LEL_SURFACE_CAPTURE
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"
#endif

// Lit with an albedo map (multiplies Color) and, each in a variant of its own that only the materials setting it pay
// for, a tangent-space normal map, an emission map (multiplies Emissive), a mask map (metallic, occlusion, smoothness)
// and a specular map. Material: Color, DiffuseMap with DiffuseMap_ST, NormalMap and NormalScale, Emissive (rgb color,
// a intensity, also fed into the GI), EmissionMap with EmissionMap_ST, Roughness (0 = unset -> 0.6), Workflow (Scene /
// Metallic / Specular), Metallic, SpecularColor, SpecularMap, MaskMap, SmoothnessScale, OcclusionStrength, and Unity's
// detail maps: DetailAlbedoMap with DetailAlbedoMap_ST, DetailNormalMap with DetailNormalScale.
// The GI sees the same surface: its captures read these values (Engine/GISurface.glsl) and the geometry prepass draws
// the normal map and the mask's smoothness into the G-buffer the screen-space GI reads (Engine/GBuffer.shader).
#include "Engine/Instancing.glsl"

in vec3 fNormal;
#ifdef NORMAL_MAP
in vec3 fTangent;
in vec3 fBitangent;
#endif
in vec3 fPosition;
in vec2 fTexCoord;
#ifdef EMISSION_MAP
in vec2 fEmissionTexCoord;
#endif
#if defined(DETAIL_ALBEDO_MAP) || (defined(DETAIL_NORMAL_MAP) && defined(NORMAL_MAP))
in vec2 fDetailTexCoord;
#endif

uniform sampler2D DiffuseMap;
#ifdef NORMAL_MAP
uniform sampler2D NormalMap;
uniform float NormalScale;
#ifdef DETAIL_NORMAL_MAP
uniform sampler2D DetailNormalMap;
uniform float DetailNormalScale;
#endif
#endif
#ifdef DETAIL_ALBEDO_MAP
uniform sampler2D DetailAlbedoMap;
#endif
#ifdef EMISSION_MAP
uniform sampler2D EmissionMap;
#endif
#ifdef MASK_MAP
uniform sampler2D MaskMap;
uniform float SmoothnessScale;
uniform float OcclusionStrength;
#endif
#ifdef WORKFLOW_METALLIC
uniform float Metallic;
#endif
#ifdef WORKFLOW_SPECULAR
uniform vec4 SpecularColor;
#ifdef SPECULAR_MAP
uniform sampler2D SpecularMap;
#endif
#endif

struct Ambient {
	vec4 ambColor;
	float ambStrength;
};

struct Specular {
	float specStrength;
	float specShine;
	vec3 viewPos;
};

uniform Ambient LAmbient;
uniform Specular LSpecular;

#ifdef LEL_SURFACE_CAPTURE
// The surface (ShaderPass.SurfaceCapture).
layout(location = 0) out vec4 CaptureDiffuse;   // rgb: the colour the surface scatters
layout(location = 1) out vec4 CaptureNormal;    // xyz: the shading normal (world)
layout(location = 2) out vec4 CaptureSpecular;  // rgb: the specular colour (0: the scene workflow), a: smoothness
layout(location = 3) out vec4 CaptureEmission;  // rgb: the emitted radiance, a: the occlusion of the indirect light
#else
out vec4 FragColor;
#endif

void main()
{
	LelMaterial material = LelMaterialData();
	vec4 color = material.color * texture(DiffuseMap, fTexCoord);
#ifdef DETAIL_ALBEDO_MAP
	// Unity's x2 in gamma space is x 2^2.2 on linear values (unity_ColorSpaceDouble of a linear project).
	color.rgb *= texture(DetailAlbedoMap, fDetailTexCoord).rgb * 4.59479380;
#endif
	vec4 emissive = material.emissive;
#ifdef EMISSION_MAP
	emissive.rgb *= texture(EmissionMap, fEmissionTexCoord).rgb;
#endif
	float materialRoughness = material.params.x;
	float roughness = materialRoughness > 0.0 ? materialRoughness : 0.6;
#ifdef MASK_MAP
	vec4 mask = texture(MaskMap, fTexCoord);
	// The GI reads a roughness of 0 as "unset": the smoothest the cones trace is kept above it.
	roughness = max(1.0 - mask.a * SmoothnessScale, 0.03);
	// Unity's occlusion strength: from no occlusion (0) to the map's (1). It darkens the indirect light only.
	float surfaceOcclusion = mix(1.0, mask.g, OcclusionStrength);
#endif

	vec3 vertexNormal = normalize(fNormal);
	vec3 N = vertexNormal;
#ifdef NORMAL_MAP
	// Tangent-space normal, its tilt scaled by NormalScale.
	vec3 tangent = fTangent - vertexNormal * dot(vertexNormal, fTangent);
	vec3 bitangent = fBitangent;
	vec3 mapped = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;
	mapped.xy *= NormalScale;
#ifdef DETAIL_NORMAL_MAP
	// Unity's UnpackScaleNormal (z rebuilt after the scale) and BlendNormals: the tilts add, the heights multiply.
	vec3 detail;
	detail.xy = (texture(DetailNormalMap, fDetailTexCoord).xy * 2.0 - 1.0) * DetailNormalScale;
	detail.z = sqrt(1.0 - clamp(dot(detail.xy, detail.xy), 0.0, 1.0));
	mapped = normalize(vec3(mapped.xy + detail.xy, mapped.z * detail.z));
#endif
	if (dot(tangent, tangent) > 1e-12 && dot(bitangent, bitangent) > 1e-12)
	{
		N = normalize(mat3(normalize(tangent), normalize(bitangent), vertexNormal) * mapped);
	}
#endif

#ifdef LEL_SURFACE_CAPTURE
	// What the workflows below shade with: their diffuse and specular colours (the scene workflow has no specular
	// colour of its own: zero), the roughness as smoothness, the mask's occlusion.
	vec3 captureDiffuse = color.rgb;
	vec3 captureSpecular = vec3(0.0);
#if defined(WORKFLOW_METALLIC) || defined(WORKFLOW_SPECULAR)
	float captureOneMinusReflectivity;
#ifdef WORKFLOW_METALLIC
#ifdef MASK_MAP
	float captureMetallic = mask.r;
#else
	float captureMetallic = Metallic;
#endif
	captureDiffuse = LelDiffuseAndSpecularFromMetallic(color.rgb, captureMetallic, captureSpecular, captureOneMinusReflectivity);
#else
	captureSpecular = SpecularColor.rgb;
#ifdef SPECULAR_MAP
	captureSpecular *= texture(SpecularMap, fTexCoord).rgb;
#endif
	captureDiffuse = LelEnergyConservation(color.rgb, captureSpecular, captureOneMinusReflectivity);
#endif
#endif
	float captureOcclusion = 1.0;
#ifdef MASK_MAP
	captureOcclusion = surfaceOcclusion;
#endif
	CaptureDiffuse = vec4(captureDiffuse, 1.0);
	CaptureNormal = vec4(N, 1.0);
	CaptureSpecular = vec4(captureSpecular, 1.0 - roughness);
	CaptureEmission = vec4(emissive.rgb * emissive.a, captureOcclusion);
#else
	vec3 V = normalize(LSpecular.viewPos - fPosition);

	vec3 albedo = color.rgb;

	// Indirect: voxel cone tracing replaces most of the flat ambient term.
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetIndirectLighting(fPosition, N, V, roughness, indirectDiffuse, occlusion, indirectSpecular);

#if defined(WORKFLOW_METALLIC) || defined(WORKFLOW_SPECULAR)
	float oneMinusReflectivity;
	vec3 specColor;
	vec3 diffColor;
#ifdef WORKFLOW_METALLIC
#ifdef MASK_MAP
	float metallic = mask.r;
#else
	float metallic = Metallic;
#endif
	diffColor = LelDiffuseAndSpecularFromMetallic(albedo, metallic, specColor, oneMinusReflectivity);
#else
	specColor = SpecularColor.rgb;
#ifdef SPECULAR_MAP
	specColor *= texture(SpecularMap, fTexCoord).rgb;
#endif
	diffColor = LelEnergyConservation(albedo, specColor, oneMinusReflectivity);
#endif

	// Unity's indirect light: the flat ambient (under the GI's occlusion) plus the GI's irradiance, and the GI's
	// reflections; the surface's own occlusion darkens both.
	LelIndirect gi;
	gi.diffuse = LAmbient.ambColor.rgb * LAmbient.ambStrength * occlusion + indirectDiffuse;
	gi.specular = indirectSpecular;
#ifdef MASK_MAP
	gi.diffuse *= surfaceOcclusion;
	gi.specular *= surfaceOcclusion;
#endif

	vec3 result = LelStandardBRDFIndirect(diffColor, specColor, oneMinusReflectivity, roughness, N, V, gi)
		+ emissive.rgb * emissive.a;

	// The directional lights (Engine/DirectionalLights.glsl), each behind its own shadows. Nothing is looked up for a
	// light the surface faces away from.
	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		if (dot(N, L) > 0.0)
		{
			// Per channel: tinted transparent casters colour their shadows. The geometric normal biases the lookup.
			vec3 lightColor = LelDirectionalRadiance(i) * SampleLightShadowRGB(i, fPosition, vertexNormal, L);
			result += LelStandardBRDFDirect(diffColor, specColor, roughness, N, V, L, lightColor);
		}
	}

	// The local lights (Engine/Lights.glsl): the same BRDF, unshadowed.
	for (int i = 0; i < LelLightCount(); i++)
	{
		vec3 Li;
		vec3 radiance;
		if (LelLightAt(i, fPosition, Li, radiance))
		{
			result += LelStandardBRDFDirect(diffColor, specColor, roughness, N, V, Li, radiance);
		}
	}
#else
	// Scene specular: Blinn-Phong with the scene's strength and shine (as Lit).
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirect = ambient * occlusion
		+ indirectDiffuse * albedo
		+ indirectSpecular * LSpecular.specStrength;
#ifdef MASK_MAP
	indirect *= surfaceOcclusion;
#endif

	vec3 result = indirect
		+ emissive.rgb * emissive.a;

	// The directional lights (Engine/DirectionalLights.glsl), each behind its own shadows. Nothing is looked up for a
	// light the surface faces away from.
	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		float ndl = dot(N, L);
		if (ndl > 0.0)
		{
			// Per channel: tinted transparent casters colour their shadows. The geometric normal biases the lookup.
			vec3 lightColor = LelDirectionalRadiance(i) * SampleLightShadowRGB(i, fPosition, vertexNormal, L);
			float spec = pow(max(dot(N, normalize(L + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + spec) * lightColor * ndl;
		}
	}

	// The local lights (Engine/Lights.glsl), lit as the directional ones, unshadowed.
	for (int i = 0; i < LelLightCount(); i++)
	{
		vec3 Li;
		vec3 radiance;
		if (LelLightAt(i, fPosition, Li, radiance))
		{
			float specularI = pow(max(dot(N, normalize(Li + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + specularI) * radiance * max(dot(N, Li), 0.0);
		}
	}
#endif
	FragColor = vec4(result, color.a);
#endif
}

/////Fragment
