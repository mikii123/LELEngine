//Properties
// The exhaust plume of an engine nozzle (the DriveCone component): two cone shells along +Z from the object's origin, the
// flame and the hot core inside it, and a glowing disc on the nozzle (seen from behind), shaped and animated in the vertex shader (length, width, ripples flowing out, the end
// swaying) and lit in the fragment shader: the flame's color along the plume, fading towards the silhouette, broken up
// by the flowing turbulence, the core with its shock diamonds. Light only (added to the scene). The shape, colors and
// animation come from the component per draw ([PerRendererData]: on a plain mesh renderer this material draws nothing).
Brightness ("Brightness", Float) = 1
// Fades the plume out where it comes this close (world units) to the opaque surface behind it (the hull it leaves).
SoftFade ("Soft Fade Distance", Float) = 0.3
[PerRendererData] DriveShape ("Shape", Vector) = (0, 0, 0, 0)
[PerRendererData] DriveBend ("Bend", Vector) = (0, 0, 1.5, 0)
[PerRendererData] DriveCore ("Core", Vector) = (0, 0, 0, 1)
[PerRendererData] DriveParams ("Parameters", Vector) = (0, 1, 0, 1)
[PerRendererData] DriveDiamonds ("Diamonds", Vector) = (0, 1, 0, 0.5)
[PerRendererData] DriveDiamondShape ("Diamond Shape, Nozzle Glow", Vector) = (0.5, 0.7, 0, 0)
[PerRendererData] DriveDiamondColor ("Diamond Color", Vector) = (0, 0, 0, 0)
ShadowCaster Off
SceneDepth On
ZWrite Off
Cull Off
Blend One One, Zero One
Queue Transparent
/////Properties

//Vertex

#version 430

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;
uniform vec4 DriveShape;           // nozzle radius, length, core shell length, distance the gas has flowed (local units)
uniform vec4 DriveBend;            // sway of the plume's end (xy), streaks round the plume, seed
uniform vec4 DriveParams;          // core radius, falloff, turbulence, turbulence size (local units)
uniform float DriveProfile[17];    // the plume's radius at t = i / 16 (local units)

in vec3 vPosition;   // the hull's unit rings: xy on the unit circle, z the share of the shell's length
in vec3 vNormal;
in vec2 vTexCoord2;  // x: 0 the flame shell, 1 the core shell, 2 the nozzle's disc (rings of radius 0..1 at z 0)

out vec3 fWorldPosition;
out vec3 fWorldNormal;
out vec3 fFlow;      // the turbulence's coordinates (the gas flowing out)
out float fShare;    // share of the plume's length
out float fLayer;
out float fRadial;   // the nozzle's disc: share of its radius

float DriveRadius(float t)
{
	float x = clamp(t, 0.0, 1.0) * 16.0;
	int i = min(int(x), 15);
	return mix(DriveProfile[i], DriveProfile[i + 1], x - float(i));
}

