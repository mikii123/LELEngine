//Properties
// An object's surface baked from many directions (LELEngine.Rendering.Impostors.ImpostorBaker fills these), lit like
// the mesh it stands for. Tint multiplies the colour, Emission is the emission map's tint (rgb) and scale (a).
Color ("Tint", Color) = (1, 1, 1, 1)
[HDR] Emissive ("Emission (rgb tint, a intensity)", Color) = (1, 1, 1, 0)
// Lighting from the scene's Specular Strength / Shine (no specular atlas): the roughness the reflections are traced with.
Roughness ("Roughness", Range(0, 1)) = 0.6
ShapeMap ("Shape (rg normal, b depth, a coverage)", 2D) = "black" {}
BaseMap ("Base (rgb colour, a smoothness or occlusion)", 2D) = "white" {}
// With a specular atlas the surface is lit as Engine/LitTextured.shader's physically based workflows.
[Toggle(SPECULAR_MAP)] SpecularMap ("Specular (rgb colour, a occlusion)", 2D) = "black" {}
// Some of the baked surfaces have no specular colour (materials lit with the scene's highlight beside physically
// based ones): those keep the scene's highlight. Set by the bake.
[Toggle(MIXED_WORKFLOW)] SceneHighlights ("Scene Highlights Where No Specular", Float) = 0
[Toggle(EMISSION_MAP)] EmissionMap ("Emission (rgb)", 2D) = "black" {}
[HideInInspector] ImpostorSphere ("Sphere (xyz centre, w radius)", Vector) = (0, 0, 0, 1)
[HideInInspector] ImpostorFrames ("Frames (x per side, y texels of a frame, z hemisphere)", Vector) = (8, 128, 0, 0)
Cutoff ("Alpha Cutoff", Range(0.05, 0.95)) = 0.5
// How far the views are shifted by the depth they stored (0: flat pictures), and how many times the shift is refined
// by the depth where it landed (each step is one more lookup of every view; 2 removes most of the doubled edges).
Parallax ("Parallax", Range(0, 1)) = 1
ParallaxSteps ("Parallax Steps", Range(1, 4)) = 2
// How strictly a view must agree with the ray about the depth where it is read before it has a say in the blend (0:
// every view counts by its direction alone). Higher keeps parts hidden from a view from smearing over what is behind
// them, and gaps open.
Agreement ("View Agreement", Range(0, 64)) = 24
// How far the shadow the impostor casts is pushed away from the light, in radii of its bounding sphere: the surface a
// light sees and the one the camera sees are rebuilt from different views and do not meet exactly.
ShadowBias ("Shadow Bias", Range(0, 0.25)) = 0.04
Queue AlphaTest
ShadowCaster Custom
GeometryPrepass Custom
/////Properties

//Vertex

#version 430
invariant gl_Position;

// Octahedral impostor: the mesh is a proxy (the bounds of the object as a box, what picking, culling and selection
// see); its vertices carry in their texture coordinates the corners of an outline around the unit circle, which this
// stage lays out as a billboard in front of the object's bounding sphere, facing the view. The fragment stage then
// rebuilds the surface along each pixel's ray from the three captured views nearest to that ray's direction.
// Variants: LEL_INSTANCED (GPU-driven), LEL_GEOMETRY_PREPASS and LEL_SHADOW_CASTER (the depth passes, drawn with this
// shader: ShadowCaster Custom, GeometryPrepass Custom), SPECULAR_MAP, MIXED_WORKFLOW, EMISSION_MAP (the material's).
#ifdef LEL_GEOMETRY_PREPASS
#define LEL_PREVIOUS_MODELS 1
#endif
#include "Engine/Instancing.glsl"

uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform vec4 ImpostorSphere;

in vec3 vPosition;
in vec2 vTexCoord;

// The impostor's space: the object's, with the bounding sphere's centre at the origin and its radius 1.
out vec3 fPosition;        // the proxy's point there
out vec3 fWorld;           // and in the world
flat out vec4 fEye;        // the eye there (w 1), or the direction an orthographic view looks along (w 0)
flat out vec4 fWorldEye;   // the same in the world
flat out mat3 fNormalMatrix;
#ifdef LEL_GEOMETRY_PREPASS
// Rows of (world matrix - last frame's world matrix): a point of the object times them is its motion.
flat out vec4 fMotionX;
flat out vec4 fMotionY;
flat out vec4 fMotionZ;
#endif

