// World-space radiance cache (Lumen's radiance cache): a grid of probes over the GI volume, each a
// full-sphere octahedral map of incoming radiance and hit distance, re-traced a few hundred probes per
// frame and accumulated over time. Screen probe rays and radiosity rays that travel further than the near
// field read it instead of tracing on: the far field (small bright emitters sampled by single rays) becomes
// a temporally stable, texel-prefiltered estimate instead of per-frame noise.
//
// Requires Engine/Sampling.glsl. Uniforms are set by RadianceCachePass.SetUniforms; rcNearDistance is 0
// when the cache is unavailable, in which case callers trace the whole distance field.

uniform sampler2D RadianceCacheAtlas;   // rgb radiance, a = hit distance + 1 (0 = probe without valid data)
uniform vec3 rcGridMin;
uniform float rcProbeSpacing;
uniform int rcProbesPerAxis;
uniform int rcProbesPerRow;             // probe tiles per atlas row
uniform int rcProbeResolution;          // stored octahedral texels per probe; a tile adds a one texel border
uniform vec2 rcAtlasSize;               // texels
uniform float rcNearDistance;           // rays trace the distance field this far, then read the cache
uniform int rcFarShortcut;              // 1: far targets skip the target visibility pass (see SampleRadianceCache)

#ifdef RC_MOVING_MASK
// Shaders that can trace on (screen probes, radiosity) define RC_MOVING_MASK: the parts of a probe's map that saw a
// moving object in its last two updates (a 4x4 grid of cells over the octahedral map, RadianceCacheResolve.shader)
// are out of date, and a lookup that would read one returns false so the ray traces the scene as it is now.
layout(std430, binding = 4) readonly buffer RadianceCacheProbeMoving
{
	int rcProbeFrames[]; // [2 * rcProbeCount + probe] = moving cells, bits 0-15 last update, 16-31 the one before
};
uniform int rcProbeCount;
uniform int rcMovingRecent; // 0 when nothing has moved for longer than any probe keeps its moving cells (no reads)
#endif

int RadianceCacheTileSize()
{
	return rcProbeResolution + 2;
}

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
	return ivec2(probeIndex % rcProbesPerRow, probeIndex / rcProbesPerRow) * RadianceCacheTileSize();
}

vec3 RadianceCacheProbePosition(ivec3 coord)
{
	return rcGridMin + vec3(coord) * rcProbeSpacing;
}

// Bilinear lookup of one probe's map at an octahedral coordinate.
vec4 RadianceCacheProbeSampleOct(int probeIndex, vec2 oct)
{
	vec2 texel = vec2(RadianceCacheTileOrigin(probeIndex)) + 1.0 + oct * float(rcProbeResolution);
	return texture(RadianceCacheAtlas, texel / rcAtlasSize);
}

// The probe's cells that saw a moving object in its last two updates (0 without RC_MOVING_MASK).
uint RadianceCacheMovingCells(int probeIndex)
{
#ifdef RC_MOVING_MASK
	if (rcMovingRecent == 0) return 0u;
	uint cells = uint(rcProbeFrames[2 * rcProbeCount + probeIndex]);
	return (cells | (cells >> 16u)) & 0xFFFFu;
#else
	return 0u;
#endif
}

// True when the probe's map around this direction saw a moving object recently (the lookup must not use it).
bool RadianceCacheMovingAt(uint cells, vec2 oct)
{
	if (cells == 0u) return false;
	ivec2 cell = clamp(ivec2(oct * 4.0), ivec2(0), ivec2(3));
	return (cells & (1u << uint(cell.y * 4 + cell.x))) != 0u;
}

// Bilinear lookup of one probe's map in the given world direction.
vec4 RadianceCacheProbeSample(ivec3 coord, vec3 direction)
{
	return RadianceCacheProbeSampleOct(RadianceCacheProbeIndex(coord), DirectionToOctahedral(direction));
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
	vec2 octAlong = DirectionToOctahedral(direction); // shared by the eight probes

	float weights[8];
	vec3 alongRadiance[8];
	uint movingCells[8];
	float hitSum = 0.0;
	float hitMin = 1e9;
	float hitMax = 0.0;
	float weightSum = 0.0;
	for (int i = 0; i < 8; i++)
	{
		weights[i] = 0.0;
		alongRadiance[i] = vec3(0.0);
		movingCells[i] = 0u;
		ivec3 corner = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
		vec3 w3 = mix(1.0 - f, f, vec3(corner));
		float w = w3.x * w3.y * w3.z;
		if (w < 1e-4) continue;

		ivec3 coord = base + corner;
		int probeIndex = RadianceCacheProbeIndex(coord);
		movingCells[i] = RadianceCacheMovingCells(probeIndex);
		vec3 probePosition = RadianceCacheProbePosition(coord);
		vec3 toPoint = p - probePosition;
		float distanceToPoint = length(toPoint);
		if (distanceToPoint > 1e-3)
		{
			vec4 towards = RadianceCacheProbeSampleOct(probeIndex, DirectionToOctahedral(toPoint / distanceToPoint));
			if (towards.a < 0.5) continue;
			float depth = towards.a - 1.0;
			w *= clamp((depth - (distanceToPoint - occlusionMargin)) / occlusionMargin, 0.0, 1.0);
			if (w < 1e-4) continue;
		}

		// Hit distance along the direction as p would measure it (planar geometry assumption).
		vec4 along = RadianceCacheProbeSampleOct(probeIndex, octAlong);
		if (along.a < 0.5) continue;
		if (RadianceCacheMovingAt(movingCells[i], octAlong)) return false;
		float hitDistance = max((along.a - 1.0) - dot(toPoint, direction), 0.0);

		weights[i] = w;
		alongRadiance[i] = along.rgb;
		hitSum += hitDistance * w;
		hitMin = min(hitMin, hitDistance);
		hitMax = max(hitMax, hitDistance);
		weightSum += w;
	}

	radiance = vec3(0.0);
	if (weightSum <= 1e-3) return false;
	if (hitMin < rcProbeSpacing * 1.5) return false;

	// Far targets: the parallax between a probe and p is below the map's texel size, so the samples along
	// the ray direction already are the answer and the second lookup pass is skipped -- but only when the
	// probes agree on what they see. A probe whose hit lies much further than the others looks past an
	// occluder edge (over a wall top, through an opening) and is exactly what the visibility pass rejects.
	if (rcFarShortcut != 0 && hitMin >= rcProbeSpacing * 4.0 && hitMax - hitMin <= max(2.0 * rcProbeSpacing, 0.2 * hitMin))
	{
		vec3 farSum = vec3(0.0);
		for (int i = 0; i < 8; i++) farSum += alongRadiance[i] * weights[i];
		radiance = farSum / weightSum;
		return true;
	}

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
		vec2 octTarget = DirectionToOctahedral(toTarget / max(distanceToTarget, 1e-3));
		vec4 s = RadianceCacheProbeSampleOct(RadianceCacheProbeIndex(coord), octTarget);
		if (s.a < 0.5) continue;
		if (RadianceCacheMovingAt(movingCells[i], octTarget)) return false;

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
