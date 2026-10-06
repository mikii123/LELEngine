//Vertex

#version 430

uniform mat4 modelMatrix;

in vec3 vPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec3 gWorldPos;
out vec3 gNormal;
out vec2 gUV;

void main()
{
	gWorldPos = vec3(modelMatrix * vec4(vPosition, 1.0));
	gNormal = normalize(mat3(transpose(inverse(modelMatrix))) * vNormal);
	gUV = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
}

/////Vertex

//Geometry

#version 430

// Projects each triangle along its dominant axis so it covers the most voxels,
// mapping the voxel grid to clip space [-1, 1].
layout(triangles) in;
layout(triangle_strip, max_vertices = 3) out;

uniform vec3 voxelGridMin;
uniform float voxelGridSize;

in vec3 gWorldPos[];
in vec3 gNormal[];
in vec2 gUV[];

out vec3 fWorldPos;
out vec3 fNormal;
out vec2 fUV;

void main()
{
	vec3 faceNormal = abs(cross(gWorldPos[1] - gWorldPos[0], gWorldPos[2] - gWorldPos[0]));
	int axis = (faceNormal.x > faceNormal.y && faceNormal.x > faceNormal.z) ? 0 : (faceNormal.y > faceNormal.z ? 1 : 2);

	for (int i = 0; i < 3; i++)
	{
		vec3 p = (gWorldPos[i] - voxelGridMin) / voxelGridSize * 2.0 - 1.0;
		vec2 projected = axis == 0 ? p.yz : (axis == 1 ? p.xz : p.xy);

		gl_Position = vec4(projected, 0.0, 1.0);
		fWorldPos = gWorldPos[i];
		fNormal = gNormal[i];
		fUV = gUV[i];
		EmitVertex();
	}
	EndPrimitive();
}

/////Geometry

//Fragment

#version 430

#include "Engine/Shadows.glsl"

layout(binding = 0, rgba16f) uniform writeonly image3D VoxelOutput;

uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;

// Material data injected per draw by VoxelGIPass
uniform vec4 voxAlbedo;
uniform vec4 voxEmissive;      // rgb color, a intensity
uniform sampler2D voxAlbedoMap;
uniform int voxUseAlbedoMap;

struct Directional {
	vec4 dirColor;
	float dirStrength;
	vec3 dirDirection;
};
uniform Directional LDirectional;

in vec3 fWorldPos;
in vec3 fNormal;
in vec2 fUV;

void main()
{
	vec3 uvw = (fWorldPos - voxelGridMin) / voxelGridSize;
	if (any(lessThan(uvw, vec3(0.0))) || any(greaterThanEqual(uvw, vec3(1.0)))) discard;
	ivec3 coord = ivec3(uvw * float(voxelResolution));

	vec3 albedo = voxAlbedo.rgb;
	if (voxUseAlbedoMap != 0)
	{
		albedo *= texture(voxAlbedoMap, fUV).rgb;
	}

	vec3 N = normalize(fNormal);
	vec3 L = normalize(-LDirectional.dirDirection);
	float ndl = max(dot(N, L), 0.0);
	float shadow = SampleShadow(fWorldPos, N, L);

	// Outgoing radiance: directly lit diffuse plus emission.
	vec3 radiance = albedo * LDirectional.dirColor.rgb * LDirectional.dirStrength * ndl * shadow
		+ voxEmissive.rgb * voxEmissive.a;

	imageStore(VoxelOutput, coord, vec4(radiance, 1.0));
}

/////Fragment