float Hash3(vec3 p)
{
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

float ValueNoise(vec3 x)
{
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(Hash3(i), Hash3(i + vec3(1, 0, 0)), f.x), mix(Hash3(i + vec3(0, 1, 0)), Hash3(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(Hash3(i + vec3(0, 0, 1)), Hash3(i + vec3(1, 0, 1)), f.x), mix(Hash3(i + vec3(0, 1, 1)), Hash3(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

void main()
{
	fLayer = vTexCoord2.x;
	fRadial = length(vPosition.xy);
	if (fLayer > 1.5)
	{
		// The nozzle's disc, the width of the plume at the nozzle, a little out of it (so the hull behind does not fade it).
		vec4 disc = modelMatrix * vec4(vPosition.xy * DriveRadius(0.0), DriveShape.x * 0.05, 1.0);
		fWorldPosition = disc.xyz;
		fWorldNormal = mat3(modelMatrix) * vec3(0.0, 0.0, 1.0);
		fFlow = vec3(vPosition.xy * max(DriveBend.z, 0.1), -DriveShape.w / max(DriveParams.w, 1e-4)) + vec3(DriveBend.w * 37.0);
		fShare = 0.0;
		gl_Position = projectionMatrix * viewMatrix * disc;
		return;
	}

	float core = vTexCoord2.x;
	float plumeLength = max(DriveShape.y, 1e-4);
	float z = vPosition.z * mix(DriveShape.y, DriveShape.z, core);
	float t = z / plumeLength;

	// The flame takes the plume's radius; the core narrows from its radius at the nozzle to a point.
	float radius = mix(DriveRadius(t), DriveParams.x * (1.0 - 0.85 * vPosition.z), core);

	// Ripples flowing out with the gas, growing downstream (less on the core).
	fFlow = vec3(vPosition.xy * max(DriveBend.z, 0.1), (z - DriveShape.w) / max(DriveParams.w, 1e-4)) + vec3(DriveBend.w * 37.0);
	float ripple = ValueNoise(fFlow) - 0.5;
	radius *= max(0.0, 1.0 + DriveParams.z * ripple * (0.3 + 0.9 * t) * mix(1.0, 0.4, core));

	vec3 p = vec3(vPosition.xy * radius + DriveBend.xy * (t * t), z);
	vec4 world = modelMatrix * vec4(p, 1.0);
	fWorldPosition = world.xyz;
	fWorldNormal = mat3(modelMatrix) * vNormal;
	fShare = t;
	gl_Position = projectionMatrix * viewMatrix * world;
}

/////Vertex

//Fragment

#version 430

#include "Engine/SceneDepth.glsl"

in vec3 fWorldPosition;
in vec3 fWorldNormal;
in vec3 fFlow;
in float fShare;
in float fLayer;
in float fRadial;

uniform mat4 viewMatrix;
uniform vec4 DriveShape;           // nozzle radius, length, core shell length, distance the gas has flowed (local units)
uniform vec4 DriveCore;            // core light (rgb), core length (local units)
uniform vec4 DriveParams;          // core radius, falloff, turbulence, turbulence size (local units)
uniform vec4 DriveDiamonds;        // count, spacing, first one's distance from the nozzle (local units), length share
uniform vec4 DriveDiamondShape;    // sharpness, brightness kept per diamond, the nozzle's glow
uniform vec4 DriveDiamondColor;    // the first diamond's light (rgb)
uniform vec4 DriveFlame[8];        // flame light (rgb) and density (a) at t = i / 7
uniform float Brightness;
uniform float SoftFade;

out vec4 FragColor;

vec4 DriveFlameAt(float t)
{
	float x = clamp(t, 0.0, 1.0) * 7.0;
	int i = min(int(x), 6);
	return mix(DriveFlame[i], DriveFlame[i + 1], x - float(i));
}

float Hash3(vec3 p)
{
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.x + p.y) * p.z);
}

float ValueNoise(vec3 x)
{
	vec3 i = floor(x);
	vec3 f = fract(x);
	f = f * f * (3.0 - 2.0 * f);
	return mix(mix(mix(Hash3(i), Hash3(i + vec3(1, 0, 0)), f.x), mix(Hash3(i + vec3(0, 1, 0)), Hash3(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(Hash3(i + vec3(0, 0, 1)), Hash3(i + vec3(1, 0, 1)), f.x), mix(Hash3(i + vec3(0, 1, 1)), Hash3(i + vec3(1, 1, 1)), f.x), f.y), f.z);
}

void main()
{
	// Facing the camera = looking through the middle of the plume: bright there, fading towards the silhouette.
	vec3 cameraWorld = -transpose(mat3(viewMatrix)) * viewMatrix[3].xyz;
	vec3 toCamera = normalize(cameraWorld - fWorldPosition);
	float facing = abs(dot(normalize(fWorldNormal), toCamera));
	float body = pow(facing, DriveParams.y) * smoothstep(0.0, 0.45, facing);

	float turbulence = DriveParams.z;
	// Value noise sits mostly near 0.5: stretched to full contrast, the streaks read as fast gas.
	float n = smoothstep(0.25, 0.75, 0.65 * ValueNoise(fFlow) + 0.35 * ValueNoise(fFlow * 2.03 + vec3(1.7, 9.2, 4.1)));
	vec3 light;
	if (fLayer > 1.5)
	{
		// The nozzle's disc: the hot gas seen down the nozzle (from behind), the core's white in the middle and the
		// flame's color out to the rim.
		float rim = 1.0 - fRadial * fRadial;
		vec3 glow = DriveCore.rgb * (0.25 * rim * rim) + DriveFlameAt(0.0).rgb * rim;
		light = glow * (DriveDiamondShape.z * sqrt(facing) * mix(1.0, 0.6 + 0.8 * n, turbulence));
	}
	else if (fLayer < 0.5)
	{
		// The flame thins out over the last quarter of the plume whatever its gradient says (no cut at the mesh's end).
		vec4 flame = DriveFlameAt(fShare);
		light = flame.rgb * (flame.a * body * smoothstep(1.0, 0.75, fShare) * mix(1.0, 0.25 + 1.5 * n, turbulence));
	}
	else
	{
		// The core fades over its length; the shock diamonds are bright knots on it, each dimmer than the one before.
		float z = fShare * DriveShape.y;
		float fade = clamp(1.0 - z / DriveCore.w, 0.0, 1.0);
		light = DriveCore.rgb * (body * body * fade * fade * mix(1.0, 0.7 + 0.6 * n, turbulence * 0.5));
		if (DriveDiamonds.x > 0.5)
		{
			float x = (z - DriveDiamonds.z) / DriveDiamonds.y;
			float k = floor(x + 0.5);
			if (k >= 0.0 && k < DriveDiamonds.x)
			{
				float knot = clamp(1.0 - abs(x - k) / (0.5 * DriveDiamonds.w), 0.0, 1.0);
				light += DriveDiamondColor.rgb * (knot * knot * pow(body, DriveDiamondShape.x) * pow(DriveDiamondShape.y, k));
			}
		}
	}

	light *= Brightness * LelSoftParticleFade(0.0, SoftFade);
	FragColor = vec4(light, 0.0);
}

/////Fragment
