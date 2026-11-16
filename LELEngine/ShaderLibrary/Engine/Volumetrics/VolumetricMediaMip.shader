//Compute

#version 430

// The levels of the GI's media volume (LELEngine.Volumetrics.VolumetricMedia): each voxel of a level is the clouds in
// it as they look from outside. From each of the six sides: the light they show (premultiplied by what they cover of
// the voxel's face) and, along each of the three axes, how much of the face they cover. Both are the mean of the
// columns through the voxel along that axis (VolumetricMediaColumns.shader: thin rows of the finest voxels put
// together front to back), so they are what a camera on that side would see of the voxel. So a level's
// coverage is how much of a path across one of its voxels the clouds cover (a small thick cloud in a coarse voxel
// covers the share of its face it stands in; a thin haze the sum of its parts), and its light is the clouds' surface
// as seen from outside, not the mean of their inside. A box filter of coverage per fine voxel made every small thick
// cloud a wide solid one in the coarse levels: thirteen times its light in wide cones.
// Media keeps the mean of the six looks (rgb) and of the three coverages (a); FacePlus the looks' luminance from the
// + side of x, y and z, FaceMinus from the - side (a thick cloud lit from one side is bright seen from there and dark
// from behind; one lit from inside near its top is bright from above), and in their a the coverage along x and
// along y (along z: three times Media's less those two). A read takes the look and the coverage of its own side
// (Engine/GIMedia.glsl). The finest level has neither: a voxel there is its cloud at one point.
// The colour, and the directional lights' part (MediaSun), have no columns: they are put together from the eight
// voxels of the level below (along each axis the two layers one in front of the other, from either end, the twelve
// looks averaged), which lets light through that the columns hide; so they are scaled to the looks' mean luminance
// (the colour of the light is right, how much of it is the columns'). The first level above the finest is exact
// that way too (its columns are the pairs of voxels themselves) and needs no columns.

layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;
layout(binding = 0, rgba16f) uniform writeonly image3D MediaOut;
layout(binding = 1, rgba16f) uniform writeonly image3D SunOut;
layout(binding = 2, rgba16f) uniform writeonly image3D FacePlusOut;
layout(binding = 3, rgba16f) uniform writeonly image3D FaceMinusOut;

uniform sampler3D Media;
uniform sampler3D MediaSun;
uniform sampler3D Columns;       // the written level's columns (the levels past the first)
uniform int sourceLevel;         // the level read (the one written is the next)
uniform ivec3 sourceSize;        // its voxels
uniform int side;                // the finest level's voxels along a side
uniform int columnsPerVoxel;     // the written level's columns along a side of one of its voxels (2, 4, 8 or 16)
uniform vec3 columnsSize;        // the texels of the Columns texture

const vec3 Luminance = vec3(0.2126, 0.7152, 0.0722);

void main()
{
	ivec3 p = ivec3(gl_GlobalInvocationID);
	ivec3 size = max(sourceSize / 2, ivec3(1));
	if (any(greaterThanEqual(p, size)))
	{
		return;
	}

	// The eight voxels below (an axis the level below has one voxel along: the same voxel twice).
	vec4 media[8];
	vec4 sun[8];
	for (int i = 0; i < 8; i++)
	{
		ivec3 c = min(p * 2 + ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1), sourceSize - 1);
		media[i] = texelFetch(Media, c, sourceLevel);
		sun[i] = texelFetch(MediaSun, c, sourceLevel);
	}

	vec3 light = vec3(0.0);
	vec4 sunLight = vec4(0.0);
	vec3 facePlus = vec3(0.0);
	vec3 faceMinus = vec3(0.0);
	vec3 covered = vec3(0.0);
	for (int axis = 0; axis < 3; axis++)
	{
		int along = 1 << axis;
		bool pair = sourceSize[axis] > 1;
		for (int i = 0; i < 8; i++)
		{
			if ((i & along) != 0)
			{
				continue;
			}

			// a lies on the - side of the axis, b on the + side.
			vec4 a = media[i];
			vec4 b = media[i | along];
			vec3 fromPlus;
			vec3 fromMinus;
			if (pair)
			{
				// Seen from the + side (b in front) and from the - side (a in front): the front one's light, plus the
				// back one's through what the front lets through; covered by both.
				fromPlus = b.rgb + (1.0 - b.a) * a.rgb;
				fromMinus = a.rgb + (1.0 - a.a) * b.rgb;
				covered[axis] += a.a + b.a - a.a * b.a;
				sunLight += sun[i] + (1.0 - a.a) * sun[i | along] + sun[i | along] + (1.0 - b.a) * sun[i];
			}
			else
			{
				fromPlus = a.rgb;
				fromMinus = a.rgb;
				covered[axis] += a.a;
				sunLight += sun[i] * 2.0;
			}

			light += fromPlus + fromMinus;
			facePlus[axis] += dot(fromPlus, Luminance);
			faceMinus[axis] += dot(fromMinus, Luminance);
		}
	}

	// Four pairs an axis, two looks a pair.
	light /= 24.0;
	sunLight /= 24.0;
	facePlus *= 0.25;
	faceMinus *= 0.25;
	covered *= 0.25;
	if (sourceLevel > 0)
	{
		// The looks and the coverage from the columns through this voxel: columnsPerVoxel a side, four a fetch (the
		// texture's own filter at the corner the four share, at the middle of the voxel's slice).
		int count = side >> (sourceLevel + 1);
		int fetches = columnsPerVoxel >> 1;
		for (int axis = 0; axis < 3; axis++)
		{
			vec2 corner = vec2(float(p[(axis + 1) % 3] * columnsPerVoxel), float(p[(axis + 2) % 3] * columnsPerVoxel));
			float slice = (float(axis * count + p[axis]) + 0.5) / columnsSize.z;
			vec3 sum = vec3(0.0);
			for (int j = 0; j < fetches; j++)
			{
				for (int i = 0; i < fetches; i++)
				{
					sum += textureLod(Columns, vec3((corner + vec2(float(2 * i + 1), float(2 * j + 1))) / columnsSize.xy, slice), 0.0).xyz;
				}
			}

			sum /= float(fetches * fetches);
			facePlus[axis] = sum.x;
			faceMinus[axis] = sum.y;
			covered[axis] = sum.z;
		}

		float made = dot(light, Luminance);
		float mean = (facePlus.x + facePlus.y + facePlus.z + faceMinus.x + faceMinus.y + faceMinus.z) / 6.0;
		if (made > 1e-9)
		{
			light *= mean / made;
			sunLight *= mean / made;
		}
		else
		{
			light = vec3(mean);
			sunLight = vec4(0.0);
		}
	}

	imageStore(MediaOut, p, vec4(light, (covered.x + covered.y + covered.z) / 3.0));
	imageStore(SunOut, p, sunLight);
	imageStore(FacePlusOut, p, vec4(facePlus, covered.x));
	imageStore(FaceMinusOut, p, vec4(faceMinus, covered.y));
}

/////Compute
