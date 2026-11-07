//Vertex

#version 430

void main()
{
	vec2 pos = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
	gl_Position = vec4(pos * 2.0 - 1.0, 0.0, 1.0);
}

/////Vertex

//Fragment

#version 430

// The opaque scene's depth at the reduced resolution volumetric objects march at (LELEngine.Volumetrics.VolumetricRuntime):
// linear eye depth, of the nearest of the block's pixels on one checkerboard colour and the farthest on the other. At a
// silhouette the march then has both depths among its neighbouring texels, and the composite picks the ones that match
// each full resolution pixel (no halo of the wrong side around objects in front of or inside a volume).

uniform sampler2D volSceneDepth;   // window depth, full resolution
uniform vec4 volDepthParams;       // eye depth = (z * x + y) / (z * z + w), z the NDC depth
uniform int volScale;              // full resolution pixels per low resolution pixel along each axis

out float FragDepth;

void main()
{
	ivec2 low = ivec2(gl_FragCoord.xy);
	ivec2 size = textureSize(volSceneDepth, 0);
	float nearest = 1e30;
	float farthest = 0.0;
	for (int y = 0; y < volScale; y++)
	{
		for (int x = 0; x < volScale; x++)
		{
			ivec2 texel = min(low * volScale + ivec2(x, y), size - 1);
			float z = texelFetch(volSceneDepth, texel, 0).r * 2.0 - 1.0;
			float eye = (z * volDepthParams.x + volDepthParams.y) / (z * volDepthParams.z + volDepthParams.w);
			nearest = min(nearest, eye);
			farthest = max(farthest, eye);
		}
	}

	FragDepth = ((low.x + low.y) & 1) == 0 ? nearest : farthest;
}

/////Fragment
