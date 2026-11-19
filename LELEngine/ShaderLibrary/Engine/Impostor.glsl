// Octahedral impostors (LELEngine.Rendering.Impostors, Engine/Impostor.shader): an object captured from the directions
// of a grid of frames x frames views, each orthographic and looking at the centre of the object's bounding sphere,
// stored side by side in an atlas. The grid is the octahedral map of the directions: the whole sphere, or its upper
// half for objects never seen from below. LELEngine.Rendering.Impostors.ImpostorMath is the same code for the bake;
// the two must stay equal.
// Include after #version: #include "Engine/Impostor.glsl"
#ifndef LEL_IMPOSTOR_GLSL
#define LEL_IMPOSTOR_GLSL

// The unit direction of a point of the octahedral map (e in -1..1 on both axes): +Y at the centre, the horizon on the
// diamond |x| + |y| = 1, -Y at the four corners.
vec3 ImpostorOctDecode(vec2 e)
{
	vec3 n = vec3(e.x, 1.0 - abs(e.x) - abs(e.y), e.y);
	float t = max(-n.y, 0.0);
	n.x += n.x >= 0.0 ? -t : t;
	n.z += n.z >= 0.0 ? -t : t;
	return normalize(n);
}

vec2 ImpostorOctEncode(vec3 n)
{
	n /= abs(n.x) + abs(n.y) + abs(n.z);
	vec2 e = n.xz;
	if (n.y < 0.0)
	{
		e = (1.0 - abs(e.yx)) * vec2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
	}

	return e;
}

// The upper half alone on the whole map: +Y at the centre, the horizon on the map's border.
vec3 ImpostorHemiOctDecode(vec2 e)
{
	vec2 t = vec2(e.x + e.y, e.x - e.y) * 0.5;
	return normalize(vec3(t.x, 1.0 - abs(t.x) - abs(t.y), t.y));
}

// Directions below the horizon are taken on it.
vec2 ImpostorHemiOctEncode(vec3 n)
{
	n.y = max(n.y, 0.0);
	vec2 t = n.xz / max(abs(n.x) + n.y + abs(n.z), 1e-6);
	return vec2(t.x + t.y, t.x - t.y);
}

// The direction a frame was captured from (from the sphere's centre towards its camera, object space); frame = its
// column and row in the grid.
vec3 ImpostorFrameDirection(vec2 frame, float frames, bool hemisphere)
{
	vec2 e = frame / (frames - 1.0) * 2.0 - 1.0;
	return hemisphere ? ImpostorHemiOctDecode(e) : ImpostorOctDecode(e);
}

// The axes of a frame's image for its direction: right along its u, up along its v. The helper vector is a little off
// the Y axis and so never along a frame's direction (the poles are frames).
void ImpostorFrameAxes(vec3 direction, out vec3 right, out vec3 up)
{
	right = normalize(cross(vec3(0.0131, 1.0, 0.0273), direction));
	up = cross(direction, right);
}

// The three frames around a point of the map and their weights (they sum to 1, each fades to 0 where its frame leaves
// the three). Every cell of the grid is cut along its shorter diagonal on the sphere, so the three views are the
// nearest ones: which diagonal that is depends on the quarter of the map.
void ImpostorNearestFrames(vec2 e, float frames, bool hemisphere, out vec2 frameA, out vec2 frameB, out vec2 frameC, out vec3 weights)
{
	vec2 grid = clamp(e * 0.5 + 0.5, 0.0, 1.0) * (frames - 1.0);
	vec2 cell = min(floor(grid), vec2(frames - 2.0));
	vec2 f = grid - cell;
	vec2 centre = (cell + 0.5) / (frames - 1.0) * 2.0 - 1.0;
	if ((centre.x * centre.y > 0.0) != hemisphere)
	{
		// Cut from (1, 0) to (0, 1).
		frameA = cell + vec2(1.0, 0.0);
		frameB = cell + vec2(0.0, 1.0);
		if (f.x + f.y < 1.0)
		{
			frameC = cell;
			weights = vec3(f.x, f.y, 1.0 - f.x - f.y);
		}
		else
		{
			frameC = cell + vec2(1.0);
			weights = vec3(1.0 - f.y, 1.0 - f.x, f.x + f.y - 1.0);
		}
	}
	else
	{
		// Cut from (0, 0) to (1, 1).
		frameA = cell;
		frameB = cell + vec2(1.0);
		if (f.x > f.y)
		{
			frameC = cell + vec2(1.0, 0.0);
			weights = vec3(1.0 - f.x, f.y, f.x - f.y);
		}
		else
		{
			frameC = cell + vec2(0.0, 1.0);
			weights = vec3(1.0 - f.y, f.x, f.y - f.x);
		}
	}
}

#endif
