// Traced shadows for the lit materials' fragment stages (Rendering.Passes.RayTracedShadowPass, bound by
// Lighting.SetRayShadowUniforms).
// Opaque surfaces of the lighting camera's view read a mask: per pixel how much of each traced light reaches the surface
// there, in a 2D array texture (Lighting.RayShadowBinding): first the local lights' slots (slot s: layer s / 4, channel
// s % 4; a light's slot is params.w of its list entry, -1: none), then a layer of the directional lights (light i: channel
// i). Draws of other views are given none.
// Surfaces that are not in the view's depth (transparent ones) trace their own rays instead, in a shader's LEL_RAY_QUERY
// variant that defines LEL_SHADOW_RAYS before including this file: a ray from the fragment towards each traced light
// through the same shadow casters (the pass's top level, acceleration structure 1), hard (from the light's centre).
// While the directional lights' shadows are traced their maps hold at most what the rays do not see (transparent casters'
// tint, particles, casters that draw their own shadow: rayShadowMapRest), which goes on top of the traced shadow.
// The mask exists on Vulkan only; elsewhere the functions are the plain lights' (no local shadows, the shadow maps).
// Include after Engine/Lights.glsl and Engine/Shadows.glsl.
#ifndef LEL_RAY_TRACED_SHADOWS_GLSL
#define LEL_RAY_TRACED_SHADOWS_GLSL

#ifdef LEL_VULKAN
uniform sampler2DArray RayShadowMask;
uniform int rayShadowLocal;             // 1: the local lights' slots are traced
uniform int rayShadowDirectional;       // bit i: directional light i is traced (channel i of the directional layer)
uniform int rayShadowDirectionalLayer;
uniform int rayShadowMapRest;           // 1: the directional maps hold only the casters the rays do not see
#endif

#ifdef LEL_SHADOW_RAYS
#include "Engine/RayOrigin.glsl"

layout(binding = 1) uniform accelerationStructureEXT RayShadowScene;

uniform int rayShadowTrace;             // 1: this draw traces its fragments' shadows (transparent surfaces)
uniform float rayShadowLength;          // how far a directional light's ray looks for casters
uniform vec3 rayShadowCamera;           // the view's position (the rays' start bias grows with the distance)

// Whether no shadow caster lies along a ray (first hit ends it).
bool LelShadowRayClear(vec3 origin, vec3 direction, float tMax)
{
	rayQueryEXT query;
	rayQueryInitializeEXT(query, RayShadowScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, origin, 0.0, direction, tMax);
	while (rayQueryProceedEXT(query))
	{
	}

	return rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT;
}

// Where a fragment's shadow ray starts: just off the surface on the light's side (a transparent surface is lit from
// both of its sides).
vec3 LelShadowRayOrigin(vec3 position, vec3 normal, vec3 towards)
{
	vec3 n = dot(normal, towards) < 0.0 ? -normal : normal;
	return SceneRayOrigin(position, n, 0.002 + 0.0005 * distance(position, rayShadowCamera));
}
#endif

// How much of local light i reaches this fragment's surface (1 where its shadows are not traced).
float LelLocalLightShadow(int i)
{
#ifdef LEL_VULKAN
	int slot = int(lelLights[i].params.w);
	if (rayShadowLocal != 0 && slot >= 0)
	{
		return texelFetch(RayShadowMask, ivec3(ivec2(gl_FragCoord.xy), slot >> 2), 0)[slot & 3];
	}
#endif
	return 1.0;
}

// Directional light i's shadow at this fragment: traced (with what the rays do not see from its maps), or from its shadow
// maps (Engine/Shadows.glsl: per channel, tinted by transparent casters).
vec3 LelDirectionalShadowRGB(int i, vec3 position, vec3 normal, vec3 towards)
{
#ifdef LEL_VULKAN
	if ((rayShadowDirectional & (1 << i)) != 0)
	{
		vec3 traced = vec3(texelFetch(RayShadowMask, ivec3(ivec2(gl_FragCoord.xy), rayShadowDirectionalLayer), 0)[i]);
		return rayShadowMapRest != 0 ? traced * SampleLightShadowRGB(i, position, normal, towards) : traced;
	}
#endif
	return SampleLightShadowRGB(i, position, normal, towards);
}

// Local light i's shadow at a point of a surface that is not in the view's depth (transparent): in a variant with
// LEL_SHADOW_RAYS a ray towards the light's centre for a light whose shadows are traced (up to its source's disc, as the
// opaque surfaces' rays), 1 elsewhere.
float LelLocalLightShadowAt(int i, vec3 position, vec3 normal)
{
#ifdef LEL_SHADOW_RAYS
	int slot = int(lelLights[i].params.w);
	if (rayShadowTrace != 0 && rayShadowLocal != 0 && slot >= 0)
	{
		vec3 toLight = lelLights[i].positionRange.xyz - position;
		float distanceToLight = length(toLight);
		vec3 direction = toLight / max(distanceToLight, 1e-6);
		vec3 origin = LelShadowRayOrigin(position, normal, direction);
		float tMax = distance(lelLights[i].positionRange.xyz, origin) - min(lelLights[i].params.z, 0.5 * distanceToLight);
		return tMax <= 0.0 || LelShadowRayClear(origin, direction, tMax) ? 1.0 : 0.0;
	}
#endif
	return 1.0;
}

// Directional light i's shadow at a point of a surface that is not in the view's depth (transparent): in a variant with
// LEL_SHADOW_RAYS a ray along a traced light (its centre's direction), with what the rays do not see from its maps; the
// maps elsewhere.
vec3 LelDirectionalShadowAtRGB(int i, vec3 position, vec3 normal, vec3 towards)
{
#ifdef LEL_SHADOW_RAYS
	if (rayShadowTrace != 0 && (rayShadowDirectional & (1 << i)) != 0)
	{
		if (!LelShadowRayClear(LelShadowRayOrigin(position, normal, towards), towards, rayShadowLength))
		{
			return vec3(0.0);
		}

		return rayShadowMapRest != 0 ? SampleLightShadowRGB(i, position, normal, towards) : vec3(1.0);
	}
#endif
	return SampleLightShadowRGB(i, position, normal, towards);
}

#endif
