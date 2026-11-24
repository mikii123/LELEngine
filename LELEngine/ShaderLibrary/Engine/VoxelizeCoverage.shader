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
	gUV = vTexCoord;
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

#ifdef LEL_VOXEL_CONSERVATIVE
// Conservative rasterization: a fragment for every voxel column the triangle touches, whose centre may lie outside it.
// The fragment works out the covered part of its column and a point of the triangle there from the whole triangle:
// each corner's column position (pixels) and depth (voxel centres along the axis), texture coordinates and normal.
uniform int voxelResolution;
flat out vec3 fCorner0;
flat out vec3 fCorner1;
flat out vec3 fCorner2;
flat out vec2 fUV0;
flat out vec2 fUV1;
flat out vec2 fUV2;
flat out vec3 fNormal0;
flat out vec3 fNormal1;
flat out vec3 fNormal2;
#else
centroid out vec3 fWorldPos;
centroid out vec3 fNormal;
centroid out vec2 fUV;
#endif
flat out int fAxis;

void main()
{
	vec3 faceNormal = abs(cross(gWorldPos[1] - gWorldPos[0], gWorldPos[2] - gWorldPos[0]));
	int axis = (faceNormal.x > faceNormal.y && faceNormal.x > faceNormal.z) ? 0 : (faceNormal.y > faceNormal.z ? 1 : 2);

#ifdef LEL_VOXEL_CONSERVATIVE
	vec3 corners[3];
	for (int i = 0; i < 3; i++)
	{
		vec3 uvw = (gWorldPos[i] - voxelGridMin) / voxelGridSize;
		vec2 across = axis == 0 ? uvw.yz : (axis == 1 ? uvw.xz : uvw.xy);
		corners[i] = vec3(across * float(voxelResolution), (axis == 0 ? uvw.x : (axis == 1 ? uvw.y : uvw.z)) * float(voxelResolution) - 0.5);
	}
#endif

	for (int i = 0; i < 3; i++)
	{
		vec3 p = (gWorldPos[i] - voxelGridMin) / voxelGridSize * 2.0 - 1.0;
		vec2 projected = axis == 0 ? p.yz : (axis == 1 ? p.xz : p.xy);

		gl_Position = vec4(projected, 0.0, 1.0);
#ifdef LEL_VOXEL_CONSERVATIVE
		fCorner0 = corners[0];
		fCorner1 = corners[1];
		fCorner2 = corners[2];
		fUV0 = gUV[0];
		fUV1 = gUV[1];
		fUV2 = gUV[2];
		fNormal0 = gNormal[0];
		fNormal1 = gNormal[1];
		fNormal2 = gNormal[2];
#else
		fWorldPos = gWorldPos[i];
		fNormal = gNormal[i];
		fUV = gUV[i];
#endif
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
#include "Engine/GISurface.glsl"

#ifdef LEL_VOXEL_CONSERVATIVE
// Conservative rasterization (VoxelGIPass, where the device has it): one fragment per voxel column the triangle
// touches; the covered part of the column is the exact area of the triangle inside it, and the surface's depth,
// texture coordinates and normal there are the triangle's at its point nearest the column's centre.
flat in vec3 fCorner0; // column position (pixels) and depth (voxel centres along the axis) of each corner
flat in vec3 fCorner1;
flat in vec3 fCorner2;
flat in vec2 fUV0;
flat in vec2 fUV1;
flat in vec2 fUV2;
flat in vec3 fNormal0;
flat in vec3 fNormal1;
flat in vec3 fNormal2;
#else
centroid in vec3 fWorldPos;
centroid in vec3 fNormal;
centroid in vec2 fUV;
#endif
flat in int fAxis;

const float FixedPoint = 256.0;

#ifdef LEL_VOXEL_CONSERVATIVE
// The integral of clamp(v, lo, hi) - lo from below lo up to u.
float RiseIntegral(float u, float lo, float hi)
{
	float c = clamp(u, lo, hi) - lo;
	return 0.5 * c * c + (hi - lo) * max(u - hi, 0.0);
}

// What a straight edge from (xa, ya) to (xb, yb), xa < xb, adds to the area below it inside [lo.y, hi.y]: the integral
// over x of clamp(y(x), lo.y, hi.y) - lo.y.
float EdgeBand(float xa, float xb, float ya, float yb, float lo, float hi)
{
	float low = min(ya, yb);
	float high = max(ya, yb);
	float mean = high - low < 1e-6 ? clamp(0.5 * (ya + yb), lo, hi) - lo : (RiseIntegral(high, lo, hi) - RiseIntegral(low, lo, hi)) / (high - low);
	return (xb - xa) * mean;
}

// One edge's part of the area of the triangle inside the box [lo, hi] (Green's theorem: the area is the sum over the
// edges, each the integral of the box-clamped height below it over its part of the box's x range, signed by its way).
float EdgeArea(vec2 a, vec2 b, vec2 lo, vec2 hi)
{
	float xa = max(min(a.x, b.x), lo.x);
	float xb = min(max(a.x, b.x), hi.x);
	if (xb <= xa)
	{
		return 0.0;
	}

	float slope = (b.y - a.y) / (b.x - a.x);
	float band = EdgeBand(xa, xb, a.y + (xa - a.x) * slope, a.y + (xb - a.x) * slope, lo.y, hi.y);
	return b.x > a.x ? band : -band;
}

// The triangle's covered part of the column (0..1), and its depth, texture coordinates and normal at the point of the
// triangle the column's centre's clamped barycentric coordinates give (the centre itself when it is inside).
float ColumnSurface(ivec2 column, out float depth, out vec2 uv, out vec3 normal)
{
	vec2 lo = vec2(column);
	float area = abs(EdgeArea(fCorner0.xy, fCorner1.xy, lo, lo + 1.0) + EdgeArea(fCorner1.xy, fCorner2.xy, lo, lo + 1.0)
		+ EdgeArea(fCorner2.xy, fCorner0.xy, lo, lo + 1.0));

	vec2 e0 = fCorner1.xy - fCorner0.xy;
	vec2 e1 = fCorner2.xy - fCorner0.xy;
	vec2 d = lo + 0.5 - fCorner0.xy;
	float determinant = e0.x * e1.y - e0.y * e1.x;
	vec3 b = vec3(1.0 / 3.0);
	if (abs(determinant) > 1e-12)
	{
		float b1 = (d.x * e1.y - d.y * e1.x) / determinant;
		float b2 = (e0.x * d.y - e0.y * d.x) / determinant;
		b = max(vec3(1.0 - b1 - b2, b1, b2), vec3(0.0));
		b /= max(b.x + b.y + b.z, 1e-6);
	}

	depth = dot(b, vec3(fCorner0.z, fCorner1.z, fCorner2.z));
	uv = b.x * fUV0 + b.y * fUV1 + b.z * fUV2;
	normal = b.x * fNormal0 + b.y * fNormal1 + b.z * fNormal2;
	return min(area, 1.0);
}
#endif

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
#ifdef LEL_VOXEL_CONSERVATIVE
	// The pixel is the voxel column; along the axis the surface's depth in voxel centres decides the weights.
	ivec2 column = ivec2(gl_FragCoord.xy);
	float depth;
	vec2 uv;
	vec3 normal;
	float coverage = ColumnSurface(column, depth, uv, normal);

	// The material is read before any fragment leaves (its textures' level of detail comes from the neighbours).
	vec3 albedo = LelGIAlbedo(clamp(voxAlbedo.rgb, 0.0, 1.0), uv);
	vec3 normal01 = normalize(normal) * 0.5 + 0.5;
	vec3 emission = LelGIEmission(uv) * voxEmissive.a;
	if (coverage <= 0.0)
	{
		return;
	}

	ivec3 axisStep = ivec3(equal(ivec3(fAxis), ivec3(0, 1, 2)));
#else
	vec3 uvw = (fWorldPos - voxelGridMin) / voxelGridSize;
	float resolution = float(voxelResolution);

	float coverage = voxSamples > 1 ? float(bitCount(gl_SampleMaskIn[0])) / float(voxSamples) : 1.0;

	vec3 albedo = LelGIAlbedo(clamp(voxAlbedo.rgb, 0.0, 1.0), fUV);
	vec3 normal01 = normalize(fNormal) * 0.5 + 0.5;
	vec3 emission = LelGIEmission(fUV) * voxEmissive.a;

	// The pixel is the voxel column; along the axis the surface's depth in voxel centres decides the weights. (Components
	// picked by comparisons: an index worked out per fragment kept the vectors in scratch memory, Vulkan on AMD.)
	ivec2 column = ivec2(gl_FragCoord.xy);
	float depth = (fAxis == 0 ? uvw.x : fAxis == 1 ? uvw.y : uvw.z) * resolution - 0.5;
	ivec3 axisStep = ivec3(equal(ivec3(fAxis), ivec3(0, 1, 2)));
#endif

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
