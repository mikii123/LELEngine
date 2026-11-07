//Vertex

#version 430

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// A volumetric object (LELEngine.Volumetrics) marched per pixel of its target: the pixel's view ray in the object's local
// space, through the part of the shape in front of the opaque scene. Per step one fetch of the lit volume gives the
// cloud's density, the sun's optical depth towards the point and the ambient light's openness there; the tiling detail
// eats into the cloud's edges (the relief the voxels lack), the lights inside add their light while lit. Empty blocks of
// 4 voxels (the occupancy volume) are crossed in one step. Output: the in-scattered light, premultiplied (rgb), and
// 1 - transmittance (a), and the light's mean distance along the ray (the temporal filter reprojects with it). The start
// of the steps is jittered per pixel, with a pattern that changes every frame under the temporal filter (fixed without);
// the scene's sun shadows fall into the cloud when it receives them.
//   default   seen from outside (VolumetricObject): even steps through the shape, at most volMaxSteps of them.
//   VOL_HQ    high quality, good inside (VolumetricObjectHQ): steps that grow with the distance from the camera (fine
//             near the camera, where the cloud fills the view when it is inside).

#include "Engine/Volumetrics/VolumetricCommon.glsl"
#include "Engine/Shadows.glsl"
#include "Engine/Sky.glsl"

uniform sampler3D volLit;            // rgba8: density, sqrt(sun optical depth / volTauMax), ambient openness
uniform sampler3D volOccupancy;      // r8: densest voxel per 4^3 block (and the voxel around it)
uniform sampler3D volDetail;         // r8, tiling
uniform sampler3D volLightsA;        // lights 0-3: linear light at unit brightness (times the slot's factor), half resolution
uniform sampler3D volLightsB;        // lights 4-7
uniform sampler3D volGIA;            // the GI received (VolumetricGI.shader): mean radiance, a coarse grid
uniform sampler3D volGIB;            // its directional term (luminance, local axes)
uniform int volGIActive;             // 1: the GI in place of the sky light
uniform float volGIStrength;
uniform sampler2D volDepth;          // the opaque scene: eye depth (volDepthIsWindow 0) or window depth (1)
uniform int volDepthIsWindow;
uniform vec4 volDepthParams;
uniform vec3 volBlocks;              // blocks of the occupancy (4 voxels each)
uniform float volVoxel;              // local units per voxel
uniform float volSigma;              // extinction per local unit at density 1
uniform float volTauMax;
uniform float volStep;               // local units per step through the shape
uniform float volMinStep;            // VOL_HQ: the smallest step, near the camera
uniform float volNearStepScale;      // VOL_HQ: step = distance from the camera x this, between the two
uniform int volMaxSteps;
uniform float volPixelAngle;         // the size of a target pixel at distance 1 (any units)
uniform float volJitterOffset;       // added to the per pixel jitter (changes every frame for the temporal filter)
uniform vec3 volSunDirection;        // local, towards the sun
uniform vec3 volSunColor;            // linear, times its strength
uniform vec3 volAmbientColor;        // the flat ambient light: the scene's or the volume's own (linear, times its strength)
uniform float volSkyStrength;        // how much of the sky lights the cloud (0: none)
uniform vec3 volAlbedo;
uniform vec3 volEmission;            // times its strength
uniform float volPhaseG;
uniform float volMultipleScattering;
uniform float volDetailStrength;
uniform float volDetailShade;
uniform float volEdgeBand;
uniform float volDetailFrequency;    // detail tiles per local unit
uniform float volDetailVoxel;        // local units per detail voxel
uniform vec3 volDetailOffset;        // the detail's drift (wind x time), local units
uniform int volLightsActive;
uniform vec4 volLightColors[8];      // rgb: colour x intensity / the slot's factor
uniform mat4 volLocalToWorld;
uniform int volReceiveShadows;
uniform float volShadowStrength;     // 1: the scene's shadows block the sun fully
uniform float volExposure;
uniform float volOpacity;

layout(location = 0) out vec4 FragColor;
layout(location = 1) out float FragDistance;

// Henyey-Greenstein, scaled so isotropic scattering is 1.
float VolPhase(float cosTheta, float g)
{
	float g2 = g * g;
	return (1.0 - g2) / pow(max(1e-4, 1.0 + g2 - 2.0 * g * cosTheta), 1.5);
}

