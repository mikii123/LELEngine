//Properties
Color ("Color (a = opacity)", Color) = (1, 1, 1, 0.5)
[HDR] Emissive ("Emissive (rgb color, a intensity)", Color) = (0, 0, 0, 0)
Roughness ("Roughness", Range(0, 1)) = 0.2
// Refraction: what is behind comes from a copy of the scene taken right before the object draws (GrabPass), shifted by
// the normal map's tilt, which also shapes the lighting; objects in front of the surface are never shifted into it.
[Toggle(REFRACTION)] Refraction ("Refraction", Float) = 0
NormalMap ("Normal Map (Refraction)", 2D) = "bump" {}
RefractionStrength ("Refraction Strength (world units)", Float) = 0.1
#if REFRACTION
// The shader composites the shifted scene itself, so nothing blends; the nearest surface wins where the object overlaps
// itself, with depth written as Unity's refraction shaders do.
Blend Off
ZWrite On
GrabPass RefractionStrength
#else
// Premultiplied alpha: the diffuse terms fade with the opacity, reflections and emission stay.
Blend One OneMinusSrcAlpha
ZWrite Off
#endif
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
#ifdef REFRACTION
out vec3 fTangent;
out vec3 fBitangent;
#endif

void main()
{
	mat4 model = LelModelMatrix();
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	fPosition = vec3(model * vec4(vPosition, 1.0));
	fNormal = LelNormalMatrix() * vNormal;
	fTexCoord = vTexCoord;
#ifdef REFRACTION
	fTangent = mat3(model) * vTangent;
	fBitangent = mat3(model) * vBitangent;
#endif
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

#ifdef REFRACTION
// Refraction: NormalMap (tangent space), RefractionStrength (how far the scene behind shifts, world units).
#include "Engine/GrabPass.glsl"
#endif

in vec3 fNormal;
in vec3 fPosition;
in vec2 fTexCoord;
#ifdef REFRACTION
in vec3 fTangent;
in vec3 fBitangent;

uniform sampler2D NormalMap;
uniform float RefractionStrength;
#endif

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

	vec3 vertexNormal = normalize(fNormal);
	vec3 N = vertexNormal;
#ifdef REFRACTION
	// Tangent-space normal (the default "bump" texture leaves the vertex normal unchanged).
	vec3 tangent = fTangent - vertexNormal * dot(vertexNormal, fTangent);
	vec3 mapped = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;
	if (dot(tangent, tangent) > 1e-12 && dot(fBitangent, fBitangent) > 1e-12)
	{
		N = normalize(mat3(normalize(tangent), normalize(fBitangent), vertexNormal) * mapped);
	}
#endif

	// Both sides are drawn (Cull Off): a back face is lit as the inside surface it is.
	if (!gl_FrontFacing)
	{
		N = -N;
		vertexNormal = -vertexNormal;
	}

	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(LSpecular.viewPos - fPosition);
	vec3 H = normalize(L + V);

	vec3 shadow = SampleShadowRGB(fPosition, vertexNormal, L);

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

	vec3 scattered = ambient * occlusion + indirectDiffuse * albedo + diffuse * shadow;
	vec3 reflected = indirectSpecular * reflectance + specular * shadow + emissive.rgb * emissive.a;
#ifdef REFRACTION
	// What the surface lets through is the scene behind it, shifted by the normal's tilt across the surface: the image
	// the blending below makes, refracted (a flat normal map lets the scene through unshifted).
	vec3 behind = LelSceneRefraction(N - vertexNormal * dot(N, vertexNormal), RefractionStrength);
	FragColor = vec4(behind * (1.0 - opacity) + scattered * opacity + reflected, 1.0);
#else
	// Premultiplied output (Blend One OneMinusSrcAlpha): what the surface scatters scales with its opacity,
	// what it reflects or emits is added on top.
	FragColor = vec4(scattered * opacity + reflected, opacity);
#endif
}

/////Fragment
