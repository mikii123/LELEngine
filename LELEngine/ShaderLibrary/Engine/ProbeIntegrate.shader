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
//
// A temporal filter then accumulates the result over frames (Lumen's screen probe history): the surface point is
// reprojected with its motion vector into the previous frame, where the four history texels around it are weighted
// bilinearly and by how well their depth and normal match the point (a texel of another surface never leaks in,
// and an edge does not blur a little more every frame). The history holds its own frame count: the weight of the
// new value is 1 / n, n growing up to temporalMaxFrames, so a fresh history converges fast and a long one is
// stable; on moving surfaces and where the probes saw the lighting change, n is capped lower (fast update).
in vec2 fUV;

layout(location = 0) out vec4 OutDiffuse;   // rgb irradiance / pi, a = 0 (no occlusion term)
layout(location = 1) out vec4 OutSpecular;  // rgb radiance along the reflection, a = linear view depth
layout(location = 2) out vec4 OutHistory;   // xy octahedral normal, z frames accumulated, w = 0

uniform sampler2DArray ProbeSH;
uniform sampler2D HistoryDiffuse;
uniform sampler2D HistorySpecular;
uniform sampler2D HistoryMeta;            // the previous frame's OutHistory
uniform sampler2D Motion;                 // world-space motion since the previous frame (geometry prepass)
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform mat4 previousViewProjection;
uniform vec3 previousCameraPosition;
uniform vec3 previousCameraForward;
uniform ivec2 resolveSize;                // size of this target and of the history
uniform float temporalMaxFrames;          // frames a history accumulates at most (0 or 1 = no temporal filter)
uniform float temporalMovingFrames;       // the cap on moving surfaces
uniform int historyValid;
uniform float planeTolerance;

// Unpacks the 7-layer coefficient layout written by ProbeSH.shader. valid: 0 = no probe, else 1 + the probe's change
// amount this frame (the fraction of its radiance that changed, see ProbeCompose.shader).
void ReadSH(ivec2 probe, out vec3 c[9], out float valid)
{
	vec4 l0 = texelFetch(ProbeSH, ivec3(probe, 0), 0);
	vec4 l1 = texelFetch(ProbeSH, ivec3(probe, 1), 0);
	vec4 l2 = texelFetch(ProbeSH, ivec3(probe, 2), 0);
	vec4 l3 = texelFetch(ProbeSH, ivec3(probe, 3), 0);
	vec4 l4 = texelFetch(ProbeSH, ivec3(probe, 4), 0);
	vec4 l5 = texelFetch(ProbeSH, ivec3(probe, 5), 0);
	vec4 l6 = texelFetch(ProbeSH, ivec3(probe, 6), 0);
	c[0] = l0.rgb;
	c[1] = vec3(l0.a, l1.rg);
	c[2] = vec3(l1.ba, l2.r);
	c[3] = l2.gba;
	c[4] = l3.rgb;
	c[5] = vec3(l3.a, l4.rg);
	c[6] = vec3(l4.ba, l5.r);
	c[7] = l5.gba;
	c[8] = l6.rgb;
	valid = l6.a;
}

