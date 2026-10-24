// The scene behind a transparent object, for shaders that declare GrabPass in their Properties block (Unity's GrabPass;
// LELEngine.Rendering.SceneGrab): right before such an object draws, the transparent pass copies what it has drawn so far
// (the opaques, the sky and the transparent objects before it) into lelGrabTexture: rgb the HDR color, a the linear eye
// depth of the opaque surface there. Only the part of the screen the object reads is copied (lelGrabRect).
//
// LelSceneRefraction(bend, strength) is the scene behind the fragment seen through a surface tilted by bend: never what
// lies in front of the fragment, even though the copy holds it.

#ifndef LEL_GRAB_PASS_GLSL
#define LEL_GRAB_PASS_GLSL

uniform sampler2D lelGrabTexture;
uniform vec4 lelGrabRect;   // the part of the grab this draw may read: uv min (xy), uv max (zw), half a texel inside
uniform vec4 lelGrabScreen; // width, height, 1 / width, 1 / height
uniform vec4 lelGrabDepth;  // linear eye depth = (z * x + y) / (z * z + w), z the NDC depth (2 * window depth - 1)
uniform vec3 lelGrabAxisX;  // camera right * projection[0][0] / 2: a world offset moves dot(offset, axis) / w in uv
uniform vec3 lelGrabAxisY;  // camera up * projection[1][1] / 2

// Linear eye depth (world units in front of the camera) of a window depth: gl_FragCoord.z or a depth buffer value.
float LelGrabEyeDepth(float windowDepth)
{
	float z = windowDepth * 2.0 - 1.0;
	return (z * lelGrabDepth.x + lelGrabDepth.y) / (z * lelGrabDepth.z + lelGrabDepth.w);
}

// The fragment's own spot in the grab (screen uv).
vec2 LelGrabUV()
{
	return gl_FragCoord.xy * lelGrabScreen.zw;
}

// The grab at a screen uv (rgb color, a linear eye depth), kept inside the part copied for this draw.
vec4 LelSampleGrab(vec2 uv)
{
	return texture(lelGrabTexture, clamp(uv, lelGrabRect.xy, lelGrabRect.zw));
}

// The scene behind the fragment refracted by a surface tilted along bend: a world-space vector across the surface, a
// normal map's tilt (tangent * n.x + bitangent * n.y), length up to 1. The lookup moves against the tilt, as light
// entering glass bends (a convex bump magnifies what is behind it), by strength world units at the fragment's distance:
// the shift keeps its size in the world at any distance and resolution. Where the moved lookup finds a surface in front of
// the fragment (an object between the camera and this one, which the copy holds too), or one closer behind it than the
// shift, the shift shrinks to that gap: nothing in front is refracted, and no seam shows where the surface meets the
// geometry behind it.
vec3 LelSceneRefraction(vec3 bend, float strength)
{
	vec2 uv = LelGrabUV();
	vec2 offset = -vec2(dot(bend, lelGrabAxisX), dot(bend, lelGrabAxisY)) * (strength * gl_FragCoord.w);
	vec4 behind = LelSampleGrab(uv + offset);
	float keep = clamp((behind.a - LelGrabEyeDepth(gl_FragCoord.z)) / max(strength, 1e-4), 0.0, 1.0);
	if (keep < 1.0)
	{
		behind = LelSampleGrab(uv + offset * keep);
	}

	return behind.rgb;
}

#endif
