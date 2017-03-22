#ifdef GL_ES
precision mediump float;
#endif


uniform float time;
uniform vec2 mouse;
uniform vec2 resolution;


#define iterations 14
#define formuparam2 0.79

#define volsteps 5
#define stepsize 0.290

#define zoom 0.900
#define tile   0.850
#define speed2  0.10

#define brightness 0.003
#define darkmatter 0.400
#define distfading 0.560
#define saturation 0.800


#define transverseSpeed zoom*2.0
#define cloud 0.11 


float triangle(float x, float a) {
	float output2 = 2.0*abs(2.0*  ((x / a) - floor((x / a) + 0.5))) - 1.0;
	return output2;
}

float field(in vec3 p) {
	float strength = 7. + .03 * log(1.e-6 + fract(sin(time) * 4373.11));
	float accum = 0.;
	float prev = 0.;
	float tw = 0.;

	for (int i = 0; i < 6; ++i) {
		float mag = dot(p, p);
		p = abs(p) / mag + vec3(-.5, -.8 + 0.1*sin(time*0.7 + 2.0), -1.1 + 0.3*cos(time*0.3));
		float w = exp(-float(i) / 7.);
		accum += w * exp(-strength * pow(abs(mag - prev), 2.3));
		tw += w;
		prev = mag;
	}
	return max(0., 5. * accum / tw - .7);
}

float pi = 3.141592653589797323;

vec4 amain(vec3 dir, vec3 from) {
	float formuparam = formuparam2;

	float v2 = 1.0;

	float zooom = 0.;
	float sampleShift = mod(zooom, stepsize);

	float zoffset = -sampleShift;
	sampleShift /= stepsize; // make from 0 to 1

							 //volumetric rendering
	float s = 0.24;
	float s3 = s + stepsize / 2.0;
	vec3 v = vec3(0.);
	float t3 = 0.0;

	vec3 backCol2 = vec3(0.);
	for (int r = 0; r<volsteps; r++) {
		vec3 p2 = from + (s + zoffset)*dir;// + vec3(0.,0.,zoffset);
		vec3 p3 = from + (s3 + zoffset)*dir;// + vec3(0.,0.,zoffset);

		p2 = abs(vec3(tile) - mod(p2, vec3(tile*2.))); // tiling fold
		p3 = abs(vec3(tile) - mod(p3, vec3(tile*2.))); // tiling fold		
#ifdef cloud
		t3 = field(p3);
#endif

		float pa, a = pa = 0.;
		for (int i = 0; i<iterations; i++) {
			p2 = abs(p2) / dot(p2, p2) - formuparam; // the magic formula
													 //p=abs(p)/max(dot(p,p),0.005)-formuparam; // another interesting way to reduce noise
			float D = abs(length(p2) - pa); // absolute sum of average change
			a += i > 7 ? min(12., D) : D;
			pa = length(p2);
		}


		//float dm=max(0.,darkmatter-a*a*.001); //dark matter
		a *= a*a; // add contrast
				  //if (r>3) fade*=1.-dm; // dark matter, don't render near
				  // brightens stuff up a bit
		float s1 = s + zoffset;
		// need closed form expression for this, now that we shift samples
		float fade = pow(distfading, max(0., float(r) - sampleShift));
		//t3 += fade;		
		v += fade;
		//backCol2 -= fade;

		// fade out samples as they approach the camera
		if (r == 0)
			fade *= (1. - (sampleShift));
		// fade in samples as they approach from the distance
		if (r == volsteps - 1)
			fade *= sampleShift;
		v += vec3(s1, s1*s1, s1*s1*s1*s1)*a*brightness*fade; // coloring based on distance

		backCol2 += mix(.4, 1., v2) * vec3(1.8 * t3 * t3 * t3, 1.4 * t3 * t3, t3) * fade;


		s += stepsize;
		s3 += stepsize;
	}

	v = mix(vec3(length(v)), v, saturation); //color adjust	

	vec4 forCol2 = vec4(v*.01, 1.);
#ifdef cloud
	backCol2 *= cloud;
#endif	
	backCol2.b *= -3.8;
	backCol2.r *= 0.05;

	backCol2.b = 0.5*mix(backCol2.g, backCol2.b, 0.8);
	backCol2.g = -0.;
	backCol2.bg = mix(backCol2.gb, backCol2.bg, 0.5*(cos(time*0.01) + 1.0));
	return forCol2 + vec4(backCol2, 1.0);
}

void main()
{

	vec2 ms = vec2(8.0, 3.0)  * (mouse - vec2(0.5, 0.5));

	vec2 scr = (gl_FragCoord.xy / resolution - vec2(0.5, 0.5)) * sqrt(vec2(resolution.x / resolution.y, resolution.y / resolution.x)) * 4.0;

	vec3 iv = vec3(0.0, 1.0, 0.0);
	iv.xz += scr;
	iv /= length(iv);
	iv = mat3(1.0, 0.0, 0.0, 0.0, cos(ms.y), sin(ms.y), 0.0, -sin(ms.y), cos(ms.y)) * iv;
	iv = mat3(cos(ms.x), -sin(ms.x), 0.0, sin(ms.x), cos(ms.x), 0.0, 0.0, 0.0, 1.0) * iv;
	iv = mat3(cos(time / 5.0), -sin(time / 5.0), 0.0, sin(time / 5.0), cos(time / 5.0), 0.0, 0.0, 0.0, 1.0) * iv;

	float ix = -10.0 * (1.0 + 0.88 * cos(time / 5.0)) * sin(time / 5.0), iy = -10.0 * (1.0 + 0.88 * cos(time / 5.0)) * cos(time / 5.0) * 7.0 / 9.0, iz = -10.0 * (1.0 + 0.88 * cos(time / 5.0)) * cos(time / 5.0) * sqrt(81. - 49.) / 9.0;

	vec4 color = vec4(0.5, 0.5, 0.5, 1.0);

	vec4 v = vec4(iv, 0.0);
	vec4 r = vec4(ix, iy, iz, 0.0);


	for (int i = 0; i < 200; i++)
	{
		float R = length(r.xyz);

		float stp = max(min(pow(dot(r.xyz, v.xyz), 1.5), 0.5), 0.05);
		if (R > 19.5)
			stp = 0.05;

		if (R >= 20.0)
		{
			color = amain(v.xyz / length(v.xyz), r.xyz / 7.0 / length(r.xyz) + vec3(1.2423, 5.144141, 43.324));
			break;
		}

		if (length(r.xyz - vec3(10.0*cos(time), 8.0*sin(time), 6.0*sin(time))) < 1.5)
		{
			color = vec4(1.0, 1.0, 0.0, 0.0);
			break;
		}
		if (R < 1.0 || length(r.xyz + v.xyz) < 1.0)
		{
			color = vec4(0.0, 0.0, 0.0, 1.0);
			break;
		}

		r += v * stp;

		float pos = -0.5 / (R * R * R) * v.w * v.w - 1.5 / (R * R * R) * dot(v.xyz, v.xyz) + 1.5 / (R * R * R * R * R) * pow(dot(r.xyz, v.xyz), 2.0);
		float spd = dot(r.xyz, v.xyz) / (R - 1.0) / (R * R);

		vec4 a = vec4(
			r.x * pos + v.x * spd,
			r.y * pos + v.y * spd,
			r.z * pos + v.z * spd,
			v.w * dot(r.xyz, v.xyz) / (2.0 * (R - 1.0) * R * R)
		);

		v += a * stp;
	}

	gl_FragColor = color;
}