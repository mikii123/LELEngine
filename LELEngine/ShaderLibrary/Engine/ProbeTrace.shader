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

#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"
#include "Engine/ScreenProbes.glsl"

// Screen probe ray tracing (Lumen final gather, step 1): one probe per probeSpacing x probeSpacing pixels,
// anchored at a jittered pixel of its cell, 64 rays per probe, exactly one ray per fragment.
//
// With a valid importance selection (Engine/ProbeImportance.shader) the 64 rays are spent as 2x2 on the
// 16 most important octahedral directions; otherwise one ray per direction. ProbeCompose.shader turns
// the ray results back into the octahedral map.
//
// Each ray first marches the depth buffer (screen trace: pixel-exact near-field occlusion), then continues
// through the global distance field up to the near distance; hits read the surface cache, rays that get
// further read the world-space radiance cache, and without a cache they trace on to the sky.
out vec4 OutRay; // rgb radiance, a = hit distance (-1 sky / far field, -2 probe on sky / invalid)

uniform sampler2D FinalLighting;
uniform usampler2D ProbeSelection;
uniform vec3 giSkyRadiance;
uniform vec2 directionJitter;
uniform mat4 viewProjection;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform float screenTraceDistance;  // world units, 0 disables
uniform int screenTraceSteps;
uniform float screenTraceThickness; // world units
uniform int frameIndex;

// Marches the depth buffer along the ray. Returns true on a hit (position and normal from the G-buffer).
// travelled receives how far the march got without hitting, so the SDF trace can continue from there.
bool ScreenTrace(vec3 origin, vec3 direction, float jitter, out vec3 hitPos, out vec3 hitNormal, out float travelled)
{
	float stepSize = screenTraceDistance / float(screenTraceSteps);
	travelled = 0.0;

	for (int i = 0; i < screenTraceSteps; i++)
	{
		float t = (float(i) + jitter) * stepSize;
		vec3 p = origin + direction * t;

		vec4 clip = viewProjection * vec4(p, 1.0);
		if (clip.w <= 0.01) return false;
		vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) return false;

		ivec2 pixel = ivec2(uv * vec2(screenSize));
		float sceneDepth = texelFetch(SceneDepth, pixel, 0).r;
		if (sceneDepth >= 1.0)
		{
			travelled = t;
			continue;
		}

		vec3 scenePos = ReconstructPosition(pixel, sceneDepth);
		float sceneViewDepth = dot(scenePos - cameraPosition, cameraForward);
		float rayViewDepth = dot(p - cameraPosition, cameraForward);

		if (rayViewDepth > sceneViewDepth + 0.02)
		{
			if (rayViewDepth < sceneViewDepth + screenTraceThickness)
			{
				hitPos = scenePos;
				hitNormal = normalize(texelFetch(NormalRoughness, pixel, 0).xyz);
				return true;
			}
			// Behind something thick: the depth buffer cannot tell what is there, hand over to the SDF.
			return false;
		}

		travelled = t;
	}

	return false;
}

// Full trace of one ray: screen space first, then the distance field. Returns radiance and hit distance.
vec4 TraceRay(vec3 position, vec3 normal, vec3 direction, float jitter)
{
	float voxel = SdfVoxelSize();
	vec3 origin = position + normal * voxel * 0.5;

	if (screenTraceDistance > 0.0)
	{
		vec3 hitPos, hitNormal;
		float travelled;
		if (ScreenTrace(position + normal * 0.02, direction, jitter, hitPos, hitNormal, travelled))
		{
			vec3 radiance;
			if (!SampleSurfaceCacheAtHit(hitPos, hitNormal, voxel * 2.0, FinalLighting, radiance))
			{
				radiance = vec3(0.0);
			}
			return vec4(radiance, length(hitPos - position));
		}
		origin += direction * max(travelled - voxel, 0.0);
	}

	float maxDistance = rcNearDistance > 0.0 ? rcNearDistance : sdfGridSize;
	float hitT;
	bool hit = TraceSdf(origin, direction, maxDistance, hitT);

	if (!hit && rcNearDistance > 0.0)
	{
		// Far field: the radiance cache interpolated at the end of the near trace, in the ray direction.
		// Only with clearance around that point: right next to a wall the probes just behind it would pass
		// the occlusion test and leak the lighting of the other side, so such rays trace on instead.
		vec3 far = origin + direction * maxDistance;
		vec3 radiance;
		if (SampleSdf(far) >= rcProbeSpacing * 0.25 && SampleRadianceCache(far, direction, radiance))
		{
			return vec4(radiance, -1.0);
		}

		hit = TraceSdf(far, direction, sdfGridSize, hitT);
		hitT += maxDistance;
	}

	if (hit)
	{
		vec3 hitPos = origin + direction * hitT;
		vec3 radiance;
		if (!SampleSurfaceCacheAtHit(hitPos, SdfNormal(hitPos), voxel * 2.0, FinalLighting, radiance))
		{
			radiance = vec3(0.0);
		}
		return vec4(radiance, length(hitPos - position));
	}

	return vec4(giSkyRadiance, -1.0);
}

void main()
{
	ivec2 texel = ivec2(gl_FragCoord.xy);
	ivec2 probe = texel / PROBE_RESOLUTION;
	ivec2 local = texel - probe * PROBE_RESOLUTION;
	int ray = local.y * PROBE_RESOLUTION + local.x;

	vec3 position, normal;
	if (!ProbeAnchor(probe, position, normal))
	{
		OutRay = vec4(0.0, 0.0, 0.0, -2.0);
		return;
	}

	// Which octahedral texel (and which part of it) does this ray sample?
	uvec4 selection = texelFetch(ProbeSelection, probe, 0);
	vec2 octUV;
	if (SelectionValid(selection))
	{
		int texelIndex = SelectedTexelIndex(selection, ray / 4);
		int sub = ray % 4;
		vec2 oct = vec2(texelIndex % PROBE_RESOLUTION, texelIndex / PROBE_RESOLUTION);
		vec2 subOffset = (vec2(sub & 1, sub >> 1) + fract(directionJitter + vec2(0.37, 0.61) * float(sub))) * 0.5;
		octUV = (oct + subOffset) / float(PROBE_RESOLUTION);
	}
	else
	{
		octUV = (vec2(local) + directionJitter) / float(PROBE_RESOLUTION);
	}

	vec3 tangent, bitangent;
	TangentFrame(normal, tangent, bitangent);
	vec3 h = HemiOctahedralToDirection(octUV);
	vec3 direction = normalize(tangent * h.x + bitangent * h.y + normal * h.z);

	float jitter = HashToFloat(HashCoord(texel, uint(frameIndex)));
	OutRay = TraceRay(position, normal, direction, jitter);
}

/////Fragment
