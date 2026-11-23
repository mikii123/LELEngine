//Compute

#version 430

#include "Engine/Sampling.glsl"

// Traced reflections (Rendering.Passes.RayTracedReflectionPass), step 2: the frames' accumulation
// (Engine/RayTracedReflections.shader) smoothed over the neighbouring pixels of the same surface (a Gaussian up to two
// pixels wide on the roughest traced surfaces, none on a mirror; weighted by how well their view depth and normal match),
// then blended into the screen probes' reflection over the last quarter of the roughness range, written over the probe
// gather's specular (rgb radiance along the reflection, a linear view depth, kept) where the surface was traced; the
// rest of it is not touched.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D TracedReflection;   // rgb the accumulated traced reflection, a the surface's roughness
uniform sampler2D Meta;               // x linear view depth, y frames (0: not traced), zw octahedral normal
uniform ivec2 outputSize;
uniform float maxRoughness;

layout(binding = 0, rgba16f) uniform image2D Specular;  // the probe gather's specular

void main()
{
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(texel, outputSize)))
	{
		return;
	}

	vec4 center = texelFetch(Meta, texel, 0);
	if (center.y < 0.5)
	{
		return;
	}

	vec4 own = texelFetch(TracedReflection, texel, 0);
	float roughness = own.a;
	int radius = int(round(2.0 * clamp(roughness / maxRoughness, 0.0, 1.0)));
	vec3 filtered = own.rgb;
	if (radius > 0)
	{
		vec3 normal = OctahedralToDirection(center.zw);
		float tolerance = 0.02 * center.x + 0.02;
		float sigma2 = float(radius * radius) * 0.5 + 0.25;
		vec3 sum = vec3(0.0);
		float weightSum = 0.0;
		for (int dy = -radius; dy <= radius; dy++)
		{
			for (int dx = -radius; dx <= radius; dx++)
			{
				ivec2 tap = texel + ivec2(dx, dy);
				if (any(lessThan(tap, ivec2(0))) || any(greaterThanEqual(tap, outputSize)))
				{
					continue;
				}

				vec4 meta = texelFetch(Meta, tap, 0);
				if (meta.y < 0.5)
				{
					continue;
				}

				// The normals' agreement to the 32nd power, by squaring.
				float facing = max(dot(OctahedralToDirection(meta.zw), normal), 0.0);
				facing *= facing;
				facing *= facing;
				facing *= facing;
				facing *= facing;
				float w = exp(-float(dx * dx + dy * dy) / sigma2)
					* clamp(1.0 - abs(meta.x - center.x) / tolerance, 0.0, 1.0)
					* facing * facing;
				sum += texelFetch(TracedReflection, tap, 0).rgb * w;
				weightSum += w;
			}
		}

		filtered = weightSum > 0.0 ? sum / weightSum : own.rgb;
	}

	// Into the probes' reflection over the last quarter of the range.
	vec4 probe = imageLoad(Specular, texel);
	float weight = 1.0 - smoothstep(0.75 * maxRoughness, maxRoughness, roughness);
	imageStore(Specular, texel, vec4(mix(probe.rgb, filtered, weight), probe.a));
}

/////Compute