// The scene's sun shadow at a point inside the cloud: one comparison in the cascade it falls in, faded out towards the
// shadow distance; lit outside the cascades.
float VolSceneShadow(vec3 worldPos)
{
	if (shadowsEnabled == 0 || shadowCascadeCount <= 0)
	{
		return 1.0;
	}

	int cascade = ShadowCascade(worldPos);
	vec4 rect = ShadowTileRect(cascade);
	vec3 proj = (shadowMatrices[cascade] * vec4(worldPos, 1.0)).xyz;
	if (proj.z > 1.0 || any(lessThan(proj.xy, rect.xy)) || any(greaterThan(proj.xy, rect.zw)))
	{
		return 1.0;
	}

	float fade = 1.0;
	if (shadowFade.w > 0.0)
	{
		fade = clamp((shadowFade.w - distance(worldPos, shadowFade.xyz)) / (shadowFade.w * 0.1), 0.0, 1.0);
	}

	return mix(1.0, texture(ShadowMap, proj), fade);
}

// Where the ray leaves the occupancy block p is in (local units along the ray from p).
float VolBlockExit(vec3 p, vec3 dir)
{
	vec3 blockSize = volSize / volBlocks;
	vec3 cell = floor((p / volSize + 0.5) * volBlocks);
	vec3 lo = cell * blockSize - volSize * 0.5;
	vec3 hi = lo + blockSize;
	vec3 safe = mix(dir, vec3(1e-12), lessThan(abs(dir), vec3(1e-12)));
	vec3 t = (mix(lo, hi, greaterThan(dir, vec3(0.0))) - p) / safe;
	return max(min(min(t.x, t.y), t.z), 0.0);
}

