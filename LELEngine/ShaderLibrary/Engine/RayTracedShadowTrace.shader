//Compute

#version 430

// One thread per pixel. Declared before the includes (Engine/Lights.glsl).
layout(local_size_x = 8, local_size_y = 8) in;

// Only the LEL_RAY_QUERY variant traces (on a device with ray queries); the other is an empty stand-in, never dispatched.
#ifdef LEL_RAY_QUERY
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"
#include "Engine/Sampling.glsl"
#include "Engine/HistoryMeta.glsl"
#include "Engine/RayOrigin.glsl"

// Traced shadows of the lighting camera's view (Rendering.Passes.RayTracedShadowPass), step 1: for every pixel of the
// view's depth (the geometry prepass), a ray towards each traced light that reaches the surface there (the local lights
// with a slot: params.w of their list entry; the directional lights of the mask), through the scene's shadow casters as
// a top level acceleration structure (with ray queries: only on devices that have them). Soft shadows: the ray goes to a
// point of the light (a local light's disc of its source radius facing the surface, a directional light's cone of its
// angular radius), another each frame and pixel, and the result is accumulated over frames with the history of the
// same surface point (reprojected with the prepass's motion: depth and normal must match, as Engine/ProbeIntegrate.shader
// does), each channel of a slot whose light changed starting again. Engine/RayTracedShadowFilter.shader then smooths the
// accumulation over neighbouring pixels. A pixel no traced light reaches (the sky, a surface out of every slot's range
// without a traced directional light) writes only its meta (no history): the lit materials never read the mask there.
//
// Mask layout (Lighting.RayShadowBinding): layers 0 .. localLayers - 1 the local lights' slots (slot s: layer s / 4,
// channel s % 4), then one layer of the directional lights (light i: channel i) when any is traced.
// The accumulation is RGBA8, rounded up or down at random in proportion (unbiased: a history converging by a sixteenth a
// frame never sticks short of its value, and a channel that is exactly 0 or 1 stays so); the meta one 32-bit word
// (Engine/HistoryMeta.glsl: the view depth, the normal in 6 bits an axis, the frames accumulated - 1 in 4 bits).

const uint MetaNormalBits = 6u;

layout(binding = 1) uniform accelerationStructureEXT ShadowScene;

uniform sampler2D SceneDepth;
uniform sampler2D NormalRoughness;
uniform sampler2D Motion;            // world-space motion since the previous frame (xyz), w = 1 where the surface moved
uniform sampler2DArray HistoryMask;  // the previous frame's accumulation
uniform usampler2D HistoryMeta;      // the previous frame's meta (0: no history)

uniform mat4 invViewProjection;
uniform mat4 previousViewProjection;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform vec3 previousCameraPosition;
uniform vec3 previousCameraForward;
uniform ivec2 screenSize;
uniform int localLayers;             // layers of local slots (0..2)
uniform int directionalLayer;        // the directional lights' layer (-1: none traced)
uniform int directionalLights;       // bit i: directional light i is traced
uniform vec4 directionalTanRadius;   // per directional light: tan of its angular radius (0: a point)
uniform int softShadows;             // 0: rays to the lights' centres
uniform int frameIndex;
uniform int historyValid;
uniform float maxFrames;             // frames a history accumulates at most (1: none)
uniform float movingFrames;          // the cap on moving surfaces
uniform int slotsChanged;            // bit s: slot s starts again (another light, or it moved)
uniform int directionalChanged;      // bit i: directional light i starts again (it turned, or its size changed)
uniform float rayLength;             // how far a directional light's ray looks for casters

layout(binding = 0, rgba8) uniform writeonly image2DArray OutMask;
layout(binding = 1, r32ui) uniform writeonly uimage2D OutMeta;

const int MaxLayers = 3;

// A value as RGBA8 holds it, rounded to one of its two neighbouring steps at random in proportion to how near it is.
vec4 StochasticRound8(vec4 value, ivec2 pixel, int layer)
{
	uint seed = HashCoord(pixel, uint(frameIndex) * 3u + uint(layer) + 1u);
	vec4 u = vec4(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u), HashToFloat(seed * 277803737u + 1442695041u), HashToFloat(seed * 2654435761u + 40503u));
	return floor(clamp(value, 0.0, 1.0) * 255.0 + u) / 255.0;
}

