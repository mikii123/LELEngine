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

// The impostor bake (LELEngine.Rendering.Impostors.ImpostorBaker): one view's capture, drawn at several samples a
// texel, filtered down into its frame of the atlases. Only the samples that hit the object count: the frame's texels
// hold the surface's values, not a mix with the empty background, and how much of the texel it covers.

uniform sampler2D CaptureDiffuse;   // rgb: the colour the surface scatters
uniform sampler2D CaptureNormal;    // xyz: the normal, in the object's space
uniform sampler2D CaptureSpecular;  // rgb: the specular colour, a: smoothness
uniform sampler2D CaptureEmission;  // rgb: the emitted radiance, a: the occlusion of the indirect light
uniform sampler2D CaptureDepth;     // the view's depth buffer (orthographic: linear from the plane in front of the sphere)

uniform int samples;                // capture samples along a texel's side
uniform ivec2 frameOrigin;          // the frame's first texel in the atlas
uniform vec3 frameRight;            // the view's axes in the object's space
uniform vec3 frameUp;
uniform vec3 frameDirection;

layout(location = 0) out vec4 OutBase;      // rgb: the colour (sRGB encoded), a: coverage
layout(location = 1) out vec4 OutShape;     // rg: the normal's x and y in the view's axes, b: depth, a: occlusion
layout(location = 2) out vec4 OutSpecular;  // rgb: the specular colour (sRGB encoded), a: smoothness
layout(location = 3) out vec4 OutEmission;  // rgb: the emitted radiance (linear)

vec3 EncodeSrgb(vec3 linear)
{
	linear = clamp(linear, 0.0, 1.0);
	return mix(linear * 12.92, 1.055 * pow(linear, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), linear));
}

void main()
{
	ivec2 first = (ivec2(gl_FragCoord.xy) - frameOrigin) * samples;
	vec3 diffuse = vec3(0.0);
	vec3 normal = vec3(0.0);
	vec4 specular = vec4(0.0);
	vec4 emission = vec4(0.0);
	float depth = 0.0;
	float covered = 0.0;
	for (int y = 0; y < samples; y++)
	{
		for (int x = 0; x < samples; x++)
		{
			ivec2 texel = first + ivec2(x, y);
			float z = texelFetch(CaptureDepth, texel, 0).r;
			if (z >= 1.0)
			{
				continue;
			}

			diffuse += texelFetch(CaptureDiffuse, texel, 0).rgb;
			normal += texelFetch(CaptureNormal, texel, 0).xyz;
			specular += texelFetch(CaptureSpecular, texel, 0);
			emission += texelFetch(CaptureEmission, texel, 0);
			depth += z;
			covered += 1.0;
		}
	}

	if (covered == 0.0)
	{
		OutBase = vec4(0.0);
		OutShape = vec4(0.5, 0.5, 0.5, 1.0);
		OutSpecular = vec4(0.0);
		OutEmission = vec4(0.0);
		return;
	}

	float scale = 1.0 / covered;

	// The normal in the view's axes; z (towards the view's camera) is rebuilt when the impostor draws, so a normal that
	// faces away (the back of a two-sided surface) is stored as its mirror image facing the camera.
	normal = dot(normal, normal) > 1e-12 ? normalize(normal) : frameDirection;
	vec2 n = vec2(dot(normal, frameRight), dot(normal, frameUp));

	// The depth buffer runs from the plane a radius in front of the centre (0) to the one a radius behind (1); stored
	// the other way round: how far towards the camera.
	OutBase = vec4(EncodeSrgb(diffuse * scale), covered / float(samples * samples));
	OutShape = vec4(n * 0.5 + 0.5, 1.0 - depth * scale, clamp(emission.a * scale, 0.0, 1.0));
	OutSpecular = vec4(EncodeSrgb(specular.rgb * scale), clamp(specular.a * scale, 0.0, 1.0));
	OutEmission = vec4(max(emission.rgb * scale, vec3(0.0)), 1.0);
}

/////Fragment