void main()
{
	mat4 model = LelModelMatrix();
	mat4 inverseModel = LelInverseModelMatrix();
	vec3 centre = ImpostorSphere.xyz;
	float radius = ImpostorSphere.w;

	// The bounding sphere in the world (the largest scale of the object's axes).
	vec3 pivot = (model * vec4(centre, 1.0)).xyz;
	float worldRadius = radius * sqrt(max(dot(model[0].xyz, model[0].xyz), max(dot(model[1].xyz, model[1].xyz), dot(model[2].xyz, model[2].xyz))));

	// A perspective view looks from a point; an orthographic one (a directional light's shadow map) along a direction.
	mat4 inverseView = inverse(viewMatrix);
	bool orthographic = projectionMatrix[3][3] != 0.0;
	vec3 eye = inverseView[3].xyz;
	vec3 forward = -normalize(inverseView[2].xyz);
	vec3 toEye = orthographic ? -forward : eye - pivot;
	float eyeDistance = max(length(toEye), 1e-6);
	vec3 axis = toEye / eyeDistance;

	// The billboard: a plane facing the eye that touches the sphere in front, as wide as the sphere's outline there.
	// Every ray that can meet the object crosses it, and before the object: the depth written is never nearer than it.
	vec3 right = normalize(cross(abs(axis.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), axis));
	vec3 up = cross(axis, right);
	vec3 planeCentre;
	float halfSize;
	if (orthographic)
	{
		planeCentre = pivot + axis * worldRadius;
		halfSize = worldRadius;
	}
	else
	{
		// An eye at the sphere (or inside it) still gets a plane beyond its near plane, as wide as the view.
		float near = projectionMatrix[3][2] / (projectionMatrix[2][2] - 1.0);
		float outside = max(eyeDistance, worldRadius * 1.05);
		float planeDistance = max(eyeDistance - worldRadius, near * 1.5);
		planeCentre = eye - axis * planeDistance;
		halfSize = planeDistance * worldRadius / sqrt(outside * outside - worldRadius * worldRadius);
	}

	vec2 corner = vTexCoord * 2.0 - 1.0;
	vec3 world = planeCentre + (right * corner.x + up * corner.y) * halfSize;
	gl_Position = projectionMatrix * viewMatrix * vec4(world, 1.0);

	fWorld = world;
	fPosition = ((inverseModel * vec4(world, 1.0)).xyz - centre) / radius;
	if (orthographic)
	{
		fWorldEye = vec4(forward, 0.0);
		fEye = vec4(mat3(inverseModel) * forward / radius, 0.0);
	}
	else
	{
		fWorldEye = vec4(eye, 1.0);
		fEye = vec4(((inverseModel * vec4(eye, 1.0)).xyz - centre) / radius, 1.0);
	}

	fNormalMatrix = LelNormalMatrix();
#ifdef LEL_GEOMETRY_PREPASS
	mat4 moved = model - LelPreviousModelMatrix();
	fMotionX = vec4(moved[0].x, moved[1].x, moved[2].x, moved[3].x);
	fMotionY = vec4(moved[0].y, moved[1].y, moved[2].y, moved[3].y);
	fMotionZ = vec4(moved[0].z, moved[1].z, moved[2].z, moved[3].z);
#endif
	LelVertexSetup();
}

/////Vertex

//Fragment

#version 430

#if !defined(LEL_SHADOW_CASTER) && !defined(LEL_GEOMETRY_PREPASS)
#define LEL_IMPOSTOR_LIT 1
#include "Engine/Shadows.glsl"
#include "Engine/VoxelConeTracing.glsl"
#include "Engine/StandardBRDF.glsl"
#include "Engine/Lights.glsl"
#include "Engine/DirectionalLights.glsl"
#endif
#include "Engine/Instancing.glsl"
#include "Engine/Impostor.glsl"

// The surface is behind the billboard along every ray (see the vertex stage): the depth test can still reject a
// fragment early against the billboard's own depth.
layout(depth_greater) out float gl_FragDepth;