void main()
{
	float depth = texture(SceneDepth, fUV).r;
	if (depth >= 1.0)
	{
		OutDiffuse = vec4(0.0);
		OutSpecular = vec4(0.0, 0.0, 0.0, 1e9);
		OutHistory = vec4(0.0);
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
	float changeSum = 0.0;

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
		changeSum += (valid - 1.0) * w;
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
		changeSum += (valid - 1.0) * w;
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
			changeSum += (valid - 1.0) * w;
		}
	}
	float probeChange = weightSum > 0.0 ? changeSum / weightSum : 0.0;

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

	// ---- temporal filter: the history of this surface point, from the four history texels around its previous position
	vec4 motion = texelFetch(Motion, pixel, 0);
	float frames = 1.0;
	if (historyValid != 0 && temporalMaxFrames > 1.0)
	{
		vec3 previousPosition = position - motion.xyz;
		vec4 prevClip = previousViewProjection * vec4(previousPosition, 1.0);
		if (prevClip.w > 0.0)
		{
			vec2 prevTexel = (prevClip.xy / prevClip.w * 0.5 + 0.5) * vec2(resolveSize) - 0.5;
			ivec2 prevBase = ivec2(floor(prevTexel));
			vec2 pf = prevTexel - vec2(prevBase);
			float expectedDepth = dot(previousPosition - previousCameraPosition, previousCameraForward);
			float depthTolerance = 0.03 * expectedDepth + 0.03;

			vec3 historyDiffuse = vec3(0.0);
			vec3 historySpecular = vec3(0.0);
			float historyFrames = 0.0;
			float historyWeight = 0.0;
			if (all(greaterThanEqual(prevBase, ivec2(0))) && all(lessThan(prevBase + 1, resolveSize)))
			{
				// The four texels' depths, normals and frame counts in four gathers (texel order of textureGather:
				// (0,1), (1,1), (1,0), (0,0) from prevBase).
				vec2 gatherUV = (vec2(prevBase) + 1.0) / vec2(resolveSize);
				vec4 depths = textureGather(HistorySpecular, gatherUV, 3);
				vec4 normalX = textureGather(HistoryMeta, gatherUV, 0);
				vec4 normalY = textureGather(HistoryMeta, gatherUV, 1);
				vec4 tapFrames = textureGather(HistoryMeta, gatherUV, 2);
				vec4 bilinear = vec4((1.0 - pf.x) * pf.y, pf.x * pf.y, pf.x * (1.0 - pf.y), (1.0 - pf.x) * (1.0 - pf.y));
				vec4 match;
				for (int i = 0; i < 4; i++)
				{
					// The same surface: at the expected depth and facing the same way (creases and silhouettes reject),
					// with a history at all (sky, or rejected and not yet filled, has none).
					float depthWeight = clamp(1.0 - abs(depths[i] - expectedDepth) / depthTolerance, 0.0, 1.0);
					float normalWeight = smoothstep(0.85, 0.97, dot(OctahedralToDirection(vec2(normalX[i], normalY[i])), N));
					match[i] = tapFrames[i] < 0.5 ? 0.0 : depthWeight * normalWeight;
				}

				vec4 weights = bilinear * match;
				historyWeight = weights.x + weights.y + weights.z + weights.w;
				historyFrames = dot(weights, tapFrames);
				if (all(greaterThan(match, vec4(0.98))))
				{
					// Every tap is the same surface (the common case): the hardware bilinear fetch is the weighted sum.
					vec2 historyUV = (prevTexel + 0.5) / vec2(resolveSize);
					historyDiffuse = texture(HistoryDiffuse, historyUV).rgb * historyWeight;
					historySpecular = texture(HistorySpecular, historyUV).rgb * historyWeight;
				}
				else
				{
					const ivec2 offsets[4] = ivec2[4](ivec2(0, 1), ivec2(1, 1), ivec2(1, 0), ivec2(0, 0));
					for (int i = 0; i < 4; i++)
					{
						if (weights[i] <= 0.0) continue;
						ivec2 coord = prevBase + offsets[i];
						historyDiffuse += texelFetch(HistoryDiffuse, coord, 0).rgb * weights[i];
						historySpecular += texelFetch(HistorySpecular, coord, 0).rgb * weights[i];
					}
				}
			}

			if (historyWeight > 0.05)
			{
				historyDiffuse /= historyWeight;
				historySpecular /= historyWeight;
				// A history that matched only partly (an edge, half the taps on another surface) counts for less.
				float previousFrames = historyFrames / historyWeight * clamp(historyWeight * 2.0, 0.0, 1.0);

				// Moving surfaces, and pixels whose probes saw their lighting change, keep a short history.
				float cap = mix(temporalMaxFrames, min(temporalMovingFrames, temporalMaxFrames), clamp(probeChange * 2.0, 0.0, 1.0));
				if (motion.w > 0.5) cap = min(cap, temporalMovingFrames);
				if (hasEstimate)
				{
					frames = min(previousFrames + 1.0, max(cap, 1.0));
					float currentWeight = 1.0 / frames;
					diffuse = mix(historyDiffuse, diffuse, currentWeight);
					specular = mix(historySpecular, specular, currentWeight);
				}
				else
				{
					// No probe this frame: keep the history rather than fading towards black, without counting a frame.
					diffuse = historyDiffuse;
					specular = historySpecular;
					frames = max(previousFrames, 1.0);
				}
			}
		}
	}

	OutDiffuse = vec4(diffuse, 0.0);
	OutSpecular = vec4(specular, linearDepth);
	OutHistory = vec4(DirectionToOctahedral(N), hasEstimate || frames > 1.0 ? frames : 0.0, 0.0);
}

/////Fragment
