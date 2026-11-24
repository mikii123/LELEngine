//Properties
Color ("Color", Color) = (1, 1, 1, 1)
[HDR] Emissive ("Emissive (rgb color, a intensity)", Color) = (0, 0, 0, 0)
Roughness ("Roughness", Range(0, 1)) = 0.6
// The LEL_SURFACE_CAPTURE variant writes the surface instead of lighting it (impostor bakes).
SurfaceCapture On
// The LEL_RAY_QUERY variant traces its voxel cones' rays per fragment where voxel cone tracing's rays trace the scene's
// triangles and the view has no screen-space resolve (the other cameras of a stack).
RayQueries On
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

// LEL_SURFACE_CAPTURE (ShaderPass.SurfaceCapture): the surface is written, not lit, so nothing of the lighting is read.
#ifndef LEL_SURFACE_CAPTURE
#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"
#include "Engine/RayTracedShadows.glsl"
#endif

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

#ifdef LEL_SURFACE_CAPTURE
// The surface (ShaderPass.SurfaceCapture).
layout(location = 0) out vec4 CaptureDiffuse;   // rgb: the colour the surface scatters
layout(location = 1) out vec4 CaptureNormal;    // xyz: the shading normal (world)
layout(location = 2) out vec4 CaptureSpecular;  // rgb: the specular colour (0: the highlight is the scene's), a: smoothness
layout(location = 3) out vec4 CaptureEmission;  // rgb: the emitted radiance, a: the occlusion of the indirect light
#else
out vec4 FragColor;
#endif

void main()
{
	LelMaterial material = LelMaterialData();
	vec4 color = material.color;
	vec4 emissive = material.emissive;
	float materialRoughness = material.params.x;

	vec3 N = normalize(fNormal);
	vec3 albedo = color.rgb;
	float roughness = materialRoughness > 0.0 ? materialRoughness : 0.6;

#ifdef LEL_SURFACE_CAPTURE
	CaptureDiffuse = vec4(albedo, 1.0);
	CaptureNormal = vec4(N, 1.0);
	CaptureSpecular = vec4(0.0, 0.0, 0.0, 1.0 - roughness);
	CaptureEmission = vec4(emissive.rgb * emissive.a, 1.0);
#else
	vec3 V = normalize(LSpecular.viewPos - fPosition);

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
			// Per channel: tinted transparent casters colour their shadows (traced shadows: Engine/RayTracedShadows.glsl).
			vec3 lightColor = LelDirectionalRadiance(i) * LelDirectionalShadowRGB(i, fPosition, N, L);
			float spec = pow(max(dot(N, normalize(L + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + spec) * lightColor * ndl;
		}
	}

	// The local lights (Engine/Lights.glsl), lit as the directional ones, behind their traced shadows where they have them.
	for (LelLightIterator lights = LelLightsAt(fPosition); LelNextLight(lights);)
	{
		int i = lights.light;
		vec3 Li;
		vec3 radiance;
		if (LelLightAt(i, fPosition, Li, radiance))
		{
			float specularI = pow(max(dot(N, normalize(Li + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + specularI) * radiance * (max(dot(N, Li), 0.0) * LelLocalLightShadow(i));
		}
	}

	FragColor = vec4(result, color.a);
#endif
}

/////Fragment
