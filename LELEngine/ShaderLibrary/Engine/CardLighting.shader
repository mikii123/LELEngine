//Compute

#version 430

// One thread per atlas texel (see below). Declared before the includes: Engine/Lights.glsl's work group culling reads
// gl_WorkGroupSize, which GLSL allows only after this declaration (NVIDIA's compiler holds to it).
layout(local_size_x = 8, local_size_y = 8) in;

// The local lights a work group's texels may get, found by the group together (Engine/Lights.glsl).
#define LEL_LIGHT_GROUPS
#include "Engine/Shadows.glsl"
#include "Engine/DistanceField.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"

// Surface cache lighting, Lumen style, one thread per atlas texel:
//   direct   = the directional lights that go into the GI, each with its shadows at the captured surface point, and
//              the local lights that go into the GI, blocked by the global distance field towards them when they ask
//              (recomputed every frame)
//   indirect = radiosity interpolated from the card's probes (CardRadiosityProbes.shader: one probe per 4x4
//              texels, 16 rays per frame, accumulated over frames so multi-bounce lighting converges)
//   final    = albedo * (direct + indirect) + emission

uniform usampler2D CardIndex;
uniform sampler2D CardAlbedo;
uniform sampler2D CardNormal;
uniform sampler2D CardEmissive;
uniform sampler2D RadiosityProbes;

layout(binding = 0, rgba16f) uniform writeonly image2D FinalLighting;

uniform ivec2 atlasRegion;        // texels to process (x = width, y = used rows)

// Bilinear interpolation of the radiosity probes around a texel, restricted to the probes of its own card
// (cards are aligned to the 4x4 probe blocks, so no probe mixes two cards). Edge texels extrapolate from
// the probes they do have.
vec3 InterpolateRadiosity(ivec2 texel, Card card)
{
	vec2 probeCoord = (vec2(texel) + 0.5) * 0.25 - 0.5;
	ivec2 base = ivec2(floor(probeCoord));
	vec2 f = probeCoord - vec2(base);
	ivec2 probeMin = card.rect.xy / 4;
	ivec2 probeMax = (card.rect.xy + card.rect.zw - 1) / 4;

	vec3 sum = vec3(0.0);
	float weightSum = 0.0;
	for (int i = 0; i < 4; i++)
	{
		ivec2 offset = ivec2(i & 1, i >> 1);
		ivec2 probe = base + offset;
		if (any(lessThan(probe, probeMin)) || any(greaterThan(probe, probeMax))) continue;

		vec4 s = texelFetch(RadiosityProbes, probe, 0);
		if (s.a < 0.5) continue;

		float w = (offset.x == 1 ? f.x : 1.0 - f.x) * (offset.y == 1 ? f.y : 1.0 - f.y);
		sum += s.rgb * w;
		weightSum += w;
	}

	if (weightSum > 1e-4) return sum / weightSum;

	vec4 own = texelFetch(RadiosityProbes, texel / 4, 0);
	return own.a > 0.5 ? own.rgb : vec3(0.0);
}

// How much of a local light reaches a texel: a sphere trace through the global distance field towards it (a surface
// before the light blocks it; a ray leaving the field gets through).
float CardLightVisibility(vec3 position, vec3 N, vec3 lightPosition)
{
	float voxel = SdfVoxelSize();
	vec3 origin = position + N * voxel;
	vec3 toLight = lightPosition - origin;
	float distanceToLight = length(toLight);
	if (distanceToLight <= voxel * 2.0)
	{
		return 1.0;
	}

	float hitT;
	return TraceSdf(origin, toLight / distanceToLight, distanceToLight - voxel, hitT) ? 0.0 : 1.0;
}

void main()
{
	// Whether the texel holds a point of a card, worked out without returning: the work group finds the local lights
	// reaching its points together (every thread takes part), and only then do the empty texels stop.
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	bool valid = all(lessThan(texel, atlasRegion));
	uint cardId = valid ? texelFetch(CardIndex, texel, 0).r : 0xFFFFu;
	valid = valid && cardId != 0xFFFFu;
	vec4 localPos = valid ? texelFetch(CardLocalPosition, texel, 0) : vec4(0.0);
	valid = valid && localPos.w >= 0.5;
	vec3 position = vec3(0.0);
	if (valid)
	{
		position = (sceneObjects[int(cards[int(cardId)].origin.w)].localToWorld * vec4(localPos.xyz, 1.0)).xyz;
	}

	LelLightGroupCullPoints(valid, position, true);
	if (!valid) return;

	Card card = cards[int(cardId)];
	SceneObject o = sceneObjects[int(card.origin.w)];

	vec3 localNormal = normalize(texelFetch(CardNormal, texel, 0).xyz * 2.0 - 1.0);
	vec3 N = SceneObjectLocalToWorldDir(o, localNormal);
	vec3 albedo = texelFetch(CardAlbedo, texel, 0).rgb;
	// Cards store the emission colour; the (possibly animated) intensity comes from the object table.
	vec3 emissive = texelFetch(CardEmissive, texel, 0).rgb * o.padding.w;

	// ---- the directional lights that go into the GI (Engine/DirectionalLights.glsl), times their indirect strength
	// (engine convention: lightColor = irradiance / pi)
	vec3 direct = vec3(0.0);
	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		float giStrength = LelDirectionalGIStrength(i);
		vec3 L = LelDirectionalTowards(i);
		float ndl = dot(N, L);
		if (giStrength > 0.0 && ndl > 0.0)
		{
			// Per channel: light through tinted transparent casters keeps their colour in the bounce.
			direct += LelDirectionalRadiance(i) * (giStrength * ndl) * SampleLightShadowRGB(i, position, N, L);
		}
	}

	// ---- the local lights that go into the GI (Engine/Lights.glsl: those the work group found), times their indirect strength
	for (LelLightIterator lights = LelGroupLights(); LelNextLight(lights);)
	{
		int i = lights.light;
		float giStrength = lelLights[i].radiance.w;
		if (giStrength <= 0.0)
		{
			continue;
		}

		// Apart from the test above: after "a || !LelLightAt(...)" NVIDIA's compiler warned that Li and radiance might be
		// used unset (C7050).
		vec3 Li;
		vec3 radiance;
		if (!LelLightAt(i, position, Li, radiance))
		{
			continue;
		}

		float nli = max(dot(N, Li), 0.0);
		if (nli > 0.0)
		{
			float visibility = LelLightHas(i, LEL_LIGHT_GI_SHADOWS) ? CardLightVisibility(position, N, lelLights[i].positionRange.xyz) : 1.0;
			direct += radiance * (giStrength * nli * visibility);
		}
	}

	// ---- indirect: the card's radiosity probes
	vec3 indirect = InterpolateRadiosity(texel, card);

	vec3 final = albedo * (direct + indirect) + emissive;
	imageStore(FinalLighting, texel, vec4(final, 1.0));
}

/////Compute
