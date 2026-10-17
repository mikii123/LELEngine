// Shared declarations of the generated VFX programs (LELEngine.VFX.VfxCodegen): the system's parameter block, the
// particle buffers (declared only where a kernel asks for them with VFX_USE_* defines), random numbers, curve and
// gradient sampling, orientation helpers and the depth buffer access of collisions and soft particles.
// The generated code defines VFX_UNIFORM_COUNT, VFX_MATRIX_COUNT, VFX_CAPACITY and VFX_STRIP_LENGTH before including it.
#ifndef VFX_COMMON_GLSL
#define VFX_COMMON_GLSL

// ---------------------------------------------------------------- parameters (VfxUniformTable)

// One range of the runtime's shared parameter buffer per system (bound with glBindBufferRange).
layout(std140, binding = 1) uniform VfxParams
{
	vec4 vfxU[VFX_UNIFORM_COUNT];
	mat4 vfxM[VFX_MATRIX_COUNT];
};

#define vfxDeltaTime (vfxU[0].x)
#define vfxTotalTime (vfxU[0].y)
#define vfxPlayRate (vfxU[0].z)
#define vfxFrameIndex floatBitsToUint(vfxU[0].w)
#define vfxSpawnCountUniform floatBitsToUint(vfxU[1].x)
#define vfxSpawnedTotal floatBitsToUint(vfxU[1].y)
#define vfxSystemSeed floatBitsToUint(vfxU[1].z)
#define vfxCapacity floatBitsToUint(vfxU[1].w)
#define vfxCounterBase floatBitsToUint(vfxU[7].x)
#define vfxDenseUpdate (floatBitsToUint(vfxU[7].y) != 0u)
#define vfxLocalToWorld (vfxM[0])
#define vfxWorldToLocal (vfxM[1])

#ifdef VFX_DRAW
// Draws face and fade towards the view that draws them (the camera, or the light of a shadow map): one block per
// view, shared by every particle draw of the view.
layout(std140, binding = 2) uniform VfxView
{
	vec4 vfxViewPosition;
	vec4 vfxViewForward;
	vec4 vfxViewUp;
	vec4 vfxViewRight;
};
#define vfxCameraPosition (vfxViewPosition.xyz)
#define vfxCameraForward (vfxViewForward.xyz)
#define vfxCameraUp (vfxViewUp.xyz)
#define vfxCameraRight (vfxViewRight.xyz)
#define vfxSortPosition (vfxViewPosition.xyz)
#else
// The simulation sees the main camera of the frame.
#define vfxCameraPosition (vfxU[2].xyz)
#define vfxCameraForward (vfxU[3].xyz)
#define vfxCameraUp (vfxU[4].xyz)
#define vfxCameraRight (vfxU[5].xyz)
#define vfxSortPosition (vfxU[6].xyz)
#endif

// ---------------------------------------------------------------- the system's counters (VfxCodegen.Counter*)

// Every system owns a block of uints in the runtime's counter pool: dispatch and draw arguments the GPU writes
// for itself, the list sizes and the bounds. The frame kernel (VfxFrame.shader) prepares them, the initialize
// kernel reads them, the update kernel fills the next frame's.
#define VFX_C_INIT_DISPATCH 0u
#define VFX_C_SPAWN 3u
#define VFX_C_UPDATE_DISPATCH 4u
#define VFX_C_ALIVE_IN 7u
#define VFX_C_ALIVE_OUT 8u
#define VFX_C_DEAD 9u
#define VFX_C_INIT_ALIVE_BASE 10u
#define VFX_C_INIT_DEAD_TOP 11u
#define VFX_C_BOUNDS 12u
#define VFX_C_ID_NEXT 18u
#define VFX_C_ID_BASE 19u
#define VFX_C_EVENTS 20u
#define VFX_C_DRAW 24u
#define VFX_C_DRAW_STRIDE 5u

// ---------------------------------------------------------------- buffers

