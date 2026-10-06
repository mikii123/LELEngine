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

#include "Engine/Sampling.glsl"
#include "Engine/ScreenProbes.glsl"
#include "Engine/AdaptiveProbes.glsl"

// Per-pixel integration (Lumen final gather, step 3) at the resolve resolution: the four surrounding
// uniform probes are interpolated with bilinear weights multiplied by a plane / normal agreement test, the
// adaptive probes of the pixel's tile join with a spatial kernel, their SH is convolved with the pixel
// normal for diffuse irradiance and evaluated along the reflection direction for rough specular.
// A temporal reprojection then accumulates the result over frames.
in vec2 fUV;

layout(location = 0) out vec4 OutDiffuse;   // rgb irradiance / pi, a = 0 (no occlusion term)
layout(location = 1) out vec4 OutSpecular;  // rgb radiance along the reflection, a = linear view depth

uniform sampler2DArray ProbeSH;
uniform sampler2D HistoryDiffuse;
uniform sampler2D HistorySpecular;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform mat4 previousViewProjection;
uniform vec3 previousCameraPosition;
uniform vec3 previousCameraForward;
uniform float temporalBlend;      // history weight, 0 = off
uniform int historyValid;
uniform float planeTolerance;

void ReadSH(ivec2 probe, out vec3 c[9], out float valid)
{
	valid = 1.0;
	for (int i = 0; i < 9; i++)
	{
		vec4 s = texelFetch(ProbeSH, ivec3(probe, i), 0);
		c[i] = s.rgb;
		if (i == 0) valid = s.a;
	}
}

