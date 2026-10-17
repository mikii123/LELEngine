using System;
using System.Collections.Generic;
using System.Globalization;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>Types a graph slot, property or attribute can hold (the GLSL type follows, see <see cref="VfxValueTypes" />).</summary>
	public enum VfxValueType
	{
		Float,
		Int,
		Uint,
		Bool,
		Vector2,
		Vector3,
		Vector4,
		Color,
		Gradient,
		Curve,
		Texture2D,
		Texture3D,
		Mesh,

		/// <summary>A 4x4 matrix (transforms); expressions only, never a property.</summary>
		Matrix
	}

	public static class VfxValueTypes
	{
		#region PublicMethods

		/// <summary>The GLSL type of a value type ("float", "vec3", "mat4"; null for resources).</summary>
		public static string Glsl(VfxValueType type)
		{
			switch (type)
			{
				case VfxValueType.Float: return "float";
				case VfxValueType.Int: return "int";
				case VfxValueType.Uint: return "uint";
				case VfxValueType.Bool: return "bool";
				case VfxValueType.Vector2: return "vec2";
				case VfxValueType.Vector3: return "vec3";
				case VfxValueType.Vector4:
				case VfxValueType.Color:
					return "vec4";
				case VfxValueType.Matrix: return "mat4";
				default: return null;
			}
		}

		/// <summary>Float components of a numeric type (0 for resources).</summary>
		public static int Components(VfxValueType type)
		{
			switch (type)
			{
				case VfxValueType.Float:
				case VfxValueType.Int:
				case VfxValueType.Uint:
				case VfxValueType.Bool:
					return 1;
				case VfxValueType.Vector2: return 2;
				case VfxValueType.Vector3: return 3;
				case VfxValueType.Vector4:
				case VfxValueType.Color:
					return 4;
				case VfxValueType.Matrix: return 16;
				default: return 0;
			}
		}

		public static bool IsNumeric(VfxValueType type)
		{
			return type <= VfxValueType.Color || type == VfxValueType.Matrix;
		}

		public static bool IsVector(VfxValueType type)
		{
			return type == VfxValueType.Vector2 || type == VfxValueType.Vector3 || type == VfxValueType.Vector4 || type == VfxValueType.Color;
		}

		public static bool IsScalar(VfxValueType type)
		{
			return type == VfxValueType.Float || type == VfxValueType.Int || type == VfxValueType.Uint || type == VfxValueType.Bool;
		}

		public static bool IsResource(VfxValueType type)
		{
			return type == VfxValueType.Texture2D || type == VfxValueType.Texture3D || type == VfxValueType.Mesh || type == VfxValueType.Gradient || type == VfxValueType.Curve;
		}

		/// <summary>A value of <paramref name="from" /> can feed a slot of <paramref name="to" /> (with the implicit conversions of <see cref="VfxValue.ConvertTo" />).</summary>
		public static bool CanConvert(VfxValueType from, VfxValueType to)
		{
			if (from == to)
			{
				return true;
			}

			if (IsNumeric(from) && IsNumeric(to))
			{
				return from != VfxValueType.Matrix && to != VfxValueType.Matrix;
			}

			return false;
		}

		/// <summary>The wider of two numeric types (binary operators): vectors win over scalars, floats over ints.</summary>
		public static VfxValueType Promote(VfxValueType a, VfxValueType b)
		{
			if (a == b)
			{
				return a;
			}

			int ca = Components(a);
			int cb = Components(b);
			if (ca != cb)
			{
				return ca > cb ? (a == VfxValueType.Color ? VfxValueType.Vector4 : a) : (b == VfxValueType.Color ? VfxValueType.Vector4 : b);
			}

			if (a == VfxValueType.Color || b == VfxValueType.Color)
			{
				return VfxValueType.Vector4;
			}

			if (a == VfxValueType.Float || b == VfxValueType.Float)
			{
				return VfxValueType.Float;
			}

			if (a == VfxValueType.Int || b == VfxValueType.Int)
			{
				return VfxValueType.Int;
			}

			return a;
		}

		/// <summary>The vector type with the given component count (1 = float).</summary>
		public static VfxValueType VectorOf(int components)
		{
			switch (components)
			{
				case 2: return VfxValueType.Vector2;
				case 3: return VfxValueType.Vector3;
				case 4: return VfxValueType.Vector4;
				default: return VfxValueType.Float;
			}
		}

		#endregion
	}

	/// <summary>A key of a color gradient: the color (rgb, linear) and alpha at a time in [0, 1].</summary>
	[Serializable]
	public struct VfxGradientKey
	{
		public float Time;
		public Color4 Color;

		public VfxGradientKey(float time, Color4 color)
		{
			Time = time;
			Color = color;
		}
	}

	/// <summary>A color gradient (Unity's Gradient): color and alpha keys interpolated linearly in time.</summary>
	[Serializable]
	public sealed class VfxGradient
	{
		public List<VfxGradientKey> ColorKeys = new List<VfxGradientKey>();
		public List<VfxGradientKey> AlphaKeys = new List<VfxGradientKey>();

		/// <summary>Most keys stored on the GPU per list (extra keys are dropped at compile time).</summary>
		public const int MaxKeys = 16;

		public static VfxGradient WhiteToTransparent()
		{
			var gradient = new VfxGradient();
			gradient.ColorKeys.Add(new VfxGradientKey(0f, Color4.White));
			gradient.ColorKeys.Add(new VfxGradientKey(1f, Color4.White));
			gradient.AlphaKeys.Add(new VfxGradientKey(0f, new Color4(1f, 1f, 1f, 1f)));
			gradient.AlphaKeys.Add(new VfxGradientKey(1f, new Color4(1f, 1f, 1f, 0f)));
			return gradient;
		}

		public static VfxGradient Solid(Color4 color)
		{
			var gradient = new VfxGradient();
			gradient.ColorKeys.Add(new VfxGradientKey(0f, color));
			gradient.ColorKeys.Add(new VfxGradientKey(1f, color));
			gradient.AlphaKeys.Add(new VfxGradientKey(0f, new Color4(1f, 1f, 1f, color.A)));
			gradient.AlphaKeys.Add(new VfxGradientKey(1f, new Color4(1f, 1f, 1f, color.A)));
			return gradient;
		}

		public VfxGradient Clone()
		{
			var copy = new VfxGradient();
			copy.ColorKeys.AddRange(ColorKeys);
			copy.AlphaKeys.AddRange(AlphaKeys);
			return copy;
		}

		/// <summary>Color (rgb from the color keys, a from the alpha keys) at a time, clamped at the ends.</summary>
		public Color4 Evaluate(float time)
		{
			Color4 color = EvaluateKeys(ColorKeys, time, Color4.White);
			Color4 alpha = EvaluateKeys(AlphaKeys, time, Color4.White);
			return new Color4(color.R, color.G, color.B, alpha.A);
		}

		private static Color4 EvaluateKeys(List<VfxGradientKey> keys, float time, Color4 fallback)
		{
			if (keys == null || keys.Count == 0)
			{
				return fallback;
			}

			if (keys.Count == 1 || time <= keys[0].Time)
			{
				return keys[0].Color;
			}

			for (int i = 1; i < keys.Count; i++)
			{
				if (time <= keys[i].Time)
				{
					float span = keys[i].Time - keys[i - 1].Time;
					float t = span > 1e-6f ? (time - keys[i - 1].Time) / span : 1f;
					Color4 a = keys[i - 1].Color;
					Color4 b = keys[i].Color;
					return new Color4(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);
				}
			}

			return keys[keys.Count - 1].Color;
		}

		/// <summary>Keys sorted by time (the editor appends keys in any order).</summary>
		public void Sort()
		{
			ColorKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
			AlphaKeys.Sort((a, b) => a.Time.CompareTo(b.Time));
		}
	}

	/// <summary>A key of a curve: value at a time, with the tangents of a cubic Hermite segment (Unity's AnimationCurve keyframe).</summary>
	[Serializable]
	public struct VfxCurveKey
	{
		public float Time;
		public float Value;
		public float InTangent;
		public float OutTangent;

		public VfxCurveKey(float time, float value, float inTangent = 0f, float outTangent = 0f)
		{
			Time = time;
			Value = value;
			InTangent = inTangent;
			OutTangent = outTangent;
		}
	}

	/// <summary>A float curve over [0, 1] (Unity's AnimationCurve): cubic Hermite segments between keys, clamped at the ends.</summary>
	[Serializable]
	public sealed class VfxCurve
	{
		public List<VfxCurveKey> Keys = new List<VfxCurveKey>();

		public const int MaxKeys = 16;

		public static VfxCurve Linear(float from, float to)
		{
			var curve = new VfxCurve();
			curve.Keys.Add(new VfxCurveKey(0f, from, to - from, to - from));
			curve.Keys.Add(new VfxCurveKey(1f, to, to - from, to - from));
			return curve;
		}

		public static VfxCurve Constant(float value)
		{
			return Linear(value, value);
		}

		/// <summary>0 at the ends, 1 in the middle (size over life of a puff).</summary>
		public static VfxCurve Bell()
		{
			var curve = new VfxCurve();
			curve.Keys.Add(new VfxCurveKey(0f, 0f, 0f, 4f));
			curve.Keys.Add(new VfxCurveKey(0.5f, 1f, 0f, 0f));
			curve.Keys.Add(new VfxCurveKey(1f, 0f, -4f, 0f));
			return curve;
		}

		public VfxCurve Clone()
		{
			var copy = new VfxCurve();
			copy.Keys.AddRange(Keys);
			return copy;
		}

		public float Evaluate(float time)
		{
			if (Keys == null || Keys.Count == 0)
			{
				return 0f;
			}

			if (Keys.Count == 1 || time <= Keys[0].Time)
			{
				return Keys[0].Value;
			}

			for (int i = 1; i < Keys.Count; i++)
			{
				if (time <= Keys[i].Time)
				{
					VfxCurveKey a = Keys[i - 1];
					VfxCurveKey b = Keys[i];
					float span = b.Time - a.Time;
					if (span <= 1e-6f)
					{
						return b.Value;
					}

					float t = (time - a.Time) / span;
					float t2 = t * t;
					float t3 = t2 * t;
					float h00 = 2f * t3 - 3f * t2 + 1f;
					float h10 = t3 - 2f * t2 + t;
					float h01 = -2f * t3 + 3f * t2;
					float h11 = t3 - t2;
					return h00 * a.Value + h10 * span * a.OutTangent + h01 * b.Value + h11 * span * b.InTangent;
				}
			}

			return Keys[Keys.Count - 1].Value;
		}

		/// <summary>Sets every key's tangents to the slope between its neighbours (smooth curve through the points).</summary>
		public void SmoothTangents()
		{
			Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
			for (int i = 0; i < Keys.Count; i++)
			{
				VfxCurveKey key = Keys[i];
				VfxCurveKey previous = Keys[Math.Max(0, i - 1)];
				VfxCurveKey next = Keys[Math.Min(Keys.Count - 1, i + 1)];
				float span = next.Time - previous.Time;
				float slope = span > 1e-6f ? (next.Value - previous.Value) / span : 0f;
				key.InTangent = slope;
				key.OutTangent = slope;
				Keys[i] = key;
			}
		}
	}

	/// <summary>
	///     A typed value of the graph: a slot default, a property value or a constant. Numbers live in
	///     <see cref="Vector" /> (float in x, int / uint / bool in x, colors as rgba); resources in their own fields.
	/// </summary>
	[Serializable]
	public sealed class VfxValue
	{
		#region PublicFields

		public VfxValueType Type;
		public Vector4 Vector;
		public VfxGradient Gradient;
		public VfxCurve Curve;
		public Texture2D Texture;
		public Texture3D Texture3D;
		public Mesh Mesh;

		/// <summary>Matrix values (expressions evaluated on the CPU only, never saved).</summary>
		[NonSerialized] public Matrix4 Matrix = Matrix4.Identity;

		public float Float
		{
			get => Vector.X;
			set => Vector.X = value;
		}

		public int Int
		{
			get => (int)Math.Round(Vector.X);
			set => Vector.X = value;
		}

		public uint Uint
		{
			get => (uint)Math.Max(0, Math.Round(Vector.X));
			set => Vector.X = value;
		}

		public bool Bool
		{
			get => Vector.X != 0f;
			set => Vector.X = value ? 1f : 0f;
		}

		public Vector2 Vector2
		{
			get => Vector.Xy;
			set => Vector = new Vector4(value.X, value.Y, Vector.Z, Vector.W);
		}

		public Vector3 Vector3
		{
			get => Vector.Xyz;
			set => Vector = new Vector4(value.X, value.Y, value.Z, Vector.W);
		}

		public Color4 Color
		{
			get => new Color4(Vector.X, Vector.Y, Vector.Z, Vector.W);
			set => Vector = new Vector4(value.R, value.G, value.B, value.A);
		}

		#endregion

		#region Constructors

		public VfxValue()
		{ }

		public VfxValue(VfxValueType type)
		{
			Type = type;
			if (type == VfxValueType.Color)
			{
				Vector = Vector4.One;
			}
			else if (type == VfxValueType.Gradient)
			{
				Gradient = VfxGradient.WhiteToTransparent();
			}
			else if (type == VfxValueType.Curve)
			{
				Curve = VfxCurve.Linear(1f, 0f);
			}
		}

		#endregion

		#region PublicMethods

		public static VfxValue FromFloat(float value)
		{
			return new VfxValue { Type = VfxValueType.Float, Vector = new Vector4(value, 0f, 0f, 0f) };
		}

		public static VfxValue FromInt(int value)
		{
			return new VfxValue { Type = VfxValueType.Int, Vector = new Vector4(value, 0f, 0f, 0f) };
		}

		public static VfxValue FromUint(uint value)
		{
			return new VfxValue { Type = VfxValueType.Uint, Vector = new Vector4(value, 0f, 0f, 0f) };
		}

		public static VfxValue FromBool(bool value)
		{
			return new VfxValue { Type = VfxValueType.Bool, Vector = new Vector4(value ? 1f : 0f, 0f, 0f, 0f) };
		}

		public static VfxValue FromVector2(Vector2 value)
		{
			return new VfxValue { Type = VfxValueType.Vector2, Vector = new Vector4(value.X, value.Y, 0f, 0f) };
		}

		public static VfxValue FromVector3(Vector3 value)
		{
			return new VfxValue { Type = VfxValueType.Vector3, Vector = new Vector4(value, 0f) };
		}

		public static VfxValue FromVector3(float x, float y, float z)
		{
			return FromVector3(new Vector3(x, y, z));
		}

		public static VfxValue FromVector4(Vector4 value)
		{
			return new VfxValue { Type = VfxValueType.Vector4, Vector = value };
		}

		public static VfxValue FromColor(Color4 value)
		{
			return new VfxValue { Type = VfxValueType.Color, Vector = new Vector4(value.R, value.G, value.B, value.A) };
		}

		public static VfxValue FromColor(float r, float g, float b, float a = 1f)
		{
			return FromColor(new Color4(r, g, b, a));
		}

		public static VfxValue FromGradient(VfxGradient gradient)
		{
			return new VfxValue { Type = VfxValueType.Gradient, Gradient = gradient };
		}

		public static VfxValue FromCurve(VfxCurve curve)
		{
			return new VfxValue { Type = VfxValueType.Curve, Curve = curve };
		}

		public static VfxValue FromTexture(Texture2D texture)
		{
			return new VfxValue { Type = VfxValueType.Texture2D, Texture = texture };
		}

		public static VfxValue FromMesh(Mesh mesh)
		{
			return new VfxValue { Type = VfxValueType.Mesh, Mesh = mesh };
		}

		public static VfxValue FromMatrix(Matrix4 matrix)
		{
			return new VfxValue { Type = VfxValueType.Matrix, Matrix = matrix };
		}

		/// <summary>A value of a type with a sensible default (1 for sizes and colors, 0 otherwise).</summary>
		public static VfxValue Default(VfxValueType type)
		{
			return new VfxValue(type);
		}

		public VfxValue Clone()
		{
			return new VfxValue
			{
				Type = Type,
				Vector = Vector,
				Gradient = Gradient?.Clone(),
				Curve = Curve?.Clone(),
				Texture = Texture,
				Texture3D = Texture3D,
				Mesh = Mesh,
				Matrix = Matrix
			};
		}

		/// <summary>Copies another value into this one (keeps the object identity, for undo and property overrides).</summary>
		public void CopyFrom(VfxValue other)
		{
			Type = other.Type;
			Vector = other.Vector;
			Gradient = other.Gradient?.Clone();
			Curve = other.Curve?.Clone();
			Texture = other.Texture;
			Texture3D = other.Texture3D;
			Mesh = other.Mesh;
			Matrix = other.Matrix;
		}

		/// <summary>The value as another numeric type: scalars splat into vectors, vectors truncate, ints round.</summary>
		public VfxValue ConvertTo(VfxValueType target)
		{
			if (target == Type)
			{
				return this;
			}

			var result = new VfxValue { Type = target };
			if (!VfxValueTypes.IsNumeric(Type) || !VfxValueTypes.IsNumeric(target))
			{
				result.Gradient = Gradient;
				result.Curve = Curve;
				result.Texture = Texture;
				result.Texture3D = Texture3D;
				result.Mesh = Mesh;
				return result;
			}

			if (target == VfxValueType.Matrix)
			{
				result.Matrix = Matrix4.Identity;
				return result;
			}

			int from = VfxValueTypes.Components(Type);
			int to = VfxValueTypes.Components(target);
			Vector4 v = Vector;
			if (Type == VfxValueType.Matrix)
			{
				v = Vector4.Zero;
			}

			if (from == 1 && to > 1)
			{
				v = new Vector4(v.X, v.X, v.X, target == VfxValueType.Color ? 1f : v.X);
			}
			else if (to > from)
			{
				if (to == 4 && from == 3)
				{
					v.W = target == VfxValueType.Color ? 1f : 0f;
				}
			}
			else if (to < from)
			{
				if (to < 4) v.W = 0f;
				if (to < 3) v.Z = 0f;
				if (to < 2) v.Y = 0f;
			}

			if (target == VfxValueType.Int)
			{
				v.X = (float)Math.Round(v.X);
			}
			else if (target == VfxValueType.Uint)
			{
				v.X = (float)Math.Max(0, Math.Round(v.X));
			}
			else if (target == VfxValueType.Bool)
			{
				v.X = v.X != 0f ? 1f : 0f;
			}

			result.Vector = v;
			return result;
		}

		/// <summary>Structural equality (textures and meshes by reference).</summary>
		public bool ValueEquals(VfxValue other)
		{
			if (other == null || other.Type != Type)
			{
				return false;
			}

			switch (Type)
			{
				case VfxValueType.Texture2D: return Texture == other.Texture;
				case VfxValueType.Texture3D: return Texture3D == other.Texture3D;
				case VfxValueType.Mesh: return Mesh == other.Mesh;
				case VfxValueType.Gradient: return GradientEquals(Gradient, other.Gradient);
				case VfxValueType.Curve: return CurveEquals(Curve, other.Curve);
				case VfxValueType.Matrix: return Matrix == other.Matrix;
				default: return Vector == other.Vector;
			}
		}

		public override string ToString()
		{
			switch (Type)
			{
				case VfxValueType.Float: return Float.ToString("0.###", CultureInfo.InvariantCulture);
				case VfxValueType.Int: return Int.ToString(CultureInfo.InvariantCulture);
				case VfxValueType.Uint: return Uint.ToString(CultureInfo.InvariantCulture);
				case VfxValueType.Bool: return Bool ? "true" : "false";
				case VfxValueType.Vector2: return $"({Vector.X:0.###}, {Vector.Y:0.###})";
				case VfxValueType.Vector3: return $"({Vector.X:0.###}, {Vector.Y:0.###}, {Vector.Z:0.###})";
				case VfxValueType.Vector4:
				case VfxValueType.Color:
					return $"({Vector.X:0.###}, {Vector.Y:0.###}, {Vector.Z:0.###}, {Vector.W:0.###})";
				case VfxValueType.Texture2D: return Texture != null ? System.IO.Path.GetFileName(Texture.AssetPath ?? Texture.name) : "None";
				case VfxValueType.Texture3D: return Texture3D != null ? System.IO.Path.GetFileName(Texture3D.AssetPath ?? Texture3D.name) : "None";
				case VfxValueType.Mesh: return Mesh != null ? System.IO.Path.GetFileName(Mesh.AssetPath) : "None";
				default: return Type.ToString();
			}
		}

		#endregion

		#region PrivateMethods

		private static bool GradientEquals(VfxGradient a, VfxGradient b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}

			if (a.ColorKeys.Count != b.ColorKeys.Count || a.AlphaKeys.Count != b.AlphaKeys.Count)
			{
				return false;
			}

			for (int i = 0; i < a.ColorKeys.Count; i++)
			{
				if (a.ColorKeys[i].Time != b.ColorKeys[i].Time || a.ColorKeys[i].Color != b.ColorKeys[i].Color) return false;
			}

			for (int i = 0; i < a.AlphaKeys.Count; i++)
			{
				if (a.AlphaKeys[i].Time != b.AlphaKeys[i].Time || a.AlphaKeys[i].Color != b.AlphaKeys[i].Color) return false;
			}

			return true;
		}

		private static bool CurveEquals(VfxCurve a, VfxCurve b)
		{
			if (a == null || b == null)
			{
				return a == b;
			}

			if (a.Keys.Count != b.Keys.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Keys.Count; i++)
			{
				VfxCurveKey x = a.Keys[i];
				VfxCurveKey y = b.Keys[i];
				if (x.Time != y.Time || x.Value != y.Value || x.InTangent != y.InTangent || x.OutTangent != y.OutTangent) return false;
			}

			return true;
		}

		#endregion
	}

	/// <summary>The stored value of an input slot that has no connection (the node's inline value).</summary>
	[Serializable]
	public sealed class VfxSlotValue
	{
		public string Name;
		public VfxValue Value;

		public VfxSlotValue()
		{ }

		public VfxSlotValue(string name, VfxValue value)
		{
			Name = name;
			Value = value;
		}
	}

	/// <summary>A node setting (mode, enum choice, number, text): stored as text, parsed by the node's definition.</summary>
	[Serializable]
	public sealed class VfxSetting
	{
		public string Name;
		public string Value;

		public VfxSetting()
		{ }

		public VfxSetting(string name, string value)
		{
			Name = name;
			Value = value;
		}
	}

	/// <summary>
	///     A node of the graph: a context (a stage of a particle system), a block inside a context, or an operator.
	///     The node's behaviour comes from its definition (<see cref="VfxNodeLibrary.Find" />, keyed by <see cref="Type" />);
	///     the instance only stores positions, settings, the inline values of unconnected inputs and, for contexts,
	///     the ordered blocks they contain.
	/// </summary>
	[Serializable]
	public sealed class VfxNodeData
	{
		public ulong Id;

		/// <summary>Definition key ("Operator.Add", "Block.SetAttribute", "Context.Initialize", ...).</summary>
		public string Type;

		public Vector2 Position;
		public bool Collapsed;

		/// <summary>Blocks can be switched off without removing them.</summary>
		public bool Enabled = true;

		/// <summary>Context that owns this block (0 for contexts and operators).</summary>
		public ulong Parent;

		/// <summary>Custom title (contexts: the system name shown in the header); null for the definition's name.</summary>
		public string Title;

		public List<VfxSlotValue> Inputs = new List<VfxSlotValue>();
		public List<VfxSetting> Settings = new List<VfxSetting>();

		/// <summary>Ordered blocks of a context.</summary>
		public List<ulong> Blocks = new List<ulong>();

		public VfxSlotValue FindInput(string name)
		{
			foreach (VfxSlotValue input in Inputs)
			{
				if (input.Name == name)
				{
					return input;
				}
			}

			return null;
		}

		public VfxValue GetInput(string name)
		{
			return FindInput(name)?.Value;
		}

		public void SetInput(string name, VfxValue value)
		{
			VfxSlotValue existing = FindInput(name);
			if (existing != null)
			{
				existing.Value = value;
			}
			else
			{
				Inputs.Add(new VfxSlotValue(name, value));
			}
		}

		public string GetSetting(string name, string fallback = null)
		{
			foreach (VfxSetting setting in Settings)
			{
				if (setting.Name == name)
				{
					return setting.Value ?? fallback;
				}
			}

			return fallback;
		}

		public void SetSetting(string name, string value)
		{
			foreach (VfxSetting setting in Settings)
			{
				if (setting.Name == name)
				{
					setting.Value = value;
					return;
				}
			}

			Settings.Add(new VfxSetting(name, value));
		}

		public bool GetBool(string name, bool fallback)
		{
			string value = GetSetting(name);
			return value == null ? fallback : value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);
		}

		public int GetInt(string name, int fallback)
		{
			string value = GetSetting(name);
			return value != null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
		}

		public float GetFloat(string name, float fallback)
		{
			string value = GetSetting(name);
			return value != null && float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : fallback;
		}

		public override string ToString()
		{
			return Type + " #" + Id;
		}
	}

	/// <summary>A data connection: an output slot of one node feeding an input slot of another.</summary>
	[Serializable]
	public sealed class VfxEdge
	{
		public ulong From;
		public string FromSlot;
		public ulong To;
		public string ToSlot;

		public VfxEdge()
		{ }

		public VfxEdge(ulong from, string fromSlot, ulong to, string toSlot)
		{
			From = from;
			FromSlot = fromSlot;
			To = to;
			ToSlot = toSlot;
		}
	}

	/// <summary>A flow connection between contexts (event to spawn, spawn to initialize, initialize to update, update to output, ...).</summary>
	[Serializable]
	public sealed class VfxFlowEdge
	{
		public ulong From;
		public int FromPort;
		public ulong To;
		public int ToPort;

		public VfxFlowEdge()
		{ }

		public VfxFlowEdge(ulong from, int fromPort, ulong to, int toPort)
		{
			From = from;
			FromPort = fromPort;
			To = to;
			ToPort = toPort;
		}
	}

	/// <summary>
	///     A graph property (the blackboard): a named value every node can read through a Property node, exposed
	///     to the component's inspector and scripts when <see cref="Exposed" /> is set.
	/// </summary>
	[Serializable]
	public sealed class VfxProperty
	{
		public string Name;
		public VfxValue Value;
		public bool Exposed = true;
		public string Tooltip;
		public string Category;
		public bool HasRange;
		public float Min;
		public float Max = 1f;

		public VfxProperty()
		{ }

		public VfxProperty(string name, VfxValue value, bool exposed = true)
		{
			Name = name;
			Value = value;
			Exposed = exposed;
		}

		public VfxProperty Clone()
		{
			return new VfxProperty { Name = Name, Value = Value?.Clone(), Exposed = Exposed, Tooltip = Tooltip, Category = Category, HasRange = HasRange, Min = Min, Max = Max };
		}
	}

	/// <summary>A per-component override of an exposed property (the component inspector's values).</summary>
	[Serializable]
	public sealed class VfxPropertyOverride
	{
		public string Name;
		public VfxValue Value;
		public bool Enabled = true;
	}
}
