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
// view's depth (the geometry prepass), rays towards each traced light that reaches the surface there (the local lights
// with a slot: params.w of their list entry; the directional lights of the mask), through the scene's shadow casters as
// a top level acceleration structure (with ray queries: only on devices that have them). Hard shadows: one ray to the
// light's centre. Soft shadows: softRays rays to points of the light (a local light's disc of its source radius facing
// the surface, a directional light's cone of its angular radius), in strata of the disc turned and shifted per pixel by
// a pattern that never changes from frame to frame; Engine/RayTracedShadowFilter.shader then averages them over the
// neighbouring pixels of the same surface. Nothing is kept from one frame to the next (no history, no reprojection):
// what a frame shows is what that frame traced, so nothing lags behind a moving light or surface.
// A pixel no traced light reaches (the sky, a surface out of every slot's range without a traced directional light)
// writes only its surface word (0): the lit materials never read the mask there.
//
// Mask layout (Lighting.RayShadowBinding): layers 0 .. localLayers - 1 the local lights' slots (slot s: layer s / 4,
// channel s % 4), then one layer of the directional lights (light i: channel i) when any is traced. The surface word
// (Engine/HistoryMeta.glsl: the view depth, the normal in 8 bits an axis) is what the filter tells surfaces apart by.

const uint MetaNormalBits = 8u;

// Rays a light at most with soft shadows (Rendering.Passes.RayTracedShadowPass: RayTracingSettings.SoftShadowRays).
const int MaxSoftRays = 8;

layout(binding = 1) uniform accelerationStructureEXT ShadowScene;

uniform sampler2D SceneDepth;
uniform sampler2D NormalRoughness;

uniform mat4 invViewProjection;
uniform vec3 cameraPosition;
uniform vec3 cameraForward;
uniform ivec2 screenSize;
uniform int localLayers;             // layers of local slots (0..2)
uniform int directionalLayer;        // the directional lights' layer (-1: none traced)
uniform int directionalLights;       // bit i: directional light i is traced
uniform vec4 directionalTanRadius;   // per directional light: tan of its angular radius (0: a point)
uniform int softRays;                // rays a light and pixel towards points of the light (0: hard, one ray to the centre)
uniform float rayLength;             // how far a directional light's ray looks for casters

layout(binding = 0, rgba8) uniform writeonly image2DArray OutMask;
layout(binding = 1, r32ui) uniform writeonly uimage2D OutMeta;

const int MaxLayers = 3;

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

// The 4 x 4 ordered dither index of a pixel (0..15): neighbouring pixels take turns evenly.
float Bayer4(ivec2 pixel)
{
	const int m[16] = int[16](0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5);
	return float(m[(pixel.y & 3) * 4 + (pixel.x & 3)]);
}

// Ray k of n for a pixel and a light: a point of the unit disc, in the k-th of the disc's strata (a grid of the sample
// square as near to square as n allows), shifted inside it by the pixel's hash and turned by the pixel's dither index. The
// same every frame: the shadow of a still scene does not change, and the filter's neighbours hold other points.
vec2 LightSample(ivec2 pixel, int light, int k, int n)
{
	int columns = n >= 4 ? (n >= 8 ? 4 : 2) : n;
	int rows = (n + columns - 1) / columns;
	uint seed = HashCoord(pixel, uint(light) * 7919u + uint(k) * 104729u + 17u);
	vec2 jitter = vec2(HashToFloat(seed), HashToFloat(seed * 747796405u + 2891336453u));
	vec2 u = (vec2(float(k % columns), float(k / columns)) + jitter) / vec2(float(columns), float(rows));
	vec2 disc = ConcentricDisc(u);
	float angle = (Bayer4(pixel) + 0.5) * (6.28318531 / 16.0) + float(light) * 2.39996323;
	float c = cos(angle);
	float s = sin(angle);
	return vec2(c * disc.x - s * disc.y, s * disc.x + c * disc.y);
}

void main()
{
	ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(pixel, screenSize)))
	{
		return;
	}

	float depth = texelFetch(SceneDepth, pixel, 0).r;
	if (depth >= 1.0)
	{
		// The sky: no surface reads the mask here (its word says so, the filter leaves it).
		imageStore(OutMeta, pixel, uvec4(0u));
		return;
	}

	int layers = localLayers + (directionalLayer >= 0 ? 1 : 0);
	vec2 uv = (vec2(pixel) + 0.5) / vec2(screenSize);
	vec4 world = invViewProjection * vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec3 position = world.xyz / world.w;
	vec3 N = normalize(texelFetch(NormalRoughness, pixel, 0).xyz);
	float viewDepth = dot(position - cameraPosition, cameraForward);
	vec3 origin = SceneRayOrigin(position, N, DepthRayBias(viewDepth));
	int rays = softRays > 0 ? min(softRays, MaxSoftRays) : 1;
	float perRay = 1.0 / float(rays);

	vec4 lit[MaxLayers];
	for (int layer = 0; layer < MaxLayers; layer++)
	{
		lit[layer] = vec4(1.0);
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
			float radius = softRays > 0 ? lelLights[i].params.z : 0.0;
			vec3 tangent, bitangent;
			TangentFrame(L, tangent, bitangent);
			float clear = 0.0;
			for (int k = 0; k < rays; k++)
			{
				vec3 target = lightPosition;
				if (radius > 0.0)
				{
					// A point of the light's disc facing the surface: the shadow as soft as the source is large.
					vec2 disc = LightSample(pixel, slot, k, rays) * radius;
					target += tangent * disc.x + bitangent * disc.y;
				}

				vec3 toTarget = target - origin;
				float distance = length(toTarget);
				// Up to the source's disc (a fixture smaller than the source does not hide it).
				float tMax = distance - min(radius, 0.5 * distance);
				clear += tMax <= 0.0 || ShadowRayClear(origin, toTarget / distance, tMax) ? 1.0 : 0.0;
				if (radius <= 0.0)
				{
					// A point light: every ray would be this one.
					clear *= float(rays);
					break;
				}
			}

			lit[slot >> 2][slot & 3] = clear * perRay;
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

			float tanRadius = softRays > 0 ? directionalTanRadius[i] : 0.0;
			vec3 tangent, bitangent;
			TangentFrame(L, tangent, bitangent);
			float clear = 0.0;
			for (int k = 0; k < rays; k++)
			{
				vec3 direction = L;
				if (tanRadius > 0.0)
				{
					// A direction of the light's cone (a disc of its angular radius one unit away).
					vec2 disc = LightSample(pixel, 8 + i, k, rays) * tanRadius;
					direction = normalize(L + tangent * disc.x + bitangent * disc.y);
				}

				clear += ShadowRayClear(origin, direction, rayLength) ? 1.0 : 0.0;
				if (tanRadius <= 0.0)
				{
					clear *= float(rays);
					break;
				}
			}

			lit[directionalLayer][i] = clear * perRay;
		}
	}

	if (!reached)
	{
		// No traced light reaches the surface here: nothing of the mask is read for it.
		imageStore(OutMeta, pixel, uvec4(0u));
		return;
	}

	for (int layer = 0; layer < layers; layer++)
	{
		imageStore(OutMask, ivec3(pixel, layer), lit[layer]);
	}

	imageStore(OutMeta, pixel, uvec4(PackHistoryMeta(viewDepth, N, MetaNormalBits, 0u)));
}
#else
void main()
{
}
#endif

/////Compute
