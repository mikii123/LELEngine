//Properties
Color ("Color", Color) = (1, 1, 1, 1)
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
out vec3 fPosition;
out vec2 fTexCoord;

void main()
{
	mat4 model = LelModelMatrix();
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	fPosition = vec3(model * vec4(vPosition, 1.0));
	fNormal = LelNormalMatrix() * vNormal;
	fTexCoord = vTexCoord;
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"

// Material: Color, Emissive (rgb color, a intensity, also fed into the GI), Roughness (0 = unset -> 0.6).
#include "Engine/Instancing.glsl"

in vec3 fNormal;
in vec3 fPosition;
in vec2 fTexCoord;

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

out vec4 FragColor;

void main()
{
	LelMaterial material = LelMaterialData();
	vec4 color = material.color;
	vec4 emissive = material.emissive;
	float materialRoughness = material.params.x;

	vec3 N = normalize(fNormal);
	vec3 V = normalize(LSpecular.viewPos - fPosition);

	vec3 albedo = color.rgb;
	float roughness = materialRoughness > 0.0 ? materialRoughness : 0.6;

	// Indirect: voxel cone tracing replaces most of the flat ambient term.
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetIndirectLighting(fPosition, N, V, roughness, indirectDiffuse, occlusion, indirectSpecular);

	vec3 result = ambient * occlusion
		+ indirectDiffuse * albedo
		+ indirectSpecular * LSpecular.specStrength
		+ emissive.rgb * emissive.a;

	// The directional lights (Engine/DirectionalLights.glsl), each behind its own shadows: Lambert and Blinn-Phong.
	// Nothing is looked up for a light the surface faces away from.
	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		float ndl = dot(N, L);
		if (ndl > 0.0)
		{
			// Per channel: tinted transparent casters colour their shadows.
			vec3 lightColor = LelDirectionalRadiance(i) * SampleLightShadowRGB(i, fPosition, N, L);
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

	FragColor = vec4(result, color.a);
}

/////Fragment