#ifdef VFX_USE_ATTRIBUTES
layout(std430, binding = 0) buffer VfxAttributeBuffer { float vfxAttr[]; };
#endif
#ifdef VFX_USE_ALIVE_IN
// The particles the update reads this frame (last frame's survivors, then the ones initialized this frame).
layout(std430, binding = 1) buffer VfxAliveInBuffer { uint vfxAliveIn[]; };
#endif
#ifdef VFX_USE_ALIVE_OUT
// The survivors of this frame's update: next frame's input and this frame's draw list.
layout(std430, binding = 2) writeonly buffer VfxAliveOutBuffer { uint vfxAliveOut[]; };
#endif
#ifdef VFX_USE_DEAD
// Free particle slots, a stack: the initialize pops from the top, the update pushes the slots of the dead.
layout(std430, binding = 3) buffer VfxDeadBuffer { uint vfxDead[]; };
#endif
#ifdef VFX_USE_COUNTERS
layout(std430, binding = 4) coherent buffer VfxCounterBuffer { uint vfxCounters[]; };
#define VFX_COUNTER(i) vfxCounters[vfxCounterBase + (i)]
#endif
#ifdef VFX_USE_CURVES
layout(std430, binding = 5) readonly buffer VfxCurveBuffer { vec4 vfxCurves[]; };
#endif
#ifdef VFX_USE_DRAWLIST
// The alive list (stride 1) or the sorted (key, particle) pairs (stride 2) of the draw.
layout(std430, binding = 1) readonly buffer VfxDrawListBuffer { uint vfxDrawList[]; };
#endif
#ifdef VFX_USE_ALIVE_MASK
// 1 for every slot holding a live particle: a dense update walks the slots in order instead of the alive list.
layout(std430, binding = 12) buffer VfxAliveMaskBuffer { uint vfxAliveMask[]; };
#endif
#ifdef VFX_USE_STRIP
layout(std430, binding = 10) buffer VfxStripBuffer { vec4 vfxStrip[]; };
#endif

// GPU events: what a parent particle hands to the particles it spawns in a child system.
struct VfxEventPayload
{
	vec4 positionSize;     // xyz position, w size
	vec4 velocityLifetime; // xyz velocity, w lifetime
	vec4 colorAlpha;       // rgb color, a alpha
	uvec4 seedIdAge;       // seed, particle id, age (float bits), unused
};

#ifdef VFX_USE_EVENT_IN
layout(std430, binding = 6) readonly buffer VfxEventInBuffer { VfxEventPayload vfxEventIn[]; };
#endif

// A particle raising an event only records it (count and payload); the update kernel reserves the room for the
// whole work group with one atomic and writes the payloads afterwards.
#ifdef VFX_USE_EVENT_OUT0
layout(std430, binding = 7) writeonly buffer VfxEventOut0Buffer { VfxEventPayload vfxEventOut0[]; };
uint vfxEvent0Count = 0u;
VfxEventPayload vfxEvent0Payload;
void VfxEmitEvent0(uint count, vec3 position, vec3 velocity, vec3 color, float alpha, float size, float lifetime, float age, uint seed, uint id)
{
	vfxEvent0Count += count;
	vfxEvent0Payload = VfxEventPayload(vec4(position, size), vec4(velocity, lifetime), vec4(color, alpha), uvec4(seed, id, floatBitsToUint(age), 0u));
}
#endif

#ifdef VFX_USE_EVENT_OUT1
layout(std430, binding = 8) writeonly buffer VfxEventOut1Buffer { VfxEventPayload vfxEventOut1[]; };
uint vfxEvent1Count = 0u;
VfxEventPayload vfxEvent1Payload;
void VfxEmitEvent1(uint count, vec3 position, vec3 velocity, vec3 color, float alpha, float size, float lifetime, float age, uint seed, uint id)
{
	vfxEvent1Count += count;
	vfxEvent1Payload = VfxEventPayload(vec4(position, size), vec4(velocity, lifetime), vec4(color, alpha), uvec4(seed, id, floatBitsToUint(age), 0u));
}
#endif

#ifdef VFX_USE_MESH
// The geometry pool's vertex buffer (LELEngine.Rendering.GeometryPool): position(3) color(4) uv(2) normal(3) tangent(3) bitangent(3).
layout(std430, binding = 9) readonly buffer VfxMeshVertexBuffer { float vfxMeshVertices[]; };
uniform uvec4 vfxMeshInfo[4]; // base vertex, vertex count, first index, index count

vec3 VfxMeshPosition(uint slot, uint index)
{
	uvec4 info = vfxMeshInfo[slot];
	if (info.y == 0u) return vec3(0.0);
	uint o = (info.x + index % info.y) * 18u;
	return vec3(vfxMeshVertices[o], vfxMeshVertices[o + 1u], vfxMeshVertices[o + 2u]);
}

