using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Brutal.OpenGlApi;
using static Brutal.OpenGlApi.OpenGl;

namespace LELEngine.Graphics.OpenGL
{
	/// <summary>
	///     A program on OpenGL: the driver's program object, which holds the uniform values itself (the setters write
	///     them through; a draw uses what the object holds). Every sampler gets a texture unit of its own when the
	///     program is linked; the textures given for the samplers are bound to those units when a draw uses the program.
	/// </summary>
	internal sealed unsafe class GLProgram : GpuProgram
	{
		// GL_COMPLETION_STATUS_KHR / ARB (the same value).
		private const GetProgramParameterName CompletionStatus = (GetProgramParameterName)0x91B1;

		private static readonly Regex storageBlock = new Regex(@"((?:\b(?:readonly|writeonly|restrict|coherent|volatile)\s+)*)buffer\s+\w+\s*\{", RegexOptions.Compiled);

		private readonly GLDevice device;
		private readonly List<int> locations = new List<int>();
		private readonly List<int> slotUnits = new List<int>();
		private readonly Dictionary<string, int> slotsByName = new Dictionary<string, int>(StringComparer.Ordinal);
		private int[] shaders;
		private ShaderStage[] shaderStages;
		private bool finished;

		public int Handle { get; private set; }

		/// <summary>Texture units the program's samplers read (unit i of the arrays below).</summary>
		public int UnitCount { get; private set; }

		public GLTexture[] Textures = Array.Empty<GLTexture>();
		public GLSampler[] Samplers = Array.Empty<GLSampler>();

		/// <summary>The kind of texture each unit's sampler reads.</summary>
		public TextureTarget[] UnitTargets = Array.Empty<TextureTarget>();

		private int[] unitLocations = Array.Empty<int>();

		/// <summary>A stage writes images or storage blocks: what it wrote has to be made visible to what follows.</summary>
		public bool WritesStorage { get; private set; }

		public GLProgram(GLDevice device, ProgramDescriptor descriptor)
		{
			this.device = device;
			Name = descriptor.Name;
			OpenGLBackend gl = GLDevice.Gl;
			Handle = gl.CreateProgram();
			shaders = new int[descriptor.Stages.Count];
			shaderStages = new ShaderStage[descriptor.Stages.Count];
			for (int i = 0; i < descriptor.Stages.Count; i++)
			{
				ShaderStage stage = descriptor.Stages[i].Key;
				IsCompute |= stage == ShaderStage.Compute;
				int shader = gl.CreateShader(Type(stage));
				gl.ShaderSource(shader, descriptor.Stages[i].Value);
				gl.CompileShader(shader);
				gl.AttachShader(Handle, shader);
				shaders[i] = shader;
				shaderStages[i] = stage;
			}

			foreach (KeyValuePair<string, int> attribute in descriptor.AttributeLocations)
			{
				gl.BindAttribLocation(Handle, attribute.Value, attribute.Key);
			}

			if (device.Capabilities.ProgramBinaries)
			{
				gl.ProgramParameter(Handle, ProgramParameterName.ProgramBinaryRetrievableHint, 1);
			}

			gl.LinkProgram(Handle);
			WritesStorage = SourcesWriteStorage(descriptor);
			GLDevice.Label(0x82E2, Handle, Name);
		}

		private GLProgram(GLDevice device, ProgramDescriptor descriptor, int handle)
		{
			this.device = device;
			Name = descriptor.Name;
			Handle = handle;
			foreach (KeyValuePair<ShaderStage, string> stage in descriptor.Stages)
			{
				IsCompute |= stage.Key == ShaderStage.Compute;
			}

			WritesStorage = SourcesWriteStorage(descriptor);
			IsLinked = true;
			ReadUniforms();
			finished = true;
		}

