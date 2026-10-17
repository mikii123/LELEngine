//Properties
Color ("Color", Color) = (1, 1, 1, 1)
DiffuseMap ("Albedo", 2D) = "white" {}
NormalMap ("Normal Map", 2D) = "bump" {}
[HDR] Emissive ("Emissive (rgb color, a intensity)", Color) = (0, 0, 0, 0)
Roughness ("Roughness", Range(0, 1)) = 0.6
/////Properties

//Vertex

#version 430
invariant gl_Position;

// Instance data: per draw (modelMatrix) or, in the instanced variant, from the GPU scene tables.
#include "Engine/Instancing.glsl"

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;

in vec3 vPosition;
in vec4 vColor;
in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vTangent;
in vec3 vBitangent;

out vec3 fNormal;
out vec3 fTangent;
out vec3 fBitangent;
out vec3 fPosition;
out vec2 fTexCoord;

void main()
{
	mat4 model = LelModelMatrix();
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	fPosition = vec3(model * vec4(vPosition, 1.0));
	fNormal = LelNormalMatrix() * vNormal;
	fTangent = mat3(model) * vTangent;
	fBitangent = mat3(model) * vBitangent;
	fTexCoord = vTexCoord;
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"

// Lit with an albedo map (multiplies Color) and a tangent-space normal map. Material: Color, DiffuseMap,
// NormalMap, Emissive (rgb color, a intensity, also fed into the GI), Roughness (0 = unset -> 0.6).
#include "Engine/Instancing.glsl"

in vec3 fNormal;
in vec3 fTangent;
in vec3 fBitangent;
in vec3 fPosition;
in vec2 fTexCoord;

uniform sampler2D DiffuseMap;
uniform sampler2D NormalMap;

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};

struct Ambient {
	vec4 ambColor;
	float ambStrength;
};

struct Specular {
	float specStrength;
	float specShine;
	vec3 viewPos;
};

uniform Directional LDirectional;
uniform Ambient LAmbient;
uniform Specular LSpecular;

out vec4 FragColor;

void main()
{
	LelMaterial material = LelMaterialData();
	vec4 color = material.color * texture(DiffuseMap, fTexCoord);
	vec4 emissive = material.emissive;
	float materialRoughness = material.params.x;

	// Tangent-space normal (the default "bump" texture leaves the vertex normal unchanged).
	vec3 vertexNormal = normalize(fNormal);
	vec3 tangent = fTangent - vertexNormal * dot(vertexNormal, fTangent);
	vec3 bitangent = fBitangent;
	vec3 mapped = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;
	vec3 N = vertexNormal;
	if (dot(tangent, tangent) > 1e-12 && dot(bitangent, bitangent) > 1e-12)
	{
		N = normalize(mat3(normalize(tangent), normalize(bitangent), vertexNormal) * mapped);
	}

	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(LSpecular.viewPos - fPosition);
	vec3 H = normalize(L + V);

	// Per channel: tinted transparent casters colour their shadows. The geometric normal biases the lookup.
	vec3 shadow = SampleShadowRGB(fPosition, vertexNormal, L);

	vec3 albedo = color.rgb;
	vec3 lightColor = LDirectional.dirColor.rgb * LDirectional.dirStrength;
	float roughness = materialRoughness > 0.0 ? materialRoughness : 0.6;

	// Direct
	float ndl = max(dot(N, L), 0.0);
	vec3 diffuse = albedo * lightColor * ndl;
	float spec = pow(max(dot(N, H), 0.0), LSpecular.specShine) * LSpecular.specStrength;
	vec3 specular = lightColor * spec * ndl;

	// Indirect: voxel cone tracing replaces most of the flat ambient term.
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetIndirectLighting(fPosition, N, V, roughness, indirectDiffuse, occlusion, indirectSpecular);

	vec3 result = ambient * occlusion
		+ indirectDiffuse * albedo
		+ indirectSpecular * LSpecular.specStrength
		+ (diffuse + specular) * shadow
		+ emissive.rgb * emissive.a;
	FragColor = vec4(result, color.a);
}

/////Fragment
