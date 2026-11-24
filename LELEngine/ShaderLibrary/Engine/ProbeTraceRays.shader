//Compute

#version 430

// One thread per texel of the ray atlas. Declared before the includes.
layout(local_size_x = 8, local_size_y = 8) in;

// Screen probe ray tracing (Lumen final gather, step 1) through the scene's triangles (the LEL_RAY_QUERY variant; the
// other is an empty stand-in, never dispatched): one ray per thread (Engine/ProbeTraceCore.glsl), written into the ray
// atlas. A compute shader, not the fragments of Engine/ProbeTrace.shader: Intel's Windows driver (Arc iGPU of the Core
// Ultra 7 155H, 101.8132) lost the device a frame or two after a full-atlas draw whose fragments traced ray queries,
// while the same queries from compute shaders run there (the traced shadows, voxel cone tracing's rays, Lumen's
// radiosity and radiance cache).
#ifdef LEL_RAY_QUERY
#include "Engine/ProbeTraceCore.glsl"

uniform ivec2 rayAtlasSize;

layout(binding = 0, rgba16f) uniform writeonly image2D OutRays;

void main()
{
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(texel, rayAtlasSize)))
	{
		return;
	}

	imageStore(OutRays, texel, TraceProbeTexel(texel));
}
#else
void main()
{
}
#endif

/////Compute
