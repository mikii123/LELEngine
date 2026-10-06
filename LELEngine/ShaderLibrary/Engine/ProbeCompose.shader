//Vertex

#version 430

out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

#include "Engine/ScreenProbes.glsl"

// Turns the per-probe ray results back into the octahedral radiance map and applies Lumen's temporal probe
// filter: directions traced this frame (2x2 rays when importance sampled) are blended with the reprojected
// previous radiance of the same direction; directions skipped by importance sampling keep that history.
//
// The history is the previous frame's *unfiltered* composed radiance. Feeding the spatially filtered map
// back would filter the carried-over directions again every frame, blurring lighting along surfaces without
// bound and leaking bright sky samples across whole walls.
out vec4 OutRadiance;

uniform sampler2D RayResults;
uniform sampler2D PreviousProbeRadiance;
uniform usampler2D ProbeSelection;
uniform vec3 giSkyRadiance;
uniform float probeHistoryWeight; // weight of the reprojected history for traced directions, 0 = off

void main()
{
	ivec2 texel = ivec2(gl_FragCoord.xy);
	ivec2 probe = texel / PROBE_RESOLUTION;
	ivec2 oct = texel - probe * PROBE_RESOLUTION;
	int octIndex = oct.y * PROBE_RESOLUTION + oct.x;

	uvec4 selection = texelFetch(ProbeSelection, probe, 0);

	vec4 history = vec4(0.0, 0.0, 0.0, -2.0);
	if (ReprojectionValid(selection))
	{
		ivec2 previous = SelectionPreviousProbe(selection);
		history = texelFetch(PreviousProbeRadiance, previous * PROBE_RESOLUTION + oct, 0);
	}

	vec4 fresh = vec4(0.0, 0.0, 0.0, -2.0);
	bool traced = false;
	if (!SelectionValid(selection))
	{
		fresh = texelFetch(RayResults, texel, 0);
		traced = true;
	}
	else
	{
		ivec2 rayBase = probe * PROBE_RESOLUTION;
		for (int slot = 0; slot < IMPORTANT_DIRECTIONS; slot++)
		{
			if (SelectedTexelIndex(selection, slot) != octIndex) continue;

			vec3 sum = vec3(0.0);
			float hitSum = 0.0;
			int hits = 0;
			for (int s = 0; s < 4; s++)
			{
				int ray = slot * 4 + s;
				vec4 r = texelFetch(RayResults, rayBase + ivec2(ray % PROBE_RESOLUTION, ray / PROBE_RESOLUTION), 0);
				sum += r.rgb;
				if (r.a >= 0.0)
				{
					hitSum += r.a;
					hits++;
				}
			}
			fresh = vec4(sum * 0.25, hits > 0 ? hitSum / float(hits) : -1.0);
			traced = true;
			break;
		}
	}

	if (!traced)
	{
		// Not traced this frame: history, or sky when there is none.
		OutRadiance = history.a < -1.5 ? vec4(giSkyRadiance, -1.0) : history;
		return;
	}

	if (fresh.a > -1.5 && history.a > -1.5 && probeHistoryWeight > 0.0)
	{
		fresh.rgb = mix(fresh.rgb, history.rgb, probeHistoryWeight);
	}
	OutRadiance = fresh;
}

/////Fragment
