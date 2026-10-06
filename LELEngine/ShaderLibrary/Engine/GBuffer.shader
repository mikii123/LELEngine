//Vertex

#version 430
invariant gl_Position;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

in vec3 vPosition;
in vec3 vNormal;

out vec3 fNormal;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
}

/////Vertex

//Fragment

#version 430

in vec3 fNormal;

// Conventional material parameter, set per draw by the prepass (0 = material default).
uniform float Roughness;

out vec4 NormalRoughness;

void main()
{
	NormalRoughness = vec4(normalize(fNormal), Roughness);
}

/////Fragment
