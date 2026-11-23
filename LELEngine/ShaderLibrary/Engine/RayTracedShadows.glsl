// Traced shadows on opaque surfaces for the lit materials' fragment stages (Rendering.Passes.RayTracedShadowPass,
// bound by Lighting.SetRayShadowUniforms): per pixel of the lighting camera's view, how much of each traced light
// reaches the surface there, in a 2D array texture (Lighting.RayShadowBinding): first the local lights' slots (slot s:
// layer s / 4, channel s % 4; a light's slot is params.w of its list entry, -1: none), then a layer of the directional
// lights (light i: channel i). Draws of other views, and transparent surfaces (not in the view's depth), are given none.
// The mask exists on Vulkan only; elsewhere the functions are the plain lights' (no local shadows, the shadow maps).
// Include after Engine/Lights.glsl and Engine/Shadows.glsl.
#ifndef LEL_RAY_TRACED_SHADOWS_GLSL
#define LEL_RAY_TRACED_SHADOWS_GLSL

#ifdef LEL_VULKAN
uniform sampler2DArray RayShadowMask;
uniform int rayShadowLocal;             // 1: the local lights' slots are traced
uniform int rayShadowDirectional;       // bit i: directional light i is traced (channel i of the directional layer)
uniform int rayShadowDirectionalLayer;
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

// Directional light i's shadow at this fragment: traced, or from its shadow maps (Engine/Shadows.glsl: per channel,
// tinted by transparent casters).
vec3 LelDirectionalShadowRGB(int i, vec3 position, vec3 normal, vec3 towards)
{
#ifdef LEL_VULKAN
	if ((rayShadowDirectional & (1 << i)) != 0)
	{
		return vec3(texelFetch(RayShadowMask, ivec3(ivec2(gl_FragCoord.xy), rayShadowDirectionalLayer), 0)[i]);
	}
#endif
	return SampleLightShadowRGB(i, position, normal, towards);
}

#endif