vec3 VfxMeshNormal(uint slot, uint index)
{
	uvec4 info = vfxMeshInfo[slot];
	if (info.y == 0u) return vec3(0.0, 1.0, 0.0);
	uint o = (info.x + index % info.y) * 18u + 9u;
	return vec3(vfxMeshVertices[o], vfxMeshVertices[o + 1u], vfxMeshVertices[o + 2u]);
}

vec2 VfxMeshUv(uint slot, uint index)
{
	uvec4 info = vfxMeshInfo[slot];
	if (info.y == 0u) return vec2(0.0);
	uint o = (info.x + index % info.y) * 18u + 7u;
	return vec2(vfxMeshVertices[o], vfxMeshVertices[o + 1u]);
}

uint VfxMeshVertexCount(uint slot)
{
	return vfxMeshInfo[slot].y;
}
#endif

// ---------------------------------------------------------------- scene depth (collisions, soft particles)

#ifdef VFX_USE_DEPTH
uniform sampler2D vfxSceneDepth;
uniform mat4 vfxDepthViewProjection;
uniform mat4 vfxDepthInvViewProjection;
uniform vec4 vfxDepthParams; // width, height, available (1 / 0), unused

bool VfxDepthAvailable()
{
	return vfxDepthParams.z > 0.5;
}

float VfxSampleSceneDepth(vec2 uv)
{
	return texture(vfxSceneDepth, uv).r;
}

vec3 VfxDepthWorldPosition(vec2 uv, float depth)
{
	vec4 clip = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
	vec4 world = vfxDepthInvViewProjection * clip;
	return world.xyz / world.w;
}

// Projects a world position into the depth map; false when it falls outside the view.
bool VfxProjectToDepth(vec3 world, out vec2 uv, out float depth)
{
	vec4 clip = vfxDepthViewProjection * vec4(world, 1.0);
	uv = vec2(0.0);
	depth = 1.0;
	if (clip.w <= 1e-5) return false;
	vec3 ndc = clip.xyz / clip.w;
	uv = ndc.xy * 0.5 + 0.5;
	depth = ndc.z * 0.5 + 0.5;
	return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0)));
}

// Surface normal from the depth of the neighbouring pixels.
vec3 VfxDepthNormal(vec2 uv)
{
	vec2 texel = 1.0 / max(vfxDepthParams.xy, vec2(1.0));
	vec3 c = VfxDepthWorldPosition(uv, VfxSampleSceneDepth(uv));
	vec3 r = VfxDepthWorldPosition(uv + vec2(texel.x, 0.0), VfxSampleSceneDepth(uv + vec2(texel.x, 0.0)));
	vec3 u = VfxDepthWorldPosition(uv + vec2(0.0, texel.y), VfxSampleSceneDepth(uv + vec2(0.0, texel.y)));
	vec3 n = cross(r - c, u - c);
	float len = length(n);
	return len > 1e-8 ? n / len : vec3(0.0, 1.0, 0.0);
}

// 1 where the fragment is well in front of the scene, 0 where it touches or sinks into it.
float VfxSoftParticleFade(vec2 fragCoord, float fragmentViewDepth, float fadeDistance)
{
	if (!VfxDepthAvailable()) return 1.0;
	vec2 uv = fragCoord / max(vfxDepthParams.xy, vec2(1.0));
	float depth = VfxSampleSceneDepth(uv);
	if (depth >= 1.0) return 1.0;
	vec3 world = VfxDepthWorldPosition(uv, depth);
	float sceneDepth = dot(world - vfxCameraPosition, vfxCameraForward);
	return clamp((sceneDepth - fragmentViewDepth) / max(fadeDistance, 1e-4), 0.0, 1.0);
}
#endif

// ---------------------------------------------------------------- random (the C# side mirrors VfxPcg / VfxRandom01)

uint VfxPcg(uint v)
{
	uint state = v * 747796405u + 2891336453u;
	uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
	return (word >> 22u) ^ word;
}

float VfxRandom01(uint seed, uint salt)
{
	return float(VfxPcg(seed ^ VfxPcg(salt + 0x9E3779B9u))) * (1.0 / 4294967296.0);
}

vec2 VfxRandom2(uint seed, uint salt)
{
	return vec2(VfxRandom01(seed, salt), VfxRandom01(seed, salt ^ 0x68BC21EBu));
}

