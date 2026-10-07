//Compute

#version 430

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Surface cache radiosity probes (Lumen's radiosity): one hemispherical probe per 4x4 block of card texels.
// Every frame each probe traces 16 cosine-distributed rays from its anchor texel (one ray per thread), the
// rays are averaged (an estimate of irradiance / pi) and accumulated into the probe. Texels then interpolate
// the probes of their card (CardLighting.shader). Sixteen rays shared by sixteen texels cost a quarter of
// four rays per texel, and the interpolation plus the 16-ray average give far smoother indirect lighting
// than per-texel estimates.
layout(local_size_x = 16) in;

uniform usampler2D CardIndex;
uniform sampler2D CardNormal;
uniform sampler2D FinalLightingPrev;
uniform vec3 giSkyRadiance;
uniform int frameIndex;
uniform float radiosityBlend;      // constant history weight per update when radiosityMaxSamples is 0
uniform float radiosityMaxSamples; // sample-count accumulation cap (lower while the scene is changing, see SurfaceCachePass)
uniform ivec2 probeRegion;         // probe grid in use (x = columns, y = rows)
uniform int probeRowStart;         // first probe row of this dispatch (idle mode refreshes a slice of rows per frame)

// rgb irradiance / pi, a = accumulated sample count (0 = no probe). fp32: with history weights up to 63/64 an
// update moves the value by a fraction fp16 would round away.
layout(binding = 0, rgba32f) uniform image2D RadiosityProbes;

shared uint validMask;
shared vec3 raySum[16];

void main()
{
	ivec2 probe = ivec2(gl_WorkGroupID.xy) + ivec2(0, probeRowStart);
	if (any(greaterThanEqual(probe, probeRegion))) return;

	int lane = int(gl_LocalInvocationID.x);
	ivec2 blockOrigin = probe * 4;
	ivec2 texel = blockOrigin + ivec2(lane & 3, lane >> 2);

	if (lane == 0) validMask = 0u;
	barrier();
	bool valid = texelFetch(CardIndex, texel, 0).r != 0xFFFFu && texelFetch(CardLocalPosition, texel, 0).w > 0.5;
	if (valid) atomicOr(validMask, 1u << uint(lane));
	barrier();

	uint mask = validMask;
	if (mask == 0u)
	{
		if (lane == 0) imageStore(RadiosityProbes, probe, vec4(0.0));
		return;
	}

	// Anchor texel: one of the four centre texels when captured, otherwise any captured texel of the block.
	int anchor = -1;
	if ((mask & (1u << 5u)) != 0u) anchor = 5;
	else if ((mask & (1u << 6u)) != 0u) anchor = 6;
	else if ((mask & (1u << 9u)) != 0u) anchor = 9;
	else if ((mask & (1u << 10u)) != 0u) anchor = 10;
	else anchor = findLSB(mask);
	ivec2 anchorTexel = blockOrigin + ivec2(anchor & 3, anchor >> 2);

	uint cardId = texelFetch(CardIndex, anchorTexel, 0).r;
	Card card = cards[int(cardId)];
	SceneObject o = sceneObjects[int(card.origin.w)];
	vec3 localPos = texelFetch(CardLocalPosition, anchorTexel, 0).xyz;
	vec3 position = (o.localToWorld * vec4(localPos, 1.0)).xyz;
	vec3 N = SceneObjectLocalToWorldDir(o, normalize(texelFetch(CardNormal, anchorTexel, 0).xyz * 2.0 - 1.0));

	// Ray `lane`: stratified 4x4 over the cosine-weighted hemisphere, jittered per probe and frame.
	vec3 tangent, bitangent;
	TangentFrame(N, tangent, bitangent);
	uint seed = HashCoord(probe, uint(frameIndex));
	vec2 jitter = vec2(HashToFloat(seed + uint(lane) * 7919u), HashToFloat((seed ^ 0x9E3779B9u) + uint(lane) * 104729u));
	vec2 u = (vec2(lane & 3, lane >> 2) + jitter) * 0.25;
	vec3 h = CosineSampleHemisphere(u);
	vec3 direction = normalize(tangent * h.x + bitangent * h.y + N * h.z);

	float sdfVoxel = SdfVoxelSize();
	vec3 origin = position + N * sdfVoxel;
	float maxDistance = rcNearDistance > 0.0 ? rcNearDistance : sdfGridSize;

	vec3 radiance = giSkyRadiance;
	float hitT;
	bool hit = TraceSdf(origin, direction, maxDistance, hitT);
	if (!hit && rcNearDistance > 0.0)
	{
		vec3 far = origin + direction * maxDistance;
		vec3 cached;
		if (SampleSdf(far) >= rcProbeSpacing * 0.25 && SampleRadianceCache(far, direction, cached))
		{
			radiance = cached;
		}
		else
		{
			hit = TraceSdf(far, direction, sdfGridSize, hitT);
			hitT += maxDistance;
		}
	}
	if (hit)
	{
		vec3 hitPos = origin + direction * hitT;
		vec3 lit;
		radiance = SampleSurfaceCacheAtHit(hitPos, SdfNormal(hitPos), sdfVoxel * 2.0, FinalLightingPrev, lit) ? lit : vec3(0.0);
	}

	raySum[lane] = radiance;
	barrier();

	if (lane == 0)
	{
		vec3 estimate = vec3(0.0);
		for (int i = 0; i < 16; i++) estimate += raySum[i];
		estimate *= 1.0 / 16.0;

		// Sample-count accumulation: the history weight grows with the samples held, up to the cap, so a quiet
		// scene converges to a value that stops wandering (a constant blend keeps re-rolling 1 - blend of the
		// 16-ray noise every update, which reads as slow blotchy drift on the walls).
		vec4 history = imageLoad(RadiosityProbes, probe);
		float weight = 0.0;
		float count = 1.0;
		if (history.a > 0.5)
		{
			if (radiosityMaxSamples > 0.0)
			{
				float n = min(history.a, max(radiosityMaxSamples, 1.0) - 1.0);
				weight = n / (n + 1.0);
				count = n + 1.0;
			}
			else
			{
				weight = radiosityBlend;
			}
		}
		imageStore(RadiosityProbes, probe, vec4(mix(estimate, history.rgb, weight), count));
	}
}

/////Compute
