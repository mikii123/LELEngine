// Sampling the voxel radiance volume (premultiplied radiance in rgb, occupancy in a).
// Uniforms are set by Lighting.SetVoxelRadianceUniforms.
//
// The voxels are VoxelRadiance level 0, its own mip chain is isotropic. giVoxelMips (VoxelMipMode) adds direction:
// 1 anisotropic: the first giVoxelAnisoLevels mips above the voxels have six views each in VoxelRadianceAniso, side by
//   side along x, slab d = 0..5 for the directions +X, -X, +Y, -Y, +Z, -Z a cone travels in, each made by compositing
//   its children front to back along that axis (level 0 = the volume's mip 1). A cone reads the slabs facing it,
//   weighted by the squared components of its direction, and the isotropic mips above them (blended over one level).
// 2 spherical harmonics L1: every voxel's radiance and coverage are weighted by (1 - n . d) for a cone travelling
//   along d (its front side counts twice, its back side not at all). The constant term is the isotropic volume, the
//   d.x, d.y, d.z terms are VoxelRadianceSHX / Y / Z (rgb radiance, a coverage, half resolution: level 0 = the
//   volume's mip 1). Everything is linear, so all mips are plain averages. A cone reads the radiance of what faces it
//   (rgb / coverage along d) and keeps the isotropic occupancy for its opacity.
//
// Passes that know the representation compile it in (LEL_VOXEL_ISO, LEL_VOXEL_ANISO or LEL_VOXEL_SH, see
// VoxelGIPass): the runtime choice of the material shaders costs the resolve a third of its time.

#ifndef LEL_VOXEL_RADIANCE
#define LEL_VOXEL_RADIANCE

uniform sampler3D VoxelRadiance;
uniform sampler3D VoxelRadianceAniso;
uniform sampler3D VoxelRadianceSHX;
uniform sampler3D VoxelRadianceSHY;
uniform sampler3D VoxelRadianceSHZ;
uniform int giVoxelMips;
uniform int giVoxelAnisoLevels;
uniform int voxelResolution;

#if defined(LEL_VOXEL_ANISO)
#define VOXEL_MIPS 1
#elif defined(LEL_VOXEL_SH)
#define VOXEL_MIPS 2
#elif defined(LEL_VOXEL_ISO)
#define VOXEL_MIPS 0
#else
#define VOXEL_MIPS giVoxelMips
#endif

// Slabs weighted below this are skipped (the others renormalized): a cone close to an axis reads one or two slabs.
const float AnisoMinWeight = 0.15;

// What a cone needs from its direction, set up once per cone: its direction, the slab weights and the slab of each axis.
struct VoxelCone
{
	vec3 direction;
	vec3 weights;
	vec3 slabs;
};

VoxelCone VoxelConeSetup(vec3 direction)
{
	vec3 w = direction * direction;
	w *= step(vec3(AnisoMinWeight), w);
	VoxelCone cone;
	cone.direction = direction;
	cone.weights = w / max(w.x + w.y + w.z, 1e-6);
	cone.slabs = vec3(direction.x >= 0.0 ? 0.0 : 1.0, direction.y >= 0.0 ? 2.0 : 3.0, direction.z >= 0.0 ? 4.0 : 5.0);
	return cone;
}

// Coarsest mip a cone may read (in the volume's mip numbering).
float VoxelMaxMip()
{
	return log2(float(voxelResolution));
}

// The directional mips at level lod (0 = the volume's mip 1). The x coordinate stays half a texel of the coarser
// level inside each slab, so the filter never reads the neighbouring slab.
vec4 SampleAnisoLevel(VoxelCone cone, vec3 uvw, float lod)
{
	float texels = max(1.0, float(voxelResolution / 2) * exp2(-ceil(lod)));
	float x = clamp(uvw.x, 0.5 / texels, 1.0 - 0.5 / texels) / 6.0;
	vec4 s = vec4(0.0);
	if (cone.weights.x > 0.0) s += cone.weights.x * textureLod(VoxelRadianceAniso, vec3(x + cone.slabs.x / 6.0, uvw.yz), lod);
	if (cone.weights.y > 0.0) s += cone.weights.y * textureLod(VoxelRadianceAniso, vec3(x + cone.slabs.y / 6.0, uvw.yz), lod);
	if (cone.weights.z > 0.0) s += cone.weights.z * textureLod(VoxelRadianceAniso, vec3(x + cone.slabs.z / 6.0, uvw.yz), lod);
	return s;
}

vec4 SampleAnisoCone(VoxelCone cone, vec3 uvw, float mip)
{
	float anisoTop = float(giVoxelAnisoLevels);
	if (mip >= anisoTop + 1.0)
	{
		return textureLod(VoxelRadiance, uvw, mip);
	}

	if (mip >= 1.0)
	{
		vec4 directional = SampleAnisoLevel(cone, uvw, min(mip - 1.0, anisoTop - 1.0));
		return mip <= anisoTop ? directional : mix(directional, textureLod(VoxelRadiance, uvw, mip), mip - anisoTop);
	}

	// Between the voxels and the first directional level.
	vec4 voxels = textureLod(VoxelRadiance, uvw, 0.0);
	return mip <= 0.0 ? voxels : mix(voxels, SampleAnisoLevel(cone, uvw, 0.0), mip);
}

vec4 SampleSHCone(VoxelCone cone, vec3 uvw, float mip)
{
	vec4 voxels = textureLod(VoxelRadiance, uvw, mip);
	if (voxels.a <= 0.0)
	{
		return vec4(0.0);
	}

	// The facing radiance from one level throughout (the gradients start at the volume's mip 1): a voxel's own value
	// with its 2x2x2 group's gradient would not be a weighted average of the voxels any more (bright seams where
	// surfaces of different colours meet).
	float lod = max(mip - 1.0, 0.0);
	vec4 constant = mip >= 1.0 ? voxels : textureLod(VoxelRadiance, uvw, 1.0);
	vec3 d = cone.direction;
	vec4 facing = constant + d.x * textureLod(VoxelRadianceSHX, uvw, lod) + d.y * textureLod(VoxelRadianceSHY, uvw, lod)
		+ d.z * textureLod(VoxelRadianceSHZ, uvw, lod);
	// Radiance of what faces the cone; nothing but back sides (coverage along d near zero) reads as dark.
	vec3 radiance = max(facing.rgb, vec3(0.0)) / max(facing.a, constant.a * 0.05 + 1e-6);
	return vec4(radiance * voxels.a, voxels.a);
}

// The volume at uvw and mip (0 = voxels) as seen by a cone.
vec4 SampleVoxelCone(VoxelCone cone, vec3 uvw, float mip)
{
	if (VOXEL_MIPS == 1)
	{
		return SampleAnisoCone(cone, uvw, mip);
	}

	if (VOXEL_MIPS == 2)
	{
		return SampleSHCone(cone, uvw, mip);
	}

	return textureLod(VoxelRadiance, uvw, mip);
}

// One sample along a direction (no per-cone setup to share).
vec4 SampleVoxelRadiance(vec3 uvw, float mip, vec3 direction)
{
	return SampleVoxelCone(VoxelConeSetup(direction), uvw, mip);
}

#endif
