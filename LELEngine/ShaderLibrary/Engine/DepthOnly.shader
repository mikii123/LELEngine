//Vertex

#version 430
invariant gl_Position;

#include "Engine/Instancing.glsl"

// Depth only (shadow maps, depth passes). gl_Position is invariant so it matches the material shaders exactly.
// Variants: LEL_INSTANCED (GPU-driven draws), LEL_SHADOW_TRANSMITTANCE (transparent shadow casters, see the fragment stage).
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;

in vec3 vPosition;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * LelModelMatrix() * vec4(vPosition, 1.0);
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#ifdef LEL_SHADOW_TRANSMITTANCE
// Transparent shadow casters (Rendering.TransparentShadows): the light the caster lets through, blended
// multiplicatively into the shadow map's transmittance attachment, and its depth (blended with a minimum).
uniform vec4 shadowTint; // the caster's Color: rgb tint, a opacity

out vec4 Transmittance;
#endif

void main()
{
#ifdef LEL_SHADOW_TRANSMITTANCE
	Transmittance = vec4(mix(vec3(1.0), clamp(shadowTint.rgb, 0.0, 1.0), clamp(shadowTint.a, 0.0, 1.0)), gl_FragCoord.z);
#endif
}

/////Fragment
