//Vertex

#version 430
invariant gl_Position;

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

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"

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

uniform vec4 Color;

out vec4 FragColor;

void main()
{
	// dirDirection is the direction the light travels; flip it to get the vector towards the light.
	vec3 lightDir = normalize(-LDirectional.dirDirection);
	vec3 viewDir = normalize(LSpecular.viewPos - fPosition);
	vec3 norm = normalize(fNormal);

	float shadow = SampleShadow(fPosition, norm, lightDir);

	//Ambient
	vec4 ambient = LAmbient.ambStrength * LAmbient.ambColor * Color;

	//Diffuse
	float diff = max(dot(norm, lightDir), 0.0);
	vec4 diffuse = diff * LDirectional.dirColor * LDirectional.dirStrength * Color;

	//Specular
	vec3 halfwayDir = normalize(lightDir + viewDir);
	float spec = pow(max(dot(norm, halfwayDir), 0.0), LSpecular.specShine);
	vec4 specular = LSpecular.specStrength * spec * LDirectional.dirColor;

	//Indirect
	vec3 indirect = vec3(0.0);
	float occlusion = 1.0;
	if (giEnabled != 0)
	{
		vec4 diffuseGI = TraceDiffuseCones(fPosition, norm);
		indirect = diffuseGI.rgb * Color.rgb * giDiffuseStrength;
		occlusion = clamp(1.0 - diffuseGI.a * giOcclusionStrength, 0.0, 1.0);
	}

	//Output
	FragColor = vec4(ambient.rgb * occlusion + indirect + (diffuse.rgb + specular.rgb) * shadow, Color.a);
}

/////Fragment