vec3 VfxRandom3(uint seed, uint salt)
{
	return vec3(VfxRandom01(seed, salt), VfxRandom01(seed, salt ^ 0x68BC21EBu), VfxRandom01(seed, salt ^ 0x02E5BE93u));
}

vec4 VfxRandom4(uint seed, uint salt)
{
	return vec4(VfxRandom3(seed, salt), VfxRandom01(seed, salt ^ 0x1B56C4E9u));
}

// Uniform direction.
vec3 VfxRandomOnSphere(uint seed, uint salt)
{
	vec2 u = VfxRandom2(seed, salt);
	float z = u.x * 2.0 - 1.0;
	float phi = u.y * 6.28318530718;
	float r = sqrt(max(0.0, 1.0 - z * z));
	return vec3(r * cos(phi), r * sin(phi), z);
}

// Uniform point in the unit ball.
vec3 VfxRandomInSphere(uint seed, uint salt)
{
	return VfxRandomOnSphere(seed, salt) * pow(VfxRandom01(seed, salt ^ 0x3C6EF372u), 1.0 / 3.0);
}

vec2 VfxRandomOnCircle(uint seed, uint salt)
{
	float a = VfxRandom01(seed, salt) * 6.28318530718;
	return vec2(cos(a), sin(a));
}

vec2 VfxRandomInCircle(uint seed, uint salt)
{
	return VfxRandomOnCircle(seed, salt) * sqrt(VfxRandom01(seed, salt ^ 0x3C6EF372u));
}

// Uniform direction inside a cone of half angle (radians) around +Z.
vec3 VfxRandomInCone(uint seed, uint salt, float halfAngle)
{
	vec2 u = VfxRandom2(seed, salt);
	float cosMax = cos(halfAngle);
	float z = mix(cosMax, 1.0, u.x);
	float phi = u.y * 6.28318530718;
	float r = sqrt(max(0.0, 1.0 - z * z));
	return vec3(r * cos(phi), r * sin(phi), z);
}

// Floats as ordered unsigned integers (atomic min / max of positions).
uint VfxOrderedUint(float f)
{
	uint u = floatBitsToUint(f);
	return (u & 0x80000000u) != 0u ? ~u : (u | 0x80000000u);
}

float VfxOrderedFloat(uint u)
{
	return uintBitsToFloat((u & 0x80000000u) != 0u ? (u & 0x7FFFFFFFu) : ~u);
}

// ---------------------------------------------------------------- math helpers of the intrinsics

float VfxSafeNormalize(float v) { return v == 0.0 ? 0.0 : sign(v); }
vec2 VfxSafeNormalize(vec2 v) { float l = length(v); return l > 1e-8 ? v / l : vec2(0.0); }
vec3 VfxSafeNormalize(vec3 v) { float l = length(v); return l > 1e-8 ? v / l : vec3(0.0); }
vec4 VfxSafeNormalize(vec4 v) { float l = length(v); return l > 1e-8 ? v / l : vec4(0.0); }

#define VFX_REMAP(T) T VfxRemap(T x, T oldMin, T oldMax, T newMin, T newMax) { T span = oldMax - oldMin; T t = (x - oldMin) / mix(span, T(1e-20), equal(span, T(0.0))); return newMin + t * (newMax - newMin); }
float VfxRemap(float x, float oldMin, float oldMax, float newMin, float newMax) { float span = oldMax - oldMin; float t = (x - oldMin) / (abs(span) > 1e-20 ? span : 1e-20); return newMin + t * (newMax - newMin); }
VFX_REMAP(vec2)
VFX_REMAP(vec3)
VFX_REMAP(vec4)

vec2 VfxProject(vec2 v, vec2 n) { float d = dot(n, n); return d > 1e-20 ? n * (dot(v, n) / d) : vec2(0.0); }
vec3 VfxProject(vec3 v, vec3 n) { float d = dot(n, n); return d > 1e-20 ? n * (dot(v, n) / d) : vec3(0.0); }
vec4 VfxProject(vec4 v, vec4 n) { float d = dot(n, n); return d > 1e-20 ? n * (dot(v, n) / d) : vec4(0.0); }
float VfxProject(float v, float n) { return n != 0.0 ? v : 0.0; }

