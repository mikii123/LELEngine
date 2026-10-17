using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     The block node definitions: spawn blocks (rates and bursts), attribute setters, position shapes, velocity
	///     blocks, forces, collisions, kills, over-lifetime curves, flipbooks, GPU event triggers and custom GLSL.
	/// </summary>
	public static class VfxBlockNodes
	{
		#region PublicFields

		public static readonly string[] CompositionOptions = { "Set", "Add", "Multiply", "Blend" };
		public static readonly string[] RandomOptions = { "Off", "Uniform", "PerComponent" };
		public static readonly string[] NumericTypes = { "Float", "Vector2", "Vector3", "Vector4", "Int", "Uint", "Bool" };

		#endregion

		#region PublicMethods

		public static void Register()
		{
			RegisterSpawn();
			RegisterAttributes();
			RegisterPosition();
			RegisterVelocity();
			RegisterForces();
			RegisterCollision();
			RegisterKill();
			RegisterOverLife();
			RegisterFlipbookAndTriggers();
			RegisterCustom();
		}

		// ---------------------------------------------------------------- space helpers

		/// <summary>A point given in the block's space (Local or World setting) converted to the system's space.</summary>
		public static string PointToSystem(VfxBlockBuild b, string point, bool blockWorld)
		{
			if (b.Writer.WorldSpace == blockWorld) return point;
			return blockWorld ? "(vfxWorldToLocal * vec4(" + point + ", 1.0)).xyz" : "(vfxLocalToWorld * vec4(" + point + ", 1.0)).xyz";
		}

		/// <summary>A vector (direction, velocity, force) converted from the block's space to the system's space.</summary>
		public static string VectorToSystem(VfxBlockBuild b, string vector, bool blockWorld)
		{
			if (b.Writer.WorldSpace == blockWorld) return vector;
			return blockWorld ? "(mat3(vfxWorldToLocal) * " + vector + ")" : "(mat3(vfxLocalToWorld) * " + vector + ")";
		}

		public static string PointToWorld(VfxBlockBuild b, string point)
		{
			return b.Writer.WorldSpace ? point : "(vfxLocalToWorld * vec4(" + point + ", 1.0)).xyz";
		}

		public static string VectorToWorld(VfxBlockBuild b, string vector)
		{
			return b.Writer.WorldSpace ? vector : "(mat3(vfxLocalToWorld) * " + vector + ")";
		}

		public static string PointFromWorld(VfxBlockBuild b, string point)
		{
			return b.Writer.WorldSpace ? point : "(vfxWorldToLocal * vec4(" + point + ", 1.0)).xyz";
		}

		public static string VectorFromWorld(VfxBlockBuild b, string vector)
		{
			return b.Writer.WorldSpace ? vector : "(mat3(vfxWorldToLocal) * " + vector + ")";
		}

		public static bool BlockWorld(VfxBlockBuild b)
		{
			return b.Setting("Space", "Local") == "World";
		}

		/// <summary>Applies the Composition setting: Set, Add, Multiply or Blend (with the Blend input).</summary>
		public static void Compose(VfxBlockBuild b, VfxAttribute attribute, string value)
		{
			switch (b.Setting("Composition", "Set"))
			{
				case "Add":
					b.Write(attribute, b.Read(attribute) + " + (" + value + ")");
					break;
				case "Multiply":
					b.Write(attribute, b.Read(attribute) + " * (" + value + ")");
					break;
				case "Blend":
					b.Write(attribute, "mix(" + b.Read(attribute) + ", " + value + ", " + (b.HasInput("Blend") ? b.Emit("Blend") : "0.5") + ")");
					break;
				default:
					b.Write(attribute, value);
					break;
			}
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- helpers

		private static VfxSettingDefinition SpaceSetting(string defaultValue = "Local")
		{
			return VfxSettingDefinition.Enum("Space", defaultValue, "Local", "World").WithTooltip("The space the block's positions and directions are given in.");
		}

		private static VfxSettingDefinition CompositionSetting(string defaultValue = "Set")
		{
			return VfxSettingDefinition.Enum("Composition", defaultValue, CompositionOptions).WithTooltip("How the value combines with the attribute's current value.");
		}

		private static VfxSlotDefinition F(string name, float value = 0f, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Float, VfxValue.FromFloat(value), tooltip);
		}

		private static VfxSlotDefinition V3(string name, float x = 0f, float y = 0f, float z = 0f, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Vector3, VfxValue.FromVector3(x, y, z), tooltip);
		}

		private static VfxSlotDefinition I(string name, int value, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Int, VfxValue.FromInt(value), tooltip);
		}

		private static VfxSlotDefinition U(string name, uint value, string tooltip = null)
		{
			return new VfxSlotDefinition(name, VfxValueType.Uint, VfxValue.FromUint(value), tooltip);
		}

		private static VfxNodeDefinition Block(string type, string name, string category, string description, VfxContextMask contexts, Func<VfxNodeData, List<VfxSlotDefinition>> inputs, VfxBlockEmitter emit, params VfxSettingDefinition[] settings)
		{
			var definition = new VfxNodeDefinition
			{
				Type = type,
				Name = name,
				Category = category,
				Description = description,
				Kind = VfxNodeKind.Block,
				AllowedContexts = contexts,
				InputsFactory = inputs,
				OutputsFactory = node => new List<VfxSlotDefinition>(),
				Block = emit
			};
			definition.Settings.AddRange(settings);
			VfxNodeLibrary.Register(definition);
			return definition;
		}

		private static List<VfxSlotDefinition> Slots(params VfxSlotDefinition[] slots)
		{
			return new List<VfxSlotDefinition>(slots);
		}

		/// <summary>The random value of the block's Random setting: the inline Min / Max inputs mixed by a per-particle random.</summary>
		private static string RandomValue(VfxBlockBuild b, VfxValueType type, string minSlot, string maxSlot, uint extraSalt = 0)
		{
			string mode = b.Setting("Random", "Off");
			string min = b.Emit(b.Input(minSlot), type);
			string max = b.Emit(b.Input(maxSlot), type);
			int components = VfxValueTypes.Components(type);
			bool perComponent = mode == "PerComponent" && components > 1;
			VfxExpression random = b.Random(perComponent ? type : VfxValueType.Float, extraSalt);
			string r = b.Emit(random);
			if (type == VfxValueType.Int || type == VfxValueType.Uint)
			{
				string glsl = VfxValueTypes.Glsl(type);
				return glsl + "(floor(mix(float(" + min + "), float(" + max + ") + 1.0, " + r + ")))";
			}

			return "mix(" + min + ", " + max + ", " + r + ")";
		}

		// ---------------------------------------------------------------- spawn

		private static void RegisterSpawn()
		{
			const string category = "Spawn";
			Block("Block.ConstantSpawnRate", "Constant Spawn Rate", category, "Spawns a steady number of particles per second.", VfxContextMask.Spawn,
				node => Slots(F("Rate", 16f, "Particles per second.")),
				b => b.Spawner?.Blocks.Add(new VfxSpawnBlockDescription { NodeId = b.Node.Id, Kind = VfxSpawnBlockKind.ConstantRate, Rate = b.Input("Rate") }));
			Block("Block.SingleBurst", "Single Burst", category, "Spawns a number of particles once per loop, after a delay.", VfxContextMask.Spawn,
				node => Slots(F("Count", 32f, "Particles in the burst."), F("Delay", 0f, "Seconds into the loop.")),
				b => b.Spawner?.Blocks.Add(new VfxSpawnBlockDescription { NodeId = b.Node.Id, Kind = VfxSpawnBlockKind.SingleBurst, Count = b.Input("Count"), Delay = b.Input("Delay") }));
			Block("Block.PeriodicBurst", "Periodic Burst", category, "Spawns bursts at a fixed interval.", VfxContextMask.Spawn,
				node => Slots(F("Count", 16f, "Particles per burst."), F("Period", 1f, "Seconds between bursts."), F("Delay", 0f, "Seconds before the first burst.")),
				b => b.Spawner?.Blocks.Add(new VfxSpawnBlockDescription { NodeId = b.Node.Id, Kind = VfxSpawnBlockKind.PeriodicBurst, Count = b.Input("Count"), Period = b.Input("Period"), Delay = b.Input("Delay") }));
			Block("Block.VariableSpawnRate", "Variable Spawn Rate", category, "A spawn rate that changes over the loop: connect a curve sampled by the Spawner Time operator.", VfxContextMask.Spawn,
				node => Slots(F("Rate", 16f, "Particles per second right now.")),
				b => b.Spawner?.Blocks.Add(new VfxSpawnBlockDescription { NodeId = b.Node.Id, Kind = VfxSpawnBlockKind.VariableRate, Rate = b.Input("Rate") }));
			Block("Block.SpawnOverDistance", "Spawn Over Distance", category, "Spawns particles as the component moves (per world unit travelled).", VfxContextMask.Spawn,
				node => Slots(F("Rate", 10f, "Particles per unit of distance.")),
				b => b.Spawner?.Blocks.Add(new VfxSpawnBlockDescription { NodeId = b.Node.Id, Kind = VfxSpawnBlockKind.SpawnOverDistance, Rate = b.Input("Rate") }));
		}

		// ---------------------------------------------------------------- attributes

		private static List<VfxSlotDefinition> SetAttributeInputs(VfxNodeData node, VfxAttribute fixedAttribute)
		{
			VfxAttribute attribute = fixedAttribute ?? VfxAttributes.Find(node.GetSetting("Attribute", "position")) ?? VfxAttributes.Position;
			var slots = new List<VfxSlotDefinition>();
			string random = node.GetSetting("Random", "Off");
			if (random == "Off")
			{
				slots.Add(new VfxSlotDefinition("Value", attribute.Type, attribute.Default.Clone()));
			}
			else
			{
				slots.Add(new VfxSlotDefinition("Min", attribute.Type, attribute.Default.Clone()));
				slots.Add(new VfxSlotDefinition("Max", attribute.Type, attribute.Default.Clone()));
			}

			if (node.GetSetting("Composition", "Set") == "Blend")
			{
				slots.Add(F("Blend", 0.5f, "0 keeps the current value, 1 takes the new one."));
			}

			return slots;
		}

		private static void EmitSetAttribute(VfxBlockBuild b, VfxAttribute fixedAttribute)
		{
			VfxAttribute attribute = fixedAttribute ?? VfxAttributes.Find(b.Setting("Attribute", "position")) ?? VfxAttributes.Position;
			string value = b.Setting("Random", "Off") == "Off" ? b.Emit(b.Input("Value"), attribute.Type) : RandomValue(b, attribute.Type, "Min", "Max");
			Compose(b, attribute, value);
		}

		private static string SetAttributeTitle(VfxNodeData node, string attributeName)
		{
			string verb = node.GetSetting("Composition", "Set") switch { "Add" => "Add ", "Multiply" => "Multiply ", "Blend" => "Blend ", _ => "Set " };
			string random = node.GetSetting("Random", "Off") == "Off" ? "" : " (Random)";
			return verb + VfxNicify(attributeName) + random;
		}

		private static string VfxNicify(string attributeName)
		{
			return Serialization.SerializationUtility.NicifyName(attributeName);
		}

		private static void RegisterAttributes()
		{
			const string category = "Attribute";
			VfxNodeDefinition generic = Block("Block.SetAttribute", "Set Attribute", category, "Sets, adds to, multiplies or blends any standard attribute (optionally a random value between Min and Max).", VfxContextMask.Particle,
				node => SetAttributeInputs(node, null), b => EmitSetAttribute(b, null),
				new VfxSettingDefinition("Attribute", VfxSettingKind.Attribute, "position", VfxAttributes.Settable, null, true),
				CompositionSetting(), VfxSettingDefinition.Enum("Random", "Off", RandomOptions));
			generic.TitleFactory = node => SetAttributeTitle(node, node.GetSetting("Attribute", "position"));

			foreach (string attributeName in new[] { "position", "velocity", "lifetime", "size", "scale", "color", "alpha", "angle", "angularVelocity", "mass", "texIndex", "direction", "targetPosition", "pivot" })
			{
				VfxAttribute attribute = VfxAttributes.Find(attributeName);
				VfxAttribute captured = attribute;
				VfxNodeDefinition preset = Block("Block.Set." + attributeName, "Set " + VfxNicify(attributeName), category + "/Set", "Sets the " + attributeName + " attribute: " + attribute.Description, VfxContextMask.Particle,
					node => SetAttributeInputs(node, captured), b => EmitSetAttribute(b, captured),
					CompositionSetting(), VfxSettingDefinition.Enum("Random", "Off", RandomOptions));
				preset.TitleFactory = node => SetAttributeTitle(node, captured.Name);
				preset.Keywords = attributeName;
			}

			VfxNodeDefinition custom = Block("Block.SetCustomAttribute", "Set Custom Attribute", category, "Sets a custom per-particle attribute (declared by its name and type; other blocks read it with Get Attribute's custom name).", VfxContextMask.Particle,
				node =>
				{
					VfxValueType type = ParseType(node.GetSetting("Type", "Float"));
					var slots = new List<VfxSlotDefinition> { new VfxSlotDefinition("Value", type) };
					if (node.GetSetting("Composition", "Set") == "Blend")
					{
						slots.Add(F("Blend", 0.5f));
					}

					return slots;
				},
				b =>
				{
					VfxAttribute attribute = VfxAttributes.Custom(b.Setting("Name", "custom"), ParseType(b.Setting("Type", "Float")));
					Compose(b, attribute, b.Emit(b.Input("Value"), attribute.Type));
				},
				VfxSettingDefinition.Text("Name", "custom", "Name of the attribute (letters, digits and underscores).", true),
				VfxSettingDefinition.Enum("Type", "Float", NumericTypes), CompositionSetting());
			custom.TitleFactory = node => "Set " + node.GetSetting("Name", "custom");

			VfxNodeDefinition getCustom = new VfxNodeDefinition
			{
				Type = "Operator.GetCustomAttribute",
				Name = "Get Custom Attribute",
				Category = "Attribute",
				Description = "Reads a custom attribute set by a Set Custom Attribute block.",
				Kind = VfxNodeKind.Operator,
				Settings = { VfxSettingDefinition.Text("Name", "custom", null, true), VfxSettingDefinition.Enum("Type", "Float", NumericTypes) },
				InputsFactory = node => new List<VfxSlotDefinition>(),
				OutputsFactory = node => new List<VfxSlotDefinition> { new VfxSlotDefinition(node.GetSetting("Name", "custom"), ParseType(node.GetSetting("Type", "Float"))) },
				TitleFactory = node => "Get " + node.GetSetting("Name", "custom"),
				Operator = b => new[] { VfxExpression.Attribute(VfxAttributes.Custom(b.Setting("Name", "custom"), ParseType(b.Setting("Type", "Float")))) }
			};
			VfxNodeLibrary.Register(getCustom);

			Block("Block.InheritSourceAttribute", "Inherit Source Attribute", category, "In a system spawned by a GPU event: copies an attribute of the parent particle that raised the event.", VfxContextMask.Initialize,
				node =>
				{
					var slots = new List<VfxSlotDefinition>();
					if (node.GetSetting("Composition", "Set") == "Blend")
					{
						slots.Add(F("Blend", 0.5f));
					}

					return slots;
				},
				b =>
				{
					VfxAttribute attribute = VfxAttributes.Find(b.Setting("Attribute", "position")) ?? VfxAttributes.Position;
					b.Writer.SourceAttributesRead.Add(attribute.Name);
					Compose(b, attribute, "src_" + attribute.Name);
				},
				new VfxSettingDefinition("Attribute", VfxSettingKind.Attribute, "position", new[] { "position", "velocity", "color", "alpha", "size", "lifetime", "age" }, null, false),
				CompositionSetting()).TitleFactory = node => "Inherit Source " + VfxNicify(node.GetSetting("Attribute", "position"));
		}

		private static VfxValueType ParseType(string text)
		{
			VfxValueType type;
			return Enum.TryParse(text, true, out type) ? type : VfxValueType.Float;
		}

		// ---------------------------------------------------------------- position

		private static void RegisterPosition()
		{
			const string category = "Position";
			VfxNodeDefinition shape = Block("Block.PositionShape", "Set Position (Shape)", category, "Places the particle in or on a shape; optionally sets the direction attribute (the shape's normal) for velocity blocks.", VfxContextMask.InitializeUpdate,
				node =>
				{
					var slots = new List<VfxSlotDefinition>();
					string kind = node.GetSetting("Shape", "Sphere");
					switch (kind)
					{
						case "Box":
							slots.Add(V3("Center"));
							slots.Add(V3("Size", 1f, 1f, 1f));
							break;
						case "Cone":
							slots.Add(V3("Center", 0f, 0f, 0f, "Base centre; the cone points along +Y."));
							slots.Add(F("Base Radius", 0.5f));
							slots.Add(F("Top Radius", 0.1f));
							slots.Add(F("Height", 1f));
							slots.Add(F("Arc", 360f, "Degrees of the circle used."));
							break;
						case "Circle":
							slots.Add(V3("Center"));
							slots.Add(F("Radius", 1f));
							slots.Add(F("Arc", 360f, "Degrees of the circle used (in the XZ plane)."));
							break;
						case "Line":
							slots.Add(V3("Start"));
							slots.Add(V3("End", 0f, 1f, 0f));
							break;
						case "Torus":
							slots.Add(V3("Center"));
							slots.Add(F("Major Radius", 1f));
							slots.Add(F("Minor Radius", 0.2f));
							slots.Add(F("Arc", 360f));
							break;
						default:
							slots.Add(V3("Center"));
							slots.Add(F("Radius", 1f));
							slots.Add(F("Arc", 360f, "Degrees around Y used."));
							break;
					}

					if (node.GetSetting("Mode", "Volume") == "Thickness")
					{
						slots.Add(F("Thickness", 0.2f, "Depth below the surface particles may spawn in (0..1 of the radius)."));
					}

					return slots;
				},
				EmitPositionShape,
				VfxSettingDefinition.Enum("Shape", "Sphere", "Sphere", "Box", "Cone", "Circle", "Line", "Torus"),
				VfxSettingDefinition.Enum("Mode", "Volume", "Volume", "Surface", "Thickness"),
				SpaceSetting(), CompositionSetting(), VfxSettingDefinition.Bool("SetDirection", true, "Also write the direction attribute (the outward normal)."));
			shape.TitleFactory = node => "Position (" + node.GetSetting("Shape", "Sphere") + ")";

			Block("Block.PositionMesh", "Set Position (Mesh)", category, "Places the particle on a random vertex of a mesh (the direction attribute takes the vertex normal).", VfxContextMask.InitializeUpdate,
				node => Slots(new VfxSlotDefinition("Mesh", VfxValueType.Mesh), F("Scale", 1f)),
				b =>
				{
					VfxExpression index = b.Setting("Selection", "Random") == "Random"
						? b.Random(VfxValueType.Uint, 0, VfxExpression.Uint(0), VfxExpression.MeshVertex(b.Input("Mesh"), VfxExpression.Uint(0), "count"))
						: VfxExpression.Attribute(VfxAttributes.ParticleId);
					string position = b.Emit(VfxExpression.Mul(VfxExpression.MeshVertex(b.Input("Mesh"), index, "position"), b.Input("Scale")));
					string normal = b.Emit(VfxExpression.MeshVertex(b.Input("Mesh"), index, "normal"));
					bool world = BlockWorld(b);
					Compose(b, VfxAttributes.Position, PointToSystem(b, position, world));
					if (b.Flag("SetDirection"))
					{
						b.Write(VfxAttributes.Direction, "VfxSafeNormalize(" + VectorToSystem(b, normal, world) + ")");
					}
				},
				VfxSettingDefinition.Enum("Selection", "Random", "Random", "ParticleId"), SpaceSetting(), CompositionSetting(), VfxSettingDefinition.Bool("SetDirection", true));
		}

		private static void EmitPositionShape(VfxBlockBuild b)
		{
			string kind = b.Setting("Shape", "Sphere");
			string mode = b.Setting("Mode", "Volume");
			bool world = BlockWorld(b);
			string seed = b.Emit(VfxExpression.Attribute(VfxAttributes.Seed));
			string salt = b.Salt.ToString(CultureInfo.InvariantCulture) + "u";
			string radial = mode == "Surface" ? "1.0" : mode == "Thickness" ? "(1.0 - " + b.Emit("Thickness") + " * VfxRandom01(" + seed + ", " + salt + " ^ 0x51u))" : "pow(VfxRandom01(" + seed + ", " + salt + " ^ 0x51u), 1.0 / 3.0)";
			string point;
			string direction;
			switch (kind)
			{
				case "Box":
				{
					string center = b.Emit("Center");
					string size = b.Emit("Size");
					b.Line("vec3 boxUnit = VfxRandom3(" + seed + ", " + salt + ") * 2.0 - 1.0;");
					if (mode != "Volume")
					{
						// On the surface: push the largest component to the face.
						b.Line("vec3 boxAbs = abs(boxUnit);");
						b.Line("if (boxAbs.x >= boxAbs.y && boxAbs.x >= boxAbs.z) boxUnit.x = sign(boxUnit.x); else if (boxAbs.y >= boxAbs.z) boxUnit.y = sign(boxUnit.y); else boxUnit.z = sign(boxUnit.z);");
						if (mode == "Thickness")
						{
							b.Line("boxUnit *= 1.0 - " + b.Emit("Thickness") + " * VfxRandom01(" + seed + ", " + salt + " ^ 0x51u);");
						}
					}

					point = center + " + boxUnit * " + size + " * 0.5";
					direction = "VfxSafeNormalize(boxUnit)";
					break;
				}
				case "Cone":
				{
					string center = b.Emit("Center");
					string baseRadius = b.Emit("Base Radius");
					string topRadius = b.Emit("Top Radius");
					string height = b.Emit("Height");
					string arc = b.Emit("Arc");
					b.Line("float coneH = VfxRandom01(" + seed + ", " + salt + " ^ 0x33u);");
					b.Line("float coneA = radians(" + arc + ") * VfxRandom01(" + seed + ", " + salt + " ^ 0x44u);");
					b.Line("float coneR = mix(" + baseRadius + ", " + topRadius + ", coneH) * " + (mode == "Volume" ? "sqrt(VfxRandom01(" + seed + ", " + salt + " ^ 0x51u))" : radial) + ";");
					b.Line("vec3 coneLocal = vec3(cos(coneA) * coneR, coneH * " + height + ", sin(coneA) * coneR);");
					point = center + " + coneLocal";
					direction = "VfxSafeNormalize(vec3(cos(coneA) * " + height + ", " + baseRadius + " - " + topRadius + ", sin(coneA) * " + height + "))";
					break;
				}
				case "Circle":
				{
					string center = b.Emit("Center");
					string radius = b.Emit("Radius");
					string arc = b.Emit("Arc");
					b.Line("float circleA = radians(" + arc + ") * VfxRandom01(" + seed + ", " + salt + " ^ 0x44u);");
					b.Line("float circleR = " + radius + " * " + (mode == "Volume" ? "sqrt(VfxRandom01(" + seed + ", " + salt + " ^ 0x51u))" : radial) + ";");
					b.Line("vec3 circleDir = vec3(cos(circleA), 0.0, sin(circleA));");
					point = center + " + circleDir * circleR";
					direction = "circleDir";
					break;
				}
				case "Line":
				{
					string start = b.Emit("Start");
					string end = b.Emit("End");
					b.Line("float lineT = VfxRandom01(" + seed + ", " + salt + ");");
					point = "mix(" + start + ", " + end + ", lineT)";
					direction = "VfxSafeNormalize(" + end + " - " + start + ")";
					break;
				}
				case "Torus":
				{
					string center = b.Emit("Center");
					string major = b.Emit("Major Radius");
					string minor = b.Emit("Minor Radius");
					string arc = b.Emit("Arc");
					b.Line("float torusA = radians(" + arc + ") * VfxRandom01(" + seed + ", " + salt + " ^ 0x44u);");
					b.Line("float torusB = 6.28318530718 * VfxRandom01(" + seed + ", " + salt + " ^ 0x55u);");
					b.Line("float torusR = " + minor + " * " + (mode == "Volume" ? "sqrt(VfxRandom01(" + seed + ", " + salt + " ^ 0x51u))" : radial) + ";");
					b.Line("vec3 torusRing = vec3(cos(torusA), 0.0, sin(torusA));");
					b.Line("vec3 torusNormal = torusRing * cos(torusB) + vec3(0.0, sin(torusB), 0.0);");
					point = center + " + torusRing * " + major + " + torusNormal * torusR";
					direction = "torusNormal";
					break;
				}
				default:
				{
					string center = b.Emit("Center");
					string radius = b.Emit("Radius");
					string arc = b.Emit("Arc");
					b.Line("vec3 sphereDir = VfxRandomOnSphere(" + seed + ", " + salt + ");");
					b.Line("float sphereArc = radians(" + arc + ");");
					b.Line("if (sphereArc < 6.283) { float sa = atan(sphereDir.z, sphereDir.x); sa = mod(sa, 6.28318530718) * (sphereArc / 6.28318530718); float sr = length(sphereDir.xz); sphereDir = vec3(cos(sa) * sr, sphereDir.y, sin(sa) * sr); }");
					point = center + " + sphereDir * (" + radius + " * " + radial + ")";
					direction = "sphereDir";
					break;
				}
			}

			Compose(b, VfxAttributes.Position, PointToSystem(b, "(" + point + ")", world));
			if (b.Flag("SetDirection"))
			{
				b.Write(VfxAttributes.Direction, "VfxSafeNormalize(" + VectorToSystem(b, "(" + direction + ")", world) + ")");
			}
		}

		// ---------------------------------------------------------------- velocity

		private static void RegisterVelocity()
		{
			const string category = "Velocity";
			Block("Block.VelocityRandomDirection", "Velocity (Random Direction)", category, "A random direction at a random speed, optionally blended towards the direction attribute (set by the position shapes).", VfxContextMask.InitializeUpdate,
				node => Slots(F("Min Speed", 1f), F("Max Speed", 2f), F("Direction Blend", 0f, "0: random direction, 1: the direction attribute.")),
				b =>
				{
					string random = b.Emit(VfxExpression.Raw(VfxValueType.Vector3, "VfxRandomOnSphere(attr_seed, " + b.Salt + "u)"));
					string speed = b.Emit(VfxExpression.Random(VfxRandomMode.PerParticle, b.Salt + 17u, VfxValueType.Float, b.Input("Min Speed"), b.Input("Max Speed")));
					string direction = "VfxSafeNormalize(mix(" + random + ", " + b.Read(VfxAttributes.Direction) + ", " + b.Emit("Direction Blend") + "))";
					Compose(b, VfxAttributes.Velocity, direction + " * " + speed);
				}, CompositionSetting());
			Block("Block.VelocityDirection", "Velocity (New Direction)", category, "A given direction at a random speed, blended with the direction attribute.", VfxContextMask.InitializeUpdate,
				node => Slots(V3("Direction", 0f, 1f, 0f), F("Min Speed", 1f), F("Max Speed", 2f), F("Direction Blend", 0f, "0: the given direction, 1: the direction attribute.")),
				b =>
				{
					string given = VectorToSystem(b, "VfxSafeNormalize(" + b.Emit("Direction") + ")", BlockWorld(b));
					string speed = b.Emit(VfxExpression.Random(VfxRandomMode.PerParticle, b.Salt + 17u, VfxValueType.Float, b.Input("Min Speed"), b.Input("Max Speed")));
					string direction = "VfxSafeNormalize(mix(" + given + ", " + b.Read(VfxAttributes.Direction) + ", " + b.Emit("Direction Blend") + "))";
					Compose(b, VfxAttributes.Velocity, direction + " * " + speed);
				}, SpaceSetting(), CompositionSetting());
			Block("Block.VelocitySpherical", "Velocity (Spherical)", category, "Away from a centre at a random speed.", VfxContextMask.InitializeUpdate,
				node => Slots(V3("Center"), F("Min Speed", 1f), F("Max Speed", 2f)),
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					string speed = b.Emit(VfxExpression.Random(VfxRandomMode.PerParticle, b.Salt + 17u, VfxValueType.Float, b.Input("Min Speed"), b.Input("Max Speed")));
					Compose(b, VfxAttributes.Velocity, "VfxSafeNormalize(" + b.Read(VfxAttributes.Position) + " - " + center + ") * " + speed);
				}, SpaceSetting(), CompositionSetting());
			Block("Block.VelocityTangent", "Velocity (Tangent)", category, "Around an axis through a centre (orbits, vortices) at a random speed.", VfxContextMask.InitializeUpdate,
				node => Slots(V3("Center"), V3("Axis", 0f, 1f, 0f), F("Min Speed", 1f), F("Max Speed", 2f)),
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					string axis = VectorToSystem(b, b.Emit("Axis"), BlockWorld(b));
					string speed = b.Emit(VfxExpression.Random(VfxRandomMode.PerParticle, b.Salt + 17u, VfxValueType.Float, b.Input("Min Speed"), b.Input("Max Speed")));
					Compose(b, VfxAttributes.Velocity, "VfxSafeNormalize(cross(VfxSafeNormalize(" + axis + "), " + b.Read(VfxAttributes.Position) + " - " + center + ")) * " + speed);
				}, SpaceSetting(), CompositionSetting());
			Block("Block.VelocityCone", "Velocity (Cone)", category, "A random direction inside a cone around an axis, at a random speed.", VfxContextMask.InitializeUpdate,
				node => Slots(V3("Axis", 0f, 1f, 0f), F("Angle", 30f, "Half angle of the cone in degrees."), F("Min Speed", 1f), F("Max Speed", 2f)),
				b =>
				{
					string axis = VectorToSystem(b, "VfxSafeNormalize(" + b.Emit("Axis") + ")", BlockWorld(b));
					string speed = b.Emit(VfxExpression.Random(VfxRandomMode.PerParticle, b.Salt + 17u, VfxValueType.Float, b.Input("Min Speed"), b.Input("Max Speed")));
					b.Line("vec3 coneAxis = " + axis + ";");
					b.Line("vec3 coneLocalDir = VfxRandomInCone(attr_seed, " + b.Salt + "u, radians(" + b.Emit("Angle") + "));");
					b.Line("vec3 coneT = VfxSafeNormalize(cross(abs(coneAxis.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), coneAxis));");
					b.Line("vec3 coneB = cross(coneAxis, coneT);");
					b.Writer.ReadAttribute(VfxAttributes.Seed);
					Compose(b, VfxAttributes.Velocity, "(coneT * coneLocalDir.x + coneB * coneLocalDir.y + coneAxis * coneLocalDir.z) * " + speed);
				}, SpaceSetting(), CompositionSetting());
		}

		// ---------------------------------------------------------------- forces

		private static void RegisterForces()
		{
			const string category = "Force";
			Block("Block.Gravity", "Gravity", category, "Accelerates particles by a constant force (default: Earth's gravity, in world space).", VfxContextMask.Update,
				node => Slots(V3("Force", 0f, -9.81f, 0f)),
				b => b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + " + VectorToSystem(b, b.Emit("Force"), BlockWorld(b)) + " * vfxDeltaTime"),
				SpaceSetting("World"));
			Block("Block.Force", "Force", category, "A force divided by the mass (Absolute), or a force towards a target velocity (Relative: force * (target - velocity)).", VfxContextMask.Update,
				node => Slots(V3("Force", 0f, 1f, 0f), node.GetSetting("Mode", "Absolute") == "Relative" ? F("Drag", 1f, "How fast the velocity follows the target.") : F("Multiplier", 1f)),
				b =>
				{
					string force = VectorToSystem(b, b.Emit("Force"), BlockWorld(b));
					if (b.Setting("Mode", "Absolute") == "Relative")
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + (" + force + " - " + b.Read(VfxAttributes.Velocity) + ") * min(1.0, " + b.Emit("Drag") + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4))");
					}
					else
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + " + force + " * " + b.Emit("Multiplier") + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4)");
					}
				}, VfxSettingDefinition.Enum("Mode", "Absolute", "Absolute", "Relative"), SpaceSetting("World"));
			Block("Block.Drag", "Drag", category, "Slows particles down in proportion to their speed (and size when asked).", VfxContextMask.Update,
				node => Slots(F("Drag Coefficient", 0.5f)),
				b =>
				{
					string size = b.Flag("UseParticleSize") ? " * " + b.Read(VfxAttributes.Size) + " * " + b.Read(VfxAttributes.Size) : "";
					b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " * max(0.0, 1.0 - " + b.Emit("Drag Coefficient") + size + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4))");
				}, VfxSettingDefinition.Bool("UseParticleSize", false, "Bigger particles are slowed more (drag scales with size squared)."));
			Block("Block.Turbulence", "Turbulence", category, "Pushes particles with a curl noise field (swirls that never pile up).", VfxContextMask.Update,
				node => Slots(F("Intensity", 2f), F("Frequency", 0.5f), I("Octaves", 1), F("Drag", 1f, "Relative mode: how fast the velocity follows the field."), V3("Offset", 0f, 0f, 0f, "Moves the field (animate it with Time).")),
				b =>
				{
					b.Writer.UsesNoise = true;
					string position = BlockWorld(b) ? PointToWorld(b, b.Read(VfxAttributes.Position)) : b.Read(VfxAttributes.Position);
					string field = "VfxCurlNoise(" + position + " + " + b.Emit("Offset") + ", " + b.Emit("Frequency") + ", " + b.Emit("Octaves") + ") * " + b.Emit("Intensity");
					string inSystem = BlockWorld(b) ? VectorFromWorld(b, "(" + field + ")") : "(" + field + ")";
					if (b.Setting("Mode", "Relative") == "Relative")
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + (" + inSystem + " - " + b.Read(VfxAttributes.Velocity) + ") * min(1.0, " + b.Emit("Drag") + " * vfxDeltaTime)");
					}
					else
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + " + inSystem + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4)");
					}
				}, VfxSettingDefinition.Enum("Mode", "Relative", "Absolute", "Relative"), SpaceSetting("World"));
			Block("Block.Attractor", "Attractor", category, "Pulls particles towards a point (negative strength pushes them away).", VfxContextMask.Update,
				node => Slots(V3("Position"), F("Strength", 10f), F("Min Distance", 0.1f, "The pull stops growing closer than this.")),
				b =>
				{
					string target = PointToSystem(b, b.Emit("Position"), BlockWorld(b));
					b.Line("vec3 attractDelta = " + target + " - " + b.Read(VfxAttributes.Position) + ";");
					b.Line("float attractDistance = max(length(attractDelta), " + b.Emit("Min Distance") + ");");
					b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + attractDelta / attractDistance * " + b.Emit("Strength") + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4)");
				}, SpaceSetting());
			Block("Block.ConformToSphere", "Conform To Sphere", category, "Attracts particles to the surface of a sphere and makes them stick to it.", VfxContextMask.Update,
				node => Slots(V3("Center"), F("Radius", 1f), F("Attraction Speed", 5f), F("Attraction Force", 20f), F("Stick Distance", 0.1f), F("Stick Force", 50f)),
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					b.Line("vec3 conformDelta = " + b.Read(VfxAttributes.Position) + " - " + center + ";");
					b.Line("float conformDistance = length(conformDelta);");
					b.Line("vec3 conformNormal = conformDistance > 1e-5 ? conformDelta / conformDistance : vec3(0.0, 1.0, 0.0);");
					b.Line("float conformSigned = conformDistance - " + b.Emit("Radius") + ";");
					EmitConform(b);
				}, SpaceSetting());
			Block("Block.ConformToSdf", "Conform To Global SDF", category, "Attracts particles to the nearest scene surface (the GI's global distance field) and makes them stick to it.", VfxContextMask.Update,
				node => Slots(F("Attraction Speed", 5f), F("Attraction Force", 20f), F("Stick Distance", 0.1f), F("Stick Force", 50f)),
				b =>
				{
					b.Writer.UsesSdf = true;
					string world = PointToWorld(b, b.Read(VfxAttributes.Position));
					b.Line("vec3 conformWorld = " + world + ";");
					b.Line("float conformSigned = VfxSampleGlobalSdf(conformWorld);");
					b.Line("vec3 conformNormal = " + VectorFromWorld(b, "VfxGlobalSdfNormal(conformWorld)") + ";");
					b.Writer.BeginBlock("if (VfxGlobalSdfAvailable(conformWorld))");
					EmitConform(b);
					b.Writer.EndBlock();
				});
			Block("Block.VectorFieldForce", "Vector Field Force", category, "Pushes particles with the vectors of a 3D texture placed in a box (rgb * 2 - 1 are the directions).", VfxContextMask.Update,
				node => Slots(new VfxSlotDefinition("Vector Field", VfxValueType.Texture3D), V3("Center"), V3("Size", 10f, 10f, 10f), F("Intensity", 1f), F("Drag", 1f)),
				b =>
				{
					string position = BlockWorld(b) ? PointToWorld(b, b.Read(VfxAttributes.Position)) : b.Read(VfxAttributes.Position);
					VfxExpression uvw = VfxExpression.Raw(VfxValueType.Vector3, "(({0}) - {1}) / max({2}, vec3(1e-4)) + 0.5", VfxExpression.Raw(VfxValueType.Vector3, position), b.Input("Center"), b.Input("Size"));
					VfxExpression sample = VfxExpression.SampleTexture3D(b.Input("Vector Field"), uvw);
					string field = "((" + b.Emit(sample) + ").xyz * 2.0 - 1.0) * " + b.Emit("Intensity");
					string inSystem = BlockWorld(b) ? VectorFromWorld(b, "(" + field + ")") : "(" + field + ")";
					if (b.Setting("Mode", "Relative") == "Relative")
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + (" + inSystem + " - " + b.Read(VfxAttributes.Velocity) + ") * min(1.0, " + b.Emit("Drag") + " * vfxDeltaTime)");
					}
					else
					{
						b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + " + inSystem + " * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4)");
					}
				}, VfxSettingDefinition.Enum("Mode", "Relative", "Absolute", "Relative"), SpaceSetting());
		}

		/// <summary>Conform response given conformSigned (distance to the surface, positive outside) and conformNormal (outward).</summary>
		private static void EmitConform(VfxBlockBuild b)
		{
			b.Line("float conformAbs = abs(conformSigned);");
			b.Line("vec3 conformTarget = -conformNormal * sign(conformSigned) * " + b.Emit("Attraction Speed") + ";");
			b.Line("float conformForce = conformAbs < " + b.Emit("Stick Distance") + " ? " + b.Emit("Stick Force") + " : " + b.Emit("Attraction Force") + ";");
			b.Line("vec3 conformDelta = conformTarget - " + b.Read(VfxAttributes.Velocity) + ";");
			b.Line("float conformMax = length(conformDelta);");
			b.Line("vec3 conformStep = conformMax > 1e-5 ? conformDelta / conformMax * min(conformMax, conformForce * vfxDeltaTime / max(" + b.Read(VfxAttributes.Mass) + ", 1e-4)) : vec3(0.0);");
			b.Write(VfxAttributes.Velocity, b.Read(VfxAttributes.Velocity) + " + conformStep");
		}

		// ---------------------------------------------------------------- collision

		private static VfxSlotDefinition[] CollisionInputs()
		{
			return new[]
			{
				F("Bounce", 0.1f, "Fraction of the normal speed kept after the hit."),
				F("Friction", 0f, "Fraction of the tangential speed lost."),
				F("Lifetime Loss", 0f, "Fraction of the lifetime lost per hit."),
				F("Roughness", 0f, "Random tilt of the surface normal (0..1)."),
				F("Radius", 0f, "Collision radius of the particle (0 = a point).")
			};
		}

		private static string Collide(VfxBlockBuild b, string distance, string normal)
		{
			b.Writer.ReadAttribute(VfxAttributes.Seed);
			return "VfxCollisionResponse(" + b.Read(VfxAttributes.Position) + ", " + b.Read(VfxAttributes.Velocity) + ", " + b.Read(VfxAttributes.Lifetime) + ", " + distance + ", " + normal + ", "
				+ b.Emit("Radius") + ", " + b.Emit("Bounce") + ", " + b.Emit("Friction") + ", " + b.Emit("Lifetime Loss") + ", " + b.Emit("Roughness") + ", attr_seed, " + b.Salt + "u);";
		}

		private static void MarkCollisionWrites(VfxBlockBuild b)
		{
			b.Writer.AttributesWritten.Add(VfxAttributes.Position.Name);
			b.Writer.AttributesWritten.Add(VfxAttributes.Velocity.Name);
			b.Writer.AttributesWritten.Add(VfxAttributes.Lifetime.Name);
		}

		private static void RegisterCollision()
		{
			const string category = "Collision";
			Block("Block.CollidePlane", "Collide With Plane", category, "Particles bounce off an infinite plane (Inverted: they stay below it).", VfxContextMask.Update,
				node =>
				{
					var slots = Slots(V3("Position"), V3("Normal", 0f, 1f, 0f));
					slots.AddRange(CollisionInputs());
					return slots;
				},
				b =>
				{
					string position = PointToSystem(b, b.Emit("Position"), BlockWorld(b));
					string normal = VectorToSystem(b, "VfxSafeNormalize(" + b.Emit("Normal") + ")", BlockWorld(b));
					string flip = b.Setting("Mode", "Solid") == "Inverted" ? "-" : "";
					b.Line("vec3 planeNormal = " + flip + normal + ";");
					b.Line("float planeDistance = dot(" + b.Read(VfxAttributes.Position) + " - " + position + ", planeNormal);");
					b.Line(Collide(b, "planeDistance", "planeNormal"));
					MarkCollisionWrites(b);
				}, VfxSettingDefinition.Enum("Mode", "Solid", "Solid", "Inverted"), SpaceSetting());
			Block("Block.CollideSphere", "Collide With Sphere", category, "Particles bounce off a sphere (Inverted: they stay inside it).", VfxContextMask.Update,
				node =>
				{
					var slots = Slots(V3("Center"), F("Sphere Radius", 1f));
					slots.AddRange(CollisionInputs());
					return slots;
				},
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					bool inverted = b.Setting("Mode", "Solid") == "Inverted";
					b.Line("vec3 sphereDelta = " + b.Read(VfxAttributes.Position) + " - " + center + ";");
					b.Line("float sphereLength = length(sphereDelta);");
					b.Line("vec3 sphereNormal = sphereLength > 1e-5 ? sphereDelta / sphereLength : vec3(0.0, 1.0, 0.0);");
					b.Line("float sphereDistance = " + (inverted ? b.Emit("Sphere Radius") + " - sphereLength" : "sphereLength - " + b.Emit("Sphere Radius")) + ";");
					b.Line(Collide(b, "sphereDistance", inverted ? "(-sphereNormal)" : "sphereNormal"));
					MarkCollisionWrites(b);
				}, VfxSettingDefinition.Enum("Mode", "Solid", "Solid", "Inverted"), SpaceSetting());
			Block("Block.CollideBox", "Collide With Box", category, "Particles bounce off an axis-aligned box (Inverted: they stay inside it).", VfxContextMask.Update,
				node =>
				{
					var slots = Slots(V3("Center"), V3("Size", 1f, 1f, 1f));
					slots.AddRange(CollisionInputs());
					return slots;
				},
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					bool inverted = b.Setting("Mode", "Solid") == "Inverted";
					b.Line("vec3 boxNormal;");
					b.Line("float boxDistance = VfxBoxDistance(" + b.Read(VfxAttributes.Position) + ", " + center + ", " + b.Emit("Size") + " * 0.5, boxNormal);");
					b.Line(Collide(b, inverted ? "(-boxDistance)" : "boxDistance", inverted ? "(-boxNormal)" : "boxNormal"));
					MarkCollisionWrites(b);
				}, VfxSettingDefinition.Enum("Mode", "Solid", "Solid", "Inverted"), SpaceSetting());
			Block("Block.CollideDepth", "Collide With Depth Buffer", category, "Particles bounce off the surfaces the camera sees (the previous frame's depth buffer).", VfxContextMask.Update,
				node =>
				{
					var slots = Slots(F("Surface Thickness", 0.5f, "How far behind the seen surface a particle still collides."));
					slots.AddRange(CollisionInputs());
					return slots;
				},
				b =>
				{
					b.Writer.UsesDepth = true;
					b.Line("vec2 depthUv; float depthZ;");
					b.Line("vec3 depthWorld = " + PointToWorld(b, b.Read(VfxAttributes.Position)) + ";");
					b.Writer.BeginBlock("if (VfxDepthAvailable() && VfxProjectToDepth(depthWorld, depthUv, depthZ))");
					b.Line("float sceneZ = VfxSampleSceneDepth(depthUv);");
					b.Line("vec3 sceneWorld = VfxDepthWorldPosition(depthUv, sceneZ);");
					b.Line("vec3 depthNormal = VfxDepthNormal(depthUv);");
					b.Line("float depthDistance = dot(depthWorld - sceneWorld, depthNormal);");
					b.Writer.BeginBlock("if (sceneZ < 1.0 && depthDistance > -" + b.Emit("Surface Thickness") + ")");
					b.Line("vec3 depthLocalNormal = " + VectorFromWorld(b, "depthNormal") + ";");
					b.Line(Collide(b, "depthDistance", "depthLocalNormal"));
					b.Writer.EndBlock();
					b.Writer.EndBlock();
					MarkCollisionWrites(b);
				});
			Block("Block.CollideSdf", "Collide With Global SDF", category, "Particles bounce off the scene geometry through the GI's global distance field.", VfxContextMask.Update,
				node => Slots(CollisionInputs()),
				b =>
				{
					b.Writer.UsesSdf = true;
					b.Line("vec3 sdfWorld = " + PointToWorld(b, b.Read(VfxAttributes.Position)) + ";");
					b.Writer.BeginBlock("if (VfxGlobalSdfAvailable(sdfWorld))");
					b.Line("float sdfDistance = VfxSampleGlobalSdf(sdfWorld);");
					b.Line("vec3 sdfNormal = " + VectorFromWorld(b, "VfxGlobalSdfNormal(sdfWorld)") + ";");
					b.Line(Collide(b, "sdfDistance", "sdfNormal"));
					b.Writer.EndBlock();
					MarkCollisionWrites(b);
				});
		}

		// ---------------------------------------------------------------- kill

		private static void RegisterKill()
		{
			const string category = "Kill";
			Block("Block.KillBox", "Kill (Box)", category, "Kills particles inside (or outside) an axis-aligned box.", VfxContextMask.Update,
				node => Slots(V3("Center"), V3("Size", 1f, 1f, 1f)),
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					b.Line("bool killInside = all(lessThanEqual(abs(" + b.Read(VfxAttributes.Position) + " - " + center + "), " + b.Emit("Size") + " * 0.5));");
					b.Line("if (" + (b.Setting("Mode", "Inside") == "Inside" ? "killInside" : "!killInside") + ") " + b.Read(VfxAttributes.Alive) + " = false;");
					b.Writer.AttributesWritten.Add(VfxAttributes.Alive.Name);
				}, VfxSettingDefinition.Enum("Mode", "Inside", "Inside", "Outside"), SpaceSetting());
			Block("Block.KillSphere", "Kill (Sphere)", category, "Kills particles inside (or outside) a sphere.", VfxContextMask.Update,
				node => Slots(V3("Center"), F("Radius", 1f)),
				b =>
				{
					string center = PointToSystem(b, b.Emit("Center"), BlockWorld(b));
					b.Line("bool killInside = distance(" + b.Read(VfxAttributes.Position) + ", " + center + ") <= " + b.Emit("Radius") + ";");
					b.Line("if (" + (b.Setting("Mode", "Inside") == "Inside" ? "killInside" : "!killInside") + ") " + b.Read(VfxAttributes.Alive) + " = false;");
					b.Writer.AttributesWritten.Add(VfxAttributes.Alive.Name);
				}, VfxSettingDefinition.Enum("Mode", "Inside", "Inside", "Outside"), SpaceSetting());
			Block("Block.KillPlane", "Kill (Plane)", category, "Kills particles below a plane.", VfxContextMask.Update,
				node => Slots(V3("Position"), V3("Normal", 0f, 1f, 0f)),
				b =>
				{
					string position = PointToSystem(b, b.Emit("Position"), BlockWorld(b));
					string normal = VectorToSystem(b, b.Emit("Normal"), BlockWorld(b));
					b.Line("if (dot(" + b.Read(VfxAttributes.Position) + " - " + position + ", " + normal + ") < 0.0) " + b.Read(VfxAttributes.Alive) + " = false;");
					b.Writer.AttributesWritten.Add(VfxAttributes.Alive.Name);
				}, SpaceSetting());
			Block("Block.KillIf", "Kill (Condition)", category, "Kills particles for which the predicate holds.", VfxContextMask.Update,
				node => Slots(new VfxSlotDefinition("Predicate", VfxValueType.Bool, VfxValue.FromBool(false))),
				b =>
				{
					b.Line("if (" + b.Emit("Predicate") + ") " + b.Read(VfxAttributes.Alive) + " = false;");
					b.Writer.AttributesWritten.Add(VfxAttributes.Alive.Name);
				});
		}

		// ---------------------------------------------------------------- over life

		private static string AgeRatio(VfxBlockBuild b)
		{
			return "clamp(" + b.Read(VfxAttributes.Age) + " / max(" + b.Read(VfxAttributes.Lifetime) + ", 1e-5), 0.0, 1.0)";
		}

		private static void RegisterOverLife()
		{
			const string category = "Over Life";
			Block("Block.SizeOverLife", "Size Over Life", category, "The size follows a curve over the particle's life (Multiply keeps a random base size: use it in the output context).", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("Size", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Bell()))),
				b => Compose(b, VfxAttributes.Size, b.Emit(VfxExpression.SampleCurve(b.Input("Size"), VfxExpression.Raw(VfxValueType.Float, AgeRatio(b))))),
				CompositionSetting("Multiply"));
			Block("Block.ScaleOverLife", "Scale Over Life", category, "Each axis of the scale follows its own curve over the particle's life.", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("X", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Constant(1f))), new VfxSlotDefinition("Y", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Constant(1f))), new VfxSlotDefinition("Z", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Constant(1f)))),
				b =>
				{
					VfxExpression t = VfxExpression.Raw(VfxValueType.Float, AgeRatio(b));
					string value = "vec3(" + b.Emit(VfxExpression.SampleCurve(b.Input("X"), t)) + ", " + b.Emit(VfxExpression.SampleCurve(b.Input("Y"), t)) + ", " + b.Emit(VfxExpression.SampleCurve(b.Input("Z"), t)) + ")";
					Compose(b, VfxAttributes.Scale, value);
				}, CompositionSetting("Multiply"));
			Block("Block.ColorOverLife", "Color Over Life", category, "The color and alpha follow a gradient over the particle's life.", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("Gradient", VfxValueType.Gradient, VfxValue.FromGradient(VfxGradient.WhiteToTransparent()))),
				b =>
				{
					string sample = b.Emit(VfxExpression.SampleGradient(b.Input("Gradient"), VfxExpression.Raw(VfxValueType.Float, AgeRatio(b))));
					string mode = b.Setting("Mode", "ColorAndAlpha");
					if (mode != "Alpha") Compose(b, VfxAttributes.Color, sample + ".rgb");
					if (mode != "Color") Compose(b, VfxAttributes.Alpha, sample + ".a");
				}, VfxSettingDefinition.Enum("Mode", "ColorAndAlpha", "ColorAndAlpha", "Color", "Alpha"), CompositionSetting("Multiply"));
			Block("Block.AlphaOverLife", "Alpha Over Life", category, "The alpha follows a curve over the particle's life.", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("Alpha", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Linear(1f, 0f)))),
				b => Compose(b, VfxAttributes.Alpha, b.Emit(VfxExpression.SampleCurve(b.Input("Alpha"), VfxExpression.Raw(VfxValueType.Float, AgeRatio(b))))),
				CompositionSetting("Multiply"));
			Block("Block.ColorBySpeed", "Color By Speed", category, "The color and alpha come from a gradient indexed by the speed.", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("Gradient", VfxValueType.Gradient, VfxValue.FromGradient(VfxGradient.WhiteToTransparent())), F("Min Speed", 0f), F("Max Speed", 5f)),
				b =>
				{
					string t = "clamp((length(" + b.Read(VfxAttributes.Velocity) + ") - " + b.Emit("Min Speed") + ") / max(" + b.Emit("Max Speed") + " - " + b.Emit("Min Speed") + ", 1e-5), 0.0, 1.0)";
					string sample = b.Emit(VfxExpression.SampleGradient(b.Input("Gradient"), VfxExpression.Raw(VfxValueType.Float, t)));
					string mode = b.Setting("Mode", "ColorAndAlpha");
					if (mode != "Alpha") Compose(b, VfxAttributes.Color, sample + ".rgb");
					if (mode != "Color") Compose(b, VfxAttributes.Alpha, sample + ".a");
				}, VfxSettingDefinition.Enum("Mode", "ColorAndAlpha", "ColorAndAlpha", "Color", "Alpha"), CompositionSetting("Multiply"));
			Block("Block.SizeBySpeed", "Size By Speed", category, "The size comes from a curve indexed by the speed.", VfxContextMask.Particle,
				node => Slots(new VfxSlotDefinition("Size", VfxValueType.Curve, VfxValue.FromCurve(VfxCurve.Linear(0.5f, 1.5f))), F("Min Speed", 0f), F("Max Speed", 5f)),
				b =>
				{
					string t = "clamp((length(" + b.Read(VfxAttributes.Velocity) + ") - " + b.Emit("Min Speed") + ") / max(" + b.Emit("Max Speed") + " - " + b.Emit("Min Speed") + ", 1e-5), 0.0, 1.0)";
					Compose(b, VfxAttributes.Size, b.Emit(VfxExpression.SampleCurve(b.Input("Size"), VfxExpression.Raw(VfxValueType.Float, t))));
				}, CompositionSetting("Multiply"));
			Block("Block.ColorFromGradientRandom", "Set Color (Random From Gradient)", "Attribute/Set", "Picks a random color of a gradient for the particle.", VfxContextMask.InitializeUpdate,
				node => Slots(new VfxSlotDefinition("Gradient", VfxValueType.Gradient, VfxValue.FromGradient(VfxGradient.Solid(Color4.White)))),
				b =>
				{
					string sample = b.Emit(VfxExpression.SampleGradient(b.Input("Gradient"), b.Random(VfxValueType.Float)));
					string mode = b.Setting("Mode", "ColorAndAlpha");
					if (mode != "Alpha") Compose(b, VfxAttributes.Color, sample + ".rgb");
					if (mode != "Color") Compose(b, VfxAttributes.Alpha, sample + ".a");
				}, VfxSettingDefinition.Enum("Mode", "ColorAndAlpha", "ColorAndAlpha", "Color", "Alpha"), CompositionSetting());
		}

		// ---------------------------------------------------------------- flipbook and triggers

		private static void RegisterFlipbookAndTriggers()
		{
			Block("Block.FlipbookPlayer", "Flipbook Player", "Flipbook", "Advances the texIndex attribute at a frame rate (looping or stopping at the last frame).", VfxContextMask.Update,
				node => Slots(F("Frame Rate", 16f, "Frames per second."), F("Frame Count", 16f, "Frames in the sheet (columns x rows).")),
				b =>
				{
					string next = b.Read(VfxAttributes.TexIndex) + " + " + b.Emit("Frame Rate") + " * vfxDeltaTime";
					string count = "max(" + b.Emit("Frame Count") + ", 1.0)";
					switch (b.Setting("Mode", "Loop"))
					{
						case "Once":
							b.Write(VfxAttributes.TexIndex, "min(" + next + ", " + count + " - 1.0)");
							break;
						case "PingPong":
							b.Line("float flipRaw = mod(" + next + ", " + count + " * 2.0);");
							b.Line("float flipCount = " + count + ";");
							b.Write(VfxAttributes.TexIndex, "flipRaw < flipCount ? flipRaw : flipCount * 2.0 - flipRaw - 1.0");
							break;
						default:
							b.Write(VfxAttributes.TexIndex, "mod(" + next + ", " + count + ")");
							break;
					}
				}, VfxSettingDefinition.Enum("Mode", "Loop", "Loop", "Once", "PingPong"));
			Block("Block.FlipbookOverLife", "Flipbook Over Life", "Flipbook", "Plays the whole sheet once over the particle's life.", VfxContextMask.Particle,
				node => Slots(F("Frame Count", 16f)),
				b => b.Write(VfxAttributes.TexIndex, AgeRatio(b) + " * (max(" + b.Emit("Frame Count") + ", 1.0) - 0.001)"));

			const string trigger = "GPU Event";
			VfxNodeDefinition onDie = Block("Block.TriggerOnDie", "Trigger Event On Die", trigger, "Raises a GPU event when the particle dies; a child system connected to the Update context's event port spawns from it.", VfxContextMask.Update,
				node => Slots(U("Count", 1, "Particles the child system spawns per event.")),
				b =>
				{
					b.Writer.BeginSection(b.Writer.DeathBody);
					b.TriggerEvent(b.Setting("Event", "OnDie"), "true", b.Emit("Count"));
					b.Writer.EndSection();
				}, VfxSettingDefinition.Text("Event", "OnDie", "Name of the event port on the Update context.", true));
			onDie.Keywords = "trigger";
			onDie.TitleFactory = node => "Trigger On Die: " + node.GetSetting("Event", "OnDie");
			VfxNodeDefinition rate = Block("Block.TriggerRate", "Trigger Event Rate", trigger, "Raises GPU events at a rate per second (or per unit of distance travelled) while the particle lives.", VfxContextMask.Update,
				node => Slots(F("Rate", 10f)),
				b =>
				{
					string amount = b.Setting("Mode", "OverTime") == "OverDistance"
						? b.Emit("Rate") + " * length(" + b.Read(VfxAttributes.Position) + " - " + b.Read(VfxAttributes.OldPosition) + ")"
						: b.Emit("Rate") + " * vfxDeltaTime";
					b.Line("float triggerAmount = " + amount + ";");
					b.Line("uint triggerCount = uint(floor(triggerAmount + VfxRandom01(attr_seed ^ (vfxFrameIndex * 0x9E3779B9u), " + b.Salt + "u)));");
					b.Writer.ReadAttribute(VfxAttributes.Seed);
					b.TriggerEvent(b.Setting("Event", "OnRate"), "triggerCount > 0u", "triggerCount");
				}, VfxSettingDefinition.Text("Event", "OnRate", null, true), VfxSettingDefinition.Enum("Mode", "OverTime", "OverTime", "OverDistance"));
			rate.Keywords = "trigger";
			rate.TitleFactory = node => "Trigger Rate: " + node.GetSetting("Event", "OnRate");
			VfxNodeDefinition always = Block("Block.TriggerAlways", "Trigger Event Always", trigger, "Raises a GPU event every frame for every particle.", VfxContextMask.Update,
				node => Slots(U("Count", 1)),
				b => b.TriggerEvent(b.Setting("Event", "OnAlways"), "true", b.Emit("Count")),
				VfxSettingDefinition.Text("Event", "OnAlways", null, true));
			always.Keywords = "trigger";
			always.TitleFactory = node => "Trigger Always: " + node.GetSetting("Event", "OnAlways");
		}

		// ---------------------------------------------------------------- custom code

		private static readonly Regex InputDeclaration = new Regex(@"^\s*(?<type>float|int|uint|bool|vec2|vec3|vec4|color|texture2D|gradient|curve)\s+(?<name>[A-Za-z_]\w*)\s*(=\s*(?<value>.*))?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
		private static readonly Regex AttributeUse = new Regex(@"\battr_([A-Za-z_]\w*)", RegexOptions.Compiled);

		private static void RegisterCustom()
		{
			VfxNodeDefinition custom = Block("Block.CustomCode", "Custom Code", "Custom", "GLSL statements run per particle. Attributes are attr_position, attr_velocity, attr_age... (see VfxAttributes); inputs declared in the Inputs setting are variables of the same name.", VfxContextMask.Particle,
				node =>
				{
					var slots = new List<VfxSlotDefinition>();
					foreach (string line in (node.GetSetting("Inputs", "") ?? "").Split('\n'))
					{
						Match match = InputDeclaration.Match(line.Trim());
						if (!match.Success)
						{
							continue;
						}

						VfxValueType type = CustomType(match.Groups["type"].Value);
						VfxValue value = VfxValue.Default(type);
						if (match.Groups["value"].Success && VfxValueTypes.IsNumeric(type))
						{
							string[] parts = match.Groups["value"].Value.Trim().Trim('(', ')').Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
							var v = new float[4];
							for (int i = 0; i < parts.Length && i < 4; i++)
							{
								float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]);
							}

							value.Vector = new Vector4(v[0], v[1], v[2], v[3]);
						}

						slots.Add(new VfxSlotDefinition(match.Groups["name"].Value, type, value));
					}

					return slots;
				},
				b =>
				{
					foreach (VfxSlotDefinition slot in b.InputSlots)
					{
						VfxExpression input = b.Input(slot.Name);
						switch (slot.Type)
						{
							case VfxValueType.Texture2D:
								b.Line("#define " + slot.Name + " " + b.Writer.Resources.Texture(input, false).Uniform);
								break;
							case VfxValueType.Gradient:
								b.Line("uint " + slot.Name + " = " + b.Writer.Resources.Curve(input, true).Offset.ToString(CultureInfo.InvariantCulture) + "u; // VfxSampleGradient(" + slot.Name + ", t)");
								b.Writer.UsesCurves = true;
								break;
							case VfxValueType.Curve:
								b.Line("uint " + slot.Name + " = " + b.Writer.Resources.Curve(input, false).Offset.ToString(CultureInfo.InvariantCulture) + "u; // VfxSampleCurve(" + slot.Name + ", t)");
								b.Writer.UsesCurves = true;
								break;
							default:
								b.Line(VfxValueTypes.Glsl(slot.Type) + " " + slot.Name + " = " + b.Emit(input) + ";");
								break;
						}
					}

					string code = b.Setting("Code", "") ?? "";
					foreach (Match match in AttributeUse.Matches(code))
					{
						VfxAttribute attribute = VfxAttributes.Find(match.Groups[1].Value);
						if (attribute != null)
						{
							b.Read(attribute);
							b.Writer.AttributesWritten.Add(attribute.Name);
						}
					}

					if (code.Contains("VfxPerlin") || code.Contains("VfxCurlNoise") || code.Contains("VfxValueNoise")) b.Writer.UsesNoise = true;
					if (code.Contains("VfxSampleGlobalSdf") || code.Contains("VfxGlobalSdf")) b.Writer.UsesSdf = true;
					if (code.Contains("VfxRandom")) b.Read(VfxAttributes.Seed);
					b.Writer.BeginBlock("");
					foreach (string line in code.Replace("\r", "").Split('\n'))
					{
						b.Line(line);
					}

					b.Writer.EndBlock();
				},
				VfxSettingDefinition.Text("Inputs", "float Strength = 1\nvec3 Axis = 0 1 0", "One input per line: type name = default (float, int, uint, bool, vec2, vec3, vec4, color, texture2D, gradient, curve).", true),
				new VfxSettingDefinition("Code", VfxSettingKind.Code, "attr_velocity += Axis * Strength * vfxDeltaTime;", null, "GLSL statements. Use vfxDeltaTime, vfxTotalTime, attr_* attributes and the declared inputs."));
			custom.TitleFactory = node => "Custom Code";
		}

		private static VfxValueType CustomType(string text)
		{
			switch (text.ToLowerInvariant())
			{
				case "int": return VfxValueType.Int;
				case "uint": return VfxValueType.Uint;
				case "bool": return VfxValueType.Bool;
				case "vec2": return VfxValueType.Vector2;
				case "vec3": return VfxValueType.Vector3;
				case "vec4": return VfxValueType.Vector4;
				case "color": return VfxValueType.Color;
				case "texture2d": return VfxValueType.Texture2D;
				case "gradient": return VfxValueType.Gradient;
				case "curve": return VfxValueType.Curve;
				default: return VfxValueType.Float;
			}
		}

		#endregion
	}
}
