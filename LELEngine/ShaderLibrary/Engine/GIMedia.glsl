// The volumetric objects in the GI: a coarse world-aligned volume of what their clouds scatter and hide, made every
// frame (VolumetricMediaPass, Engine/Volumetrics/VolumetricMedia.shader) and read along the cones and rays the GI
// traces, so clouds light the surfaces around them and hide the sky and the light behind them. It reaches further than
// the GI's own volume: a cone that leaves that goes on through the clouds beyond before it sees the sky.
// Uniforms are set by Lighting.SetMediaUniforms (giMediaActive 0: no cloud contributes, nothing is read).
#ifndef LEL_GI_MEDIA_GLSL
#define LEL_GI_MEDIA_GLSL

uniform sampler3D giMedia;     // rgb: light sent out along one voxel of path (premultiplied by a), a: how much of that path the clouds cover (the mean of the three axes)
uniform sampler3D giMediaSun;  // of that light, the directional lights' part: w its luminance, xyz that times the mean cosine of its scattering, towards the light
uniform sampler3D giMediaFacePlus;  // xyz: the light's luminance as the clouds look from the + side of x, y, z; w: how much they cover along x (the levels above the finest)
uniform sampler3D giMediaFaceMinus; // xyz: from the - side; w: how much they cover along y
uniform int giMediaSides;           // 1: wide reads take the looks from their own side (GlobalIlluminationSettings.VolumeMediaSides)
uniform int giMediaActive;
uniform vec3 giMediaMin;       // world position of the texture's first corner
uniform vec3 giMediaInvSize;   // 1 / the world size of the whole texture
uniform vec3 giMediaUsed;      // world position of the first corner of the part that holds clouds (the texture's middle)
uniform vec3 giMediaExtent;    // world size of that part
uniform float giMediaVoxel;    // world units per texel
uniform float giMediaMaxLod;

// A cone or ray that has not met a cloud yet (its phase factor, see GIMediaRead).
const float GIMediaNoPhase = -1.0;