		/// <summary>The driver's binary, with its format in the first four bytes; null when the driver refuses it.</summary>
		public static GLProgram FromBinary(GLDevice device, ProgramDescriptor descriptor, byte[] binary)
		{
			if (binary == null || binary.Length <= 4 || !device.Capabilities.ProgramBinaries)
			{
				return null;
			}

			OpenGLBackend gl = GLDevice.Gl;
			int handle = gl.CreateProgram();
			fixed (byte* data = binary)
			{
				gl.ProgramBinary(handle, (BinaryFormat)BitConverter.ToInt32(binary, 0), data + 4, binary.Length - 4);
			}

			gl.GetProgram(handle, GetProgramParameterName.LinkStatus, out int status);
			if (status == 0)
			{
				gl.DeleteProgram(handle);
				return null;
			}

			return new GLProgram(device, descriptor, handle);
		}

		public override bool IsReady
		{
			get
			{
				if (finished || !device.Capabilities.ParallelShaderCompile)
				{
					return true;
				}

				GLDevice.Gl.GetProgram(Handle, CompletionStatus, out int complete);
				return complete != 0;
			}
		}

		public override void Finish()
		{
			if (finished)
			{
				return;
			}

			OpenGLBackend gl = GLDevice.Gl;
			string warnings = "";
			for (int i = 0; i < shaders.Length; i++)
			{
				gl.GetShader(shaders[i], ShaderParameter.CompileStatus, out int compiled);
				string log = gl.GetShaderInfoLog(shaders[i]) ?? "";
				if (compiled == 0)
				{
					stageErrors.Add(new KeyValuePair<ShaderStage, string>(shaderStages[i], log));
				}
				else if (!string.IsNullOrWhiteSpace(log))
				{
					warnings += shaderStages[i] + ": " + log.TrimEnd() + "\n";
				}
			}

			gl.GetProgram(Handle, GetProgramParameterName.LinkStatus, out int status);
			string linkLog = gl.GetProgramInfoLog(Handle) ?? "";
			IsLinked = status != 0;
			Log = warnings + linkLog;
			foreach (int shader in shaders)
			{
				gl.DetachShader(Handle, shader);
				gl.DeleteShader(shader);
			}

			shaders = null;
			shaderStages = null;
			if (IsLinked)
			{
				ReadUniforms();
			}

			finished = true;
		}

		public override int GetUniformSlot(string name)
		{
			return slotsByName.TryGetValue(name, out int slot) ? slot : -1;
		}

		public override int GetAttributeLocation(string name)
		{
			return finished && IsLinked ? GLDevice.Gl.GetAttribLocation(Handle, name) : -1;
		}

		public override void SetFloats(int slot, float* values, int components, int count)
		{
			int location = Location(slot);
			if (location < 0)
			{
				return;
			}

			GlProgram program = new GlProgram((uint)Handle);
			switch (components)
			{
				case 1:
					Native.glProgramUniform1fv(program, location, count, values);
					break;
				case 2:
					Native.glProgramUniform2fv(program, location, count, values);
					break;
				case 3:
					Native.glProgramUniform3fv(program, location, count, values);
					break;
				default:
					Native.glProgramUniform4fv(program, location, count, values);
					break;
			}
		}

		public override void SetInts(int slot, int* values, int components, int count)
		{
			int location = Location(slot);
			if (location < 0)
			{
				return;
			}

			if (components == 1 && slot < slotUnits.Count && slotUnits[slot] >= 0)
			{
				// A sampler: it reads the texture given to it (SetTexture) on the unit the device keeps for it.
				return;
			}

			GlProgram program = new GlProgram((uint)Handle);
			switch (components)
			{
				case 1:
					Native.glProgramUniform1iv(program, location, count, values);
					break;
				case 2:
					Native.glProgramUniform2iv(program, location, count, values);
					break;
				case 3:
					Native.glProgramUniform3iv(program, location, count, values);
					break;
				default:
					Native.glProgramUniform4iv(program, location, count, values);
					break;
			}
		}

		public override void SetUInts(int slot, uint* values, int components, int count)
		{
			int location = Location(slot);
			if (location < 0)
			{
				return;
			}

			GlProgram program = new GlProgram((uint)Handle);
			switch (components)
			{
				case 1:
					Native.glProgramUniform1uiv(program, location, count, values);
					break;
				case 2:
					Native.glProgramUniform2uiv(program, location, count, values);
					break;
				case 3:
					Native.glProgramUniform3uiv(program, location, count, values);
					break;
				default:
					Native.glProgramUniform4uiv(program, location, count, values);
					break;
			}
		}

