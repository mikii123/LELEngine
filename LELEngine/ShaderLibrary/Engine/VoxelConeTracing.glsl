// Voxel cone tracing for material shaders (fragment stage only).
// Uniforms are set by Lighting.SetGIUniforms.
// Include after #version: #include "Engine/VoxelConeTracing.glsl"
//
// Material shaders call GetIndirectLighting(). Depending on giResolveMode it either traces the
// cones per pixel or reads the reduced-resolution buffers produced by GIResolvePass.

#include "Engine/VoxelConeTracingCore.glsl"

uniform int giEnabled;

// Screen-space resolve (GIResolvePass)
uniform int giResolveMode;
uniform sampler2D IndirectDiffuse;   // rgb irradiance, a cone coverage
uniform sampler2D IndirectSpecular;  // rgb specular radiance, a linear view depth
uniform vec2 giResolveSize;
uniform vec2 giScreenSize;
uniform mat4 viewMatrix;

// Depth-aware bilinear upsample of the reduced-resolution GI buffers for the current fragment.
void SampleResolvedIndirect(vec3 position, out vec4 diffuseCoverage, out vec3 specular)
{
	vec2 uv = gl_FragCoord.xy / giScreenSize;
	float depth = -(viewMatrix * vec4(position, 1.0)).z;

	vec2 texelPos = uv * giResolveSize - 0.5;
	ivec2 base = ivec2(floor(texelPos));
	vec2 f = fract(texelPos);
	ivec2 maxCoord = ivec2(giResolveSize) - 1;

	vec4 accDiffuse = vec4(0.0);
	vec3 accSpecular = vec3(0.0);
	float weightSum = 0.0;

	for (int i = 0; i < 4; i++)
	{
		ivec2 offset = ivec2(i & 1, i >> 1);
		float bilinear = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y);
		ivec2 coord = clamp(base + offset, ivec2(0), maxCoord);

		vec4 s = texelFetch(IndirectSpecular, coord, 0);
		// Reject samples from surfaces at a clearly different depth (edges between objects).
		float depthWeight = max(0.0, 1.0 - abs(s.a - depth) / (0.05 * depth + 0.02));
		float w = bilinear * depthWeight;

		accDiffuse += texelFetch(IndirectDiffuse, coord, 0) * w;
		accSpecular += s.rgb * w;
		weightSum += w;
	}

	if (weightSum < 1e-4)
	{
		// No depth-compatible neighbour (thin geometry): plain bilinear is better than black.
		diffuseCoverage = texture(IndirectDiffuse, uv);
		specular = texture(IndirectSpecular, uv).rgb;
		return;
	}

	diffuseCoverage = accDiffuse / weightSum;
	specular = accSpecular / weightSum;
}

// Entry point for material shaders.
//   diffuse   : indirect irradiance, multiply by albedo
//   occlusion : ambient occlusion factor for the flat ambient term
//   specular  : indirect specular radiance, multiply by the specular color / strength
void GetIndirectLighting(vec3 position, vec3 normal, vec3 viewDir, float roughness, out vec3 diffuse, out float occlusion, out vec3 specular)
{
	diffuse = vec3(0.0);
	occlusion = 1.0;
	specular = vec3(0.0);

	if (giEnabled == 0) return;

	vec4 diffuseCoverage;
	vec3 specularRadiance;
	if (giResolveMode != 0)
	{
		SampleResolvedIndirect(position, diffuseCoverage, specularRadiance);
	}
	else
	{
		diffuseCoverage = TraceDiffuseCones(position, normal);
		specularRadiance = TraceSpecularCone(position, normal, viewDir, roughness);
	}

	diffuse = diffuseCoverage.rgb * giDiffuseStrength;
	occlusion = clamp(1.0 - diffuseCoverage.a * giOcclusionStrength, 0.0, 1.0);
	specular = specularRadiance * giSpecularStrength;
}
