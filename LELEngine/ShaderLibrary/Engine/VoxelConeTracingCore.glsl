// Core cone tracing against a radiance volume.
// Shared by material shaders (through VoxelConeTracing.glsl) and by the voxel light injection
// compute shader, which traces the previous frame's volume to add the indirect bounce.
//
// The volume stores premultiplied radiance (rgb) and occupancy (a); mip levels average both,
// so front-to-back compositing with (1 - alpha) weights is correct.
//
// Visibility modes (giTraceMode):
//   0  voxel cones: occlusion from the voxel alpha mips (fast, leaks through thin geometry: at coarse mips a
//                   wall one voxel thick averages with the empty space on both sides and cones pass through)
//   1  SDF detail:  the first giSdfDetailDistance of every cone is sphere-traced through the global
//                   distance field (exact visibility, no leaks through nearby walls); a hit reads the
//                   radiance volume at the surface, otherwise the voxel cone continues from there.
//                   Binary per cone: six cones give seven occlusion levels (banded corners).
//   2  SDF cones:   the whole cone's visibility comes from the global distance field (distance field AO):
//                   continuous, it only decreases (no cone sees through a wall), and where it decreases the
//                   cone takes the radiance of the surfaces there from the voxel volume.

#include "Engine/DistanceField.glsl"
#include "Engine/VoxelRadiance.glsl"

uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform float giDiffuseStrength;
uniform float giSpecularStrength;
uniform float giOcclusionStrength;
uniform float giConeMaxDistance;
uniform int giTraceMode;
uniform float giSdfDetailDistance;
// SDF cones take two half-diameter steps per footprint, so each sample's opacity is corrected to
// 1 - (1 - a)^exponent; 0.5 would be exact for a uniform medium. A wall between two samples is read by both at
// partial occupancy (trilinear), which 0.5 lets through by 5-20% depending on where the samples fall; 0.6 keeps most
// of that light in (furnace test 0.148 -> 0.154 of 0.1875) without darkening open views much (0.7 dimmed the sunlit
// room's walls by 5-10%).
const float ConeOpacityExponent = 0.6;
// Radiance arriving from outside the scene (cones that escape): the skybox or the constant sky color.
#include "Engine/Sky.glsl"

float VoxelSize()
{
	return voxelGridSize / float(voxelResolution);
}

float ConeMaxDistance()
{
	return giConeMaxDistance > 0.0 ? giConeMaxDistance : voxelGridSize;
}

// Radiance of the surface at an SDF hit, read from the voxel volume just outside the wall.
vec4 SdfHitRadiance(vec3 hit, vec3 direction, float aperture, float hitT)
{
	float voxelSize = VoxelSize();

	// Offset along the SDF normal so the footprint sits on the lit surface voxels; keep it small,
	// coarse mips mix in too much empty space and amplify noise once un-premultiplied.
	vec3 hitNormal = SdfNormal(hit);
	vec3 samplePos = hit + hitNormal * voxelSize * 0.75;
	float diameter = max(voxelSize, 2.0 * aperture * hitT);
	float mip = clamp(log2(diameter / voxelSize), 0.0, 2.0);
	vec4 s = SampleVoxelRadiance((samplePos - voxelGridMin) / voxelGridSize, mip, direction);

	return vec4(s.rgb / max(s.a, 0.25), 1.0);
}

// aperture = tan(half angle). Marches from startDistance; returns premultiplied radiance in rgb and
// accumulated occlusion in a. stepJitter in [0, 1) moves every sample that fraction of a step further out (the
// steps grow with the distance, so one offset of the first step shifts them all alike).
vec4 TraceConeVoxel(vec3 origin, vec3 direction, float aperture, float startDistance, float maxDistance, float stepJitter)
{
	float voxelSize = VoxelSize();
	float maxMip = VoxelMaxMip();

	VoxelCone cone = VoxelConeSetup(direction);

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	float dist = max(startDistance, voxelSize);
	dist += stepJitter * max(voxelSize, 2.0 * aperture * dist);

	while (dist < maxDistance && alpha < 0.95)
	{
		float diameter = max(voxelSize, 2.0 * aperture * dist);
		float mip = min(log2(diameter / voxelSize), maxMip);

		vec3 uvw = (origin + direction * dist - voxelGridMin) / voxelGridSize;
		if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0)))) break;

		vec4 s = SampleVoxelCone(cone, uvw, mip);
		color += (1.0 - alpha) * s.rgb;
		alpha += (1.0 - alpha) * s.a;

		// Advance one cone diameter: each sample then covers a disjoint slab of the cone,
		// so premultiplied averages are not counted twice.
		dist += diameter;
	}

	// Whatever the cone did not hit sees the sky.
	color += (1.0 - alpha) * SkyRadiance(direction);
	return vec4(color, alpha);
}