		public override void SetMatrices(int slot, float* values, int count)
		{
			int location = Location(slot);
			if (location >= 0)
			{
				Native.glProgramUniformMatrix4fv(new GlProgram((uint)Handle), location, count, false, values);
			}
		}

		public override void SetTexture(int slot, GpuTexture texture, GpuSampler sampler = null)
		{
			if (slot < 0 || slot >= slotUnits.Count)
			{
				return;
			}

			int unit = slotUnits[slot];
			if (unit >= 0)
			{
				Textures[unit] = (GLTexture)texture;
				Samplers[unit] = (GLSampler)sampler;
			}
		}

		public override byte[] GetBinary()
		{
			if (!finished || !IsLinked || !device.Capabilities.ProgramBinaries)
			{
				return null;
			}

			OpenGLBackend gl = GLDevice.Gl;
			gl.GetProgram(Handle, GetProgramParameterName.ProgramBinaryLength, out int size);
			if (size <= 0)
			{
				return null;
			}

			byte[] binary = new byte[size + 4];
			int length;
			BinaryFormat format;
			fixed (byte* data = binary)
			{
				gl.GetProgramBinary(Handle, size, out length, out format, data + 4);
			}

			if (length <= 0)
			{
				return null;
			}

			BitConverter.TryWriteBytes(new Span<byte>(binary, 0, 4), (int)format);
			if (length != size)
			{
				Array.Resize(ref binary, length + 4);
			}

			return binary;
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			IsDisposed = true;
			if (shaders != null && GLDevice.ContextOfThread != null)
			{
				foreach (int shader in shaders)
				{
					GLDevice.Gl.DeleteShader(shader);
				}

				shaders = null;
			}

			device.ReleaseProgram(Handle);
		}

		private int Location(int slot)
		{
			return slot >= 0 && slot < locations.Count ? locations[slot] : -1;
		}

		private void ReadUniforms()
		{
			OpenGLBackend gl = GLDevice.Gl;
			gl.GetProgram(Handle, GetProgramParameterName.ActiveUniforms, out int count);
			List<TextureTarget> targets = new List<TextureTarget>();
			List<int> samplerLocations = new List<int>();
			for (int i = 0; i < count; i++)
			{
				string name = gl.GetActiveUniform(Handle, i, out int size, out ActiveUniformType glType);
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}

				bool array = name.EndsWith("[0]", StringComparison.Ordinal);
				string baseName = array ? name.Substring(0, name.Length - 3) : name;
				int first = gl.GetUniformLocation(Handle, name);
				if (first < 0)
				{
					// A member of a uniform block: its values come from the block's buffer.
					continue;
				}

				UniformType type = Convert(glType);
				int slot = locations.Count;
				bool sampler = type >= UniformType.Sampler2D && type <= UniformType.UIntSampler2DArray;
				for (int element = 0; element < size; element++)
				{
					string elementName = array ? baseName + "[" + element + "]" : name;
					int location = element == 0 ? first : gl.GetUniformLocation(Handle, elementName);
					locations.Add(location);
					if (sampler && location >= 0)
					{
						int unit = targets.Count;
						targets.Add(Target(type));
						samplerLocations.Add(location);
						// Sampler i reads texture unit i (the device binds its texture there before a draw).
						int value = unit;
						Native.glProgramUniform1iv(new GlProgram((uint)Handle), location, 1, &value);
						slotUnits.Add(unit);
					}
					else
					{
						slotUnits.Add(-1);
					}

					slotsByName[elementName] = slot + element;
				}

				slotsByName[baseName] = slot;
				uniforms.Add(new UniformInfo { Name = baseName, Type = type, ArraySize = size, Slot = slot });
			}

			UnitCount = targets.Count;
			UnitTargets = targets.ToArray();
			unitLocations = samplerLocations.ToArray();
			Textures = new GLTexture[UnitCount];
			Samplers = new GLSampler[UnitCount];
			if (UnitCount > device.Capabilities.MaxSampledTextures)
			{
				Console.WriteLine($"[Graphics] Program '{Name}' samples {UnitCount} textures; this GPU gives a program {device.Capabilities.MaxSampledTextures}.");
			}
		}