// Whether nothing of the scene lies along a ray (first hit ends it).
bool ShadowRayClear(vec3 origin, vec3 direction, float tMax)
{
	rayQueryEXT query;
	rayQueryInitializeEXT(query, ShadowScene, gl_RayFlagsOpaqueEXT | gl_RayFlagsTerminateOnFirstHitEXT, 0xFFu, origin, 0.0, direction, tMax);
	while (rayQueryProceedEXT(query))
	{
	}

	return rayQueryGetIntersectionTypeEXT(query, true) == gl_RayQueryCommittedIntersectionNoneEXT;
}

// A point of the unit disc, uniform over its area (concentric mapping), from a 2D sample.
vec2 ConcentricDisc(vec2 u)
{
	vec2 o = u * 2.0 - 1.0;
	if (o.x == 0.0 && o.y == 0.0)
	{
		return vec2(0.0);
	}

	float r;
	float theta;
	if (abs(o.x) > abs(o.y))
	{
		r = o.x;
		theta = 0.78539816 * (o.y / o.x);
	}
	else
	{
		r = o.y;
		theta = 1.57079633 - 0.78539816 * (o.x / o.y);
	}

	return r * vec2(cos(theta), sin(theta));
}

// This frame's sample for a pixel and a light: a hash of the pixel moved along an R2 sequence over the frames, so every
// pixel takes another point each frame and neighbours differ.
vec2 LightSample(ivec2 pixel, int light)
{
	uint seed = HashCoord(pixel, uint(light) * 7919u + 17u);
	vec2 base = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));
	const float g = 1.32471795724474602596;
	vec2 step = vec2(1.0 / g, 1.0 / (g * g)) * float(frameIndex & 1023);
	return fract(base + step);
}

