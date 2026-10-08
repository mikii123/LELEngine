//Vertex

#version 430
invariant gl_Position;

#include "Engine/Instancing.glsl"

// Depth only (shadow maps, depth passes). gl_Position is invariant so it matches the material shaders exactly.
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

void main()
{
}

/////Fragment
