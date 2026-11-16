//Compute

#version 430

// The columns of the GI's media volume (LELEngine.Volumetrics.VolumetricMedia), from which its levels take how the
// clouds look and cover from each side (VolumetricMediaMip.shader). A column is a row of voxels along an axis put
// together front to back, from both ends: the light it shows seen from its + end, from its - end, and how much of it
// is covered. A level's columns are as long as that level's voxels and stay thin (two voxels of the finest level
// across; from where a level's voxel would have more than sixteen of them a side, a sixteenth of that voxel), so
// every part of a cloud hides what lies behind it in its own column and nothing else. A level's voxel is the mean of
// the columns through it.
// Why not from the level below (each voxel of a level from its eight, the back four through what the front four let
// through): that takes the front voxel's coverage as spread evenly over its face, and a cloud's parts lie behind
// one another (a flat storm seen from its edge: every part hides the next whole, but each covers a third of its
// voxel's face, so two thirds of the next showed through, level after level; its dark side came out five times too
// bright, its light as a whole 1.6 times).
// The texture: x, y across the columns (the two axes after the column's own: y, z for x; z, x for y; x, y for z), z
// along them, the three axes one after another (side >> level slices each). Each level is made from the one below
// (two columns end to end; where the level has half as many columns across, the four pairs of a block are joined
// first and averaged then), the first from the voxels (four voxels beside each other a fetch: the texture's own
// filter at the corner they share).
// The levels fetch a voxel's columns in one thread: a level whose voxel was the mean of thousands of columns took a
// third of a millisecond for its one voxel (the reason for the sixteenth).

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
layout(binding = 0, rgba16f) uniform writeonly image3D ColumnsOut;

uniform sampler3D Media;     // the volume (its finest level is read, for the first level of columns)
uniform sampler3D Columns;   // the columns of the level below (for the levels after)
uniform int level;           // the level written
uniform int firstLevel;      // the first level that has columns
uniform int side;            // the volume's voxels along a side
uniform int across;          // this level's columns along a side of the volume
uniform int acrossBelow;     // the level below's (the same or twice as many)

const vec3 Luminance = vec3(0.2126, 0.7152, 0.0722);

// Two columns end to end, a at the - end: x seen from the + end (b in front), y from the - end, z covered.
vec3 Join(vec3 a, vec3 b)
{
	return vec3(b.x + (1.0 - b.z) * a.x, a.y + (1.0 - a.z) * b.y, a.z + b.z - a.z * b.z);
}

void main()
{
	ivec3 p = ivec3(gl_GlobalInvocationID);
	int count = side >> level;
	if (p.x >= across || p.y >= across || p.z >= count * 3)
	{
		return;
	}

	int axis = p.z / count;
	int w = p.z - axis * count;
	vec3 joined;
	if (level == firstLevel)
	{
		// The voxels of the finest level this column covers: side / across of them across (averaged by the filter,
		// which is exact for two), 1 << level along.
		int run = 1 << level;
		float texel = 1.0 / float(side);
		vec3 at;
		at[(axis + 1) % 3] = (float(p.x) + 0.5) / float(across);
		at[(axis + 2) % 3] = (float(p.y) + 0.5) / float(across);
		joined = vec3(0.0);
		for (int k = 0; k < run; k++)
		{
			at[axis] = (float(w * run + k) + 0.5) * texel;
			vec4 m = textureLod(Media, at, 0.0);
			float light = dot(m.rgb, Luminance);
			joined = Join(joined, vec3(light, light, min(m.a, 1.0)));
		}
	}
	else
	{
		int below = count * 2;
		int block = acrossBelow / across;
		joined = vec3(0.0);
		for (int j = 0; j < block; j++)
		{
			for (int i = 0; i < block; i++)
			{
				ivec2 c = p.xy * block + ivec2(i, j);
				vec3 a = texelFetch(Columns, ivec3(c, axis * below + w * 2), 0).xyz;
				vec3 b = texelFetch(Columns, ivec3(c, axis * below + w * 2 + 1), 0).xyz;
				joined += Join(a, b);
			}
		}

		joined /= float(block * block);
	}

	imageStore(ColumnsOut, p, vec4(joined, 0.0));
}

/////Compute