// The clouds around a point, averaged over `footprint`: rgb the light they send out towards where `direction` comes
// from (a cone's or ray's direction, from its start `from`), a their optical depth along a path of `len` through them
// there (`before` of it before the point), as a cone that wide meets them (0: none).
// A cloud sends a directional light's light mostly on forward, so it is brighter seen against the light than with it:
// the volume holds the light at the phase's mean and, of the directional lights' part, the luminance and the mean
// cosine of its scattering towards the light; the phase for this direction is a Henyey-Greenstein lobe of that mean
// cosine (the orders of scattering the march adds up have lobes of their own; one lobe of their mean is within a
// fifth of their sum from the side and from behind). It is worked out where the cone first meets a cloud and kept in
// `phase` for the rest of its way (negative: not met one yet): what the light is scattered by changes little along
// one cone, and a second fetch at every read cost as much as the read.
// The level read is the one whose voxels are as large as the footprint, between two levels as it falls: the next
// level above reads up to four times the footprint's area, and a cloud counted for that many times the cone's own
// width (wide cones took in three times a cloud's light).
// sides: the light as it looks from the side the cone comes from, not its mean over all sides. A coarse voxel holds a
// cloud whole, and a thick one lit from one side is bright from there and dark from behind (to a cone it was as
// bright from both: two to four times off). The levels keep each of the six looks' luminance and, along each axis,
// how much of the voxel's face the clouds cover (VolumetricMediaMip.shader: a flat cloud covers all of a voxel from
// above and a strip of it from its edge). The read blends the three looks it faces, and the three coverages, by the
// squares of its direction's parts: their ratio is how bright the clouds are from there (a cloud as bright from
// every side stays so whatever its shape; a storm's dark side seen from 15 degrees above its edge came out 0.44 of
// its lit side, drawn 0.45). What the read covers is the root of the coverages' squares blended the same way: how an
// ellipsoid's outline changes with the direction it is seen from (exact for one, and for a ball; a box shows up to
// 1.4 times more between its axes). From the second level on (the finest has neither; between the two the read fades
// in), whatever the level's coverage: a coarse voxel's coverage is how thick its clouds are times how much of it
// they fill, so a small thick cloud in a large voxel covers little of it (a threshold on the coverage left three
// quarters of a storm's looks out), and a haze needs no test, its looks and coverages being equal. For a read as
// wide as a cone; the steps of a walk along a cone put the near side in front themselves.
vec4 GIMediaRead(vec3 p, vec3 from, vec3 direction, float footprint, float len, float before, bool sides, inout float phase)
{
	// Nothing further than two texels of the level read from the part that holds clouds. Up to there the texture
	// itself fades out (empty around that part, a border of nothing around the texture): a test at the part's faces cut
	// the coarse levels off there, and each cone's reads crossing a face drew a straight edge on the surfaces.
	float lod = min(log2(max(footprint / giMediaVoxel, 1.0)), giMediaMaxLod);
	float texel = exp2(lod) * giMediaVoxel;
	vec3 q = p - giMediaMin;
	vec3 uvw = q * giMediaInvSize;

	// path: how much of one crossing of the level's voxel the stretch makes. The clouds lie within the part in use
	// only, so only the stretch's length within that part's extent along the cone counts, and where that part is
	// thinner than the voxel (along the cone) a crossing of it is one of the voxel's clouds. A coarse level's voxel
	// is as large as all the clouds and its filter reaches a voxel further: a stretch that ended above a flat storm
	// took it in from above, and the next one again (2.2 times its light from above, 1.45 from its sides).
	vec3 middle = giMediaUsed + 0.5 * giMediaExtent;
	float along = dot(middle - p, direction);
	float reach = 0.5 * dot(giMediaExtent, abs(direction));
	float overlap = min(along + reach, len - before) - max(along - reach, -before);
	if (overlap <= 0.0)
	{
		return vec4(0.0);
	}

	float path = min(overlap / max(min(texel, 2.0 * reach), 1e-6), 1.0);

	// share: of the cone, how much the level's voxel takes (a cone wider than the voxel has its clouds in that share).
	float share = 1.0;
	vec3 seenAlong = direction;
	if (footprint > texel * 1.01)
	{
		// A footprint larger than the whole volume (the coarsest level is one voxel: all of it): the texture's own
		// filter reaches one of its voxels around it, a cone that wide takes the clouds in from as far as it is wide
		// (it passed a small cloud by unless its sample fell right next to it). The volume as a whole, then, as the
		// cone sees it: the share of the cone's solid angle the volume takes from where the cone starts (its face's
		// square over the distance's, over the cone's solid angle), whole while the volume lies within 0.7 of the
		// cone's angle from its axis and down to nothing at 1.3 (1.03 times the cone's solid angle in all; the sum
		// over the directions near a cloud is then the cloud's light); along the cone it counts for the share of the
		// part's length there that the stretch covers (path, above).
		// A share of the cone's cross-section where it is read, fading over the footprint, took a wide cone's angle
		// as small: a cone of thirty degrees took in a flat storm from up to ninety degrees off its axis, the fast
		// gather's of sixty degrees a ball over the surface at a third of its light. A fade along the axis (the
		// stretch read at one point, the volume a tent around it) took in a small cloud at 0.65 of its light: the
		// stretches are longer than such a cloud.
		vec3 toward = middle - from;
		float far2 = max(dot(toward, toward), 1e-8);
		// The cone's half angle: the footprint is 0.886 of its width at the stretch's middle.
		float spread = atan(footprint / (1.772 * max(length(p - from) - before + 0.5 * len, 1e-4)));
		float off = acos(clamp(dot(toward, direction) * inversesqrt(far2), -1.0, 1.0));
		float profile = clamp((1.3 - off / spread) / 0.6, 0.0, 1.0);
		share = min(texel * texel * profile / (far2 * 6.4717 * (1.0 - cos(spread))), 1.0);
		if (share <= 0.0)
		{
			return vec4(0.0);
		}

		uvw = vec3(0.5);

		// All the clouds at once are seen from where the cone starts towards where they are, which a cone this wide
		// may have far off its axis (the fast gather's one cone along a wall's normal, a storm above the wall: the
		// storm's underside is what lights the wall, not the side the normal points at).
		seenAlong = toward * inversesqrt(far2);
	}
	else
	{
		float margin = 2.0 * texel + giMediaVoxel;
		vec3 inside = p - giMediaUsed;
		if (any(lessThan(inside, vec3(-margin))) || any(greaterThan(inside, giMediaExtent + margin)))
		{
			return vec4(0.0);
		}
	}

	vec4 m = textureLod(giMedia, uvw, lod);
	if (m.a <= 0.0)
	{
		return vec4(0.0);
	}

	if (phase < 0.0)
	{
		phase = 1.0;
		vec4 sun = textureLod(giMediaSun, uvw, lod);
		float moment = length(sun.xyz);
		if (sun.w > 1e-7 && moment > 1e-7)
		{
			float g = min(moment / sun.w, 0.95);
			float root = inversesqrt(max(1e-4, 1.0 + g * g - 2.0 * g * dot(sun.xyz, direction) / moment));
			float lobe = (1.0 - g * g) * root * root * root;
			float luminance = max(dot(m.rgb, vec3(0.2126, 0.7152, 0.0722)), sun.w);
			phase = max(1.0 + sun.w / luminance * (lobe - 1.0), 0.0);
		}
	}

	// The level's coverage of a path across one of its voxels (VolumetricMediaMip.shader). A path n of those voxels
	// long lets (1 - coverage)^n through; never more than one of them (the coarsest level is the whole volume: a longer
	// path does not cross its clouds again). A cone wider than the level's voxel (wider than all the clouds, at the
	// coarsest level) has them in the share of it the voxel takes (above).
	float covers = m.a;
	float lit = m.a;
	float look = phase;
	if (sides && giMediaSides != 0 && lod > 0.0)
	{
		// Seen from where the cone comes from: the faces on the side opposite to its direction. Their mean is the
		// level's light (the six looks' mean), so the ratio is taken within one level.
		float faceLod = max(lod, 1.0);
		vec4 facePlus = textureLod(giMediaFacePlus, uvw, faceLod);
		vec4 faceMinus = textureLod(giMediaFaceMinus, uvw, faceLod);
		float mean = (facePlus.x + facePlus.y + facePlus.z + faceMinus.x + faceMinus.y + faceMinus.z) / 6.0;
		if (mean > 1e-7)
		{
			vec3 faces = mix(facePlus.xyz, faceMinus.xyz, step(vec3(0.0), seenAlong));
			vec3 axes = vec3(facePlus.w, faceMinus.w, max(3.0 * m.a - facePlus.w - faceMinus.w, 0.0));
			vec3 weights = seenAlong * seenAlong;
			float blend = min(lod, 1.0);
			look *= mix(1.0, dot(faces, weights) / mean, blend);
			lit = max(mix(m.a, dot(axes, weights), blend), 1e-6);
			covers = max(mix(m.a, sqrt(dot(axes * axes, weights)), blend), 1e-6);
		}
	}

	float coverage = min(covers, 0.9999) * share;
	return vec4(m.rgb * (look / lit), -log(1.0 - coverage) * path);
}

