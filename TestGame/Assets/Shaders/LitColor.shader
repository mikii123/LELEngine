//Vertex

#version 330

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

// Out for fragment shader
out vec3 fNormal;
out vec3 fPosition;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0f));
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
}

/////Vertex

//Fragment

#version 330

in vec3 fNormal;
in vec3 fPosition;

// Directional
struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};

// Ambient
struct Ambient {
	vec4 ambColor;
	float ambStrength;
};

// Specular
struct Specular {
	float specStrength;
	float specShine;
	vec3 viewPos;
};

// Light
uniform Directional LDirectional;
uniform Ambient LAmbient;
uniform Specular LSpecular;

// Textures
uniform vec4 Color;

out vec4 FragColor;

void main()
{
	vec3 lightDir = normalize(LDirectional.dirDirection);
	vec3 viewDir = normalize(LSpecular.viewPos - fPosition);
	vec3 norm = normalize(fNormal);

	//Ambient
	vec4 ambient = LAmbient.ambStrength * LAmbient.ambColor * Color;

	//Diffuse
	float diff = max(dot(norm, lightDir), 0.0);
	vec4 diffuse = diff * LDirectional.dirColor * LDirectional.dirStrength * Color;

	//Specular
	vec3 halfwayDir = normalize(-lightDir + viewDir);
	float spec = pow(max(dot(norm, halfwayDir), 0.0), LSpecular.specShine);
	vec4 specular = LSpecular.specStrength * spec * LDirectional.dirColor;

	//Output
	FragColor = (ambient + diffuse + specular);
}

/////Fragment