uniform mat4 projectionMatrix;
#ifndef LEL_IMPOSTOR_LIT
uniform mat4 viewMatrix;
#endif
uniform vec4 ImpostorSphere;
uniform vec4 ImpostorFrames;
uniform float Cutoff;
uniform float Parallax;
uniform float ParallaxSteps;
uniform float Agreement;

// rg: the normal's x and y in its frame's axes (z towards the frame's camera, rebuilt), b: the depth along the frame's
// direction (0.5 at the sphere's centre, 1 a radius towards the camera), a: the coverage as a distance field (the
// outline at 0.5), which blends between views into a shape between theirs.
uniform sampler2D ShapeMap;
// rgb: the colour the surface scatters, a: its smoothness (with a specular atlas) or the occlusion of its indirect light.
uniform sampler2D BaseMap;
#ifdef SPECULAR_MAP
// rgb: the specular colour, a: the occlusion of the indirect light.
uniform sampler2D SpecularMap;
#endif
#ifdef EMISSION_MAP
uniform sampler2D EmissionMap;
#endif

in vec3 fPosition;
in vec3 fWorld;
flat in vec4 fEye;
flat in vec4 fWorldEye;
flat in mat3 fNormalMatrix;

// What the three views say of the surface on this pixel's ray.
struct ImpostorSurface
{
	float alpha;    // coverage: drawn from Cutoff up
	float travel;   // from the billboard to the surface, in lengths of the ray vector (never negative)
	vec3 normal;    // object space
	vec3 weights;   // of the three views for the surface's values: the frames' weights by how sure each is of a surface
	vec2 frameA;    // the three views (their column and row in the grid)
	vec2 frameB;
	vec2 frameC;
	vec2 uvA;       // where each view is read (inside its frame, 0..1)
	vec2 uvB;
	vec2 uvC;
	float lod;      // the atlas level read
	float margin;   // what stays of a frame's border at that level
};

// A frame of an atlas at a place inside it. The lookup stays half a texel of the level read inside the frame: the
// filter never reaches the neighbouring view.
vec4 ImpostorFetch(sampler2D map, vec2 frame, vec2 uv, float lod, float margin)
{
	return textureLod(map, (frame + clamp(uv, vec2(margin), vec2(1.0 - margin))) / ImpostorFrames.x, lod);
}

// One view: where the ray meets the surface that view shows, and how well the view knows (agreement: 1 when the ray
// is on the view's surface where it is read, towards 0 when the surface there is in front of the ray or behind it).
void ImpostorView(vec2 frame, vec3 origin, vec3 ray, float lod, float margin, out vec2 uv, out float coverage, out float travel, out vec3 normal, out float agreement)
{
	vec3 direction = ImpostorFrameDirection(frame, ImpostorFrames.x, ImpostorFrames.z > 0.5);
	vec3 right;
	vec3 up;
	ImpostorFrameAxes(direction, right, up);

	// The ray on the view's plane (through the sphere's centre, facing the view's camera). A view seen from the side
	// or from behind has nothing to say of this ray: its lookup lands outside the frame.
	float along = min(dot(ray, direction), -1e-4 * length(ray));
	float toPlane = -dot(origin, direction) / along;
	vec3 onPlane = origin + ray * toPlane;
	uv = vec2(dot(onPlane, right), dot(onPlane, up)) * 0.5 + 0.5;

	// Parallax: the surface stands off the plane by the depth stored there, so the ray meets it earlier or later than
	// the plane; the lookup moves to where the ray is at that height. Each further step reads the depth where the last
	// one landed and moves again: on a surface that does not turn away from the view too steeply the lookups close in
	// on the point the ray really meets.
	int steps = clamp(int(ParallaxSteps + 0.5), 1, 4);
	float height = 0.0;
	for (int i = 0; i < steps; i++)
	{
		height = (ImpostorFetch(ShapeMap, frame, uv, lod, margin).b * 2.0 - 1.0) * Parallax;
		vec3 shifted = onPlane + ray * (height / along);
		uv = vec2(dot(shifted, right), dot(shifted, up)) * 0.5 + 0.5;
	}

	vec4 shape = ImpostorFetch(ShapeMap, frame, uv, lod, margin);
	bool inside = all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
	coverage = inside ? shape.a : 0.0;

	// The ray is at `height` where the view is read; the view's surface there is at its own stored height. Where the
	// two differ, the view sees another surface at this place than the one on the ray (something in front of it, or a
	// gap the ray goes through that this view cannot see into): what it says counts for less.
	float apart = ((shape.b * 2.0 - 1.0) * Parallax - height) * Agreement;
	agreement = 1.0 / (1.0 + apart * apart);
	travel = toPlane + (shape.b * 2.0 - 1.0) * Parallax / along;
	vec2 n = shape.rg * 2.0 - 1.0;
	normal = right * n.x + up * n.y + direction * sqrt(max(1.0 - dot(n, n), 0.0));
}

