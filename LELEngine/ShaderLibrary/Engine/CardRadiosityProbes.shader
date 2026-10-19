//Compute

#version 430

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
// Rays that would read cache texels of a moving object trace on (see RadianceCache.glsl).
#define RC_MOVING_MASK 1
#include "Engine/RadianceCache.glsl"

// Surface cache radiosity probes (Lumen's radiosity): one hemispherical probe per 4x4 block of card texels.
// Every frame each probe traces 16 cosine-distributed rays from its anchor texel (one ray per thread), the
// rays are averaged (an estimate of irradiance / pi) and accumulated into the probe. Texels then interpolate
// the probes of their card (CardLighting.shader). Sixteen rays shared by sixteen texels cost a quarter of
// four rays per texel, and the interpolation plus the 16-ray average give far smoother indirect lighting
// than per-texel estimates.
//
// As the screen probes do, a probe whose fresh estimate is far outside the spread of its own updates (second
// moment) restarts its accumulation (a light appeared or left), and a probe whose rays hit a moving object keeps a
// short history for a few updates (it follows the object, and forgets it fast where it left).
layout(local_size_x = 16) in;

uniform usampler2D CardIndex;
uniform sampler2D CardNormal;
uniform sampler2D FinalLightingPrev;
#include "Engine/Sky.glsl"
uniform int frameIndex;
uniform float radiosityBlend;      // constant history weight per update when radiosityMaxSamples is 0
uniform float radiosityMaxSamples; // sample-count accumulation cap (lower while the scene is changing, see SurfaceCachePass)
uniform float movingSamples;       // the cap while the probe's rays hit, or recently hit, a moving object
uniform float movingFrames;        // updates after a moving hit that the short cap holds
uniform float changeSigma;         // change test: standard deviations of the probe's updates...
uniform float changeFloor;         // ...plus this fraction of its value
uniform ivec2 probeRegion;         // probe grid in use (x = columns, y = rows)
uniform int probeRowStart;         // first probe row of this dispatch (idle mode refreshes a slice of rows per frame)

// rgb irradiance / pi, a = accumulated sample count (0 = no probe). fp32: with history weights up to 63/64 an
// update moves the value by a fraction fp16 would round away.
layout(binding = 0, rgba32f) uniform image2D RadiosityProbes;
// x = mean squared luminance of the updates, y = updates since a ray hit a moving object, z = the change test's
// verdict on the previous update (+1 brighter, -1 darker): one outlier is noise, the same verdict twice a change.
layout(binding = 1, rgba32f) uniform image2D RadiosityMeta;

shared uint validMask;
shared uint movingMask;
shared vec3 raySum[16];

void main()
{
	ivec2 probe = ivec2(gl_WorkGroupID.xy) + ivec2(0, probeRowStart);
	if (any(greaterThanEqual(probe, probeRegion))) return;

	int lane = int(gl_LocalInvocationID.x);
	ivec2 blockOrigin = probe * 4;
	ivec2 texel = blockOrigin + ivec2(lane & 3, lane >> 2);

	if (lane == 0)
	{
		validMask = 0u;
		movingMask = 0u;
	}
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

	vec3 radiance = SkyRadiance(direction);
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
		int objectIndex;
		radiance = SampleSurfaceCacheAtHitObject(hitPos, SdfNormal(hitPos), sdfVoxel * 2.0, FinalLightingPrev, lit, objectIndex) ? lit : vec3(0.0);
		if (SceneObjectMoved(objectIndex)) atomicOr(movingMask, 1u);
	}

	raySum[lane] = radiance;
	barrier();

	if (lane == 0)
	{
		vec3 estimate = vec3(0.0);
		for (int i = 0; i < 16; i++) estimate += raySum[i];
		estimate *= 1.0 / 16.0;
		float freshLuminance = dot(estimate, vec3(0.2126, 0.7152, 0.0722));

		// Sample-count accumulation: the history weight grows with the samples held, up to the cap, so a quiet
		// scene converges to a value that stops wandering (a constant blend keeps re-rolling 1 - blend of the
		// 16-ray noise every update, which reads as slow blotchy drift on the walls).
		vec4 history = imageLoad(RadiosityProbes, probe);
		vec4 meta = imageLoad(RadiosityMeta, probe);
		bool hasHistory = history.a > 0.5;
		float age = movingMask != 0u ? 0.0 : min((hasHistory ? meta.y : 255.0) + 1.0, 255.0);
		float weight = 0.0;
		float count = 1.0;
		float verdict = 0.0;
		if (hasHistory)
		{
			if (radiosityMaxSamples > 0.0)
			{
				float n = history.a;
				float cap = max(radiosityMaxSamples, 1.0);
				if (age < movingFrames) cap = min(cap, movingSamples);

				// Change test, once the probe holds enough updates for their spread to mean something.
				float historyLuminance = dot(history.rgb, vec3(0.2126, 0.7152, 0.0722));
				if (n >= 4.0)
				{
					float spread = sqrt(max(meta.x - historyLuminance * historyLuminance, 0.0));
					float difference = freshLuminance - historyLuminance;
					if (abs(difference) > changeSigma * spread + changeFloor * max(historyLuminance, freshLuminance) + 0.002)
					{
						verdict = sign(difference);
						if (verdict == meta.z)
						{
							n = min(n, 1.0);
							verdict = 0.0;
						}
					}
				}

				n = min(n, cap - 1.0);
				weight = n / (n + 1.0);
				count = n + 1.0;
			}
			else
			{
				weight = radiosityBlend;
			}
		}
		imageStore(RadiosityProbes, probe, vec4(mix(estimate, history.rgb, weight), count));
		float secondMoment = hasHistory ? mix(freshLuminance * freshLuminance, meta.x, weight) : freshLuminance * freshLuminance;
		imageStore(RadiosityMeta, probe, vec4(secondMoment, age, verdict, 0.0));
	}
}

/////Compute
