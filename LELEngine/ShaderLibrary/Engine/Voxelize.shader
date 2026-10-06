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

// Geometry voxelization: writes surface attributes only. Lighting is applied per frame by
// Engine/VoxelInject.shader, so this result can be cached for static geometry.
layout(binding = 0, rgba8) uniform writeonly image3D AlbedoOut;            // rgb albedo, a occupancy
layout(binding = 1, rgba8) uniform writeonly image3D NormalOut;            // xyz normal * 0.5 + 0.5
layout(binding = 2, r11f_g11f_b10f) uniform writeonly image3D EmissiveOut; // rgb emitted radiance

uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;

// Material data injected per draw by VoxelGIPass
uniform vec4 voxAlbedo;
uniform vec4 voxEmissive;      // rgb color, a intensity
uniform sampler2D voxAlbedoMap;
uniform int voxUseAlbedoMap;

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

	imageStore(AlbedoOut, coord, vec4(albedo, 1.0));
	imageStore(NormalOut, coord, vec4(normalize(fNormal) * 0.5 + 0.5, 1.0));
	imageStore(EmissiveOut, coord, vec4(voxEmissive.rgb * voxEmissive.a, 0.0));
}

/////Fragment