		private static bool SourcesWriteStorage(ProgramDescriptor descriptor)
		{
			foreach (KeyValuePair<ShaderStage, string> stage in descriptor.Stages)
			{
				string source = stage.Value;
				if (source == null)
				{
					continue;
				}

				if (source.Contains("imageStore", StringComparison.Ordinal) || source.Contains("imageAtomic", StringComparison.Ordinal) || source.Contains("atomicCounter", StringComparison.Ordinal))
				{
					return true;
				}

				foreach (Match match in storageBlock.Matches(source))
				{
					if (!match.Groups[1].Value.Contains("readonly", StringComparison.Ordinal))
					{
						return true;
					}
				}
			}

			return false;
		}

		private static ShaderType Type(ShaderStage stage)
		{
			switch (stage)
			{
				case ShaderStage.Vertex: return ShaderType.VertexShader;
				case ShaderStage.Fragment: return ShaderType.FragmentShader;
				case ShaderStage.Geometry: return ShaderType.GeometryShader;
				case ShaderStage.Compute: return ShaderType.ComputeShader;
				case ShaderStage.TessControl: return ShaderType.TessControlShader;
				default: return ShaderType.TessEvaluationShader;
			}
		}

		private static TextureTarget Target(UniformType type)
		{
			switch (type)
			{
				case UniformType.Sampler3D:
				case UniformType.IntSampler3D:
				case UniformType.UIntSampler3D:
					return TextureTarget.Texture3D;
				case UniformType.SamplerCube:
				case UniformType.SamplerCubeShadow:
					return TextureTarget.TextureCubeMap;
				case UniformType.Sampler2DArray:
				case UniformType.Sampler2DArrayShadow:
				case UniformType.UIntSampler2DArray:
					return TextureTarget.Texture2DArray;
				default:
					return TextureTarget.Texture2D;
			}
		}

		private static UniformType Convert(ActiveUniformType type)
		{
			switch ((int)type)
			{
				case 0x1406: return UniformType.Float;
				case 0x8B50: return UniformType.Vector2;
				case 0x8B51: return UniformType.Vector3;
				case 0x8B52: return UniformType.Vector4;
				case 0x1404: return UniformType.Int;
				case 0x8B53: return UniformType.IntVector2;
				case 0x8B54: return UniformType.IntVector3;
				case 0x8B55: return UniformType.IntVector4;
				case 0x1405: return UniformType.UInt;
				case 0x8DC6: return UniformType.UIntVector2;
				case 0x8DC7: return UniformType.UIntVector3;
				case 0x8DC8: return UniformType.UIntVector4;
				case 0x8B56: return UniformType.Bool;
				case 0x8B57: return UniformType.BoolVector2;
				case 0x8B58: return UniformType.BoolVector3;
				case 0x8B59: return UniformType.BoolVector4;
				case 0x8B5A: return UniformType.Matrix2;
				case 0x8B5B: return UniformType.Matrix3;
				case 0x8B5C: return UniformType.Matrix4;
				case 0x8B5E: return UniformType.Sampler2D;
				case 0x8B5F: return UniformType.Sampler3D;
				case 0x8B60: return UniformType.SamplerCube;
				case 0x8B62: return UniformType.Sampler2DShadow;
				case 0x8DC1: return UniformType.Sampler2DArray;
				case 0x8DC4: return UniformType.Sampler2DArrayShadow;
				case 0x8DC5: return UniformType.SamplerCubeShadow;
				case 0x8DCA: return UniformType.IntSampler2D;
				case 0x8DCB: return UniformType.IntSampler3D;
				case 0x8DD2: return UniformType.UIntSampler2D;
				case 0x8DD3: return UniformType.UIntSampler3D;
				case 0x8DD7: return UniformType.UIntSampler2DArray;
				default:
					// GL_IMAGE_1D .. GL_UNSIGNED_INT_IMAGE_2D_MULTISAMPLE_ARRAY
					return (int)type >= 0x904C && (int)type <= 0x906C ? UniformType.Image : UniformType.Unknown;
			}
		}
	}
}
