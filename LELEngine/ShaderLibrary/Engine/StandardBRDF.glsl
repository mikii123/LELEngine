// Unity's Standard BRDF (BRDF1_Unity_PBS of UnityStandardBRDF.cginc, linear colour space) for the physically based
// workflows of Engine/LitTextured.shader: Disney diffuse, GGX with Smith joint visibility, Schlick Fresnel. Unity's units
// are the engine's: a light carries no 1 / pi (a white Lambert surface facing a light of strength 1 shows 1), so the
// specular term is scaled by pi as Unity does. Include after #version: #include "Engine/StandardBRDF.glsl"
#ifndef LEL_STANDARD_BRDF_GLSL
#define LEL_STANDARD_BRDF_GLSL

// unity_ColorSpaceDielectricSpec in linear space: the reflectance of non-metals at normal incidence.
const float LelDielectricSpecular = 0.04;

// Metallic workflow (DiffuseAndSpecularFromMetallic): metals reflect their albedo and scatter nothing.
vec3 LelDiffuseAndSpecularFromMetallic(vec3 albedo, float metallic, out vec3 specColor, out float oneMinusReflectivity)
{
	specColor = mix(vec3(LelDielectricSpecular), albedo, metallic);
	oneMinusReflectivity = (1.0 - LelDielectricSpecular) - metallic * (1.0 - LelDielectricSpecular);
	return albedo * oneMinusReflectivity;
}

// Specular workflow (EnergyConservationBetweenDiffuseAndSpecular, monochrome as Unity on desktop): what the strongest
// specular channel reflects is not scattered.
vec3 LelEnergyConservation(vec3 albedo, vec3 specColor, out float oneMinusReflectivity)
{
	oneMinusReflectivity = 1.0 - max(max(specColor.r, specColor.g), specColor.b);
	return albedo * oneMinusReflectivity;
}

float LelPow5(float x)
{
	float x2 = x * x;
	return x2 * x2 * x;
}

// Light arriving at the surface from the GI (Unity's UnityIndirect): diffuse irradiance and the reflected radiance.
struct LelIndirect
{
	vec3 diffuse;
	vec3 specular;
};

// perceptualRoughness = 1 - smoothness (the material's Roughness); lightColor already shadowed.
vec3 LelStandardBRDF(vec3 diffColor, vec3 specColor, float oneMinusReflectivity, float perceptualRoughness, vec3 N, vec3 V, vec3 L, vec3 lightColor, LelIndirect gi)
{
	vec3 H = normalize(L + V);
	// The abs limits the artifacts of normals facing away from the viewer (Unity's choice).
	float nv = abs(dot(N, V));
	float nl = clamp(dot(N, L), 0.0, 1.0);
	float nh = clamp(dot(N, H), 0.0, 1.0);
	float lh = clamp(dot(L, H), 0.0, 1.0);

	// Disney diffuse: two Schlick terms with a retro-reflection that grows with the roughness.
	float fd90 = 0.5 + 2.0 * lh * lh * perceptualRoughness;
	float diffuseTerm = (1.0 + (fd90 - 1.0) * LelPow5(1.0 - nl)) * (1.0 + (fd90 - 1.0) * LelPow5(1.0 - nv)) * nl;

	// GGX (roughness = perceptual roughness squared, at least 0.002 as Unity / HDRP) with Unity's approximation of the
	// Smith joint visibility term.
	float roughness = max(perceptualRoughness * perceptualRoughness, 0.002);
	float lambdaV = nl * (nv * (1.0 - roughness) + roughness);
	float lambdaL = nv * (nl * (1.0 - roughness) + roughness);
	float visibility = 0.5 / (lambdaV + lambdaL + 1e-5);
	float a2 = roughness * roughness;
	float d = (nh * a2 - nh) * nh + 1.0;
	float distribution = 0.31830988618 * a2 / (d * d + 1e-7);
	float specularTerm = max(0.0, visibility * distribution * 3.14159265359 * nl);
	// A specular colour of zero gives true Lambert.
	specularTerm *= any(greaterThan(specColor, vec3(0.0))) ? 1.0 : 0.0;

	// Reflections of rough surfaces lose energy at grazing angles (1 / (roughness^2 + 1)); the reflectance rises to the
	// grazing term towards the silhouette.
	float surfaceReduction = 1.0 / (roughness * roughness + 1.0);
	float grazingTerm = clamp(1.0 - perceptualRoughness + (1.0 - oneMinusReflectivity), 0.0, 1.0);
	vec3 fresnel = specColor + (1.0 - specColor) * LelPow5(1.0 - lh);
	vec3 fresnelLerp = mix(specColor, vec3(grazingTerm), LelPow5(1.0 - nv));

	return diffColor * (gi.diffuse + lightColor * diffuseTerm)
		+ specularTerm * lightColor * fresnel
		+ surfaceReduction * gi.specular * fresnelLerp;
}

#endif
