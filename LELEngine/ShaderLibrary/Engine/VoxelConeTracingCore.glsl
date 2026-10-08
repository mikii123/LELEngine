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

uniform sampler3D VoxelRadiance;
uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;
uniform float giDiffuseStrength;
uniform float giSpecularStrength;
uniform float giOcclusionStrength;
uniform float giConeMaxDistance;
uniform int giTraceMode;
uniform float giSdfDetailDistance;
uniform vec3 giSkyRadiance; // radiance arriving from outside the scene (cones that escape)

float VoxelSize()
{
	return voxelGridSize / float(voxelResolution);
}

float ConeMaxDistance()
{
	return giConeMaxDistance > 0.0 ? giConeMaxDistance : voxelGridSize;
}

// Radiance of the surface at an SDF hit, read from the voxel volume just outside the wall.
vec4 SdfHitRadiance(vec3 hit, float aperture, float hitT)
{
	float voxelSize = VoxelSize();

	// Offset along the SDF normal so the footprint sits on the lit surface voxels; keep it small,
	// coarse mips mix in too much empty space and amplify noise once un-premultiplied.
	vec3 hitNormal = SdfNormal(hit);
	vec3 samplePos = hit + hitNormal * voxelSize * 0.75;
	float diameter = max(voxelSize, 2.0 * aperture * hitT);
	float mip = clamp(log2(diameter / voxelSize), 0.0, 2.0);
	vec4 s = textureLod(VoxelRadiance, (samplePos - voxelGridMin) / voxelGridSize, mip);

	return vec4(s.rgb / max(s.a, 0.25), 1.0);
}

// aperture = tan(half angle). Marches from startDistance; returns premultiplied radiance in rgb and
// accumulated occlusion in a.
vec4 TraceConeVoxel(vec3 origin, vec3 direction, float aperture, float startDistance, float maxDistance)
{
	float voxelSize = VoxelSize();
	float maxMip = log2(float(voxelResolution));

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	float dist = max(startDistance, voxelSize);

	while (dist < maxDistance && alpha < 0.95)
	{
		float diameter = max(voxelSize, 2.0 * aperture * dist);
		float mip = min(log2(diameter / voxelSize), maxMip);

		vec3 uvw = (origin + direction * dist - voxelGridMin) / voxelGridSize;
		if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0)))) break;

		vec4 s = textureLod(VoxelRadiance, uvw, mip);
		color += (1.0 - alpha) * s.rgb;
		alpha += (1.0 - alpha) * s.a;

		// Advance one cone diameter: each sample then covers a disjoint slab of the cone,
		// so premultiplied averages are not counted twice.
		dist += diameter;
	}

	// Whatever the cone did not hit sees the sky.
	color += (1.0 - alpha) * giSkyRadiance;
	return vec4(color, alpha);
}

// Near field through the distance field, far field through the voxel mips.
vec4 TraceConeSdfDetail(vec3 origin, vec3 direction, float aperture, float maxDistance)
{
	float detail = min(giSdfDetailDistance, maxDistance);
	float hitT;
	if (TraceSdf(origin, direction, detail, hitT))
	{
		return SdfHitRadiance(origin + direction * hitT, aperture, hitT);
	}

	return TraceConeVoxel(origin, direction, aperture, detail, maxDistance);
}

// Voxel cone with distance field opacity (giTraceMode 2). The samples sit where TraceConeVoxel puts them (fixed
// positions along the cone, every half diameter, footprint of the diameter), so the radiance from the mips stays
// smooth; sample positions that followed the distance (sphere tracing) drew rings and contours on the walls.
// The global distance field sets a floor under each sample's opacity, 1 - d / r (d = distance to the nearest
// surface, r = cone radius, as in distance field AO): a cone cannot pass a wall that the coarse mips averaged
// away, and near the origin, where the voxel occupancy is blocky (stepped corners), the field alone decides.
// rgb = radiance (premultiplied by the cone's coverage), a = occlusion.
vec4 TraceConeSdf(vec3 origin, vec3 direction, float aperture, float maxDistance)
{
	float voxelSize = VoxelSize();
	float sdfVoxel = SdfVoxelSize();
	float maxMip = log2(float(voxelResolution));
	// A little narrower than the cone: a 60-degree cone tilted 60 degrees from a plane touches it along one edge,
	// and the sphere approximation of its cross-section would darken every flat surface.
	float occlusionAperture = aperture * 0.85;
	// The field stores distances only up to its band: the radius stays inside it, so a distance at the band edge
	// reads as free rather than as a surface there.
	float maxRadius = sdfMaxDistance * 0.9;

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	float dist = voxelSize;
	while (dist < maxDistance && alpha < 0.98)
	{
		vec3 p = origin + direction * dist;
		vec3 uvw = (p - voxelGridMin) / voxelGridSize;
		if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0)))) break;

		float diameter = max(voxelSize, 2.0 * aperture * dist);
		float mip = min(log2(diameter / voxelSize), maxMip);
		vec4 s = textureLod(VoxelRadiance, uvw, mip);

		float fieldAlpha = 0.0;
		if (InsideSdfGrid(p))
		{
			float radius = clamp(occlusionAperture * dist, sdfVoxel * 0.5, maxRadius);
			fieldAlpha = 1.0 - clamp(SampleSdf(p) / radius, 0.0, 1.0);
		}

		// The field alone near the origin, the larger of both further out (the voxels also see what lies beyond the
		// field's band).
		float voxelWeight = smoothstep(voxelSize * 2.0, voxelSize * 6.0, dist);
		float a = mix(fieldAlpha, max(s.a, fieldAlpha), voxelWeight);
		// Half-diameter steps overlap: two samples make up one footprint's opacity.
		a = 1.0 - sqrt(1.0 - min(a, 0.999));
		// The occupied voxels' average radiance in the footprint.
		vec3 radiance = s.rgb / max(s.a, 0.02);
		color += (1.0 - alpha) * a * radiance;
		alpha += (1.0 - alpha) * a;

		dist += diameter * 0.5;
	}

	// Whatever the cone did not hit sees the sky.
	color += (1.0 - alpha) * giSkyRadiance;
	return vec4(color, alpha);
}

vec4 TraceCone(vec3 origin, vec3 direction, float aperture, float maxDistance)
{
	if (giTraceMode == 2)
	{
		return TraceConeSdf(origin, direction, aperture, maxDistance);
	}

	return giTraceMode != 0
		? TraceConeSdfDetail(origin, direction, aperture, maxDistance)
		: TraceConeVoxel(origin, direction, aperture, 0.0, maxDistance);
}

// Start offset off the surface, so the cones' footprints do not take in the surface's own voxels.
float ConeStartOffset()
{
	return VoxelSize() * 1.5;
}

// Six 60-degree cones over the hemisphere, starting <offset> off the surface along the normal.
// rgb = indirect irradiance (multiply by albedo), a = occlusion.
vec4 TraceDiffuseConesFrom(vec3 position, vec3 normal, float offset)
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

	vec3 helper = abs(normal.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
	vec3 tangent = normalize(cross(helper, normal));
	vec3 bitangent = cross(normal, tangent);

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
		result += TraceCone(origin, direction, aperture, maxDistance) * coneWeights[i];
	}

	return result;
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