ImpostorSurface ImpostorTrace(vec3 ray)
{
	ImpostorSurface surface;

	// Texels of a frame under a pixel (a frame spans two units of the impostor's space): the atlas level, at most the
	// one where a frame is still four texels wide.
	float texels = max(length(dFdx(fPosition)), length(dFdy(fPosition))) * ImpostorFrames.y * 0.5;
	surface.lod = clamp(log2(max(texels, 1e-6)), 0.0, max(log2(ImpostorFrames.y) - 2.0, 0.0));
	surface.margin = 0.5 * exp2(ceil(surface.lod)) / ImpostorFrames.y;

	// The three captured views nearest to where this pixel's ray comes from (the views are orthographic: the nearer a
	// view's direction to the ray's, the less the two disagree about what the ray meets). Chosen per pixel, so a
	// perspective view of a near impostor reads each part of it from the views that saw it that way; the weights are
	// continuous, so neighbouring pixels on different views meet without a seam.
	bool hemisphere = ImpostorFrames.z > 0.5;
	vec3 from = -normalize(ray);
	vec2 e = hemisphere ? ImpostorHemiOctEncode(from) : ImpostorOctEncode(from);
	vec3 weights;
	ImpostorNearestFrames(e, ImpostorFrames.x, hemisphere, surface.frameA, surface.frameB, surface.frameC, weights);

	vec3 coverage;
	vec3 travel;
	vec3 agreement;
	vec3 normalA;
	vec3 normalB;
	vec3 normalC;
	ImpostorView(surface.frameA, fPosition, ray, surface.lod, surface.margin, surface.uvA, coverage.x, travel.x, normalA, agreement.x);
	ImpostorView(surface.frameB, fPosition, ray, surface.lod, surface.margin, surface.uvB, coverage.y, travel.y, normalB, agreement.y);
	ImpostorView(surface.frameC, fPosition, ray, surface.lod, surface.margin, surface.uvC, coverage.z, travel.z, normalC, agreement.z);

	// The views that know this ray decide, the others follow (a little of each stays: weights that reach zero where a
	// view leaves the three keep doing so).
	weights *= agreement + 0.02;
	weights /= weights.x + weights.y + weights.z;
	surface.alpha = dot(weights, coverage);

	// A view that sees no surface here says nothing of its depth, normal or colour. The surface is inside the sphere:
	// no further along the ray than the sphere is wide (and a little, for the billboard in front of it).
	vec3 sure = weights * coverage;
	surface.weights = sure / max(sure.x + sure.y + sure.z, 1e-6);
	travel = clamp(travel, vec3(0.0), vec3(2.5 / max(length(ray), 1e-12)));
	surface.travel = dot(surface.weights, travel);
	vec3 normal = normalA * surface.weights.x + normalB * surface.weights.y + normalC * surface.weights.z;
	surface.normal = normal * inversesqrt(max(dot(normal, normal), 1e-12));
	return surface;
}

// An atlas blended over the three views.
vec4 ImpostorBlend(sampler2D map, ImpostorSurface surface)
{
	return ImpostorFetch(map, surface.frameA, surface.uvA, surface.lod, surface.margin) * surface.weights.x
		+ ImpostorFetch(map, surface.frameB, surface.uvB, surface.lod, surface.margin) * surface.weights.y
		+ ImpostorFetch(map, surface.frameC, surface.uvC, surface.lod, surface.margin) * surface.weights.z;
}

