//Compute

#version 430

// The volumetric objects in the GI (LELEngine.Volumetrics.VolumetricMedia, Engine/GIMedia.glsl): one cloud written into
// the GI's media volume, a coarse world-aligned grid the GI's cones and rays read on their way. Per voxel of the grid
// inside the cloud's shape: how much of a path across the voxel the cloud covers (a: 1 - what it lets through) and the
// light it sends out along it (rgb, times a), added to what the clouds written before left there.
// The light is the march's (VolumetricMarch.shader): the sun through the cloud with its softer orders, the scene's
// shadows of it, the scene's other directional lights, the flat ambient light and the GI the cloud receives (else the
// sky), its emission and its lights, live and baked; the detail is left out (far finer than these voxels). One fetch
// of the lit voxels at the mip of the grid's voxel size gives the cloud's mean over the voxel.
// A cloud sends the directional lights' light mostly on forward (its phase), so how bright it is depends on where it is
// seen from: Media holds the light at the phase's mean over all directions (1), and MediaSun what the look-up needs
// to put the phase back for its own direction: the luminance of the directional lights' part (w) and that luminance
// times the mean cosine of its scattering (the orders' g weighted by their light) towards the light (xyz, world).

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform image3D Media;
layout(binding = 1, rgba16f) uniform image3D MediaSun;

#include "Engine/Shadows.glsl"
#include "Engine/Sky.glsl"

uniform sampler3D volLit;            // rgba8: density, sqrt(sun optical depth / volTauMax), ambient openness
uniform sampler3D volLightsA;        // lights 0-3: linear light at unit brightness (times the slot's factor), half resolution
uniform sampler3D volLightsB;        // lights 4-7
uniform sampler3D volLightsC;        // the baked lights: rgb their light, colours in it
uniform sampler3D volGIA;            // the GI received: mean radiance, a coarse grid
uniform sampler3D volDetail;         // r8, tiling: the detail the march eats into the cloud's edges with
uniform float volDetailStrength;
uniform float volDetailShade;
uniform float volEdgeBand;
uniform int volLightsActive;         // 1: a live light is lit (A and B are bound)
uniform int volBakedActive;          // 1: baked lights shine (C is bound)
uniform int volGIActive;             // 1: the GI in place of the sky light
uniform float volGIStrength;
uniform float volSigma;              // extinction per local unit at density 1
uniform float volTauMax;
uniform vec3 volSunDirection;        // local, towards the sun
uniform vec3 volSunColor;            // linear, times its strengths (the GI's share of it)
uniform float volPhaseG;
uniform vec3 volAmbientColor;
uniform float volSkyStrength;
uniform vec3 volAlbedo;
uniform vec3 volEmission;
uniform float volMultipleScattering;
uniform vec4 volLightColors[8];      // rgb: colour x intensity / the slot's factor (0 for a directional light's slot)
uniform int volDirectionalCount;     // the scene's other directional lights among the slots
uniform ivec4 volDirectionalSlots;
uniform vec4 volDirectionalDirs[4];  // xyz: local, towards the light
uniform vec4 volDirectionalColors[4];
uniform int volReceiveShadows;
uniform int volSunShadowLight;       // the sun's index among the directional lights: its shadow maps
uniform float volShadowStrength;

uniform mat4 volWorldToLocal;
uniform mat4 volLocalToWorld;
uniform vec3 volSize;                // the shape's extents (local units)
uniform float volVoxelWorld;         // world units per voxel of the cloud
uniform float volWorldScale;         // world units per local unit
uniform float volExposure;
uniform float volOpacity;
uniform float volGIContribution;     // how much of the cloud's light the GI takes

uniform ivec3 mediaFirst;            // the first voxel of the grid this dispatch covers
uniform ivec3 mediaEnd;              // one past its last
uniform vec3 mediaMin;               // world position of the grid's first corner
uniform float mediaVoxel;            // world units per voxel of the grid

// The scene's shadow of the cloud's sun at a point (the march's): one comparison in the cascade it falls in, faded
// out towards the shadow distance; lit outside the cascades.
float VolSceneShadow(vec3 worldPos)
{
	if (!ShadowLightHasMap(volSunShadowLight))
	{
		return 1.0;
	}

	int cascade = ShadowLightCascade(volSunShadowLight, worldPos);
	vec4 rect = lelShadowCascades[cascade].rect;
	vec3 proj = (lelShadowCascades[cascade].matrix * vec4(worldPos, 1.0)).xyz;
	if (proj.z > 1.0 || any(lessThan(proj.xy, rect.xy)) || any(greaterThan(proj.xy, rect.zw)))
	{
		return 1.0;
	}

	return mix(1.0, texture(ShadowMap, proj), ShadowFade(worldPos));
}

