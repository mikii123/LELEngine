using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LELEngine.VFX
{
	/// <summary>
	///     Assembles the GLSL of a compiled system: the particle buffer accessors, the kernel prologues (attribute loads)
	///     and epilogues (stores, alive / dead list bookkeeping), the implicit update behaviour (aging, integration,
	///     reaping, strips, bounds) and the draw programs of every output kind (quads, meshes, points, lines, strips).
	///     The block bodies come from the writers filled by <see cref="VfxCompiler" />.
	/// </summary>
	/// <remarks>
	///     The kernels avoid per-particle atomics on shared counters: the frame kernel (VfxFrame.shader) reserves the
	///     slots of the spawned particles before the initialize runs (each thread knows its dead-stack slot and its
	///     alive-list slot from its index), and the update gathers its survivors, its dead, its GPU events and its
	///     bounds per work group in shared memory, so one thread per group touches the global counters.
	/// </remarks>
	public static class VfxCodegen
	{
		#region PublicFields

		public const int GroupSize = 64;

		/// <summary>Uints of a system's block in the runtime's counter pool (VfxCommon.glsl VFX_C_*).</summary>
		public const int CounterInitDispatch = 0;

		public const int CounterSpawn = 3;
		public const int CounterUpdateDispatch = 4;
		public const int CounterAliveIn = 7;
		public const int CounterAliveOut = 8;
		public const int CounterDead = 9;
		public const int CounterInitAliveBase = 10;
		public const int CounterInitDeadTop = 11;
		public const int CounterBounds = 12;
		public const int CounterIdNext = 18;
		public const int CounterIdBase = 19;
		public const int CounterEvents = 20;
		public const int CounterDraw = 24;
		public const int CounterDrawStride = 5;
		public const int MaxOutputs = 8;
		public const int CounterBlock = CounterDraw + CounterDrawStride * MaxOutputs;

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- implicit update behaviour

		/// <summary>Aging, integration, reaping and strip recording of the update kernel (emitted before storage is decided).</summary>
		public static void EmitUpdateImplicit(VfxSystemDescription system)
		{
			VfxGlslWriter writer = system.UpdateWriter;
			writer.BeginSection(writer.PreBody);
			if (system.TrackOldPosition || writer.AttributesRead.Contains(VfxAttributes.OldPosition.Name))
			{
				writer.WriteAttribute(VfxAttributes.OldPosition, writer.ReadAttribute(VfxAttributes.Position));
			}

			if (system.AgeParticles)
			{
				writer.WriteAttribute(VfxAttributes.Age, writer.ReadAttribute(VfxAttributes.Age) + " + vfxDeltaTime");
			}

			writer.EndSection();

			writer.Comment("System");
			bool hasVelocity = system.InitWriter.AttributesWritten.Contains(VfxAttributes.Velocity.Name) || writer.AttributesWritten.Contains(VfxAttributes.Velocity.Name);
			if (system.IntegratePosition && hasVelocity)
			{
				writer.WriteAttribute(VfxAttributes.Position, writer.ReadAttribute(VfxAttributes.Position) + " + " + writer.ReadAttribute(VfxAttributes.Velocity) + " * vfxDeltaTime");
			}

			bool hasAngularVelocity = system.InitWriter.AttributesWritten.Contains(VfxAttributes.AngularVelocity.Name) || writer.AttributesWritten.Contains(VfxAttributes.AngularVelocity.Name);
			if (system.IntegrateAngle && hasAngularVelocity)
			{
				writer.WriteAttribute(VfxAttributes.Angle, writer.ReadAttribute(VfxAttributes.Angle) + " + " + writer.ReadAttribute(VfxAttributes.AngularVelocity) + " * vfxDeltaTime");
			}

			if (system.ReapParticles)
			{
				writer.Line("if (" + writer.ReadAttribute(VfxAttributes.Age) + " >= " + writer.ReadAttribute(VfxAttributes.Lifetime) + ") " + writer.ReadAttribute(VfxAttributes.Alive) + " = false;");
				writer.AttributesWritten.Add(VfxAttributes.Alive.Name);
			}

			if (system.UsesStrip)
			{
				writer.ReadAttribute(VfxAttributes.StripHead);
				writer.ReadAttribute(VfxAttributes.StripCount);
				writer.AttributesWritten.Add(VfxAttributes.StripHead.Name);
				writer.AttributesWritten.Add(VfxAttributes.StripCount.Name);
				writer.ReadAttribute(VfxAttributes.Position);
			}
		}

		// ---------------------------------------------------------------- kernels

		public static string AssembleInitialize(VfxSystemDescription system)
		{
			VfxGlslWriter writer = system.InitWriter;
			var code = new StringBuilder();
			Header(code, system, writer, VfxKernelKind.Initialize);
			code.Append("layout(local_size_x = ").Append(GroupSize).Append(") in;\n");
			Includes(code, system, writer);
			Accessors(code, system);
			code.Append(writer.Declarations);
			code.Append("\nvoid main()\n{\n");
			// The frame kernel reserved the slots: thread i takes the i-th free slot from the top of the dead stack and
			// the i-th alive-list slot after last frame's survivors, so the threads need no atomics.
			code.Append("\tuint spawnIndex = gl_GlobalInvocationID.x;\n");
			code.Append("\tuint spawnCount = VFX_COUNTER(VFX_C_SPAWN);\n");
			code.Append("\tif (spawnIndex >= spawnCount) return;\n");
			code.Append("\tuint particleIndex = vfxDead[VFX_COUNTER(VFX_C_INIT_DEAD_TOP) - 1u - spawnIndex];\n");
			if (system.HasGpuEventParent)
			{
				code.Append("\tVfxEventPayload src = vfxEventIn[spawnIndex];\n");
				code.Append("\tvec3 src_position = src.positionSize.xyz;\n\tfloat src_size = src.positionSize.w;\n");
				code.Append("\tvec3 src_velocity = src.velocityLifetime.xyz;\n\tfloat src_lifetime = src.velocityLifetime.w;\n");
				code.Append("\tvec3 src_color = src.colorAlpha.xyz;\n\tfloat src_alpha = src.colorAlpha.w;\n");
				code.Append("\tuint src_seed = src.seedIdAge.x;\n\tuint src_particleId = src.seedIdAge.y;\n\tfloat src_age = uintBitsToFloat(src.seedIdAge.z);\n");
			}

			// Every used attribute starts at its default; the system's own ones are set here.
			List<string> used = UsedAttributes(system, writer, true);
			foreach (string name in used)
			{
				VfxAttribute attribute = system.Attributes[name];
				code.Append('\t').Append(VfxValueTypes.Glsl(attribute.Type)).Append(' ').Append(attribute.Variable).Append(" = ").Append(VfxGlsl.Literal(attribute.Default)).Append(";\n");
			}

			code.Append("\tattr_particleId = VFX_COUNTER(VFX_C_ID_BASE) + spawnIndex;\n");
			code.Append("\tattr_seed = VfxPcg(attr_particleId * 0x9E3779B9u ^ VfxPcg(vfxSystemSeed + 0x68E31DA4u) ^ VfxPcg(vfxFrameIndex * 0xB5297A4Du));\n");
			code.Append("\tattr_spawnTime = vfxTotalTime;\n");
			code.Append("\tattr_age = 0.0;\n");
			code.Append(writer.PreBody);
			code.Append(writer.Body);

			code.Append("\t// Store every attribute the buffer holds (the slot may hold a dead particle's values).\n");
			foreach (VfxStoredAttribute stored in system.Stored)
			{
				code.Append("\tVfxStore_").Append(stored.Attribute.Name).Append("(particleIndex, ").Append(stored.Attribute.Variable).Append(");\n");
			}

			if (system.UsesStrip)
			{
				code.Append("\tvfxStrip[particleIndex * VFX_STRIP_LENGTH] = vec4(attr_position, 0.0);\n");
			}

			code.Append("\tvfxAliveIn[VFX_COUNTER(VFX_C_INIT_ALIVE_BASE) + spawnIndex] = particleIndex;\n");
			code.Append("\tvfxAliveMask[particleIndex] = 1u;\n");
			code.Append("}\n");
			return code.ToString();
		}

		public static string AssembleUpdate(VfxSystemDescription system)
		{
			VfxGlslWriter writer = system.UpdateWriter;
			var code = new StringBuilder();
			Header(code, system, writer, VfxKernelKind.Update);
			code.Append("layout(local_size_x = ").Append(GroupSize).Append(") in;\n");
			Includes(code, system, writer);
			Accessors(code, system);
			code.Append(writer.Declarations);
			bool bounds = system.BoundsMode == VfxBoundsMode.Automatic;
			int events = Math.Min(system.Events.Count, VfxCompiler.MaxEventsPerSystem);

			// What a work group gathers before one of its threads touches the global counters.
			code.Append("\nshared uint vfxGroupAlive;\nshared uint vfxGroupDead;\nshared uint vfxGroupAliveBase;\nshared uint vfxGroupDeadBase;\n");
			if (bounds)
			{
				code.Append("shared uint vfxGroupBounds[6];\n");
			}

			for (int e = 0; e < events; e++)
			{
				code.Append("shared uint vfxGroupEvents").Append(e).Append(";\nshared uint vfxGroupEventBase").Append(e).Append(";\n");
			}

			code.Append("vec3 vfxBoundsPosition = vec3(0.0);\n");

			// The particle's frame: in a function so the block code runs apart from the group bookkeeping.
			code.Append("\n// Updates the particle; false when it died this frame.\nbool VfxUpdateParticle(uint particleIndex)\n{\n");
			LoadAttributes(code, system, writer, UsedAttributes(system, writer, false));
			code.Append(writer.PreBody);
			code.Append(writer.Body);
			code.Append("\tif (attr_alive)\n\t{\n");
			foreach (VfxStoredAttribute stored in system.Stored)
			{
				if (writer.AttributesWritten.Contains(stored.Attribute.Name))
				{
					code.Append("\t\tVfxStore_").Append(stored.Attribute.Name).Append("(particleIndex, ").Append(stored.Attribute.Variable).Append(");\n");
				}
			}

			if (system.UsesStrip)
			{
				// A new sample once the particle moved the spacing away from the last fixed one; the head (the tip) always follows.
				code.Append("\t\tuint stripHead = attr_stripHead;\n\t\tuint stripCount = attr_stripCount;\n");
				code.Append("\t\tvec3 stripAnchor = vfxStrip[particleIndex * VFX_STRIP_LENGTH + ((stripHead + VFX_STRIP_LENGTH - 1u) % VFX_STRIP_LENGTH)].xyz;\n");
				code.Append("\t\tif (stripCount < 2u || VFX_STRIP_SPACING <= 0.0 || distance(stripAnchor, attr_position) >= VFX_STRIP_SPACING)\n\t\t{\n");
				code.Append("\t\t\tstripHead = (stripHead + 1u) % VFX_STRIP_LENGTH;\n\t\t\tstripCount = min(stripCount + 1u, VFX_STRIP_LENGTH);\n\t\t}\n");
				code.Append("\t\tvfxStrip[particleIndex * VFX_STRIP_LENGTH + stripHead] = vec4(attr_position, attr_age);\n");
				code.Append("\t\tVfxStore_stripHead(particleIndex, stripHead);\n");
				code.Append("\t\tVfxStore_stripCount(particleIndex, stripCount);\n");
			}

			code.Append("\t\tvfxBoundsPosition = attr_position;\n");
			code.Append("\t\treturn true;\n\t}\n\n");
			code.Append(writer.DeathBody);
			code.Append("\treturn false;\n}\n");

			code.Append("\nvoid main()\n{\n");
			code.Append("\tuint lane = gl_LocalInvocationIndex;\n");
			code.Append("\tif (lane == 0u)\n\t{\n\t\tvfxGroupAlive = 0u;\n\t\tvfxGroupDead = 0u;\n");
			if (bounds)
			{
				code.Append("\t\tvfxGroupBounds[0] = 0xFFFFFFFFu; vfxGroupBounds[1] = 0xFFFFFFFFu; vfxGroupBounds[2] = 0xFFFFFFFFu;\n");
				code.Append("\t\tvfxGroupBounds[3] = 0u; vfxGroupBounds[4] = 0u; vfxGroupBounds[5] = 0u;\n");
			}

			for (int e = 0; e < events; e++)
			{
				code.Append("\t\tvfxGroupEvents").Append(e).Append(" = 0u;\n");
			}

			code.Append("\t}\n\n\tbarrier();\n");
			// Dense systems walk every slot in order (coalesced reads and writes, an ordered draw list); sparse ones
			// walk the alive list.
			code.Append("\tuint slot = gl_GlobalInvocationID.x;\n");
			code.Append("\tbool vfxActive;\n\tuint particleIndex;\n");
			code.Append("\tif (vfxDenseUpdate)\n\t{\n\t\tparticleIndex = slot;\n\t\tvfxActive = slot < VFX_CAPACITY && vfxAliveMask[slot] != 0u;\n\t}\n");
			code.Append("\telse\n\t{\n\t\tvfxActive = slot < VFX_COUNTER(VFX_C_ALIVE_IN);\n\t\tparticleIndex = vfxActive ? vfxAliveIn[slot] : 0u;\n\t}\n\n");
			code.Append("\tbool survived = false;\n\tuint localSlot = 0u;\n");
			for (int e = 0; e < events; e++)
			{
				code.Append("\tuint localEvent").Append(e).Append(" = 0u;\n");
			}

			code.Append("\tif (vfxActive)\n\t{\n");
			code.Append("\t\tsurvived = VfxUpdateParticle(particleIndex);\n");
			code.Append("\t\tlocalSlot = survived ? atomicAdd(vfxGroupAlive, 1u) : atomicAdd(vfxGroupDead, 1u);\n");
			if (bounds)
			{
				code.Append("\t\tif (survived)\n\t\t{\n");
				code.Append("\t\t\tuvec3 ordered = uvec3(VfxOrderedUint(vfxBoundsPosition.x), VfxOrderedUint(vfxBoundsPosition.y), VfxOrderedUint(vfxBoundsPosition.z));\n");
				code.Append("\t\t\tatomicMin(vfxGroupBounds[0], ordered.x); atomicMin(vfxGroupBounds[1], ordered.y); atomicMin(vfxGroupBounds[2], ordered.z);\n");
				code.Append("\t\t\tatomicMax(vfxGroupBounds[3], ordered.x); atomicMax(vfxGroupBounds[4], ordered.y); atomicMax(vfxGroupBounds[5], ordered.z);\n");
				code.Append("\t\t}\n");
			}

			for (int e = 0; e < events; e++)
			{
				code.Append("\t\tif (vfxEvent").Append(e).Append("Count > 0u) localEvent").Append(e).Append(" = atomicAdd(vfxGroupEvents").Append(e).Append(", vfxEvent").Append(e).Append("Count);\n");
			}

			code.Append("\t}\n\n\tbarrier();\n");

			// One thread per group reserves the group's room in the system's lists and adds to its draw arguments.
			code.Append("\tif (lane == 0u)\n\t{\n");
			code.Append("\t\tuint alive = vfxGroupAlive;\n");
			code.Append("\t\tif (alive > 0u)\n\t\t{\n");
			code.Append("\t\t\tvfxGroupAliveBase = atomicAdd(VFX_COUNTER(VFX_C_ALIVE_OUT), alive);\n");
			int outputs = Math.Min(system.Outputs.Count, MaxOutputs);
			for (int o = 0; o < outputs; o++)
			{
				VfxOutputDescription output = system.Outputs[o];
				int field = CounterDraw + o * CounterDrawStride + output.DrawCountField;
				int perParticle = Math.Max(1, output.DrawCountPerParticle);
				code.Append("\t\t\tatomicAdd(VFX_COUNTER(").Append(field).Append("u), alive");
				if (perParticle != 1)
				{
					code.Append(" * ").Append(perParticle).Append('u');
				}

				code.Append("); // ").Append(output.Name).Append('\n');
			}

			if (bounds)
			{
				code.Append("\t\t\tatomicMin(VFX_COUNTER(VFX_C_BOUNDS + 0u), vfxGroupBounds[0]); atomicMin(VFX_COUNTER(VFX_C_BOUNDS + 1u), vfxGroupBounds[1]); atomicMin(VFX_COUNTER(VFX_C_BOUNDS + 2u), vfxGroupBounds[2]);\n");
				code.Append("\t\t\tatomicMax(VFX_COUNTER(VFX_C_BOUNDS + 3u), vfxGroupBounds[3]); atomicMax(VFX_COUNTER(VFX_C_BOUNDS + 4u), vfxGroupBounds[4]); atomicMax(VFX_COUNTER(VFX_C_BOUNDS + 5u), vfxGroupBounds[5]);\n");
			}

			code.Append("\t\t}\n\n");
			code.Append("\t\tif (vfxGroupDead > 0u) vfxGroupDeadBase = atomicAdd(VFX_COUNTER(VFX_C_DEAD), vfxGroupDead);\n");
			for (int e = 0; e < events; e++)
			{
				code.Append("\t\tif (vfxGroupEvents").Append(e).Append(" > 0u) vfxGroupEventBase").Append(e).Append(" = atomicAdd(VFX_COUNTER(VFX_C_EVENTS + ").Append(e).Append("u), vfxGroupEvents").Append(e).Append(");\n");
			}

			code.Append("\t}\n\n\tbarrier();\n");
			code.Append("\tif (!vfxActive)\n\t{\n\t\treturn;\n\t}\n\n");
			code.Append("\tif (survived)\n\t{\n\t\tvfxAliveOut[vfxGroupAliveBase + localSlot] = particleIndex;\n\t}\n");
			code.Append("\telse\n\t{\n\t\tvfxDead[vfxGroupDeadBase + localSlot] = particleIndex;\n\t\tvfxAliveMask[particleIndex] = 0u;\n\t}\n\n");
			for (int e = 0; e < events; e++)
			{
				string count = "vfxEvent" + e + "Count";
				string payload = "vfxEvent" + e + "Payload";
				code.Append("\tfor (uint i = 0u; i < ").Append(count).Append("; i++)\n\t{\n");
				code.Append("\t\tuint eventSlot = vfxGroupEventBase").Append(e).Append(" + localEvent").Append(e).Append(" + i;\n");
				code.Append("\t\tif (eventSlot >= VFX_EVENT").Append(e).Append("_CAPACITY) break;\n");
				code.Append("\t\tVfxEventPayload payload = ").Append(payload).Append(";\n");
				code.Append("\t\tpayload.seedIdAge.x ^= i * 0x9E3779B9u;\n");
				code.Append("\t\tvfxEventOut").Append(e).Append("[eventSlot] = payload;\n\t}\n");
			}

			code.Append("}\n");
			return code.ToString();
		}

		// ---------------------------------------------------------------- outputs

		/// <summary>Resolves the output context's own inputs into locals of the vertex body (out_* variables).</summary>
		public static void EmitOutputInputs(VfxCompiler compiler, VfxSystemDescription system, VfxOutputDescription output, VfxNodeData node, VfxNodeDefinition definition)
		{
			VfxGlslWriter writer = output.Writer;
			writer.WorldSpace = system.InitWriter.WorldSpace;
			foreach (VfxSlotDefinition slot in definition.GetInputs(node))
			{
				VfxExpression expression = compiler.ResolveInput(node, slot);
				string variable = "out_" + VfxAttributes.SanitizeName(slot.Name);
				switch (slot.Type)
				{
					case VfxValueType.Texture2D:
					{
						VfxResourceTable.TextureSlot textureSlot = writer.Resources.Texture(expression, false);
						// Quads and strips without a texture draw a soft disc instead of a hard square.
						textureSlot.SoftDefault |= slot.Name == "Main Texture" && (output.Kind == VfxOutputKind.Quad || output.Kind == VfxOutputKind.Point);
						textureSlot.StripeDefault |= slot.Name == "Main Texture" && output.Kind == VfxOutputKind.Strip;
						output.Textures[slot.Name] = textureSlot.Uniform;
						break;
					}
					case VfxValueType.Mesh:
						break;
					default:
						writer.Line(VfxValueTypes.Glsl(slot.Type) + " " + variable + " = " + writer.Emit(expression) + ";");
						output.Locals[slot.Name] = variable;
						break;
				}
			}
		}

		/// <summary>The per-vertex geometry of an output (after its blocks), written into the writer so attribute reads are tracked.</summary>
		public static void EmitOutputBody(VfxSystemDescription system, VfxOutputDescription output)
		{
			VfxGlslWriter writer = output.Writer;
			writer.Comment("Geometry");
			string position = writer.ReadAttribute(VfxAttributes.Position);
			string color = writer.ReadAttribute(VfxAttributes.Color);
			string alpha = writer.ReadAttribute(VfxAttributes.Alpha);
			string size = writer.ReadAttribute(VfxAttributes.Size);
			string scale = writer.ReadAttribute(VfxAttributes.Scale);
			writer.Line("vec3 worldCenter = " + (writer.WorldSpace ? position : "(vfxLocalToWorld * vec4(" + position + ", 1.0)).xyz") + ";");
			writer.Line("vec3 outColor = " + color + ";");
			writer.Line("float outAlpha = " + alpha + ";");
			writer.Line("vec3 outScale = " + scale + " * " + size + ";");
			writer.Line("float outTexIndex = " + writer.ReadAttribute(VfxAttributes.TexIndex) + ";");
			switch (output.Kind)
			{
				case VfxOutputKind.Quad:
					EmitQuad(system, output, writer);
					break;
				case VfxOutputKind.Mesh:
					EmitMesh(system, output, writer);
					break;
				case VfxOutputKind.Point:
					writer.Line("vec3 worldPos = worldCenter;");
					writer.Line("vec3 outNormal = -vfxCameraForward;");
					writer.Line("vec2 outUv = vec2(0.5);");
					break;
				case VfxOutputKind.Line:
					EmitLine(system, output, writer);
					break;
				case VfxOutputKind.Strip:
					EmitStrip(system, output, writer);
					break;
			}
		}

		/// <summary>Builds the vertex and fragment sources of an output.</summary>
		public static void AssembleOutput(VfxSystemDescription system, VfxOutputDescription output)
		{
			VfxGlslWriter writer = output.Writer;
			var vertex = new StringBuilder();
			Header(vertex, system, writer, VfxKernelKind.Output);
			vertex.Append("#define VFX_USE_DRAWLIST 1\n");
			if (output.Kind == VfxOutputKind.Strip)
			{
				vertex.Append("#define VFX_USE_STRIP 1\n");
				vertex.Append("#define VFX_STRIP_POINTS ").Append(Math.Clamp(output.StripLength, 2, Math.Max(2, system.StripLength))).Append("u\n");
			}

			Includes(vertex, system, writer);
			Accessors(vertex, system);
			vertex.Append("uniform mat4 projectionMatrix;\nuniform mat4 viewMatrix;\nuniform uint vfxDrawListMask;\nuniform float vfxViewportHeight;\n");
			if (output.Kind == VfxOutputKind.Mesh)
			{
				vertex.Append("in vec3 vPosition;\nin vec3 vNormal;\nin vec2 vTexCoord;\n");
			}

			vertex.Append(writer.Declarations);
			vertex.Append("out VfxVaryings\n{\n\tvec3 worldPos;\n\tvec3 normal;\n\tvec2 uv;\n\tvec4 color;\n\tflat float texIndex;\n\tflat vec2 flipbook;\n\tflat float softDistance;\n\tvec4 clip;\n} v;\n");
			vertex.Append("\nvoid main()\n{\n");
			// Indexed outputs: gl_VertexID is the index value, particle * vertices per particle + corner.
			vertex.Append("\tuint vertexId = uint(gl_VertexID);\n");
			switch (output.Kind)
			{
				case VfxOutputKind.Mesh:
					vertex.Append("\tuint slot = uint(gl_InstanceID);\n\tuint corner = 0u;\n");
					break;
				case VfxOutputKind.Strip:
					vertex.Append("\tuint slot = vertexId / (2u * VFX_STRIP_POINTS);\n\tuint point = (vertexId >> 1u) % VFX_STRIP_POINTS;\n\tuint corner = vertexId & 1u;\n");
					break;
				default:
					int vertices = Math.Max(1, output.VerticesPerParticle);
					vertex.Append("\tuint slot = vertexId / ").Append(vertices).Append("u;\n\tuint corner = vertexId % ").Append(vertices).Append("u;\n");
					break;
			}

			// The alive list (mask all ones) or the sorted keys (the particle index in their low bits).
			vertex.Append("\tuint particleIndex = vfxDrawList[slot] & vfxDrawListMask;\n");
			LoadAttributes(vertex, system, writer, UsedAttributes(system, writer, false));
			vertex.Append(writer.PreBody);
			vertex.Append(writer.Body);
			vertex.Append("\tv.worldPos = worldPos;\n\tv.normal = outNormal;\n\tv.uv = outUv;\n\tv.color = vec4(outColor, outAlpha);\n\tv.texIndex = outTexIndex;\n");
			vertex.Append("\tv.flipbook = " + (output.Locals.ContainsKey("Flipbook Size") ? output.Locals["Flipbook Size"] : "vec2(1.0)") + ";\n");
			vertex.Append("\tv.softDistance = " + (output.Locals.ContainsKey("Soft Particle Distance") ? output.Locals["Soft Particle Distance"] : "1.0") + ";\n");
			vertex.Append("\tvec4 clip = projectionMatrix * viewMatrix * vec4(worldPos, 1.0);\n\tv.clip = clip;\n\tgl_Position = clip;\n");
			if (output.Kind == VfxOutputKind.Point)
			{
				vertex.Append("\tgl_PointSize = max(1.0, outScale.x * projectionMatrix[1][1] * vfxViewportHeight * 0.5 / max(clip.w, 1e-4));\n");
			}

			vertex.Append("}\n");
			output.VertexSource = vertex.ToString();
			output.FragmentSource = AssembleFragment(system, output);
		}

		#endregion

		#region PrivateMethods

		private static void Header(StringBuilder code, VfxSystemDescription system, VfxGlslWriter writer, VfxKernelKind kernel)
		{
			code.Append("#version 430\n");
			code.Append("// Generated by LELEngine VFX for system '").Append(system.Name).Append("' (").Append(kernel).Append(")\n");
			code.Append("#define VFX_UNIFORM_COUNT ").Append(Math.Max(VfxUniformTable.HeaderVectors, system.Uniforms.VectorCount)).Append('\n');
			code.Append("#define VFX_MATRIX_COUNT ").Append(Math.Max(VfxUniformTable.HeaderMatrices, system.Uniforms.MatrixCount)).Append('\n');
			code.Append("#define VFX_CAPACITY ").Append(system.Capacity).Append("u\n");
			code.Append("#define VFX_STRIP_LENGTH ").Append(Math.Max(2, system.StripLength)).Append("u\n");
			code.Append("#define VFX_STRIP_SPACING ").Append(VfxGlsl.Literal(system.StripSpacing)).Append('\n');
			code.Append("#define VFX_WORLD_SPACE ").Append(system.InitWriter.WorldSpace ? 1 : 0).Append('\n');
			switch (kernel)
			{
				case VfxKernelKind.Initialize:
					code.Append("#define VFX_USE_ATTRIBUTES 1\n#define VFX_USE_ALIVE_IN 1\n#define VFX_USE_DEAD 1\n#define VFX_USE_COUNTERS 1\n#define VFX_USE_ALIVE_MASK 1\n");
					if (system.HasGpuEventParent) code.Append("#define VFX_USE_EVENT_IN 1\n");
					if (system.UsesStrip) code.Append("#define VFX_USE_STRIP 1\n");
					break;
				case VfxKernelKind.Update:
					code.Append("#define VFX_USE_ATTRIBUTES 1\n#define VFX_USE_ALIVE_IN 1\n#define VFX_USE_ALIVE_OUT 1\n#define VFX_USE_DEAD 1\n#define VFX_USE_COUNTERS 1\n#define VFX_USE_ALIVE_MASK 1\n");
					for (int e = 0; e < system.Events.Count && e < VfxCompiler.MaxEventsPerSystem; e++)
					{
						code.Append("#define VFX_USE_EVENT_OUT").Append(e).Append(" 1\n");
						code.Append("#define VFX_EVENT").Append(e).Append("_CAPACITY ").Append(Math.Max(0, system.EventCapacities[e])).Append("u\n");
					}

					if (system.UsesStrip) code.Append("#define VFX_USE_STRIP 1\n");
					break;
				case VfxKernelKind.Output:
				case VfxKernelKind.SortKeys:
					code.Append("#define VFX_USE_ATTRIBUTES 1\n#define VFX_DRAW 1\n");
					break;
			}

			if (writer.UsesCurves || system.Resources.Curves.Count > 0) code.Append("#define VFX_USE_CURVES 1\n");
			if (writer.UsesMesh) code.Append("#define VFX_USE_MESH 1\n");
			if (writer.UsesDepth) code.Append("#define VFX_USE_DEPTH 1\n");
		}

		private static void Includes(StringBuilder code, VfxSystemDescription system, VfxGlslWriter writer)
		{
			code.Append("#include \"Engine/VFX/VfxCommon.glsl\"\n");
			if (writer.UsesNoise)
			{
				code.Append("#include \"Engine/VFX/VfxNoise.glsl\"\n");
			}

			if (writer.UsesSdf)
			{
				code.Append("#include \"Engine/DistanceField.glsl\"\n#include \"Engine/VFX/VfxSdf.glsl\"\n");
			}

			foreach (VfxResourceTable.TextureSlot texture in system.Resources.Textures)
			{
				code.Append(texture.Volume ? "uniform sampler3D " : "uniform sampler2D ").Append(texture.Uniform).Append(";\n");
			}

			foreach (VfxResourceTable.MeshSlot mesh in system.Resources.Meshes)
			{
				code.Append("uniform uvec4 vfxMesh").Append(mesh.Index).Append("; // base vertex, vertex count, first index, index count\n");
			}
		}

		/// <summary>Load / store functions of the stored attributes (structure of arrays: one plane of VFX_CAPACITY floats per component).</summary>
		private static void Accessors(StringBuilder code, VfxSystemDescription system)
		{
			foreach (VfxStoredAttribute stored in system.Stored)
			{
				VfxAttribute attribute = stored.Attribute;
				string glsl = VfxValueTypes.Glsl(attribute.Type);
				string name = attribute.Name;
				code.Append(glsl).Append(" VfxLoad_").Append(name).Append("(uint i) { ");
				code.Append("return ").Append(Load(attribute.Type, stored.Plane)).Append("; }\n");
				code.Append("void VfxStore_").Append(name).Append("(uint i, ").Append(glsl).Append(" v) { ");
				code.Append(Store(attribute.Type, stored.Plane)).Append(" }\n");
			}

			code.Append('\n');
		}

		private static string Plane(int plane)
		{
			return "vfxAttr[" + plane.ToString(CultureInfo.InvariantCulture) + "u * VFX_CAPACITY + i]";
		}

		private static string Load(VfxValueType type, int plane)
		{
			switch (type)
			{
				case VfxValueType.Float: return Plane(plane);
				case VfxValueType.Int: return "floatBitsToInt(" + Plane(plane) + ")";
				case VfxValueType.Uint: return "floatBitsToUint(" + Plane(plane) + ")";
				case VfxValueType.Bool: return "(" + Plane(plane) + " != 0.0)";
				case VfxValueType.Vector2: return "vec2(" + Plane(plane) + ", " + Plane(plane + 1) + ")";
				case VfxValueType.Vector3: return "vec3(" + Plane(plane) + ", " + Plane(plane + 1) + ", " + Plane(plane + 2) + ")";
				default: return "vec4(" + Plane(plane) + ", " + Plane(plane + 1) + ", " + Plane(plane + 2) + ", " + Plane(plane + 3) + ")";
			}
		}

		private static string Store(VfxValueType type, int plane)
		{
			switch (type)
			{
				case VfxValueType.Float: return Plane(plane) + " = v;";
				case VfxValueType.Int: return Plane(plane) + " = intBitsToFloat(v);";
				case VfxValueType.Uint: return Plane(plane) + " = uintBitsToFloat(v);";
				case VfxValueType.Bool: return Plane(plane) + " = v ? 1.0 : 0.0;";
				case VfxValueType.Vector2: return Plane(plane) + " = v.x; " + Plane(plane + 1) + " = v.y;";
				case VfxValueType.Vector3: return Plane(plane) + " = v.x; " + Plane(plane + 1) + " = v.y; " + Plane(plane + 2) + " = v.z;";
				default: return Plane(plane) + " = v.x; " + Plane(plane + 1) + " = v.y; " + Plane(plane + 2) + " = v.z; " + Plane(plane + 3) + " = v.w;";
			}
		}

		/// <summary>Attributes a kernel declares: those it reads or writes, plus the ones the system handles itself.</summary>
		private static List<string> UsedAttributes(VfxSystemDescription system, VfxGlslWriter writer, bool initialize)
		{
			var names = new HashSet<string>(StringComparer.Ordinal);
			names.UnionWith(writer.AttributesRead);
			names.UnionWith(writer.AttributesWritten);
			if (initialize)
			{
				names.Add(VfxAttributes.Seed.Name);
				names.Add(VfxAttributes.ParticleId.Name);
				names.Add(VfxAttributes.SpawnTime.Name);
				names.Add(VfxAttributes.Age.Name);
				names.Add(VfxAttributes.Position.Name);
				foreach (VfxStoredAttribute stored in system.Stored)
				{
					names.Add(stored.Attribute.Name);
				}
			}
			else if (writer.Kernel == VfxKernelKind.Update)
			{
				names.Add(VfxAttributes.Alive.Name);
				names.Add(VfxAttributes.Position.Name);
				if (system.UsesStrip)
				{
					names.Add(VfxAttributes.StripHead.Name);
					names.Add(VfxAttributes.StripCount.Name);
				}
			}

			var result = new List<string>(names);
			result.Sort(StringComparer.Ordinal);
			return result;
		}

		private static void LoadAttributes(StringBuilder code, VfxSystemDescription system, VfxGlslWriter writer, List<string> names)
		{
			foreach (string name in names)
			{
				VfxAttribute attribute;
				if (!system.Attributes.TryGetValue(name, out attribute))
				{
					continue;
				}

				VfxStoredAttribute stored = system.FindStored(name);
				string value = stored != null ? "VfxLoad_" + name + "(particleIndex)" : VfxGlsl.Literal(attribute.Default);
				code.Append('\t').Append(VfxValueTypes.Glsl(attribute.Type)).Append(' ').Append(attribute.Variable).Append(" = ").Append(value).Append(";\n");
			}
		}

		// ---------------------------------------------------------------- output geometry

		private static void EmitQuad(VfxSystemDescription system, VfxOutputDescription output, VfxGlslWriter writer)
		{
			string angle = writer.ReadAttribute(VfxAttributes.Angle);
			string pivot = writer.ReadAttribute(VfxAttributes.Pivot);
			switch (output.Primitive)
			{
				case VfxQuadPrimitive.Triangle:
					writer.Line("vec2 cornerUv = VfxTriangleCorner(corner);");
					break;
				case VfxQuadPrimitive.Octagon:
					writer.Line("vec2 cornerUv = VfxOctagonCorner(corner, " + (output.Locals.ContainsKey("Crop Factor") ? output.Locals["Crop Factor"] : "0.5") + ");");
					break;
				default:
					writer.Line("vec2 cornerUv = VfxQuadCorner4(corner);");
					break;
			}

			writer.Line("vec3 axisX; vec3 axisY; vec3 axisZ;");
			switch (output.Orient)
			{
				case VfxOrientMode.FaceCamera:
					writer.Line("axisZ = VfxSafeNormalize(vfxCameraPosition - worldCenter);");
					writer.Line("axisX = VfxSafeNormalize(cross(vfxCameraUp, axisZ));");
					writer.Line("if (dot(axisX, axisX) < 1e-6) axisX = vfxCameraRight;");
					writer.Line("axisY = cross(axisZ, axisX);");
					break;
				case VfxOrientMode.FixedAxis:
				{
					string up = output.Locals.ContainsKey("Up Axis") ? output.Locals["Up Axis"] : "vec3(0.0, 1.0, 0.0)";
					string right = output.Locals.ContainsKey("Right Axis") ? output.Locals["Right Axis"] : "vec3(1.0, 0.0, 0.0)";
					writer.Line("axisY = VfxSafeNormalize(" + Space(writer, up) + ");");
					writer.Line("axisX = VfxSafeNormalize(" + Space(writer, right) + " - axisY * dot(" + Space(writer, right) + ", axisY));");
					writer.Line("axisZ = cross(axisX, axisY);");
					break;
				}
				case VfxOrientMode.AlongVelocity:
				{
					string velocity = writer.ReadAttribute(VfxAttributes.Velocity);
					string stretch = output.Locals.ContainsKey("Velocity Scale") ? output.Locals["Velocity Scale"] : "0.0";
					writer.Line("vec3 worldVelocity = " + (writer.WorldSpace ? velocity : "mat3(vfxLocalToWorld) * " + velocity) + ";");
					writer.Line("axisY = VfxSafeNormalize(worldVelocity);");
					writer.Line("if (dot(axisY, axisY) < 1e-6) axisY = vfxCameraUp;");
					writer.Line("axisZ = VfxSafeNormalize(vfxCameraPosition - worldCenter);");
					writer.Line("axisX = VfxSafeNormalize(cross(axisY, axisZ));");
					writer.Line("axisZ = cross(axisX, axisY);");
					writer.Line("outScale.y += length(worldVelocity) * " + stretch + ";");
					break;
				}
				case VfxOrientMode.AxisAlignedBillboard:
				{
					string axis = output.Locals.ContainsKey("Axis") ? output.Locals["Axis"] : "vec3(0.0, 1.0, 0.0)";
					writer.Line("axisY = VfxSafeNormalize(" + Space(writer, axis) + ");");
					writer.Line("vec3 toCamera = vfxCameraPosition - worldCenter;");
					writer.Line("axisX = VfxSafeNormalize(cross(axisY, toCamera));");
					writer.Line("if (dot(axisX, axisX) < 1e-6) axisX = vfxCameraRight;");
					writer.Line("axisZ = cross(axisX, axisY);");
					break;
				}
				default:
					writer.Line("axisX = vfxCameraRight;");
					writer.Line("axisY = vfxCameraUp;");
					writer.Line("axisZ = -vfxCameraForward;");
					break;
			}

			writer.Line("float spin = radians(" + angle + ".z);");
			writer.Line("float cs = cos(spin); float sn = sin(spin);");
			writer.Line("vec2 local = (cornerUv - 0.5 - " + pivot + ".xy) * outScale.xy;");
			writer.Line("vec2 rotated = vec2(local.x * cs - local.y * sn, local.x * sn + local.y * cs);");
			writer.Line("vec3 worldPos = worldCenter + axisX * rotated.x + axisY * rotated.y + axisZ * (" + pivot + ".z * outScale.z);");
			if (output.NormalBending > 0f)
			{
				writer.Line("vec3 outNormal = VfxSafeNormalize(mix(axisZ, axisX * (cornerUv.x - 0.5) * 2.0 + axisY * (cornerUv.y - 0.5) * 2.0 + axisZ, " + VfxGlsl.Literal(output.NormalBending) + "));");
			}
			else
			{
				writer.Line("vec3 outNormal = axisZ;");
			}

			writer.Line("vec2 outUv = cornerUv;");
		}

		private static void EmitMesh(VfxSystemDescription system, VfxOutputDescription output, VfxGlslWriter writer)
		{
			string angle = writer.ReadAttribute(VfxAttributes.Angle);
			writer.Line("mat3 rotation = VfxEulerToMat3(" + angle + ");");
			writer.Line("vec3 localPos = rotation * (vPosition * outScale);");
			writer.Line("vec3 localNormal = rotation * (vNormal / max(outScale, vec3(1e-6)));");
			if (writer.WorldSpace)
			{
				writer.Line("vec3 worldPos = worldCenter + localPos;");
				writer.Line("vec3 outNormal = VfxSafeNormalize(localNormal);");
			}
			else
			{
				writer.Line("vec3 worldPos = worldCenter + mat3(vfxLocalToWorld) * localPos;");
				writer.Line("vec3 outNormal = VfxSafeNormalize(mat3(vfxLocalToWorld) * localNormal);");
			}

			writer.Line("vec2 outUv = vTexCoord;");
		}

		private static void EmitLine(VfxSystemDescription system, VfxOutputDescription output, VfxGlslWriter writer)
		{
			string mode = "Velocity";
			if (output.Locals.ContainsKey("Line Length"))
			{
				mode = output.LineMode;
			}

			if (mode == "Target")
			{
				string target = writer.ReadAttribute(VfxAttributes.TargetPosition);
				writer.Line("vec3 worldTarget = " + (writer.WorldSpace ? target : "(vfxLocalToWorld * vec4(" + target + ", 1.0)).xyz") + ";");
			}
			else
			{
				string velocity = writer.ReadAttribute(VfxAttributes.Velocity);
				string length = output.Locals.ContainsKey("Line Length") ? output.Locals["Line Length"] : "0.1";
				writer.Line("vec3 worldVelocity = " + (writer.WorldSpace ? velocity : "mat3(vfxLocalToWorld) * " + velocity) + ";");
				writer.Line("vec3 worldTarget = worldCenter + worldVelocity * " + length + ";");
			}

			writer.Line("vec3 worldPos = corner == 0u ? worldCenter : worldTarget;");
			writer.Line("vec3 outNormal = -vfxCameraForward;");
			writer.Line("vec2 outUv = vec2(corner == 0u ? 0.0 : 1.0, 0.5);");
		}

		/// <summary>
		///     A strip vertex: one of the two edges (corner) at one recorded position (point 0 is the particle, the head of
		///     its history). The direction along the strip comes from the neighbouring points, so consecutive segments
		///     share their vertices and join without gaps.
		/// </summary>
		private static void EmitStrip(VfxSystemDescription system, VfxOutputDescription output, VfxGlslWriter writer)
		{
			string head = writer.ReadAttribute(VfxAttributes.StripHead);
			string count = writer.ReadAttribute(VfxAttributes.StripCount);
			writer.Line("uint stripLast = " + count + " > 0u ? min(" + count + ", VFX_STRIP_POINTS) - 1u : 0u;");
			writer.Line("uint stripPoint = min(point, stripLast);");
			writer.Line("uint stripNewer = stripPoint > 0u ? stripPoint - 1u : 0u;");
			writer.Line("uint stripOlder = min(stripPoint + 1u, stripLast);");
			writer.Line("uint stripBase = particleIndex * VFX_STRIP_LENGTH;");
			writer.Line("vec3 h0 = vfxStrip[stripBase + ((" + head + " + VFX_STRIP_LENGTH - stripPoint) % VFX_STRIP_LENGTH)].xyz;");
			writer.Line("vec3 hNewer = vfxStrip[stripBase + ((" + head + " + VFX_STRIP_LENGTH - stripNewer) % VFX_STRIP_LENGTH)].xyz;");
			writer.Line("vec3 hOlder = vfxStrip[stripBase + ((" + head + " + VFX_STRIP_LENGTH - stripOlder) % VFX_STRIP_LENGTH)].xyz;");
			if (!writer.WorldSpace)
			{
				writer.Line("h0 = (vfxLocalToWorld * vec4(h0, 1.0)).xyz;");
				writer.Line("hNewer = (vfxLocalToWorld * vec4(hNewer, 1.0)).xyz;");
				writer.Line("hOlder = (vfxLocalToWorld * vec4(hOlder, 1.0)).xyz;");
			}

			writer.Line("vec3 tangent = VfxSafeNormalize(hNewer - hOlder);");
			writer.Line("if (dot(tangent, tangent) < 1e-6) tangent = vfxCameraUp;");
			writer.Line("vec3 side = VfxSafeNormalize(cross(tangent, VfxSafeNormalize(vfxCameraPosition - h0)));");
			writer.Line("float along = float(point) / float(VFX_STRIP_POINTS - 1u);");
			string widthCurve = output.Locals.ContainsKey("Width Over Strip") ? output.Locals["Width Over Strip"] : null;
			writer.Line("float w = outScale.x * 0.5" + (widthCurve != null ? " * " + widthCurve : "") + ";");
			writer.Line("vec3 worldPos = h0 + side * ((float(corner) * 2.0 - 1.0) * w);");
			writer.Line("vec3 outNormal = VfxSafeNormalize(vfxCameraPosition - h0);");
			writer.Line("vec2 outUv = vec2(float(corner), " + (output.StripTile ? "float(point)" : "along") + ");");
		}

		private static string Space(VfxGlslWriter writer, string direction)
		{
			return writer.WorldSpace ? direction : "mat3(vfxLocalToWorld) * " + direction;
		}

		// ---------------------------------------------------------------- fragment

		private static string AssembleFragment(VfxSystemDescription system, VfxOutputDescription output)
		{
			var code = new StringBuilder();
			code.Append("#version 430\n");
			code.Append("// Generated by LELEngine VFX for system '").Append(system.Name).Append("' output '").Append(output.Name).Append("'\n");
			code.Append("#define VFX_UNIFORM_COUNT ").Append(Math.Max(VfxUniformTable.HeaderVectors, system.Uniforms.VectorCount)).Append('\n');
			code.Append("#define VFX_MATRIX_COUNT ").Append(Math.Max(VfxUniformTable.HeaderMatrices, system.Uniforms.MatrixCount)).Append('\n');
			code.Append("#define VFX_CAPACITY ").Append(system.Capacity).Append("u\n");
			code.Append("#define VFX_STRIP_LENGTH 2u\n#define VFX_FRAGMENT 1\n#define VFX_DRAW 1\n");
			if (output.SoftParticles)
			{
				code.Append("#define VFX_USE_DEPTH 1\n");
			}

			code.Append("#include \"Engine/VFX/VfxCommon.glsl\"\n");
			bool lit = output.Lighting == VfxLightingMode.Lit;
			if (lit)
			{
				code.Append("#include \"Engine/Shadows.glsl\"\n#include \"Engine/VolumeLighting.glsl\"\n#include \"Engine/VFX/VfxLighting.glsl\"\n");
			}

			foreach (VfxResourceTable.TextureSlot texture in system.Resources.Textures)
			{
				if (!texture.Volume)
				{
					code.Append("uniform sampler2D ").Append(texture.Uniform).Append(";\n");
				}
			}

			code.Append("uniform vec4 vfxFragmentParams; // alpha threshold, roughness, 0, 0\n");
			code.Append("in VfxVaryings\n{\n\tvec3 worldPos;\n\tvec3 normal;\n\tvec2 uv;\n\tvec4 color;\n\tflat float texIndex;\n\tflat vec2 flipbook;\n\tflat float softDistance;\n\tvec4 clip;\n} v;\n");
			code.Append("out vec4 FragColor;\n");
			code.Append("\nvoid main()\n{\n");
			string mainTexture = output.Textures.ContainsKey("Main Texture") ? output.Textures["Main Texture"] : null;
			if (output.Kind == VfxOutputKind.Point)
			{
				code.Append("\tvec2 uv = gl_PointCoord;\n");
				code.Append("\tvec2 centered = uv - 0.5;\n\tif (dot(centered, centered) > 0.25) discard;\n");
			}
			else
			{
				code.Append("\tvec2 uv = v.uv;\n");
			}

			if (output.Kind == VfxOutputKind.Quad && output.Primitive == VfxQuadPrimitive.Triangle)
			{
				// The triangle covers more than the texture.
				code.Append("\tif (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) discard;\n");
			}

			code.Append("\tvec4 texel = vec4(1.0);\n");
			if (mainTexture != null)
			{
				switch (output.Uv)
				{
					case VfxUvMode.Flipbook:
						code.Append("\ttexel = texture(").Append(mainTexture).Append(", VfxFlipbookUv(uv, v.flipbook, floor(v.texIndex)));\n");
						break;
					case VfxUvMode.FlipbookBlend:
						code.Append("\tfloat frame = floor(v.texIndex);\n");
						code.Append("\tvec4 texelA = texture(").Append(mainTexture).Append(", VfxFlipbookUv(uv, v.flipbook, frame));\n");
						code.Append("\tvec4 texelB = texture(").Append(mainTexture).Append(", VfxFlipbookUv(uv, v.flipbook, frame + 1.0));\n");
						code.Append("\ttexel = mix(texelA, texelB, fract(v.texIndex));\n");
						break;
					default:
						code.Append("\ttexel = texture(").Append(mainTexture).Append(", uv);\n");
						break;
				}
			}

			code.Append("\tvec4 color = v.color * texel;\n");
			if (output.SoftParticles)
			{
				code.Append("\tcolor.a *= VfxSoftParticleFade(gl_FragCoord.xy, v.clip.w, v.softDistance);\n");
			}

			if (output.Blend == VfxBlendMode.AlphaClip)
			{
				code.Append("\tif (color.a < vfxFragmentParams.x) discard;\n");
			}
			else if (output.Blend != VfxBlendMode.Opaque)
			{
				code.Append("\tif (color.a <= 0.0005) discard;\n");
			}

			code.Append("#ifdef VFX_SHADOW_PASS\n\tFragColor = vec4(0.0);\n\treturn;\n#endif\n");
			if (lit)
			{
				code.Append("\tvec3 N = VfxSafeNormalize(v.normal);\n");
				if (output.TwoSidedLighting)
				{
					code.Append("\tif (!gl_FrontFacing) N = -N;\n");
				}

				code.Append("\tvec3 lit = VfxShade(color.rgb, N, v.worldPos, vfxFragmentParams.y);\n");
				code.Append("\tcolor.rgb = lit;\n");
			}

			switch (output.Blend)
			{
				case VfxBlendMode.Additive:
					code.Append("\tFragColor = vec4(color.rgb * color.a, 0.0);\n");
					break;
				case VfxBlendMode.Premultiplied:
					code.Append("\tFragColor = vec4(color.rgb * color.a, color.a);\n");
					break;
				case VfxBlendMode.Opaque:
				case VfxBlendMode.AlphaClip:
					code.Append("\tFragColor = vec4(color.rgb, 1.0);\n");
					break;
				default:
					code.Append("\tFragColor = color;\n");
					break;
			}

			code.Append("}\n");
			return code.ToString();
		}

		#endregion
	}
}
