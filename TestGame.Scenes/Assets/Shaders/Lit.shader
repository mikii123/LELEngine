//Vertex

#version 330
invariant gl_Position;

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

in vec3 vPosition;
in vec4 vColor;
in vec2 vTexCoord;
in vec3 vNormal;
in vec3 vTangent;
in vec3 vBitangent;

out vec3 fNormal;
out vec3 fPosition;
out vec2 fTexCoord;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0));
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
	fTexCoord = vTexCoord;
}

/////Vertex

//Fragment

#version 330

in vec3 fNormal;
in vec3 fPosition;
in vec2 fTexCoord;

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};

struct Ambient {
	vec4 ambColor;
	float ambStrength;
};

struct Specular {
	float specStrength;
	float specShine;
	vec3 viewPos;
};

uniform Directional LDirectional;
uniform Ambient LAmbient;
uniform Specular LSpecular;

// Material
uniform vec4 Color;

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

	// 3x3 PCF on top of hardware 2x2 comparison filtering.
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
	vec3 N = normalize(fNormal);
	vec3 L = normalize(-LDirectional.dirDirection);
	vec3 V = normalize(LSpecular.viewPos - fPosition);
	vec3 H = normalize(L + V);

	float shadow = SampleShadow(fPosition, N, L);

	vec3 albedo = Color.rgb;
	vec3 lightColor = LDirectional.dirColor.rgb * LDirectional.dirStrength;

	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;

	float ndl = max(dot(N, L), 0.0);
	vec3 diffuse = albedo * lightColor * ndl;

	float spec = pow(max(dot(N, H), 0.0), LSpecular.specShine) * LSpecular.specStrength;
	vec3 specular = lightColor * spec * ndl;

	vec3 color = ambient + (diffuse + specular) * shadow;
	FragColor = vec4(color, Color.a);
}

/////Fragment