// Near field through the distance field, far field through the voxel mips.
vec4 TraceConeSdfDetail(vec3 origin, vec3 direction, float aperture, float maxDistance, float stepJitter)
{
	float detail = min(giSdfDetailDistance, maxDistance);
	float hitT;
	if (TraceSdf(origin, direction, detail, hitT))
	{
		return SdfHitRadiance(origin + direction * hitT, direction, aperture, hitT);
	}

	return TraceConeVoxel(origin, direction, aperture, detail, maxDistance, stepJitter);
}

// Voxel cone with distance field opacity (giTraceMode 2). The samples sit where TraceConeVoxel puts them (fixed
// positions along the cone, every half diameter, footprint of the diameter), so the radiance from the mips stays
// smooth; sample positions that followed the distance (sphere tracing) drew rings and contours on the walls.
// The global distance field sets a floor under each sample's opacity, 1 - d / r (d = distance to the nearest
// surface, r = cone radius, as in distance field AO): a cone cannot pass a wall that the coarse mips averaged
// away, and near the origin, where the voxel occupancy is blocky (stepped corners), the field alone decides.
// rgb = radiance (premultiplied by the cone's coverage), a = occlusion.
vec4 TraceConeSdf(vec3 origin, vec3 direction, float aperture, float maxDistance, float stepJitter)
{
	float voxelSize = VoxelSize();
	float sdfVoxel = SdfVoxelSize();
	float maxMip = VoxelMaxMip();
	// A little narrower than the cone: a 60-degree cone tilted 60 degrees from a plane touches it along one edge,
	// and the sphere approximation of its cross-section would darken every flat surface.
	float occlusionAperture = aperture * 0.85;
	// The field stores distances only up to its band: the radius stays inside it, so a distance at the band edge
	// reads as free rather than as a surface there.
	float maxRadius = sdfMaxDistance * 0.9;
	VoxelCone cone = VoxelConeSetup(direction);

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	float dist = voxelSize + stepJitter * 0.5 * max(voxelSize, 2.0 * aperture * voxelSize);
	while (dist < maxDistance && alpha < 0.98)
	{
		vec3 p = origin + direction * dist;
		vec3 uvw = (p - voxelGridMin) / voxelGridSize;
		if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0)))) break;

		float diameter = max(voxelSize, 2.0 * aperture * dist);
		float mip = min(log2(diameter / voxelSize), maxMip);
		vec4 s = SampleVoxelCone(cone, uvw, mip);

		float fieldAlpha = 0.0;
		if (InsideSdfGrid(p))
		{
			// The field knows the surfaces within its band only: of a cone wider than that it vouches for the central
			// disc, (radius / coneRadius)^2 of the cross-section. Counting that disc as the whole cone drew the edge of
			// every object a far cone's axis passes within the band of as a sharp band on the walls.
			float coneRadius = max(occlusionAperture * dist, sdfVoxel * 0.5);
			float radius = min(coneRadius, maxRadius);
			float coverage = radius / coneRadius;
			fieldAlpha = coverage * coverage * (1.0 - clamp(SampleSdf(p) / radius, 0.0, 1.0));
		}

		// The field alone near the origin, the larger of both further out (the voxels also see what lies beyond the
		// field's band).
		float voxelWeight = smoothstep(voxelSize * 2.0, voxelSize * 6.0, dist);
		float a = mix(fieldAlpha, max(s.a, fieldAlpha), voxelWeight);
		// Half-diameter steps overlap: two samples make up one footprint's opacity.
		a = 1.0 - pow(1.0 - min(a, 0.999), ConeOpacityExponent);
		// The occupied voxels' average radiance in the footprint.
		vec3 radiance = s.rgb / max(s.a, 0.02);
		color += (1.0 - alpha) * a * radiance;
		alpha += (1.0 - alpha) * a;

		dist += diameter * 0.5;
	}

	// Whatever the cone did not hit sees the sky.
	color += (1.0 - alpha) * SkyRadiance(direction);
	return vec4(color, alpha);
}

