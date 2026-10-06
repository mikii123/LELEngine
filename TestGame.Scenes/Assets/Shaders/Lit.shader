//Vertex

#version 430
invariant gl_Position;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

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
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0));
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
	fTexCoord = vTexCoord;
}

/////Vertex

//Fragment

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"

in vec3 fNormal;
in vec3 fPosition;
in vec2 fTexCoord;

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

// Material
uniform vec4 Color;
uniform vec4 Emissive;   // rgb color, a intensity (also fed into the GI volume)
uniform float Roughness; // 0 = unset -> treated as 0.6

out vec4 FragColor;

void main()
{
	vec3 N = normalize(fNormal);
	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(LSpecular.viewPos - fPosition);
	vec3 H = normalize(L + V);

	float shadow = SampleShadow(fPosition, N, L);

	vec3 albedo = Color.rgb;
	vec3 lightColor = LDirectional.dirColor.rgb * LDirectional.dirStrength;
	float roughness = Roughness > 0.0 ? Roughness : 0.6;

	// Direct
	float ndl = max(dot(N, L), 0.0);
	vec3 diffuse = albedo * lightColor * ndl;
	float spec = pow(max(dot(N, H), 0.0), LSpecular.specShine) * LSpecular.specStrength;
	vec3 specular = lightColor * spec * ndl;

	// Indirect: voxel cone tracing replaces most of the flat ambient term.
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirect = vec3(0.0);
	float occlusion = 1.0;
	if (giEnabled != 0)
	{
		vec4 diffuseGI = TraceDiffuseCones(fPosition, N);
		indirect += diffuseGI.rgb * albedo * giDiffuseStrength;
		occlusion = clamp(1.0 - diffuseGI.a * giOcclusionStrength, 0.0, 1.0);

		vec3 specularGI = TraceSpecularCone(fPosition, N, V, roughness);
		indirect += specularGI * LSpecular.specStrength * giSpecularStrength;
	}

	vec3 color = ambient * occlusion + indirect + (diffuse + specular) * shadow + Emissive.rgb * Emissive.a;
	FragColor = vec4(color, Color.a);
}

/////Fragment
