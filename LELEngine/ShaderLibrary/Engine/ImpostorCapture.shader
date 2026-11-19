//Vertex

#version 430

// The impostor bake's capture of a material whose shader has no surface capture variant of its own (SurfaceCapture
// On, LEL_SURFACE_CAPTURE): the surface as the GI's captures see it (Engine/GISurface.glsl: the conventional values
// Color, DiffuseMap, Emissive, EmissionMap), with the mesh's own normals.
uniform mat4 projectionMatrix;
uniform mat4 viewMatrix;
uniform mat4 modelMatrix;

in vec3 vPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec3 fNormal;
out vec2 fTexCoord;

void main()
{
	gl_Position = projectionMatrix * viewMatrix * modelMatrix * vec4(vPosition, 1.0);
	fNormal = mat3(transpose(inverse(modelMatrix))) * vNormal;
	fTexCoord = vTexCoord;
}

/////Vertex

//Fragment

#version 430

#include "Engine/GISurface.glsl"

uniform float captureRoughness;

in vec3 fNormal;
in vec2 fTexCoord;

// The outputs of ShaderPass.SurfaceCapture.
layout(location = 0) out vec4 CaptureDiffuse;
layout(location = 1) out vec4 CaptureNormal;
layout(location = 2) out vec4 CaptureSpecular;
layout(location = 3) out vec4 CaptureEmission;

void main()
{
	// A face seen from behind is lit as from its front.
	vec3 normal = normalize(fNormal);
	if (!gl_FrontFacing)
	{
		normal = -normal;
	}

	CaptureDiffuse = vec4(LelGIAlbedo(voxAlbedo.rgb, fTexCoord), 1.0);
	CaptureNormal = vec4(normal, 1.0);
	CaptureSpecular = vec4(0.0, 0.0, 0.0, 1.0 - captureRoughness);
	CaptureEmission = vec4(LelGIEmission(fTexCoord) * voxEmissive.a, 1.0);
}

/////Fragment
