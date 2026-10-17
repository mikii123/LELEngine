using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     The operator node definitions: math, logic, random, noise, sampling, attribute and built-in reads, properties
	///     and inline values. Arithmetic operators have a "Type" setting (Float by default) that sets the type of their
	///     slots; the editor widens it when a vector is connected.
	/// </summary>
	public static class VfxOperatorNodes
	{
		#region PublicFields

		public static readonly string[] NumericTypes = { "Float", "Vector2", "Vector3", "Vector4", "Color", "Int", "Uint" };
		public static readonly string[] AllTypes = { "Float", "Int", "Uint", "Bool", "Vector2", "Vector3", "Vector4", "Color", "Gradient", "Curve", "Texture2D", "Texture3D", "Mesh" };

		#endregion

		#region PublicMethods

		public static VfxValueType TypeSetting(VfxNodeData node, string fallback = "Float")
		{
			VfxValueType type;
			return Enum.TryParse(node.GetSetting("Type", fallback), true, out type) ? type : VfxValueType.Float;
		}

		public static void Register()
		{
			RegisterArithmetic();
			RegisterVectors();
			RegisterLogic();
			RegisterRandomAndNoise();
			RegisterSampling();
			RegisterAttributesAndBuiltins();
			RegisterValues();
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- helpers

		/// <summary>An operator whose slots take the node's Type setting.</summary>
		private static VfxNodeDefinition Typed(string type, string name, string category, string description, string[] inputs, float[] defaults, VfxOperatorEmitter emit, VfxValueType? outputType = null, string outputName = "Out")
		{
			var definition = new VfxNodeDefinition
			{
				Type = type,
				Name = name,
				Category = category,
				Description = description,
				Kind = VfxNodeKind.Operator,
				Operator = emit
			};
			definition.Settings.Add(VfxSettingDefinition.Enum("Type", "Float", NumericTypes));
			definition.InputsFactory = node =>
			{
				VfxValueType slotType = TypeSetting(node);
				var slots = new List<VfxSlotDefinition>();
				for (int i = 0; i < inputs.Length; i++)
				{
					float d = defaults != null && i < defaults.Length ? defaults[i] : 0f;
					slots.Add(new VfxSlotDefinition(inputs[i], slotType, VfxValue.FromFloat(d).ConvertTo(slotType)));
				}

				return slots;
			};
			definition.OutputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition(outputName, outputType ?? TypeSetting(node)) };
			VfxNodeLibrary.Register(definition);
			return definition;
		}

		/// <summary>An operator with fixed slot types.</summary>
		private static VfxNodeDefinition Fixed(string type, string name, string category, string description, VfxSlotDefinition[] inputs, VfxSlotDefinition[] outputs, VfxOperatorEmitter emit, params VfxSettingDefinition[] settings)
		{
			var definition = new VfxNodeDefinition
			{
				Type = type,
				Name = name,
				Category = category,
				Description = description,
				Kind = VfxNodeKind.Operator,
				Operator = emit,
				InputsFactory = node => new List<VfxSlotDefinition>(inputs),
				OutputsFactory = node => new List<VfxSlotDefinition>(outputs)
			};
			definition.Settings.AddRange(settings);
			VfxNodeLibrary.Register(definition);
			return definition;
		}

		private static VfxSlotDefinition F(string name, float value = 0f, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Float, VfxValue.FromFloat(value), tooltip);
		}

		private static VfxSlotDefinition V3(string name, float x = 0f, float y = 0f, float z = 0f, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Vector3, VfxValue.FromVector3(x, y, z), tooltip);
		}

		private static VfxSlotDefinition V2(string name, float x = 0f, float y = 0f)
		{
			return new VfxSlotDefinition(name, VfxValueType.Vector2, VfxValue.FromVector2(new Vector2(x, y)));
		}

		private static VfxSlotDefinition I(string name, int value = 0, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Int, VfxValue.FromInt(value), tooltip);
		}

		private static VfxSlotDefinition Of(string name, VfxValueType type, string tooltip = null)
		{
			return new VfxSlotDefinition(name, type, null, tooltip);
		}

		private static VfxExpression[] One(VfxExpression e)
		{
			return new[] { e };
		}

		private static void Unary(string key, string name, string category, string intrinsic, string description, string input = "X")
		{
			Typed("Operator." + key, name, category, description, new[] { input }, null, b => One(VfxExpression.Call(intrinsic, b.Input(0))));
		}

		private static void Binary(string key, string name, string category, string intrinsic, string description, float defaultB = 0f)
		{
			Typed("Operator." + key, name, category, description, new[] { "A", "B" }, new[] { 0f, defaultB }, b => One(VfxExpression.Call(intrinsic, b.Input(0), b.Input(1))));
		}

		// ---------------------------------------------------------------- math

		private static void RegisterArithmetic()
		{
			const string arithmetic = "Math/Arithmetic";
			Binary("Add", "Add", arithmetic, "add", "A + B.");
			Binary("Subtract", "Subtract", arithmetic, "sub", "A - B.");
			Binary("Multiply", "Multiply", arithmetic, "mul", "A * B.", 1f);
			Binary("Divide", "Divide", arithmetic, "div", "A / B.", 1f);
			Binary("Modulo", "Modulo", arithmetic, "mod", "A modulo B (positive, like GLSL mod).", 1f);
			Typed("Operator.MultiplyAdd", "Multiply Add", arithmetic, "A * B + C.", new[] { "A", "B", "C" }, new[] { 0f, 1f, 0f }, b => One(VfxExpression.Add(VfxExpression.Mul(b.Input(0), b.Input(1)), b.Input(2))));
			Unary("Negate", "Negate", arithmetic, "neg", "-X.");
			Unary("Reciprocal", "Reciprocal", arithmetic, "reciprocal", "1 / X.");
			Unary("Absolute", "Absolute", arithmetic, "abs", "|X|.");
			Unary("Sign", "Sign", arithmetic, "sign", "-1, 0 or 1.");
			Unary("Square", "Square", arithmetic, "square", "X * X.");
			Unary("SquareRoot", "Square Root", arithmetic, "sqrt", "Square root (0 for negative inputs).");
			Binary("Power", "Power", arithmetic, "pow", "A to the power B.", 2f);
			Unary("Exponential", "Exponential", arithmetic, "exp", "e to the power X.");
			Unary("Logarithm", "Logarithm", arithmetic, "log", "Natural logarithm.");
			Unary("OneMinus", "One Minus", arithmetic, "oneMinus", "1 - X.");

			const string clamp = "Math/Clamp";
			Typed("Operator.Clamp", "Clamp", clamp, "X limited to [Min, Max].", new[] { "X", "Min", "Max" }, new[] { 0f, 0f, 1f }, b => One(VfxExpression.Call("clamp", b.Input(0), b.Input(1), b.Input(2))));
			Unary("Saturate", "Saturate", clamp, "saturate", "X limited to [0, 1].");
			Binary("Minimum", "Minimum", clamp, "min", "The smaller of A and B (per component).");
			Binary("Maximum", "Maximum", clamp, "max", "The larger of A and B (per component).");
			Unary("Floor", "Floor", clamp, "floor", "Rounded down.");
			Unary("Ceiling", "Ceiling", clamp, "ceil", "Rounded up.");
			Unary("Round", "Round", clamp, "round", "Rounded to the nearest integer.");
			Unary("Fraction", "Fraction", clamp, "frac", "X - floor(X).");

			const string interpolation = "Math/Interpolation";
			Typed("Operator.Lerp", "Lerp", interpolation, "Linear interpolation from A to B by T.", new[] { "A", "B", "T" }, new[] { 0f, 1f, 0.5f }, b => One(VfxExpression.Call("lerpVector", b.Input(0), b.Input(1), b.Input(2))));
			Typed("Operator.Smoothstep", "Smoothstep", interpolation, "Smooth Hermite step of X between A and B.", new[] { "A", "B", "X" }, new[] { 0f, 1f, 0.5f }, b => One(VfxExpression.Call("smoothstep", b.Input(0), b.Input(1), b.Input(2))));
			Typed("Operator.Step", "Step", interpolation, "1 where X >= Edge, else 0.", new[] { "Edge", "X" }, new[] { 0.5f, 0f }, b => One(VfxExpression.Call("step", b.Input(0), b.Input(1))));
			Typed("Operator.Remap", "Remap", interpolation, "Maps X from [Old Min, Old Max] onto [New Min, New Max].", new[] { "X", "Old Min", "Old Max", "New Min", "New Max" }, new[] { 0f, 0f, 1f, 0f, 1f },
				b => One(VfxExpression.Call("remap", b.Input(0), b.Input(1), b.Input(2), b.Input(3), b.Input(4))));
			Typed("Operator.Remap01", "Remap To 0-1", interpolation, "Maps X from [Min, Max] onto [0, 1], clamped.", new[] { "X", "Min", "Max" }, new[] { 0f, 0f, 1f },
				b => One(VfxExpression.Saturate(VfxExpression.Call("remap", b.Input(0), b.Input(1), b.Input(2), VfxExpression.Float(0f), VfxExpression.Float(1f)))));
			Typed("Operator.InverseLerp", "Inverse Lerp", interpolation, "Where X lies between A and B (0 at A, 1 at B).", new[] { "A", "B", "X" }, new[] { 0f, 1f, 0.5f },
				b => One(VfxExpression.Call("remap", b.Input(2), b.Input(0), b.Input(1), VfxExpression.Float(0f), VfxExpression.Float(1f))));

			const string trigonometry = "Math/Trigonometry";
			Unary("Sine", "Sine", trigonometry, "sin", "sin(X), X in radians.");
			Unary("Cosine", "Cosine", trigonometry, "cos", "cos(X), X in radians.");
			Unary("Tangent", "Tangent", trigonometry, "tan", "tan(X), X in radians.");
			Unary("Arcsine", "Arcsine", trigonometry, "asin", "asin(X) in radians.");
			Unary("Arccosine", "Arccosine", trigonometry, "acos", "acos(X) in radians.");
			Unary("Arctangent", "Arctangent", trigonometry, "atan", "atan(X) in radians.");
			Typed("Operator.Arctangent2", "Arctangent 2", trigonometry, "atan2(Y, X) in radians.", new[] { "Y", "X" }, new[] { 0f, 1f }, b => One(VfxExpression.Call("atan2", b.Input(0), b.Input(1))));
			Unary("DegreesToRadians", "Degrees To Radians", trigonometry, "radians", "Degrees to radians.");
			Unary("RadiansToDegrees", "Radians To Degrees", trigonometry, "degrees", "Radians to degrees.");

			Fixed("Operator.Pi", "Pi", "Math/Constants", "Pi, two Pi and half Pi.", new VfxSlotDefinition[0],
				new[] { F("Pi"), F("Two Pi"), F("Half Pi") },
				b => new[] { VfxExpression.Float(MathF.PI), VfxExpression.Float(MathF.PI * 2f), VfxExpression.Float(MathF.PI * 0.5f) });
			Fixed("Operator.Epsilon", "Epsilon", "Math/Constants", "A tiny positive number (1e-5).", new VfxSlotDefinition[0], new[] { F("Epsilon") }, b => One(VfxExpression.Float(1e-5f)));
		}

		// ---------------------------------------------------------------- vectors

		private static void RegisterVectors()
		{
			const string vector = "Math/Vector";
			Typed("Operator.Length", "Length", vector, "Length of the vector.", new[] { "X" }, null, b => One(VfxExpression.Length(b.Input(0))), VfxValueType.Float);
			Typed("Operator.Distance", "Distance", vector, "Distance between A and B.", new[] { "A", "B" }, null, b => One(VfxExpression.Call("distance", b.Input(0), b.Input(1))), VfxValueType.Float);
			Typed("Operator.Normalize", "Normalize", vector, "The vector with length 1 (zero stays zero).", new[] { "X" }, null, b => One(VfxExpression.Normalize(b.Input(0))));
			Typed("Operator.Dot", "Dot Product", vector, "A . B.", new[] { "A", "B" }, null, b => One(VfxExpression.Dot(b.Input(0), b.Input(1))), VfxValueType.Float);
			Fixed("Operator.Cross", "Cross Product", vector, "A x B.", new[] { V3("A", 1f), V3("B", 0f, 1f) }, new[] { V3("Out") }, b => One(VfxExpression.Cross(b.Input(0), b.Input(1))));
			Typed("Operator.Reflect", "Reflect", vector, "The vector reflected on a surface with the (normalized) normal.", new[] { "Vector", "Normal" }, null, b => One(VfxExpression.Call("reflect", b.Input(0), b.Input(1))));
			Typed("Operator.Project", "Project", vector, "The part of the vector along another.", new[] { "Vector", "Onto" }, null, b => One(VfxExpression.Call("project", b.Input(0), b.Input(1))));
			Fixed("Operator.Rotate2D", "Rotate 2D", vector, "Rotates a 2D vector by an angle (radians).", new[] { V2("Vector", 1f, 0f), F("Angle") }, new[] { V2("Out") }, b => One(VfxExpression.Call("rotate2D", b.Input(0), b.Input(1))));
			Fixed("Operator.Rotate3D", "Rotate 3D", vector, "Rotates a vector around an axis by an angle (radians).", new[] { V3("Vector", 1f), V3("Axis", 0f, 1f, 0f), F("Angle") }, new[] { V3("Out") },
				b => One(VfxExpression.Call("rotateAxis", b.Input(0), b.Input(1), b.Input(2))));

			Fixed("Operator.Combine", "Combine", vector, "A vector from separate components.", new[] { F("X"), F("Y"), F("Z"), F("W") }, new[] { V2("Vector2"), V3("Vector3"), Of("Vector4", VfxValueType.Vector4) },
				b => new[]
				{
					VfxExpression.Construct(VfxValueType.Vector2, b.Input(0), b.Input(1)),
					VfxExpression.Construct(VfxValueType.Vector3, b.Input(0), b.Input(1), b.Input(2)),
					VfxExpression.Construct(VfxValueType.Vector4, b.Input(0), b.Input(1), b.Input(2), b.Input(3))
				});
			Fixed("Operator.Split", "Split", vector, "The components of a vector.", new[] { Of("Vector", VfxValueType.Vector4) }, new[] { F("X"), F("Y"), F("Z"), F("W") },
				b => new[] { VfxExpression.X(b.Input(0)), VfxExpression.Y(b.Input(0)), VfxExpression.Z(b.Input(0)), VfxExpression.W(b.Input(0)) });
			var swizzle = Fixed("Operator.Swizzle", "Swizzle", vector, "Reorders and picks components (a mask of x, y, z, w).", new[] { Of("Vector", VfxValueType.Vector4) }, new[] { Of("Out", VfxValueType.Vector4) },
				b => One(VfxExpression.Swizzle(b.Input(0), CleanMask(b.Setting("Mask", "xyz")))),
				new VfxSettingDefinition("Mask", VfxSettingKind.Text, "xyz", null, "Components to pick, e.g. xzy, xy, zzz.", true));
			swizzle.OutputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition("Out", VfxValueTypes.VectorOf(CleanMask(node.GetSetting("Mask", "xyz")).Length)) };
			Fixed("Operator.Append", "Append Vector", vector, "A vector with one more component.", new[] { V3("Vector"), F("W", 1f) }, new[] { Of("Out", VfxValueType.Vector4) },
				b => One(VfxExpression.Construct(VfxValueType.Vector4, b.Input(0), b.Input(1))));

			const string transform = "Math/Transform";
			Fixed("Operator.LocalToWorld", "Local To World", transform, "The component's transform (local space to world space).", new VfxSlotDefinition[0], new[] { Of("Matrix", VfxValueType.Matrix) },
				b => One(VfxExpression.Builtin(VfxBuiltins.LocalToWorld)));
			Fixed("Operator.WorldToLocal", "World To Local", transform, "The inverse of the component's transform.", new VfxSlotDefinition[0], new[] { Of("Matrix", VfxValueType.Matrix) },
				b => One(VfxExpression.Builtin(VfxBuiltins.WorldToLocal)));
			Fixed("Operator.TransformPosition", "Transform Position", transform, "A position transformed by a matrix.", new[] { Of("Matrix", VfxValueType.Matrix), V3("Position") }, new[] { V3("Out") },
				b => One(VfxExpression.TransformPoint(b.Input(0), b.Input(1))));
			Fixed("Operator.TransformDirection", "Transform Direction", transform, "A direction turned by a matrix (normalized).", new[] { Of("Matrix", VfxValueType.Matrix), V3("Direction", 0f, 0f, 1f) }, new[] { V3("Out") },
				b => One(VfxExpression.TransformDirection(b.Input(0), b.Input(1))));
			Fixed("Operator.TransformVector", "Transform Vector", transform, "A vector turned and scaled by a matrix.", new[] { Of("Matrix", VfxValueType.Matrix), V3("Vector", 0f, 0f, 1f) }, new[] { V3("Out") },
				b => One(VfxExpression.Call("transformVector", b.Input(0), b.Input(1))));
			Fixed("Operator.ChangeSpace", "Change Space", transform, "A position or direction moved between the component's local space and world space.", new[] { V3("Value") }, new[] { V3("Out") }, b =>
				{
					bool toWorld = b.Setting("Direction", "LocalToWorld") == "LocalToWorld";
					bool position = b.Setting("Kind", "Position") == "Position";
					VfxExpression matrix = VfxExpression.Builtin(toWorld ? VfxBuiltins.LocalToWorld : VfxBuiltins.WorldToLocal);
					return One(position ? VfxExpression.TransformPoint(matrix, b.Input(0)) : VfxExpression.TransformDirection(matrix, b.Input(0)));
				},
				VfxSettingDefinition.Enum("Direction", "LocalToWorld", "LocalToWorld", "WorldToLocal"), VfxSettingDefinition.Enum("Kind", "Position", "Position", "Direction"));
			Fixed("Operator.ComposeTransform", "Compose Transform", transform, "A matrix from a position, Euler angles (degrees) and a scale.", new[] { V3("Position"), V3("Angles"), V3("Scale", 1f, 1f, 1f) }, new[] { Of("Matrix", VfxValueType.Matrix) },
				b => One(VfxExpression.Call("composeTransform", b.Input(0), b.Input(1), b.Input(2))));
			Fixed("Operator.InverseTransform", "Inverse Transform", transform, "The inverse of a matrix.", new[] { Of("Matrix", VfxValueType.Matrix) }, new[] { Of("Matrix", VfxValueType.Matrix) },
				b => One(VfxExpression.Call("inverse", b.Input(0))));
			Fixed("Operator.MultiplyTransform", "Multiply Transform", transform, "A * B (B applied first).", new[] { Of("A", VfxValueType.Matrix), Of("B", VfxValueType.Matrix) }, new[] { Of("Matrix", VfxValueType.Matrix) },
				b => One(VfxExpression.Call("mulMatrix", b.Input(0), b.Input(1))));

			const string coordinates = "Math/Coordinates";
			Fixed("Operator.PolarToRectangular", "Polar To Rectangular", coordinates, "A 2D point from an angle (radians) and a distance.", new[] { F("Angle"), F("Distance", 1f) }, new[] { V2("Out") },
				b => One(VfxExpression.Call("polarToRectangular", b.Input(0), b.Input(1))));
			Fixed("Operator.RectangularToPolar", "Rectangular To Polar", coordinates, "Angle (radians) and distance of a 2D point.", new[] { V2("Point", 1f, 0f) }, new[] { F("Angle"), F("Distance") },
				b =>
				{
					VfxExpression polar = VfxExpression.Call("rectangularToPolar", b.Input(0));
					return new[] { VfxExpression.X(polar), VfxExpression.Y(polar) };
				});
			Fixed("Operator.SphericalToRectangular", "Spherical To Rectangular", coordinates, "A 3D point from the angle around Y (theta), the angle from Y (phi) and a distance.", new[] { F("Theta"), F("Phi"), F("Distance", 1f) }, new[] { V3("Out") },
				b => One(VfxExpression.Call("sphericalToRectangular", b.Input(0), b.Input(1), b.Input(2))));
			Fixed("Operator.RectangularToSpherical", "Rectangular To Spherical", coordinates, "Theta, phi and distance of a 3D point.", new[] { V3("Point", 1f) }, new[] { F("Theta"), F("Phi"), F("Distance") },
				b =>
				{
					VfxExpression spherical = VfxExpression.Call("rectangularToSpherical", b.Input(0));
					return new[] { VfxExpression.X(spherical), VfxExpression.Y(spherical), VfxExpression.Z(spherical) };
				});
		}

		private static string CleanMask(string mask)
		{
			var builder = new System.Text.StringBuilder();
			foreach (char c in (mask ?? "").ToLowerInvariant())
			{
				char m = c == 'r' ? 'x' : c == 'g' ? 'y' : c == 'b' ? 'z' : c == 'a' ? 'w' : c;
				if ((m == 'x' || m == 'y' || m == 'z' || m == 'w') && builder.Length < 4)
				{
					builder.Append(m);
				}
			}

			return builder.Length > 0 ? builder.ToString() : "x";
		}

		// ---------------------------------------------------------------- logic

		private static void RegisterLogic()
		{
			const string logic = "Logic";
			var compare = Typed("Operator.Compare", "Compare", logic, "Compares A with B (first components).", new[] { "A", "B" }, null, b =>
			{
				string op = b.Setting("Operation", "Greater");
				string intrinsic = op switch
				{
					"Less" => "less",
					"LessOrEqual" => "lessEqual",
					"GreaterOrEqual" => "greaterEqual",
					"Equal" => "equal",
					"NotEqual" => "notEqual",
					_ => "greater"
				};
				return One(VfxExpression.Call(intrinsic, VfxExpression.ConvertTo(b.Input(0), VfxValueType.Float), VfxExpression.ConvertTo(b.Input(1), VfxValueType.Float)));
			}, VfxValueType.Bool);
			compare.Settings.Add(VfxSettingDefinition.Enum("Operation", "Greater", "Less", "LessOrEqual", "Greater", "GreaterOrEqual", "Equal", "NotEqual"));

			var branch = Typed("Operator.Branch", "Branch", logic, "True or False depending on the predicate.", new[] { "True", "False" }, new[] { 1f, 0f }, b => One(VfxExpression.Select(b.Input("Predicate"), b.Input("True"), b.Input("False"))));
			var branchInputs = branch.InputsFactory;
			branch.InputsFactory = node =>
			{
				List<VfxSlotDefinition> slots = branchInputs(node);
				slots.Insert(0, new VfxSlotDefinition("Predicate", VfxValueType.Bool, VfxValue.FromBool(true)));
				return slots;
			};

			Fixed("Operator.And", "And", logic, "A and B.", new[] { Of("A", VfxValueType.Bool), Of("B", VfxValueType.Bool) }, new[] { Of("Out", VfxValueType.Bool) }, b => One(VfxExpression.Call("and", b.Input(0), b.Input(1))));
			Fixed("Operator.Or", "Or", logic, "A or B.", new[] { Of("A", VfxValueType.Bool), Of("B", VfxValueType.Bool) }, new[] { Of("Out", VfxValueType.Bool) }, b => One(VfxExpression.Call("or", b.Input(0), b.Input(1))));
			Fixed("Operator.Not", "Not", logic, "Not A.", new[] { Of("A", VfxValueType.Bool) }, new[] { Of("Out", VfxValueType.Bool) }, b => One(VfxExpression.Call("not", b.Input(0))));
		}

		// ---------------------------------------------------------------- random and noise

		private static void RegisterRandomAndNoise()
		{
			const string random = "Random";
			var randomNumber = Typed("Operator.Random", "Random Number", random, "A random value in [Min, Max): per particle (new every evaluation), per component (fixed while it plays) or constant.", new[] { "Min", "Max" }, new[] { 0f, 1f }, b =>
			{
				VfxRandomMode mode;
				if (!Enum.TryParse(b.Setting("Mode", "PerParticle"), true, out mode)) mode = VfxRandomMode.PerParticle;
				uint salt = b.Salt;
				VfxExpression seed = b.Input("Seed");
				if (seed.Kind == VfxExprKind.Constant)
				{
					salt ^= seed.Constant.Uint * 0x9E3779B9u;
					return One(VfxExpression.Random(mode, salt, TypeSetting(b.Node), b.Input("Min"), b.Input("Max")));
				}

				// A connected seed: hash it into the salt on the GPU.
				VfxExpression hashed = VfxExpression.Call("hashUint", VfxExpression.ConvertTo(seed, VfxValueType.Uint));
				VfxExpression r = VfxExpression.Random(mode, salt, TypeSetting(b.Node), b.Input("Min"), b.Input("Max"));
				return One(VfxExpression.Raw(r.Type, "mix(" + "{0}" + ", {1}, " + RandomCall(r.Type, "({2} ^ " + salt + "u)") + ")", b.Input("Min"), b.Input("Max"), hashed));
			});
			randomNumber.Settings.Add(VfxSettingDefinition.Enum("Mode", "PerParticle", "PerParticle", "PerComponent", "Constant"));
			var randomInputs = randomNumber.InputsFactory;
			randomNumber.InputsFactory = node =>
			{
				List<VfxSlotDefinition> slots = randomInputs(node);
				slots.Add(new VfxSlotDefinition("Seed", VfxValueType.Uint, VfxValue.FromUint(0), "Changes the sequence (0 = the node's own)."));
				return slots;
			};

			Fixed("Operator.RandomDirection", "Random Direction", random, "A uniformly random unit vector (per particle).", new VfxSlotDefinition[0], new[] { V3("Out") },
				b => One(VfxExpression.Raw(VfxValueType.Vector3, "VfxRandomOnSphere(" + SeedOf() + ", " + b.Salt + "u)")));
			Fixed("Operator.RandomPointInSphere", "Random Point In Sphere", random, "A uniformly random point of the unit ball (per particle).", new VfxSlotDefinition[0], new[] { V3("Out") },
				b => One(VfxExpression.Raw(VfxValueType.Vector3, "VfxRandomInSphere(" + SeedOf() + ", " + b.Salt + "u)")));

			const string noise = "Noise";
			Fixed("Operator.PerlinNoise", "Perlin Noise", noise, "Fractal Perlin noise in [-1, 1] of a 3D coordinate.", new[] { V3("Coordinate"), F("Frequency", 1f), I("Octaves", 1), F("Roughness", 0.5f) }, new[] { F("Out") },
				b => One(VfxExpression.Call("perlin", b.Input(0), b.Input(1), b.Input(2), b.Input(3))));
			Fixed("Operator.PerlinNoise3D", "Perlin Noise (Vector)", noise, "Three independent fractal Perlin noises: a vector in [-1, 1]^3.", new[] { V3("Coordinate"), F("Frequency", 1f), I("Octaves", 1), F("Roughness", 0.5f) }, new[] { V3("Out") },
				b => One(VfxExpression.Call("perlin3", b.Input(0), b.Input(1), b.Input(2), b.Input(3))));
			Fixed("Operator.ValueNoise", "Value Noise", noise, "Fractal value noise in [-1, 1] (cheaper, blockier).", new[] { V3("Coordinate"), F("Frequency", 1f), I("Octaves", 1), F("Roughness", 0.5f) }, new[] { F("Out") },
				b => One(VfxExpression.Call("valueNoise", b.Input(0), b.Input(1), b.Input(2), b.Input(3))));
			Fixed("Operator.CurlNoise", "Curl Noise", noise, "A divergence-free vector field (swirling turbulence).", new[] { V3("Coordinate"), F("Frequency", 1f), I("Octaves", 1) }, new[] { V3("Out") },
				b => One(VfxExpression.Call("curlNoise", b.Input(0), b.Input(1), b.Input(2))));
		}

		private static string SeedOf()
		{
			return "attr_seed";
		}

		private static string RandomCall(VfxValueType type, string seed)
		{
			switch (VfxValueTypes.Components(type))
			{
				case 2: return "VfxRandom2(attr_seed, " + seed + ")";
				case 3: return "VfxRandom3(attr_seed, " + seed + ")";
				case 4: return "VfxRandom4(attr_seed, " + seed + ")";
				default: return "VfxRandom01(attr_seed, " + seed + ")";
			}
		}

		// ---------------------------------------------------------------- sampling

		private static void RegisterSampling()
		{
			const string sampling = "Sampling";
			Fixed("Operator.SampleGradient", "Sample Gradient", sampling, "The color of a gradient at T (0..1).", new[] { Of("Gradient", VfxValueType.Gradient), F("T") }, new[] { Of("Color", VfxValueType.Color) },
				b => One(VfxExpression.SampleGradient(b.Input(0), b.Input(1))));
			Fixed("Operator.SampleCurve", "Sample Curve", sampling, "The value of a curve at T (0..1).", new[] { Of("Curve", VfxValueType.Curve), F("T") }, new[] { F("Value") },
				b => One(VfxExpression.SampleCurve(b.Input(0), b.Input(1))));
			Fixed("Operator.SampleTexture2D", "Sample Texture 2D", sampling, "A texture sampled at UV (mip level 0).", new[] { Of("Texture", VfxValueType.Texture2D), V2("UV", 0.5f, 0.5f) }, new[] { Of("Color", VfxValueType.Color) },
				b => One(VfxExpression.SampleTexture2D(b.Input(0), b.Input(1))));
			Fixed("Operator.SampleTexture2DLod", "Sample Texture 2D LOD", sampling, "A texture sampled at UV and a mip level.", new[] { Of("Texture", VfxValueType.Texture2D), V2("UV", 0.5f, 0.5f), F("LOD") }, new[] { Of("Color", VfxValueType.Color) },
				b => One(VfxExpression.SampleTexture2D(b.Input(0), b.Input(1), b.Input(2))));
			Fixed("Operator.SampleTexture3D", "Sample Texture 3D", sampling, "A volume texture sampled at UVW.", new[] { Of("Texture", VfxValueType.Texture3D), V3("UVW", 0.5f, 0.5f, 0.5f) }, new[] { Of("Color", VfxValueType.Color) },
				b => One(VfxExpression.SampleTexture3D(b.Input(0), b.Input(1))));
			Fixed("Operator.SampleMesh", "Sample Mesh", sampling, "Position, normal and UV of a mesh vertex (the index wraps around the vertex count).", new[] { Of("Mesh", VfxValueType.Mesh), new VfxSlotDefinition("Index", VfxValueType.Uint, VfxValue.FromUint(0)) },
				new[] { V3("Position"), V3("Normal"), V2("UV"), new VfxSlotDefinition("Vertex Count", VfxValueType.Uint) },
				b => new[]
				{
					VfxExpression.MeshVertex(b.Input(0), b.Input(1), "position"),
					VfxExpression.MeshVertex(b.Input(0), b.Input(1), "normal"),
					VfxExpression.MeshVertex(b.Input(0), b.Input(1), "uv"),
					VfxExpression.MeshVertex(b.Input(0), b.Input(1), "count")
				});
			Fixed("Operator.SampleSdf", "Sample Global SDF", sampling, "Distance to the nearest scene surface and its normal at a world position (the GI's global distance field).", new[] { V3("Position") },
				new[] { F("Distance"), V3("Normal") },
				b => new[] { VfxExpression.Call("sampleSdf", b.Input(0)), VfxExpression.Call("sdfNormal", b.Input(0)) });
		}

		// ---------------------------------------------------------------- attributes and built-ins

		private static void RegisterAttributesAndBuiltins()
		{
			const string attribute = "Attribute";
			var get = new VfxNodeDefinition
			{
				Type = "Operator.GetAttribute",
				Name = "Get Attribute",
				Category = attribute,
				Description = "Reads a particle attribute (the current value; Source reads the parent particle's value in a GPU-event child).",
				Kind = VfxNodeKind.Operator,
				Settings =
				{
					new VfxSettingDefinition("Attribute", VfxSettingKind.Attribute, "position", VfxAttributes.Readable, null, true),
					VfxSettingDefinition.Enum("Location", "Current", "Current", "Source")
				},
				InputsFactory = node => new List<VfxSlotDefinition>(),
				OutputsFactory = node =>
				{
					VfxAttribute a = VfxAttributes.Find(node.GetSetting("Attribute", "position")) ?? VfxAttributes.Position;
					return new List<VfxSlotDefinition> { new VfxSlotDefinition(a.Name, a.Type) };
				},
				TitleFactory = node => "Get " + node.GetSetting("Attribute", "position") + (node.GetSetting("Location", "Current") == "Source" ? " (source)" : ""),
				Operator = b =>
				{
					VfxAttribute a = VfxAttributes.Find(b.Setting("Attribute", "position")) ?? VfxAttributes.Position;
					bool source = b.Setting("Location", "Current") == "Source";
					return One(source ? VfxExpression.SourceAttribute(a) : VfxExpression.Attribute(a));
				}
			};
			VfxNodeLibrary.Register(get);

			Fixed("Operator.AgeOverLifetime", "Age Over Lifetime", attribute, "age / lifetime, 0 at birth and 1 at death.", new VfxSlotDefinition[0], new[] { F("Ratio") },
				b => One(VfxExpression.Saturate(VfxExpression.Div(VfxExpression.Attribute(VfxAttributes.Age), VfxExpression.Max(VfxExpression.Attribute(VfxAttributes.Lifetime), VfxExpression.Float(1e-5f))))));

			const string builtin = "Built-in";
			Fixed("Operator.Time", "Time", builtin, "The effect's time (seconds since it started playing, scaled by the play rate), the frame time and the frame index.", new VfxSlotDefinition[0],
				new[] { F("Total Time"), F("Delta Time"), new VfxSlotDefinition("Frame Index", VfxValueType.Uint), F("Play Rate") },
				b => new[] { VfxExpression.Builtin(VfxBuiltins.TotalTime), VfxExpression.Builtin(VfxBuiltins.DeltaTime), VfxExpression.Builtin(VfxBuiltins.FrameIndex), VfxExpression.Builtin(VfxBuiltins.PlayRate) });
			Fixed("Operator.Camera", "Main Camera", builtin, "Position and axes of the camera the effect is simulated for.", new VfxSlotDefinition[0],
				new[] { V3("Position"), V3("Forward"), V3("Up"), V3("Right") },
				b => new[] { VfxExpression.Builtin(VfxBuiltins.CameraPosition), VfxExpression.Builtin(VfxBuiltins.CameraForward), VfxExpression.Builtin(VfxBuiltins.CameraUp), VfxExpression.Builtin(VfxBuiltins.CameraRight) });
			Fixed("Operator.SpawnContext", "Spawn Context", builtin, "In initialize contexts: the particle's index among those spawned this frame, and their number.", new VfxSlotDefinition[0],
				new[] { new VfxSlotDefinition("Spawn Index", VfxValueType.Uint), new VfxSlotDefinition("Spawn Count", VfxValueType.Uint), F("Spawn Index Ratio", 0f, "spawnIndex / spawnCount") },
				b => new[]
				{
					VfxExpression.Builtin(VfxBuiltins.SpawnIndex),
					VfxExpression.Builtin(VfxBuiltins.SpawnCount),
					VfxExpression.Div(VfxExpression.ConvertTo(VfxExpression.Builtin(VfxBuiltins.SpawnIndex), VfxValueType.Float), VfxExpression.Max(VfxExpression.ConvertTo(VfxExpression.Builtin(VfxBuiltins.SpawnCount), VfxValueType.Float), VfxExpression.Float(1f)))
				});
			Fixed("Operator.ParticleIndex", "Particle Index", builtin, "The particle's slot in the buffer (0..capacity) and its unique id.", new VfxSlotDefinition[0],
				new[] { new VfxSlotDefinition("Index", VfxValueType.Uint), new VfxSlotDefinition("Id", VfxValueType.Uint) },
				b => new[] { VfxExpression.Builtin(VfxBuiltins.ParticleIndex), VfxExpression.Attribute(VfxAttributes.ParticleId) });
			Fixed("Operator.SystemInfo", "System Info", builtin, "The system's seed and capacity.", new VfxSlotDefinition[0],
				new[] { new VfxSlotDefinition("Seed", VfxValueType.Uint), new VfxSlotDefinition("Capacity", VfxValueType.Uint) },
				b => new[] { VfxExpression.Builtin(VfxBuiltins.SystemSeed), VfxExpression.Builtin(VfxBuiltins.Capacity) });
			Fixed("Operator.SpawnerTime", "Spawner Time", builtin, "In spawn contexts: seconds since the current loop started and the progress through the loop (0..1).", new VfxSlotDefinition[0],
				new[] { F("Time"), F("Loop Progress") },
				b => new[] { VfxExpression.Builtin(VfxBuiltins.SpawnerTime), VfxExpression.Builtin(VfxBuiltins.LoopProgress) });
		}

		// ---------------------------------------------------------------- values

		private static void RegisterValues()
		{
			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = VfxNodeLibrary.PropertyNodeType,
				Name = "Property",
				Category = "Property",
				Description = "Reads a property of the blackboard (exposed properties can be set per component and from scripts).",
				Kind = VfxNodeKind.Operator,
				Settings = { new VfxSettingDefinition("Property", VfxSettingKind.Property, "", null, null, true) },
				InputsFactory = node => new List<VfxSlotDefinition>(),
				OutputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition("Value", PropertyType(node)) },
				TitleFactory = node => node.GetSetting("Property", "Property"),
				Operator = b =>
				{
					string name = b.Setting("Property", "");
					VfxProperty property = b.Compiler.Graph.FindProperty(name);
					if (property == null)
					{
						b.Error("The property '" + name + "' does not exist");
						return One(VfxExpression.Float(0f));
					}

					return One(VfxExpression.Property(property.Name, property.Value.Type));
				}
			});

			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = VfxNodeLibrary.InlineNodeType,
				Name = "Value",
				Category = "Value",
				Description = "A constant of any type (a color, a gradient, a curve, a texture...).",
				Kind = VfxNodeKind.Operator,
				Settings = { VfxSettingDefinition.Enum("Type", "Float", AllTypes) },
				InputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition("Value", TypeSetting(node)) },
				OutputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition("Out", TypeSetting(node)) },
				TitleFactory = node => node.GetSetting("Type", "Float"),
				Operator = b => One(b.Input(0))
			});

			const string color = "Color";
			Fixed("Operator.HsvToRgb", "HSV To RGB", color, "A color from hue, saturation and value (0..1).", new[] { V3("HSV", 0f, 1f, 1f) }, new[] { Of("Color", VfxValueType.Color) },
				b => One(VfxExpression.Raw(VfxValueType.Color, "vec4(VfxHsvToRgb({0}), 1.0)", b.Input(0))));
			Fixed("Operator.RgbToHsv", "RGB To HSV", color, "Hue, saturation and value of a color.", new[] { Of("Color", VfxValueType.Color) }, new[] { V3("HSV") },
				b => One(VfxExpression.Raw(VfxValueType.Vector3, "VfxRgbToHsv({0}.rgb)", b.Input(0))));
			Fixed("Operator.Luminance", "Luminance", color, "Perceived brightness of a color.", new[] { Of("Color", VfxValueType.Color) }, new[] { F("Luminance") },
				b => One(VfxExpression.Dot(VfxExpression.Xyz(b.Input(0)), VfxExpression.Vec3(0.2126f, 0.7152f, 0.0722f))));
		}

		private static VfxValueType PropertyType(VfxNodeData node)
		{
			// The editor stores the type next to the name so the slot is right before the graph is compiled.
			VfxValueType type;
			return Enum.TryParse(node.GetSetting("PropertyType", "Float"), true, out type) ? type : VfxValueType.Float;
		}

		#endregion
	}
}
