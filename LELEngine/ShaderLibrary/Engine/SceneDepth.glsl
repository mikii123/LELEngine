// The opaque scene's depth for transparent shaders that declare SceneDepth On in their Properties block (Unity's
// _CameraDepthTexture; LELEngine.Rendering.SceneDepthTexture): the transparent pass copies the depth buffer once the
// opaques are drawn (with the opaque surfaces it draws itself at GeometryLast + 1: alpha clip, vertex functions) and
// binds the copy to such draws. Soft particles fade out where they come close to what lies behind.

#ifndef LEL_SCENE_DEPTH_GLSL
#define LEL_SCENE_DEPTH_GLSL

uniform sampler2D lelSceneDepth;
uniform vec4 lelSceneDepthParams;     // linear eye depth = (z * x + y) / (z * z + w), z the NDC depth (2 * window depth - 1)
uniform float lelSceneDepthAvailable; // 1 when the copy is bound; 0 when there is none (nothing fades)
uniform float lelSceneDepthPixelScale; // scene pixels per pixel drawn: 2 in the half-resolution layer (LowResolution On), else 1

// Linear eye depth (world units in front of the camera) of a window depth: gl_FragCoord.z or a depth buffer value.
float LelEyeDepth(float windowDepth)
{
	float z = windowDepth * 2.0 - 1.0;
	return (z * lelSceneDepthParams.x + lelSceneDepthParams.y) / (z * lelSceneDepthParams.z + lelSceneDepthParams.w);
}

// Linear eye depth of the opaque surface behind the fragment (drawn into the half-resolution layer: one of the four scene
// pixels under it).
float LelSceneEyeDepth()
{
	ivec2 texel = ivec2(gl_FragCoord.xy * max(lelSceneDepthPixelScale, 1.0));
	return LelEyeDepth(texelFetch(lelSceneDepth, min(texel, textureSize(lelSceneDepth, 0) - 1), 0).r);
}

// Unity's soft particle fade: 0 where the opaque surface behind the fragment is nearer than near, 1 from far on, linear
// between (Unity's legacy particle shaders: near 0, far 1 / InvFade). 1 when both are 0 or there is no depth copy.
float LelSoftParticleFade(float near, float far)
{
	if (lelSceneDepthAvailable < 0.5 || (near <= 0.0 && far <= 0.0))
	{
		return 1.0;
	}

	float gap = LelSceneEyeDepth() - LelEyeDepth(gl_FragCoord.z);
	return clamp((gap - near) / max(far - near, 1e-4), 0.0, 1.0);
}

#endif
