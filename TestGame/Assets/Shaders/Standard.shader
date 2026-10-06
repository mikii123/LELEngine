//Vertex

#version 330
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
out vec2 fTexCoord;
out vec3 fPosition;
out vec3 fNormal;
out mat3 fTBN;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fTexCoord = vec2(vTexCoord.x, 1.0f - vTexCoord.y);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0f));
	vec3 T = normalize(vec3(modelMatrix * vec4(vTangent, 0.0)));
	vec3 B = normalize(vec3(modelMatrix * vec4(vBitangent, 0.0)));
	vec3 N = normalize(vec3(modelMatrix * vec4(vNormal, 0.0)));

	fNormal = N;
	fTBN = transpose(mat3(T, B, N));
}

/////Vertex

//Fragment

#version 330

in vec2 fTexCoord;
in vec3 fPosition;
in vec3 fNormal;
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
uniform sampler2D NormalMap;

// Shadows (set by the engine, see Lighting.SetShadowUniforms)
uniform int shadowsEnabled;
uniform mat4 lightSpaceMatrix;
uniform sampler2DShadow ShadowMap;
uniform float shadowNormalBias;
uniform float shadowDepthBias;

out vec4 FragColor;

float SampleShadow(vec3 worldPos, vec3 worldNormal, vec3 toLight)
{
	if (shadowsEnabled == 0) return 1.0;

	float ndl = clamp(dot(worldNormal, toLight), 0.0, 1.0);
	vec3 offsetPos = worldPos + worldNormal * shadowNormalBias * (1.5 - ndl);
	vec4 ls = lightSpaceMatrix * vec4(offsetPos, 1.0);
	vec3 proj = ls.xyz / ls.w * 0.5 + 0.5;
	if (proj.z > 1.0) return 1.0;
	proj.z -= shadowDepthBias;

	vec2 texel = 1.0 / vec2(textureSize(ShadowMap, 0));
	float sum = 0.0;
	for (int x = -1; x <= 1; x++)
	{
		for (int y = -1; y <= 1; y++)
		{
			sum += texture(ShadowMap, vec3(proj.xy + vec2(x, y) * texel, proj.z));
		}
	}
	return sum / 9.0;
}

void main()
{
	// Textures
	vec4 texColor = texture(DiffuseMap, fTexCoord);
	vec4 texSpec = texture(SpecularMap, fTexCoord);
	vec3 norm = texture(NormalMap, fTexCoord).rgb;
	norm = normalize(norm * 2.0 - 1.0);

	vec3 toLightWorld = normalize(-LDirectional.dirDirection);
	vec3 lightDir = fTBN * toLightWorld;
	vec3 viewDir = fTBN * normalize(LSpecular.viewPos - fPosition);

	float shadow = SampleShadow(fPosition, normalize(fNormal), toLightWorld);

	//Ambient
	vec4 ambient = LAmbient.ambStrength * LAmbient.ambColor * texColor;

	//Diffuse
	float diff = max(dot(norm, lightDir), 0.0);
	vec4 diffuse = diff * LDirectional.dirColor * LDirectional.dirStrength * texColor;

	//Specular
	vec3 halfwayDir = normalize(lightDir + viewDir);
	float spec = pow(max(dot(norm, halfwayDir), 0.0), LSpecular.specShine);
	vec4 specular = LSpecular.specStrength * spec * LDirectional.dirColor * texSpec;

	//Output
	FragColor = ambient + (diffuse + specular) * shadow;
}

/////Fragment