// Rotation matrices of the engine's Euler convention (QuaternionHelper.Euler: yaw about Y, then pitch about Z, then roll about X).
mat3 VfxRotationX(float a) { float c = cos(a); float s = sin(a); return mat3(1.0, 0.0, 0.0, 0.0, c, s, 0.0, -s, c); }
mat3 VfxRotationY(float a) { float c = cos(a); float s = sin(a); return mat3(c, 0.0, -s, 0.0, 1.0, 0.0, s, 0.0, c); }
mat3 VfxRotationZ(float a) { float c = cos(a); float s = sin(a); return mat3(c, s, 0.0, -s, c, 0.0, 0.0, 0.0, 1.0); }

mat3 VfxEulerToMat3(vec3 eulerDegrees)
{
	vec3 r = radians(eulerDegrees);
	return VfxRotationY(r.y) * VfxRotationZ(r.z) * VfxRotationX(r.x);
}

mat4 VfxComposeTransform(vec3 position, vec3 eulerDegrees, vec3 scale)
{
	mat3 r = VfxEulerToMat3(eulerDegrees);
	return mat4(vec4(r[0] * scale.x, 0.0), vec4(r[1] * scale.y, 0.0), vec4(r[2] * scale.z, 0.0), vec4(position, 1.0));
}

// Rodrigues' rotation of v around a (normalized) axis by an angle in radians.
vec3 VfxRotateAroundAxis(vec3 v, vec3 axis, float angle)
{
	vec3 a = VfxSafeNormalize(axis);
	float c = cos(angle);
	float s = sin(angle);
	return v * c + cross(a, v) * s + a * dot(a, v) * (1.0 - c);
}

vec2 VfxRotate2D(vec2 v, float angle)
{
	float c = cos(angle);
	float s = sin(angle);
	return vec2(v.x * c - v.y * s, v.x * s + v.y * c);
}

vec2 VfxPolarToRectangular(float angle, float radius) { return vec2(cos(angle), sin(angle)) * radius; }
vec3 VfxSphericalToRectangular(float theta, float phi, float radius) { return vec3(sin(phi) * cos(theta), cos(phi), sin(phi) * sin(theta)) * radius; }
vec2 VfxRectangularToPolar(vec2 v) { return vec2(atan(v.y, v.x), length(v)); }
vec3 VfxRectangularToSpherical(vec3 v) { float r = length(v); return vec3(atan(v.z, v.x), r > 1e-20 ? acos(clamp(v.y / r, -1.0, 1.0)) : 0.0, r); }

