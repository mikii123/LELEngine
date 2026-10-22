//Vertex

#version 430

uniform mat4 modelMatrix;

in vec3 vPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec3 gWorldPos;
out vec3 gNormal;
out vec2 gUV;

void main()
{
	gWorldPos = vec3(modelMatrix * vec4(vPosition, 1.0));
	gNormal = normalize(mat3(transpose(inverse(modelMatrix))) * vNormal);
	gUV = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
}

/////Vertex

//Geometry

#version 430

// Projects each triangle along its dominant axis so it covers the most voxel columns, mapping the voxel grid to
// clip space [-1, 1]; the fragment shader needs the axis to turn the pixel and the depth back into voxels.
layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;

uniform vec3 voxelGridMin;
uniform float voxelGridSize;

in vec3 gWorldPos[];
in vec3 gNormal[];
in vec2 gUV[];

centroid out vec3 fWorldPos;
centroid out vec3 fNormal;
centroid out vec2 fUV;
flat out int fAxis;

void main()
{
	vec3 faceNormal = abs(cross(gWorldPos[1] - gWorldPos[0], gWorldPos[2] - gWorldPos[0]));
	int axis = (faceNormal.x > faceNormal.y && faceNormal.x > faceNormal.z) ? 0 : (faceNormal.y > faceNormal.z ? 1 : 2);

	for (int i = 0; i < 3; i++)
	{
		vec3 p = (gWorldPos[i] - voxelGridMin) / voxelGridSize * 2.0 - 1.0;
		vec2 projected = axis == 0 ? p.yz : (axis == 1 ? p.xz : p.xy);

		gl_Position = vec4(projected, 0.0, 1.0);
		fWorldPos = gWorldPos[i];
		fNormal = gNormal[i];
		fUV = gUV[i];
		fAxis = axis;
		EmitVertex();
	}
	EndPrimitive();
}

/////Geometry

//Fragment

#version 430

// Coverage voxelization into accumulators (Engine/VoxelResolveGeometry.shader turns them into the geometry volume).
// Rasterized with multisampling: a fragment stands for the covered part of its voxel column (covered samples /
// samples), split between the voxels nearest to the surface along the projection axis (a tent over two, or a
// quadratic B-spline over three). The weights of a surface crossing the volume sum to its covered area in voxel
// faces, so a moving object's occupancy and colour change continuously. Sums are 16-bit fixed point (1/256) added
// with integer atomics: order independent, the same result every frame. Emission is HDR: summed as RGB9E5 by
// compare-and-swap.
layout(binding = 0, r32ui) uniform uimage3D AccAlbedoRG;   // albedo r, g x weight
layout(binding = 1, r32ui) uniform uimage3D AccAlbedoBW;   // albedo b x weight, weight
layout(binding = 2, r32ui) uniform uimage3D AccNormalXY;   // (normal x, y * 0.5 + 0.5) x weight
layout(binding = 3, r32ui) uniform uimage3D AccNormalZ;    // (normal z * 0.5 + 0.5) x weight
layout(binding = 4, r32ui) uniform uimage3D AccEmission;   // emission x weight, RGB9E5
layout(binding = 5, r8) uniform writeonly image3D BlocksOut; // 1 where the 8^3 block has geometry

uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;
uniform int voxSamples;    // samples per pixel of the rasterization (0 or 1: full coverage)
uniform int voxSplatWidth; // 2: linear tent over the two nearest voxels, 3: quadratic B-spline over three

// Material data injected per draw by VoxelGIPass
uniform vec4 voxAlbedo;
uniform vec4 voxEmissive;      // rgb color, a intensity
uniform sampler2D voxAlbedoMap;
uniform int voxUseAlbedoMap;
uniform sampler2D voxEmissionMap; // multiplies the emission colour (texture unit 1)
uniform int voxUseEmissionMap;

centroid in vec3 fWorldPos;
centroid in vec3 fNormal;
centroid in vec2 fUV;
flat in int fAxis;

const float FixedPoint = 256.0;

uint EncodeRGB9E5(vec3 rgb)
{
	const float maxValue = 65408.0;
	vec3 c = clamp(rgb, 0.0, maxValue);
	float maxChannel = max(c.r, max(c.g, c.b));
	int exponent = max(-16, int(floor(log2(max(maxChannel, 1e-30))))) + 16;
	float denominator = exp2(float(exponent - 24));
	if (int(floor(maxChannel / denominator + 0.5)) == 512)
	{
		denominator *= 2.0;
		exponent += 1;
	}

	uvec3 m = uvec3(floor(c / denominator + 0.5));
	return m.r | (m.g << 9) | (m.b << 18) | (uint(exponent) << 27);
}

