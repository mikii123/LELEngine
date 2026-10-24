//Properties
// Unlit: Base Map x Base Color x vertex color, nothing lights it (Unity's Particles/Unlit). Blending Mode, Render Face,
// Alpha Clipping and Refraction are keywords: each combination a material picks is a variant of its own, with the render
// state below (compiled on first use; game builds compile the ones their materials use).
BaseMap ("Base Map", 2D) = "white" {}
[HDR] Color ("Base Color", Color) = (1, 1, 1, 1)
[KeywordEnum(Alpha, Premultiply, Additive, Soft Additive, Multiply, Opaque)] BlendMode ("Blending Mode", Float) = 0
[KeywordEnum(Both, Front, Back)] RenderFace ("Render Face", Float) = 0
[Toggle(ALPHA_CLIP)] AlphaClip ("Alpha Clipping", Float) = 0
Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
// Refraction (Unity's particle Distortion): the scene behind, from a copy taken right before the object draws
// (GrabPass), shifted by the normal map's tilt and fading with the alpha. Refraction Blend gives the dense parts back to
// the particle's own color (0: wherever it has alpha, 1: refraction only). Objects in front are never shifted into it.
[Toggle(REFRACTION)] Refraction ("Refraction", Float) = 0
NormalMap ("Normal Map (Refraction)", 2D) = "bump" {}
RefractionStrength ("Refraction Strength (world units)", Float) = 0.1
RefractionBlend ("Refraction Blend", Range(0, 1)) = 0.5
// Light-emitting or see-through: no shadows in any mode (no ShadowCaster pass, as Unity's).
ShadowCaster Off
#if REFRACTION
GrabPass RefractionStrength
#endif
#if BLENDMODE_OPAQUE
#if ALPHA_CLIP || REFRACTION
// Cutout or refraction: an alpha-tested surface is not in the depth prepass and the scene copy exists only in the
// transparent pass, so it draws there right after the opaques (no blending, depth written).
Queue GeometryLast+1
#endif
#else
ZWrite Off
Queue Transparent
#endif
#if BLENDMODE_ALPHA
Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
#elif BLENDMODE_PREMULTIPLY
Blend One OneMinusSrcAlpha
#elif BLENDMODE_ADDITIVE
Blend SrcAlpha One, Zero One
#elif BLENDMODE_SOFT_ADDITIVE || BLENDMODE_MULTIPLY
#if REFRACTION
// Refraction: the shader blends over the refracted scene itself and adds the difference to the scene there.
Blend One One, Zero One
#elif BLENDMODE_SOFT_ADDITIVE
Blend One OneMinusSrcColor, Zero One
#else
Blend DstColor Zero, Zero One
#endif
#endif
#if RENDERFACE_FRONT
Cull Back
#elif RENDERFACE_BACK
Cull Front
#else
Cull Off
#endif
/////Properties

//Vertex

#version 430
invariant gl_Position;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

in vec3 vPosition;
in vec4 vColor;
in vec2 vTexCoord;
#ifdef REFRACTION
in vec3 vTangent;
in vec3 vBitangent;
#endif

out vec2 fTexCoord;
out vec4 fColor;
#ifdef REFRACTION
out vec3 fTangent;
out vec3 fBitangent;
#endif

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fTexCoord = vTexCoord;
	fColor = vColor;
#ifdef REFRACTION
	// The surface's directions in the world: the normal map's tilt becomes a world vector across the surface.
	fTangent = mat3(modelMatrix) * vTangent;
	fBitangent = mat3(modelMatrix) * vBitangent;
#endif
}

/////Vertex

//Fragment

#version 430

#ifdef REFRACTION
#include "Engine/GrabPass.glsl"
#endif

in vec2 fTexCoord;
in vec4 fColor;
#ifdef REFRACTION
in vec3 fTangent;
in vec3 fBitangent;
#endif

uniform sampler2D BaseMap;
uniform vec4 Color;
uniform float Cutoff;
#ifdef REFRACTION
uniform sampler2D NormalMap;
uniform float RefractionStrength;
uniform float RefractionBlend;
#endif

out vec4 FragColor;

void main()
{
	vec4 texel = texture(BaseMap, fTexCoord);
	vec4 tint = Color * fColor;
	vec4 color = texel * tint;

#ifdef ALPHA_CLIP
	if (color.a < Cutoff)
	{
		discard;
	}
#endif

#ifdef REFRACTION
	// The normal map's tilt across the surface (from behind, a bump is a dent), the scene behind shifted by it with
	// a strength that fades with the alpha, and how much of the particle's own color covers it.
	vec3 mapped = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;
	vec3 tangent = dot(fTangent, fTangent) > 1e-12 ? normalize(fTangent) : vec3(0.0);
	vec3 bitangent = dot(fBitangent, fBitangent) > 1e-12 ? normalize(fBitangent) : vec3(0.0);
	vec3 bend = (mapped.x * tangent + mapped.y * bitangent) * (gl_FrontFacing ? 1.0 : -1.0);
	vec3 behind = LelSceneRefraction(bend, RefractionStrength * color.a);
	float own = clamp(color.a - RefractionBlend, 0.0, 1.0);
#if defined(BLENDMODE_ALPHA)
	color.rgb = mix(behind, color.rgb, own);
#elif defined(BLENDMODE_PREMULTIPLY)
	// The map is premultiplied already: the refracted scene comes in with the particle's coverage.
	color.rgb = behind * (color.a * (1.0 - own)) + texel.rgb * tint.rgb * tint.a * own;
#elif defined(BLENDMODE_ADDITIVE)
	// Blend SrcAlpha One adds to the scene as it is here: the refracted scene comes in as its difference from that.
	color.rgb = color.rgb * own + (behind - LelSampleGrab(LelGrabUV()).rgb) * (1.0 - own);
#elif defined(BLENDMODE_SOFT_ADDITIVE) || defined(BLENDMODE_MULTIPLY)
	// Blend One One: the mode's own blend over the refracted scene, minus the scene here that the blend adds it to.
	vec3 here = LelSampleGrab(LelGrabUV()).rgb;
	vec3 refracted = mix(here, behind, color.a * (1.0 - own));
#if defined(BLENDMODE_SOFT_ADDITIVE)
	vec3 glow = color.rgb * (color.a * own);
	color.rgb = glow + (1.0 - glow) * refracted - here;
#else
	color.rgb = mix(vec3(1.0), color.rgb, color.a * own) * refracted - here;
#endif
#elif defined(BLENDMODE_OPAQUE)
	color = vec4(mix(behind, color.rgb, own), 1.0);
#endif
#else
#if defined(BLENDMODE_PREMULTIPLY)
	// The map is premultiplied already (its color is not scaled by its alpha again): the tint's and the vertex
	// color's alpha fade all of it, and color where the map's alpha is 0 adds light.
	color.rgb = texel.rgb * tint.rgb * tint.a;
#elif defined(BLENDMODE_SOFT_ADDITIVE)
	color.rgb *= color.a;
#elif defined(BLENDMODE_MULTIPLY)
	// Transparent parts multiply by white (leave the background as it is).
	color.rgb = mix(vec3(1.0), color.rgb, color.a);
#elif defined(BLENDMODE_OPAQUE)
	color.a = 1.0;
#endif
#endif

	FragColor = color;
}

/////Fragment
