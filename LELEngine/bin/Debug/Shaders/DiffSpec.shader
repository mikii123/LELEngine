﻿//Vertex

#version 330

// a transformation to apply to the vertex' position
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

// Vertex Attributes
in vec3 vPosition;
in vec3 vColor;
in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vTangent;
in vec3 vBitangent;

// Out for fragment shader
out vec2 fTexCoord;
out vec3 fNormal;
out vec3 fPosition;
out mat3 fTBN;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fTexCoord = vec2(vTexCoord.x, 1.0f - vTexCoord.y);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0f));
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
	vec3 T = normalize(vec3(modelMatrix * vec4(vTangent, 0.0)));
	vec3 B = normalize(vec3(modelMatrix * vec4(vBitangent, 0.0)));
	vec3 N = normalize(vec3(modelMatrix * vec4(vNormal, 0.0)));

	fTBN = transpose(mat3(T, B, N));
}

/////Vertex

//Fragment

#version 330

in vec2 fTexCoord;
in vec3 fNormal;
in vec3 fPosition;
in mat3 fTBN;

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
uniform sampler2D DiffuseMap;
uniform sampler2D SpecularMap;

void main()
{
	// Textures
	vec4 texColor = texture(DiffuseMap, fTexCoord);
	vec4 texSpec = texture(SpecularMap, fTexCoord);

	vec3 lightDir = normalize(-LDirectional.dirDirection);
	vec3 viewDir = normalize(LSpecular.viewPos - fPosition);

	//Ambient
	vec4 ambient = LAmbient.ambStrength * LAmbient.ambColor * texColor;

	//Diffuse
	vec3 norm = normalize(fNormal);
	float diff = max(dot(norm, lightDir), 0.0);
	vec4 diffuse = diff * LDirectional.dirColor * LDirectional.dirStrength * texColor;

	//Specular
	vec3 halfwayDir = normalize(lightDir + viewDir);
	float spec = pow(max(dot(norm, halfwayDir), 0.0), LSpecular.specShine);
	vec4 specular = LSpecular.specStrength * spec * LDirectional.dirColor * texSpec;

	//Output
	gl_FragColor = (ambient + diffuse + specular);
}

/////Fragment