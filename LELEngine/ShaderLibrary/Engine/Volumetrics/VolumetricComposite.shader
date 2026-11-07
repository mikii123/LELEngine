//Vertex

#version 430

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// A volumetric object's march blended over the scene (premultiplied: Blend One OneMinusSrcAlpha), at full resolution, in
// the transparent pass where the object sorts. Where the opaque surface is in front of the shape the pixel is left
// alone (the shape's entry point is computed exactly, so objects in front of a volume get no halo). A march at reduced
// resolution is upsampled from its four nearest texels weighted by how well each one's depth matches the pixel's (the
// depth checkerboard gives each silhouette both sides).

#include "Engine/Volumetrics/VolumetricCommon.glsl"

uniform sampler2D volResult;        // premultiplied light, 1 - transmittance
uniform sampler2D volResultDepth;   // eye depth each result texel marched to (reduced resolution only)
uniform sampler2D volSceneDepth;    // window depth, full resolution
uniform vec4 volDepthParams;
uniform int volUpsample;            // 0: the result is full resolution

out vec4 FragColor;

void main()
{
	vec2 pixel = gl_FragCoord.xy;
	float eye = VolEyeDepth(texelFetch(volSceneDepth, ivec2(pixel), 0).r, volDepthParams);
	VolRay ray = VolMakeRay(pixel);
	vec2 range = VolIntersect(ray.origin, ray.dir);
	float enter = max(range.x, 0.0);
	if (range.y <= enter || enter / ray.scale * dot(ray.worldDir, volCameraForward) >= eye)
	{
		discard;
	}

	if (volUpsample == 0)
	{
		FragColor = texelFetch(volResult, ivec2(pixel), 0);
		return;
	}

	vec2 resultSize = vec2(textureSize(volResult, 0));
	vec2 position = pixel / volTargetSize * resultSize - 0.5;
	ivec2 base = ivec2(floor(position));
	vec2 f = position - vec2(base);
	vec4 sum = vec4(0.0);
	float weights = 0.0;
	for (int y = 0; y <= 1; y++)
	{
		for (int x = 0; x <= 1; x++)
		{
			ivec2 texel = clamp(base + ivec2(x, y), ivec2(0), ivec2(resultSize) - 1);
			float bilinear = (x == 0 ? 1.0 - f.x : f.x) * (y == 0 ? 1.0 - f.y : f.y);
			float mismatch = abs(texelFetch(volResultDepth, texel, 0).r - eye) / max(eye, 1e-3);
			float w = (bilinear + 1e-3) / (0.02 + mismatch);
			sum += texelFetch(volResult, texel, 0) * w;
			weights += w;
		}
	}

	FragColor = sum / max(weights, 1e-6);
}

/////Fragment
