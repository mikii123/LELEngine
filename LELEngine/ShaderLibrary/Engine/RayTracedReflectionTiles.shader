//Compute

#version 430

// Traced reflections (Rendering.Passes.RayTracedReflectionPass), step 0: which 8 x 8 tiles of the resolve have a pixel
// smooth enough to trace (roughness below maxRoughness, a surface: not the sky). One work group per tile: every tile's
// flag is written (TileFlags, read by the next steps and by the next frame's history lookups), and the tiles to trace are
// listed behind an indirect dispatch's arguments (x counts them; the pass sets it to 0 and y, z to 1 before), so the trace
// and its filter run only where something is glossy.
layout(local_size_x = 8, local_size_y = 8) in;

layout(std430, binding = 0) buffer TileList
{
	uint numGroupsX;
	uint numGroupsY;
	uint numGroupsZ;
	uint padding;
	uint tiles[];
};

layout(binding = 0, r32ui) uniform writeonly uimage2D TileFlags;

uniform sampler2D NormalRoughness;
uniform sampler2D SceneDepth;
uniform ivec2 screenSize;   // the scene's pixels (normals, depth)
uniform ivec2 outputSize;   // the resolve's
uniform float maxRoughness;

shared uint glossy;

void main()
{
	if (gl_LocalInvocationIndex == 0u)
	{
		glossy = 0u;
	}

	barrier();
	ivec2 texel = ivec2(gl_GlobalInvocationID.xy);
	if (all(lessThan(texel, outputSize)))
	{
		ivec2 pixel = min(ivec2((vec2(texel) + 0.5) * vec2(screenSize) / vec2(outputSize)), screenSize - 1);
		float roughness = texelFetch(NormalRoughness, pixel, 0).w;
		roughness = roughness > 0.0 ? roughness : 0.6;
		if (roughness < maxRoughness && texelFetch(SceneDepth, pixel, 0).r < 1.0)
		{
			atomicOr(glossy, 1u);
		}
	}

	barrier();
	if (gl_LocalInvocationIndex == 0u)
	{
		uvec2 tile = gl_WorkGroupID.xy;
		imageStore(TileFlags, ivec2(tile), uvec4(glossy));
		if (glossy != 0u)
		{
			tiles[atomicAdd(numGroupsX, 1u)] = tile.x | (tile.y << 16);
		}
	}
}

/////Compute