// The clouds along a stretch of a cone or ray: what they scatter towards its start (rgb, premultiplied by their
// coverage) and how much of it they cover (a). start: where the stretch begins, travelled: how far that is from the
// cone's own start, direction: the cone's, width: its diameter there, len: the stretch's length, jitter: where in a
// step its sample lies (0.5: the middle).
// A wide cone (as wide as its stretch is long, or wider): one read as wide as the cone's cross-section gives the
// clouds' mean over the stretch, which is how much of it they cover. That mean is not what is seen where the stretch
// is thick: a thick cloud shows its near side (lit from there it is bright, lit from behind dark; the mean is its dim
// inside and would let the light through it). So a thick stretch is walked along its middle as well, and what the walk
// met is its light: more of it the thicker the stretch (from an optical depth of 1 to 2: no edge where the walk sets
// in) and the more of a cloud the walk met (one that lies off the cone's middle keeps its mean).
// A narrow cone (its stretch longer than it is wide): the walk alone, coverage and light (a read as long as the
// stretch would be far wider than the cone).
vec4 GIMediaSegment(vec3 start, float travelled, vec3 direction, float width, float len, float jitter, inout float phase)
{
	bool narrow = len > 1.3 * max(width, giMediaVoxel);
	vec3 from = start - direction * travelled;
	vec4 mean = vec4(0.0);
	float covered = 0.0;
	if (!narrow)
	{
		// As wide as the cone's cross-section: a square of the disc's area, 0.886 of its diameter.
		mean = GIMediaRead(start + direction * (len * jitter), from, direction, 0.886 * width, len, len * jitter, true, phase);
		if (mean.a <= 0.0)
		{
			return vec4(0.0);
		}

		covered = 1.0 - exp(-mean.a);
		if (mean.a <= 1.0 || len < giMediaVoxel * 2.0)
		{
			return vec4(mean.rgb * covered, covered);
		}
	}

	// The walk, front to back: pieces of a quarter of the stretch, each read as fine as it is long; halved while a
	// piece holds more than a trace of cloud (down to a voxel, or a thirty-second of the stretch: a dense cloud's near
	// side is that thin), doubled again through empty space; sixteen reads at most (an empty piece returns before it
	// fetches).
	float finest = max(giMediaVoxel, len / 32.0);
	float longest = max(len * 0.25, finest);
	float piece = longest;
	vec4 seen = vec4(0.0);
	float t = 0.0;
	for (int i = 0; i < 16 && t < len - finest * 0.01 && seen.a < 0.95; i++)
	{
		float l = min(piece, len - t);
		vec4 s = GIMediaRead(start + direction * (t + l * jitter), from, direction, l, l, l * jitter, false, phase);
		if (s.a > 0.3 && l > finest * 1.01)
		{
			piece = max(l * 0.5, finest);
			continue;
		}

		float a = 1.0 - exp(-s.a);
		seen.rgb += (1.0 - seen.a) * a * s.rgb;
		seen.a += (1.0 - seen.a) * a;
		t += l;
		if (s.a < 0.02)
		{
			piece = min(piece * 2.0, longest);
		}
	}

	if (narrow)
	{
		return seen;
	}

	float walked = smoothstep(1.0, 2.0, mean.a) * smoothstep(0.05, 0.4, seen.a);
	return vec4(mix(mean.rgb, seen.rgb / max(seen.a, 0.05), walked) * covered, covered);
}

