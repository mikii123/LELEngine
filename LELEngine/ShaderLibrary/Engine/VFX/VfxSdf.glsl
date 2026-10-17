// The global signed distance field of the GI (Engine/DistanceField.glsl) for particle operators and blocks:
// distance and normal at a world position, with a "not available" answer outside the field.
#ifndef VFX_SDF_GLSL
#define VFX_SDF_GLSL

// Distance to the nearest surface (world units, clamped to the field's band); a large value outside the grid.
float VfxSampleGlobalSdf(vec3 world)
{
	if (sdfResolution <= 0 || !InsideSdfGrid(world)) return 1e6;
	return SampleSdf(world);
}

vec3 VfxGlobalSdfNormal(vec3 world)
{
	if (sdfResolution <= 0 || !InsideSdfGrid(world)) return vec3(0.0, 1.0, 0.0);
	return SdfNormal(world);
}

bool VfxGlobalSdfAvailable(vec3 world)
{
	return sdfResolution > 0 && InsideSdfGrid(world);
}

#endif
