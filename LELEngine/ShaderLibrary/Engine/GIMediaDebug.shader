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

#include "Engine/Sky.glsl"
#include "Engine/GIMedia.glsl"

// Debug view of the volumetric objects in the GI (VolumetricMediaDebugPass): what a ray of the GI from the camera sees
// of the clouds, over the GI's sky. It should look like the clouds as they are drawn, blurred to the media volume's
// voxels: as bright from every side (the phase), as opaque.
in vec2 fUV;
out vec4 FragColor;

uniform mat4 invViewProjection;
uniform vec3 cameraPosition;
uniform int debugStretches;
uniform float debugAperture;   // of the cone each pixel stands for in the coarse part (tan of its half angle)

void main()
{
	vec4 farPoint = invViewProjection * vec4(fUV * 2.0 - 1.0, 1.0, 1.0);
	vec3 direction = normalize(farPoint.xyz / farPoint.w - cameraPosition);
	vec3 color = SkyRadianceLod(direction, 0.0);
	if (giMediaActive != 0)
	{
		// Steps of a voxel through the part that holds clouds, read at the finest level: the volume itself.
		vec4 seen = vec4(0.0);
		float phase = GIMediaNoPhase;
		vec3 safe = mix(direction, vec3(1e-12), lessThan(abs(direction), vec3(1e-12)));
		vec3 t0 = (giMediaUsed - cameraPosition) / safe;
		vec3 t1 = (giMediaUsed + giMediaExtent - cameraPosition) / safe;
		vec3 tNear = min(t0, t1);
		vec3 tFar = max(t0, t1);
		float from = max(max(max(tNear.x, tNear.y), tNear.z), 0.0);
		float to = min(min(tFar.x, tFar.y), tFar.z);
		float fine = from;
		for (int i = 0; i < debugStretches * 16 && fine < to && seen.a < 0.99; i++)
		{
			vec4 m = GIMediaSegment(cameraPosition + direction * fine, fine, direction, 0.0, giMediaVoxel, 0.5, phase);
			seen.rgb += (1.0 - seen.a) * m.rgb;
			seen.a += (1.0 - seen.a) * m.a;
			fine += giMediaVoxel;
		}

		// The rest of the way (all of it with no fine steps) as a narrow cone of the GI reads it: stretches that grow
		// with the distance, their lengths jittered per pixel as the GI's passes jitter their cones (noise here; the
		// passes filter it).
		float jitter = fract(52.9829189 * fract(dot(gl_FragCoord.xy, vec2(0.06711056, 0.00583715))));
		vec4 rest = GIMediaRange(cameraPosition, direction, debugAperture, debugStretches > 0 ? fine : 0.0, 1e30, jitter, phase);
		seen.rgb += (1.0 - seen.a) * rest.rgb;
		seen.a += (1.0 - seen.a) * rest.a;
		color = seen.rgb + (1.0 - seen.a) * color;
	}

	FragColor = vec4(color, 1.0);
}

/////Fragment
