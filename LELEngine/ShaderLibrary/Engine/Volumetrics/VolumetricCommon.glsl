// What the volumetric objects' drawing passes share (LELEngine.Volumetrics.VolumetricRuntime): the view ray of a pixel
// taken into the object's local space, and where it crosses the object's shape. Uniforms are set per object and pass.
#ifndef LEL_VOLUMETRIC_COMMON_GLSL
#define LEL_VOLUMETRIC_COMMON_GLSL

uniform mat4 volInvViewProjection; // world from clip, of the camera drawing
uniform vec3 volCameraPosition;    // world
uniform vec3 volCameraForward;     // world, unit length
uniform mat4 volWorldToLocal;      // the object's
uniform vec3 volSize;              // the shape's extents (local units)
uniform int volShape;              // 0 box, 1 cylinder (axis y), 2 sphere (ellipsoid)
uniform vec2 volTargetSize;        // pixels of the target the pass draws into

// A pixel's view ray: its world direction, and in local space its origin (the camera), its unit direction and how many
// local units one world unit along it is (a scaled object stretches its local units).
struct VolRay
{
	vec3 worldDir;
	vec3 origin;
	vec3 dir;
	float scale;
};

VolRay VolMakeRay(vec2 pixel)
{
	vec2 ndc = pixel / volTargetSize * 2.0 - 1.0;
	vec4 farPoint = volInvViewProjection * vec4(ndc, 1.0, 1.0);
	VolRay ray;
	ray.worldDir = normalize(farPoint.xyz / farPoint.w - volCameraPosition);
	ray.origin = (volWorldToLocal * vec4(volCameraPosition, 1.0)).xyz;
	vec3 d = (volWorldToLocal * vec4(ray.worldDir, 0.0)).xyz;
	ray.scale = max(length(d), 1e-12);
	ray.dir = d / ray.scale;
	return ray;
}

// The part of a local ray (unit direction) inside the shape: x where it enters, y where it leaves (y <= x: it misses).
vec2 VolIntersect(vec3 origin, vec3 dir)
{
	vec3 halfSize = volSize * 0.5;
	if (volShape == 0)
	{
		vec3 safe = mix(dir, vec3(1e-12), lessThan(abs(dir), vec3(1e-12)));
		vec3 t0 = (-halfSize - origin) / safe;
		vec3 t1 = (halfSize - origin) / safe;
		vec3 tNear = min(t0, t1);
		vec3 tFar = max(t0, t1);
		return vec2(max(max(tNear.x, tNear.y), tNear.z), min(min(tFar.x, tFar.y), tFar.z));
	}

	if (volShape == 2)
	{
		vec3 o = origin / halfSize;
		vec3 d = dir / halfSize;
		float a = dot(d, d);
		float b = dot(o, d);
		float c = dot(o, o) - 1.0;
		float disc = b * b - a * c;
		if (disc < 0.0)
		{
			return vec2(1.0, 0.0);
		}

		float root = sqrt(disc);
		return vec2((-b - root) / a, (-b + root) / a);
	}

	// Cylinder: the round side (an ellipse across x and z), then the top and the bottom.
	vec2 o = origin.xz / halfSize.xz;
	vec2 d = dir.xz / halfSize.xz;
	float enter = -1e30;
	float leave = 1e30;
	float a = dot(d, d);
	float b = dot(o, d);
	float c = dot(o, o) - 1.0;
	if (a > 1e-12)
	{
		float disc = b * b - a * c;
		if (disc < 0.0)
		{
			return vec2(1.0, 0.0);
		}

		float root = sqrt(disc);
		enter = (-b - root) / a;
		leave = (-b + root) / a;
	}
	else if (c > 0.0)
	{
		return vec2(1.0, 0.0);
	}

	if (abs(dir.y) > 1e-12)
	{
		float ta = (-halfSize.y - origin.y) / dir.y;
		float tb = (halfSize.y - origin.y) / dir.y;
		enter = max(enter, min(ta, tb));
		leave = min(leave, max(ta, tb));
	}
	else if (abs(origin.y) > halfSize.y)
	{
		return vec2(1.0, 0.0);
	}

	return vec2(enter, leave);
}

// Linear eye depth (world units in front of the camera) of a window depth, from the projection's depth rows
// (SceneGrab.DepthParams): (z * x + y) / (z * z + w) with z the NDC depth.
float VolEyeDepth(float windowDepth, vec4 depthParams)
{
	float z = windowDepth * 2.0 - 1.0;
	return (z * depthParams.x + depthParams.y) / (z * depthParams.z + depthParams.w);
}

// Interleaved gradient noise: a well spread per pixel value 0..1 (jitters the march start).
float VolNoise(vec2 pixel)
{
	return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

#endif