// The depth buffer's value of a world position.
float ImpostorDepth(vec3 world)
{
	vec4 clip = projectionMatrix * viewMatrix * vec4(world, 1.0);
	return clip.z / clip.w * 0.5 + 0.5;
}

#if defined(LEL_SHADOW_CASTER)

uniform float ShadowBias;

void main()
{
	vec3 ray = fEye.w > 0.5 ? fPosition - fEye.xyz : fEye.xyz;
	vec3 worldRay = fWorldEye.w > 0.5 ? fWorld - fWorldEye.xyz : fWorldEye.xyz;
	ImpostorSurface surface = ImpostorTrace(ray);
	if (surface.alpha < Cutoff)
	{
		discard;
	}

	// ShadowBias radii further from the light.
	float travel = surface.travel + ShadowBias / max(length(ray), 1e-12);
	gl_FragDepth = max(ImpostorDepth(fWorld + worldRay * travel), gl_FragCoord.z);
}

#elif defined(LEL_GEOMETRY_PREPASS)

flat in vec4 fMotionX;
flat in vec4 fMotionY;
flat in vec4 fMotionZ;

// What Engine/GBuffer.shader writes for a mesh.
layout(location = 0) out vec4 NormalRoughness;
layout(location = 1) out vec4 Motion;

void main()
{
	vec3 ray = fEye.w > 0.5 ? fPosition - fEye.xyz : fEye.xyz;
	vec3 worldRay = fWorldEye.w > 0.5 ? fWorld - fWorldEye.xyz : fWorldEye.xyz;
	ImpostorSurface surface = ImpostorTrace(ray);
	if (surface.alpha < Cutoff)
	{
		discard;
	}

	gl_FragDepth = max(ImpostorDepth(fWorld + worldRay * surface.travel), gl_FragCoord.z);

	float roughness = LelRoughness();
#ifdef SPECULAR_MAP
	// The surface's own smoothness, kept above 0 (which reads as unset) as the lit shaders do.
	roughness = max(1.0 - ImpostorBlend(BaseMap, surface).a, 0.03);
#endif
	NormalRoughness = vec4(normalize(fNormalMatrix * surface.normal), roughness);

	// The surface point moves with the object: its place in the object times the change of the world matrix.
	vec4 object = vec4(ImpostorSphere.xyz + (fPosition + ray * surface.travel) * ImpostorSphere.w, 1.0);
	vec3 motion = vec3(dot(fMotionX, object), dot(fMotionY, object), dot(fMotionZ, object));
	Motion = vec4(motion, dot(motion, motion) > 1e-12 ? 1.0 : 0.0);
}

#else

struct Ambient {
	vec4 ambColor;
	float ambStrength;
};

struct Specular {
	float specStrength;
	float specShine;
	vec3 viewPos;
};

uniform Ambient LAmbient;
uniform Specular LSpecular;

out vec4 FragColor;

