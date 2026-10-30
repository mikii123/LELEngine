//Properties
// What the variants read of a material whose own variant samples a normal map, a mask map or a detail normal map
// (NORMAL_MAP, MASK_MAP, DETAIL_NORMAL_MAP with NORMAL_MAP: the keywords and values of Engine/LitTextured.shader), set
// by the geometry prepass; the defaults stand in for values an older material lacks.
DiffuseMap_ST ("Tiling Offset", Vector) = (1, 1, 0, 0)
NormalMap ("Normal Map", 2D) = "bump" {}
NormalScale ("Normal Scale", Float) = 1
MaskMap ("Mask Map", 2D) = "white" {}
SmoothnessScale ("Smoothness Scale", Range(0, 1)) = 1
DetailAlbedoMap_ST ("Detail Tiling Offset", Vector) = (1, 1, 0, 0)
DetailNormalMap ("Detail Normal Map", 2D) = "bump" {}
DetailNormalScale ("Detail Normal Scale", Float) = 1
/////Properties

//Vertex

#version 430
invariant gl_Position;

#define LEL_PREVIOUS_MODELS 1
#include "Engine/Instancing.glsl"

#if defined(NORMAL_MAP) || defined(MASK_MAP) || defined(DETAIL_NORMAL_MAP)
#define LEL_GBUFFER_MAPS 1
#endif

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
#ifdef LEL_GBUFFER_MAPS
uniform vec4 DiffuseMap_ST;
#endif
#ifdef DETAIL_NORMAL_MAP
uniform vec4 DetailAlbedoMap_ST;
#endif

in vec3 vPosition;
in vec3 vNormal;
#ifdef LEL_GBUFFER_MAPS
in vec2 vTexCoord;
#endif
#ifdef NORMAL_MAP
in vec3 vTangent;
in vec3 vBitangent;
#endif

out vec3 fNormal;
out vec3 fMotion;
#ifdef LEL_GBUFFER_MAPS
out vec2 fTexCoord;
#endif
#ifdef NORMAL_MAP
out vec3 fTangent;
out vec3 fBitangent;
#endif
#ifdef DETAIL_NORMAL_MAP
out vec2 fDetailTexCoord;
#endif

void main()
{
	mat4 model = LelModelMatrix();
	// Exactly the material shaders' expression: the opaque pass tests LEQUAL against this depth without writing its
	// own, and invariant only holds for identical expressions (P * V * (M * v) rounds differently, the sky shows through).
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	vec4 world = model * vec4(vPosition, 1.0);
	fNormal = LelNormalMatrix() * vNormal;
	// World-space displacement since last frame: affine over a triangle of a rigid object, so the interpolation is exact.
	fMotion = world.xyz - (LelPreviousModelMatrix() * vec4(vPosition, 1.0)).xyz;
#ifdef LEL_GBUFFER_MAPS
	fTexCoord = vTexCoord * DiffuseMap_ST.xy + DiffuseMap_ST.zw;
#endif
#ifdef NORMAL_MAP
	fTangent = mat3(model) * vTangent;
	fBitangent = mat3(model) * vBitangent;
#endif
#ifdef DETAIL_NORMAL_MAP
	fDetailTexCoord = vTexCoord * DetailAlbedoMap_ST.xy + DetailAlbedoMap_ST.zw;
#endif
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#include "Engine/Instancing.glsl"

in vec3 fNormal;
in vec3 fMotion;
#if defined(NORMAL_MAP) || defined(MASK_MAP)
in vec2 fTexCoord;
#endif
#ifdef NORMAL_MAP
in vec3 fTangent;
in vec3 fBitangent;

uniform sampler2D NormalMap;
uniform float NormalScale;
#endif
#ifdef DETAIL_NORMAL_MAP
in vec2 fDetailTexCoord;

uniform sampler2D DetailNormalMap;
uniform float DetailNormalScale;
#endif
#ifdef MASK_MAP
uniform sampler2D MaskMap;
uniform float SmoothnessScale;
#endif

layout(location = 0) out vec4 NormalRoughness;
// xyz: how far the surface point moved in world space since the previous frame (current - previous), w: 1 when it moved.
layout(location = 1) out vec4 Motion;

void main()
{
	vec3 normal = normalize(fNormal);
#ifdef NORMAL_MAP
	// The shading normal of Engine/LitTextured.shader, so the screen-space GI lights the bumps the material shows.
	vec3 tangent = fTangent - normal * dot(normal, fTangent);
	vec3 mapped = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;
	mapped.xy *= NormalScale;
#ifdef DETAIL_NORMAL_MAP
	vec3 detail;
	detail.xy = (texture(DetailNormalMap, fDetailTexCoord).xy * 2.0 - 1.0) * DetailNormalScale;
	detail.z = sqrt(1.0 - clamp(dot(detail.xy, detail.xy), 0.0, 1.0));
	mapped = normalize(vec3(mapped.xy + detail.xy, mapped.z * detail.z));
#endif
	if (dot(tangent, tangent) > 1e-12 && dot(fBitangent, fBitangent) > 1e-12)
	{
		normal = normalize(mat3(normalize(tangent), normalize(fBitangent), normal) * mapped);
	}
#endif

	// Conventional material parameter (0 = material default): per draw, or from the material table.
	float roughness = LelRoughness();
#ifdef MASK_MAP
	// The mask's smoothness, kept above 0 (which reads as unset) as the material shader does.
	roughness = max(1.0 - texture(MaskMap, fTexCoord).a * SmoothnessScale, 0.03);
#endif

	NormalRoughness = vec4(normal, roughness);
	Motion = vec4(fMotion, dot(fMotion, fMotion) > 1e-12 ? 1.0 : 0.0);
}

/////Fragment
