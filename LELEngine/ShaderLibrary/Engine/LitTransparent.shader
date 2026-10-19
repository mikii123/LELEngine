//Properties
Color ("Color (a = opacity)", Color) = (1, 1, 1, 0.5)
[HDR] Emissive ("Emissive (rgb color, a intensity)", Color) = (0, 0, 0, 0)
Roughness ("Roughness", Range(0, 1)) = 0.2
// Premultiplied alpha: the diffuse terms fade with the opacity, reflections and emission stay.
Blend One OneMinusSrcAlpha
ZWrite Off
Cull Off
Queue Transparent
/////Properties

//Vertex

#version 430
invariant gl_Position;

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
// Transparent surfaces are not in the depth buffer: the indirect light comes from the GI volume (cones or the
// radiance cache probes), never from the screen-space resolve of the opaque surface behind them.
#include "Engine/VolumeLighting.glsl"

// Material: Color (a = opacity), Emissive (rgb color, a intensity), Roughness (0 = unset -> 0.2).
#include "Engine/Instancing.glsl"

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

out vec4 FragColor;

void main()
{
	LelMaterial material = LelMaterialData();
	vec4 color = material.color;
	vec4 emissive = material.emissive;
	float materialRoughness = material.params.x;

	// Both sides are drawn (Cull Off): a back face is lit as the inside surface it is.
	vec3 N = normalize(fNormal);
	if (!gl_FrontFacing) N = -N;
	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(LSpecular.viewPos - fPosition);
	vec3 H = normalize(L + V);

	vec3 shadow = SampleShadowRGB(fPosition, N, L);

	vec3 albedo = color.rgb;
	vec3 lightColor = LDirectional.dirColor.rgb * LDirectional.dirStrength;
	float roughness = materialRoughness > 0.0 ? materialRoughness : 0.2;

	// Glass reflects (and hides what is behind it) more at grazing angles: Schlick's Fresnel term lifts the
	// opacity and the reflection weight from their face-on values towards 1 at the silhouette.
	float fresnel = pow(1.0 - max(dot(N, V), 0.0), 5.0);
	float opacity = mix(clamp(color.a, 0.0, 1.0), 1.0, fresnel);
	float reflectance = mix(LSpecular.specStrength, 1.0, fresnel);

	// Direct
	float ndl = max(dot(N, L), 0.0);
	vec3 diffuse = albedo * lightColor * ndl;
	float spec = pow(max(dot(N, H), 0.0), LSpecular.specShine) * LSpecular.specStrength;
	vec3 specular = lightColor * spec * ndl;

	// Indirect from the GI volume.
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetVolumeIndirectLighting(fPosition, N, V, roughness, indirectDiffuse, occlusion, indirectSpecular);

	// Premultiplied output (Blend One OneMinusSrcAlpha): what the surface scatters scales with its opacity,
	// what it reflects or emits is added on top.
	vec3 scattered = ambient * occlusion + indirectDiffuse * albedo + diffuse * shadow;
	vec3 reflected = indirectSpecular * reflectance + specular * shadow + emissive.rgb * emissive.a;
	FragColor = vec4(scattered * opacity + reflected, opacity);
}

/////Fragment
