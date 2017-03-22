/////Vertex

#version 130

// a projection transformation to apply to the vertex' position
uniform mat4 projectionMatrix;

// attributes of our vertex
in vec3 vPosition;
in vec4 vColor;

out vec4 fColor; // must match name in fragment shader

void main()
{
	// gl_Position is a special variable of OpenGL that must be set
	gl_Position = projectionMatrix * vec4(vPosition, 1.0);
	fColor = vColor;
}

/////Fragment

#version 130

in vec4 fColor; // must match name in vertex shader

out vec4 fragColor; // first out variable is automatically written to the screen

void main()
{
	fragColor = fColor;
}