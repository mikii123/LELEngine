// Surface cache lookup: Lumen's cards. Each object has up to six axis-aligned cards whose texels hold
// the captured surface (albedo, normal, emission, local position) and the cached lighting. A world hit
// point is projected onto the cards that face its normal; the stored position must agree with the hit
// (depth test) or the card is skipped, so the lighting of the wrong side of a thin wall is never read.
//
// Requires Engine/SceneObjects.glsl.

uniform vec2 surfaceCacheAtlasSize;   // texels
uniform sampler2D CardLocalPosition;  // xyz scaled-local position of the captured surface, w = 1 where valid

// Weighted lighting of the given atlas (final or previous final lighting) at a local point of an object.
vec3 SampleSurfaceCache(int objectIndex, vec3 localPos, vec3 localNormal, sampler2D lighting)
{
	SceneObject o = sceneObjects[objectIndex];

	vec3 sum = vec3(0.0);
	float weightSum = 0.0;
	vec3 relaxedSum = vec3(0.0);
	float relaxedWeightSum = 0.0;

	for (int i = 0; i < o.cardInfo.y; i++)
	{
		Card c = cards[o.cardInfo.x + i];
		float facing = dot(localNormal, c.axisZ.xyz);
		if (facing <= 0.05) continue;

		vec3 d = localPos - c.origin.xyz;
		vec2 uv = vec2(dot(d, c.axisX.xyz) / c.axisX.w, dot(d, c.axisY.xyz) / c.axisY.w) + 0.5;
		if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) continue;

		// Stay half a texel inside the card so bilinear filtering never reads a neighbouring card.
		vec2 size = vec2(c.rect.zw);
		vec2 texelUV = (vec2(c.rect.xy) + clamp(uv * size, vec2(0.5), size - 0.5)) / surfaceCacheAtlasSize;

		vec4 stored = texture(CardLocalPosition, texelUV);
		if (stored.w < 0.5) continue;

		float weight = facing * facing;
		vec3 radiance = texture(lighting, texelUV).rgb;

		// Depth test: the captured surface must be where the hit is, within a couple of card texels.
		float storedDepth = dot(stored.xyz - c.origin.xyz, c.axisZ.xyz);
		float depth = dot(d, c.axisZ.xyz);
		float texelWorld = max(c.axisX.w / size.x, c.axisY.w / size.y);
		if (abs(storedDepth - depth) <= 2.0 * texelWorld + 0.05)
		{
			sum += radiance * weight;
			weightSum += weight;
		}
		relaxedSum += radiance * weight;
		relaxedWeightSum += weight;
	}

	if (weightSum > 0.0) return sum / weightSum;
	if (relaxedWeightSum > 0.0) return relaxedSum / relaxedWeightSum;
	return vec3(0.0);
}

// Surface cache lighting at a world hit point found by the global distance field.
// hitNormal is the SDF normal (world). Returns false when no object is close enough.
bool SampleSurfaceCacheAtHit(vec3 hitPos, vec3 hitNormal, float searchDistance, sampler2D lighting, out vec3 radiance)
{
	vec3 localPos;
	int objectIndex = SceneObjectAtPoint(hitPos, searchDistance, localPos);
	if (objectIndex < 0)
	{
		radiance = vec3(0.0);
		return false;
	}

	vec3 localNormal = SceneObjectWorldToLocalDir(sceneObjects[objectIndex], hitNormal);
	radiance = SampleSurfaceCache(objectIndex, localPos, localNormal, lighting);
	return true;
}
