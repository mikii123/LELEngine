//Compute

#version 430

#include "Engine/ScreenProbes.glsl"
#include "Engine/Sampling.glsl"

// Turns the per-probe ray results back into the octahedral radiance map and applies Lumen's temporal probe
// filter. One workgroup per probe, one thread per octahedral texel. A texel gathers the rays that sampled it
// (Engine/ProbeImportance.shader's ray table): its own ray, the four quadrant rays of a bright direction, or the
// one ray of the dim 2x2 block it belongs to, which counts as half a sample. Every texel gets something each frame.
//
// The history is the previous frame's *unfiltered* composed radiance of the probe this one reprojects to. Feeding
// the spatially filtered map back would filter the carried-over directions again every frame, blurring lighting
// along surfaces without bound and leaking bright sky samples across whole walls.
//
// Accumulation is sample-count based: the history weight grows with the samples a texel holds (up to
// probeMaxHistorySamples), so lighting that stays put converges to a noise-free value. What keeps it from lagging
// behind lighting that changes:
//   - anti-lag with a fast history (as in NVIDIA's ReBLUR / ReLAX): every texel also keeps the mean of its last few
//     samples (probeFastSamples). When the long history is further from it than the noise of a mean of that many
//     samples explains (the texel's own spread, from its second moment), the long history lags behind a change (a
//     light appeared or left) and restarts from the fast one. A mean of a few samples tells a change from the
//     heavy-tailed noise of single rays (a ray that clips a small emitter) far better than one sample does;
//   - the same test on the whole probe: the cosine-weighted sums of the fast and the long histories over the 64
//     directions, against the noise of the sum. A small emitter a few metres away covers about one direction, whose
//     own samples are all or nothing, so no single direction can tell it left; the probe's total can, and when it
//     lags every direction restarts from its fast history;
//   - and a relative one, weighted towards the probe's bright directions (the long history's luminance): when that
//     mean moved by more than probeJumpThreshold, the light the probe mostly sees appeared, left or went out, and
//     every direction restarts from this frame's sample (the old probe-wide change detector, on fast means);
//   - moving hits: a texel whose rays hit an object that moved, now or in the last few frames, keeps a short
//     history (it follows the object, and forgets it fast where it left);
//   - a probe on a moving surface keeps a short history in every direction.
// Each probe also reports how much of its radiance changed this frame (ProbeChange): the per-pixel temporal
// filter shortens its own history there, so the pixels follow the probes.
layout(local_size_x = 8, local_size_y = 8) in;

uniform sampler2D RayResults;
uniform sampler2D PreviousProbeRadiance;
uniform sampler2D PreviousProbeMeta;
uniform sampler2D PreviousProbeFast;
uniform usampler2D ProbeSelection;
uniform float probeHistoryWeight;     // constant history weight when probeMaxHistorySamples is 0
uniform float probeMaxHistorySamples; // cap of the per-texel sample count (0 = constant weight)
uniform float probeFastSamples;       // samples the fast history averages
uniform float probeMovingSamples;     // the cap while a texel sees, or recently saw, a moving object
uniform float probeMovingFrames;      // frames after a moving hit that the short cap holds
uniform float changeSigma;            // the long history lags when it is this many standard deviations of the fast mean off...
uniform float changeFloor;            // ...plus this fraction of its value (half of it for the whole probe's sum)
uniform float probeJumpThreshold;     // relative change of the probe's brightness-weighted fast mean that restarts it

// fp16 is enough for the radiance: the fresh samples are noisy (one to four rays per texel), so the per-update
// change is far above the fp16 quantum and the accumulation does not stall. The same holds for the second moment of
// the record (squares of noisy samples), and its counts are small integers, exact in fp16.
layout(binding = 0, rgba16f) uniform writeonly image2D OutRadiance;
// x samples, y mean luminance^2 of the samples, z frames since a moving hit, w samples in the fast history
layout(binding = 1, rgba16f) uniform writeonly image2D OutMeta;
layout(binding = 2, r16f) uniform writeonly image2D OutChange;  // per probe: fraction of its radiance that changed
layout(binding = 3, rgba16f) uniform writeonly image2D OutFast; // the fast history (rgb)

// The probe's change amount in fixed point (shared-memory atomics) of 1/4096: luminance capped at 1000 keeps 64
// lanes in range.
#define FIXED_SCALE 4096.0
shared int changedFixed;
shared int totalFixed;

// The whole-probe tests' sums, reduced over the 64 directions in shared memory: x = fast means, y = long means,
// z = variance of the fast means (all cosine weighted), w = directions taking part; and weighted by the long mean's
// brightness: x = fast means, y = long means.
shared vec4 reduceSums[64];
shared vec2 reduceWeighted[64];

float Luminance(vec3 c)
{
	return dot(c, vec3(0.2126, 0.7152, 0.0722));
}

