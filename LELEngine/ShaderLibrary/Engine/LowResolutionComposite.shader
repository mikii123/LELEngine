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

// A half-resolution transparent layer (LELEngine.Rendering.LowResolutionTransparency) laid over the scene: rgb the light
// the layer's draws send, alpha what the scene shows through them. Drawn with Blend One SrcAlpha (scene x alpha + light).
// Each pixel reads the layer's four nearest texels, each weighted by how close the depth that texel was drawn for is to
// the pixel's own: where something stands in front of the layer, the texels drawn for the background behind it count
// for little, so its outline stays sharp.

uniform sampler2D LayerColor;
uniform sampler2D LayerDepth;   // window depth the layer was drawn against
uniform sampler2D SceneDepth;   // window depth of the scene, full resolution
uniform vec4 depthParams;       // linear eye depth = (z * x + y) / (z * z + w), z the NDC depth
uniform vec2 targetSize;

out vec4 FragColor;

float EyeDepth(float windowDepth)
{
	float z = windowDepth * 2.0 - 1.0;
	return (z * depthParams.x + depthParams.y) / (z * depthParams.z + depthParams.w);
}

void main()
{
	// Most of the screen has nothing of the layer: one filtered lookup (the four texels' mean) tells.
	vec2 uv = gl_FragCoord.xy / targetSize;
	vec4 mean = texture(LayerColor, uv);
	if (mean.a > 0.9995 && dot(mean.rgb, vec3(1.0)) < 1e-5)
	{
		discard;
	}

	float eye = EyeDepth(texelFetch(SceneDepth, ivec2(gl_FragCoord.xy), 0).r);
	ivec2 size = textureSize(LayerColor, 0);
	vec2 position = uv * vec2(size) - 0.5;
	ivec2 base = ivec2(floor(position));
	vec2 f = position - vec2(base);
	vec4 sum = vec4(0.0);
	float weights = 0.0;
	for (int y = 0; y <= 1; y++)
	{
		for (int x = 0; x <= 1; x++)
		{
			ivec2 texel = clamp(base + ivec2(x, y), ivec2(0), size - 1);
			float bilinear = (x == 0 ? 1.0 - f.x : f.x) * (y == 0 ? 1.0 - f.y : f.y);
			float depth = EyeDepth(texelFetch(LayerDepth, texel, 0).r);
			float weight = (bilinear + 0.02) / (0.02 + abs(depth - eye) / max(eye, 1e-3));
			sum += texelFetch(LayerColor, texel, 0) * weight;
			weights += weight;
		}
	}

	FragColor = sum / max(weights, 1e-6);
}

/////Fragment
