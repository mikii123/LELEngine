﻿//Vertex
#version 330 core
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
	fTexCoord = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
	fPosition = vec3(modelMatrix * vec4(vPosition, 1.0));
	vec3 T = normalize(vec3(modelMatrix * vec4(vTangent, 0.0)));
	vec3 B = normalize(vec3(modelMatrix * vec4(vBitangent, 0.0)));
	vec3 N = normalize(vec3(modelMatrix * vec4(vNormal, 0.0)));
	fNormal = mat3(modelMatrix) * vNormal;
	fTBN = transpose(mat3(T, B, N));
}

/////Vertex

//Fragment

#version 330 core
out vec4 FragColor;
in vec2 fTexCoord;
in vec3 fPosition;
in vec3 fNormal;
in mat3 fTBN;

// material parameters
uniform sampler2D AlbedoMap;
uniform sampler2D NormalMap;
uniform sampler2D MetalRoughMap;
//uniform sampler2D AOMap;

// lights
uniform vec3 lightPosition;
uniform vec4 lightColor;

uniform vec3 CamPosition;
const float Exposure = 1;

const float PI = 3.14159265359;
// ----------------------------------------------------------------------------
// Easy trick to get tangent-normals to world-space to keep PBR code simplified.
// Don't worry if you don't get what's going on; you generally want to do normal 
// mapping the usual way for performance anways; I do plan make a note of this 
// technique somewhere later in the normal mapping tutorial.
vec3 getNormalFromMap()
{
	vec3 tangentNormal = texture(NormalMap, fTexCoord).xyz * 2.0 - 1.0;

	vec3 Q1 = dFdx(fPosition);
	vec3 Q2 = dFdy(fPosition);
	vec2 st1 = dFdx(fTexCoord);
	vec2 st2 = dFdy(fTexCoord);

	vec3 N = normalize(fNormal);
	vec3 T = normalize(Q1*st2.t - Q2*st1.t);
	vec3 B = -normalize(cross(N, T));
	mat3 TBN = mat3(T, B, N);

	return normalize(TBN * tangentNormal);
}
// ----------------------------------------------------------------------------
float DistributionGGX(vec3 N, vec3 H, float roughness)
{
	float a = roughness*roughness;
	float a2 = a*a;
	float NdotH = max(dot(N, H), 0.0);
	float NdotH2 = NdotH*NdotH;

	float nom = a2;
	float denom = (NdotH2 * (a2 - 1.0) + 1.0);
	denom = PI * denom * denom;

	return nom / denom;
}
// ----------------------------------------------------------------------------
float GeometrySchlickGGX(float NdotV, float roughness)
{
	float r = (roughness + 1.0);
	float k = (r*r) / 8.0;

	float nom = NdotV;
	float denom = NdotV * (1.0 - k) + k;

	return nom / denom;
}
// ----------------------------------------------------------------------------
float GeometrySmith(vec3 N, vec3 V, vec3 L, float roughness)
{
	float NdotV = max(dot(N, V), 0.0);
	float NdotL = max(dot(N, L), 0.0);
	float ggx2 = GeometrySchlickGGX(NdotV, roughness);
	float ggx1 = GeometrySchlickGGX(NdotL, roughness);

	return ggx1 * ggx2;
}
// ----------------------------------------------------------------------------
vec3 fresnelSchlick(float cosTheta, vec3 F0)
{
	return F0 + (1.0 - F0) * pow(1.0 - cosTheta, 5.0);
}
// ----------------------------------------------------------------------------
void main()
{
	vec3 albedo = pow(texture(AlbedoMap, fTexCoord).rgb, vec3(2.2));
	float metallic = texture(MetalRoughMap, fTexCoord).r;
	float roughness = 1-texture(MetalRoughMap, fTexCoord).a;
	//float ao = texture(AOMap, fTexCoord).r;

	vec3 N = getNormalFromMap();
	vec3 V = normalize(CamPosition - fPosition);

	// calculate reflectance at normal incidence; if dia-electric (like plastic) use F0 
	// of 0.04 and if it's a metal, use their albedo color as F0 (metallic workflow)    
	vec3 F0 = vec3(0.04);
	F0 = mix(F0, albedo, metallic);

	// reflectance equation
	vec3 Lo = vec3(0.0);

	// calculate per-light radiance
	vec3 L = normalize(-lightPosition);
	vec3 H = normalize(V + L);
	//float distance = (length(lightPosition - fPosition));
	//float attenuation = 1.0 / (distance * distance);
	float attenuation = 0.01;
	vec3 radiance = lightColor.rgb * attenuation;

	// Cook-Torrance BRDF
	float NDF = DistributionGGX(N, H, roughness);
	float G = GeometrySmith(N, V, L, roughness);
	vec3 F = fresnelSchlick(max(dot(H, V), 0.0), F0);

	vec3 nominator = NDF * G * F;
	float denominator = 4 * max(dot(N, V), 0.0) * max(dot(N, L), 0.0) + 0.001; // 0.001 to prevent divide by zero.
	vec3 brdf = nominator / denominator;

	// kS is equal to Fresnel
	vec3 kS = F;
	// for energy conservation, the diffuse and specular light can't
	// be above 1.0 (unless the surface emits light); to preserve this
	// relationship the diffuse component (kD) should equal 1.0 - kS.
	vec3 kD = vec3(1.0) - kS;
	// multiply kD by the inverse metalness such that only non-metals 
	// have diffuse lighting, or a linear blend if partly metal (pure metals
	// have no diffuse light).
	kD *= 1.0 - metallic;

	// scale light by NdotL
	float NdotL = max(dot(N, L), 0.0);

	// add to outgoing radiance Lo
	Lo = (kD * albedo / PI + brdf) * radiance * NdotL;  // note that we already multiplied the BRDF by the Fresnel (kS) so we won't multiply by kS again

	// ambient lighting (note that the next IBL tutorial will replace 
	// this ambient lighting with environment lighting).
	vec3 ambient = vec3(0.03) * albedo;// *ao;

	vec3 color = ambient + Lo;

	// HDR tonemapping
	color = color / (color + vec3(1.0));
	// gamma correct
	color = pow(color, vec3(1.0 / 2.2));

	FragColor = vec4(color, 1.0);
}

/////Fragment