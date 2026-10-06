// World-space radiance cache (Lumen's radiance cache): a grid of probes over the GI volume, each a
// full-sphere octahedral map of incoming radiance and hit distance, re-traced a few hundred probes per
// frame and accumulated over time. Screen probe rays and radiosity rays that travel further than the near
// field read it instead of tracing on: the far field (small bright emitters sampled by single rays) becomes
// a temporally stable, texel-prefiltered estimate instead of per-frame noise.
//
// Requires Engine/Sampling.glsl. Uniforms are set by RadianceCachePass.SetUniforms; rcNearDistance is 0
// when the cache is unavailable, in which case callers trace the whole distance field.

#define RC_RESOLUTION 8                 // octahedral texels per probe
#define RC_TILE (RC_RESOLUTION + 2)     // plus a one texel border so bilinear filtering is seamless

uniform sampler2D RadianceCacheAtlas;   // rgb radiance, a = hit distance + 1 (0 = probe without valid data)
uniform vec3 rcGridMin;
uniform float rcProbeSpacing;
uniform int rcProbesPerAxis;
uniform int rcProbesPerRow;             // probe tiles per atlas row
uniform vec2 rcAtlasSize;               // texels
uniform float rcNearDistance;           // rays trace the distance field this far, then read the cache

int RadianceCacheProbeIndex(ivec3 coord)
{
	return coord.x + (coord.y + coord.z * rcProbesPerAxis) * rcProbesPerAxis;
}

ivec3 RadianceCacheProbeCoord(int probeIndex)
{
	int perAxis = rcProbesPerAxis;
	return ivec3(probeIndex % perAxis, (probeIndex / perAxis) % perAxis, probeIndex / (perAxis * perAxis));
}

ivec2 RadianceCacheTileOrigin(int probeIndex)
{
	return ivec2(probeIndex % rcProbesPerRow, probeIndex / rcProbesPerRow) * RC_TILE;
}

vec3 RadianceCacheProbePosition(ivec3 coord)
{
	return rcGridMin + vec3(coord) * rcProbeSpacing;
}

// Bilinear lookup of one probe's map in the given world direction.
vec4 RadianceCacheProbeSample(ivec3 coord, vec3 direction)
{
	vec2 oct = DirectionToOctahedral(direction);
	vec2 texel = vec2(RadianceCacheTileOrigin(RadianceCacheProbeIndex(coord))) + 1.0 + oct * float(RC_RESOLUTION);
	return texture(RadianceCacheAtlas, texel / rcAtlasSize);
}

// Radiance arriving at world point p from `direction`, interpolated from the eight surrounding probes.
//  - Probes inside geometry or not traced yet are skipped.
//  - Probe occlusion (Lumen): a probe whose stored hit distance towards p is shorter than the distance to p
//    has a wall between them and is skipped; without this, probes outside a room leak sky light into it.
//  - Parallax: a probe 1-2 m away from p sees something else along `direction` than p does (sky past a
//    ceiling edge instead of the ceiling). The probes' hit distances give the surface point p would see,
//    and each probe is looked up towards that point instead. When that surface is closer than
//    1.5 probe spacings the correction is too coarse and the function returns false: the caller traces on.
bool SampleRadianceCache(vec3 p, vec3 direction, out vec3 radiance)
{
	vec3 g = (p - rcGridMin) / rcProbeSpacing;
	ivec3 base = clamp(ivec3(floor(g)), ivec3(0), ivec3(rcProbesPerAxis - 2));
	vec3 f = clamp(g - vec3(base), vec3(0.0), vec3(1.0));
	float occlusionMargin = rcProbeSpacing * 0.25;

	float weights[8];
	float hitSum = 0.0;
	float hitMin = 1e9;
	float weightSum = 0.0;
	for (int i = 0; i < 8; i++)
	{
		weights[i] = 0.0;
		ivec3 corner = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
		vec3 w3 = mix(1.0 - f, f, vec3(corner));
		float w = w3.x * w3.y * w3.z;
		if (w < 1e-4) continue;

		ivec3 coord = base + corner;
		vec3 probePosition = RadianceCacheProbePosition(coord);
		vec3 toPoint = p - probePosition;
		float distanceToPoint = length(toPoint);
		if (distanceToPoint > 1e-3)
		{
			vec4 towards = RadianceCacheProbeSample(coord, toPoint / distanceToPoint);
			if (towards.a < 0.5) continue;
			float depth = towards.a - 1.0;
			w *= clamp((depth - (distanceToPoint - occlusionMargin)) / occlusionMargin, 0.0, 1.0);
			if (w < 1e-4) continue;
		}

		// Hit distance along the direction as p would measure it (planar geometry assumption).
		vec4 along = RadianceCacheProbeSample(coord, direction);
		if (along.a < 0.5) continue;
		float hitDistance = max((along.a - 1.0) - dot(toPoint, direction), 0.0);

		weights[i] = w;
		hitSum += hitDistance * w;
		hitMin = min(hitMin, hitDistance);
		weightSum += w;
	}

	radiance = vec3(0.0);
	if (weightSum <= 1e-3) return false;
	if (hitMin < rcProbeSpacing * 1.5) return false;

	// Look each probe up towards the surface point p sees. The probe's stored hit distance in that
	// direction also says whether it actually reaches that point: a probe outside a room may see p
	// through an opening yet look at the target through a wall, and would return the wall's outside.
	vec3 target = p + direction * (hitSum / weightSum);
	vec3 sum = vec3(0.0);
	float visibleWeightSum = 0.0;
	for (int i = 0; i < 8; i++)
	{
		if (weights[i] <= 0.0) continue;
		ivec3 coord = base + ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
		vec3 toTarget = target - RadianceCacheProbePosition(coord);
		float distanceToTarget = length(toTarget);
		vec4 s = RadianceCacheProbeSample(coord, toTarget / max(distanceToTarget, 1e-3));
		if (s.a < 0.5) continue;

		float margin = max(occlusionMargin, 0.1 * distanceToTarget);
		float visibility = clamp(((s.a - 1.0) - (distanceToTarget - margin)) / margin, 0.0, 1.0);
		float w = weights[i] * visibility;
		sum += s.rgb * w;
		visibleWeightSum += w;
	}

	if (visibleWeightSum <= 1e-3) return false;
	radiance = sum / visibleWeightSum;
	return true;
}