vec3 DecodeRGB9E5(uint v)
{
	return vec3(uvec3(v, v >> 9, v >> 18) & 511u) * exp2(float(int(v >> 27) - 24));
}

void AddEmission(ivec3 coord, vec3 value)
{
	uint expected = 0u;
	for (int i = 0; i < 64; i++)
	{
		uint stored = imageAtomicCompSwap(AccEmission, coord, expected, EncodeRGB9E5(DecodeRGB9E5(expected) + value));
		if (stored == expected)
		{
			break;
		}
		expected = stored;
	}
}

void Splat(ivec3 coord, float weight, vec3 albedo, vec3 normal01, vec3 emission)
{
	if (weight <= 0.0 || any(lessThan(coord, ivec3(0))) || any(greaterThanEqual(coord, ivec3(voxelResolution))))
	{
		return;
	}

	uint w = uint(round(weight * FixedPoint));
	if (w == 0u)
	{
		return;
	}

	uvec3 a = uvec3(round(albedo * weight * FixedPoint));
	uvec3 n = uvec3(round(normal01 * weight * FixedPoint));
	imageAtomicAdd(AccAlbedoRG, coord, a.r | (a.g << 16));
	imageAtomicAdd(AccAlbedoBW, coord, a.b | (w << 16));
	imageAtomicAdd(AccNormalXY, coord, n.x | (n.y << 16));
	imageAtomicAdd(AccNormalZ, coord, n.z);
	if (emission.r + emission.g + emission.b > 0.0)
	{
		AddEmission(coord, emission * weight);
	}
	imageStore(BlocksOut, coord >> 3, vec4(1.0));
}

void main()
{
	vec3 uvw = (fWorldPos - voxelGridMin) / voxelGridSize;
	float resolution = float(voxelResolution);

	float coverage = voxSamples > 1 ? float(bitCount(gl_SampleMaskIn[0])) / float(voxSamples) : 1.0;

	vec3 albedo = clamp(voxAlbedo.rgb, 0.0, 1.0);
	if (voxUseAlbedoMap != 0)
	{
		albedo *= texture(voxAlbedoMap, fUV).rgb;
	}
	vec3 normal01 = normalize(fNormal) * 0.5 + 0.5;
	vec3 emission = voxEmissive.rgb * voxEmissive.a;
	if (voxUseEmissionMap != 0)
	{
		emission *= texture(voxEmissionMap, fUV).rgb;
	}

	// The pixel is the voxel column; along the axis the surface's depth in voxel centres decides the weights.
	ivec2 column = ivec2(gl_FragCoord.xy);
	float depth = uvw[fAxis] * resolution - 0.5;
	ivec3 axisStep = ivec3(0);
	axisStep[fAxis] = 1;

	if (voxSplatWidth >= 3)
	{
		// Quadratic B-spline around the nearest centre: weights and their slopes change smoothly as the surface moves.
		int nearest = int(floor(depth + 0.5));
		float t = depth - float(nearest);
		ivec3 nearestCoord = fAxis == 0 ? ivec3(nearest, column) : fAxis == 1 ? ivec3(column.x, nearest, column.y) : ivec3(column, nearest);
		Splat(nearestCoord - axisStep, coverage * 0.5 * (0.5 - t) * (0.5 - t), albedo, normal01, emission);
		Splat(nearestCoord, coverage * (0.75 - t * t), albedo, normal01, emission);
		Splat(nearestCoord + axisStep, coverage * 0.5 * (0.5 + t) * (0.5 + t), albedo, normal01, emission);
		return;
	}

	// Linear tent between the two centres the surface lies between.
	int lower = int(floor(depth));
	float upperWeight = depth - float(lower);
	ivec3 lowerCoord = fAxis == 0 ? ivec3(lower, column) : fAxis == 1 ? ivec3(column.x, lower, column.y) : ivec3(column, lower);
	Splat(lowerCoord, coverage * (1.0 - upperWeight), albedo, normal01, emission);
	Splat(lowerCoord + axisStep, coverage * upperWeight, albedo, normal01, emission);
}

/////Fragment