void main()
{
	ivec3 v = mediaFirst + ivec3(gl_GlobalInvocationID);
	if (any(greaterThanEqual(v, mediaEnd)))
	{
		return;
	}

	vec3 world = mediaMin + (vec3(v) + 0.5) * mediaVoxel;
	vec3 uvw = (volWorldToLocal * vec4(world, 1.0)).xyz / volSize + 0.5;
	if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
	{
		return;
	}

	// The cloud's mean over the grid's voxel.
	float lod = log2(max(mediaVoxel / volVoxelWorld, 1.0));
	vec4 lit = textureLod(volLit, uvw, lod);
	float density = lit.r;
	if (density <= 0.002)
	{
		return;
	}

	// The detail the march eats into the cloud's edges with, at its mean (the detail texture's coarsest level): thin
	// clouds are a good deal thinner drawn than their voxels say, and the pockets it eats are darker.
	float shade = 1.0;
	if (volDetailStrength > 0.0 || volDetailShade > 0.0)
	{
		float detail = textureLod(volDetail, vec3(0.5), 16.0).r;
		float edge = 1.0 - clamp(density / volEdgeBand, 0.0, 1.0);
		density = clamp(density - volDetailStrength * edge * (1.0 - detail), 0.0, 1.0);
		shade = mix(1.0 - volDetailShade, 1.0, detail);
		if (density <= 0.002)
		{
			return;
		}
	}

	// The directional lights: per order of scattering its light (the phase's mean, 1) and its g (the direct order the
	// cloud's, the softer ones a half and a quarter of it, as the march's phases); `forward` gathers the luminance times
	// g towards each light.
	const vec3 Luminance = vec3(0.2126, 0.7152, 0.0722);
	vec3 orderG = volPhaseG * vec3(1.0, 0.5, 0.25);
	mat3 toWorld = mat3(volLocalToWorld);
	float softer = volMultipleScattering;
	float tau = lit.g * lit.g * volTauMax;
	vec3 orders = vec3(exp(-tau), 0.5 * softer * exp(-tau * 0.5), 0.25 * softer * exp(-tau * 0.25));
	float shadow = volReceiveShadows != 0 ? mix(1.0, VolSceneShadow(world), volShadowStrength) : 1.0;
	vec3 sun = volSunColor * (shadow * (orders.x + orders.y + orders.z));
	float sunLuminance = dot(volAlbedo * volSunColor, Luminance) * shadow;
	vec3 forward = normalize(toWorld * volSunDirection) * (sunLuminance * dot(orders, orderG));

	vec4 lightsA = vec4(0.0);
	vec4 lightsB = vec4(0.0);
	if (volLightsActive != 0)
	{
		float lightsLod = max(lod - 1.0, 0.0);
		lightsA = textureLod(volLightsA, uvw, lightsLod);
		lightsB = textureLod(volLightsB, uvw, lightsLod);
		// The other directional lights are scattered as the sun is (their slots hold e^(-tau / 4)).
		for (int k = 0; k < volDirectionalCount; k++)
		{
			int slot = volDirectionalSlots[k];
			float through = slot < 4 ? lightsA[slot] : lightsB[slot - 4];
			float through2 = through * through;
			vec3 lightOrders = vec3(through2 * through2, 0.5 * softer * through2, 0.25 * softer * through);
			sun += volDirectionalColors[k].rgb * (lightOrders.x + lightOrders.y + lightOrders.z);
			forward += normalize(toWorld * volDirectionalDirs[k].xyz) * (dot(volAlbedo * volDirectionalColors[k].rgb, Luminance) * dot(lightOrders, orderG));
		}
	}

	float directionalLuminance = dot(volAlbedo * sun, Luminance) * shade;
	forward *= shade;

	// The light around: the flat ambient through the cloud's openness, and the GI it receives or else the sky.
	vec3 around = volAmbientColor * lit.b;
	if (volGIActive != 0)
	{
		around += textureLod(volGIA, uvw, 0.0).rgb * volGIStrength;
	}
	else if (volSkyStrength > 0.0)
	{
		vec3 sky = (SkyRadiance(vec3(1.0, 0.0, 0.0)) + SkyRadiance(vec3(-1.0, 0.0, 0.0)) + SkyRadiance(vec3(0.0, 1.0, 0.0))
			+ SkyRadiance(vec3(0.0, -1.0, 0.0)) + SkyRadiance(vec3(0.0, 0.0, 1.0)) + SkyRadiance(vec3(0.0, 0.0, -1.0))) * (volSkyStrength / 6.0);
		around += sky * lit.b;
	}

	vec3 light = volAlbedo * (sun + around) * shade + volEmission * clamp(density * 1.5, 0.0, 1.0);
	if (volLightsActive != 0)
	{
		light += lightsA.x * volLightColors[0].rgb + lightsA.y * volLightColors[1].rgb + lightsA.z * volLightColors[2].rgb + lightsA.w * volLightColors[3].rgb
			+ lightsB.x * volLightColors[4].rgb + lightsB.y * volLightColors[5].rgb + lightsB.z * volLightColors[6].rgb + lightsB.w * volLightColors[7].rgb;
	}

	if (volBakedActive != 0)
	{
		light += textureLod(volLightsC, uvw, max(lod - 1.0, 0.0)).rgb;
	}

	// How much of a path across the grid's voxel the cloud covers (its optical depth there), and its light times that.
	// Coverage, not optical depth, is what the levels above average: a small thick cloud spread over a coarse voxel as
	// optical depth would be a large thin one, all of whose light gets out (many times the light its surface gives).
	// With the clouds written before: both cover (1 - the product of what each lets through), their light adds up.
	float depth = density * volSigma / volWorldScale * mediaVoxel * volOpacity;
	float coverage = 1.0 - exp(-depth);
	float scale = volExposure * volGIContribution * coverage;
	vec4 before = imageLoad(Media, v);
	imageStore(Media, v, vec4(min(before.rgb + light * scale, vec3(60000.0)), before.a + coverage - before.a * coverage));
	imageStore(MediaSun, v, clamp(imageLoad(MediaSun, v) + vec4(forward, directionalLuminance) * scale, vec4(-60000.0), vec4(60000.0)));
}

/////Compute