void main()
{
	float depth = texture(SceneDepth, fUV).r;
	if (depth >= 1.0)
	{
		OutDiffuse = vec4(0.0);
		OutSpecular = vec4(0.0, 0.0, 0.0, 1e9);
		return;
	}

	ivec2 pixel = ivec2(fUV * vec2(screenSize));
	vec3 position = ReconstructPosition(pixel, depth);
	vec4 nr = texture(NormalRoughness, fUV);
	vec3 N = normalize(nr.xyz);
	vec3 V = normalize(cameraPosition - position);
	vec3 R = reflect(-V, N);

	// Continuous probe coordinate: probe p is anchored at p * spacing + jitter.
	vec2 probeCoord = (vec2(pixel) - vec2(probeJitter)) / float(probeSpacing);
	ivec2 base = ivec2(floor(probeCoord));
	vec2 f = probeCoord - vec2(base);

	vec3 accum[9];
	for (int i = 0; i < 9; i++) accum[i] = vec3(0.0);
	float weightSum = 0.0;

	for (int i = 0; i < 4; i++)
	{
		ivec2 offset = ivec2(i & 1, i >> 1);
		ivec2 probe = clamp(base + offset, ivec2(0), probeCount - 1);
		float bilinear = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y);

		vec3 pPosition, pNormal;
		if (!ProbeAnchor(probe, pPosition, pNormal)) continue;

		// Probes on other surfaces must not leak: plane distance and normal agreement.
		float planeDistance = abs(dot(N, pPosition - position));
		float normalWeight = max(dot(N, pNormal), 0.0);
		if (planeDistance > planeTolerance || normalWeight < 0.5) continue;

		float w = bilinear * normalWeight * (1.0 - planeDistance / planeTolerance) + 1e-4;

		vec3 c[9];
		float valid;
		ReadSH(probe, c, valid);
		if (valid < 0.5) continue;

		for (int k = 0; k < 9; k++) accum[k] += c[k] * w;
		weightSum += w;
	}

	// Adaptive probes placed in this pixel's tile (Lumen): same plane / normal test, tent kernel in screen space.
	int tileOffset = AdaptiveTileOffset(pixel);
	int adaptiveCount = AdaptiveTileCount(tileOffset);
	for (int k = 0; k < adaptiveCount; k++)
	{
		float spatial = AdaptiveSpatialWeight(pixel, AdaptiveTilePixel(tileOffset, k));
		if (spatial <= 0.0) continue;

		ivec2 probe = AdaptiveProbeTexel(AdaptiveTileProbe(tileOffset, k));
		vec3 pPosition, pNormal;
		if (!ProbeAnchor(probe, pPosition, pNormal)) continue;

		float planeDistance = abs(dot(N, pPosition - position));
		float normalWeight = max(dot(N, pNormal), 0.0);
		if (planeDistance > planeTolerance || normalWeight < 0.5) continue;

		vec3 c[9];
		float valid;
		ReadSH(probe, c, valid);
		if (valid < 0.5) continue;

		float w = spatial * normalWeight * (1.0 - planeDistance / planeTolerance) + 1e-4;
		for (int i = 0; i < 9; i++) accum[i] += c[i] * w;
		weightSum += w;
	}

	if (weightSum <= 0.0)
	{
		// No probe passed the plane / normal test (thin geometry, silhouettes, cell corners). Lumen relaxes
		// the test here instead of leaving the pixel unlit: take the valid probes with bilinear weights only.
		for (int i = 0; i < 4; i++)
		{
			ivec2 offset = ivec2(i & 1, i >> 1);
			ivec2 probe = clamp(base + offset, ivec2(0), probeCount - 1);
			float bilinear = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y);

			vec3 pPosition, pNormal;
			if (!ProbeAnchor(probe, pPosition, pNormal)) continue;
			if (dot(N, pNormal) < 0.0) continue;

			vec3 c[9];
			float valid;
			ReadSH(probe, c, valid);
			if (valid < 0.5) continue;

			float w = bilinear + 1e-4;
			for (int k = 0; k < 9; k++) accum[k] += c[k] * w;
			weightSum += w;
		}
	}

	vec3 diffuse = vec3(0.0);
	vec3 specular = vec3(0.0);
	bool hasEstimate = weightSum > 0.0;
	if (hasEstimate)
	{
		for (int k = 0; k < 9; k++) accum[k] /= weightSum;
		// Irradiance / pi: the engine's indirect diffuse convention (multiply by albedo in the material).
		diffuse = SHIrradiance(accum, N) / SAMPLING_PI;

		// Rough specular from the same probes (Lumen reuses the radiance cache for rough surfaces):
		// a very rough GGX lobe is close to Lambertian, so blend from a damped directional lookup to the
		// cosine-convolved value as roughness grows. This keeps the lookup view-stable.
		float roughness = nr.w > 0.0 ? nr.w : 0.6;
		vec3 directional = SHEvaluateWindowed(accum, R);
		specular = mix(directional, diffuse, smoothstep(0.25, 0.6, roughness));
	}

	float linearDepth = dot(position - cameraPosition, cameraForward);

	// Temporal accumulation: reproject into the previous frame and keep the history where the depth agrees.
	if (historyValid != 0 && temporalBlend > 0.0)
	{
		vec4 prevClip = previousViewProjection * vec4(position, 1.0);
		vec2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;
		if (prevClip.w > 0.0 && all(greaterThanEqual(prevUV, vec2(0.0))) && all(lessThanEqual(prevUV, vec2(1.0))))
		{
			vec4 historySpecular = texture(HistorySpecular, prevUV);
			float expectedDepth = dot(position - previousCameraPosition, previousCameraForward);
			if (abs(historySpecular.a - expectedDepth) < 0.05 * expectedDepth + 0.05)
			{
				vec4 historyDiffuse = texture(HistoryDiffuse, prevUV);
				// Constant history weight: making it depend on the (noisy) difference to the history
				// lets sampling noise through and reads as flicker. Without any probe this frame the
				// history is kept as is rather than faded towards black.
				float weight = hasEstimate ? temporalBlend : 1.0;
				diffuse = mix(diffuse, historyDiffuse.rgb, weight);
				specular = mix(specular, historySpecular.rgb, weight);
			}
		}
	}

	OutDiffuse = vec4(diffuse, 0.0);
	OutSpecular = vec4(specular, linearDepth);
}

/////Fragment
