namespace LELEngine.UIElements.Rendering
{
	/// <summary>
	///     The one shader program the UI draws with. Vertices carry a local position, uv, color, the index of their
	///     element and a kind (box, text, textured); the element data buffer holds per-element transforms, clipping,
	///     opacity and box parameters, so transforms, opacity and clipping never regenerate geometry.
	/// </summary>
	internal static class UIShaders
	{
		public const string Vertex = @"
#version 430 core

layout(location = 0) in vec2 aPosition;
layout(location = 1) in vec2 aUV;
layout(location = 2) in vec4 aColor;
layout(location = 3) in uint aElement;
layout(location = 4) in uint aFlags;
layout(location = 5) in float aExtra;

struct ElementData
{
	vec4 transform0;      // m00 m01 tx 0
	vec4 transform1;      // m10 m11 ty 0
	vec4 clipRect;        // panel-space xmin ymin xmax ymax
	vec4 sizeOpacity;     // width height opacity flags
	vec4 radii;           // tl tr br bl
	vec4 borderWidths;    // top right bottom left
	vec4 borderColorTop;
	vec4 borderColorRight;
	vec4 borderColorBottom;
	vec4 borderColorLeft;
	vec4 roundClipRect;   // nearest rounded clipper: x y w h in its local space
	vec4 roundClipRadii;
	vec4 roundClipInv0;   // panel -> clipper local
	vec4 roundClipInv1;
	vec4 textOutlineColor;
	vec4 textParams;      // outline width (px), 0, 0, 0
};

layout(std430, binding = 0) readonly buffer Elements
{
	ElementData elements[];
};

uniform vec2 uTargetSize;   // pixels
uniform float uScale;       // panel units -> pixels

out vec2 fLocal;
out vec2 fPanel;
out vec2 fUV;
out vec4 fColor;
out float fExtra;
flat out uint fElement;
flat out uint fFlags;

void main()
{
	ElementData e = elements[aElement];
	vec2 panel = vec2(e.transform0.x * aPosition.x + e.transform0.y * aPosition.y + e.transform0.z,
	                  e.transform1.x * aPosition.x + e.transform1.y * aPosition.y + e.transform1.z);
	vec2 pixel = panel * uScale;
	vec2 ndc = pixel / uTargetSize * 2.0 - 1.0;
	gl_Position = vec4(ndc.x, -ndc.y, 0.0, 1.0);
	fLocal = aPosition;
	fPanel = panel;
	fUV = aUV;
	fColor = aColor;
	fExtra = aExtra;
	fElement = aElement;
	fFlags = aFlags;
}
";

