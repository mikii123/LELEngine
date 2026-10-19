// Instance and material tables of GPU-driven rendering (LELEngine.Rendering.GpuScene).
//
// A shader that includes this file (in each stage that needs it) and uses the Lel* accessors draws in both modes:
//  - per object (default): the classic uniforms modelMatrix, Color, Emissive and Roughness, set per draw;
//  - instanced variant (LEL_INSTANCED, compiled by the engine): multi-draw indirect. The instance index is the draw's
//    base instance (gl_BaseInstance with shader draw parameters, else a per-instance vertex attribute fed from an
//    identity buffer) and indexes the instance table; the material table holds the standard material parameters.
// Vertex stages call LelVertexSetup() so later stages can read the material.

#ifndef LEL_INSTANCING_GLSL
#define LEL_INSTANCING_GLSL

struct LelInstance
{
	mat4 model;
	mat4 normalMatrix; // transpose(inverse(model))
	vec4 boundsMin;    // local-space bounds of the mesh
	vec4 boundsMax;
	uvec4 draw;        // index count, first index, base vertex, flags (bit 0: casts shadows)
	uvec4 refs;        // material index, opaque bucket, layer bit (1 << the object's layer), unused
};

struct LelMaterial
{
	vec4 color;
	vec4 emissive;     // rgb color, a intensity
	vec4 params;       // x roughness (0 = shader default)
};

#if defined(LEL_INSTANCED) || defined(LEL_STAGE_COMPUTE)
layout(std430, binding = 10) readonly buffer LelInstanceBuffer { LelInstance lelInstances[]; };
layout(std430, binding = 11) readonly buffer LelMaterialBuffer { LelMaterial lelMaterials[]; };
#endif

#ifdef LEL_STAGE_VERTEX
#ifdef LEL_INSTANCED
#ifdef LEL_BASE_INSTANCE
#define LEL_INSTANCE_INDEX (uint(LEL_BASE_INSTANCE) + uint(gl_InstanceID))
#else
in uint vInstanceIndex;
#define LEL_INSTANCE_INDEX vInstanceIndex
#endif
// The material index travels to the fragment stage, so a fragment reads the material table once (no instance read).
flat out uint lelInstance;
flat out uint lelMaterialIndex;
mat4 LelModelMatrix() { return lelInstances[LEL_INSTANCE_INDEX].model; }
mat3 LelNormalMatrix() { return mat3(lelInstances[LEL_INSTANCE_INDEX].normalMatrix); }
void LelVertexSetup()
{
	lelInstance = LEL_INSTANCE_INDEX;
	lelMaterialIndex = lelInstances[LEL_INSTANCE_INDEX].refs.x;
}
// Last frame's world matrix (motion vectors): a shader that needs it defines LEL_PREVIOUS_MODELS before the include;
// LEL_NO_PREVIOUS_MODELS (no free binding on this context) makes it the current one.
#ifdef LEL_PREVIOUS_MODELS
#ifdef LEL_NO_PREVIOUS_MODELS
mat4 LelPreviousModelMatrix() { return lelInstances[LEL_INSTANCE_INDEX].model; }
#else
layout(std430, binding = 15) readonly buffer LelPreviousModelBuffer { mat4 lelPreviousModels[]; };
mat4 LelPreviousModelMatrix() { return lelPreviousModels[LEL_INSTANCE_INDEX]; }
#endif
#endif
#else
uniform mat4 modelMatrix;
mat4 LelModelMatrix() { return modelMatrix; }
mat3 LelNormalMatrix() { return mat3(transpose(inverse(modelMatrix))); }
void LelVertexSetup() { }
#ifdef LEL_PREVIOUS_MODELS
uniform mat4 previousModelMatrix;
mat4 LelPreviousModelMatrix() { return previousModelMatrix; }
#endif
#endif
#endif

#ifdef LEL_STAGE_FRAGMENT
// Read the material once per fragment (LelMaterialData) when several parameters are needed.
#ifdef LEL_INSTANCED
flat in uint lelInstance;
flat in uint lelMaterialIndex;
LelMaterial LelMaterialData() { return lelMaterials[lelMaterialIndex]; }
#else
uniform vec4 Color;
uniform vec4 Emissive;
uniform float Roughness;
LelMaterial LelMaterialData() { return LelMaterial(Color, Emissive, vec4(Roughness, 0.0, 0.0, 0.0)); }
#endif
vec4 LelColor() { return LelMaterialData().color; }
vec4 LelEmissive() { return LelMaterialData().emissive; }
float LelRoughness() { return LelMaterialData().params.x; }
#endif

#endif
