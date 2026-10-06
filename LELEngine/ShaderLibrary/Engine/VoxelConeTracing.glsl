// Voxel cone tracing against the scene radiance volume built by VoxelGIPass.
// Uniforms are set by Lighting.SetGIUniforms.
// Include after #version in a fragment stage: #include "Engine/VoxelConeTracing.glsl"
//
// Material shaders call GetIndirectLighting(). Depending on giResolveMode it either traces the
// cones per pixel or reads the reduced-resolution buffers produced by GIResolvePass.
//
// The volume stores premultiplied radiance (rgb) and occupancy (a); mip levels average both,
// so front-to-back compositing with (1 - alpha) weights is correct.

uniform int giEnabled;
uniform sampler3D VoxelRadiance;
uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;
uniform float giDiffuseStrength;
uniform float giSpecularStrength;
uniform float giOcclusionStrength;
uniform float giConeMaxDistance;

// Screen-space resolve (GIResolvePass)
uniform int giResolveMode;
uniform sampler2D IndirectDiffuse;   // rgb irradiance, a cone coverage
uniform sampler2D IndirectSpecular;  // rgb specular radiance, a linear view depth
uniform vec2 giResolveSize;
uniform vec2 giScreenSize;
uniform mat4 viewMatrix;

float VoxelSize()
{
	return voxelGridSize / float(voxelResolution);
}

float ConeMaxDistance()
{
	return giConeMaxDistance > 0.0 ? giConeMaxDistance : voxelGridSize;
}

// aperture = tan(half angle). Returns premultiplied radiance in rgb and accumulated occlusion in a.
vec4 TraceCone(vec3 origin, vec3 direction, float aperture, float maxDistance)
{
	float voxelSize = VoxelSize();
	float maxMip = log2(float(voxelResolution));

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	float dist = voxelSize;

	while (dist < maxDistance && alpha < 0.95)
	{
		float diameter = max(voxelSize, 2.0 * aperture * dist);
		float mip = min(log2(diameter / voxelSize), maxMip);

		vec3 uvw = (origin + direction * dist - voxelGridMin) / voxelGridSize;
		if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0)))) break;

		vec4 s = textureLod(VoxelRadiance, uvw, mip);
		color += (1.0 - alpha) * s.rgb;
		alpha += (1.0 - alpha) * s.a;

		// Advance one cone diameter: each sample then covers a disjoint slab of the cone,
		// so premultiplied averages are not counted twice.
		dist += diameter;
	}

	return vec4(color, alpha);
}

// Six 60-degree cones over the hemisphere. rgb = indirect irradiance (multiply by albedo), a = occlusion.
vec4 TraceDiffuseCones(vec3 position, vec3 normal)
{
	const vec3 coneDirs[6] = vec3[6](
		vec3(0.0, 1.0, 0.0),
		vec3(0.0, 0.5, 0.866025),
		vec3(0.823639, 0.5, 0.267617),
		vec3(0.509037, 0.5, -0.700629),
		vec3(-0.509037, 0.5, -0.700629),
		vec3(-0.823639, 0.5, 0.267617));
	const float coneWeights[6] = float[6](0.25, 0.15, 0.15, 0.15, 0.15, 0.15);
	const float aperture = 0.57735; // tan(30 deg)

	vec3 helper = abs(normal.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 tangent = normalize(cross(helper, normal));
	vec3 bitangent = cross(normal, tangent);

	// Push the start point off the surface so the cone does not sample its own voxel.
	vec3 origin = position + normal * VoxelSize() * 1.5;
	float maxDistance = ConeMaxDistance();

	vec4 result = vec4(0.0);
	for (int i = 0; i < 6; i++)
	{
		vec3 d = coneDirs[i];
		vec3 direction = normalize(tangent * d.x + normal * d.y + bitangent * d.z);
		result += TraceCone(origin, direction, aperture, maxDistance) * coneWeights[i];
	}

	return result;
}

// Single cone along the mirror direction; roughness in [0,1] widens the cone.
vec3 TraceSpecularCone(vec3 position, vec3 normal, vec3 viewDir, float roughness)
{
	vec3 reflected = reflect(-viewDir, normal);
	float aperture = clamp(roughness, 0.03, 1.0);
	vec3 origin = position + normal * VoxelSize() * 1.5;
	return TraceCone(origin, reflected, aperture, ConeMaxDistance()).rgb;
}

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
