//Vertex

#version 430

out vec2 fUV;

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	fUV = pos;
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// Ray-marches the voxel volume from the camera and composites it over the scene (premultiplied alpha).
in vec2 fUV;
out vec4 FragColor;

uniform sampler3D VoxelRadiance;
uniform vec3 voxelGridMin;
uniform float voxelGridSize;
uniform int voxelResolution;
uniform mat4 invViewProjection;
uniform vec3 cameraPosition;
uniform float debugMip;

bool IntersectBox(vec3 ro, vec3 rd, vec3 bmin, vec3 bmax, out float tmin, out float tmax)
{
	vec3 inv = 1.0 / rd;
	vec3 t0 = (bmin - ro) * inv;
	vec3 t1 = (bmax - ro) * inv;
	vec3 tsmall = min(t0, t1);
	vec3 tbig = max(t0, t1);
	tmin = max(max(tsmall.x, tsmall.y), tsmall.z);
	tmax = min(min(tbig.x, tbig.y), tbig.z);
	return tmax > max(tmin, 0.0);
}

void main()
{
	vec2 ndc = fUV * 2.0 - 1.0;
	vec4 farPoint = invViewProjection * vec4(ndc, 1.0, 1.0);
	vec3 target = farPoint.xyz / farPoint.w;
	vec3 rd = normalize(target - cameraPosition);

	float tmin, tmax;
	if (!IntersectBox(cameraPosition, rd, voxelGridMin, voxelGridMin + vec3(voxelGridSize), tmin, tmax)) discard;

	float voxelSize = voxelGridSize / float(voxelResolution) * exp2(debugMip);
	float stepSize = voxelSize * 0.5;
	float t = max(tmin, 0.0) + stepSize * 0.5;

	vec3 color = vec3(0.0);
	float alpha = 0.0;
	for (int i = 0; i < 1024 && t < tmax && alpha < 0.98; i++)
	{
		vec3 uvw = (cameraPosition + rd * t - voxelGridMin) / voxelGridSize;
		vec4 s = textureLod(VoxelRadiance, uvw, debugMip);
		color += (1.0 - alpha) * s.rgb;
		alpha += (1.0 - alpha) * s.a;
		t += stepSize;
	}

	if (alpha < 0.01) discard;
	FragColor = vec4(color, alpha);
}

/////Fragment
