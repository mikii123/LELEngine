//Vertex

#version 430

// Orthographic capture of one mesh into one surface cache card (scaled-local space).
uniform mat4 cardViewProjection;
uniform vec3 objectScale;

in vec3 vPosition;
in vec3 vNormal;
in vec2 vTexCoord;

out vec3 fLocalPos;
out vec3 fLocalNormal;
out vec2 fTexCoord;

void main()
{
	fLocalPos = vPosition * objectScale;
	// Normals transform with the inverse transpose of the scale.
	fLocalNormal = normalize(vNormal / max(objectScale, vec3(1e-5)));
	fTexCoord = vec2(vTexCoord.x, 1.0 - vTexCoord.y);
	gl_Position = cardViewProjection * vec4(fLocalPos, 1.0);
}

/////Vertex

//Fragment

#version 430

in vec3 fLocalPos;
in vec3 fLocalNormal;
in vec2 fTexCoord;

uniform vec4 voxAlbedo;        // material base color
uniform vec4 voxEmissive;      // rgb color, a intensity
uniform sampler2D voxAlbedoMap;
uniform int voxUseAlbedoMap;
uniform int cardIndex;

layout(location = 0) out vec4 OutAlbedo;        // rgb albedo, a = 1 valid
layout(location = 1) out vec4 OutNormal;        // local normal * 0.5 + 0.5
layout(location = 2) out vec4 OutEmissive;      // emitted radiance
layout(location = 3) out vec4 OutLocalPosition; // xyz scaled-local position, w = 1
layout(location = 4) out uint OutCardIndex;

void main()
{
	vec3 albedo = voxAlbedo.rgb;
	if (voxUseAlbedoMap != 0)
	{
		albedo *= texture(voxAlbedoMap, fTexCoord).rgb;
	}

	// Cards look at the object from outside; a back face means the front face was culled by depth,
	// so flip the normal towards the viewer to keep lighting consistent.
	vec3 n = normalize(fLocalNormal);
	if (!gl_FrontFacing) n = -n;

	OutAlbedo = vec4(albedo, 1.0);
	OutNormal = vec4(n * 0.5 + 0.5, 1.0);
	OutEmissive = vec4(voxEmissive.rgb * voxEmissive.a, 1.0);
	OutLocalPosition = vec4(fLocalPos, 1.0);
	OutCardIndex = uint(cardIndex);
}

/////Fragment