void main()
{
	vec3 ray = fEye.w > 0.5 ? fPosition - fEye.xyz : fEye.xyz;
	vec3 worldRay = fWorldEye.w > 0.5 ? fWorld - fWorldEye.xyz : fWorldEye.xyz;
	ImpostorSurface surface = ImpostorTrace(ray);
	// A little more than the geometry prepass keeps: every pixel it wrote depth for is shaded, whatever the rounding
	// of the two programs.
	if (surface.alpha < Cutoff - 0.01)
	{
		discard;
	}

	// The depth the prepass wrote, a few steps of the depth buffer nearer (the test after a prepass is "not behind").
	vec3 position = fWorld + worldRay * surface.travel;
	float depth = ImpostorDepth(position);
	gl_FragDepth = max(depth - depth * 3e-7, gl_FragCoord.z);

	LelMaterial material = LelMaterialData();
	vec4 base = ImpostorBlend(BaseMap, surface);
	vec3 albedo = base.rgb * material.color.rgb;
	vec3 emission = vec3(0.0);
#ifdef EMISSION_MAP
	emission = ImpostorBlend(EmissionMap, surface).rgb * material.emissive.rgb * material.emissive.a;
#endif

	vec3 N = normalize(fNormalMatrix * surface.normal);
	vec3 V = normalize(LSpecular.viewPos - position);

#ifdef SPECULAR_MAP
	// Lit as the physically based workflows of Engine/LitTextured.shader: the bake stored what they shade with.
	vec4 specular = ImpostorBlend(SpecularMap, surface);
	vec3 specColor = specular.rgb;
	float roughness = max(1.0 - base.a, 0.03);
	float surfaceOcclusion = specular.a;
	float oneMinusReflectivity = 1.0 - max(max(specColor.r, specColor.g), specColor.b);
#else
	float roughness = material.params.x > 0.0 ? material.params.x : 0.6;
	float surfaceOcclusion = base.a;
#endif

	vec3 indirectDiffuse;
	float occlusion;
	vec3 indirectSpecular;
	GetIndirectLighting(position, N, V, roughness, indirectDiffuse, occlusion, indirectSpecular);

#ifdef SPECULAR_MAP
	LelIndirect gi;
	gi.diffuse = (LAmbient.ambColor.rgb * LAmbient.ambStrength * occlusion + indirectDiffuse) * surfaceOcclusion;
	gi.specular = indirectSpecular * surfaceOcclusion;
	vec3 result = LelStandardBRDFIndirect(albedo, specColor, oneMinusReflectivity, roughness, N, V, gi);
#ifdef MIXED_WORKFLOW
	// A surface the bake stored no specular colour for is one lit with the scene's highlight (Engine/Lit.shader, the
	// scene workflow of Engine/LitTextured.shader): it keeps that lighting among the physically based surfaces.
	float sceneLit = 1.0 - smoothstep(0.004, 0.02, max(max(specColor.r, specColor.g), specColor.b));
	result = mix(result, albedo * gi.diffuse + gi.specular * LSpecular.specStrength, sceneLit);
#endif
	result += emission;

	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		float ndl = dot(N, L);
		if (ndl > 0.0)
		{
			vec3 lightColor = LelDirectionalRadiance(i) * SampleLightShadowRGB(i, position, N, L);
			vec3 lit = LelStandardBRDFDirect(albedo, specColor, roughness, N, V, L, lightColor);
#ifdef MIXED_WORKFLOW
			float spec = pow(max(dot(N, normalize(L + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			lit = mix(lit, (albedo + spec) * lightColor * ndl, sceneLit);
#endif
			result += lit;
		}
	}

	for (int i = 0; i < LelLightCount(); i++)
	{
		vec3 Li;
		vec3 radiance;
		if (LelLightAt(i, position, Li, radiance))
		{
			vec3 lit = LelStandardBRDFDirect(albedo, specColor, roughness, N, V, Li, radiance);
#ifdef MIXED_WORKFLOW
			float specularI = pow(max(dot(N, normalize(Li + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			lit = mix(lit, (albedo + specularI) * radiance * max(dot(N, Li), 0.0), sceneLit);
#endif
			result += lit;
		}
	}
#else
	// The scene's highlight (Environment Specular Strength / Shine), as Engine/Lit.shader.
	vec3 ambient = LAmbient.ambColor.rgb * LAmbient.ambStrength * albedo;
	vec3 result = (ambient * occlusion + indirectDiffuse * albedo + indirectSpecular * LSpecular.specStrength) * surfaceOcclusion + emission;

	for (int i = 0; i < LelDirectionalCount(); i++)
	{
		vec3 L = LelDirectionalTowards(i);
		float ndl = dot(N, L);
		if (ndl > 0.0)
		{
			vec3 lightColor = LelDirectionalRadiance(i) * SampleLightShadowRGB(i, position, N, L);
			float spec = pow(max(dot(N, normalize(L + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + spec) * lightColor * ndl;
		}
	}

	for (int i = 0; i < LelLightCount(); i++)
	{
		vec3 Li;
		vec3 radiance;
		if (LelLightAt(i, position, Li, radiance))
		{
			float specularI = pow(max(dot(N, normalize(Li + V)), 0.0), LSpecular.specShine) * LSpecular.specStrength;
			result += (albedo + specularI) * radiance * max(dot(N, Li), 0.0);
		}
	}
#endif

	FragColor = vec4(result, 1.0);
}

#endif

/////Fragment
