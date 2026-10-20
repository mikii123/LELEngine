//Vertex

#version 430
invariant gl_Position;

#define LEL_PREVIOUS_MODELS 1
#include "Engine/Instancing.glsl"

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;

in vec3 vPosition;
in vec3 vNormal;

out vec3 fNormal;
out vec3 fMotion;

void main()
{
	mat4 model = LelModelMatrix();
	// Exactly the material shaders' expression: the opaque pass tests LEQUAL against this depth without writing its
	// own, and invariant only holds for identical expressions (P * V * (M * v) rounds differently, the sky shows through).
	gl_Position = projectionMatrix * viewMatrix * model * vec4(vPosition, 1.0);
	vec4 world = model * vec4(vPosition, 1.0);
	fNormal = LelNormalMatrix() * vNormal;
	// World-space displacement since last frame: affine over a triangle of a rigid object, so the interpolation is exact.
	fMotion = world.xyz - (LelPreviousModelMatrix() * vec4(vPosition, 1.0)).xyz;
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#include "Engine/Instancing.glsl"

in vec3 fNormal;
in vec3 fMotion;

layout(location = 0) out vec4 NormalRoughness;
// xyz: how far the surface point moved in world space since the previous frame (current - previous), w: 1 when it moved.
layout(location = 1) out vec4 Motion;

void main()
{
	// Conventional material parameter (0 = material default): per draw, or from the material table.
	NormalRoughness = vec4(normalize(fNormal), LelRoughness());
	Motion = vec4(fMotion, dot(fMotion, fMotion) > 1e-12 ? 1.0 : 0.0);
}

/////Fragment