vec3 VfxHsvToRgb(vec3 hsv)
{
	vec3 p = abs(fract(hsv.xxx + vec3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0);
	return hsv.z * mix(vec3(1.0), clamp(p - 1.0, 0.0, 1.0), hsv.y);
}

vec3 VfxRgbToHsv(vec3 c)
{
	vec4 K = vec4(0.0, -1.0 / 3.0, 2.0 / 3.0, -1.0);
	vec4 p = c.g < c.b ? vec4(c.bg, K.wz) : vec4(c.gb, K.xy);
	vec4 q = c.r < p.x ? vec4(p.xyw, c.r) : vec4(c.r, p.yzx);
	float d = q.x - min(q.w, q.y);
	float e = 1e-10;
	return vec3(abs(q.z + (q.w - q.y) / (6.0 * d + e)), d / (q.x + e), q.x);
}

// Collision response shared by the collide blocks: distance is the signed distance of the particle's centre to the
// surface (positive on the free side), normal points to the free side. Pushes the particle out, reflects the
// velocity (bounce, friction), roughens the normal and shortens the lifetime.
void VfxCollisionResponse(inout vec3 position, inout vec3 velocity, inout float lifetime, float distance, vec3 normal, float radius, float bounce, float friction, float lifetimeLoss, float roughness, uint seed, uint salt)
{
	float penetration = radius - distance;
	if (penetration <= 0.0) return;
	if (roughness > 0.0)
	{
		normal = VfxSafeNormalize(normal + (VfxRandom3(seed, salt) * 2.0 - 1.0) * roughness);
	}

	position += normal * penetration;
	float vn = dot(velocity, normal);
	if (vn < 0.0)
	{
		vec3 tangent = velocity - normal * vn;
		velocity = tangent * (1.0 - clamp(friction, 0.0, 1.0)) - normal * vn * bounce;
	}

	lifetime -= lifetimeLoss * lifetime;
}

// Signed distance to an axis-aligned box (positive outside) and the outward normal at the nearest face.
float VfxBoxDistance(vec3 p, vec3 center, vec3 halfSize, out vec3 normal)
{
	vec3 d = abs(p - center) - halfSize;
	vec3 s = sign(p - center);
	float outside = length(max(d, vec3(0.0)));
	float inside = min(max(d.x, max(d.y, d.z)), 0.0);
	if (d.x > d.y && d.x > d.z) normal = vec3(s.x, 0.0, 0.0);
	else if (d.y > d.z) normal = vec3(0.0, s.y, 0.0);
	else normal = vec3(0.0, 0.0, s.z);
	if (outside > 0.0) normal = VfxSafeNormalize(max(d, vec3(0.0)) * s);
	return outside + inside;
}

// Corners of a quad drawn as two triangles (6 vertices): uv in [0, 1].
vec2 VfxQuadCorner(uint corner)
{
	switch (corner)
	{
		case 0u: return vec2(0.0, 0.0);
		case 1u: return vec2(1.0, 0.0);
		case 2u: return vec2(1.0, 1.0);
		case 3u: return vec2(0.0, 0.0);
		case 4u: return vec2(1.0, 1.0);
		default: return vec2(0.0, 1.0);
	}
}

// Corners of the shapes drawn indexed (VfxRuntime's index buffers), uv in [0, 1] over the texture:
// a quad (4 vertices, triangles 0 1 2 and 0 2 3),
vec2 VfxQuadCorner4(uint corner)
{
	return vec2(corner == 1u || corner == 2u ? 1.0 : 0.0, corner >= 2u ? 1.0 : 0.0);
}

// a triangle around the quad (3 vertices; uv outside [0, 1] is discarded),
vec2 VfxTriangleCorner(uint corner)
{
	return corner == 0u ? vec2(-0.5, 0.0) : corner == 1u ? vec2(1.5, 0.0) : vec2(0.5, 2.0);
}

// an octagon, the quad with its corners cut by crop (0: square, 1: regular octagon), as a fan of 6 triangles.
vec2 VfxOctagonCorner(uint corner, float crop)
{
	float c = clamp(crop, 0.0, 1.0) * 0.29289322;
	switch (corner)
	{
		case 0u: return vec2(c, 0.0);
		case 1u: return vec2(1.0 - c, 0.0);
		case 2u: return vec2(1.0, c);
		case 3u: return vec2(1.0, 1.0 - c);
		case 4u: return vec2(1.0 - c, 1.0);
		case 5u: return vec2(c, 1.0);
		case 6u: return vec2(0.0, 1.0 - c);
		default: return vec2(0.0, c);
	}
}

// Texture coordinates of a flipbook frame (frames run left to right, top to bottom).
vec2 VfxFlipbookUv(vec2 uv, vec2 size, float frame)
{
	size = max(size, vec2(1.0));
	float count = size.x * size.y;
	frame = mod(frame, count);
	if (frame < 0.0) frame += count;
	float column = mod(frame, size.x);
	float row = floor(frame / size.x);
	return (uv + vec2(column, size.y - 1.0 - row)) / size;
}

// ---------------------------------------------------------------- gradients and curves (VfxResourceTable layout)

#ifdef VFX_USE_CURVES
// Gradients and curves are baked into lookup tables of 128 samples (VfxResourceTable.Bake): two reads and a lerp
// whatever the key count.
// Gradient: 128 colors over t in [0, 1].
vec4 VfxSampleGradient(uint offset, float t)
{
	float x = clamp(t, 0.0, 1.0) * 127.0;
	uint i = min(uint(x), 126u);
	return mix(vfxCurves[offset + i], vfxCurves[offset + i + 1u], x - float(i));
}

// Curve: a header (first key time, 1 / key span), then 128 samples over the keys' span, four per entry.
float VfxSampleCurve(uint offset, float t)
{
	vec4 header = vfxCurves[offset];
	float x = clamp((t - header.x) * header.y, 0.0, 1.0) * 127.0;
	uint i = min(uint(x), 126u);
	uint j = i + 1u;
	float a = vfxCurves[offset + 1u + (i >> 2u)][i & 3u];
	float b = vfxCurves[offset + 1u + (j >> 2u)][j & 3u];
	return mix(a, b, x - float(i));
}
#endif

#endif
