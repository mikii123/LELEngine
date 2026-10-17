using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     Where an expression's value can change: a constant is folded at compile time, a uniform expression is
	///     evaluated on the CPU once per frame and uploaded (VFX Graph hoists such expressions the same way), a spawn
	///     expression differs per spawned particle (spawn index, event payload) and a particle expression per particle.
	/// </summary>
	public enum VfxScope
	{
		Constant = 0,
		Uniform = 1,
		Spawn = 2,
		Particle = 3
	}

	public enum VfxExprKind
	{
		Constant,
		Property,
		Builtin,
		Attribute,

		/// <summary>An attribute of the parent particle that raised the GPU event (child initialize contexts).</summary>
		SourceAttribute,
		Call,
		Swizzle,
		Construct,
		Convert,
		Select,
		Random,
		SampleGradient,
		SampleCurve,
		SampleTexture2D,
		SampleTexture3D,
		MeshVertex,

		/// <summary>A GLSL template with {0}, {1}... placeholders for its arguments (GPU only).</summary>
		Raw
	}

	public enum VfxRandomMode
	{
		/// <summary>A new value per particle (and per frame in update contexts).</summary>
		PerParticle,

		/// <summary>The same value for every particle of a component, fixed when it starts (differs between components).</summary>
		PerComponent,

		/// <summary>The same value everywhere, fixed by the seed.</summary>
		Constant
	}

	/// <summary>Built-in values expressions can read.</summary>
	public static class VfxBuiltins
	{
		public const string DeltaTime = "deltaTime";
		public const string TotalTime = "totalTime";
		public const string FrameIndex = "frameIndex";
		public const string PlayRate = "playRate";
		public const string LocalToWorld = "localToWorld";
		public const string WorldToLocal = "worldToLocal";
		public const string CameraPosition = "cameraPosition";
		public const string CameraForward = "cameraForward";
		public const string CameraUp = "cameraUp";
		public const string CameraRight = "cameraRight";
		public const string SpawnCount = "spawnCount";
		public const string SpawnIndex = "spawnIndex";
		public const string ParticleIndex = "particleIndex";
		public const string SystemSeed = "systemSeed";
		public const string SpawnedTotal = "spawnedTotal";
		public const string Capacity = "capacity";
		public const string AliveCount = "aliveCount";

		/// <summary>GPU events: the number of events the child system initializes from this frame.</summary>
		public const string EventCount = "eventCount";

		/// <summary>Spawn contexts: seconds since the spawner's current loop started, and that time over the loop duration (0..1).</summary>
		public const string SpawnerTime = "spawnerTime";

		public const string LoopProgress = "loopProgress";

		public static VfxValueType TypeOf(string builtin)
		{
			switch (builtin)
			{
				case DeltaTime:
				case TotalTime:
				case PlayRate:
				case SpawnerTime:
				case LoopProgress:
					return VfxValueType.Float;
				case FrameIndex:
				case SpawnCount:
				case SpawnIndex:
				case ParticleIndex:
				case SystemSeed:
				case SpawnedTotal:
				case Capacity:
				case AliveCount:
				case EventCount:
					return VfxValueType.Uint;
				case LocalToWorld:
				case WorldToLocal:
					return VfxValueType.Matrix;
				default:
					return VfxValueType.Vector3;
			}
		}

		public static VfxScope ScopeOf(string builtin)
		{
			switch (builtin)
			{
				case SpawnIndex:
				case SpawnCount:
				case EventCount:
					return VfxScope.Spawn;
				case ParticleIndex:
				case AliveCount:
					return VfxScope.Particle;
				default:
					return VfxScope.Uniform;
			}
		}
	}

	/// <summary>
	///     A node of the expression tree the compiler builds from the operator graph: a typed value with a scope,
	///     emitted as GLSL (<see cref="VfxGlslWriter" />) or evaluated on the CPU (<see cref="Evaluate" />).
	/// </summary>
	public sealed class VfxExpression
	{
		#region PublicFields

		public readonly VfxExprKind Kind;
		public readonly VfxValueType Type;
		public readonly VfxExpression[] Args;
		public readonly VfxValue Constant;

		/// <summary>Property / builtin / attribute / intrinsic name, swizzle mask or raw template.</summary>
		public readonly string Name;

		/// <summary>Random mode, mesh vertex component or other per-kind data.</summary>
		public readonly object Data;

		/// <summary>Salt that makes two random nodes differ (random expressions).</summary>
		public readonly uint Salt;

		public readonly VfxScope Scope;

		/// <summary>Cannot be evaluated on the CPU (texture sampling, noise, raw GLSL).</summary>
		public readonly bool GpuOnly;

		public static readonly VfxExpression[] NoArgs = Array.Empty<VfxExpression>();

		#endregion

		#region Constructors

		private VfxExpression(VfxExprKind kind, VfxValueType type, VfxExpression[] args, VfxValue constant, string name, object data, uint salt, VfxScope minimumScope, bool gpuOnly)
		{
			Kind = kind;
			Type = type;
			Args = args ?? NoArgs;
			Constant = constant;
			Name = name;
			Data = data;
			Salt = salt;
			GpuOnly = gpuOnly;
			VfxScope scope = minimumScope;
			foreach (VfxExpression arg in Args)
			{
				if (arg != null)
				{
					if (arg.Scope > scope) scope = arg.Scope;
					if (arg.GpuOnly) GpuOnly = true;
				}
			}

			if (GpuOnly && scope < VfxScope.Spawn)
			{
				scope = VfxScope.Spawn;
			}

			Scope = scope;
		}

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- construction

		public static VfxExpression Const(VfxValue value)
		{
			return new VfxExpression(VfxExprKind.Constant, value.Type, null, value, null, null, 0, VfxScope.Constant, false);
		}

		public static VfxExpression Float(float value)
		{
			return Const(VfxValue.FromFloat(value));
		}

		public static VfxExpression Int(int value)
		{
			return Const(VfxValue.FromInt(value));
		}

		public static VfxExpression Uint(uint value)
		{
			return Const(VfxValue.FromUint(value));
		}

		public static VfxExpression Bool(bool value)
		{
			return Const(VfxValue.FromBool(value));
		}

		public static VfxExpression Vec3(float x, float y, float z)
		{
			return Const(VfxValue.FromVector3(x, y, z));
		}

		public static VfxExpression Vec3(Vector3 v)
		{
			return Const(VfxValue.FromVector3(v));
		}

		public static VfxExpression Vec4(Vector4 v)
		{
			return Const(VfxValue.FromVector4(v));
		}

		/// <summary>A graph property (uniform; resources stay resources).</summary>
		public static VfxExpression Property(string propertyName, VfxValueType type)
		{
			return new VfxExpression(VfxExprKind.Property, type, null, null, propertyName, null, 0, VfxScope.Uniform, false);
		}

		public static VfxExpression Builtin(string builtin)
		{
			return new VfxExpression(VfxExprKind.Builtin, VfxBuiltins.TypeOf(builtin), null, null, builtin, null, 0, VfxBuiltins.ScopeOf(builtin), false);
		}

		public static VfxExpression Attribute(VfxAttribute attribute)
		{
			return new VfxExpression(VfxExprKind.Attribute, attribute.Type, null, null, attribute.Name, attribute, 0, VfxScope.Particle, false);
		}

		public static VfxExpression SourceAttribute(VfxAttribute attribute)
		{
			return new VfxExpression(VfxExprKind.SourceAttribute, attribute.Type, null, null, attribute.Name, attribute, 0, VfxScope.Spawn, false);
		}

		/// <summary>An intrinsic call (see <see cref="VfxIntrinsics" />); arguments are converted as the intrinsic asks.</summary>
		public static VfxExpression Call(string intrinsic, params VfxExpression[] args)
		{
			VfxIntrinsic definition = VfxIntrinsics.Get(intrinsic);
			if (definition == null)
			{
				throw new ArgumentException("Unknown intrinsic '" + intrinsic + "'");
			}

			VfxExpression[] converted = definition.PrepareArguments(args);
			VfxValueType type = definition.ResultType(converted);
			return new VfxExpression(VfxExprKind.Call, type, converted, null, intrinsic, definition, 0, VfxScope.Constant, definition.GpuOnly);
		}

		public static VfxExpression Swizzle(VfxExpression value, string mask)
		{
			VfxValueType type = VfxValueTypes.VectorOf(mask.Length);
			if (!VfxValueTypes.IsVector(value.Type) && mask.Length > 1)
			{
				value = ConvertTo(value, VfxValueType.Vector4);
			}

			return new VfxExpression(VfxExprKind.Swizzle, type, new[] { value }, null, mask, null, 0, VfxScope.Constant, false);
		}

		/// <summary>A vector built from scalar / vector parts (their components are concatenated, excess dropped).</summary>
		public static VfxExpression Construct(VfxValueType type, params VfxExpression[] parts)
		{
			return new VfxExpression(VfxExprKind.Construct, type, parts, null, null, null, 0, VfxScope.Constant, false);
		}

		public static VfxExpression ConvertTo(VfxExpression value, VfxValueType type)
		{
			if (value == null)
			{
				return Const(VfxValue.Default(type));
			}

			if (value.Type == type)
			{
				return value;
			}

			if (value.Kind == VfxExprKind.Constant && VfxValueTypes.IsNumeric(type) && VfxValueTypes.IsNumeric(value.Type))
			{
				return Const(value.Constant.ConvertTo(type));
			}

			return new VfxExpression(VfxExprKind.Convert, type, new[] { value }, null, null, null, 0, VfxScope.Constant, false);
		}

		public static VfxExpression Select(VfxExpression condition, VfxExpression a, VfxExpression b)
		{
			VfxValueType type = VfxValueTypes.Promote(a.Type, b.Type);
			return new VfxExpression(VfxExprKind.Select, type, new[] { ConvertTo(condition, VfxValueType.Bool), ConvertTo(a, type), ConvertTo(b, type) }, null, null, null, 0, VfxScope.Constant, false);
		}

		/// <summary>A random number in [min, max) of the given numeric type.</summary>
		public static VfxExpression Random(VfxRandomMode mode, uint salt, VfxValueType type, VfxExpression min = null, VfxExpression max = null)
		{
			VfxScope scope = mode == VfxRandomMode.PerParticle ? VfxScope.Particle : VfxScope.Uniform;
			var args = new[] { ConvertTo(min ?? Float(0f), type), ConvertTo(max ?? Float(1f), type) };
			return new VfxExpression(VfxExprKind.Random, type, args, null, null, mode, salt, scope, false);
		}

		public static VfxExpression SampleGradient(VfxExpression gradient, VfxExpression t)
		{
			return new VfxExpression(VfxExprKind.SampleGradient, VfxValueType.Color, new[] { gradient, ConvertTo(t, VfxValueType.Float) }, null, null, null, 0, VfxScope.Uniform, false);
		}

		public static VfxExpression SampleCurve(VfxExpression curve, VfxExpression t)
		{
			return new VfxExpression(VfxExprKind.SampleCurve, VfxValueType.Float, new[] { curve, ConvertTo(t, VfxValueType.Float) }, null, null, null, 0, VfxScope.Uniform, false);
		}

		public static VfxExpression SampleTexture2D(VfxExpression texture, VfxExpression uv, VfxExpression lod = null)
		{
			var args = lod != null ? new[] { texture, ConvertTo(uv, VfxValueType.Vector2), ConvertTo(lod, VfxValueType.Float) } : new[] { texture, ConvertTo(uv, VfxValueType.Vector2) };
			return new VfxExpression(VfxExprKind.SampleTexture2D, VfxValueType.Color, args, null, null, null, 0, VfxScope.Spawn, true);
		}

		public static VfxExpression SampleTexture3D(VfxExpression texture, VfxExpression uvw)
		{
			return new VfxExpression(VfxExprKind.SampleTexture3D, VfxValueType.Color, new[] { texture, ConvertTo(uvw, VfxValueType.Vector3) }, null, null, null, 0, VfxScope.Spawn, true);
		}

		/// <summary>A component ("position", "normal", "uv") of a mesh vertex (by vertex index, wrapped to the vertex count).</summary>
		public static VfxExpression MeshVertex(VfxExpression mesh, VfxExpression index, string component)
		{
			VfxValueType type = component == "uv" ? VfxValueType.Vector2 : component == "count" ? VfxValueType.Uint : VfxValueType.Vector3;
			return new VfxExpression(VfxExprKind.MeshVertex, type, new[] { mesh, ConvertTo(index, VfxValueType.Uint) }, null, component, null, 0, VfxScope.Spawn, true);
		}

		/// <summary>A raw GLSL template ("sin({0}) * {1}") of the given type; GPU only.</summary>
		public static VfxExpression Raw(VfxValueType type, string template, params VfxExpression[] args)
		{
			return new VfxExpression(VfxExprKind.Raw, type, args, null, template, null, 0, VfxScope.Spawn, true);
		}

		// Arithmetic helpers for the node definitions.

		public static VfxExpression Add(VfxExpression a, VfxExpression b) => Call("add", a, b);
		public static VfxExpression Sub(VfxExpression a, VfxExpression b) => Call("sub", a, b);
		public static VfxExpression Mul(VfxExpression a, VfxExpression b) => Call("mul", a, b);
		public static VfxExpression Div(VfxExpression a, VfxExpression b) => Call("div", a, b);
		public static VfxExpression Neg(VfxExpression a) => Call("neg", a);
		public static VfxExpression Min(VfxExpression a, VfxExpression b) => Call("min", a, b);
		public static VfxExpression Max(VfxExpression a, VfxExpression b) => Call("max", a, b);
		public static VfxExpression Lerp(VfxExpression a, VfxExpression b, VfxExpression t) => Call("lerp", a, b, t);
		public static VfxExpression Saturate(VfxExpression a) => Call("saturate", a);
		public static VfxExpression Normalize(VfxExpression a) => Call("normalize", a);
		public static VfxExpression Length(VfxExpression a) => Call("length", a);
		public static VfxExpression Dot(VfxExpression a, VfxExpression b) => Call("dot", a, b);
		public static VfxExpression Cross(VfxExpression a, VfxExpression b) => Call("cross", a, b);
		public static VfxExpression X(VfxExpression a) => Swizzle(a, "x");
		public static VfxExpression Y(VfxExpression a) => Swizzle(a, "y");
		public static VfxExpression Z(VfxExpression a) => Swizzle(a, "z");
		public static VfxExpression W(VfxExpression a) => Swizzle(a, "w");
		public static VfxExpression Xyz(VfxExpression a) => Swizzle(a, "xyz");

		/// <summary>The position of a vector transformed by a matrix.</summary>
		public static VfxExpression TransformPoint(VfxExpression matrix, VfxExpression position) => Call("transformPoint", matrix, position);

		public static VfxExpression TransformDirection(VfxExpression matrix, VfxExpression direction) => Call("transformDirection", matrix, direction);

		// ---------------------------------------------------------------- evaluation

		/// <summary>True when the expression can be computed on the CPU (no particle data, no GPU-only sampling).</summary>
		public bool IsCpuEvaluable => !GpuOnly && Scope <= VfxScope.Uniform;

		/// <summary>
		///     Depends only on constants and properties (no time, camera, transform, spawn state or randomness): its
		///     CPU value changes only when a property does, so callers evaluate it once per property version.
		/// </summary>
		public bool IsFrameInvariant
		{
			get
			{
				if (frameInvariant == 0)
				{
					bool invariant = Kind != VfxExprKind.Builtin && Kind != VfxExprKind.Random && Kind != VfxExprKind.Attribute && Kind != VfxExprKind.SourceAttribute;
					foreach (VfxExpression arg in Args)
					{
						if (arg != null && !arg.IsFrameInvariant)
						{
							invariant = false;
							break;
						}
					}

					frameInvariant = invariant ? 1 : 2;
				}

				return frameInvariant == 1;
			}
		}

		private int frameInvariant;

		/// <summary>CPU evaluation (constant folding and per-frame uniform expressions).</summary>
		public VfxValue Evaluate(VfxEvalContext context)
		{
			switch (Kind)
			{
				case VfxExprKind.Constant:
					return Constant;
				case VfxExprKind.Property:
					return context.GetProperty(Name, Type);
				case VfxExprKind.Builtin:
					return context.GetBuiltin(Name);
				case VfxExprKind.Attribute:
				case VfxExprKind.SourceAttribute:
					return ((VfxAttribute)Data).Default;
				case VfxExprKind.Call:
					return ((VfxIntrinsic)Data).Evaluate(EvaluateArgs(context));
				case VfxExprKind.Swizzle:
					return EvaluateSwizzle(Args[0].Evaluate(context), Name, Type);
				case VfxExprKind.Construct:
					return EvaluateConstruct(EvaluateArgs(context), Type);
				case VfxExprKind.Convert:
					return Args[0].Evaluate(context).ConvertTo(Type);
				case VfxExprKind.Select:
					return (Args[0].Evaluate(context).Bool ? Args[1] : Args[2]).Evaluate(context).ConvertTo(Type);
				case VfxExprKind.Random:
					return EvaluateRandom(context);
				case VfxExprKind.SampleGradient:
				{
					VfxValue gradient = Args[0].Evaluate(context);
					float t = Args[1].Evaluate(context).Float;
					return VfxValue.FromColor(gradient.Gradient?.Evaluate(t) ?? Color4.White);
				}
				case VfxExprKind.SampleCurve:
				{
					VfxValue curve = Args[0].Evaluate(context);
					float t = Args[1].Evaluate(context).Float;
					return VfxValue.FromFloat(curve.Curve?.Evaluate(t) ?? 0f);
				}
				default:
					throw new InvalidOperationException("The expression " + Kind + " cannot be evaluated on the CPU");
			}
		}

		/// <summary>Every expression of the tree (depth first, each once).</summary>
		public void Visit(Action<VfxExpression> visitor, HashSet<VfxExpression> visited = null)
		{
			visited = visited ?? new HashSet<VfxExpression>();
			if (!visited.Add(this))
			{
				return;
			}

			foreach (VfxExpression arg in Args)
			{
				arg?.Visit(visitor, visited);
			}

			visitor(this);
		}

		/// <summary>The attributes the expression reads.</summary>
		public void CollectAttributes(HashSet<string> names)
		{
			Visit(e =>
			{
				if (e.Kind == VfxExprKind.Attribute)
				{
					names.Add(e.Name);
				}
			});
		}

		public override string ToString()
		{
			switch (Kind)
			{
				case VfxExprKind.Constant: return Constant.ToString();
				case VfxExprKind.Call: return Name + "(" + string.Join(", ", (object[])Args) + ")";
				default: return Kind + (Name != null ? ":" + Name : "");
			}
		}

		#endregion

		#region PrivateMethods

		private VfxValue[] EvaluateArgs(VfxEvalContext context)
		{
			var values = new VfxValue[Args.Length];
			for (int i = 0; i < Args.Length; i++)
			{
				values[i] = Args[i].Evaluate(context);
			}

			return values;
		}

		internal static VfxValue EvaluateSwizzle(VfxValue value, string mask, VfxValueType type)
		{
			Vector4 v = value.Vector;
			var result = new float[4];
			for (int i = 0; i < mask.Length && i < 4; i++)
			{
				result[i] = Component(v, mask[i]);
			}

			return new VfxValue { Type = type, Vector = new Vector4(result[0], result[1], result[2], result[3]) };
		}

		private static float Component(Vector4 v, char c)
		{
			switch (c)
			{
				case 'x':
				case 'r':
					return v.X;
				case 'y':
				case 'g':
					return v.Y;
				case 'z':
				case 'b':
					return v.Z;
				default:
					return v.W;
			}
		}

		internal static VfxValue EvaluateConstruct(VfxValue[] parts, VfxValueType type)
		{
			var components = new List<float>();
			foreach (VfxValue part in parts)
			{
				int count = Math.Max(1, VfxValueTypes.Components(part.Type));
				Vector4 v = part.Vector;
				for (int i = 0; i < count && components.Count < 4; i++)
				{
					components.Add(i == 0 ? v.X : i == 1 ? v.Y : i == 2 ? v.Z : v.W);
				}
			}

			while (components.Count < 4)
			{
				components.Add(type == VfxValueType.Color && components.Count == 3 ? 1f : 0f);
			}

			return new VfxValue { Type = type, Vector = new Vector4(components[0], components[1], components[2], components[3]) };
		}

		private VfxValue EvaluateRandom(VfxEvalContext context)
		{
			var mode = (VfxRandomMode)Data;
			uint seed = mode == VfxRandomMode.Constant ? 0x1234567u : context.ComponentSeed;
			VfxValue min = Args[0].Evaluate(context);
			VfxValue max = Args[1].Evaluate(context);
			int components = Math.Max(1, VfxValueTypes.Components(Type));
			var result = new float[4];
			for (int i = 0; i < components; i++)
			{
				float r = VfxRandomHash.Random01(seed ^ Salt, (uint)i);
				float a = i == 0 ? min.Vector.X : i == 1 ? min.Vector.Y : i == 2 ? min.Vector.Z : min.Vector.W;
				float b = i == 0 ? max.Vector.X : i == 1 ? max.Vector.Y : i == 2 ? max.Vector.Z : max.Vector.W;
				result[i] = a + (b - a) * r;
			}

			return new VfxValue { Type = Type, Vector = new Vector4(result[0], result[1], result[2], result[3]) }.ConvertTo(Type);
		}

		#endregion
	}

	/// <summary>The same hash the GLSL side uses (Engine/VFX/VfxCommon.glsl), so CPU-side random values match.</summary>
	public static class VfxRandomHash
	{
		public static uint Pcg(uint input)
		{
			uint state = input * 747796405u + 2891336453u;
			uint word = ((state >> (int)((state >> 28) + 4u)) ^ state) * 277803737u;
			return (word >> 22) ^ word;
		}

		public static float Random01(uint seed, uint salt)
		{
			return Pcg(seed ^ Pcg(salt + 0x9E3779B9u)) * (1f / 4294967296f);
		}
	}

	/// <summary>Values an expression needs to evaluate on the CPU: the properties and built-ins of one frame.</summary>
	public sealed class VfxEvalContext
	{
		#region PublicFields

		public Func<string, VfxValueType, VfxValue> PropertyLookup;
		public float DeltaTime;
		public float TotalTime;
		public uint FrameIndex;
		public float PlayRate = 1f;
		public Matrix4 LocalToWorld = Matrix4.Identity;
		public Matrix4 WorldToLocal = Matrix4.Identity;
		public Vector3 CameraPosition;
		public Vector3 CameraForward = -Vector3.UnitZ;
		public Vector3 CameraUp = Vector3.UnitY;
		public Vector3 CameraRight = Vector3.UnitX;
		public uint SystemSeed;
		public uint ComponentSeed;
		public uint SpawnedTotal;
		public uint SpawnCount;
		public uint Capacity;
		public float SpawnerTime;
		public float LoopProgress;

		#endregion

		#region PublicMethods

		public VfxValue GetProperty(string propertyName, VfxValueType type)
		{
			VfxValue value = PropertyLookup?.Invoke(propertyName, type);
			if (value == null)
			{
				return VfxValue.Default(type);
			}

			return VfxValueTypes.IsNumeric(type) ? value.ConvertTo(type) : value;
		}

		public VfxValue GetBuiltin(string builtin)
		{
			switch (builtin)
			{
				case VfxBuiltins.DeltaTime: return VfxValue.FromFloat(DeltaTime);
				case VfxBuiltins.TotalTime: return VfxValue.FromFloat(TotalTime);
				case VfxBuiltins.FrameIndex: return VfxValue.FromUint(FrameIndex);
				case VfxBuiltins.PlayRate: return VfxValue.FromFloat(PlayRate);
				case VfxBuiltins.LocalToWorld: return VfxValue.FromMatrix(LocalToWorld);
				case VfxBuiltins.WorldToLocal: return VfxValue.FromMatrix(WorldToLocal);
				case VfxBuiltins.CameraPosition: return VfxValue.FromVector3(CameraPosition);
				case VfxBuiltins.CameraForward: return VfxValue.FromVector3(CameraForward);
				case VfxBuiltins.CameraUp: return VfxValue.FromVector3(CameraUp);
				case VfxBuiltins.CameraRight: return VfxValue.FromVector3(CameraRight);
				case VfxBuiltins.SystemSeed: return VfxValue.FromUint(SystemSeed);
				case VfxBuiltins.SpawnedTotal: return VfxValue.FromUint(SpawnedTotal);
				case VfxBuiltins.SpawnCount: return VfxValue.FromUint(SpawnCount);
				case VfxBuiltins.Capacity: return VfxValue.FromUint(Capacity);
				case VfxBuiltins.SpawnerTime: return VfxValue.FromFloat(SpawnerTime);
				case VfxBuiltins.LoopProgress: return VfxValue.FromFloat(LoopProgress);
				default: return VfxValue.FromUint(0);
			}
		}

		#endregion
	}

	public enum VfxTypeRule
	{
		/// <summary>The arguments are converted to their common type, which is the result (add, min, lerp).</summary>
		Promote,

		/// <summary>The first argument's type (the others are converted to it or stay scalars).</summary>
		First,

		/// <summary>A fixed result type (length: float, cross: vec3).</summary>
		Fixed,

		/// <summary>Promoted arguments, boolean result (comparisons).</summary>
		Compare
	}

	/// <summary>
	///     A function expressions can call: its GLSL template, its CPU implementation (null for GPU-only ones) and how
	///     its argument and result types are decided.
	/// </summary>
	public sealed class VfxIntrinsic
	{
		#region PublicFields

		public readonly string Name;
		public readonly string Template;
		public readonly VfxTypeRule Rule;
		public readonly VfxValueType FixedType;
		public readonly Func<VfxValue[], VfxValue> Cpu;
		public readonly bool GpuOnly;

		/// <summary>Arguments after this index stay float (lerp's t, pow's exponent): not promoted to vectors.</summary>
		public readonly int ScalarFrom;

		#endregion

		#region Constructors

		public VfxIntrinsic(string name, string template, VfxTypeRule rule, VfxValueType fixedType, Func<VfxValue[], VfxValue> cpu, int scalarFrom = int.MaxValue)
		{
			Name = name;
			Template = template;
			Rule = rule;
			FixedType = fixedType;
			Cpu = cpu;
			GpuOnly = cpu == null;
			ScalarFrom = scalarFrom;
		}

		#endregion

		#region PublicMethods

		/// <summary>Converts the arguments to the types the intrinsic expects.</summary>
		public VfxExpression[] PrepareArguments(VfxExpression[] args)
		{
			var result = new VfxExpression[args.Length];
			switch (Rule)
			{
				case VfxTypeRule.Promote:
				case VfxTypeRule.Compare:
				{
					VfxValueType common = CommonType(args);
					for (int i = 0; i < args.Length; i++)
					{
						result[i] = i >= ScalarFrom ? VfxExpression.ConvertTo(args[i], ScalarTypeOf(common)) : VfxExpression.ConvertTo(args[i], common);
					}

					return result;
				}
				case VfxTypeRule.First:
				{
					VfxValueType first = args.Length > 0 ? args[0].Type : VfxValueType.Float;
					for (int i = 0; i < args.Length; i++)
					{
						result[i] = i >= ScalarFrom ? VfxExpression.ConvertTo(args[i], ScalarTypeOf(first)) : (i == 0 ? args[0] : VfxExpression.ConvertTo(args[i], first));
					}

					return result;
				}
				default:
				{
					// Fixed result: vector arguments are promoted among themselves (dot of a float and a vec3 is a vec3 dot).
					VfxValueType common = CommonType(args);
					for (int i = 0; i < args.Length; i++)
					{
						VfxExpression arg = args[i];
						result[i] = i >= ScalarFrom ? VfxExpression.ConvertTo(arg, ScalarTypeOf(common)) : (VfxValueTypes.IsNumeric(arg.Type) && arg.Type != VfxValueType.Matrix ? VfxExpression.ConvertTo(arg, common) : arg);
					}

					return result;
				}
			}
		}

		public VfxValueType ResultType(VfxExpression[] args)
		{
			switch (Rule)
			{
				case VfxTypeRule.Promote: return CommonType(args);
				case VfxTypeRule.First: return args.Length > 0 ? args[0].Type : VfxValueType.Float;
				case VfxTypeRule.Compare: return VfxValueType.Bool;
				default: return FixedType;
			}
		}

		public VfxValue Evaluate(VfxValue[] args)
		{
			if (Cpu == null)
			{
				throw new InvalidOperationException("The intrinsic " + Name + " runs on the GPU only");
			}

			return Cpu(args);
		}

		private VfxValueType CommonType(VfxExpression[] args)
		{
			VfxValueType common = VfxValueType.Float;
			bool first = true;
			for (int i = 0; i < args.Length && i < ScalarFrom; i++)
			{
				VfxValueType type = args[i].Type;
				if (!VfxValueTypes.IsNumeric(type) || type == VfxValueType.Matrix)
				{
					continue;
				}

				common = first ? type : VfxValueTypes.Promote(common, type);
				first = false;
			}

			return common;
		}

		private static VfxValueType ScalarTypeOf(VfxValueType type)
		{
			return type == VfxValueType.Int || type == VfxValueType.Uint ? type : VfxValueType.Float;
		}

		#endregion
	}

	/// <summary>The intrinsic functions (component-wise math, vector math, comparisons, noise) and their CPU twins.</summary>
	public static class VfxIntrinsics
	{
		#region PrivateFields

		private static readonly Dictionary<string, VfxIntrinsic> table = new Dictionary<string, VfxIntrinsic>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		static VfxIntrinsics()
		{
			Unary("neg", "(-{0})", x => -x);
			Unary("abs", "abs({0})", MathF.Abs);
			Unary("floor", "floor({0})", MathF.Floor);
			Unary("ceil", "ceil({0})", MathF.Ceiling);
			Unary("round", "round({0})", MathF.Round);
			Unary("frac", "fract({0})", x => x - MathF.Floor(x));
			Unary("sign", "sign({0})", x => MathF.Sign(x));
			Unary("sqrt", "sqrt(max({0}, 0.0))", x => MathF.Sqrt(MathF.Max(x, 0f)));
			Unary("exp", "exp({0})", MathF.Exp);
			Unary("exp2", "exp2({0})", x => MathF.Pow(2f, x));
			Unary("log", "log(max({0}, 1e-20))", x => MathF.Log(MathF.Max(x, 1e-20f)));
			Unary("log2", "log2(max({0}, 1e-20))", x => MathF.Log2(MathF.Max(x, 1e-20f)));
			Unary("sin", "sin({0})", MathF.Sin);
			Unary("cos", "cos({0})", MathF.Cos);
			Unary("tan", "tan({0})", MathF.Tan);
			Unary("asin", "asin(clamp({0}, -1.0, 1.0))", x => MathF.Asin(Math.Clamp(x, -1f, 1f)));
			Unary("acos", "acos(clamp({0}, -1.0, 1.0))", x => MathF.Acos(Math.Clamp(x, -1f, 1f)));
			Unary("atan", "atan({0})", MathF.Atan);
			Unary("saturate", "clamp({0}, 0.0, 1.0)", x => Math.Clamp(x, 0f, 1f));
			Unary("oneMinus", "(1.0 - {0})", x => 1f - x);
			Unary("reciprocal", "(1.0 / {0})", x => 1f / x);
			Unary("square", "({0} * {0})", x => x * x);
			Unary("degrees", "degrees({0})", x => x * (180f / MathF.PI));
			Unary("radians", "radians({0})", x => x * (MathF.PI / 180f));
			Unary("smoothstep01", "smoothstep(0.0, 1.0, {0})", x => { float t = Math.Clamp(x, 0f, 1f); return t * t * (3f - 2f * t); });
			Unary("rsqrt", "inversesqrt(max({0}, 1e-20))", x => 1f / MathF.Sqrt(MathF.Max(x, 1e-20f)));

			Binary("add", "({0} + {1})", (a, b) => a + b);
			Binary("sub", "({0} - {1})", (a, b) => a - b);
			Binary("mul", "({0} * {1})", (a, b) => a * b);
			Binary("div", "({0} / {1})", (a, b) => a / b);
			Binary("mod", "mod({0}, {1})", (a, b) => a - b * MathF.Floor(a / b));
			Binary("min", "min({0}, {1})", MathF.Min);
			Binary("max", "max({0}, {1})", MathF.Max);
			Binary("pow", "pow(max({0}, 0.0), {1})", (a, b) => MathF.Pow(MathF.Max(a, 0f), b));
			Binary("atan2", "atan({0}, {1})", MathF.Atan2);
			Binary("step", "step({0}, {1})", (edge, x) => x >= edge ? 1f : 0f);

			Ternary("clamp", "clamp({0}, {1}, {2})", (x, a, b) => Math.Clamp(x, MathF.Min(a, b), MathF.Max(a, b)));
			Ternary("smoothstep", "smoothstep({0}, {1}, {2})", (a, b, x) => { float t = Math.Clamp((x - a) / MathF.Max(b - a, 1e-20f), 0f, 1f); return t * t * (3f - 2f * t); });
			Register(new VfxIntrinsic("lerp", "mix({0}, {1}, {2})", VfxTypeRule.Promote, VfxValueType.Float, args => ComponentWise(args[0].Type, args, v => v[0] + (v[1] - v[0]) * v[2]), 2));
			Register(new VfxIntrinsic("lerpVector", "mix({0}, {1}, {2})", VfxTypeRule.Promote, VfxValueType.Float, args => ComponentWise(args[0].Type, args, v => v[0] + (v[1] - v[0]) * v[2])));
			Register(new VfxIntrinsic("remap", "VfxRemap({0}, {1}, {2}, {3}, {4})", VfxTypeRule.Promote, VfxValueType.Float,
				args => ComponentWise(args[0].Type, args, v => v[3] + (v[0] - v[1]) / MathF.Max(MathF.Abs(v[2] - v[1]), 1e-20f) * MathF.Sign(v[2] - v[1]) * (v[4] - v[3]))));

			Fixed("length", "length({0})", VfxValueType.Float, args => VfxValue.FromFloat(VectorOf(args[0]).Length));
			Fixed("distance", "distance({0}, {1})", VfxValueType.Float, args => VfxValue.FromFloat((VectorOf(args[0]) - VectorOf(args[1])).Length));
			Fixed("dot", "dot({0}, {1})", VfxValueType.Float, args => VfxValue.FromFloat(Vector4.Dot(VectorOf(args[0]), VectorOf(args[1]))));
			Fixed("cross", "cross({0}, {1})", VfxValueType.Vector3, args => VfxValue.FromVector3(Vector3.Cross(args[0].Vector3, args[1].Vector3)));
			Register(new VfxIntrinsic("normalize", "VfxSafeNormalize({0})", VfxTypeRule.First, VfxValueType.Float, args =>
			{
				Vector4 v = VectorOf(args[0]);
				float length = v.Length;
				return new VfxValue { Type = args[0].Type, Vector = length > 1e-20f ? v / length : Vector4.Zero };
			}));
			Register(new VfxIntrinsic("reflect", "reflect({0}, {1})", VfxTypeRule.Promote, VfxValueType.Float, args =>
			{
				Vector4 i = VectorOf(args[0]);
				Vector4 n = VectorOf(args[1]);
				return new VfxValue { Type = args[0].Type, Vector = i - 2f * Vector4.Dot(n, i) * n };
			}));
			Register(new VfxIntrinsic("project", "VfxProject({0}, {1})", VfxTypeRule.Promote, VfxValueType.Float, args =>
			{
				Vector4 v = VectorOf(args[0]);
				Vector4 n = VectorOf(args[1]);
				float d = Vector4.Dot(n, n);
				return new VfxValue { Type = args[0].Type, Vector = d > 1e-20f ? n * (Vector4.Dot(v, n) / d) : Vector4.Zero };
			}));

			Compare("less", "({0} < {1})", (a, b) => a < b);
			Compare("lessEqual", "({0} <= {1})", (a, b) => a <= b);
			Compare("greater", "({0} > {1})", (a, b) => a > b);
			Compare("greaterEqual", "({0} >= {1})", (a, b) => a >= b);
			Compare("equal", "({0} == {1})", (a, b) => a == b);
			Compare("notEqual", "({0} != {1})", (a, b) => a != b);
			Register(new VfxIntrinsic("and", "({0} && {1})", VfxTypeRule.Fixed, VfxValueType.Bool, args => VfxValue.FromBool(args[0].Bool && args[1].Bool)));
			Register(new VfxIntrinsic("or", "({0} || {1})", VfxTypeRule.Fixed, VfxValueType.Bool, args => VfxValue.FromBool(args[0].Bool || args[1].Bool)));
			Register(new VfxIntrinsic("not", "(!{0})", VfxTypeRule.Fixed, VfxValueType.Bool, args => VfxValue.FromBool(!args[0].Bool)));

			// Transforms (OpenTK row-vector matrices: v * M; GLSL sees the transposed matrix, so M * v there).
			Register(new VfxIntrinsic("transformPoint", "({0} * vec4({1}, 1.0)).xyz", VfxTypeRule.Fixed, VfxValueType.Vector3, args => VfxValue.FromVector3(Vector3.TransformPosition(args[1].Vector3, args[0].Matrix))));
			Register(new VfxIntrinsic("transformDirection", "VfxSafeNormalize(mat3({0}) * {1})", VfxTypeRule.Fixed, VfxValueType.Vector3, args =>
			{
				Vector3 d = Vector3.TransformNormal(args[1].Vector3, args[0].Matrix);
				return VfxValue.FromVector3(d.LengthSquared > 1e-20f ? d.Normalized() : Vector3.Zero);
			}));
			Register(new VfxIntrinsic("transformVector", "(mat3({0}) * {1})", VfxTypeRule.Fixed, VfxValueType.Vector3, args => VfxValue.FromVector3(Vector3.TransformNormal(args[1].Vector3, args[0].Matrix))));
			Register(new VfxIntrinsic("mulMatrix", "({0} * {1})", VfxTypeRule.Fixed, VfxValueType.Matrix, args => VfxValue.FromMatrix(args[1].Matrix * args[0].Matrix)));
			Register(new VfxIntrinsic("inverse", "inverse({0})", VfxTypeRule.Fixed, VfxValueType.Matrix, args => VfxValue.FromMatrix(args[0].Matrix.Inverted())));
			Register(new VfxIntrinsic("composeTransform", "VfxComposeTransform({0}, {1}, {2})", VfxTypeRule.Fixed, VfxValueType.Matrix, args =>
				VfxValue.FromMatrix(Matrix4.CreateScale(args[2].Vector3) * Matrix4.CreateFromQuaternion(QuaternionHelper.Euler(args[1].Vector3.X, args[1].Vector3.Y, args[1].Vector3.Z)) * Matrix4.CreateTranslation(args[0].Vector3))));
			Register(new VfxIntrinsic("rotateAxis", "VfxRotateAroundAxis({0}, {1}, {2})", VfxTypeRule.Fixed, VfxValueType.Vector3, args =>
			{
				Vector3 axis = args[1].Vector3;
				if (axis.LengthSquared < 1e-12f) return VfxValue.FromVector3(args[0].Vector3);
				return VfxValue.FromVector3(Quaternion.FromAxisAngle(axis.Normalized(), args[2].Float) * args[0].Vector3);
			}, 2));
			Register(new VfxIntrinsic("rotate2D", "VfxRotate2D({0}, {1})", VfxTypeRule.Fixed, VfxValueType.Vector2, args =>
			{
				float c = MathF.Cos(args[1].Float);
				float s = MathF.Sin(args[1].Float);
				Vector2 v = args[0].Vector2;
				return VfxValue.FromVector2(new Vector2(v.X * c - v.Y * s, v.X * s + v.Y * c));
			}, 1));
			Register(new VfxIntrinsic("polarToRectangular", "VfxPolarToRectangular({0}, {1})", VfxTypeRule.Fixed, VfxValueType.Vector2, args => VfxValue.FromVector2(new Vector2(MathF.Cos(args[0].Float), MathF.Sin(args[0].Float)) * args[1].Float), 0));
			Register(new VfxIntrinsic("sphericalToRectangular", "VfxSphericalToRectangular({0}, {1}, {2})", VfxTypeRule.Fixed, VfxValueType.Vector3, args =>
			{
				float theta = args[0].Float;
				float phi = args[1].Float;
				float r = args[2].Float;
				return VfxValue.FromVector3(new Vector3(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta)) * r);
			}, 0));
			Register(new VfxIntrinsic("rectangularToPolar", "VfxRectangularToPolar({0})", VfxTypeRule.Fixed, VfxValueType.Vector2, args =>
			{
				Vector2 v = args[0].Vector2;
				return VfxValue.FromVector2(new Vector2(MathF.Atan2(v.Y, v.X), v.Length));
			}));
			Register(new VfxIntrinsic("rectangularToSpherical", "VfxRectangularToSpherical({0})", VfxTypeRule.Fixed, VfxValueType.Vector3, args =>
			{
				Vector3 v = args[0].Vector3;
				float r = v.Length;
				return VfxValue.FromVector3(new Vector3(MathF.Atan2(v.Z, v.X), r > 1e-20f ? MathF.Acos(Math.Clamp(v.Y / r, -1f, 1f)) : 0f, r));
			}));

			// GPU only.
			Register(new VfxIntrinsic("perlin", "VfxPerlin({0}, {1}, {2}, {3})", VfxTypeRule.Fixed, VfxValueType.Float, null));
			Register(new VfxIntrinsic("perlin3", "VfxPerlin3({0}, {1}, {2}, {3})", VfxTypeRule.Fixed, VfxValueType.Vector3, null));
			Register(new VfxIntrinsic("valueNoise", "VfxValueNoise({0}, {1}, {2}, {3})", VfxTypeRule.Fixed, VfxValueType.Float, null));
			Register(new VfxIntrinsic("curlNoise", "VfxCurlNoise({0}, {1}, {2})", VfxTypeRule.Fixed, VfxValueType.Vector3, null));
			Register(new VfxIntrinsic("sampleSdf", "VfxSampleGlobalSdf({0})", VfxTypeRule.Fixed, VfxValueType.Float, null));
			Register(new VfxIntrinsic("sdfNormal", "VfxGlobalSdfNormal({0})", VfxTypeRule.Fixed, VfxValueType.Vector3, null));
			Register(new VfxIntrinsic("hashUint", "VfxPcg({0})", VfxTypeRule.Fixed, VfxValueType.Uint, args => VfxValue.FromUint(VfxRandomHash.Pcg(args[0].Uint))));
			Register(new VfxIntrinsic("floatBits", "uintBitsToFloat({0})", VfxTypeRule.Fixed, VfxValueType.Float, args => VfxValue.FromFloat(BitConverter.UInt32BitsToSingle(args[0].Uint))));
		}

		#endregion

		#region PublicMethods

		public static VfxIntrinsic Get(string name)
		{
			VfxIntrinsic intrinsic;
			return table.TryGetValue(name, out intrinsic) ? intrinsic : null;
		}

		public static void Register(VfxIntrinsic intrinsic)
		{
			table[intrinsic.Name] = intrinsic;
		}

		/// <summary>Applies a per-component function over the components of the (equally typed) arguments.</summary>
		public static VfxValue ComponentWise(VfxValueType type, VfxValue[] args, Func<float[], float> function)
		{
			int components = Math.Max(1, VfxValueTypes.Components(type));
			var result = new float[4];
			var operands = new float[args.Length];
			for (int c = 0; c < components && c < 4; c++)
			{
				for (int i = 0; i < args.Length; i++)
				{
					Vector4 v = args[i].Vector;
					int argComponents = Math.Max(1, VfxValueTypes.Components(args[i].Type));
					int index = argComponents == 1 ? 0 : c;
					operands[i] = index == 0 ? v.X : index == 1 ? v.Y : index == 2 ? v.Z : v.W;
				}

				result[c] = function(operands);
			}

			return new VfxValue { Type = type, Vector = new Vector4(result[0], result[1], result[2], result[3]) }.ConvertTo(type);
		}

		#endregion

		#region PrivateMethods

		private static Vector4 VectorOf(VfxValue value)
		{
			int components = VfxValueTypes.Components(value.Type);
			Vector4 v = value.Vector;
			if (components < 4) v.W = 0f;
			if (components < 3) v.Z = 0f;
			if (components < 2) v.Y = 0f;
			return v;
		}

		private static void Unary(string name, string template, Func<float, float> function)
		{
			Register(new VfxIntrinsic(name, template, VfxTypeRule.First, VfxValueType.Float, args => ComponentWise(args[0].Type, args, v => function(v[0]))));
		}

		private static void Binary(string name, string template, Func<float, float, float> function)
		{
			Register(new VfxIntrinsic(name, template, VfxTypeRule.Promote, VfxValueType.Float, args => ComponentWise(args[0].Type, args, v => function(v[0], v[1]))));
		}

		private static void Ternary(string name, string template, Func<float, float, float, float> function)
		{
			Register(new VfxIntrinsic(name, template, VfxTypeRule.Promote, VfxValueType.Float, args => ComponentWise(args[0].Type, args, v => function(v[0], v[1], v[2]))));
		}

		private static void Fixed(string name, string template, VfxValueType type, Func<VfxValue[], VfxValue> cpu)
		{
			Register(new VfxIntrinsic(name, template, VfxTypeRule.Fixed, type, cpu));
		}

		private static void Compare(string name, string template, Func<float, float, bool> function)
		{
			Register(new VfxIntrinsic(name, template, VfxTypeRule.Compare, VfxValueType.Bool, args => VfxValue.FromBool(function(args[0].Vector.X, args[1].Vector.X))));
		}

		#endregion
	}

	/// <summary>Formats numbers as GLSL literals.</summary>
	public static class VfxGlsl
	{
		public static string Literal(float value)
		{
			if (float.IsNaN(value) || float.IsInfinity(value))
			{
				value = 0f;
			}

			string text = value.ToString("0.0########", CultureInfo.InvariantCulture);
			return text;
		}

		public static string Literal(VfxValue value)
		{
			Vector4 v = value.Vector;
			switch (value.Type)
			{
				case VfxValueType.Float: return Literal(v.X);
				case VfxValueType.Int: return value.Int.ToString(CultureInfo.InvariantCulture);
				case VfxValueType.Uint: return value.Uint.ToString(CultureInfo.InvariantCulture) + "u";
				case VfxValueType.Bool: return value.Bool ? "true" : "false";
				case VfxValueType.Vector2: return "vec2(" + Literal(v.X) + ", " + Literal(v.Y) + ")";
				case VfxValueType.Vector3: return "vec3(" + Literal(v.X) + ", " + Literal(v.Y) + ", " + Literal(v.Z) + ")";
				case VfxValueType.Vector4:
				case VfxValueType.Color:
					return "vec4(" + Literal(v.X) + ", " + Literal(v.Y) + ", " + Literal(v.Z) + ", " + Literal(v.W) + ")";
				case VfxValueType.Matrix:
				{
					// GLSL mat4 constructor takes columns; OpenTK's rows are the GLSL columns (row-vector convention).
					Matrix4 m = value.Matrix;
					var builder = new StringBuilder("mat4(");
					for (int row = 0; row < 4; row++)
					{
						Vector4 r = row == 0 ? m.Row0 : row == 1 ? m.Row1 : row == 2 ? m.Row2 : m.Row3;
						if (row > 0) builder.Append(", ");
						builder.Append(Literal(r.X)).Append(", ").Append(Literal(r.Y)).Append(", ").Append(Literal(r.Z)).Append(", ").Append(Literal(r.W));
					}

					return builder.Append(')').ToString();
				}
				default:
					return "0.0";
			}
		}
	}
}
