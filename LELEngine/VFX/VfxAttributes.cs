using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     A per-particle attribute (Unity's VFX attributes): a named, typed value blocks read and write. Attributes a
	///     system never writes keep their default and cost no storage; the compiler stores the others in the particle
	///     buffer (<see cref="VfxCompiler" />). Custom attributes are declared by the blocks that use them.
	/// </summary>
	public sealed class VfxAttribute
	{
		#region PublicFields

		public readonly string Name;
		public readonly VfxValueType Type;
		public readonly VfxValue Default;
		public readonly string Description;

		/// <summary>Written by the system itself (seed, particle id, age): never a default.</summary>
		public readonly bool Implicit;

		/// <summary>A user attribute declared by a Set Custom Attribute block.</summary>
		public readonly bool Custom;

		/// <summary>GLSL local variable holding the attribute inside a kernel.</summary>
		public string Variable => "attr_" + Name;

		#endregion

		#region Constructors

		public VfxAttribute(string name, VfxValueType type, VfxValue defaultValue, string description, bool implicitWrite = false, bool custom = false)
		{
			Name = name;
			Type = type;
			Default = defaultValue;
			Description = description;
			Implicit = implicitWrite;
			Custom = custom;
		}

		#endregion

		public override string ToString()
		{
			return Name;
		}
	}

	/// <summary>The catalog of standard attributes.</summary>
	public static class VfxAttributes
	{
		#region PublicFields

		public static readonly VfxAttribute Position = new VfxAttribute("position", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Position in the effect's space.");
		public static readonly VfxAttribute Velocity = new VfxAttribute("velocity", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Velocity (units per second); the update context integrates it into the position.");
		public static readonly VfxAttribute Lifetime = new VfxAttribute("lifetime", VfxValueType.Float, VfxValue.FromFloat(1f), "Seconds the particle lives; the update context kills it when its age reaches this.");
		public static readonly VfxAttribute Age = new VfxAttribute("age", VfxValueType.Float, VfxValue.FromFloat(0f), "Seconds since the particle spawned.", true);
		public static readonly VfxAttribute Size = new VfxAttribute("size", VfxValueType.Float, VfxValue.FromFloat(0.1f), "Uniform size (world units) of quads and meshes.");
		public static readonly VfxAttribute Scale = new VfxAttribute("scale", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.One), "Per-axis scale multiplied with the size.");
		public static readonly VfxAttribute Color = new VfxAttribute("color", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.One), "Linear RGB color.");
		public static readonly VfxAttribute Alpha = new VfxAttribute("alpha", VfxValueType.Float, VfxValue.FromFloat(1f), "Opacity.");
		public static readonly VfxAttribute Angle = new VfxAttribute("angle", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Rotation in degrees around x, y, z (quads use z).");
		public static readonly VfxAttribute AngularVelocity = new VfxAttribute("angularVelocity", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Degrees per second; the update context integrates it into the angle.");
		public static readonly VfxAttribute Mass = new VfxAttribute("mass", VfxValueType.Float, VfxValue.FromFloat(1f), "Mass: forces divide by it.");
		public static readonly VfxAttribute TexIndex = new VfxAttribute("texIndex", VfxValueType.Float, VfxValue.FromFloat(0f), "Flipbook frame.");
		public static readonly VfxAttribute Seed = new VfxAttribute("seed", VfxValueType.Uint, VfxValue.FromUint(0), "Random seed of the particle (Random operators derive from it).", true);
		public static readonly VfxAttribute ParticleId = new VfxAttribute("particleId", VfxValueType.Uint, VfxValue.FromUint(0), "Number of the particle since the effect started (unique).", true);
		public static readonly VfxAttribute Direction = new VfxAttribute("direction", VfxValueType.Vector3, VfxValue.FromVector3(0f, 0f, 1f), "A direction the position blocks set (the sphere normal, the cone axis) for velocity blocks to use.");
		public static readonly VfxAttribute TargetPosition = new VfxAttribute("targetPosition", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Target of line outputs and attractors.");
		public static readonly VfxAttribute OldPosition = new VfxAttribute("oldPosition", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Position of the previous frame (collision, strips).", true);
		public static readonly VfxAttribute Pivot = new VfxAttribute("pivot", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Offset of the quad's centre in quad units (-0.5 .. 0.5).");
		public static readonly VfxAttribute SpawnTime = new VfxAttribute("spawnTime", VfxValueType.Float, VfxValue.FromFloat(0f), "Effect time at which the particle spawned.", true);
		public static readonly VfxAttribute Alive = new VfxAttribute("alive", VfxValueType.Bool, VfxValue.FromBool(true), "False kills the particle at the end of the update.", true);
		public static readonly VfxAttribute StripHead = new VfxAttribute("stripHead", VfxValueType.Uint, VfxValue.FromUint(0), "Strip outputs: index of the newest recorded position.", true);
		public static readonly VfxAttribute StripCount = new VfxAttribute("stripCount", VfxValueType.Uint, VfxValue.FromUint(0), "Strip outputs: recorded positions so far.", true);

		public static readonly VfxAttribute[] Standard =
		{
			Position, Velocity, Lifetime, Age, Size, Scale, Color, Alpha, Angle, AngularVelocity, Mass, TexIndex, Seed, ParticleId, Direction, TargetPosition, OldPosition, Pivot, SpawnTime, Alive, StripHead, StripCount
		};

		/// <summary>Attributes blocks may set (the implicit ones excluded): the choices of the Set Attribute block.</summary>
		public static readonly string[] Settable =
		{
			"position", "velocity", "lifetime", "age", "size", "scale", "color", "alpha", "angle", "angularVelocity", "mass", "texIndex", "direction", "targetPosition", "pivot", "alive"
		};

		/// <summary>Attributes operators may read.</summary>
		public static readonly string[] Readable =
		{
			"position", "velocity", "lifetime", "age", "size", "scale", "color", "alpha", "angle", "angularVelocity", "mass", "texIndex", "seed", "particleId", "direction", "targetPosition", "oldPosition", "pivot", "spawnTime", "alive"
		};

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, VfxAttribute> byName = new Dictionary<string, VfxAttribute>(StringComparer.Ordinal);

		#endregion

		#region Constructors

		static VfxAttributes()
		{
			foreach (VfxAttribute attribute in Standard)
			{
				byName[attribute.Name] = attribute;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>A standard attribute by name, or null.</summary>
		public static VfxAttribute Find(string attributeName)
		{
			VfxAttribute attribute;
			return attributeName != null && byName.TryGetValue(attributeName, out attribute) ? attribute : null;
		}

		/// <summary>A custom attribute (user name, sanitized to a GLSL identifier).</summary>
		public static VfxAttribute Custom(string attributeName, VfxValueType type)
		{
			string clean = SanitizeName(attributeName);
			return new VfxAttribute(clean, type, VfxValue.Default(type), "Custom attribute " + attributeName + ".", false, true);
		}

		/// <summary>A GLSL-safe identifier from a user name ("my attr" -> "my_attr").</summary>
		public static string SanitizeName(string attributeName)
		{
			if (string.IsNullOrWhiteSpace(attributeName))
			{
				return "custom";
			}

			var builder = new System.Text.StringBuilder();
			foreach (char c in attributeName.Trim())
			{
				builder.Append(char.IsLetterOrDigit(c) ? c : '_');
			}

			string result = builder.ToString();
			if (char.IsDigit(result[0]))
			{
				result = "_" + result;
			}

			return result;
		}

		#endregion
	}
}
