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

// Super sampling's filter down to the window (LELEngine.Rendering.SuperSamplingSettings, PostProcessPass), separable in
// two passes, in linear light after the tonemap: a sub-pixel highlight counts at most as a white sample, so edges
// against bright light are anti-aliased as well as any other (averaged in HDR, one hot sample would light the whole
// pixel), and no full-size tonemapped copy of the scene is written.
//   SS_HORIZONTAL  the scene (HDR, super sampled): exposure and the tonemap per sample, filtered along x down to the
//                  window's width; rows stay the scene's (an RGBA16F target).
//   default        that, filtered along y down to the window's height, then the sRGB transfer and the output's dither.
// The filter (ssFilter) over the window pixel's footprint, distances in window pixels: 0 Gaussian (sigma ssSigma), 1 the
// exact area average (each source texel weighted by its overlap with the footprint), 2 Lanczos-2.

uniform sampler2D ssSource;
uniform float ssRatio;      // source pixels per window pixel along this pass's axis (above 1)
uniform int ssFilter;
uniform float ssSigma;      // the Gaussian's, in window pixels
#ifdef SS_HORIZONTAL
uniform float Exposure;
uniform int TonemapEnabled;
#endif

out vec4 FragColor;

#ifdef SS_HORIZONTAL
vec3 ACESFilm(vec3 x)
{
	const float a = 2.51;
	const float b = 0.03;
	const float c = 2.43;
	const float d = 0.59;
	const float e = 0.14;
	return clamp((x * (a * x + b)) / (x * (c * x + d) + e), 0.0, 1.0);
}

vec3 Tonemap(vec3 hdr)
{
	hdr *= Exposure;
	return TonemapEnabled != 0 ? ACESFilm(hdr) : clamp(hdr, 0.0, 1.0);
}
#else
float Dither(vec2 pixel)
{
	return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

vec3 LinearToSrgb(vec3 c)
{
	return mix(c * 12.92, 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055, step(vec3(0.0031308), c));
}
#endif

float Sinc(float x)
{
	float px = 3.14159265 * x;
	return abs(x) < 1e-4 ? 1.0 : sin(px) / px;
}

void main()
{
#ifdef SS_HORIZONTAL
	int i = int(gl_FragCoord.x);
	int line = int(gl_FragCoord.y);
	int size = textureSize(ssSource, 0).x;
#else
	int i = int(gl_FragCoord.y);
	int line = int(gl_FragCoord.x);
	int size = textureSize(ssSource, 0).y;
#endif
	// The window pixel's footprint in source pixels, its centre, and how far the filter reaches (window pixels).
	float start = float(i) * ssRatio;
	float end = start + ssRatio;
	float centre = (float(i) + 0.5) * ssRatio;
	float reach = ssFilter == 0 ? 3.0 * ssSigma : (ssFilter == 2 ? 2.0 : 0.5);
	int first = int(floor(centre - reach * ssRatio));
	int last = int(ceil(centre + reach * ssRatio)) - 1;
	last = min(last, first + 31);

	vec3 sum = vec3(0.0);
	float weights = 0.0;
	for (int j = first; j <= last; j++)
	{
		float w;
		if (ssFilter == 1)
		{
			w = max(min(float(j + 1), end) - max(float(j), start), 0.0);
		}
		else
		{
			float d = (float(j) + 0.5 - centre) / ssRatio;
			w = ssFilter == 0 ? exp(-0.5 * d * d / (ssSigma * ssSigma)) : (abs(d) < 2.0 ? Sinc(d) * Sinc(d * 0.5) : 0.0);
		}

		if (w == 0.0)
		{
			continue;
		}

		int k = clamp(j, 0, size - 1);
#ifdef SS_HORIZONTAL
		vec3 value = Tonemap(texelFetch(ssSource, ivec2(k, line), 0).rgb);
#else
		vec3 value = texelFetch(ssSource, ivec2(line, k), 0).rgb;
#endif
		sum += value * w;
		weights += w;
	}

	vec3 color = sum / max(weights, 1e-6);
#ifdef SS_HORIZONTAL
	FragColor = vec4(color, 1.0);
#else
	// Lanczos' lobes may step outside 0..1 at hard edges.
	color = LinearToSrgb(clamp(color, 0.0, 1.0));
	// Half an 8-bit step of static dither before quantisation, as the plain tonemap's (at the window's pixels).
	color += (Dither(gl_FragCoord.xy) - 0.5) / 255.0;
	FragColor = vec4(color, 1.0);
#endif
}

/////Fragment