vec4 TraceConeJittered(vec3 origin, vec3 direction, float aperture, float maxDistance, float stepJitter)
{
	if (giTraceMode == 2)
	{
		return TraceConeSdf(origin, direction, aperture, maxDistance, stepJitter);
	}

	return giTraceMode != 0
		? TraceConeSdfDetail(origin, direction, aperture, maxDistance, stepJitter)
		: TraceConeVoxel(origin, direction, aperture, 0.0, maxDistance, stepJitter);
}

vec4 TraceCone(vec3 origin, vec3 direction, float aperture, float maxDistance)
{
	return TraceConeJittered(origin, direction, aperture, maxDistance, 0.0);
}

// Start offset off the surface, so the cones' footprints do not take in the surface's own voxels.
float ConeStartOffset()
{
	return VoxelSize() * 1.5;
}

// ---- Cone jitter, anchored in the world ----
// Six fixed cones print a small bright emitter onto the surfaces around it as rings and blobs (each cone sees it
// over its own patch of surface, each sample distance draws an arc). Turning the cone set around the normal and
// moving the samples along the cones by a hash of the surface position spreads those prints into noise that a spatial
// filter takes out (GIResolvePass). The hash belongs to world cells (giConeJitterCell wide), so the pattern stays on
// the surfaces when the camera moves and never changes for a still scene: no history needed.
uniform int giConeJitter;          // 1: the screen-space resolve jitters its cones
uniform float giConeJitterCell;    // world size of the cells sharing one jitter

uvec3 ConeJitterHash(uvec3 v)
{
	v = v * 1664525u + 1013904223u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	v ^= v >> 16u;
	v.x += v.y * v.z;
	v.y += v.z * v.x;
	v.z += v.x * v.y;
	return v;
}

// 3D ordered dither: 4x4x4 cells get the 64 values (index + 0.5) / 64, spread so that every 2x2x2 block holds one of
// each eighth (the 2D Bayer construction with a third axis): the filter finds a balanced set of jitters in any
// neighbourhood, where white noise leaves clumps it averages away slowly (and that flicker as the camera moves).
float OrderedDither3D(uvec3 p)
{
	uint value = 0u;
	for (int level = 0; level < 2; level++)
	{
		uvec3 b = (p >> uint(level)) & 1u;
		// The finest bits decide the most significant part of the value.
		uint digit = ((b.x ^ b.z) << 2) | ((b.y ^ b.z) << 1) | b.z;
		value |= digit << uint(3 * (1 - level));
	}
	return (float(value) + 0.5) / 64.0;
}

// x: turn of the cone set (fraction of the side cones' 72-degree symmetry), y: step offset (fraction of a step), both
// ordered dithers of the cell (the step's with the axes permuted, so the two are not correlated), offset per 4^3
// block by a hash so that the tiling does not repeat. (Tilting the set as well dips the side cones, 60 degrees off the
// normal and 30 wide, below the surface: they read the back of the wall, 5% of the light lost in the furnace test.)
vec2 ConeJitterAt(ivec3 cell)
{
	uvec3 p = uvec3(cell);
	uvec3 h = ConeJitterHash(p >> 2u);
	vec2 offset = vec2(h.xy >> 8u) * (1.0 / 16777216.0);
	return fract(vec2(OrderedDither3D(p), OrderedDither3D(p.zxy + uvec3(1u, 2u, 3u))) + offset);
}

// One jitter per giConeJitterCell cell. Neighbouring pixels with different jitters read different texels, which is
// what the jitter costs (a finer step pattern alone cost as much as finer cells).
vec2 ConeJitterAtPosition(vec3 position)
{
	return ConeJitterAt(ivec3(floor(position / giConeJitterCell)));
}

