//Compute

#version 430

#include "Engine/Shadows.glsl"
#include "Engine/SceneObjects.glsl"
#include "Engine/SurfaceCache.glsl"

// Surface cache lighting, Lumen style, one thread per atlas texel:
//   direct   = sun with shadows at the captured surface point (recomputed every frame)
//   indirect = radiosity interpolated from the card's probes (CardRadiosityProbes.shader: one probe per 4x4
//              texels, 16 rays per frame, accumulated over frames so multi-bounce lighting converges)
//   final    = albedo * (direct + indirect) + emission
layout(local_size_x = 8, local_size_y = 8) in;

uniform usampler2D CardIndex;
uniform sampler2D CardAlbedo;
uniform sampler2D CardNormal;
uniform sampler2D CardEmissive;
uniform sampler2D RadiosityProbes;

layout(binding = 0, rgba16f) uniform writeonly image2D FinalLighting;

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};
uniform Directional LDirectional;

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
	// Cards store the emission colour; the (possibly animated) intensity comes from the object table.
	vec3 emissive = texelFetch(CardEmissive, texel, 0).rgb * o.padding.w;

	// ---- direct sun light (engine convention: lightColor = irradiance / pi)
	vec3 L = normalize(-LDirectional.dirDirection);
	float ndl = max(dot(N, L), 0.0);
	float shadow = ndl > 0.0 ? SampleShadow(position, N, L) : 1.0;
	vec3 direct = LDirectional.dirColor.rgb * LDirectional.dirStrength * ndl * shadow;

	// ---- indirect: the card's radiosity probes
	vec3 indirect = InterpolateRadiosity(texel, card);

	vec3 final = albedo * (direct + indirect) + emissive;
	imageStore(FinalLighting, texel, vec4(final, 1.0));
}

/////Compute
