// Global signed distance field sampling and sphere tracing.
// Uniforms are set by Lighting.SetSdfUniforms. Include after #version.

uniform sampler3D GlobalSdf;
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
bool TraceSdf(vec3 origin, vec3 direction, float maxDistance, out float hitT)
{
	float voxel = SdfVoxelSize();
	float hitEpsilon = voxel * 0.5;
	float minStep = voxel * 0.25;

	float t = voxel * 0.5;
	float d = 1e9;
	bool exhausted = true;
	for (int i = 0; i < sdfMaxSteps; i++)
	{
		vec3 p = origin + direction * t;
		if (!InsideSdfGrid(p))
		{
			exhausted = false;
			break;
		}

		d = SampleSdf(p);
		if (i > 0 && d < hitEpsilon)
		{
			hitT = t;
			return true;
		}

		t += max(d, minStep);
		if (t > maxDistance)
		{
			exhausted = false;
			break;
		}
	}

	if (exhausted && d < voxel * 2.0)
	{
		hitT = t;
		return true;
	}

	hitT = maxDistance;
	return false;
}
