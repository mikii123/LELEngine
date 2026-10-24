//Properties
// Unlit: Base Map x Base Color x vertex color, nothing lights it (Unity's Particles/Unlit). Blending Mode, Render Face
// and Alpha Clipping are keywords: each combination a material picks is a variant of its own, with the render state
// below (compiled on first use; game builds compile the ones their materials use).
BaseMap ("Base Map", 2D) = "white" {}
[HDR] Color ("Base Color", Color) = (1, 1, 1, 1)
[KeywordEnum(Alpha, Premultiply, Additive, Soft Additive, Multiply, Opaque)] BlendMode ("Blending Mode", Float) = 0
[KeywordEnum(Both, Front, Back)] RenderFace ("Render Face", Float) = 0
[Toggle(ALPHA_CLIP)] AlphaClip ("Alpha Clipping", Float) = 0
Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
// Light-emitting or see-through: no shadows in any mode (no ShadowCaster pass, as Unity's).
ShadowCaster Off
#if BLENDMODE_OPAQUE
#if ALPHA_CLIP
// Cutout: an alpha-tested surface is not in the depth prepass, so it draws in the transparent pass right after the
// opaques (no blending, depth written).
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
#elif BLENDMODE_SOFT_ADDITIVE
Blend One OneMinusSrcColor, Zero One
#elif BLENDMODE_MULTIPLY
Blend DstColor Zero, Zero One
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

out vec2 fTexCoord;
out vec4 fColor;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fTexCoord = vTexCoord;
	fColor = vColor;
}

/////Vertex

//Fragment

#version 430

in vec2 fTexCoord;
in vec4 fColor;

uniform sampler2D BaseMap;
uniform vec4 Color;
uniform float Cutoff;

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

	FragColor = color;
}

/////Fragment
