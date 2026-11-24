using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A program on Vulkan. Each stage's GLSL (written as for OpenGL) is compiled to SPIR-V with shaderc, then the
	///     stages are linked here: every resource gets a binding of the program's one descriptor set (the same
	///     resource in two stages gets one), each stage's outputs the locations of the next stage's inputs of the same
	///     name, vertex inputs the locations the descriptor names. glslang gathers a stage's loose uniforms into a
	///     uniform block: the program keeps a copy of each such block in memory, the setters write into the copies, and
	///     a draw whose values changed copies them into the frame's uniform memory.
	/// </summary>
	internal sealed unsafe partial class VulkanProgram : GpuProgram
	{
		/// <summary>Counts the ways programs are compiled and kept: binaries of another number are refused.</summary>
		public const int BinaryVersion = 1;

		private const uint BinaryMagic = 0x564C454C;

		private static readonly Regex boolUniform = new Regex(@"\buniform\s+(?:bool|bvec[234])\s+(\w+)", RegexOptions.Compiled);

		/// <summary>
		///     LEL_VULKAN_SHADER_NOTE=text: a note every shader module carries, which a driver's own cache of compiled
		///     shaders does not know: every pipeline is compiled anew (to measure what a first run on a machine costs).
		/// </summary>
		private static readonly string shaderNote = Environment.GetEnvironmentVariable("LEL_VULKAN_SHADER_NOTE");

		[ThreadStatic]
		private static Compiler compiler;

		[ThreadStatic]
		private static bool compilerMade;

		private readonly VulkanDevice device;
		private readonly Task build;
		private readonly Dictionary<string, int> slotsByName = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly Dictionary<string, int> attributes = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly Dictionary<PipelineKey, PipelineEntry> pipelines = new Dictionary<PipelineKey, PipelineEntry>();
		private readonly List<VertexInput> vertexInputs = new List<VertexInput>();
		private Stage[] stages = Array.Empty<Stage>();
		private Binding[] bindings = Array.Empty<Binding>();
		private UniformEntry[] entries = Array.Empty<UniformEntry>();
		private Slot[] slots = Array.Empty<Slot>();
		private VkDescriptorSetLayout setLayout;
		private VkPipelineLayout pipelineLayout;
		private VkPipeline computePipeline;
		private int blockCount;

		// The descriptor set of the last draw, and what it was written for.
		private VkDescriptorSet currentSet;
		private long setSerial;
		private int setEpoch = -1;
		private int setTextureVersion = -1;
		private ulong[] key = Array.Empty<ulong>();

		/// <summary>The textures and samplers given for the program's samplers, by unit (a sampler array has a unit per element).</summary>
		public VulkanTexture[] Textures = Array.Empty<VulkanTexture>();

		public VulkanSampler[] Samplers = Array.Empty<VulkanSampler>();

		/// <summary>The sampler each texture had of its own (<see cref="GpuTexture.Sampler" />) when it was given, by unit.</summary>
		private GpuSampler[] ownSamplers = Array.Empty<GpuSampler>();

		/// <summary>Counts the changes of the textures and samplers given to the samplers.</summary>
		public int TextureVersion;

		/// <summary>A stage writes images or storage blocks: what it wrote has to be made visible to the draws that follow.</summary>
		public bool WritesStorage { get; private set; }

		/// <summary>The last submission that drew or dispatched with the program.</summary>
		public long LastUse;

		public VkPipelineLayout PipelineLayout => pipelineLayout;
		public VkPipeline ComputePipeline => computePipeline;

		/// <summary>Uniform blocks of loose uniforms: the dynamic offsets a bound descriptor set takes.</summary>
		public int BlockCount => blockCount;

		public VulkanProgram(VulkanDevice device, ProgramDescriptor descriptor, List<KeyValuePair<ShaderStage, byte[]>> compiled)
		{
			this.device = device;
			Name = descriptor.Name;
			foreach (KeyValuePair<ShaderStage, string> stage in descriptor.Stages)
			{
				IsCompute |= stage.Key == ShaderStage.Compute;
			}

			// The descriptor is the caller's to change once this returns.
			List<KeyValuePair<ShaderStage, string>> sources = new List<KeyValuePair<ShaderStage, string>>(descriptor.Stages);
			List<KeyValuePair<string, int>> locations = new List<KeyValuePair<string, int>>(descriptor.AttributeLocations);
			perDrawUniforms = descriptor.PerDrawUniforms.Count > 0 ? new HashSet<string>(descriptor.PerDrawUniforms, StringComparer.Ordinal) : null;
			if (descriptor.Asynchronous && !VulkanDevice.CompileOnCallingThread)
			{
				build = Task.Run(() => Build(sources, locations, compiled));
			}
			else
			{
				Build(sources, locations, compiled);
				build = Task.CompletedTask;
			}
		}

		public override bool IsReady => build.IsCompleted;

		/// <summary>The build has ended and the program is linked: draws and dispatches can use it.</summary>
		internal bool IsUsable => build.IsCompleted && IsLinked && !IsDisposed;

		public override void Finish()
		{
			build.Wait();
		}

		#region Building

		private void Build(List<KeyValuePair<ShaderStage, string>> sources, List<KeyValuePair<string, int>> locations, List<KeyValuePair<ShaderStage, byte[]>> compiled)
		{
			try
			{
				if (compiled == null)
				{
					compiled = new List<KeyValuePair<ShaderStage, byte[]>>();
					StringBuilder log = new StringBuilder();
					foreach (KeyValuePair<ShaderStage, string> source in sources)
					{
						byte[] spirv = Compile(source.Key, source.Value, Name, out string error);
						if (spirv == null)
						{
							stageErrors.Add(new KeyValuePair<ShaderStage, string>(source.Key, error));
							log.Append(source.Key).Append(": ").Append(error.TrimEnd()).Append('\n');
						}
						else
						{
							compiled.Add(new KeyValuePair<ShaderStage, byte[]>(source.Key, spirv));
						}
					}

					if (stageErrors.Count > 0)
					{
						Log = log.ToString();
						return;
					}
				}

				HashSet<string> bools = new HashSet<string>(StringComparer.Ordinal);
				foreach (KeyValuePair<ShaderStage, string> source in sources)
				{
					if (source.Value != null)
					{
						foreach (Match match in boolUniform.Matches(source.Value))
						{
							bools.Add(match.Groups[1].Value);
						}
					}
				}

				Link(compiled, locations, bools);
				IsLinked = true;
			}
			catch (Exception e)
			{
				Log += "Linking for Vulkan failed: " + e.Message;
				IsLinked = false;
			}
		}

		/// <summary>One stage's GLSL as SPIR-V for Vulkan; null with the compiler's messages when it does not compile.</summary>
		internal static byte[] Compile(ShaderStage stage, string source, string name, out string error)
		{
			if (!compilerMade)
			{
				compiler = ShaderC.CreateCompiler();
				compilerMade = true;
			}

			CompileOptions options = ShaderC.CreateCompilerOptions();
			try
			{
				options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
				// OpenGL's GLSL: loose uniforms, samplers without bindings, varyings without locations.
				options.SetVulkanRulesRelaxed(true);
				options.SetAutoBindUniforms(true);
				options.SetAutoMapLocations(true);
				// Names are what the stages are linked by: nothing may strip them.
				options.SetOptimizationLevel(OptimizationLevel.Zero);
				// gl_BaseInstance and gl_DrawID are of GLSL 4.60 whatever version a shader names.
				options.SetForcedVersionProfile(460, Profile.Core);
				options.AddMacroDefinition("LEL_VULKAN"u8, "1"u8);
				options.AddMacroDefinition("gl_VertexID"u8, "gl_VertexIndex"u8);
				// Vulkan's instance index counts from the draw's first instance.
				options.AddMacroDefinition("gl_InstanceID"u8, "(gl_InstanceIndex - gl_BaseInstance)"u8);
				if (stage == ShaderStage.Fragment)
				{
					// With the engine's row order a point's coordinate runs the other way than OpenGL's.
					options.AddMacroDefinition("gl_PointCoord"u8, "vec2(gl_PointCoord.x, 1.0 - gl_PointCoord.y)"u8);
				}

				ShaderKind kind;
				switch (stage)
				{
					case ShaderStage.Vertex: kind = ShaderKind.VertexShader; break;
					case ShaderStage.Fragment: kind = ShaderKind.FragmentShader; break;
					case ShaderStage.Geometry: kind = ShaderKind.GeometryShader; break;
					case ShaderStage.Compute: kind = ShaderKind.ComputeShader; break;
					case ShaderStage.TessControl: kind = ShaderKind.TessControlShader; break;
					default: kind = ShaderKind.TessEvaluationShader; break;
				}

				byte[] text = Encoding.UTF8.GetBytes(source ?? "");
				byte[] file = Encoding.UTF8.GetBytes(string.IsNullOrEmpty(name) ? "shader" : name);
				try
				{
					CompilationResult result = compiler.Compile(text, kind, file, "main"u8, options);
					try
					{
						int length = (int)result.GetLength().Value;
						byte[] spirv = new byte[length];
						Marshal.Copy((IntPtr)result.GetBytes(), spirv, 0, length);
						error = null;
						return spirv;
					}
					finally
					{
						result.Dispose();
					}
				}
				catch (ShaderException e)
				{
					error = e.Message;
					return null;
				}
			}
			finally
			{
				options.Dispose();
			}
		}

		private void Link(List<KeyValuePair<ShaderStage, byte[]>> compiled, List<KeyValuePair<string, int>> locations, HashSet<string> bools)
		{
			compiled.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
			int count = compiled.Count;
			SpirvModule[] modules = new SpirvModule[count];
			Stage[] made = new Stage[count];
			for (int i = 0; i < count; i++)
			{
				modules[i] = SpirvModule.Parse(compiled[i].Value);
				made[i] = new Stage { Kind = compiled[i].Key, Unlinked = compiled[i].Value, Flag = Flag(compiled[i].Key) };
			}

			// Loose uniforms a draw reads from push constants (VulkanProgramPush.cs): out of the stages' blocks, which
			// lose their binding when nothing else of them is read.
			SpirvResource[] defaultBlocks = MoveToPushConstants(modules, made);

			// Bindings: the blocks of loose uniforms first (their dynamic offsets come in this order), then every
			// other resource a stage uses, one binding per name and kind over all stages.
			List<Binding> layout = new List<Binding>();
			List<KeyValuePair<SpirvModule, SpirvResource>> unused = new List<KeyValuePair<SpirvModule, SpirvResource>>();
			for (int i = 0; i < count; i++)
			{
				foreach (SpirvResource resource in modules[i].Resources)
				{
					if (!resource.IsDefaultUniformBlock)
					{
						continue;
					}

					if (!resource.Used || resource.BlockSize <= 0)
					{
						unused.Add(new KeyValuePair<SpirvModule, SpirvResource>(modules[i], resource));
						continue;
					}

					made[i].BlockBinding = layout.Count;
					made[i].BlockSize = resource.BlockSize;
					made[i].Block = (byte*)NativeMemory.AllocZeroed((nuint)resource.BlockSize);
					made[i].Dirty = true;
					made[i].Resource = resource;
					modules[i].SetBinding(resource.VariableId, 0, layout.Count);
					layout.Add(new Binding { Kind = BindingKind.Block, Count = 1, Stages = made[i].Flag, StageIndex = i, BlockSize = resource.BlockSize });
				}
			}

			blockCount = layout.Count;
			Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.Ordinal);
			int units = 0;
			bool writes = false;
			for (int i = 0; i < count; i++)
			{
				foreach (SpirvResource resource in modules[i].Resources)
				{
					if (resource.IsDefaultUniformBlock)
					{
						continue;
					}

					if (!resource.Used)
					{
						unused.Add(new KeyValuePair<SpirvModule, SpirvResource>(modules[i], resource));
						continue;
					}

					BindingKind kind;
					switch (resource.Kind)
					{
						case SpirvResourceKind.UniformBuffer:
							kind = BindingKind.UniformBuffer;
							break;
						case SpirvResourceKind.StorageBuffer:
							kind = BindingKind.StorageBuffer;
							writes |= !resource.NonWritable;
							break;
						case SpirvResourceKind.CombinedImageSampler when resource.Dimension != SpirvImageDimension.Buffer:
							kind = BindingKind.Texture;
							break;
						case SpirvResourceKind.StorageImage when resource.Dimension != SpirvImageDimension.Buffer && resource.Dimension != SpirvImageDimension.SubpassData:
							kind = BindingKind.Image;
							writes |= !resource.NonWritable;
							break;
						case SpirvResourceKind.AccelerationStructure when device.HasRayQuery:
							kind = BindingKind.AccelerationStructure;
							break;
						default:
							throw new NotSupportedException($"'{resource.Name}' ({resource.Kind}, {resource.Dimension}) is a kind of shader resource the device does not bind.");
					}

					if (resource.IsRuntimeArray && kind != BindingKind.StorageBuffer)
					{
						throw new NotSupportedException($"'{resource.Name}' is an array of resources without a size.");
					}

					string name = (int)kind + ":" + resource.Name;
					if (!byName.TryGetValue(name, out int index))
					{
						index = layout.Count;
						byName.Add(name, index);
						Binding binding = new Binding
						{
							Kind = kind,
							Name = resource.Name,
							Count = Math.Max(1, resource.ArrayLength),
							IsArray = resource.ArrayLength > 0,
							Number = Math.Max(0, resource.Binding),
							BlockSize = resource.BlockSize,
							Dimension = resource.Dimension,
							Arrayed = resource.Arrayed,
							Shadow = resource.Shadow,
							SampledType = resource.SampledType,
							ImageFormat = resource.ImageFormat,
							Expected = Expected(resource),
							Writable = (kind == BindingKind.StorageBuffer || kind == BindingKind.Image) && !resource.NonWritable
						};
						if (kind == BindingKind.Texture)
						{
							binding.Unit = units;
							units += binding.Count;
						}

						layout.Add(binding);
					}

					Binding merged = layout[index];
					merged.Stages |= made[i].Flag;
					merged.BlockSize = Math.Max(merged.BlockSize, resource.BlockSize);
					merged.Writable |= (kind == BindingKind.StorageBuffer || kind == BindingKind.Image) && !resource.NonWritable;
					layout[index] = merged;
					modules[i].SetBinding(resource.VariableId, 0, index);
				}
			}

			// What no code uses keeps out of the way of what is bound: numbers past the layout.
			int parked = layout.Count;
			foreach (KeyValuePair<SpirvModule, SpirvResource> pair in unused)
			{
				pair.Key.SetBinding(pair.Value.VariableId, 0, parked++);
			}

			WritesStorage = writes;
			LinkInterfaces(modules, made, locations);

			// The uniforms: the leaves of the blocks of loose uniforms by OpenGL's names, one entry per name over all
			// stages, then the samplers and the images.
			List<UniformEntry> uniformEntries = new List<UniformEntry>();
			List<Slot> uniformSlots = new List<Slot>();
			Dictionary<string, int> entryByName = new Dictionary<string, int>(StringComparer.Ordinal);
			for (int i = 0; i < count; i++)
			{
				if (defaultBlocks[i] == null)
				{
					continue;
				}

				foreach (SpirvBlockMember member in defaultBlocks[i].FlattenMembers())
				{
					SpirvBaseType type = member.Type.BaseType;
					if (member.Name.Contains("/*", StringComparison.Ordinal) || (type != SpirvBaseType.Float && type != SpirvBaseType.Int && type != SpirvBaseType.UInt && type != SpirvBaseType.Bool))
					{
						continue;
					}

					if (!entryByName.TryGetValue(member.Name, out int index))
					{
						index = uniformEntries.Count;
						entryByName.Add(member.Name, index);
						bool isBool = type == SpirvBaseType.Bool || bools.Contains(member.Name);
						UniformEntry entry = new UniformEntry
						{
							Type = type == SpirvBaseType.Bool ? SpirvBaseType.UInt : type,
							IsBool = isBool,
							Components = Math.Max(1, member.Type.VectorSize),
							Columns = Math.Max(1, member.Type.Columns),
							Length = Math.Max(1, member.ArrayLength),
							FirstSlot = uniformSlots.Count,
							Targets = new List<UniformTarget>()
						};
						uniformEntries.Add(entry);
						AddSlots(uniformSlots, member.Name, member.ArrayLength > 0, entry.Length, index, -1);
						uniforms.Add(new UniformInfo { Name = member.Name, Type = ValueType(entry), ArraySize = entry.Length, Slot = entry.FirstSlot });
					}

					int stride = member.ArrayStride > 0 ? member.ArrayStride : 0;
					int matrixStride = member.MatrixStride > 0 ? member.MatrixStride : 16;
					if (pushOffsets != null && pushOffsets.TryGetValue(member.Name, out int pushOffset))
					{
						// Read from the push constants, by every stage that reads it: one copy of its values.
						if (!uniformEntries[index].Pushed)
						{
							uniformEntries[index].Pushed = true;
							uniformEntries[index].Targets.Add(new UniformTarget { Stage = PushTarget, Offset = pushOffset, Stride = stride, MatrixStride = matrixStride });
						}
					}
					else if (made[i].Resource != null)
					{
						uniformEntries[index].Targets.Add(new UniformTarget { Stage = i, Offset = member.Offset, Stride = stride, MatrixStride = matrixStride });
					}
				}
			}

			foreach (Binding binding in layout)
			{
				if (binding.Kind == BindingKind.Texture)
				{
					int first = uniformSlots.Count;
					AddSlots(uniformSlots, binding.Name, binding.IsArray, binding.Count, -1, binding.Unit);
					uniforms.Add(new UniformInfo { Name = binding.Name, Type = SamplerType(binding), ArraySize = binding.Count, Slot = first });
				}
				else if (binding.Kind == BindingKind.Image)
				{
					// Listed as OpenGL lists them; an image is bound by its number (CommandList.SetImage), its slot takes nothing.
					int first = uniformSlots.Count;
					AddSlots(uniformSlots, binding.Name, binding.IsArray, binding.Count, -1, -1);
					uniforms.Add(new UniformInfo { Name = binding.Name, Type = UniformType.Image, ArraySize = binding.Count, Slot = first });
				}
			}

			// The device's objects.
			for (int i = 0; i < count; i++)
			{
				if (device.HasPipelineLibraries)
				{
					modules[i].AddProcessedNote(VulkanDevice.LibrariesNote);
				}

				if (shaderNote != null)
				{
					modules[i].AddProcessedNote(shaderNote);
				}

				byte[] linked = modules[i].ToBytes();
				fixed (byte* code = linked)
				{
					VkShaderModuleCreateInfo info = new VkShaderModuleCreateInfo { CodeSize = (Brutal.ByteSize64)(ulong)linked.Length, Code = (int*)code };
					VkShaderModule module;
					VulkanDevice.Check(device.Vk->vkCreateShaderModule.Invoke(device.Handle, &info, null, &module), "vkCreateShaderModule (" + Name + ", " + made[i].Kind + ")");
					made[i].Module = module;
				}
			}

			VkDescriptorSetLayoutBinding* layoutBindings = stackalloc VkDescriptorSetLayoutBinding[Math.Max(1, layout.Count)];
			for (int i = 0; i < layout.Count; i++)
			{
				layoutBindings[i] = new VkDescriptorSetLayoutBinding { Binding = i, DescriptorType = DescriptorType(layout[i].Kind), DescriptorCount = layout[i].Count, StageFlags = layout[i].Stages };
			}

			VkDescriptorSetLayoutCreateInfo layoutInfo = new VkDescriptorSetLayoutCreateInfo { BindingCount = layout.Count, Bindings = layoutBindings };
			VkDescriptorSetLayout descriptorLayout;
			VulkanDevice.Check(device.Vk->vkCreateDescriptorSetLayout.Invoke(device.Handle, &layoutInfo, null, &descriptorLayout), "vkCreateDescriptorSetLayout (" + Name + ")");
			VkPushConstantRange pushRange = new VkPushConstantRange { StageFlags = PushStages, Offset = (Brutal.ByteSize32)0u, Size = (Brutal.ByteSize32)(uint)PushSize };
			VkPipelineLayoutCreateInfo pipelineLayoutInfo = new VkPipelineLayoutCreateInfo
			{
				SetLayoutCount = 1,
				SetLayouts = &descriptorLayout,
				PushConstantRangeCount = PushSize > 0 ? 1 : 0,
				PushConstantRanges = &pushRange
			};
			VkPipelineLayout madeLayout;
			VulkanDevice.Check(device.Vk->vkCreatePipelineLayout.Invoke(device.Handle, &pipelineLayoutInfo, null, &madeLayout), "vkCreatePipelineLayout (" + Name + ")");

			stages = made;
			bindings = layout.ToArray();
			entries = uniformEntries.ToArray();
			slots = uniformSlots.ToArray();
			Textures = new VulkanTexture[units];
			Samplers = new VulkanSampler[units];
			ownSamplers = new GpuSampler[units];
			setLayout = descriptorLayout;
			pipelineLayout = madeLayout;
			int keyLength = 2;
			foreach (Binding binding in bindings)
			{
				keyLength += 3 * binding.Count;
			}

			key = new ulong[keyLength];
			if (IsCompute)
			{
				fixed (byte* entry = "main\0"u8)
				{
					VkComputePipelineCreateInfo info = new VkComputePipelineCreateInfo
					{
						Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = stages[0].Module, Name = entry },
						Layout = pipelineLayout
					};
					VkPipeline pipeline;
					VulkanDevice.Check(device.Vk->vkCreateComputePipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &pipeline), "vkCreateComputePipelines (" + Name + ")");
					device.PipelineMade();
					device.ReportShaderInfo(pipeline, VkShaderStageFlags.ComputeBit, Name, "");
					computePipeline = pipeline;
				}
			}
			else if (device.HasPipelineLibraries)
			{
				// The shaders compiled now, on the thread that builds the program, not at its first draw.
				MakeShaderLibraries();
			}
		}

		/// <summary>
		///     Gives the stages' inputs and outputs their locations: vertex inputs those the descriptor names, the others
		///     their own (the source's layout(location = N), or the compiler's in declaration order) where that is free,
		///     else the lowest free ones; between two stages the same location to an output and the input of its name.
		/// </summary>
		private void LinkInterfaces(SpirvModule[] modules, Stage[] made, List<KeyValuePair<string, int>> locations)
		{
			for (int i = 0; i < modules.Length; i++)
			{
				if (made[i].Kind != ShaderStage.Vertex)
				{
					continue;
				}

				HashSet<int> taken = new HashSet<int>();
				List<SpirvInterfaceVariable> own = new List<SpirvInterfaceVariable>();
				foreach (SpirvInterfaceVariable input in modules[i].Inputs)
				{
					int location = -1;
					foreach (KeyValuePair<string, int> named in locations)
					{
						if (named.Key == input.Name)
						{
							location = named.Value;
						}
					}

					if (location < 0)
					{
						own.Add(input);
						continue;
					}

					Place(modules[i], input, location, taken);
				}

				List<SpirvInterfaceVariable> moved = new List<SpirvInterfaceVariable>();
				foreach (SpirvInterfaceVariable input in own)
				{
					if (input.Location >= 0 && IsFree(taken, input.Location, Math.Max(1, input.LocationCount)))
					{
						Place(modules[i], input, input.Location, taken);
					}
					else
					{
						moved.Add(input);
					}
				}

				foreach (SpirvInterfaceVariable input in moved)
				{
					int location = 0;
					while (!IsFree(taken, location, Math.Max(1, input.LocationCount)))
					{
						location++;
					}

					Place(modules[i], input, location, taken);
				}
			}

			for (int i = 0; i + 1 < modules.Length; i++)
			{
				int next = 0;
				Dictionary<string, int> given = new Dictionary<string, int>(StringComparer.Ordinal);
				foreach (SpirvInterfaceVariable output in modules[i].Outputs)
				{
					modules[i].SetLocation(output.VariableId, next);
					given[output.Name] = next;
					next += Math.Max(1, output.LocationCount);
				}

				foreach (SpirvInterfaceVariable input in modules[i + 1].Inputs)
				{
					if (given.TryGetValue(input.Name, out int location))
					{
						modules[i + 1].SetLocation(input.VariableId, location);
					}
					else
					{
						// Nothing writes it: a location of its own, read as undefined.
						modules[i + 1].SetLocation(input.VariableId, next);
						next += Math.Max(1, input.LocationCount);
					}
				}
			}
		}

		private void Place(SpirvModule module, SpirvInterfaceVariable input, int location, HashSet<int> taken)
		{
			module.SetLocation(input.VariableId, location);
			int count = Math.Max(1, input.LocationCount);
			for (int i = 0; i < count; i++)
			{
				taken.Add(location + i);
			}

			if (input.Used)
			{
				attributes[input.Name] = location;
			}

			// A pipeline gives every input the stage declares an attribute, read or not.
			for (int i = 0; i < count; i++)
			{
				vertexInputs.Add(new VertexInput { Location = location + i, Type = input.Type.BaseType });
			}
		}

		private static bool IsFree(HashSet<int> taken, int location, int count)
		{
			for (int i = 0; i < count; i++)
			{
				if (taken.Contains(location + i))
				{
					return false;
				}
			}

			return true;
		}

		private void AddSlots(List<Slot> list, string name, bool array, int length, int entry, int unit)
		{
			slotsByName[name] = list.Count;
			for (int i = 0; i < length; i++)
			{
				if (array)
				{
					slotsByName[name + "[" + i + "]"] = list.Count;
				}

				list.Add(new Slot { Entry = entry, Element = i, Unit = unit >= 0 ? unit + i : -1 });
			}
		}

		private static int Order(ShaderStage stage)
		{
			switch (stage)
			{
				case ShaderStage.Vertex: return 0;
				case ShaderStage.TessControl: return 1;
				case ShaderStage.TessEvaluation: return 2;
				case ShaderStage.Geometry: return 3;
				case ShaderStage.Fragment: return 4;
				default: return 5;
			}
		}

		private static VkShaderStageFlags Flag(ShaderStage stage)
		{
			switch (stage)
			{
				case ShaderStage.Vertex: return VkShaderStageFlags.VertexBit;
				case ShaderStage.Fragment: return VkShaderStageFlags.FragmentBit;
				case ShaderStage.Geometry: return VkShaderStageFlags.GeometryBit;
				case ShaderStage.Compute: return VkShaderStageFlags.ComputeBit;
				case ShaderStage.TessControl: return VkShaderStageFlags.TessellationControlBit;
				default: return VkShaderStageFlags.TessellationEvaluationBit;
			}
		}

		private static VkDescriptorType DescriptorType(BindingKind kind)
		{
			switch (kind)
			{
				case BindingKind.Block: return VkDescriptorType.UniformBufferDynamic;
				case BindingKind.UniformBuffer: return VkDescriptorType.UniformBuffer;
				case BindingKind.StorageBuffer: return VkDescriptorType.StorageBuffer;
				case BindingKind.Texture: return VkDescriptorType.CombinedImageSampler;
				case BindingKind.AccelerationStructure: return VkDescriptorType.AccelerationStructureKHR;
				default: return VkDescriptorType.StorageImage;
			}
		}

		private static TextureKind Expected(SpirvResource resource)
		{
			switch (resource.Dimension)
			{
				case SpirvImageDimension.Dim3D: return TextureKind.Texture3D;
				case SpirvImageDimension.Cube: return TextureKind.Cube;
				default: return resource.Arrayed ? TextureKind.Texture2DArray : TextureKind.Texture2D;
			}
		}

		private static UniformType ValueType(UniformEntry entry)
		{
			if (entry.Columns > 1)
			{
				return entry.Columns == 4 ? UniformType.Matrix4 : entry.Columns == 3 ? UniformType.Matrix3 : UniformType.Matrix2;
			}

			UniformType scalar = entry.IsBool ? UniformType.Bool : entry.Type == SpirvBaseType.Float ? UniformType.Float : entry.Type == SpirvBaseType.Int ? UniformType.Int : UniformType.UInt;
			return scalar + (entry.Components - 1);
		}

		private static UniformType SamplerType(in Binding binding)
		{
			bool array = binding.Arrayed;
			if (binding.SampledType == SpirvBaseType.Int)
			{
				return binding.Dimension == SpirvImageDimension.Dim3D ? UniformType.IntSampler3D : UniformType.IntSampler2D;
			}

			if (binding.SampledType == SpirvBaseType.UInt)
			{
				return binding.Dimension == SpirvImageDimension.Dim3D ? UniformType.UIntSampler3D : array ? UniformType.UIntSampler2DArray : UniformType.UIntSampler2D;
			}

			switch (binding.Dimension)
			{
				case SpirvImageDimension.Dim3D: return UniformType.Sampler3D;
				case SpirvImageDimension.Cube: return binding.Shadow ? UniformType.SamplerCubeShadow : UniformType.SamplerCube;
				default:
					if (binding.Shadow)
					{
						return array ? UniformType.Sampler2DArrayShadow : UniformType.Sampler2DShadow;
					}

					return array ? UniformType.Sampler2DArray : UniformType.Sampler2D;
			}
		}

		#endregion

		#region Binaries

		public override byte[] GetBinary()
		{
			Finish();
			if (!IsLinked)
			{
				return null;
			}

			using MemoryStream stream = new MemoryStream();
			using BinaryWriter writer = new BinaryWriter(stream);
			writer.Write(BinaryMagic);
			writer.Write(BinaryVersion);
			writer.Write(stages.Length);
			foreach (Stage stage in stages)
			{
				writer.Write((int)stage.Kind);
				writer.Write(stage.Unlinked.Length);
				writer.Write(stage.Unlinked);
			}

			writer.Flush();
			return stream.ToArray();
		}

		/// <summary>The stages' SPIR-V out of a binary; null when it is not one of this version for the descriptor's stages.</summary>
		internal static List<KeyValuePair<ShaderStage, byte[]>> ReadBinary(ProgramDescriptor descriptor, byte[] binary)
		{
			if (binary == null || binary.Length < 12)
			{
				return null;
			}

			try
			{
				using BinaryReader reader = new BinaryReader(new MemoryStream(binary));
				if (reader.ReadUInt32() != BinaryMagic || reader.ReadInt32() != BinaryVersion)
				{
					return null;
				}

				int count = reader.ReadInt32();
				if (count != descriptor.Stages.Count || count <= 0)
				{
					return null;
				}

				List<KeyValuePair<ShaderStage, byte[]>> stages = new List<KeyValuePair<ShaderStage, byte[]>>();
				for (int i = 0; i < count; i++)
				{
					ShaderStage stage = (ShaderStage)reader.ReadInt32();
					int length = reader.ReadInt32();
					if (length <= 0 || length > binary.Length || length % 4 != 0)
					{
						return null;
					}

					byte[] spirv = reader.ReadBytes(length);
					if (spirv.Length != length || !descriptor.Stages.Exists(s => s.Key == stage))
					{
						return null;
					}

					stages.Add(new KeyValuePair<ShaderStage, byte[]>(stage, spirv));
				}

				return stages;
			}
			catch (EndOfStreamException)
			{
				return null;
			}
		}

		#endregion

		#region Uniforms

		public override int GetUniformSlot(string name)
		{
			return slotsByName.TryGetValue(name, out int slot) ? slot : -1;
		}

		public override int GetAttributeLocation(string name)
		{
			return attributes.TryGetValue(name, out int location) ? location : -1;
		}

		public override void SetFloats(int slot, float* values, int components, int count)
		{
			Write(slot, values, components, count, SpirvBaseType.Float);
		}

		public override void SetInts(int slot, int* values, int components, int count)
		{
			Write(slot, values, components, count, SpirvBaseType.Int);
		}

		public override void SetUInts(int slot, uint* values, int components, int count)
		{
			Write(slot, values, components, count, SpirvBaseType.UInt);
		}

		public override void SetMatrices(int slot, float* values, int count)
		{
			if (slot < 0 || slot >= slots.Length || slots[slot].Entry < 0)
			{
				return;
			}

			UniformEntry entry = entries[slots[slot].Entry];
			if (entry.Type != SpirvBaseType.Float || entry.Columns != 4 || entry.Components != 4)
			{
				return;
			}

			int first = slots[slot].Element;
			count = Math.Min(count, entry.Length - first);
			List<UniformTarget> targets = entry.Targets;
			for (int t = 0; t < targets.Count; t++)
			{
				UniformTarget target = targets[t];
				byte* block = target.Stage == PushTarget ? pushBlock : stages[target.Stage].Block;
				bool changed = false;
				for (int i = 0; i < count; i++)
				{
					byte* destination = block + target.Offset + (long)(first + i) * target.Stride;
					float* source = values + i * 16;
					if (target.MatrixStride == 16)
					{
						changed |= CopyChanged(source, destination, 64);
					}
					else
					{
						for (int column = 0; column < 4; column++)
						{
							changed |= CopyChanged(source + column * 4, destination + column * target.MatrixStride, 16);
						}
					}
				}

				MarkChanged(target.Stage, changed);
			}
		}

		/// <summary>Values of a stage's block (or of the push constants) changed: they reach the GPU with the next draw.</summary>
		private void MarkChanged(int stage, bool changed)
		{
			if (stage == PushTarget)
			{
				if (changed)
				{
					PushVersion++;
				}
			}
			else
			{
				stages[stage].Dirty |= changed;
			}
		}

		/// <summary>Copies bytes over others; true when they differed (the stage's block then reaches the GPU again).</summary>
		private static bool CopyChanged(void* source, void* destination, int bytes)
		{
			if (new ReadOnlySpan<byte>(source, bytes).SequenceEqual(new ReadOnlySpan<byte>(destination, bytes)))
			{
				return false;
			}

			Buffer.MemoryCopy(source, destination, bytes, bytes);
			return true;
		}

		public override void SetTexture(int slot, GpuTexture texture, GpuSampler sampler = null)
		{
			if (slot < 0 || slot >= slots.Length)
			{
				return;
			}

			int unit = slots[slot].Unit;
			if (unit < 0)
			{
				return;
			}

			// The same texture and sampler again (a material set for each of its draws) keeps the descriptor set; a
			// texture whose own sampler changed since it was given counts as new, as it did when every call counted.
			VulkanTexture given = (VulkanTexture)texture;
			VulkanSampler how = (VulkanSampler)sampler;
			GpuSampler own = given?.Sampler;
			if (Textures[unit] != given || Samplers[unit] != how || ownSamplers[unit] != own)
			{
				Textures[unit] = given;
				Samplers[unit] = how;
				ownSamplers[unit] = own;
				TextureVersion++;
			}
		}

		/// <summary>
		///     Writes values into every stage's copy of the uniform. Values of another type than the uniform's are
		///     ignored as OpenGL ignores them; a bool takes any type (it is an unsigned integer in the block).
		/// </summary>
		private void Write(int slot, void* values, int components, int count, SpirvBaseType given)
		{
			if (slot < 0 || slot >= slots.Length || slots[slot].Entry < 0)
			{
				return;
			}

			UniformEntry entry = entries[slots[slot].Entry];
			if (entry.Columns != 1 || entry.Components != components)
			{
				return;
			}

			bool convert = false;
			if (entry.Type != given)
			{
				// glslang leaves no trace of bool: integers given for an unsigned uniform are taken as they are.
				if (entry.Type == SpirvBaseType.UInt && given == SpirvBaseType.Int)
				{
					convert = false;
				}
				else if (entry.IsBool && given == SpirvBaseType.Float)
				{
					convert = true;
				}
				else
				{
					return;
				}
			}

			int first = slots[slot].Element;
			count = Math.Min(count, entry.Length - first);
			int bytes = components * 4;
			List<UniformTarget> targets = entry.Targets;
			for (int t = 0; t < targets.Count; t++)
			{
				UniformTarget target = targets[t];
				byte* block = target.Stage == PushTarget ? pushBlock : stages[target.Stage].Block;
				bool changed = false;
				for (int i = 0; i < count; i++)
				{
					byte* destination = block + target.Offset + (long)(first + i) * target.Stride;
					byte* source = (byte*)values + (long)i * bytes;
					if (convert)
					{
						for (int c = 0; c < components; c++)
						{
							uint value = ((float*)source)[c] != 0f ? 1u : 0u;
							changed |= ((uint*)destination)[c] != value;
							((uint*)destination)[c] = value;
						}
					}
					else
					{
						changed |= CopyChanged(source, destination, bytes);
					}
				}

				// A block whose values stay the same keeps its copy in the frame's memory: no new offset, no new binding.
				MarkChanged(target.Stage, changed);
			}
		}

		#endregion

		#region Descriptors

		/// <summary>
		///     The descriptor set for a draw or dispatch given now: the program's uniform values copied into the
		///     frame's uniform memory when they changed, and a set that binds exactly what is set (found among the
		///     frame's sets by all of it, or written). The offsets of the uniform blocks go into
		///     <paramref name="offsets" /> (<see cref="BlockCount" /> of them).
		/// </summary>
		internal VkDescriptorSet PrepareDescriptors(VulkanCommandList list, int* offsets)
		{
			long serial = device.RecordingSerial;
			LastUse = serial;
			bool moved = false;
			long alignment = Math.Max(16, (long)device.Limits.MinUniformBufferOffsetAlignment.Value);
			for (int i = 0; i < stages.Length; i++)
			{
				Stage stage = stages[i];
				if (stage.BlockBinding < 0)
				{
					continue;
				}

				if (stage.Dirty || stage.ArenaSerial != serial)
				{
					byte* memory = device.AllocateTransient(stage.BlockSize, alignment, out VkBuffer buffer, out long offset);
					Buffer.MemoryCopy(stage.Block, memory, stage.BlockSize, stage.BlockSize);
					moved |= buffer.VkHandle != stage.ArenaBuffer.VkHandle || stage.ArenaSerial != serial;
					stage.ArenaBuffer = buffer;
					stage.ArenaOffset = offset;
					stage.ArenaSerial = serial;
					stage.Dirty = false;
				}

				offsets[stage.BlockBinding] = (int)stage.ArenaOffset;
			}

			if (!moved && setSerial == serial && setEpoch == device.BindingEpoch && setTextureVersion == TextureVersion)
			{
				return currentSet;
			}

			// Everything the set binds, as numbers; what is bound is noted for the barriers of the program's commands.
			BeginTracking(list);
			int length = 0;
			key[length++] = (ulong)setLayout.VkHandle;
			for (int b = 0; b < bindings.Length; b++)
			{
				ref Binding binding = ref bindings[b];
				switch (binding.Kind)
				{
					case BindingKind.Block:
						key[length++] = (ulong)stages[binding.StageIndex].ArenaBuffer.VkHandle;
						break;
					case BindingKind.UniformBuffer:
					case BindingKind.StorageBuffer:
						list.ResolveBuffer(binding.Kind == BindingKind.StorageBuffer, binding.Number, binding.BlockSize, Name, binding.Name, out VkBuffer buffer, out long offset, out long range);
						key[length++] = (ulong)buffer.VkHandle;
						key[length++] = (ulong)offset;
						key[length++] = (ulong)range;
						break;
					case BindingKind.Texture:
						for (int i = 0; i < binding.Count; i++)
						{
							ResolveTexture(binding, binding.Unit + i, out VkImageView view, out VkSampler sampler);
							key[length++] = (ulong)view.VkHandle;
							key[length++] = (ulong)sampler.VkHandle;
						}

						break;
					case BindingKind.AccelerationStructure:
						for (int i = 0; i < binding.Count; i++)
						{
							key[length++] = (ulong)list.ResolveAccelerationStructure(binding.Number + i).VkHandle;
						}

						break;
					default:
						for (int i = 0; i < binding.Count; i++)
						{
							key[length++] = (ulong)list.ResolveImage(binding.Number + i, binding.Expected, binding.SampledType, binding.ImageFormat, Name, binding.Name).VkHandle;
						}

						break;
				}
			}

			list.Resolving = null;
			DescriptorKey lookup = new DescriptorKey(key, length);
			if (!device.DescriptorCache.TryGetValue(lookup, out VkDescriptorSet set))
			{
				set = WriteSet(length);
				NameHandles(device.NamedHandles);
				device.CacheDescriptorSet(lookup.Copy(), set);
			}

			currentSet = set;
			setSerial = serial;
			setEpoch = device.BindingEpoch;
			setTextureVersion = TextureVersion;
			return set;
		}

		/// <summary>The view and sampler a sampler of the program reads: the texture given for it, or a stand-in of the kind the shader expects.</summary>
		private void ResolveTexture(in Binding binding, int unit, out VkImageView view, out VkSampler sampler)
		{
			VulkanTexture texture = Textures[unit];
			bool integer = binding.SampledType != SpirvBaseType.Float;
			if (texture == null || texture.IsDisposed || texture.Kind != binding.Expected || texture.Format.IsInteger() != integer || (binding.Shadow && !texture.Format.IsDepth()))
			{
				texture = device.DummyTexture(binding.Expected, binding.SampledType, binding.Shadow);
			}

			view = device.CommandList.SampledView(this, texture, unit);
			VulkanSampler chosen = Samplers[unit] ?? (VulkanSampler)texture.Sampler ?? (VulkanSampler)LELEngine.Graphics.Samplers.LinearClamp;
			if (!texture.Filters)
			{
				chosen = chosen.Nearest;
			}

			sampler = chosen.Handle;
		}

		/// <summary>
		///     The objects the key just built names, whose destruction the cached set has to be forgotten with: the set
		///     layout, the buffers bound by number, the views of textures and images, the acceleration structures (not the
		///     frame's uniform memory, offsets, ranges or samplers).
		/// </summary>
		private void NameHandles(List<nint> named)
		{
			named.Clear();
			named.Add(setLayout.VkHandle);
			int position = 1;
			foreach (Binding binding in bindings)
			{
				switch (binding.Kind)
				{
					case BindingKind.Block:
						position++;
						break;
					case BindingKind.UniformBuffer:
					case BindingKind.StorageBuffer:
						named.Add((nint)key[position]);
						position += 3;
						break;
					case BindingKind.Texture:
						for (int i = 0; i < binding.Count; i++)
						{
							named.Add((nint)key[position]);
							position += 2;
						}

						break;
					default:
						for (int i = 0; i < binding.Count; i++)
						{
							named.Add((nint)key[position++]);
						}

						break;
				}
			}
		}

		/// <summary>A new descriptor set written from the key just built.</summary>
		private VkDescriptorSet WriteSet(int length)
		{
			VkDescriptorSet set = device.AllocateDescriptorSet(setLayout);
			int descriptors = 0;
			foreach (Binding binding in bindings)
			{
				descriptors += binding.Count;
			}

			if (descriptors == 0)
			{
				return set;
			}

			VkWriteDescriptorSet* writes = stackalloc VkWriteDescriptorSet[bindings.Length];
			VkDescriptorImageInfo* images = stackalloc VkDescriptorImageInfo[descriptors];
			VkDescriptorBufferInfo* buffers = stackalloc VkDescriptorBufferInfo[descriptors];
			VkWriteDescriptorSetAccelerationStructureKHR* structureWrites = stackalloc VkWriteDescriptorSetAccelerationStructureKHR[bindings.Length];
			VkAccelerationStructureKHR* structures = stackalloc VkAccelerationStructureKHR[descriptors];
			int position = 1;
			int image = 0;
			int buffer = 0;
			int structure = 0;
			for (int b = 0; b < bindings.Length; b++)
			{
				ref Binding binding = ref bindings[b];
				VkWriteDescriptorSet write = new VkWriteDescriptorSet { DstSet = set, DstBinding = b, DescriptorCount = binding.Count, DescriptorType = DescriptorType(binding.Kind) };
				switch (binding.Kind)
				{
					case BindingKind.Block:
						buffers[buffer] = new VkDescriptorBufferInfo { Buffer = new VkBuffer((nint)key[position++]), Range = (Brutal.ByteSize64)(ulong)binding.BlockSize };
						write.BufferInfo = &buffers[buffer++];
						break;
					case BindingKind.UniformBuffer:
					case BindingKind.StorageBuffer:
						buffers[buffer] = new VkDescriptorBufferInfo
						{
							Buffer = new VkBuffer((nint)key[position]),
							Offset = (Brutal.ByteSize64)key[position + 1],
							Range = (Brutal.ByteSize64)key[position + 2]
						};
						position += 3;
						write.BufferInfo = &buffers[buffer++];
						break;
					case BindingKind.Texture:
						write.ImageInfo = &images[image];
						for (int i = 0; i < binding.Count; i++)
						{
							images[image++] = new VkDescriptorImageInfo
							{
								ImageView = new VkImageView((nint)key[position]),
								Sampler = new VkSampler((nint)key[position + 1]),
								ImageLayout = textureLayouts[binding.Unit + i]
							};
							position += 2;
						}

						break;
					case BindingKind.AccelerationStructure:
						structureWrites[b] = new VkWriteDescriptorSetAccelerationStructureKHR { AccelerationStructureCount = binding.Count, AccelerationStructures = &structures[structure] };
						for (int i = 0; i < binding.Count; i++)
						{
							structures[structure++] = new VkAccelerationStructureKHR((nint)key[position++]);
						}

						write.Next = &structureWrites[b];
						break;
					default:
						write.ImageInfo = &images[image];
						for (int i = 0; i < binding.Count; i++)
						{
							images[image++] = new VkDescriptorImageInfo { ImageView = new VkImageView((nint)key[position++]), ImageLayout = VkImageLayout.General };
						}

						break;
				}

				writes[b] = write;
			}

			device.Vk->vkUpdateDescriptorSets.Invoke(device.Handle, bindings.Length, writes, 0, null);
			return set;
		}

		#endregion

		#region Pipelines

		/// <summary>
		///     The pipeline for a state, a vertex layout, a kind of primitive and a pass's formats; made the first time
		///     they come together: linked from the program's and the device's libraries where the device has them
		///     (VulkanProgramLibraries.cs), else compiled whole.
		/// </summary>
		internal PipelineEntry GetPipeline(in PipelineKey pipelineKey, VertexInputLayout layout, in RenderState state, TextureFormat[] colorFormats, int colorCount, TextureFormat depthFormat)
		{
			if (!pipelines.TryGetValue(pipelineKey, out PipelineEntry entry))
			{
				long started = FrameProfiler.Timestamp;
				if (shaderLibraries != null)
				{
					entry = LinkPipeline(pipelineKey, layout, state, colorFormats, colorCount, depthFormat);
					FrameProfiler.EventSince(started, "pipeline", Name, "graphics pipeline linked from libraries at its first draw");
				}
				else
				{
					entry = CreatePipeline(pipelineKey, layout, state, colorFormats, colorCount, depthFormat);
					FrameProfiler.EventSince(started, "pipeline", Name, "graphics pipeline made at its first draw");
				}

				pipelines.Add(pipelineKey, entry);
			}

			return entry;
		}

		private PipelineEntry CreatePipeline(in PipelineKey pipelineKey, VertexInputLayout layout, in RenderState state, TextureFormat[] colorFormats, int colorCount, TextureFormat depthFormat)
		{
			PipelineEntry entry = new PipelineEntry { DefaultsBinding = -1 };
			fixed (byte* main = "main\0"u8)
			{
				VkPipelineShaderStageCreateInfo* shaderStages = stackalloc VkPipelineShaderStageCreateInfo[stages.Length];
				bool tessellated = false;
				for (int i = 0; i < stages.Length; i++)
				{
					shaderStages[i] = new VkPipelineShaderStageCreateInfo { Stage = stages[i].Flag, Module = stages[i].Module, Name = main };
					tessellated |= stages[i].Kind == ShaderStage.TessControl;
				}

				VkVertexInputBindingDescription* inputBindings = stackalloc VkVertexInputBindingDescription[layout.Buffers.Length + 1];
				VkVertexInputAttributeDescription* inputAttributes = stackalloc VkVertexInputAttributeDescription[layout.Attributes.Length + vertexInputs.Count];
				entry.DefaultsBinding = DescribeVertexInput(layout, inputBindings, inputAttributes, out int bufferCount, out int attributeCount);
				VkPipelineVertexInputStateCreateInfo vertexInput = new VkPipelineVertexInputStateCreateInfo
				{
					VertexBindingDescriptionCount = bufferCount,
					VertexBindingDescriptions = inputBindings,
					VertexAttributeDescriptionCount = attributeCount,
					VertexAttributeDescriptions = inputAttributes
				};
				VkPrimitiveTopology topology = pipelineKey.TopologyClass == 0 ? VkPrimitiveTopology.TriangleList : pipelineKey.TopologyClass == 1 ? VkPrimitiveTopology.LineList : VkPrimitiveTopology.PointList;
				VkPipelineInputAssemblyStateCreateInfo inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = tessellated ? VkPrimitiveTopology.PatchList : topology };
				VkPipelineTessellationStateCreateInfo tessellation = new VkPipelineTessellationStateCreateInfo { PatchControlPoints = 3 };
				VkPipelineViewportDepthClipControlCreateInfoEXT clip = new VkPipelineViewportDepthClipControlCreateInfoEXT { NegativeOneToOne = true };
				VkPipelineViewportStateCreateInfo viewport = new VkPipelineViewportStateCreateInfo { Next = &clip };
				VkPipelineRasterizationProvokingVertexStateCreateInfoEXT provoking = new VkPipelineRasterizationProvokingVertexStateCreateInfoEXT
				{
					ProvokingVertexMode = VkProvokingVertexModeEXT.LastVertexEXT
				};
				// Overestimating by exactly what the GPU does (no extra size): every pixel a triangle touches.
				VkPipelineRasterizationConservativeStateCreateInfoEXT conservative = new VkPipelineRasterizationConservativeStateCreateInfoEXT
				{
					Next = device.HasProvokingVertexLast ? &provoking : null,
					ConservativeRasterizationMode = VkConservativeRasterizationModeEXT.OverestimateEXT
				};
				bool conservativeRasterization = (pipelineKey.State & 1UL << 32) != 0;
				VkPipelineRasterizationStateCreateInfo rasterization = new VkPipelineRasterizationStateCreateInfo
				{
					Next = conservativeRasterization ? &conservative : device.HasProvokingVertexLast ? &provoking : null,
					PolygonMode = state.Wireframe && device.HasWireframe ? VkPolygonMode.Line : VkPolygonMode.Fill,
					// The engine's counter-clockwise front faces, seen in a target whose first row is the lowest.
					FrontFace = VkFrontFace.Clockwise,
					LineWidth = 1f
				};
				VkPipelineMultisampleStateCreateInfo multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)Math.Max(1, (int)pipelineKey.Samples) };
				VkPipelineDepthStencilStateCreateInfo depthStencil = new VkPipelineDepthStencilStateCreateInfo();
				VkPipelineColorBlendAttachmentState* blends = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, colorCount)];
				VkFormat* formats = stackalloc VkFormat[Math.Max(1, colorCount)];
				for (int i = 0; i < colorCount; i++)
				{
					formats[i] = device.Format(colorFormats[i]).Format;
					blends[i] = BlendAttachment(device, colorFormats[i], state);
				}

				VkPipelineColorBlendStateCreateInfo colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = colorCount, Attachments = blends };
				const int DynamicCount = 9;
				VkDynamicState* dynamics = stackalloc VkDynamicState[DynamicCount]
				{
					VkDynamicState.ViewportWithCount, VkDynamicState.ScissorWithCount, VkDynamicState.CullMode, VkDynamicState.PrimitiveTopology, VkDynamicState.DepthTestEnable,
					VkDynamicState.DepthWriteEnable, VkDynamicState.DepthCompareOp, VkDynamicState.DepthBiasEnable, VkDynamicState.DepthBias
				};
				VkPipelineDynamicStateCreateInfo dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = DynamicCount, DynamicStates = dynamics };
				VkPipelineRenderingCreateInfo rendering = new VkPipelineRenderingCreateInfo
				{
					ColorAttachmentCount = colorCount,
					ColorAttachmentFormats = formats,
					DepthAttachmentFormat = depthFormat != TextureFormat.None ? device.Format(depthFormat).Format : default
				};
				VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
				{
					Next = &rendering,
					StageCount = stages.Length,
					Stages = shaderStages,
					VertexInputState = &vertexInput,
					InputAssemblyState = &inputAssembly,
					TessellationState = tessellated ? &tessellation : null,
					ViewportState = &viewport,
					RasterizationState = &rasterization,
					MultisampleState = &multisample,
					DepthStencilState = &depthStencil,
					ColorBlendState = &colorBlend,
					DynamicState = &dynamic,
					Layout = pipelineLayout
				};
				VkPipeline pipeline;
				VulkanDevice.Check(device.Vk->vkCreateGraphicsPipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines (" + Name + ")");
				device.PipelineMade();
				if (device.HasShaderInfo)
				{
					string variant = $" ({colorCount} colours, depth {depthFormat}, samples {Math.Max(1, (int)pipelineKey.Samples)})";
					for (int i = 0; i < stages.Length; i++)
					{
						device.ReportShaderInfo(pipeline, stages[i].Flag, Name, variant);
					}
				}

				entry.Pipeline = pipeline;
			}

			return entry;
		}

		/// <summary>
		///     The vertex input of pipelines of a layout: the layout's buffers and the attributes the vertex stage reads, and
		///     for what it reads beyond them the device's defaults (OpenGL's value of an attribute without an array) from
		///     one more binding. Returns that binding (-1: none needed).
		/// </summary>
		private int DescribeVertexInput(VertexInputLayout layout, VkVertexInputBindingDescription* inputBindings, VkVertexInputAttributeDescription* inputAttributes, out int bufferCount,
			out int attributeCount)
		{
			int defaultsBinding = -1;
			bufferCount = layout.Buffers.Length;
			for (int i = 0; i < bufferCount; i++)
			{
				inputBindings[i] = new VkVertexInputBindingDescription
				{
					Binding = i,
					Stride = (Brutal.ByteSize32)(uint)layout.Buffers[i].Stride,
					InputRate = layout.Buffers[i].PerInstance ? VkVertexInputRate.Instance : VkVertexInputRate.Vertex
				};
			}

			attributeCount = 0;
			foreach (VertexAttributeDescriptor attribute in layout.Attributes)
			{
				bool read = false;
				foreach (VertexInput input in vertexInputs)
				{
					read |= input.Location == attribute.Location;
				}

				if (read)
				{
					inputAttributes[attributeCount++] = new VkVertexInputAttributeDescription
					{
						Location = attribute.Location,
						Binding = attribute.Buffer,
						Format = AttributeFormat(attribute.Format),
						Offset = (Brutal.ByteSize32)(uint)attribute.Offset
					};
				}
			}

			foreach (VertexInput input in vertexInputs)
			{
				bool given = false;
				foreach (VertexAttributeDescriptor attribute in layout.Attributes)
				{
					given |= attribute.Location == input.Location;
				}

				if (given)
				{
					continue;
				}

				if (defaultsBinding < 0)
				{
					defaultsBinding = bufferCount;
					inputBindings[bufferCount] = new VkVertexInputBindingDescription { Binding = bufferCount, Stride = (Brutal.ByteSize32)0u, InputRate = VkVertexInputRate.Instance };
					bufferCount++;
				}

				// R32G32B32A32 as floats (109), signed (108) or unsigned (107) integers.
				int format = input.Type == SpirvBaseType.Int ? 108 : input.Type == SpirvBaseType.UInt ? 107 : 109;
				inputAttributes[attributeCount++] = new VkVertexInputAttributeDescription
				{
					Location = input.Location,
					Binding = defaultsBinding,
					Format = (VkFormat)format,
					Offset = (Brutal.ByteSize32)(format == 109 ? 0u : 16u)
				};
			}

			return defaultsBinding;
		}

		/// <summary>How a colour attachment of a format blends with a render state (formats the device does not blend are written as they come).</summary>
		internal static VkPipelineColorBlendAttachmentState BlendAttachment(VulkanDevice device, TextureFormat format, in RenderState state)
		{
			bool blend = state.Blend && (device.Format(format).Features & VkFormatFeatureFlags.ColorAttachmentBlendBit) != 0;
			return new VkPipelineColorBlendAttachmentState
			{
				BlendEnable = blend,
				SrcColorBlendFactor = blend ? Factor(state.SourceColor) : VkBlendFactor.One,
				DstColorBlendFactor = blend ? Factor(state.DestinationColor) : VkBlendFactor.Zero,
				ColorBlendOp = blend ? (VkBlendOp)(int)state.ColorOperation : VkBlendOp.Add,
				SrcAlphaBlendFactor = blend ? Factor(state.SourceAlpha) : VkBlendFactor.One,
				DstAlphaBlendFactor = blend ? Factor(state.DestinationAlpha) : VkBlendFactor.Zero,
				AlphaBlendOp = blend ? (VkBlendOp)(int)state.AlphaOperation : VkBlendOp.Add,
				ColorWriteMask = (VkColorComponentFlags)(int)state.ColorMask
			};
		}

		private static VkFormat AttributeFormat(VertexFormat format)
		{
			switch (format)
			{
				case VertexFormat.Float1: return (VkFormat)100;
				case VertexFormat.Float2: return (VkFormat)103;
				case VertexFormat.Float3: return (VkFormat)106;
				case VertexFormat.Float4: return (VkFormat)109;
				case VertexFormat.UByte4Normalized: return (VkFormat)37;
				case VertexFormat.Int1: return (VkFormat)99;
				case VertexFormat.Int2: return (VkFormat)102;
				case VertexFormat.Int3: return (VkFormat)105;
				case VertexFormat.Int4: return (VkFormat)108;
				case VertexFormat.UInt1: return (VkFormat)98;
				case VertexFormat.UInt2: return (VkFormat)101;
				case VertexFormat.UInt3: return (VkFormat)104;
				default: return (VkFormat)107;
			}
		}

		private static VkBlendFactor Factor(BlendFactor factor)
		{
			switch (factor)
			{
				case BlendFactor.Zero: return VkBlendFactor.Zero;
				case BlendFactor.One: return VkBlendFactor.One;
				case BlendFactor.SrcColor: return VkBlendFactor.SrcColor;
				case BlendFactor.OneMinusSrcColor: return VkBlendFactor.OneMinusSrcColor;
				case BlendFactor.DstColor: return VkBlendFactor.DstColor;
				case BlendFactor.OneMinusDstColor: return VkBlendFactor.OneMinusDstColor;
				case BlendFactor.SrcAlpha: return VkBlendFactor.SrcAlpha;
				case BlendFactor.OneMinusSrcAlpha: return VkBlendFactor.OneMinusSrcAlpha;
				case BlendFactor.DstAlpha: return VkBlendFactor.DstAlpha;
				case BlendFactor.OneMinusDstAlpha: return VkBlendFactor.OneMinusDstAlpha;
				case BlendFactor.ConstantColor: return VkBlendFactor.ConstantColor;
				case BlendFactor.OneMinusConstantColor: return VkBlendFactor.OneMinusConstantColor;
				case BlendFactor.ConstantAlpha: return VkBlendFactor.ConstantAlpha;
				case BlendFactor.OneMinusConstantAlpha: return VkBlendFactor.OneMinusConstantAlpha;
				default: return VkBlendFactor.SrcAlphaSaturate;
			}
		}

		#endregion

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			IsDisposed = true;
			// The build may still run: what it makes is freed when it is done.
			build.ContinueWith(_ =>
			{
				long serial = Math.Max(LastUse, device.RecordingSerial);
				ReleaseLibraries(serial);
				device.Retire(RetiredKind.Pipeline, computePipeline.VkHandle, 0, serial);
				foreach (Stage stage in stages)
				{
					device.Retire(RetiredKind.ShaderModule, stage.Module.VkHandle, 0, serial);
					if (stage.Block != null)
					{
						NativeMemory.Free(stage.Block);
						stage.Block = null;
					}
				}

				ReleasePushBlock();

				device.Retire(RetiredKind.PipelineLayout, pipelineLayout.VkHandle, 0, serial);
				device.Retire(RetiredKind.SetLayout, setLayout.VkHandle, 0, serial);
			});
		}

		private enum BindingKind : byte
		{
			/// <summary>A stage's block of loose uniforms.</summary>
			Block,
			UniformBuffer,
			StorageBuffer,
			Texture,
			Image,

			/// <summary>A top level acceleration structure (CommandList.SetAccelerationStructure).</summary>
			AccelerationStructure
		}

		private struct Binding
		{
			public BindingKind Kind;
			public string Name;
			public int Count;
			public bool IsArray;
			public VkShaderStageFlags Stages;

			/// <summary>Buffers, images and acceleration structures: the binding number of the GLSL source, which is the engine's number of it.</summary>
			public int Number;

			public int StageIndex;
			public int BlockSize;

			/// <summary>Textures: the first unit of the program's texture table.</summary>
			public int Unit;

			public SpirvImageDimension Dimension;
			public bool Arrayed;
			public bool Shadow;
			public SpirvBaseType SampledType;

			/// <summary>Images: SPIR-V's number of the format the shader declares (0: none).</summary>
			public int ImageFormat;

			public TextureKind Expected;

			/// <summary>Storage blocks and images: a stage writes them.</summary>
			public bool Writable;
		}

		private sealed class Stage
		{
			public ShaderStage Kind;
			public VkShaderStageFlags Flag;
			public byte[] Unlinked;
			public VkShaderModule Module;
			public SpirvResource Resource;
			public int BlockBinding = -1;
			public int BlockSize;
			public byte* Block;
			public bool Dirty;
			public VkBuffer ArenaBuffer;
			public long ArenaOffset;
			public long ArenaSerial;
		}

		private sealed class UniformEntry
		{
			public SpirvBaseType Type;
			public bool IsBool;
			public int Components;
			public int Columns;
			public int Length;
			public int FirstSlot;
			public List<UniformTarget> Targets;

			/// <summary>Draws read it from the push constants (its one target there).</summary>
			public bool Pushed;
		}

		private struct UniformTarget
		{
			/// <summary>The stage whose block holds the values, or <see cref="PushTarget" />: the push constants.</summary>
			public int Stage;
			public int Offset;
			public int Stride;
			public int MatrixStride;
		}

		private struct Slot
		{
			public int Entry;
			public int Element;
			public int Unit;
		}

		private struct VertexInput
		{
			public int Location;
			public SpirvBaseType Type;
		}

		/// <summary>A pipeline of the program, and the vertex buffer binding that takes the device's default attribute values (-1: the layout gives every attribute the vertex stage reads).</summary>
		internal sealed class PipelineEntry
		{
			private nint pipeline;

			public int DefaultsBinding;

			/// <summary>The pipeline draws bind: a linked one is replaced by its optimized link once a thread has made it.</summary>
			public VkPipeline Pipeline
			{
				get => new VkPipeline(System.Threading.Volatile.Read(ref pipeline));
				set => System.Threading.Volatile.Write(ref pipeline, value.VkHandle);
			}
		}
	}

	/// <summary>What a pipeline is made for besides its program: the key of a program's pipelines.</summary>
	internal struct PipelineKey : IEquatable<PipelineKey>
	{
		/// <summary>Blending and the colour mask (zero factors without blending); bit 32 conservative rasterization.</summary>
		public ulong State;

		/// <summary>The colour attachments' formats, a byte each.</summary>
		public ulong Colors;

		public int LayoutId;
		public byte ColorCount;
		public byte DepthFormat;

		/// <summary>0: triangles, 1: lines, 2: points.</summary>
		public byte TopologyClass;

		public bool Wireframe;

		/// <summary>Samples of a pass without attachments (1 otherwise).</summary>
		public byte Samples;

		public bool Equals(PipelineKey other)
		{
			return State == other.State && Colors == other.Colors && LayoutId == other.LayoutId && ColorCount == other.ColorCount && DepthFormat == other.DepthFormat
				&& TopologyClass == other.TopologyClass && Wireframe == other.Wireframe && Samples == other.Samples;
		}

		public override bool Equals(object obj)
		{
			return obj is PipelineKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(State, Colors, LayoutId, ColorCount | DepthFormat << 8 | TopologyClass << 16 | (Wireframe ? 1 << 24 : 0) | Samples << 25);
		}
	}
}