void main()
{
	ivec2 probe = ivec2(gl_WorkGroupID.xy);
	ivec2 oct = ivec2(gl_LocalInvocationID.xy);
	ivec2 texel = probe * PROBE_RESOLUTION + oct;
	int octIndex = oct.y * PROBE_RESOLUTION + oct.x;
	int lane = int(gl_LocalInvocationIndex);

	uvec4 selection = texelFetch(ProbeSelection, probe, 0);
	if (lane == 0)
	{
		changedFixed = 0;
		totalFixed = 0;
	}

	// ---- this frame's estimate of the texel from the rays that sampled it (its own, its four, or its block's one)
	int firstRay = octIndex;
	int extraRay = 0;
	int rayCount = 1;
	bool blockRay = false;
	if (SelectionValid(selection))
	{
		TexelRays(selection, octIndex, firstRay, extraRay, rayCount, blockRay);
	}

	vec3 sum = vec3(0.0);
	float hitSum = 0.0;
	int hits = 0;
	int rays = 0;
	bool ownRay = !blockRay;
	bool movingHit = false;
	bool invalid = false;
	for (int k = 0; k < rayCount; k++)
	{
		int r = k == 0 ? firstRay : extraRay + k - 1;
		vec4 result = texelFetch(RayResults, probe * PROBE_RESOLUTION + ivec2(r % PROBE_RESOLUTION, r / PROBE_RESOLUTION), 0);
		if (result.a < -1.5 && result.a > -2.5)
		{
			invalid = true;
			continue;
		}

		sum += result.rgb;
		rays++;
		movingHit = movingHit || RayHitMoving(result.a);
		float hitDistance = RayHitDistance(result.a);
		if (hitDistance >= 0.0)
		{
			hitSum += hitDistance;
			hits++;
		}
	}

	vec4 history = vec4(0.0, 0.0, 0.0, -2.0);
	vec4 meta = vec4(0.0, 0.0, 255.0, 0.0);
	vec3 fastHistory = vec3(0.0);
	if (ReprojectionValid(selection))
	{
		ivec2 previous = SelectionPreviousProbe(selection);
		history = texelFetch(PreviousProbeRadiance, previous * PROBE_RESOLUTION + oct, 0);
		meta = texelFetch(PreviousProbeMeta, previous * PROBE_RESOLUTION + oct, 0);
		fastHistory = texelFetch(PreviousProbeFast, previous * PROBE_RESOLUTION + oct, 0).rgb;
	}
	bool hasHistory = history.a > -1.5 && meta.x > 0.0;
	// ("active" would be the natural name, but it is a reserved word in GLSL.)
	bool sampled = !invalid && rays > 0;
	bool accumulate = sampled && hasHistory && probeMaxHistorySamples > 0.0;
	float cosTheta = max(HemiOctahedralToDirection((vec2(oct) + 0.5) / float(PROBE_RESOLUTION)).z, 0.0);

	// ---- the fresh estimate, the fast history and the per-texel anti-lag test
	vec4 fresh = vec4(0.0, 0.0, 0.0, -2.0);
	float freshLuminance = 0.0;
	float freshSquare = 0.0;
	float weight = 1.0;
	float age = 255.0;
	vec3 fast = vec3(0.0);
	float fastTotal = 0.0;
	bool eligible = false;
	bool texelChanged = false;
	if (sampled)
	{
		fresh = vec4(sum / float(rays), hits > 0 ? hitSum / float(hits) : -1.0);
		freshLuminance = min(Luminance(fresh.rgb), 1e4);
		// The second moment is stored in fp16: luminance capped at 200 keeps its square in range.
		freshSquare = min(freshLuminance, 200.0) * min(freshLuminance, 200.0);
		// A shared block ray says less about this texel than its own rays do.
		weight = ownRay ? 1.0 : 0.5;
		age = movingHit ? 0.0 : min((hasHistory ? meta.z : 255.0) + 1.0, 255.0);
	}

	vec4 sums = vec4(0.0);
	vec2 weightedSums = vec2(0.0);
	if (accumulate)
	{
		// The fast history: the mean of the last few samples.
		float fastKept = min(meta.w, max(probeFastSamples - weight, 0.0));
		fastTotal = fastKept + weight;
		fast = (fastHistory * fastKept + fresh.rgb * weight) / fastTotal;

		float historyLuminance = Luminance(history.rgb);
		float fastLuminance = Luminance(fast);
		if (meta.x >= 2.0 * probeFastSamples && fastTotal >= probeFastSamples - 0.01)
		{
			eligible = true;
			float cappedHistory = min(historyLuminance, 200.0);
			float variance = max(meta.y - cappedHistory * cappedHistory, 0.0) / fastTotal;
			float limit = changeSigma * sqrt(variance) + changeFloor * max(historyLuminance, fastLuminance) + 0.005;
			texelChanged = abs(fastLuminance - historyLuminance) > limit;

			sums = vec4(fastLuminance * cosTheta, historyLuminance * cosTheta, variance * cosTheta * cosTheta, 1.0);
			// Weighted by the long mean's brightness: what the probe mostly sees (the test below is relative, so the
			// weights need no normalization).
			float w = historyLuminance * cosTheta;
			weightedSums = vec2(w * fastLuminance, w * historyLuminance);
		}
	}

	// ---- the whole probe's tests: far less noisy than any direction alone (tree reduction over the 64 directions,
	// unrolled: Intel's compiler warns about a barrier inside a loop)
	reduceSums[lane] = sums;
	reduceWeighted[lane] = weightedSums;
	barrier();
	if (lane < 32) { reduceSums[lane] += reduceSums[lane + 32]; reduceWeighted[lane] += reduceWeighted[lane + 32]; }
	barrier();
	if (lane < 16) { reduceSums[lane] += reduceSums[lane + 16]; reduceWeighted[lane] += reduceWeighted[lane + 16]; }
	barrier();
	if (lane < 8) { reduceSums[lane] += reduceSums[lane + 8]; reduceWeighted[lane] += reduceWeighted[lane + 8]; }
	barrier();
	if (lane < 4) { reduceSums[lane] += reduceSums[lane + 4]; reduceWeighted[lane] += reduceWeighted[lane + 4]; }
	barrier();
	if (lane < 2) { reduceSums[lane] += reduceSums[lane + 2]; reduceWeighted[lane] += reduceWeighted[lane + 2]; }
	barrier();
	if (lane < 1) { reduceSums[lane] += reduceSums[lane + 1]; reduceWeighted[lane] += reduceWeighted[lane + 1]; }
	barrier();
	vec4 probeSums = reduceSums[0];
	vec2 probeWeighted = reduceWeighted[0];
	bool probeJumped = probeSums.w >= 16.0 && abs(probeWeighted.x - probeWeighted.y) > probeJumpThreshold * max(probeWeighted.x, probeWeighted.y) + 1e-5;
	bool probeLags = !probeJumped && probeSums.w >= 16.0
		&& abs(probeSums.x - probeSums.y) > changeSigma * sqrt(probeSums.z) + 0.5 * changeFloor * max(probeSums.x, probeSums.y) + 0.01;

	vec4 result;
	vec4 outMeta;
	bool changed = false;
	if (!sampled)
	{
		// The probe is on the sky (or this texel got no ray, which the table never leaves): nothing to accumulate.
		result = vec4(0.0, 0.0, 0.0, -2.0);
		outMeta = vec4(0.0, 0.0, 255.0, 0.0);
	}
	else if (!hasHistory)
	{
		// A new probe (disocclusion, adaptive placement): not a lighting change, the pixels judge their own history.
		result = fresh;
		fast = fresh.rgb;
		outMeta = vec4(weight, freshSquare, age, weight);
	}
	else if (!accumulate)
	{
		result = vec4(mix(fresh.rgb, history.rgb, probeHistoryWeight), fresh.a);
		fast = result.rgb;
		outMeta = vec4(1.0, freshSquare, age, 1.0);
	}
	else
	{
		changed = texelChanged || (eligible && (probeLags || probeJumped));
		float cap = probeMaxHistorySamples;
		if (age < probeMovingFrames || SelectionMoving(selection)) cap = min(cap, probeMovingSamples);
		float total;
		if (eligible && probeJumped)
		{
			// The probe's light jumped: this frame's sample alone (the fast history still holds the old light).
			total = weight;
			fastTotal = weight;
			fast = fresh.rgb;
			result = fresh;
		}
		else if (changed)
		{
			// The long history restarts from the fast one (which already holds this frame's sample).
			total = min(fastTotal, cap);
			result = vec4(fast, fresh.a);
		}
		else
		{
			float samples = min(meta.x, max(cap - weight, 0.0));
			total = samples + weight;
			result = vec4((history.rgb * samples + fresh.rgb * weight) / total, fresh.a);
		}

		float kept = total - weight;
		outMeta = vec4(total, (meta.y * kept + freshSquare * weight) / total, age, fastTotal);
		changed = changed || age < probeMovingFrames;
	}

	imageStore(OutRadiance, texel, result);
	imageStore(OutMeta, texel, outMeta);
	imageStore(OutFast, texel, vec4(fast, 0.0));

	// ---- how much of the probe's radiance (cosine weighted) changed or follows a moving object this frame. A direction
	// counts with the brighter of its old and new radiance: a light that went out matters as much as one that came on.
	float energyLuminance = max(freshLuminance, hasHistory ? min(Luminance(history.rgb), 1e4) : 0.0);
	int energy = int(min(energyLuminance, 1000.0) * cosTheta * FIXED_SCALE);
	atomicAdd(totalFixed, energy);
	if (changed) atomicAdd(changedFixed, energy);
	barrier();
	if (lane == 0)
	{
		float amount = totalFixed > 0 ? float(changedFixed) / float(totalFixed) : 0.0;
		imageStore(OutChange, probe, vec4(amount));
	}
}

/////Compute