void main()
{
	vec2 pixel = gl_FragCoord.xy;
	VolRay ray = VolMakeRay(pixel);
	vec2 range = VolIntersect(ray.origin, ray.dir);
	range.x = max(range.x, 0.0);

	// The opaque scene ends the ray.
	float depth = texelFetch(volDepth, ivec2(pixel * vec2(textureSize(volDepth, 0)) / volTargetSize), 0).r;
	float eye = volDepthIsWindow != 0 ? VolEyeDepth(depth, volDepthParams) : depth;
	float cosForward = max(dot(ray.worldDir, volCameraForward), 1e-4);
	range.y = min(range.y, eye / cosForward * ray.scale);

	FragDistance = 0.0;
	if (range.y <= range.x)
	{
		FragColor = vec4(0.0);
		return;
	}

	// The light that does not depend on the point: the sun's phase for this view, the sky from six sides.
	float cosSun = dot(ray.dir, volSunDirection);
	float phase0 = VolPhase(cosSun, volPhaseG);
	float phase1 = VolPhase(cosSun, volPhaseG * 0.5) * 0.5 * volMultipleScattering;
	float phase2 = VolPhase(cosSun, volPhaseG * 0.25) * 0.25 * volMultipleScattering;
	vec3 sky = vec3(0.0);
	if (volSkyStrength > 0.0)
	{
		sky = (SkyRadiance(vec3(1.0, 0.0, 0.0)) + SkyRadiance(vec3(-1.0, 0.0, 0.0)) + SkyRadiance(vec3(0.0, 1.0, 0.0))
			+ SkyRadiance(vec3(0.0, -1.0, 0.0)) + SkyRadiance(vec3(0.0, 0.0, 1.0)) + SkyRadiance(vec3(0.0, 0.0, -1.0))) * (volSkyStrength / 6.0);
	}

	float jitter = fract(VolNoise(pixel) + volJitterOffset);
	vec3 rayDir = normalize(ray.dir);

	float distanceSum = 0.0;
	float weightSum = 0.0;
#ifdef VOL_HQ
	float t = range.x;
	t += jitter * clamp(t * volNearStepScale, volMinStep, volStep);
#else
	float span = range.y - range.x;
	float steps = clamp(ceil(span / volStep), 4.0, float(volMaxSteps));
	float evenStep = span / steps;
	float t = range.x + jitter * evenStep;
#endif

	vec3 color = vec3(0.0);
	float transmittance = 1.0;
	int iterations = volMaxSteps * 3;
	for (int i = 0; i < iterations && t < range.y; i++)
	{
		vec3 p = ray.origin + ray.dir * t;
		vec3 uvw = p / volSize + 0.5;

		// An empty block: on to the first step past where the ray leaves it, at the same phase of the pixel's step pattern
		// (restarting at the block's face would put every pixel's samples on the blocks' faces: contour lines).
		if (texelFetch(volOccupancy, ivec3(clamp(uvw, 0.0, 0.99999) * volBlocks), 0).r < 0.002)
		{
			float blockEnd = t + VolBlockExit(p, ray.dir) + 0.01 * volVoxel;
#ifdef VOL_HQ
			t = blockEnd + jitter * clamp(blockEnd * volNearStepScale, volMinStep, volStep);
#else
			t = range.x + (ceil((blockEnd - range.x) / evenStep - jitter) + jitter) * evenStep;
#endif
			continue;
		}

#ifdef VOL_HQ
		// The last step ends with the ray.
		float dt = clamp(t * volNearStepScale, volMinStep, volStep);
		dt = min(dt, range.y - t + dt * 0.5);
#else
		float dt = evenStep;
#endif

		// The levels that fit a pixel's size at this distance: sharp up close, no sparkle far away.
		float footprint = t * volPixelAngle;
		float lod = log2(max(footprint / volVoxel, 1.0));
		vec4 lit = textureLod(volLit, uvw, lod);
		float density = lit.r;
		if (density > 0.002)
		{
			float shade = 1.0;
			if (volDetailStrength > 0.0 || volDetailShade > 0.0)
			{
				float detailLod = log2(max(footprint / volDetailVoxel, 1.0));
				float detail = textureLod(volDetail, (p + volDetailOffset) * volDetailFrequency, detailLod).r;
				float edge = 1.0 - clamp(density / volEdgeBand, 0.0, 1.0);
				density = clamp(density - volDetailStrength * edge * (1.0 - detail), 0.0, 1.0);
				shade = mix(1.0 - volDetailShade, 1.0, detail);
			}

			float stepT = exp(-density * volSigma * dt);
			float weight = transmittance * (1.0 - stepT);
			if (weight > 0.0)
			{
				float tau = lit.g * lit.g * volTauMax;
				vec3 sun = volSunColor * (phase0 * exp(-tau) + phase1 * exp(-tau * 0.5) + phase2 * exp(-tau * 0.25));
				if (volReceiveShadows != 0)
				{
					sun *= mix(1.0, VolSceneShadow((volLocalToWorld * vec4(p, 1.0)).xyz), volShadowStrength);
				}
				// The light around: the flat ambient through the cloud's openness, and the GI (from every side, dimmed by the
				// cloud towards each, scattered towards the eye with the phase: a + g (b . ray)) or else the sky.
				vec3 around = volAmbientColor * lit.b;
				if (volGIActive != 0)
				{
					vec3 gi = textureLod(volGIA, uvw, 0.0).rgb;
					vec3 giDirection = textureLod(volGIB, uvw, 0.0).xyz;
					float giLuminance = max(dot(gi, vec3(0.2126, 0.7152, 0.0722)), 1e-6);
					around += gi * (max(1.0 + volPhaseG * dot(giDirection, rayDir) / giLuminance, 0.0) * volGIStrength);
				}
				else
				{
					around += sky * lit.b;
				}

				vec3 light = volAlbedo * (sun + around) * shade + volEmission * clamp(density * 1.5, 0.0, 1.0);
				if (volLightsActive != 0)
				{
					vec4 a = textureLod(volLightsA, uvw, max(lod - 1.0, 0.0));
					vec4 b = textureLod(volLightsB, uvw, max(lod - 1.0, 0.0));
					light += a.x * volLightColors[0].rgb + a.y * volLightColors[1].rgb + a.z * volLightColors[2].rgb + a.w * volLightColors[3].rgb
						+ b.x * volLightColors[4].rgb + b.y * volLightColors[5].rgb + b.z * volLightColors[6].rgb + b.w * volLightColors[7].rgb;
				}

				color += weight * light;
				distanceSum += weight * t;
				weightSum += weight;
				transmittance *= stepT;
				if (transmittance < 0.005)
				{
					transmittance = 0.0;
					break;
				}
			}
		}

		t += dt;
	}

	// World units along the ray (the temporal filter reprojects the light from there).
	FragDistance = (weightSum > 1e-4 ? distanceSum / weightSum : (range.x + range.y) * 0.5) / ray.scale;
	FragColor = vec4(color * volExposure, 1.0 - transmittance) * volOpacity;
}

/////Fragment