void main()
{
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(pixel, screenSize)))
	{
		return;
	}

	int layers = localLayers + (directionalLayer >= 0 ? 1 : 0);
	float depth = texelFetch(SceneDepth, pixel, 0).r;
	if (depth >= 1.0)
	{
		// The sky: no surface reads the mask here (its meta says so, the filter leaves it).
		imageStore(OutMeta, pixel, uvec4(0u));
		return;
	}

	vec2 uv = (vec2(pixel) + 0.5) / vec2(screenSize);
	vec4 world = invViewProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec3 position = world.xyz / world.w;
	vec3 N = normalize(texelFetch(NormalRoughness, pixel, 0).xyz);
	float viewDepth = dot(position - cameraPosition, cameraForward);
	vec3 origin = SceneRayOrigin(position, N, DepthRayBias(viewDepth));

	vec4 fresh[MaxLayers];
	for (int layer = 0; layer < MaxLayers; layer++)
	{
		fresh[layer] = vec4(1.0);
	}

	// ---- the local lights with a slot that reach this point (its cluster's: Engine/Lights.glsl)
	bool reached = directionalLayer >= 0;
	if (localLayers > 0)
	{
		for (LelLightIterator lights = LelLightsAt(position); LelNextLight(lights);)
		{
			int i = lights.light;
			int slot = int(lelLights[i].params.w);
			vec3 L;
			float attenuation;
			// No slot, or out of its reach: the surface never reads its channel here.
			if (slot < 0 || !LelLightShape(i, position, L, attenuation))
			{
				continue;
			}

			reached = true;
			// Behind the surface: the light gives nothing there anyway.
			if (dot(N, L) <= 0.0)
			{
				continue;
			}

			vec3 lightPosition = lelLights[i].positionRange.xyz;
			float radius = softShadows != 0 ? lelLights[i].params.z : 0.0;
			vec3 target = lightPosition;
			if (radius > 0.0)
			{
				// A point of the light's disc facing the surface: the shadow as soft as the source is large.
				vec3 tangent, bitangent;
				TangentFrame(L, tangent, bitangent);
				vec2 disc = ConcentricDisc(LightSample(pixel, slot)) * radius;
				target += tangent * disc.x + bitangent * disc.y;
			}

			vec3 toTarget = target - origin;
			float distance = length(toTarget);
			// Up to the source's disc (a fixture smaller than the source does not hide it).
			float tMax = distance - min(radius, 0.5 * distance);
			fresh[slot >> 2][slot & 3] = tMax <= 0.0 || ShadowRayClear(origin, toTarget / distance, tMax) ? 1.0 : 0.0;
		}
	}

	// ---- the traced directional lights (Engine/DirectionalLights.glsl)
	if (directionalLayer >= 0)
	{
		for (int i = 0; i < LelDirectionalCount(); i++)
		{
			vec3 L = LelDirectionalTowards(i);
			if ((directionalLights & (1 << i)) == 0 || dot(N, L) <= 0.0)
			{
				continue;
			}

			vec3 direction = L;
			float tanRadius = softShadows != 0 ? directionalTanRadius[i] : 0.0;
			if (tanRadius > 0.0)
			{
				// A direction of the light's cone (a disc of its angular radius one unit away).
				vec3 tangent, bitangent;
				TangentFrame(L, tangent, bitangent);
				vec2 disc = ConcentricDisc(LightSample(pixel, 8 + i)) * tanRadius;
				direction = normalize(L + tangent * disc.x + bitangent * disc.y);
			}

			fresh[directionalLayer][i] = ShadowRayClear(origin, direction, rayLength) ? 1.0 : 0.0;
		}
	}

	if (!reached)
	{
		// No traced light reaches the surface here: nothing of the mask is read for it (nor kept as history).
		imageStore(OutMeta, pixel, uvec4(0u));
		return;
	}

	// ---- the history of this surface point: the texel nearest to its previous place, when its depth and normal match
	// (one texel, not four: the shadows are soft where they accumulate, and the filter smooths them after)
	vec4 motion = texelFetch(Motion, pixel, 0);
	vec4 history[MaxLayers];
	for (int layer = 0; layer < MaxLayers; layer++)
	{
		history[layer] = vec4(1.0);
	}

	float previousFrames = 0.0;
	if (historyValid != 0 && maxFrames > 1.0)
	{
		vec3 previousPosition = position - motion.xyz;
		vec4 previousClip = previousViewProjection * vec4(previousPosition, 1.0);
		if (previousClip.w > 0.0)
		{
			ivec2 tap = ivec2(floor((previousClip.xy / previousClip.w * 0.5 + 0.5) * vec2(screenSize)));
			if (all(greaterThanEqual(tap, ivec2(0))) && all(lessThan(tap, screenSize)))
			{
				uint meta = texelFetch(HistoryMeta, tap, 0).r;
				float metaDepth = HistoryMetaDepth(meta);
				float expectedDepth = dot(previousPosition - previousCameraPosition, previousCameraForward);
				float match = metaDepth <= 0.0 ? 0.0 : clamp(1.0 - abs(metaDepth - expectedDepth) / (0.03 * expectedDepth + 0.03), 0.0, 1.0)
					* smoothstep(0.85, 0.97, dot(HistoryMetaNormal(meta, MetaNormalBits), N));
				if (match > 0.05)
				{
					for (int layer = 0; layer < layers; layer++)
					{
						history[layer] = texelFetch(HistoryMask, ivec3(tap, layer), 0);
					}

					// A history that matched only partly (an edge) counts for less.
					previousFrames = float(HistoryMetaExtra(meta, MetaNormalBits) + 1u) * clamp(match * 2.0, 0.0, 1.0);
				}
			}
		}
	}

	float cap = motion.w > 0.5 ? min(movingFrames, maxFrames) : maxFrames;
	float frames = min(previousFrames + 1.0, max(cap, 1.0));
	float weight = 1.0 / frames;
	for (int layer = 0; layer < layers; layer++)
	{
		vec4 w = vec4(weight);
		// A channel whose light changed takes this frame's rays alone.
		for (int c = 0; c < 4; c++)
		{
			bool restart = layer < localLayers ? (slotsChanged & (1 << (layer * 4 + c))) != 0 : (directionalChanged & (1 << c)) != 0;
			w[c] = restart ? 1.0 : w[c];
		}

		imageStore(OutMask, ivec3(pixel, layer), StochasticRound8(mix(history[layer], fresh[layer], w), pixel, layer));
	}

	imageStore(OutMeta, pixel, uvec4(PackHistoryMeta(viewDepth, N, MetaNormalBits, clamp(uint(frames + 0.5), 1u, 16u) - 1u)));
}
#else
void main()
{
}
#endif

/////Compute
