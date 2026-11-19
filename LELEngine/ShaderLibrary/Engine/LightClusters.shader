//Compute

#version 430

#include "Engine/Lights.glsl"

// The lights' clusters of a camera's view (Rendering.LightClusterPass, read by Engine/Lights.glsl's LelLightsAt): the
// view cut into tiles of the screen (even steps of NDC) x slices of depth (logarithmic: a slice's far depth is a fixed
// multiple of its near one), each with a bit per light of the list whose range reaches the cluster's box: the bounds,
// in view space, of the part of the view it covers, with a little room. One thread per cluster and mask word (32 lights).
layout(local_size_x = 64) in;

// A binding no other pass binds (Rendering.LightClusterPass.BuildBinding).
layout(std430, binding = 18) writeonly buffer ClusterMasks
{
	uint masks[];
};

uniform mat4 clusterView;               // world -> the camera's view space (it looks along -z)
uniform mat4 clusterInverseProjection;  // the camera's clip space -> view space
uniform ivec3 clusterGrid;              // tiles across, down, slices of depth
uniform int clusterWords;               // mask words of a cluster
uniform int clusterLights;              // lights in the list
uniform vec4 clusterSlicing;            // as lelLightClusterSlicing: a depth's slice is floor(log(depth) * x + y); z, w: the nearest and furthest depth covered

// The point at a view depth on the line of sight through a point of the screen (NDC): through two points of that line.
vec3 SightPoint(vec2 ndc, float depth)
{
	vec4 a = clusterInverseProjection * vec4(ndc, 0.25, 1.0);
	vec4 b = clusterInverseProjection * vec4(ndc, 0.5, 1.0);
	vec3 pa = a.xyz / a.w;
	vec3 pb = b.xyz / b.w;
	return pa + (pb - pa) * ((-depth - pa.z) / (pb.z - pa.z));
}

void main()
{
	int cells = clusterGrid.x * clusterGrid.y * clusterGrid.z;
	int cell = int(gl_GlobalInvocationID.x);
	int word = int(gl_GlobalInvocationID.y);
	if (cell >= cells || word >= clusterWords)
	{
		return;
	}

	int tileX = cell % clusterGrid.x;
	int tileY = (cell / clusterGrid.x) % clusterGrid.y;
	int slice = cell / (clusterGrid.x * clusterGrid.y);

	// The first slice reaches back to the camera, the last one to the furthest depth covered: a point the lookup
	// clamps into them is inside their box.
	float nearDepth = slice == 0 ? clusterSlicing.z : exp((float(slice) - clusterSlicing.y) / clusterSlicing.x);
	float farDepth = slice == clusterGrid.z - 1 ? clusterSlicing.w : exp((float(slice + 1) - clusterSlicing.y) / clusterSlicing.x);
	vec2 ndcMin = vec2(tileX, tileY) / vec2(clusterGrid.xy) * 2.0 - 1.0;
	vec2 ndcMax = vec2(tileX + 1, tileY + 1) / vec2(clusterGrid.xy) * 2.0 - 1.0;

	vec3 boxMin = vec3(3.0e38);
	vec3 boxMax = vec3(-3.0e38);
	for (int corner = 0; corner < 4; corner++)
	{
		vec2 ndc = vec2((corner & 1) != 0 ? ndcMax.x : ndcMin.x, (corner & 2) != 0 ? ndcMax.y : ndcMin.y);
		vec3 nearPoint = SightPoint(ndc, nearDepth);
		vec3 farPoint = SightPoint(ndc, farDepth);
		boxMin = min(boxMin, min(nearPoint, farPoint));
		boxMax = max(boxMax, max(nearPoint, farPoint));
	}

	// Room for rounding: the lookup finds the cluster from the world position with other arithmetic.
	vec3 room = vec3(farDepth * 0.002 + 1e-4);
	boxMin -= room;
	boxMax += room;

	uint mask = 0u;
	int first = word * 32;
	int last = min(first + 32, clusterLights);
	for (int i = first; i < last; i++)
	{
		vec4 positionRange = lelLights[i].positionRange;
		vec3 center = (clusterView * vec4(positionRange.xyz, 1.0)).xyz;
		vec3 outside = center - clamp(center, boxMin, boxMax);
		if (dot(outside, outside) < positionRange.w * positionRange.w)
		{
			mask |= 1u << uint(i - first);
		}
	}

	masks[cell * clusterWords + word] = mask;
}

/////Compute
