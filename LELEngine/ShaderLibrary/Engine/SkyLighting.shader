//Compute

#version 430

// The GI's sky (Rendering/SkyEnvironment.cs): the skybox cubemap resampled into a small RGBA16F cubemap whose mips
// the cones read by their aperture (Engine/Sky.glsl). Every output texel averages taps x taps directions spread over
// its footprint, so the copy is a box filter of the source whatever the source's own mips and filtering are (a
// skybox imported without mips and point filtered still lights smoothly).
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(binding = 0, rgba16f) uniform writeonly imageCube Target;
uniform samplerCube Source;
uniform int faceSize;
uniform int taps;
uniform float sourceLod;

// The direction of a face texel (GL's cube map face selection, s and t in -1..1, t along the rows).
vec3 FaceDirection(int face, float sc, float tc)
{
	if (face == 0) return vec3(1.0, -tc, -sc);
	if (face == 1) return vec3(-1.0, -tc, sc);
	if (face == 2) return vec3(sc, 1.0, tc);
	if (face == 3) return vec3(sc, -1.0, -tc);
	if (face == 4) return vec3(sc, -tc, 1.0);
	return vec3(-sc, -tc, -1.0);
}

void main()
{
	ivec3 id = ivec3(gl_GlobalInvocationID);
	if (id.x >= faceSize || id.y >= faceSize)
	{
		return;
	}

	vec3 sum = vec3(0.0);
	for (int j = 0; j < taps; j++)
	{
		for (int i = 0; i < taps; i++)
		{
			vec2 st = (vec2(id.xy) + (vec2(i, j) + 0.5) / float(taps)) / float(faceSize) * 2.0 - 1.0;
			sum += textureLod(Source, normalize(FaceDirection(id.z, st.x, st.y)), sourceLod).rgb;
		}
	}

	imageStore(Target, id, vec4(sum / float(taps * taps), 1.0));
}

/////Compute