// How long the stretch of a cone that starts `from` its origin is: one and a quarter of the cone's diameters there (a
// little shorter than the cone is wide at the stretch's middle: the read's voxels are that wide, and reads a voxel
// apart count every cloud once), at
// least a third of the distance (narrow cones) and half a voxel of the volume (nothing in it is finer). jitter in
// [0, 1) makes it 0.7 to 1.3 times that: every stretch is read at a level of its own, and with the same stretches for
// every pixel their ends drew shells around each surface point (rings across a cloud seen through them); the pass's
// jitter turns those into noise its filter takes out.
float GIMediaStride(float aperture, float from, float jitter)
{
	return max(max(2.5 * aperture, 0.35) * from, giMediaVoxel * 0.5) * (0.7 + 0.6 * jitter);
}

// The clouds along a cone or ray between two distances from its origin (aperture = tan of the half angle; rays pass
// the angle their sample stands for), in stretches that grow with the distance, never more than sixteen of them;
// jitter in [0, 1) moves the samples within their stretches (rays: noise instead of bands). rgb = their light
// (premultiplied), a = their coverage.
vec4 GIMediaRange(vec3 origin, vec3 direction, float aperture, float from, float to, float jitter, inout float phase)
{
	// As far as the volume's farthest corner: nothing of it lies further. Not only where the axis crosses the volume: a
	// wide cone that passes beside a cloud still takes part of it in (its stretches are read as wide as they are long),
	// and a cut at the volume's faces would draw their edges on the surfaces. A read that is nowhere near the clouds
	// returns before it fetches.
	vec3 lo = giMediaUsed - origin;
	float start = from;
	float end = min(to, length(max(abs(lo), abs(lo + giMediaExtent))));
	vec4 result = vec4(0.0);
	if (end <= start)
	{
		return result;
	}

	float least = (end - start) / 16.0;
	float dist = start;
	for (int i = 0; i < 16 && dist < end && result.a < 0.98; i++)
	{
		float len = min(max(GIMediaStride(aperture, dist, jitter), least), end - dist);
		vec4 m = GIMediaSegment(origin + direction * dist, dist, direction, 2.0 * aperture * (dist + len * 0.5), len, jitter, phase);
		result.rgb += (1.0 - result.a) * m.rgb;
		result.a += (1.0 - result.a) * m.a;
		dist += len;
	}

	return result;
}

vec4 GIMediaRange(vec3 origin, vec3 direction, float aperture, float from, float to, float jitter)
{
	float phase = GIMediaNoPhase;
	return GIMediaRange(origin, direction, aperture, from, to, jitter, phase);
}

// A ray's radiance (what it hit, the far field or the sky, `reached` away; negative: nothing nearer than the sky)
// with the clouds on its way.
vec3 GIMediaApply(vec3 radiance, vec3 origin, vec3 direction, float aperture, float reached, float jitter)
{
	if (giMediaActive == 0)
	{
		return radiance;
	}

	vec4 m = GIMediaRange(origin, direction, aperture, 0.0, reached >= 0.0 ? reached : 1e30, jitter);
	return m.rgb + (1.0 - m.a) * radiance;
}

#endif
