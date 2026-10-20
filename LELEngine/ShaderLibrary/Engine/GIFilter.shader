//Vertex

#version 430

out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// One pass of an edge-aware a-trous filter over the voxel GI resolve's diffuse (GIResolvePass), spatial only: a 3x3
// B-spline kernel (1/4, 1/2, 1/4) with its taps filterStep texels apart (1, 2, 4, 8 over the passes), each tap weighted
// by how well its surface continues the centre's: the normals' agreement and the distance of its point from the
// centre's plane (Guide: normal and linear depth the resolve traced with). No colour weight: the prints of the
// jittered cones are colour differences on one surface, which is what the filter averages away.

in vec2 fUV;

layout(location = 0) out vec4 OutDiffuse; // rgb irradiance, a occlusion

uniform sampler2D Diffuse;
uniform sampler2D Guide; // xyz normal, w linear view depth (negative: sky)
uniform vec2 resolveTargetSize;
uniform vec2 screenSize;
uniform int filterStep;
uniform vec3 cameraForward;
uniform float filterNormalPower;
uniform float filterPlaneTolerance; // fraction of the depth
// View rays (to the far plane, from the camera) are linear across the screen: the ray of a full-resolution pixel is
// rayOrigin + x * rayStepX + y * rayStepY.
uniform vec3 rayOrigin;
uniform vec3 rayStepX;
uniform vec3 rayStepY;

// Position of a resolve texel relative to the camera, from its linear view depth (the full-resolution pixel the
// resolve traced).
vec3 TexelPosition(ivec2 texel, float linearDepth)
{
	vec2 pixel = vec2(ivec2((vec2(texel) + 0.5) * screenSize / resolveTargetSize));
	vec3 ray = rayOrigin + pixel.x * rayStepX + pixel.y * rayStepY;
	return ray * (linearDepth / dot(ray, cameraForward));
}

void main()
{
	ivec2 center = ivec2(gl_FragCoord.xy);
	vec4 centerDiffuse = texelFetch(Diffuse, center, 0);
	vec4 centerGuide = texelFetch(Guide, center, 0);
	if (centerGuide.w < 0.0)
	{
		// Sky.
		OutDiffuse = centerDiffuse;
		return;
	}

	vec3 position = TexelPosition(center, centerGuide.w);
	float tolerance = filterPlaneTolerance * centerGuide.w + 0.01;
	ivec2 maxTexel = ivec2(resolveTargetSize) - 1;

	const float kernel[2] = float[2](0.5, 0.25);
	vec4 sum = centerDiffuse * 0.25;
	float weightSum = 0.25;
	for (int y = -1; y <= 1; y++)
	{
		for (int x = -1; x <= 1; x++)
		{
			ivec2 texel = center + ivec2(x, y) * filterStep;
			if ((x == 0 && y == 0) || any(lessThan(texel, ivec2(0))) || any(greaterThan(texel, maxTexel)))
			{
				continue;
			}

			vec4 guide = texelFetch(Guide, texel, 0);
			if (guide.w < 0.0)
			{
				continue;
			}

			float normalWeight = pow(max(dot(centerGuide.xyz, guide.xyz), 0.0), filterNormalPower);
			float planeDistance = abs(dot(centerGuide.xyz, TexelPosition(texel, guide.w) - position));
			float w = kernel[abs(x)] * kernel[abs(y)] * normalWeight * max(0.0, 1.0 - planeDistance / tolerance);
			sum += texelFetch(Diffuse, texel, 0) * w;
			weightSum += w;
		}
	}

	OutDiffuse = sum / weightSum;
}

/////Fragment
