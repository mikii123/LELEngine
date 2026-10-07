// Global signed distance field sampling and sphere tracing.
// Uniforms are set by Lighting.SetSdfUniforms. Include after #version and before Engine/SceneObjects.glsl,
// which then gets the object-id fast path.

#define HAS_GLOBAL_SDF 1

uniform sampler3D GlobalSdf;
uniform usampler3D GlobalSdfObjectIds; // per voxel: index + 1 of the object nearest to it, 0 = none
uniform vec3 sdfGridMin;
uniform float sdfGridSize;
uniform int sdfResolution;
uniform int sdfMaxSteps;
uniform float sdfMaxDistance; // distance band stored in the field

float SdfVoxelSize()
{
	return sdfGridSize / float(sdfResolution);
}

bool InsideSdfGrid(vec3 p)
{
	vec3 uvw = (p - sdfGridMin) / sdfGridSize;
	return all(greaterThanEqual(uvw, vec3(0.0))) && all(lessThanEqual(uvw, vec3(1.0)));
}

// Distance to the nearest surface, clamped to the band. Outside the grid the edge value is returned.
float SampleSdf(vec3 p)
{
	vec3 uvw = (p - sdfGridMin) / sdfGridSize;
	return texture(GlobalSdf, uvw).r;
}

// Central differences. (A four-tap tetrahedral stencil would save two fetches, but its mixed-derivative
// term biases the normal along box edges, which changes the surface cache card selection at every edge.)
vec3 SdfNormal(vec3 p)
{
	float e = SdfVoxelSize() * 0.5;
	vec3 n = vec3(
		SampleSdf(p + vec3(e, 0.0, 0.0)) - SampleSdf(p - vec3(e, 0.0, 0.0)),
		SampleSdf(p + vec3(0.0, e, 0.0)) - SampleSdf(p - vec3(0.0, e, 0.0)),
		SampleSdf(p + vec3(0.0, 0.0, e)) - SampleSdf(p - vec3(0.0, 0.0, e)));
	float len = length(n);
	return len > 1e-6 ? n / len : vec3(0.0, 1.0, 0.0);
}

// Sphere tracing. The first step is never a hit so rays can start right next to a surface.
// A ray that runs out of steps while skimming along a surface counts as a hit on that surface: reporting
// it as a miss would turn every grazing ray into sky and brighten the lighting of everything the ray
// result feeds (surface cache radiosity, radiance cache probes, screen probes).
// The march is bounded by the ray's exit from the grid (slab test up front), so no step tests the bounds;
// a ray that leaves the grid while skimming a surface counts as a hit for the same reason.
bool TraceSdf(vec3 origin, vec3 direction, float maxDistance, out float hitT)
{
	float voxel = SdfVoxelSize();
	float hitEpsilon = voxel * 0.5;
	float minStep = voxel * 0.25;

	vec3 safeDirection = mix(vec3(1e-6), direction, greaterThan(abs(direction), vec3(1e-6)));
	vec3 invDirection = 1.0 / safeDirection;
	vec3 t0 = (sdfGridMin - origin) * invDirection;
	vec3 t1 = (sdfGridMin + vec3(sdfGridSize) - origin) * invDirection;
	vec3 tNear = min(t0, t1);
	vec3 tFar = max(t0, t1);
	float tEntry = max(max(tNear.x, tNear.y), tNear.z);
	float tExit = min(min(tFar.x, tFar.y), tFar.z);

	float t = voxel * 0.5;
	hitT = maxDistance;
	// A first sample outside the grid is a miss, as before.
	if (tEntry > t || tExit < t) return false;
	float tEnd = min(maxDistance, tExit);

	float d = 1e9;
	bool exhausted = true;
	for (int i = 0; i < sdfMaxSteps; i++)
	{
		d = SampleSdf(origin + direction * t);
		if (i > 0 && d < hitEpsilon)
		{
			hitT = t;
			return true;
		}

		t += max(d, minStep);
		if (t > tEnd)
		{
			exhausted = tExit < maxDistance; // left the grid: skimming rays still count as hits below
			break;
		}
	}

	if (exhausted && d < voxel * 2.0)
	{
		hitT = t;
		return true;
	}

	return false;
}