		public const string Fragment = @"
#version 430 core

struct ElementData
{
	vec4 transform0;
	vec4 transform1;
	vec4 clipRect;
	vec4 sizeOpacity;
	vec4 radii;
	vec4 borderWidths;
	vec4 borderColorTop;
	vec4 borderColorRight;
	vec4 borderColorBottom;
	vec4 borderColorLeft;
	vec4 roundClipRect;
	vec4 roundClipRadii;
	vec4 roundClipInv0;
	vec4 roundClipInv1;
	vec4 textOutlineColor;
	vec4 textParams;
};

layout(std430, binding = 0) readonly buffer Elements
{
	ElementData elements[];
};

uniform sampler2D uTextures[8];
uniform int uTextureIds[8];
uniform float uScale;
uniform float uSdfUnitRange; // 2 * spread / atlas size

in vec2 fLocal;
in vec2 fPanel;
in vec2 fUV;
in vec4 fColor;
in float fExtra;
flat in uint fElement;
flat in uint fFlags;

out vec4 FragColor;

const uint KIND_BOX = 0u;
const uint KIND_TEXT = 1u;
const uint KIND_TEXTURE = 2u;
const uint KIND_TEXT_SHADOW = 3u;
const uint FLAG_CLIP_TO_BOX = 0x10000u;

// Signed distance to a rounded box centered at the origin; radii: tl tr br bl.
float RoundedBox(vec2 p, vec2 halfSize, vec4 radii)
{
	float r = p.x < 0.0 ? (p.y < 0.0 ? radii.x : radii.w) : (p.y < 0.0 ? radii.y : radii.z);
	r = min(r, min(halfSize.x, halfSize.y));
	vec2 q = abs(p) - halfSize + r;
	return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

vec4 SampleSlot(int id, vec2 uv)
{
	if (uTextureIds[0] == id) return texture(uTextures[0], uv);
	if (uTextureIds[1] == id) return texture(uTextures[1], uv);
	if (uTextureIds[2] == id) return texture(uTextures[2], uv);
	if (uTextureIds[3] == id) return texture(uTextures[3], uv);
	if (uTextureIds[4] == id) return texture(uTextures[4], uv);
	if (uTextureIds[5] == id) return texture(uTextures[5], uv);
	if (uTextureIds[6] == id) return texture(uTextures[6], uv);
	return texture(uTextures[7], uv);
}

void main()
{
	ElementData e = elements[fElement];

	// Axis-aligned clip in panel space, anti-aliased at the edge.
	vec4 cr = e.clipRect;
	float dClip = min(min(fPanel.x - cr.x, cr.z - fPanel.x), min(fPanel.y - cr.y, cr.w - fPanel.y)) * uScale;
	float clipCoverage = clamp(dClip + 0.5, 0.0, 1.0);
	if (clipCoverage <= 0.0)
	{
		discard;
	}

	uint flags = floatBitsToUint(e.sizeOpacity.w);
	if ((flags & 1u) != 0u)
	{
		// Rounded (or rotated) clipper: test in its local space.
		vec2 lp = vec2(e.roundClipInv0.x * fPanel.x + e.roundClipInv0.y * fPanel.y + e.roundClipInv0.z,
		               e.roundClipInv1.x * fPanel.x + e.roundClipInv1.y * fPanel.y + e.roundClipInv1.z);
		vec2 halfSize = e.roundClipRect.zw * 0.5;
		float d = RoundedBox(lp - e.roundClipRect.xy - halfSize, halfSize, e.roundClipRadii);
		float localScale = uScale * length(vec2(e.roundClipInv0.x, e.roundClipInv1.x));
		localScale = localScale > 0.0 ? 1.0 / localScale : uScale;
		clipCoverage *= clamp(0.5 - d * localScale, 0.0, 1.0);
	}

	uint kind = fFlags & 0xFFu;
	int textureId = int((fFlags >> 8u) & 0xFFu);
	float pixelsPerLocal = uScale * length(vec2(e.transform0.x, e.transform1.x));
	vec4 result;

	if (kind == KIND_BOX)
	{
		vec2 size = e.sizeOpacity.xy;
		vec2 halfSize = size * 0.5;
		vec2 c = fLocal - halfSize;
		float dOuter = RoundedBox(c, halfSize, e.radii);
		float coverage = clamp(0.5 - dOuter * pixelsPerLocal, 0.0, 1.0);

		vec4 bw = e.borderWidths; // top right bottom left
		vec2 innerMin = vec2(bw.w, bw.x);
		vec2 innerMax = size - vec2(bw.y, bw.z);
		vec2 innerHalf = max((innerMax - innerMin) * 0.5, vec2(0.0));
		vec2 innerCenter = (innerMin + innerMax) * 0.5;
		vec4 innerRadii = max(e.radii - vec4(max(bw.x, bw.w), max(bw.x, bw.y), max(bw.z, bw.y), max(bw.z, bw.w)), vec4(0.0));
		float dInner = RoundedBox(fLocal - innerCenter, innerHalf, innerRadii);
		float innerCoverage = clamp(0.5 - dInner * pixelsPerLocal, 0.0, 1.0);
		float ring = max(coverage - innerCoverage, 0.0);

		vec4 background = fColor;
		if (textureId != 0)
		{
			background *= SampleSlot(textureId, fUV);
		}

		vec3 rgb = background.rgb * background.a * coverage;
		float a = background.a * coverage;

		if (ring > 0.0)
		{
			// Side by distance to the edge relative to that edge's border width (diagonal joins at the corners).
			vec4 widths = max(bw, vec4(1e-4));
			vec4 rel = vec4(fLocal.y, size.x - fLocal.x, size.y - fLocal.y, fLocal.x) / widths;
			vec4 border = e.borderColorTop;
			float best = rel.x;
			if (rel.y < best) { best = rel.y; border = e.borderColorRight; }
			if (rel.z < best) { best = rel.z; border = e.borderColorBottom; }
			if (rel.w < best) { best = rel.w; border = e.borderColorLeft; }
			float ba = border.a * ring;
			rgb = border.rgb * ba + rgb * (1.0 - ba);
			a = ba + a * (1.0 - ba);
		}

		result = vec4(rgb, a);
	}
	else if (kind == KIND_TEXT || kind == KIND_TEXT_SHADOW)
	{
		float sd = SampleSlot(textureId, fUV).r;
		vec2 unitRange = vec2(uSdfUnitRange);
		vec2 screenTexSize = vec2(1.0) / fwidth(fUV);
		float pxRange = max(0.5 * dot(unitRange, screenTexSize), 1.0);
		vec4 color = fColor;
		float alpha;
		if (kind == KIND_TEXT)
		{
			float sdf = sd - 0.5 + fExtra; // fExtra: synthesized bold
			alpha = clamp(sdf * pxRange + 0.5, 0.0, 1.0);
			float outlineWidth = e.textParams.x;
			if (outlineWidth > 0.0)
			{
				float outlineAlpha = clamp((sdf * pxRange + outlineWidth * uScale) + 0.5, 0.0, 1.0);
				vec4 outline = e.textOutlineColor;
				color = vec4(mix(outline.rgb, color.rgb, alpha), max(alpha * color.a, outlineAlpha * outline.a));
				alpha = 1.0;
			}
		}
		else
		{
			// Shadow: softened edge by the blur radius (fExtra, px).
			float blur = max(fExtra * uScale, 0.0);
			float d = (sd - 0.5) * pxRange;
			alpha = clamp((d + 0.5 + blur) / (1.0 + 2.0 * blur), 0.0, 1.0);
		}

		result = vec4(color.rgb * color.a * alpha, color.a * alpha);
	}
	else
	{
		vec4 color = fColor;
		if (textureId != 0)
		{
			color *= SampleSlot(textureId, fUV);
		}

		float coverage = 1.0;
		if ((fFlags & FLAG_CLIP_TO_BOX) != 0u)
		{
			vec2 size = e.sizeOpacity.xy;
			vec2 halfSize = size * 0.5;
			coverage = clamp(0.5 - RoundedBox(fLocal - halfSize, halfSize, e.radii) * pixelsPerLocal, 0.0, 1.0);
		}

		result = vec4(color.rgb * color.a * coverage, color.a * coverage);
	}

	result *= e.sizeOpacity.z * clipCoverage;
	FragColor = result;
}
";
	}
}