// One jitter per cell of about wantedCell (at most giConeJitterCell, at least 1 cm), the cell sizes powers of two
// (in centimetres): a screen-space pass asks for cells a few pixels wide, so the filter always finds several of them
// near the camera as well as far away. Between two sizes each coarse cell picks one by its own hash, in proportion,
// so the change of size moves across the surfaces in cells, not as one sharp line, when the camera moves.
vec2 ConeJitterAtPosition(vec3 position, float wantedCell)
{
	float base = 0.01;
	float level = log2(clamp(wantedCell, base, giConeJitterCell) / base);
	float lower = floor(level);
	float coarse = base * exp2(lower + 1.0);
	uvec3 pick = ConeJitterHash(uvec3(ivec3(floor(position / coarse)) + 0x40000));
	float size = float(pick.z >> 8u) * (1.0 / 16777216.0) < level - lower ? coarse : coarse * 0.5;
	return ConeJitterAt(ivec3(floor(position / min(size, giConeJitterCell))));
}

// Six 60-degree cones over the hemisphere, starting <offset> off the surface along the normal, the set turned around
// the normal by jitter.x (of 72 degrees) and its samples moved out by jitter.y of a step.
// rgb = indirect irradiance (multiply by albedo), a = occlusion.
vec4 TraceDiffuseConesJittered(vec3 position, vec3 normal, float offset, vec2 jitter)
{
	const vec3 coneDirs[6] = vec3[6](
		vec3(0.0, 1.0, 0.0),
		vec3(0.0, 0.5, 0.866025),
		vec3(0.823639, 0.5, 0.267617),
		vec3(0.509037, 0.5, -0.700629),
		vec3(-0.509037, 0.5, -0.700629),
		vec3(-0.823639, 0.5, 0.267617));
	const float coneWeights[6] = float[6](0.25, 0.15, 0.15, 0.15, 0.15, 0.15);
	const float aperture = 0.57735; // tan(30 deg)

	// Continuous frame (Duff et al. 2017), then turned around the normal.
	float s = normal.z >= 0.0 ? 1.0 : -1.0;
	float a = -1.0 / (s + normal.z);
	float b = normal.x * normal.y * a;
	vec3 t0 = vec3(1.0 + s * normal.x * normal.x * a, s * b, -s * normal.x);
	vec3 b0 = vec3(b, s + normal.y * normal.y * a, -normal.y);
	float angle = jitter.x * 1.2566371; // 2 pi / 5
	float c = cos(angle);
	float sn = sin(angle);
	vec3 tangent = t0 * c + b0 * sn;
	vec3 bitangent = b0 * c - t0 * sn;

	vec3 origin = position + normal * offset;
	float maxDistance = ConeMaxDistance();

	vec4 result = vec4(0.0);
	// The bound depends on a uniform only so the compiler keeps the loop: unrolled, every cone would inline all
	// three trace modes (seconds of shader compilation on Intel).
	int cones = giTraceMode >= 0 ? 6 : 0;
	for (int i = 0; i < cones; i++)
	{
		vec3 d = coneDirs[i];
		vec3 direction = normalize(tangent * d.x + normal * d.y + bitangent * d.z);
		result += TraceConeJittered(origin, direction, aperture, maxDistance, jitter.y) * coneWeights[i];
	}

	return result;
}

// Six cones from a surface point, jittered when the pass asks for it (giConeJitter).
vec4 TraceDiffuseConesFrom(vec3 position, vec3 normal, float offset)
{
	vec2 jitter = giConeJitter != 0 ? ConeJitterAtPosition(position) : vec2(0.0);
	return TraceDiffuseConesJittered(position, normal, offset, jitter);
}

// Hemisphere cones from a surface point.
vec4 TraceDiffuseCones(vec3 position, vec3 normal)
{
	return TraceDiffuseConesFrom(position, normal, ConeStartOffset());
}

// Single wide cone along the normal: a crude but cheap stand-in for the hemisphere.
vec4 TraceWideConeFrom(vec3 position, vec3 normal, float offset)
{
	return TraceCone(position + normal * offset, normal, 1.0, ConeMaxDistance());
}

vec4 TraceWideCone(vec3 position, vec3 normal)
{
	return TraceWideConeFrom(position, normal, ConeStartOffset());
}

// Single cone along the mirror direction; roughness in [0,1] widens the cone.
vec3 TraceSpecularCone(vec3 position, vec3 normal, vec3 viewDir, float roughness)
{
	vec3 reflected = reflect(-viewDir, normal);
	float aperture = clamp(roughness, 0.03, 1.0);
	vec3 origin = position + normal * ConeStartOffset();
	return TraceCone(origin, reflected, aperture, ConeMaxDistance()).rgb;
}
