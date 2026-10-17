using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>The GPU programs a system compiles to.</summary>
	public enum VfxKernelKind
	{
		/// <summary>Runs on the CPU (spawn contexts).</summary>
		Cpu,
		Initialize,
		Update,
		Output,

		/// <summary>Per-view sort keys of an output drawn back to front.</summary>
		SortKeys
	}

	/// <summary>
	///     The per-system uniform block: a fixed header of built-ins (time, camera, transforms) followed by the
	///     expressions hoisted to the CPU (<see cref="VfxScope.Uniform" />). Numbers live in a vec4 array (ints and
	///     uints as raw bits), matrices in a mat4 array. Uploaded once per system per frame.
	/// </summary>
	public sealed class VfxUniformTable
	{
		#region PublicFields

		public const int HeaderVectors = 8;
		public const int HeaderMatrices = 2;
		public const int MaxVectors = 240;
		public const int MaxMatrices = 16;

		// Header slots.
		public const int TimeSlot = 0; // deltaTime, totalTime, playRate, frameIndex (bits)
		public const int CountsSlot = 1; // spawnCount, spawnedTotal, systemSeed, capacity (bits)
		public const int CameraPositionSlot = 2;
		public const int CameraForwardSlot = 3;
		public const int CameraUpSlot = 4;
		public const int CameraRightSlot = 5;
		public const int SortSlot = 6; // sort reference position (xyz) and mode
		public const int ReservedSlot = 7;
		public const int LocalToWorldSlot = 0;
		public const int WorldToLocalSlot = 1;

		public sealed class Entry
		{
			public VfxExpression Expression;
			public int Slot;
			public VfxValueType Type;
		}

		public readonly List<Entry> Entries = new List<Entry>();
		public int VectorCount = HeaderVectors;
		public int MatrixCount = HeaderMatrices;

		#endregion

		#region PrivateFields

		private readonly Dictionary<VfxExpression, Entry> byExpression = new Dictionary<VfxExpression, Entry>();

		#endregion

		#region PublicMethods

		/// <summary>The GLSL that reads the (hoisted) expression from the block.</summary>
		public string Access(VfxExpression expression)
		{
			Entry entry;
			if (!byExpression.TryGetValue(expression, out entry))
			{
				entry = new Entry { Expression = expression, Type = expression.Type };
				if (expression.Type == VfxValueType.Matrix)
				{
					entry.Slot = MatrixCount++;
				}
				else
				{
					entry.Slot = VectorCount++;
				}

				Entries.Add(entry);
				byExpression[expression] = entry;
			}

			return AccessSlot(entry.Slot, entry.Type);
		}

		public static string AccessSlot(int slot, VfxValueType type)
		{
			string vector = "vfxU[" + slot.ToString(CultureInfo.InvariantCulture) + "]";
			switch (type)
			{
				case VfxValueType.Float: return vector + ".x";
				case VfxValueType.Int: return "floatBitsToInt(" + vector + ".x)";
				case VfxValueType.Uint: return "floatBitsToUint(" + vector + ".x)";
				case VfxValueType.Bool: return "(" + vector + ".x != 0.0)";
				case VfxValueType.Vector2: return vector + ".xy";
				case VfxValueType.Vector3: return vector + ".xyz";
				case VfxValueType.Matrix: return "vfxM[" + slot.ToString(CultureInfo.InvariantCulture) + "]";
				default: return vector;
			}
		}

		public bool IsFull => VectorCount >= MaxVectors || MatrixCount >= MaxMatrices;

		#endregion
	}

	/// <summary>Resources a system's programs bind: textures (sampler units), meshes (vertex ranges) and the curve data buffer.</summary>
	public sealed class VfxResourceTable
	{
		#region PublicFields

		public sealed class TextureSlot
		{
			public int Index;
			public VfxExpression Expression;
			public bool Volume;

			/// <summary>Unset: a soft disc texture instead of plain white (quad outputs).</summary>
			public bool SoftDefault;

			/// <summary>Unset: a stripe fading across u (strip outputs).</summary>
			public bool StripeDefault;
			public string Uniform => (Volume ? "vfxTex3D" : "vfxTex") + Index.ToString(CultureInfo.InvariantCulture);
		}

		public sealed class MeshSlot
		{
			public int Index;
			public VfxExpression Expression;
			private string infoUniform;

			/// <summary>The mesh's range uniform (base vertex, vertex count, first index, index count).</summary>
			public string InfoUniform => infoUniform ??= "vfxMeshInfo[" + Index.ToString(CultureInfo.InvariantCulture) + "]";
		}

		/// <summary>
		///     A gradient or curve in the curve buffer (vec4 entries), baked into a lookup table of <see cref="LutSamples" />
		///     samples: a gradient as RGBA over [0, 1], a curve as a header (first key time, 1 / key span) and four
		///     samples per entry over its keys' span. A constant's table, or a property's table baked per instance.
		/// </summary>
		public sealed class CurveSlot
		{
			public int Offset;
			public int Length;
			public VfxExpression Expression;
			public bool IsGradient;
		}

		public readonly List<TextureSlot> Textures = new List<TextureSlot>();
		public readonly List<MeshSlot> Meshes = new List<MeshSlot>();
		public readonly List<CurveSlot> Curves = new List<CurveSlot>();

		/// <summary>vec4 entries of the curve buffer.</summary>
		public int CurveBufferLength;

		/// <summary>Samples of a baked gradient or curve (sampling is two reads and a lerp, whatever the key count).</summary>
		public const int LutSamples = 128;

		/// <summary>Entries one gradient takes: a color per sample.</summary>
		public const int GradientEntries = LutSamples;

		/// <summary>Entries one curve takes: the header, then four samples per entry.</summary>
		public const int CurveEntries = 1 + LutSamples / 4;

		#endregion

		#region PublicMethods

		public TextureSlot Texture(VfxExpression expression, bool volume)
		{
			foreach (TextureSlot slot in Textures)
			{
				if (slot.Expression == expression || SameResource(slot.Expression, expression))
				{
					return slot;
				}
			}

			var created = new TextureSlot { Index = Textures.Count, Expression = expression, Volume = volume };
			Textures.Add(created);
			return created;
		}

		public MeshSlot Mesh(VfxExpression expression)
		{
			foreach (MeshSlot slot in Meshes)
			{
				if (slot.Expression == expression || SameResource(slot.Expression, expression))
				{
					return slot;
				}
			}

			var created = new MeshSlot { Index = Meshes.Count, Expression = expression };
			Meshes.Add(created);
			return created;
		}

		public CurveSlot Curve(VfxExpression expression, bool gradient)
		{
			foreach (CurveSlot slot in Curves)
			{
				if (slot.IsGradient == gradient && (slot.Expression == expression || SameResource(slot.Expression, expression)))
				{
					return slot;
				}
			}

			var created = new CurveSlot { Offset = CurveBufferLength, Expression = expression, IsGradient = gradient, Length = gradient ? GradientEntries : CurveEntries };
			CurveBufferLength += created.Length;
			Curves.Add(created);
			return created;
		}

		/// <summary>The curve buffer's content: every gradient and curve baked into its lookup table (values from <paramref name="resolve" />).</summary>
		public float[] Bake(Func<VfxExpression, VfxValue> resolve)
		{
			var data = new float[CurveBufferLength * 4];
			foreach (CurveSlot slot in Curves)
			{
				VfxValue value = slot.Expression != null ? resolve(slot.Expression) : null;
				int o = slot.Offset * 4;
				if (slot.IsGradient)
				{
					VfxGradient gradient = value?.Gradient ?? VfxGradient.Solid(Color4.White);
					for (int s = 0; s < LutSamples; s++)
					{
						Color4 color = gradient.Evaluate(s / (float)(LutSamples - 1));
						data[o + s * 4] = color.R;
						data[o + s * 4 + 1] = color.G;
						data[o + s * 4 + 2] = color.B;
						data[o + s * 4 + 3] = color.A;
					}
				}
				else
				{
					VfxCurve curve = value?.Curve ?? VfxCurve.Constant(1f);
					float start = 0f;
					float end = 1f;
					if (curve.Keys.Count > 0)
					{
						start = float.MaxValue;
						end = float.MinValue;
						foreach (VfxCurveKey key in curve.Keys)
						{
							start = Math.Min(start, key.Time);
							end = Math.Max(end, key.Time);
						}
					}

					float span = end - start;
					data[o] = start;
					data[o + 1] = span > 1e-6f ? 1f / span : 0f;
					for (int s = 0; s < LutSamples; s++)
					{
						data[o + 4 + s] = curve.Evaluate(start + span * s / (LutSamples - 1));
					}
				}
			}

			return data;
		}

		/// <summary>Two resource expressions name the same thing (the same property, or the same constant object).</summary>
		private static bool SameResource(VfxExpression a, VfxExpression b)
		{
			if (a.Kind == VfxExprKind.Property && b.Kind == VfxExprKind.Property)
			{
				return a.Name == b.Name;
			}

			if (a.Kind == VfxExprKind.Constant && b.Kind == VfxExprKind.Constant)
			{
				VfxValue x = a.Constant;
				VfxValue y = b.Constant;
				return x.Type == y.Type && x.ValueEquals(y);
			}

			return false;
		}

		#endregion
	}

	/// <summary>A GPU event a system raises from its update kernel (Trigger blocks) for a child system to spawn from.</summary>
	public sealed class VfxEventOutput
	{
		public string Name;
		public int Index;
	}

	/// <summary>
	///     Emits the GLSL of expressions and block statements of one kernel: constants fold, uniform-scope expressions
	///     hoist to the system's uniform table, everything else becomes temporaries. Records which attributes the
	///     kernel reads so the compiler can decide what to store.
	/// </summary>
	public sealed class VfxGlslWriter
	{
		#region PublicFields

		public readonly VfxKernelKind Kernel;
		public readonly VfxUniformTable Uniforms;
		public readonly VfxResourceTable Resources;
		public readonly StringBuilder Body = new StringBuilder();

		/// <summary>Statements that run before the blocks (aging, old position tracking).</summary>
		public readonly StringBuilder PreBody = new StringBuilder();

		/// <summary>Statements that run when the particle dies this frame (update kernels: trigger-on-die events).</summary>
		public readonly StringBuilder DeathBody = new StringBuilder();

		/// <summary>The system keeps its attributes in world space (otherwise in the effect's local space).</summary>
		public bool WorldSpace;

		/// <summary>Attributes read through expressions or blocks.</summary>
		public readonly HashSet<string> AttributesRead = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Attributes written by blocks.</summary>
		public readonly HashSet<string> AttributesWritten = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Custom attributes declared by this kernel's blocks.</summary>
		public readonly Dictionary<string, VfxAttribute> CustomAttributes = new Dictionary<string, VfxAttribute>(StringComparer.Ordinal);

		/// <summary>Parent attributes the kernel reads from the GPU event payload (child initialize).</summary>
		public readonly HashSet<string> SourceAttributesRead = new HashSet<string>(StringComparer.Ordinal);

		public readonly List<VfxEventOutput> Events = new List<VfxEventOutput>();
		public readonly List<string> Errors = new List<string>();
		public bool UsesNoise;
		public bool UsesSdf;
		public bool UsesDepth;
		public bool UsesMesh;
		public bool UsesCurves;
		public bool UsesRandom;

		/// <summary>Extra GLSL declared before main (custom functions of code blocks).</summary>
		public readonly StringBuilder Declarations = new StringBuilder();

		#endregion

		#region PrivateFields

		private readonly Dictionary<VfxExpression, string> emitted = new Dictionary<VfxExpression, string>();
		private readonly HashSet<string> declaredFunctions = new HashSet<string>(StringComparer.Ordinal);
		private int temps;
		private int indent = 1;
		private StringBuilder target;

		#endregion

		#region Constructors

		public VfxGlslWriter(VfxKernelKind kernel, VfxUniformTable uniforms, VfxResourceTable resources)
		{
			Kernel = kernel;
			Uniforms = uniforms;
			Resources = resources;
			target = Body;
		}

		#endregion

		#region PublicMethods

		/// <summary>Adds a statement to the kernel body.</summary>
		public void Line(string code)
		{
			target.Append('\t', indent).Append(code).Append('\n');
		}

		/// <summary>Sends the following lines to another section (<see cref="PreBody" />, <see cref="DeathBody" />); <see cref="EndSection" /> returns to the body.</summary>
		public void BeginSection(StringBuilder section)
		{
			target = section;
			indent = section == Body ? 1 : 2;
		}

		public void EndSection()
		{
			target = Body;
			indent = 1;
		}

		public void Comment(string text)
		{
			Line("// " + text.Replace('\n', ' '));
		}

		public void BeginBlock(string header)
		{
			Line(header);
			Line("{");
			indent++;
		}

		public void EndBlock()
		{
			indent = Math.Max(1, indent - 1);
			Line("}");
		}

		/// <summary>Declares a function once (custom code blocks, helper functions).</summary>
		public void DeclareOnce(string key, string code)
		{
			if (declaredFunctions.Add(key))
			{
				Declarations.Append(code).Append('\n');
			}
		}

		/// <summary>A new temporary holding the expression.</summary>
		public string Temp(VfxValueType type, string expression)
		{
			string name = "t" + (temps++).ToString(CultureInfo.InvariantCulture);
			Line(VfxValueTypes.Glsl(type) + " " + name + " = " + expression + ";");
			return name;
		}

		public string TempName()
		{
			return "t" + (temps++).ToString(CultureInfo.InvariantCulture);
		}

		/// <summary>The GLSL reading the attribute's local variable (recorded as read).</summary>
		public string ReadAttribute(VfxAttribute attribute)
		{
			AttributesRead.Add(attribute.Name);
			if (attribute.Custom)
			{
				CustomAttributes[attribute.Name] = attribute;
			}

			return attribute.Variable;
		}

		/// <summary>Writes the attribute's local variable (recorded as written).</summary>
		public void WriteAttribute(VfxAttribute attribute, string expression)
		{
			AttributesWritten.Add(attribute.Name);
			if (attribute.Custom)
			{
				CustomAttributes[attribute.Name] = attribute;
			}

			Line(attribute.Variable + " = " + expression + ";");
		}

		/// <summary>An expression as inline GLSL (temporaries, hoisted uniforms and literals as needed).</summary>
		public string Emit(VfxExpression expression)
		{
			if (expression == null)
			{
				return "0.0";
			}

			string cached;
			if (emitted.TryGetValue(expression, out cached))
			{
				return cached;
			}

			string result = EmitUncached(expression);
			emitted[expression] = result;
			return result;
		}

		/// <summary>Emits the expression converted to a type.</summary>
		public string Emit(VfxExpression expression, VfxValueType type)
		{
			return Emit(VfxExpression.ConvertTo(expression, type));
		}

		#endregion

		#region PrivateMethods

		private string EmitUncached(VfxExpression e)
		{
			// Constants fold on the CPU; uniform expressions hoist to the parameter block.
			if (VfxValueTypes.IsNumeric(e.Type) && !e.GpuOnly)
			{
				if (e.Scope == VfxScope.Constant && e.Kind != VfxExprKind.Constant)
				{
					try
					{
						return VfxGlsl.Literal(e.Evaluate(new VfxEvalContext()));
					}
					catch (Exception)
					{
						// Falls through to GPU emission.
					}
				}
				else if (e.Scope == VfxScope.Uniform && Kernel != VfxKernelKind.Cpu && e.Kind != VfxExprKind.Builtin)
				{
					if (!Uniforms.IsFull)
					{
						return Uniforms.Access(e);
					}
				}
			}

			switch (e.Kind)
			{
				case VfxExprKind.Constant:
					return VfxGlsl.Literal(e.Constant);
				case VfxExprKind.Property:
					// A numeric property that could not hoist (table full): read nothing sensible; resources are bound by slot.
					return VfxValueTypes.IsNumeric(e.Type) ? Uniforms.Access(e) : "0.0";
				case VfxExprKind.Builtin:
					return EmitBuiltin(e.Name);
				case VfxExprKind.Attribute:
					return ReadAttribute((VfxAttribute)e.Data);
				case VfxExprKind.SourceAttribute:
					SourceAttributesRead.Add(e.Name);
					return "src_" + e.Name;
				case VfxExprKind.Call:
					return EmitCall(e);
				case VfxExprKind.Swizzle:
					return "(" + Emit(e.Args[0]) + ")." + e.Name;
				case VfxExprKind.Construct:
					return EmitConstruct(e);
				case VfxExprKind.Convert:
					return EmitConvert(e);
				case VfxExprKind.Select:
					return Temp(e.Type, "(" + Emit(e.Args[0]) + " ? " + Emit(e.Args[1]) + " : " + Emit(e.Args[2]) + ")");
				case VfxExprKind.Random:
					return EmitRandom(e);
				case VfxExprKind.SampleGradient:
				{
					UsesCurves = true;
					VfxResourceTable.CurveSlot slot = Resources.Curve(e.Args[0], true);
					return Temp(VfxValueType.Color, "VfxSampleGradient(" + slot.Offset.ToString(CultureInfo.InvariantCulture) + "u, " + Emit(e.Args[1]) + ")");
				}
				case VfxExprKind.SampleCurve:
				{
					UsesCurves = true;
					VfxResourceTable.CurveSlot slot = Resources.Curve(e.Args[0], false);
					return Temp(VfxValueType.Float, "VfxSampleCurve(" + slot.Offset.ToString(CultureInfo.InvariantCulture) + "u, " + Emit(e.Args[1]) + ")");
				}
				case VfxExprKind.SampleTexture2D:
				{
					VfxResourceTable.TextureSlot slot = Resources.Texture(e.Args[0], false);
					string call = e.Args.Length > 2
						? "textureLod(" + slot.Uniform + ", " + Emit(e.Args[1]) + ", " + Emit(e.Args[2]) + ")"
						: "textureLod(" + slot.Uniform + ", " + Emit(e.Args[1]) + ", 0.0)";
					return Temp(VfxValueType.Color, call);
				}
				case VfxExprKind.SampleTexture3D:
				{
					VfxResourceTable.TextureSlot slot = Resources.Texture(e.Args[0], true);
					return Temp(VfxValueType.Color, "textureLod(" + slot.Uniform + ", " + Emit(e.Args[1]) + ", 0.0)");
				}
				case VfxExprKind.MeshVertex:
				{
					UsesMesh = true;
					VfxResourceTable.MeshSlot slot = Resources.Mesh(e.Args[0]);
					if (e.Name == "count")
					{
						return Temp(VfxValueType.Uint, "VfxMeshVertexCount(" + slot.Index.ToString(CultureInfo.InvariantCulture) + "u)");
					}

					string function = e.Name == "normal" ? "VfxMeshNormal" : e.Name == "uv" ? "VfxMeshUv" : "VfxMeshPosition";
					return Temp(e.Type, function + "(" + slot.Index.ToString(CultureInfo.InvariantCulture) + "u, " + Emit(e.Args[1]) + ")");
				}
				case VfxExprKind.Raw:
				{
					var args = new object[e.Args.Length];
					for (int i = 0; i < args.Length; i++)
					{
						args[i] = Emit(e.Args[i]);
					}

					string code = string.Format(CultureInfo.InvariantCulture, e.Name, args);
					return VfxValueTypes.Glsl(e.Type) != null ? Temp(e.Type, code) : code;
				}
				default:
					Errors.Add("Cannot emit " + e.Kind);
					return "0.0";
			}
		}

		private string EmitBuiltin(string builtin)
		{
			switch (builtin)
			{
				case VfxBuiltins.DeltaTime: return "vfxDeltaTime";
				case VfxBuiltins.TotalTime: return "vfxTotalTime";
				case VfxBuiltins.PlayRate: return "vfxPlayRate";
				case VfxBuiltins.FrameIndex: return "vfxFrameIndex";
				case VfxBuiltins.SpawnCount: return "spawnCount";
				case VfxBuiltins.SpawnedTotal: return "vfxSpawnedTotal";
				case VfxBuiltins.SystemSeed: return "vfxSystemSeed";
				case VfxBuiltins.Capacity: return "vfxCapacity";
				case VfxBuiltins.SpawnIndex: return Kernel == VfxKernelKind.Initialize ? "spawnIndex" : "0u";
				case VfxBuiltins.ParticleIndex: return "particleIndex";
				case VfxBuiltins.AliveCount: return Kernel == VfxKernelKind.Initialize || Kernel == VfxKernelKind.Update ? "VFX_COUNTER(VFX_C_ALIVE_IN)" : "0u";
				case VfxBuiltins.EventCount: return Kernel == VfxKernelKind.Initialize ? "spawnCount" : "0u";
				case VfxBuiltins.LocalToWorld: return "vfxLocalToWorld";
				case VfxBuiltins.WorldToLocal: return "vfxWorldToLocal";
				case VfxBuiltins.CameraPosition: return "vfxCameraPosition";
				case VfxBuiltins.CameraForward: return "vfxCameraForward";
				case VfxBuiltins.CameraUp: return "vfxCameraUp";
				case VfxBuiltins.CameraRight: return "vfxCameraRight";
				case VfxBuiltins.SpawnerTime:
				case VfxBuiltins.LoopProgress:
					return "0.0";
				default:
					Errors.Add("Unknown built-in " + builtin);
					return "0.0";
			}
		}

		private string EmitCall(VfxExpression e)
		{
			var intrinsic = (VfxIntrinsic)e.Data;
			if (intrinsic.Name == "perlin" || intrinsic.Name == "perlin3" || intrinsic.Name == "valueNoise" || intrinsic.Name == "curlNoise")
			{
				UsesNoise = true;
			}
			else if (intrinsic.Name == "sampleSdf" || intrinsic.Name == "sdfNormal")
			{
				UsesSdf = true;
			}

			var args = new object[e.Args.Length];
			for (int i = 0; i < args.Length; i++)
			{
				args[i] = Emit(e.Args[i]);
			}

			string code;
			try
			{
				code = string.Format(CultureInfo.InvariantCulture, intrinsic.Template, args);
			}
			catch (FormatException)
			{
				Errors.Add("The intrinsic " + intrinsic.Name + " expects more arguments");
				return "0.0";
			}

			return Temp(e.Type, code);
		}

		private string EmitConstruct(VfxExpression e)
		{
			int needed = VfxValueTypes.Components(e.Type);
			var parts = new List<string>();
			int have = 0;
			foreach (VfxExpression part in e.Args)
			{
				if (have >= needed)
				{
					break;
				}

				int count = Math.Max(1, VfxValueTypes.Components(part.Type));
				string code = Emit(VfxValueTypes.IsScalar(part.Type) ? VfxExpression.ConvertTo(part, VfxValueType.Float) : part);
				if (have + count > needed)
				{
					// Too many components: take the leading ones.
					string mask = "xyzw".Substring(0, needed - have);
					code = "(" + code + ")." + mask;
					count = needed - have;
				}

				parts.Add(code);
				have += count;
			}

			while (have < needed)
			{
				parts.Add(e.Type == VfxValueType.Color && have == 3 ? "1.0" : "0.0");
				have++;
			}

			if (needed == 1)
			{
				return parts.Count > 0 ? parts[0] : "0.0";
			}

			return Temp(e.Type, VfxValueTypes.Glsl(e.Type) + "(" + string.Join(", ", parts) + ")");
		}

		private string EmitConvert(VfxExpression e)
		{
			VfxExpression source = e.Args[0];
			string code = Emit(source);
			VfxValueType from = source.Type;
			VfxValueType to = e.Type;
			if (!VfxValueTypes.IsNumeric(from) || !VfxValueTypes.IsNumeric(to))
			{
				return code;
			}

			if (to == VfxValueType.Matrix || from == VfxValueType.Matrix)
			{
				return to == VfxValueType.Matrix ? "mat4(1.0)" : "0.0";
			}

			int fromCount = VfxValueTypes.Components(from);
			int toCount = VfxValueTypes.Components(to);
			string glsl = VfxValueTypes.Glsl(to);
			if (fromCount == 1)
			{
				string scalar = code;
				if (from == VfxValueType.Bool)
				{
					scalar = "(" + code + " ? 1.0 : 0.0)";
					if (to == VfxValueType.Int) return "int(" + scalar + ")";
					if (to == VfxValueType.Uint) return "uint(" + scalar + ")";
				}
				else if (to == VfxValueType.Bool)
				{
					return "(" + code + " != " + (from == VfxValueType.Int ? "0" : from == VfxValueType.Uint ? "0u" : "0.0") + ")";
				}

				if (toCount == 1)
				{
					return glsl + "(" + scalar + ")";
				}

				return to == VfxValueType.Color ? "vec4(vec3(" + scalar + "), 1.0)" : glsl + "(" + scalar + ")";
			}

			if (toCount == 1)
			{
				string x = "(" + code + ").x";
				switch (to)
				{
					case VfxValueType.Bool: return "(" + x + " != 0.0)";
					case VfxValueType.Int: return "int(" + x + ")";
					case VfxValueType.Uint: return "uint(" + x + ")";
					default: return x;
				}
			}

			if (toCount < fromCount)
			{
				return "(" + code + ")." + "xyzw".Substring(0, toCount);
			}

			if (toCount > fromCount)
			{
				string padding = to == VfxValueType.Color ? "1.0" : "0.0";
				if (toCount - fromCount == 1)
				{
					return glsl + "(" + code + ", " + padding + ")";
				}

				return glsl + "(" + code + ", " + padding + ", " + padding + ")";
			}

			return code;
		}

		private string EmitRandom(VfxExpression e)
		{
			UsesRandom = true;
			string salt = e.Salt.ToString(CultureInfo.InvariantCulture) + "u";
			string seed;
			switch (Kernel)
			{
				case VfxKernelKind.Update:
					seed = "(" + ReadAttribute(VfxAttributes.Seed) + " ^ (vfxFrameIndex * 0x9E3779B9u))";
					break;
				case VfxKernelKind.Initialize:
				case VfxKernelKind.Output:
				case VfxKernelKind.SortKeys:
					seed = ReadAttribute(VfxAttributes.Seed);
					break;
				default:
					seed = "vfxSystemSeed";
					break;
			}

			string min = Emit(e.Args[0]);
			string max = Emit(e.Args[1]);
			int components = VfxValueTypes.Components(e.Type);
			string random = components switch
			{
				2 => "VfxRandom2(" + seed + ", " + salt + ")",
				3 => "VfxRandom3(" + seed + ", " + salt + ")",
				4 => "VfxRandom4(" + seed + ", " + salt + ")",
				_ => "VfxRandom01(" + seed + ", " + salt + ")"
			};

			VfxValueType floatType = components == 1 ? VfxValueType.Float : e.Type == VfxValueType.Color ? VfxValueType.Color : VfxValueTypes.VectorOf(components);
			string mixed = "mix(" + Emit(VfxExpression.ConvertTo(e.Args[0], floatType)) + ", " + Emit(VfxExpression.ConvertTo(e.Args[1], floatType)) + ", " + random + ")";
			if (e.Type == VfxValueType.Int) return Temp(VfxValueType.Int, "int(floor(" + mixed + "))");
			if (e.Type == VfxValueType.Uint) return Temp(VfxValueType.Uint, "uint(floor(max(" + mixed + ", 0.0)))");
			if (e.Type == VfxValueType.Bool) return Temp(VfxValueType.Bool, "(" + random + " < " + Emit(VfxExpression.ConvertTo(e.Args[1], VfxValueType.Float)) + ")");
			_ = min;
			_ = max;
			return Temp(floatType, mixed);
		}

		#endregion
	}

	/// <summary>What an operator definition receives: the node, its resolved input expressions and the compile context.</summary>
	public sealed class VfxOperatorBuild
	{
		#region PublicFields

		public VfxNodeData Node;
		public VfxNodeDefinition Definition;
		public VfxCompiler Compiler;
		public List<VfxSlotDefinition> InputSlots;
		public VfxExpression[] Inputs;

		/// <summary>Salt for random nodes: stable per node.</summary>
		public uint Salt => (uint)(Node.Id ^ (Node.Id >> 32));

		#endregion

		#region PublicMethods

		public VfxExpression Input(string slotName)
		{
			for (int i = 0; i < InputSlots.Count; i++)
			{
				if (InputSlots[i].Name == slotName)
				{
					return Inputs[i];
				}
			}

			return VfxExpression.Float(0f);
		}

		public VfxExpression Input(int index)
		{
			return index < Inputs.Length ? Inputs[index] : VfxExpression.Float(0f);
		}

		public string Setting(string name, string fallback = null)
		{
			return Node.GetSetting(name, fallback ?? Definition.FindSetting(name)?.Default);
		}

		public bool Flag(string name)
		{
			return Node.GetBool(name, Definition.FindSetting(name)?.Default == "1");
		}

		public void Error(string message)
		{
			Compiler.Error(Node, message);
		}

		#endregion
	}

	/// <summary>
	///     What a block definition receives when it compiles into a kernel: its resolved inputs, the writer to emit
	///     statements into and attribute access with read / write tracking.
	/// </summary>
	public sealed class VfxBlockBuild
	{
		#region PublicFields

		public VfxNodeData Node;
		public VfxNodeDefinition Definition;
		public VfxCompiler Compiler;
		public VfxGlslWriter Writer;
		public VfxKernelKind Kernel;
		public List<VfxSlotDefinition> InputSlots;
		public VfxExpression[] Inputs;

		/// <summary>The spawner descriptor being built (spawn context blocks).</summary>
		public VfxSpawnerDescription Spawner;

		public uint Salt => (uint)(Node.Id ^ (Node.Id >> 32));
		public bool IsInitialize => Kernel == VfxKernelKind.Initialize;
		public bool IsUpdate => Kernel == VfxKernelKind.Update;
		public bool IsOutput => Kernel == VfxKernelKind.Output;

		#endregion

		#region PublicMethods

		public VfxExpression Input(string slotName)
		{
			for (int i = 0; i < InputSlots.Count; i++)
			{
				if (InputSlots[i].Name == slotName)
				{
					return Inputs[i];
				}
			}

			return VfxExpression.Float(0f);
		}

		public bool HasInput(string slotName)
		{
			foreach (VfxSlotDefinition slot in InputSlots)
			{
				if (slot.Name == slotName)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The input as inline GLSL of the slot's type.</summary>
		public string Emit(string slotName)
		{
			return Writer.Emit(Input(slotName));
		}

		public string Emit(VfxExpression expression)
		{
			return Writer.Emit(expression);
		}

		public string Emit(VfxExpression expression, VfxValueType type)
		{
			return Writer.Emit(expression, type);
		}

		public string Read(VfxAttribute attribute)
		{
			return Writer.ReadAttribute(attribute);
		}

		public void Write(VfxAttribute attribute, string expression)
		{
			Writer.WriteAttribute(attribute, expression);
		}

		public void Line(string code)
		{
			Writer.Line(code);
		}

		public string Temp(VfxValueType type, string expression)
		{
			return Writer.Temp(type, expression);
		}

		public string Setting(string name, string fallback = null)
		{
			return Node.GetSetting(name, fallback ?? Definition.FindSetting(name)?.Default);
		}

		public bool Flag(string name)
		{
			return Node.GetBool(name, Definition.FindSetting(name)?.Default == "1");
		}

		public int IntSetting(string name)
		{
			return Node.GetInt(name, int.TryParse(Definition.FindSetting(name)?.Default ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) ? d : 0);
		}

		public float FloatSetting(string name)
		{
			return Node.GetFloat(name, float.TryParse(Definition.FindSetting(name)?.Default ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out float d) ? d : 0f);
		}

		public void Error(string message)
		{
			Compiler.Error(Node, message);
		}

		/// <summary>A random number per particle for this block (a distinct salt per call).</summary>
		public VfxExpression Random(VfxValueType type, uint extraSalt = 0, VfxExpression min = null, VfxExpression max = null)
		{
			return VfxExpression.Random(VfxRandomMode.PerParticle, Salt + extraSalt * 7919u, type, min, max);
		}

		/// <summary>Registers a GPU event this kernel raises; returns the buffer index to append to.</summary>
		public int Event(string eventName)
		{
			return Compiler.RegisterEvent(Writer, eventName);
		}

		/// <summary>Appends an event with the particle's attributes as payload when the GLSL condition holds.</summary>
		public void TriggerEvent(string eventName, string condition, string count = "1u")
		{
			int index = Event(eventName);
			if (index < 0)
			{
				return;
			}

			// The payload: the parent's attributes a child can read as source attributes.
			string position = Read(VfxAttributes.Position);
			string velocity = Read(VfxAttributes.Velocity);
			string color = Read(VfxAttributes.Color);
			string alpha = Read(VfxAttributes.Alpha);
			string size = Read(VfxAttributes.Size);
			string lifetime = Read(VfxAttributes.Lifetime);
			string age = Read(VfxAttributes.Age);
			string seed = Read(VfxAttributes.Seed);
			string id = Read(VfxAttributes.ParticleId);
			Writer.BeginBlock("if (" + condition + ")");
			Writer.Line("VfxEmitEvent" + index.ToString(CultureInfo.InvariantCulture) + "(" + count + ", " + position + ", " + velocity + ", " + color + ", " + alpha + ", " + size + ", " + lifetime + ", " + age + ", " + seed + ", " + id + ");");
			Writer.EndBlock();
		}

		#endregion
	}
}
