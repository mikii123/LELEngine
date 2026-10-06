//Compute

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/RadianceCache.glsl"

// Surface cache lighting, Lumen style, one thread per atlas texel:
//   direct   = sun with shadows at the captured surface point (recomputed every frame)
//   indirect = radiosity: a few cosine-distributed rays per texel per frame, traced through the global
//              distance field, reading the previous frame's final lighting at the hits (the world-space
//              radiance cache beyond the near distance); accumulated exponentially so multi-bounce
//              lighting converges over frames
//   final    = albedo * (direct + indirect) + emission
layout(local_size_x = 8, local_size_y = 8) in;

uniform usampler2D CardIndex;
uniform sampler2D CardAlbedo;
uniform sampler2D CardNormal;
uniform sampler2D CardEmissive;
uniform sampler2D FinalLightingPrev;

layout(binding = 0, rgba16f) uniform image2D IndirectLighting; // rgb irradiance / pi, a = accumulated sample weight
layout(binding = 1, rgba16f) uniform writeonly image2D FinalLighting;

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};
uniform Directional LDirectional;

uniform vec3 giSkyRadiance;
uniform int radiosityRays;        // rays per texel this frame
uniform float radiosityBlend;     // history weight, 0 = restart every frame
uniform int frameIndex;
uniform ivec2 atlasRegion;        // texels to process (x = width, y = used rows)

void main()
{
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(texel, atlasRegion))) return;

	uint cardId = texelFetch(CardIndex, texel, 0).r;
	if (cardId == 0xFFFFu) return;

	vec4 localPos = texelFetch(CardLocalPosition, texel, 0);
	if (localPos.w < 0.5) return;

	Card card = cards[int(cardId)];
	SceneObject o = sceneObjects[int(card.origin.w)];

	vec3 position = (o.localToWorld * vec4(localPos.xyz, 1.0)).xyz;
	vec3 localNormal = normalize(texelFetch(CardNormal, texel, 0).xyz * 2.0 - 1.0);
	vec3 N = SceneObjectLocalToWorldDir(o, localNormal);
	vec3 albedo = texelFetch(CardAlbedo, texel, 0).rgb;
	vec3 emissive = texelFetch(CardEmissive, texel, 0).rgb;

	// ---- direct sun light (engine convention: lightColor = irradiance / pi)
	vec3 L = normalize(-LDirectional.dirDirection);
	float ndl = max(dot(N, L), 0.0);
	float shadow = ndl > 0.0 ? SampleShadow(position, N, L) : 1.0;
	vec3 direct = LDirectional.dirColor.rgb * LDirectional.dirStrength * ndl * shadow;

	// ---- indirect: cosine-weighted hemisphere rays, estimate of irradiance / pi is the mean radiance
	vec3 tangent, bitangent;
	TangentFrame(N, tangent, bitangent);
	float sdfVoxel = SdfVoxelSize();
	vec3 origin = position + N * sdfVoxel;
	uint seed = HashCoord(texel, uint(frameIndex));
	vec2 rotation = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));

	float maxDistance = rcNearDistance > 0.0 ? rcNearDistance : sdfGridSize;
	vec3 estimate = vec3(0.0);
	for (int r = 0; r < radiosityRays; r++)
	{
		vec2 u = fract(Hammersley(uint(frameIndex * radiosityRays + r), 1024u) + rotation);
		vec3 h = CosineSampleHemisphere(u);
		vec3 direction = normalize(tangent * h.x + bitangent * h.y + N * h.z);

		float hitT;
		vec3 radiance;
		bool hit = TraceSdf(origin, direction, maxDistance, hitT);
		if (!hit && rcNearDistance > 0.0)
		{
			// Far field from the radiance cache when the lookup point has clearance (see ProbeTrace.shader),
			// otherwise finish the trace.
			vec3 far = origin + direction * maxDistance;
			if (SampleSdf(far) >= rcProbeSpacing * 0.25 && SampleRadianceCache(far, direction, radiance))
			{
				estimate += radiance;
				continue;
			}
			hit = TraceSdf(far, direction, sdfGridSize, hitT);
			hitT += maxDistance;
		}

		if (hit)
		{
			vec3 hitPos = origin + direction * hitT;
			if (SampleSurfaceCacheAtHit(hitPos, SdfNormal(hitPos), sdfVoxel * 2.0, FinalLightingPrev, radiance))
			{
				estimate += radiance;
			}
		}
		else
		{
			estimate += giSkyRadiance;
		}
	}
	estimate /= float(max(radiosityRays, 1));

	// Exponential accumulation with a constant history weight (four rays per frame are noisy on their own;
	// a difference-driven weight would let that noise through as flicker).
	vec4 history = imageLoad(IndirectLighting, texel);
	float weight = history.a > 0.5 ? radiosityBlend : 0.0;
	vec3 indirect = mix(estimate, history.rgb, weight);
	imageStore(IndirectLighting, texel, vec4(indirect, 1.0));

	vec3 final = albedo * (direct + indirect) + emissive;
	imageStore(FinalLighting, texel, vec4(final, 1.0));
}

/////Compute
