//Vertex

#version 130

// a transformation to apply to the vertex' position
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

// Vertex Attributes
in vec3 vPosition;
in vec4 vColor;
in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vTangent;
in vec3 vBitangent;

out vec4 fColor; // must match name in fragment shader

void main()
{
	// gl_Position is a special variable of OpenGL that must be set
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fColor = vColor;
}

/////Vertex

//Fragment

#version 130

in vec4 fColor; // must match name in vertex shader

out vec4 FragColor;

void main()
{
	FragColor = fColor;
}

/////Fragment