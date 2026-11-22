using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>The stage a SPIR-V module's entry point runs in.</summary>
	internal enum SpirvStage
	{
		Vertex,
		TessControl,
		TessEvaluation,
		Geometry,
		Fragment,
		Compute
	}

	/// <summary>What a resource variable of a SPIR-V module is: the kind of descriptor it is bound through.</summary>
	internal enum SpirvResourceKind
	{
		/// <summary>A uniform block, also the one glslang gathers a shader's loose uniforms into.</summary>
		UniformBuffer,

		/// <summary>A shader storage block.</summary>
		StorageBuffer,

		/// <summary>A GLSL sampler (sampler2D, samplerCubeShadow, ...): a texture with its sampler.</summary>
		CombinedImageSampler,

		/// <summary>
		///     An image read and written by texel (image2D, ...). With <see cref="SpirvImageDimension.Buffer" /> it is a
		///     storage texel buffer (imageBuffer), with <see cref="SpirvImageDimension.SubpassData" /> an input attachment.
		/// </summary>
		StorageImage,

		/// <summary>
		///     A texture without a sampler (texture2D). glslang writes samplerBuffer like this too: with
		///     <see cref="SpirvImageDimension.Buffer" /> it is a uniform texel buffer.
		/// </summary>
		SeparateImage,

		/// <summary>A sampler without a texture (sampler, samplerShadow).</summary>
		SeparateSampler
	}

	/// <summary>The component type of a numeric type, or <see cref="Struct" /> for a structure.</summary>
	internal enum SpirvBaseType
	{
		Unknown,
		Bool,
		Int,
		UInt,
		Float,
		Double,
		Struct
	}

	/// <summary>The dimensionality of an image type, in the order of SPIR-V's own values.</summary>
	internal enum SpirvImageDimension
	{
		Dim1D,
		Dim2D,
		Dim3D,
		Cube,
		Rect,
		Buffer,
		SubpassData
	}

	/// <summary>
	///     A numeric type: a scalar (<see cref="VectorSize" /> 1, <see cref="Columns" /> 1), a vector (VectorSize 2..4,
	///     Columns 1) or a matrix (Columns 2..4, VectorSize = its rows). A structure has only
	///     <see cref="SpirvBaseType.Struct" /> and zeros.
	/// </summary>
	internal struct SpirvNumericType
	{
		public SpirvBaseType BaseType;
		public int VectorSize;
		public int Columns;

		/// <summary>Bits of one component: 32, or 64 for doubles and 64-bit integers (a bool counts as 32).</summary>
		public int Bits;
	}

	/// <summary>
	///     A member of a uniform block: a uniform of a numeric type, or a structure with members of its own
	///     (<see cref="Members" />). Either can be an array. OpenGL names a uniform by the way to it: "name",
	///     "s.member", "items[2].member"; <see cref="SpirvResource.FlattenMembers" /> lists a block's uniforms so.
	/// </summary>
	internal sealed class SpirvBlockMember
	{
		/// <summary>
		///     The member's own name ("member", not "s.member"). In the list of
		///     <see cref="SpirvResource.FlattenMembers" />: OpenGL's whole name, without a "[0]" after an array's.
		/// </summary>
		public string Name;

		/// <summary>
		///     Byte offset from the start of the block, as the module's layout decorations give it (std140, std430).
		///     Of a member inside an array of structures it is the one in the array's first element: in element i
		///     it is i times the array's <see cref="ArrayStride" /> further.
		/// </summary>
		public int Offset;

		/// <summary>
		///     The numeric type; <see cref="SpirvBaseType.Struct" /> for a structure. A bool of the shader is a uint
		///     here (bvec3: three of them): SPIR-V has no bool in a block.
		/// </summary>
		public SpirvNumericType Type;

		/// <summary>
		///     0 when it is not an array, else the number of elements. An array of arrays counts as one array of all
		///     its elements, with the innermost stride.
		/// </summary>
		public int ArrayLength;

		/// <summary>Bytes from one element to the next; 0 when it is not an array.</summary>
		public int ArrayStride;

		/// <summary>Bytes from one column of a matrix to the next (from one row when <see cref="RowMajor" />); 0 otherwise.</summary>
		public int MatrixStride;

		/// <summary>The matrix is stored row by row.</summary>
		public bool RowMajor;

		/// <summary>The members of a structure, in the order declared; null for a numeric type.</summary>
		public List<SpirvBlockMember> Members;
	}

	/// <summary>A variable of a SPIR-V module that is bound through a descriptor: a block, a texture, an image, a sampler.</summary>
	internal sealed class SpirvResource
	{
		public uint VariableId;

		/// <summary>
		///     Blocks: the block's name (OpenGL's name of a uniform or shader storage block). Others: the variable's name.
		/// </summary>
		public string Name;

		/// <summary>The variable's own name; "" when it has none.</summary>
		public string InstanceName;

		public SpirvResourceKind Kind;

		/// <summary>The descriptor set; -1 when the module gives none.</summary>
		public int Set;

		/// <summary>The binding; -1 when the module gives none.</summary>
		public int Binding;

		/// <summary>
		///     0 when it is not an array of resources, else the number of elements (<c>uniform sampler2D maps[4]</c>: 4;
		///     an array of arrays: all of them). An array takes one binding. 0 for an array without a size too, see
		///     <see cref="IsRuntimeArray" />.
		/// </summary>
		public int ArrayLength;

		/// <summary>An array of resources without a size.</summary>
		public bool IsRuntimeArray;

		/// <summary>Textures and images: the dimensionality.</summary>
		public SpirvImageDimension Dimension;

		/// <summary>Textures and images: an array texture.</summary>
		public bool Arrayed;

		/// <summary>Textures: sampled with a depth comparison (sampler2DShadow).</summary>
		public bool Shadow;

		/// <summary>Textures and images: multisampled.</summary>
		public bool Multisampled;

		/// <summary>Textures and images: the component type, Float, Int or UInt.</summary>
		public SpirvBaseType SampledType;

		/// <summary>Images: SPIR-V's image format value (0 = unknown, which is what textures have).</summary>
		public int ImageFormat;

		/// <summary>Declared readonly: the decoration on the variable, or on every member of a block.</summary>
		public bool NonWritable;

		/// <summary>Declared writeonly: the decoration on the variable, or on every member of a block.</summary>
		public bool NonReadable;

		/// <summary>
		///     Whether an instruction of some function names the variable. glslang keeps the uniforms a shader declares
		///     and never uses: they are here with false (OpenGL would not list them as active). Its automatic bindings
		///     pass them over, which leaves them at binding 0 whatever else is there.
		/// </summary>
		public bool Used;

		/// <summary>
		///     Blocks: the bytes the block's members take. A uniform block's is rounded up to 16 (std140); a storage
		///     block's is that of the members before an array without a size.
		/// </summary>
		public int BlockSize;

		/// <summary>The uniform block glslang makes for a shader's loose uniforms: its name is "gl_DefaultUniformBlock".</summary>
		public bool IsDefaultUniformBlock;

		/// <summary>
		///     A uniform block's members in the order declared, structures with their own members inside; null for
		///     every other kind. Where a structure of the shader had a sampler, glslang leaves an int named like a
		///     comment ("/*map*/"); the sampler is a resource of its own, named "s.map".
		/// </summary>
		public List<SpirvBlockMember> Members;

		/// <summary>
		///     A uniform block's uniforms one by one as OpenGL names them, structures and arrays of structures taken
		///     apart: "name", "array" (<see cref="SpirvBlockMember.ArrayLength" /> above 0), "s.member",
		///     "items[2].member", each with its offset from the start of the block. The names are those inside the
		///     block: no block or instance name in front. Empty for every other kind.
		/// </summary>
		public List<SpirvBlockMember> FlattenMembers()
		{
			List<SpirvBlockMember> uniforms = new List<SpirvBlockMember>();
			if (Members != null)
			{
				Flatten(Members, "", 0, uniforms);
			}
			return uniforms;
		}

		private static void Flatten(List<SpirvBlockMember> members, string prefix, int shift, List<SpirvBlockMember> uniforms)
		{
			foreach (SpirvBlockMember member in members)
			{
				string name = prefix + member.Name;
				if (member.Members == null)
				{
					uniforms.Add(new SpirvBlockMember
					{
						Name = name,
						Offset = member.Offset + shift,
						Type = member.Type,
						ArrayLength = member.ArrayLength,
						ArrayStride = member.ArrayStride,
						MatrixStride = member.MatrixStride,
						RowMajor = member.RowMajor
					});
				}
				else if (member.ArrayLength == 0)
				{
					Flatten(member.Members, name + ".", shift, uniforms);
				}
				else
				{
					for (int i = 0; i < member.ArrayLength; i++)
					{
						Flatten(member.Members, name + "[" + i.ToString(CultureInfo.InvariantCulture) + "].", shift + i * member.ArrayStride, uniforms);
					}
				}
			}
		}
	}

	/// <summary>
	///     An input or output of a SPIR-V module's stage: a vertex attribute, a varying, a fragment output, or one of
	///     the built-in ones (gl_FragDepth, the block gl_Position is in).
	/// </summary>
	internal sealed class SpirvInterfaceVariable
	{
		public uint VariableId;

		/// <summary>The variable's name; for an interface block, the name of the block.</summary>
		public string Name;

		/// <summary>The first location; -1 when the module gives none (built-ins have none).</summary>
		public int Location;

		/// <summary>The first component within the location; 0 when the module gives none.</summary>
		public int Component;

		/// <summary>
		///     The type of one element. Not counted as an array: the array geometry and tessellation stages put around
		///     what they get or give per vertex.
		/// </summary>
		public SpirvNumericType Type;

		/// <summary>0 when it is not an array, else the number of elements (not the vertices of a per-vertex array).</summary>
		public int ArrayLength;

		/// <summary>
		///     How many locations it takes from <see cref="Location" /> on, by Vulkan's rules: one per vector (two for
		///     64-bit vectors of 3 or 4), one per matrix column, times the array's length; the members' sum for a
		///     structure. 0 for built-ins.
		/// </summary>
		public int LocationCount;

		/// <summary>An interface block (<c>out Name { ... } instance;</c>, also gl_PerVertex).</summary>
		public bool IsBlock;

		/// <summary>Not interpolated (<c>flat</c>).</summary>
		public bool Flat;

		/// <summary>
		///     SPIR-V's BuiltIn value when the variable is a built-in (Layer 9, FragDepth 22, VertexIndex 42, ...),
		///     else -1. -1 for a block of built-ins too (gl_PerVertex): its members are the built-ins.
		/// </summary>
		public int BuiltIn;

		/// <summary>Whether an instruction of some function names the variable (glslang keeps what is declared and never used).</summary>
		public bool Used;
	}

	/// <summary>
	///     A SPIR-V module read into what a graphics backend needs to know of it: its stage, the resources it binds, its
	///     inputs and outputs. Bindings and locations can be given new numbers, and the module written out again.
	/// </summary>
	/// <remarks>
	///     Made for what glslang writes for one GLSL stage. The declarations before the first function are read; of the
	///     functions only which variables their instructions name. Not followed: decoration groups (glslang writes
	///     none) and array lengths computed from specialization constants (a plain specialization constant counts with
	///     its default). The lists are in the order the module declares the variables, which for glslang is the order
	///     the code first names them in, the unused ones last.
	/// </remarks>
	internal sealed class SpirvModule
	{
		private const uint Magic = 0x07230203;
		private const uint SwappedMagic = 0x03022307;
		private const int HeaderWords = 5;

		/// <summary>SPIR-V's own limit: every id is below it.</summary>
		private const uint IdLimit = 0x400000;

		private readonly uint version;
		private readonly uint generator;
		private readonly uint bound;
		private readonly uint schema;

		/// <summary>The instructions before the first function, each with its words: what is read and rewritten.</summary>
		private readonly List<uint[]> instructions = new List<uint[]>();

		/// <summary>The words of the functions, from the first OpFunction to the end. Nothing in them changes.</summary>
		private readonly uint[] functions;

		/// <summary>Where the annotations (decorations) start in <see cref="instructions" />.</summary>
		private readonly int annotationStart;

		/// <summary>Where they end, which is where a new decoration goes.</summary>
		private int annotationEnd;

		private readonly List<SpirvResource> resources = new List<SpirvResource>();
		private readonly List<SpirvInterfaceVariable> inputs = new List<SpirvInterfaceVariable>();
		private readonly List<SpirvInterfaceVariable> outputs = new List<SpirvInterfaceVariable>();
		private readonly List<SpirvInterfaceVariable> builtInInputs = new List<SpirvInterfaceVariable>();
		private readonly List<SpirvInterfaceVariable> builtInOutputs = new List<SpirvInterfaceVariable>();
		private readonly HashSet<int> usedBuiltIns = new HashSet<int>();

		/// <summary>
		///     The interface blocks whose members have locations of their own: the structure type by variable. Null
		///     without any, which is the usual.
		/// </summary>
		private Dictionary<uint, uint> locatedBlocks;

		public SpirvStage Stage { get; }

		/// <summary>The name of the entry point (the first one, should a module have several).</summary>
		public string EntryPoint { get; }

		/// <summary>
		///     Every variable bound through a descriptor: the storage classes UniformConstant, Uniform and StorageBuffer.
		///     Left out is what <see cref="SpirvResourceKind" /> has no name for (acceleration structures, push constants).
		/// </summary>
		public IReadOnlyList<SpirvResource> Resources => resources;

		/// <summary>The stage's inputs the shader declares itself; the built-in ones are in <see cref="BuiltInInputs" />.</summary>
		public IReadOnlyList<SpirvInterfaceVariable> Inputs => inputs;

		/// <summary>The stage's outputs the shader declares itself; the built-in ones are in <see cref="BuiltInOutputs" />.</summary>
		public IReadOnlyList<SpirvInterfaceVariable> Outputs => outputs;

		/// <summary>
		///     The built-in inputs the module declares (gl_VertexIndex, gl_FragCoord, gl_in), used or not: see
		///     <see cref="SpirvInterfaceVariable.Used" /> and <see cref="UsesBuiltIn" />.
		/// </summary>
		public IReadOnlyList<SpirvInterfaceVariable> BuiltInInputs => builtInInputs;

		/// <summary>The built-in outputs the module declares (the block of gl_Position, gl_Layer, gl_FragDepth), used or not.</summary>
		public IReadOnlyList<SpirvInterfaceVariable> BuiltInOutputs => builtInOutputs;

		public static SpirvModule Parse(byte[] spirv)
		{
			if (spirv == null)
			{
				throw new ArgumentNullException(nameof(spirv));
			}
			return new SpirvModule(spirv);
		}

		public static SpirvModule Parse(ReadOnlySpan<byte> spirv)
		{
			return new SpirvModule(spirv);
		}

		private SpirvModule(ReadOnlySpan<byte> spirv)
		{
			if (spirv.Length < HeaderWords * 4)
			{
				throw new InvalidDataException($"Not a SPIR-V module: {spirv.Length} bytes, less than its header of {HeaderWords * 4}.");
			}
			if (spirv.Length % 4 != 0)
			{
				throw new InvalidDataException($"Not a SPIR-V module: {spirv.Length} bytes is not a whole number of 32-bit words.");
			}
			uint magic = ReadWord(spirv, 0);
			if (magic == SwappedMagic)
			{
				throw new InvalidDataException("The SPIR-V module is big-endian, only little-endian ones are read.");
			}
			if (magic != Magic)
			{
				throw new InvalidDataException($"Not a SPIR-V module: it starts with 0x{magic:X8} where 0x{Magic:X8} is expected.");
			}
			version = ReadWord(spirv, 1);
			generator = ReadWord(spirv, 2);
			bound = ReadWord(spirv, 3);
			schema = ReadWord(spirv, 4);

			int wordCount = spirv.Length / 4;
			int position = HeaderWords;
			int firstAnnotation = -1;
			int lastAnnotation = -1;
			int firstDeclaration = -1;
			Declarations declarations = new Declarations();
			while (position < wordCount)
			{
				int opcode = ReadInstruction(spirv, position, wordCount, out int length);
				if (opcode == Op.Function)
				{
					break;
				}
				uint[] words = new uint[length];
				for (int i = 0; i < length; i++)
				{
					words[i] = ReadWord(spirv, position + i);
				}
				if (IsAnnotation(opcode))
				{
					if (firstAnnotation < 0)
					{
						firstAnnotation = instructions.Count;
					}
					lastAnnotation = instructions.Count;
				}
				else if (firstDeclaration < 0 && !ComesBeforeAnnotations(opcode))
				{
					firstDeclaration = instructions.Count;
				}
				instructions.Add(words);
				declarations.Read(words);
				position += length;
			}

			functions = new uint[wordCount - position];
			for (int i = 0; i < functions.Length; i++)
			{
				functions[i] = ReadWord(spirv, position + i);
			}
			while (position < wordCount)
			{
				ReadInstruction(spirv, position, wordCount, out int length);
				position += length;
			}

			// New decorations go after the last one there is. A module without any gets them where they would be:
			// after the debug names, before the first type, constant or variable.
			if (lastAnnotation >= 0)
			{
				annotationStart = firstAnnotation;
				annotationEnd = lastAnnotation + 1;
			}
			else
			{
				annotationStart = firstDeclaration >= 0 ? firstDeclaration : instructions.Count;
				annotationEnd = annotationStart;
			}

			if (declarations.EntryPoint == null)
			{
				throw new InvalidDataException("The SPIR-V module has no entry point.");
			}
			EntryPoint = declarations.EntryPoint;
			Stage = StageOf(declarations.ExecutionModel);

			bool[] used = declarations.FindUsedVariables(functions, usedBuiltIns);
			for (int i = 0; i < declarations.Variables.Count; i++)
			{
				Variable variable = declarations.Variables[i];
				switch (variable.StorageClass)
				{
					case StorageClass.UniformConstant:
					case StorageClass.Uniform:
					case StorageClass.StorageBuffer:
						SpirvResource resource = declarations.DescribeResource(variable);
						if (resource != null)
						{
							resource.Used = used[i];
							resources.Add(resource);
						}
						break;
					case StorageClass.Input:
					case StorageClass.Output:
						bool input = variable.StorageClass == StorageClass.Input;
						SpirvInterfaceVariable described = declarations.DescribeInterface(variable, Stage, out bool builtIn, out uint locatedStruct);
						described.Used = used[i];
						if (builtIn)
						{
							(input ? builtInInputs : builtInOutputs).Add(described);
							break;
						}
						(input ? inputs : outputs).Add(described);
						if (locatedStruct != 0)
						{
							locatedBlocks ??= new Dictionary<uint, uint>();
							locatedBlocks[variable.Id] = locatedStruct;
						}
						break;
				}
			}
		}

		/// <summary>
		///     Whether the shader's code reads or writes a built-in, by SPIR-V's BuiltIn value (Position 0, PointSize 1,
		///     Layer 9, FragDepth 22, ...): an instruction of a function names the built-in's variable, or reaches its
		///     member of a block of built-ins (gl_PerVertex).
		/// </summary>
		/// <remarks>
		///     Declaring is not using: glslang declares gl_VertexIndex and gl_InstanceIndex in every vertex shader, and
		///     gl_PointSize, gl_ClipDistance and gl_CullDistance next to gl_Position in every block of built-ins. What
		///     is declared is in <see cref="BuiltInInputs" /> and <see cref="BuiltInOutputs" />.
		/// </remarks>
		public bool UsesBuiltIn(int builtIn)
		{
			return usedBuiltIns.Contains(builtIn);
		}

		/// <summary>Sets (adds or replaces) the DescriptorSet and Binding decorations of a resource variable.</summary>
		public void SetBinding(uint variableId, int set, int binding)
		{
			SpirvResource resource = null;
			foreach (SpirvResource candidate in resources)
			{
				if (candidate.VariableId == variableId)
				{
					resource = candidate;
					break;
				}
			}
			if (resource == null)
			{
				throw new ArgumentException($"SPIR-V id {variableId} is not a resource variable of this module.", nameof(variableId));
			}
			if (set < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(set), set, "A descriptor set cannot be negative.");
			}
			if (binding < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(binding), binding, "A binding cannot be negative.");
			}
			SetDecoration(variableId, Decoration.DescriptorSet, (uint)set);
			SetDecoration(variableId, Decoration.Binding, (uint)binding);
			resource.Set = set;
			resource.Binding = binding;
		}

		/// <summary>
		///     Sets (adds or replaces) the Location decoration of an input or output the shader declares itself (not of
		///     a built-in).
		/// </summary>
		/// <remarks>
		///     An interface block whose members had locations of their own (<c>layout(location = 4) vec3 a;</c> inside
		///     the block) loses those: a block has a location itself or on its members, not both, and with its own its
		///     members follow one another from it, so it takes its <see cref="SpirvInterfaceVariable.LocationCount" />
		///     locations like any other variable.
		/// </remarks>
		public void SetLocation(uint variableId, int location)
		{
			SpirvInterfaceVariable variable = FindInterfaceVariable(inputs, variableId) ?? FindInterfaceVariable(outputs, variableId);
			if (variable == null)
			{
				throw new ArgumentException($"SPIR-V id {variableId} is not an input or output variable of this module that takes a location.", nameof(variableId));
			}
			if (location < 0)
			{
				throw new ArgumentOutOfRangeException(nameof(location), location, "A location cannot be negative.");
			}
			if (locatedBlocks != null && locatedBlocks.Remove(variableId, out uint structId))
			{
				for (int i = annotationEnd - 1; i >= annotationStart; i--)
				{
					uint[] words = instructions[i];
					if (OpcodeOf(words) == Op.MemberDecorate && words.Length >= 4 && words[1] == structId && words[3] == Decoration.Location)
					{
						instructions.RemoveAt(i);
						annotationEnd--;
					}
				}
			}
			SetDecoration(variableId, Decoration.Location, (uint)location);
			variable.Location = location;
		}

		/// <summary>The module's words as bytes, with every change made through the setters.</summary>
		public byte[] ToBytes()
		{
			int wordCount = HeaderWords + functions.Length;
			foreach (uint[] words in instructions)
			{
				wordCount += words.Length;
			}
			byte[] bytes = new byte[wordCount * 4];
			int position = 0;
			WriteWord(bytes, ref position, Magic);
			WriteWord(bytes, ref position, version);
			WriteWord(bytes, ref position, generator);
			WriteWord(bytes, ref position, bound);
			WriteWord(bytes, ref position, schema);
			foreach (uint[] words in instructions)
			{
				foreach (uint word in words)
				{
					WriteWord(bytes, ref position, word);
				}
			}
			foreach (uint word in functions)
			{
				WriteWord(bytes, ref position, word);
			}
			return bytes;
		}

		/// <summary>
		///     Gives an id's decoration a new value where the module has the decoration, else adds it after the last
		///     annotation. A decoration declares no id, so the header's bound stays.
		/// </summary>
		private void SetDecoration(uint id, uint decoration, uint value)
		{
			bool found = false;
			for (int i = annotationStart; i < annotationEnd; i++)
			{
				uint[] words = instructions[i];
				if (OpcodeOf(words) == Op.Decorate && words.Length >= 4 && words[1] == id && words[2] == decoration)
				{
					words[3] = value;
					found = true;
				}
			}
			if (!found)
			{
				instructions.Insert(annotationEnd, new uint[] { (4u << 16) | Op.Decorate, id, decoration, value });
				annotationEnd++;
			}
		}

		private static SpirvInterfaceVariable FindInterfaceVariable(List<SpirvInterfaceVariable> variables, uint variableId)
		{
			foreach (SpirvInterfaceVariable variable in variables)
			{
				if (variable.VariableId == variableId)
				{
					return variable;
				}
			}
			return null;
		}

		private static SpirvStage StageOf(int executionModel)
		{
			switch (executionModel)
			{
				case 0: return SpirvStage.Vertex;
				case 1: return SpirvStage.TessControl;
				case 2: return SpirvStage.TessEvaluation;
				case 3: return SpirvStage.Geometry;
				case 4: return SpirvStage.Fragment;
				case 5: return SpirvStage.Compute;
				default: throw new NotSupportedException($"The SPIR-V module's entry point has execution model {executionModel}: not a stage read here.");
			}
		}

		private static uint ReadWord(ReadOnlySpan<byte> spirv, int word)
		{
			return BinaryPrimitives.ReadUInt32LittleEndian(spirv.Slice(word * 4, 4));
		}

		private static void WriteWord(byte[] bytes, ref int position, uint word)
		{
			BinaryPrimitives.WriteUInt32LittleEndian(new Span<byte>(bytes, position, 4), word);
			position += 4;
		}

		/// <summary>The opcode of the instruction at a word, and how many words it has; throws when it does not fit.</summary>
		private static int ReadInstruction(ReadOnlySpan<byte> spirv, int position, int wordCount, out int length)
		{
			uint first = ReadWord(spirv, position);
			length = (int)(first >> 16);
			if (length == 0 || length > wordCount - position)
			{
				throw new InvalidDataException(
					$"Malformed SPIR-V: the instruction at word {position} (opcode {first & 0xFFFF}) says it has {length} words, the module has {wordCount - position} left.");
			}
			return (int)(first & 0xFFFF);
		}

		private static int OpcodeOf(uint[] words)
		{
			return (int)(words[0] & 0xFFFF);
		}

		private static bool IsAnnotation(int opcode)
		{
			switch (opcode)
			{
				case Op.Decorate:
				case Op.MemberDecorate:
				case Op.DecorationGroup:
				case Op.GroupDecorate:
				case Op.GroupMemberDecorate:
				case Op.DecorateId:
				case Op.DecorateString:
				case Op.MemberDecorateString:
					return true;
				default:
					return false;
			}
		}

		/// <summary>
		///     The instructions of the sections a module has before its annotations: capabilities, extensions, imports,
		///     the memory model, entry points, execution modes and debug information.
		/// </summary>
		private static bool ComesBeforeAnnotations(int opcode)
		{
			switch (opcode)
			{
				case Op.SourceContinued:
				case Op.Source:
				case Op.SourceExtension:
				case Op.Name:
				case Op.MemberName:
				case Op.String:
				case Op.Extension:
				case Op.ExtInstImport:
				case Op.MemoryModel:
				case Op.EntryPoint:
				case Op.ExecutionMode:
				case Op.Capability:
				case Op.ModuleProcessed:
				case Op.ExecutionModeId:
					return true;
				default:
					return false;
			}
		}

		private static class Op
		{
			public const int SourceContinued = 2;
			public const int Source = 3;
			public const int SourceExtension = 4;
			public const int Name = 5;
			public const int MemberName = 6;
			public const int String = 7;
			public const int Line = 8;
			public const int Extension = 10;
			public const int ExtInstImport = 11;
			public const int ExtInst = 12;
			public const int MemoryModel = 14;
			public const int EntryPoint = 15;
			public const int ExecutionMode = 16;
			public const int Capability = 17;
			public const int TypeBool = 20;
			public const int TypeInt = 21;
			public const int TypeFloat = 22;
			public const int TypeVector = 23;
			public const int TypeMatrix = 24;
			public const int TypeImage = 25;
			public const int TypeSampler = 26;
			public const int TypeSampledImage = 27;
			public const int TypeArray = 28;
			public const int TypeRuntimeArray = 29;
			public const int TypeStruct = 30;
			public const int TypePointer = 32;
			public const int Constant = 43;
			public const int SpecConstant = 50;
			public const int Function = 54;
			public const int Variable = 59;
			public const int Load = 61;
			public const int Store = 62;
			public const int CopyMemory = 63;
			public const int CopyMemorySized = 64;
			public const int AccessChain = 65;
			public const int InBoundsAccessChain = 66;
			public const int PtrAccessChain = 67;
			public const int ArrayLength = 68;
			public const int InBoundsPtrAccessChain = 70;
			public const int Decorate = 71;
			public const int MemberDecorate = 72;
			public const int DecorationGroup = 73;
			public const int GroupDecorate = 74;
			public const int GroupMemberDecorate = 75;
			public const int VectorShuffle = 79;
			public const int CompositeExtract = 81;
			public const int CompositeInsert = 82;
			public const int ImageSampleImplicitLod = 87;
			public const int ImageSampleExplicitLod = 88;
			public const int ImageSampleDrefImplicitLod = 89;
			public const int ImageSampleDrefExplicitLod = 90;
			public const int ImageSampleProjImplicitLod = 91;
			public const int ImageSampleProjExplicitLod = 92;
			public const int ImageSampleProjDrefImplicitLod = 93;
			public const int ImageSampleProjDrefExplicitLod = 94;
			public const int ImageFetch = 95;
			public const int ImageGather = 96;
			public const int ImageDrefGather = 97;
			public const int ImageRead = 98;
			public const int ImageWrite = 99;
			public const int LoopMerge = 246;
			public const int SelectionMerge = 247;
			public const int BranchConditional = 250;
			public const int Switch = 251;
			public const int LifetimeStart = 256;
			public const int LifetimeStop = 257;
			public const int ImageSparseSampleImplicitLod = 305;
			public const int ImageSparseSampleExplicitLod = 306;
			public const int ImageSparseSampleDrefImplicitLod = 307;
			public const int ImageSparseSampleDrefExplicitLod = 308;
			public const int ImageSparseSampleProjImplicitLod = 309;
			public const int ImageSparseSampleProjExplicitLod = 310;
			public const int ImageSparseSampleProjDrefImplicitLod = 311;
			public const int ImageSparseSampleProjDrefExplicitLod = 312;
			public const int ImageSparseFetch = 313;
			public const int ImageSparseGather = 314;
			public const int ImageSparseDrefGather = 315;
			public const int ImageSparseRead = 320;
			public const int ModuleProcessed = 330;
			public const int ExecutionModeId = 331;
			public const int DecorateId = 332;
			public const int DecorateString = 5632;
			public const int MemberDecorateString = 5633;
		}

		private static class Decoration
		{
			public const int Block = 2;
			public const int BufferBlock = 3;
			public const int RowMajor = 4;
			public const int ArrayStride = 6;
			public const int MatrixStride = 7;
			public const int BuiltIn = 11;
			public const int Flat = 14;
			public const int Patch = 15;
			public const int NonWritable = 24;
			public const int NonReadable = 25;
			public const int Location = 30;
			public const int Component = 31;
			public const int Binding = 33;
			public const int DescriptorSet = 34;
			public const int Offset = 35;
		}

		private static class StorageClass
		{
			public const int UniformConstant = 0;
			public const int Input = 1;
			public const int Uniform = 2;
			public const int Output = 3;
			public const int StorageBuffer = 12;
		}

		/// <summary>A global variable as its OpVariable declares it.</summary>
		private struct Variable
		{
			public uint Id;
			public uint TypeId;
			public int StorageClass;
		}

		/// <summary>One type declaration. Which fields say something depends on <see cref="Opcode" />.</summary>
		private sealed class TypeInfo
		{
			/// <summary>What an id that declares no type known here is taken for.</summary>
			public static readonly TypeInfo Unknown = new TypeInfo();

			public int Opcode;

			/// <summary>
			///     What the type is made of: a vector's component, a matrix's column, an array's element, a pointer's
			///     target, a sampled image's image, an image's sampled type.
			/// </summary>
			public uint Element;

			/// <summary>The components of a vector, the columns of a matrix.</summary>
			public int Count;

			/// <summary>The width of an integer or floating point type.</summary>
			public int Bits;

			public bool Signed;

			/// <summary>The id of the constant that is an array's length.</summary>
			public uint LengthId;

			/// <summary>The types of a structure's members.</summary>
			public uint[] Members;

			public int Dimension;
			public int Depth;
			public bool Arrayed;
			public bool Multisampled;
			public int Sampled;
			public int Format;
		}

		/// <summary>The decorations read here, of one id or of one member of a structure.</summary>
		private sealed class DecorationSet
		{
			/// <summary>What an id or member without decorations has. Never written to.</summary>
			public static readonly DecorationSet None = new DecorationSet();

			public bool Block;
			public bool BufferBlock;
			public bool RowMajor;
			public bool Flat;
			public bool Patch;
			public bool NonWritable;
			public bool NonReadable;
			public int ArrayStride;
			public int MatrixStride;
			public int BuiltIn = -1;
			public int Location = -1;
			public int Component = -1;
			public int Binding = -1;
			public int DescriptorSet = -1;
			public int Offset = -1;
		}

		/// <summary>
		///     A variable that is a built-in (gl_FragDepth) or holds built-ins (gl_PerVertex, gl_in), for finding out which
		///     of them the code uses.
		/// </summary>
		private sealed class BuiltInVariable
		{
			/// <summary>The variable's own BuiltIn value; -1 for a block of built-ins.</summary>
			public int BuiltIn = -1;

			/// <summary>A block's BuiltIn value per member (-1 for a member that is none); null for a plain variable.</summary>
			public int[] Members;

			/// <summary>How many arrays are around the block (1 for gl_in): the indices an access chain has before the member's.</summary>
			public int ArrayDepth;
		}

		/// <summary>
		///     What the instructions before the first function declare, by id. Only needed while a module is read: the
		///     lists the module keeps are made from it.
		/// </summary>
		private sealed class Declarations
		{
			public readonly List<Variable> Variables = new List<Variable>();
			public string EntryPoint;
			public int ExecutionModel;

			private readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
			private readonly Dictionary<ulong, string> memberNames = new Dictionary<ulong, string>();
			private readonly Dictionary<uint, DecorationSet> decorations = new Dictionary<uint, DecorationSet>();
			private readonly Dictionary<ulong, DecorationSet> memberDecorations = new Dictionary<ulong, DecorationSet>();
			private readonly Dictionary<uint, TypeInfo> types = new Dictionary<uint, TypeInfo>();
			private readonly Dictionary<uint, uint> constants = new Dictionary<uint, uint>();

			/// <summary>
			///     The ids types are made of that no type had declared by then. None of them may be declared later:
			///     that keeps a damaged module's types from containing themselves.
			/// </summary>
			private readonly HashSet<uint> namedUndeclared = new HashSet<uint>();

			/// <summary>Takes in one instruction.</summary>
			public void Read(uint[] words)
			{
				int opcode = OpcodeOf(words);
				switch (opcode)
				{
					case Op.Name:
						Require(words, 3, "OpName");
						names[words[1]] = ReadString(words, 2);
						break;
					case Op.MemberName:
						Require(words, 4, "OpMemberName");
						memberNames[MemberKey(words[1], words[2])] = ReadString(words, 3);
						break;
					case Op.EntryPoint:
						Require(words, 4, "OpEntryPoint");
						if (EntryPoint == null)
						{
							ExecutionModel = (int)words[1];
							EntryPoint = ReadString(words, 3);
						}
						break;
					case Op.TypeBool:
					case Op.TypeSampler:
						Require(words, 2, "OpTypeBool / OpTypeSampler");
						Declare(words[1], new TypeInfo { Opcode = opcode });
						break;
					case Op.TypeInt:
						Require(words, 4, "OpTypeInt");
						Declare(words[1], new TypeInfo { Opcode = opcode, Bits = (int)words[2], Signed = words[3] != 0 });
						break;
					case Op.TypeFloat:
						Require(words, 3, "OpTypeFloat");
						Declare(words[1], new TypeInfo { Opcode = opcode, Bits = (int)words[2] });
						break;
					case Op.TypeVector:
					case Op.TypeMatrix:
						Require(words, 4, "OpTypeVector / OpTypeMatrix");
						Declare(words[1], new TypeInfo { Opcode = opcode, Element = words[2], Count = (int)words[3] });
						break;
					case Op.TypeImage:
						Require(words, 9, "OpTypeImage");
						Declare(words[1], new TypeInfo
						{
							Opcode = opcode,
							Element = words[2],
							Dimension = (int)words[3],
							Depth = (int)words[4],
							Arrayed = words[5] == 1,
							Multisampled = words[6] == 1,
							Sampled = (int)words[7],
							Format = (int)words[8]
						});
						break;
					case Op.TypeSampledImage:
					case Op.TypeRuntimeArray:
						Require(words, 3, "OpTypeSampledImage / OpTypeRuntimeArray");
						Declare(words[1], new TypeInfo { Opcode = opcode, Element = words[2] });
						break;
					case Op.TypeArray:
						Require(words, 4, "OpTypeArray");
						Declare(words[1], new TypeInfo { Opcode = opcode, Element = words[2], LengthId = words[3] });
						break;
					case Op.TypeStruct:
						Require(words, 2, "OpTypeStruct");
						Declare(words[1], new TypeInfo { Opcode = opcode, Members = words.AsSpan(2).ToArray() });
						break;
					case Op.TypePointer:
						Require(words, 4, "OpTypePointer");
						Declare(words[1], new TypeInfo { Opcode = opcode, Element = words[3] });
						break;
					case Op.Constant:
					case Op.SpecConstant:
						// Kept for array lengths: the low word is all of a length. A specialization constant counts
						// with its default, which is its value unless a pipeline specializes it.
						Require(words, 4, "OpConstant / OpSpecConstant");
						constants[words[2]] = words[3];
						break;
					case Op.Variable:
						Require(words, 4, "OpVariable");
						if (words[2] >= IdLimit)
						{
							throw new InvalidDataException($"Malformed SPIR-V: variable id {words[2]} is above the limit of {IdLimit - 1} ids.");
						}
						Variables.Add(new Variable { TypeId = words[1], Id = words[2], StorageClass = (int)words[3] });
						break;
					case Op.Decorate:
						Require(words, 3, "OpDecorate");
						if (!decorations.TryGetValue(words[1], out DecorationSet ofId))
						{
							ofId = new DecorationSet();
							decorations.Add(words[1], ofId);
						}
						Apply(ofId, words, 2);
						break;
					case Op.MemberDecorate:
						Require(words, 4, "OpMemberDecorate");
						ulong key = MemberKey(words[1], words[2]);
						if (!memberDecorations.TryGetValue(key, out DecorationSet ofMember))
						{
							ofMember = new DecorationSet();
							memberDecorations.Add(key, ofMember);
						}
						Apply(ofMember, words, 3);
						break;
				}
			}

			/// <summary>
			///     What kind of resource a variable is and what is known of it; null when it is none of the kinds
			///     <see cref="SpirvResourceKind" /> names.
			/// </summary>
			public SpirvResource DescribeResource(Variable variable)
			{
				uint typeId = PointeeOf(variable);
				TypeInfo type = TypeOf(typeId);

				// Arrays of resources: uniform sampler2D maps[4], a block with instances, textures without a count.
				int arrayLength = 0;
				bool runtimeArray = false;
				while (type.Opcode == Op.TypeArray || type.Opcode == Op.TypeRuntimeArray)
				{
					if (type.Opcode == Op.TypeRuntimeArray)
					{
						runtimeArray = true;
					}
					else
					{
						arrayLength = Math.Max(arrayLength, 1) * ArrayLength(typeId, type);
					}
					typeId = type.Element;
					type = TypeOf(typeId);
				}

				DecorationSet ofVariable = DecorationsOf(variable.Id);
				SpirvResource resource = new SpirvResource
				{
					VariableId = variable.Id,
					InstanceName = NameOf(variable.Id),
					Set = ofVariable.DescriptorSet,
					Binding = ofVariable.Binding,
					ArrayLength = runtimeArray ? 0 : arrayLength,
					IsRuntimeArray = runtimeArray,
					NonWritable = ofVariable.NonWritable,
					NonReadable = ofVariable.NonReadable
				};
				resource.Name = resource.InstanceName;

				switch (type.Opcode)
				{
					case Op.TypeStruct:
						// Before SPIR-V 1.3 a storage block is a BufferBlock in the Uniform storage class; since then
						// a Block in the StorageBuffer one.
						DecorationSet ofBlock = DecorationsOf(typeId);
						if (ofBlock.BufferBlock || variable.StorageClass == StorageClass.StorageBuffer)
						{
							resource.Kind = SpirvResourceKind.StorageBuffer;
							resource.BlockSize = StructSize(typeId, type);
						}
						else if (ofBlock.Block && variable.StorageClass == StorageClass.Uniform)
						{
							resource.Kind = SpirvResourceKind.UniformBuffer;
							resource.BlockSize = (StructSize(typeId, type) + 15) / 16 * 16;
							resource.Members = DescribeMembers(typeId, type, 0);
						}
						else
						{
							return null;
						}
						resource.Name = NameOf(typeId);
						resource.IsDefaultUniformBlock = resource.Kind == SpirvResourceKind.UniformBuffer && resource.Name == "gl_DefaultUniformBlock";
						// glslang decorates the members of a readonly / writeonly block, since SPIR-V 1.4 the variable too.
						resource.NonWritable |= EveryMember(typeId, type, Decoration.NonWritable);
						resource.NonReadable |= EveryMember(typeId, type, Decoration.NonReadable);
						return resource;
					case Op.TypeSampledImage:
						resource.Kind = SpirvResourceKind.CombinedImageSampler;
						return DescribeImage(resource, TypeOf(type.Element)) ? resource : null;
					case Op.TypeImage:
						resource.Kind = type.Sampled == 2 ? SpirvResourceKind.StorageImage : SpirvResourceKind.SeparateImage;
						return DescribeImage(resource, type) ? resource : null;
					case Op.TypeSampler:
						resource.Kind = SpirvResourceKind.SeparateSampler;
						return resource;
					default:
						return null;
				}
			}

			/// <summary>
			///     An input or output as the stage's interface has it, and whether it is a built-in (a variable with a
			///     BuiltIn decoration, or a block whose members have them). Also gives the structure type of a block
			///     whose members have locations of their own (0 otherwise).
			/// </summary>
			public SpirvInterfaceVariable DescribeInterface(Variable variable, SpirvStage stage, out bool builtIn, out uint locatedStruct)
			{
				locatedStruct = 0;
				DecorationSet ofVariable = DecorationsOf(variable.Id);
				builtIn = ofVariable.BuiltIn >= 0;
				uint typeId = PointeeOf(variable);
				TypeInfo type = TypeOf(typeId);

				// Geometry and tessellation stages have one value per vertex: an array around the type declared.
				bool array = type.Opcode == Op.TypeArray || type.Opcode == Op.TypeRuntimeArray;
				if (array && IsPerVertex(stage, variable.StorageClass) && !ofVariable.Patch && !IsPatchBlock(type))
				{
					typeId = type.Element;
					type = TypeOf(typeId);
				}
				uint valueTypeId = typeId;

				int arrayLength = 0;
				while (type.Opcode == Op.TypeArray)
				{
					arrayLength = Math.Max(arrayLength, 1) * ArrayLength(typeId, type);
					typeId = type.Element;
					type = TypeOf(typeId);
				}

				SpirvInterfaceVariable result = new SpirvInterfaceVariable
				{
					VariableId = variable.Id,
					Name = NameOf(variable.Id),
					Location = ofVariable.Location,
					Component = Math.Max(ofVariable.Component, 0),
					Type = NumericTypeOf(type),
					ArrayLength = arrayLength,
					Flat = ofVariable.Flat,
					BuiltIn = ofVariable.BuiltIn
				};
				if (type.Opcode == Op.TypeStruct)
				{
					int lowestLocation = -1;
					for (int i = 0; i < type.Members.Length; i++)
					{
						DecorationSet ofMember = MemberDecorationsOf(typeId, i);
						builtIn |= ofMember.BuiltIn >= 0;
						if (ofMember.Location >= 0 && (lowestLocation < 0 || ofMember.Location < lowestLocation))
						{
							lowestLocation = ofMember.Location;
						}
					}
					if (DecorationsOf(typeId).Block)
					{
						result.IsBlock = true;
						result.Name = NameOf(typeId);
						if (lowestLocation >= 0 && !builtIn)
						{
							// Locations on the members are the ones that count, whatever the variable has.
							result.Location = lowestLocation;
							locatedStruct = typeId;
						}
					}
				}
				if (builtIn)
				{
					result.Location = -1;
				}
				else
				{
					result.LocationCount = CountLocations(valueTypeId);
				}
				return result;
			}

			/// <summary>
			///     Finds what the functions' instructions name: which global variables (true at a variable's place in
			///     <see cref="Variables" />) and, into a set, which built-ins. Every operand that can be an id is looked
			///     at, so no way of using a variable is missed.
			/// </summary>
			public bool[] FindUsedVariables(uint[] functions, HashSet<int> usedBuiltIns)
			{
				bool[] used = new bool[Variables.Count];
				BuiltInVariable[] builtIns = new BuiltInVariable[Variables.Count];
				uint highestId = 0;
				for (int i = 0; i < Variables.Count; i++)
				{
					builtIns[i] = DescribeBuiltIn(Variables[i]);
					highestId = Math.Max(highestId, Variables[i].Id);
				}
				// By id, a variable's place in the list plus one; 0 for every other id.
				int[] places = new int[highestId + 1];
				for (int i = 0; i < Variables.Count; i++)
				{
					places[Variables[i].Id] = i + 1;
				}
				// A built-in that is no variable is a constant (gl_WorkGroupSize): there by being declared.
				foreach (KeyValuePair<uint, DecorationSet> decorated in decorations)
				{
					if (decorated.Value.BuiltIn >= 0 && (decorated.Key > highestId || places[decorated.Key] == 0))
					{
						usedBuiltIns.Add(decorated.Value.BuiltIn);
					}
				}

				int position = 0;
				while (position < functions.Length)
				{
					int length = (int)(functions[position] >> 16);
					int opcode = (int)(functions[position] & 0xFFFF);

					// Operands are ids but for the numbers some instructions carry: from a word on (to), or one
					// word among ids (number). A number taken for an id could name a variable that is not used.
					int from = 1;
					int to = length;
					int number = 0;
					switch (opcode)
					{
						case Op.SelectionMerge:
						case Op.LifetimeStart:
						case Op.LifetimeStop:
						case Op.Line:
							to = 2;
							break;
						case Op.Store:
						case Op.CopyMemory:
						case Op.LoopMerge:
						case Op.Switch:
							to = 3;
							break;
						case Op.Load:
						case Op.CopyMemorySized:
						case Op.ArrayLength:
						case Op.CompositeExtract:
						case Op.BranchConditional:
							to = 4;
							break;
						case Op.VectorShuffle:
						case Op.CompositeInsert:
							to = 5;
							break;
						case Op.Function:
						case Op.Variable:
							number = 3;
							break;
						case Op.ExtInst:
						case Op.ImageWrite:
							number = 4;
							break;
						case Op.ImageSampleImplicitLod:
						case Op.ImageSampleExplicitLod:
						case Op.ImageSampleProjImplicitLod:
						case Op.ImageSampleProjExplicitLod:
						case Op.ImageFetch:
						case Op.ImageRead:
						case Op.ImageSparseSampleImplicitLod:
						case Op.ImageSparseSampleExplicitLod:
						case Op.ImageSparseSampleProjImplicitLod:
						case Op.ImageSparseSampleProjExplicitLod:
						case Op.ImageSparseFetch:
						case Op.ImageSparseRead:
							number = 5;
							break;
						case Op.ImageSampleDrefImplicitLod:
						case Op.ImageSampleDrefExplicitLod:
						case Op.ImageSampleProjDrefImplicitLod:
						case Op.ImageSampleProjDrefExplicitLod:
						case Op.ImageGather:
						case Op.ImageDrefGather:
						case Op.ImageSparseSampleDrefImplicitLod:
						case Op.ImageSparseSampleDrefExplicitLod:
						case Op.ImageSparseSampleProjDrefImplicitLod:
						case Op.ImageSparseSampleProjDrefExplicitLod:
						case Op.ImageSparseGather:
						case Op.ImageSparseDrefGather:
							number = 6;
							break;
						case Op.AccessChain:
						case Op.InBoundsAccessChain:
							if (UseBuiltInMember(functions, position, length, 4, places, builtIns, used, usedBuiltIns))
							{
								from = 4;
							}
							break;
						case Op.PtrAccessChain:
						case Op.InBoundsPtrAccessChain:
							// One more operand before the indices: the element of the base pointer.
							if (UseBuiltInMember(functions, position, length, 5, places, builtIns, used, usedBuiltIns))
							{
								from = 4;
							}
							break;
					}
					if (to > length)
					{
						to = length;
					}
					for (int i = from; i < to; i++)
					{
						uint word = functions[position + i];
						if (i == number || word > highestId || places[word] == 0)
						{
							continue;
						}
						int place = places[word] - 1;
						used[place] = true;
						BuiltInVariable builtIn = builtIns[place];
						if (builtIn == null)
						{
							continue;
						}
						if (builtIn.Members == null)
						{
							usedBuiltIns.Add(builtIn.BuiltIn);
							continue;
						}
						// A block of built-ins named whole: all of them.
						foreach (int member in builtIn.Members)
						{
							if (member >= 0)
							{
								usedBuiltIns.Add(member);
							}
						}
					}
					position += length;
				}
				return used;
			}

			/// <summary>
			///     An access chain into a block of built-ins uses the one member its index names (gl_Position of
			///     gl_PerVertex), which comes after the indices of the arrays around the block (gl_in[i]). False when the
			///     chain is not that: its base is then looked at like any operand.
			/// </summary>
			private bool UseBuiltInMember(uint[] words, int position, int length, int firstIndex, int[] places, BuiltInVariable[] builtIns, bool[] used, HashSet<int> usedBuiltIns)
			{
				if (length < 4)
				{
					return false;
				}
				uint baseId = words[position + 3];
				if (baseId >= (uint)places.Length || places[baseId] == 0)
				{
					return false;
				}
				int place = places[baseId] - 1;
				BuiltInVariable builtIn = builtIns[place];
				int memberAt = firstIndex + (builtIn?.ArrayDepth ?? 0);
				if (builtIn?.Members == null || memberAt >= length || !constants.TryGetValue(words[position + memberAt], out uint member)
					|| member >= (uint)builtIn.Members.Length)
				{
					return false;
				}
				used[place] = true;
				if (builtIn.Members[member] >= 0)
				{
					usedBuiltIns.Add(builtIn.Members[member]);
				}
				return true;
			}

			/// <summary>What a variable has of built-ins; null when it has none.</summary>
			private BuiltInVariable DescribeBuiltIn(Variable variable)
			{
				if (variable.StorageClass != StorageClass.Input && variable.StorageClass != StorageClass.Output)
				{
					return null;
				}
				DecorationSet ofVariable = DecorationsOf(variable.Id);
				if (ofVariable.BuiltIn >= 0)
				{
					return new BuiltInVariable { BuiltIn = ofVariable.BuiltIn };
				}
				uint typeId = PointeeOf(variable);
				TypeInfo type = TypeOf(typeId);
				int arrayDepth = 0;
				while (type.Opcode == Op.TypeArray || type.Opcode == Op.TypeRuntimeArray)
				{
					arrayDepth++;
					typeId = type.Element;
					type = TypeOf(typeId);
				}
				if (type.Opcode != Op.TypeStruct)
				{
					return null;
				}
				int[] members = new int[type.Members.Length];
				bool any = false;
				for (int i = 0; i < members.Length; i++)
				{
					members[i] = MemberDecorationsOf(typeId, i).BuiltIn;
					any |= members[i] >= 0;
				}
				return any ? new BuiltInVariable { Members = members, ArrayDepth = arrayDepth } : null;
			}

			private bool DescribeImage(SpirvResource resource, TypeInfo image)
			{
				if (image.Opcode != Op.TypeImage || image.Dimension < 0 || image.Dimension > (int)SpirvImageDimension.SubpassData)
				{
					return false;
				}
				resource.Dimension = (SpirvImageDimension)image.Dimension;
				resource.Arrayed = image.Arrayed;
				resource.Shadow = image.Depth == 1;
				resource.Multisampled = image.Multisampled;
				resource.SampledType = NumericTypeOf(TypeOf(image.Element)).BaseType;
				resource.ImageFormat = image.Format;
				return true;
			}

			/// <summary>
			///     A structure's members as a block has them: numeric ones as they are, structures with their own members
			///     inside. Offsets count from the start of the block; those inside an array of structures are of its
			///     first element.
			/// </summary>
			private List<SpirvBlockMember> DescribeMembers(uint structId, TypeInfo structType, int baseOffset)
			{
				List<SpirvBlockMember> members = new List<SpirvBlockMember>(structType.Members.Length);
				for (int i = 0; i < structType.Members.Length; i++)
				{
					DecorationSet ofMember = MemberDecorationsOf(structId, i);
					SpirvBlockMember member = new SpirvBlockMember
					{
						Name = MemberNameOf(structId, i),
						Offset = baseOffset + Math.Max(ofMember.Offset, 0)
					};

					// An array of arrays is one array of all its elements: they lie one after another by the innermost stride.
					uint typeId = structType.Members[i];
					TypeInfo type = TypeOf(typeId);
					bool array = false;
					while (type.Opcode == Op.TypeArray || type.Opcode == Op.TypeRuntimeArray)
					{
						int length = type.Opcode == Op.TypeArray ? ArrayLength(typeId, type) : 0;
						member.ArrayLength = array ? member.ArrayLength * length : length;
						member.ArrayStride = DecorationsOf(typeId).ArrayStride;
						array = true;
						typeId = type.Element;
						type = TypeOf(typeId);
					}

					member.Type = NumericTypeOf(type);
					if (type.Opcode == Op.TypeStruct)
					{
						member.Members = DescribeMembers(typeId, type, member.Offset);
					}
					else if (member.Type.Columns > 1)
					{
						member.MatrixStride = ofMember.MatrixStride;
						member.RowMajor = ofMember.RowMajor;
					}
					members.Add(member);
				}
				return members;
			}

			/// <summary>The bytes a structure's members take: the furthest end of any, by their offsets.</summary>
			private int StructSize(uint structId, TypeInfo structType)
			{
				int size = 0;
				for (int i = 0; i < structType.Members.Length; i++)
				{
					DecorationSet ofMember = MemberDecorationsOf(structId, i);
					size = Math.Max(size, Math.Max(ofMember.Offset, 0) + SizeOf(structType.Members[i], ofMember));
				}
				return size;
			}

			/// <summary>
			///     The bytes a block's member of a type takes: 4 or 8 a component, a matrix its stride times its columns
			///     (rows when row-major), an array its stride times its length, none for an array without a size.
			/// </summary>
			private int SizeOf(uint typeId, DecorationSet ofMember)
			{
				TypeInfo type = TypeOf(typeId);
				switch (type.Opcode)
				{
					case Op.TypeStruct:
						return StructSize(typeId, type);
					case Op.TypeRuntimeArray:
						return 0;
					case Op.TypeArray:
						int stride = DecorationsOf(typeId).ArrayStride;
						return ArrayLength(typeId, type) * (stride > 0 ? stride : SizeOf(type.Element, ofMember));
				}
				SpirvNumericType numeric = NumericTypeOf(type);
				int component = numeric.Bits / 8;
				if (numeric.Columns <= 1)
				{
					return numeric.VectorSize * component;
				}
				int vectors = ofMember.RowMajor ? numeric.VectorSize : numeric.Columns;
				int vectorSize = (ofMember.RowMajor ? numeric.Columns : numeric.VectorSize) * component;
				return vectors * (ofMember.MatrixStride > 0 ? ofMember.MatrixStride : vectorSize);
			}

			/// <summary>
			///     How many locations a type takes in a stage's interface, by Vulkan's rules: one per scalar or vector,
			///     two for 64-bit vectors of 3 or 4 components, a matrix as its columns, an array as its elements, a
			///     structure as its members.
			/// </summary>
			private int CountLocations(uint typeId)
			{
				TypeInfo type = TypeOf(typeId);
				switch (type.Opcode)
				{
					case Op.TypeArray:
						return ArrayLength(typeId, type) * CountLocations(type.Element);
					case Op.TypeMatrix:
						return type.Count * CountLocations(type.Element);
					case Op.TypeVector:
						return type.Count > 2 && TypeOf(type.Element).Bits == 64 ? 2 : 1;
					case Op.TypeStruct:
						int count = 0;
						foreach (uint member in type.Members)
						{
							count += CountLocations(member);
						}
						return count;
					default:
						return 1;
				}
			}

			private SpirvNumericType NumericTypeOf(TypeInfo type)
			{
				switch (type.Opcode)
				{
					case Op.TypeBool:
						return new SpirvNumericType { BaseType = SpirvBaseType.Bool, VectorSize = 1, Columns = 1, Bits = 32 };
					case Op.TypeInt:
						return new SpirvNumericType { BaseType = type.Signed ? SpirvBaseType.Int : SpirvBaseType.UInt, VectorSize = 1, Columns = 1, Bits = type.Bits };
					case Op.TypeFloat:
						return new SpirvNumericType { BaseType = type.Bits == 64 ? SpirvBaseType.Double : SpirvBaseType.Float, VectorSize = 1, Columns = 1, Bits = type.Bits };
					case Op.TypeVector:
						SpirvNumericType vector = NumericTypeOf(TypeOf(type.Element));
						vector.VectorSize = type.Count;
						return vector;
					case Op.TypeMatrix:
						SpirvNumericType matrix = NumericTypeOf(TypeOf(type.Element));
						matrix.Columns = type.Count;
						return matrix;
					case Op.TypeStruct:
						return new SpirvNumericType { BaseType = SpirvBaseType.Struct };
					default:
						return default;
				}
			}

			/// <summary>
			///     Geometry and tessellation stages get their inputs once per vertex of the primitive or patch, and a
			///     tessellation control shader gives its outputs so, unless they are per patch.
			/// </summary>
			private static bool IsPerVertex(SpirvStage stage, int storageClass)
			{
				switch (stage)
				{
					case SpirvStage.TessControl:
						return true;
					case SpirvStage.Geometry:
					case SpirvStage.TessEvaluation:
						return storageClass == StorageClass.Input;
					default:
						return false;
				}
			}

			/// <summary>An array of interface blocks declared <c>patch</c>: glslang puts that on the members.</summary>
			private bool IsPatchBlock(TypeInfo type)
			{
				uint typeId = 0;
				while (type.Opcode == Op.TypeArray || type.Opcode == Op.TypeRuntimeArray)
				{
					typeId = type.Element;
					type = TypeOf(typeId);
				}
				return type.Opcode == Op.TypeStruct && EveryMember(typeId, type, Decoration.Patch);
			}

			/// <summary>Whether a structure has members and each of them one of the decorations that say how it is used.</summary>
			private bool EveryMember(uint structId, TypeInfo structType, int decoration)
			{
				for (int i = 0; i < structType.Members.Length; i++)
				{
					DecorationSet ofMember = MemberDecorationsOf(structId, i);
					bool has = decoration == Decoration.NonWritable ? ofMember.NonWritable
						: decoration == Decoration.NonReadable ? ofMember.NonReadable
						: ofMember.Patch;
					if (!has)
					{
						return false;
					}
				}
				return structType.Members.Length > 0;
			}

			private int ArrayLength(uint typeId, TypeInfo array)
			{
				if (!constants.TryGetValue(array.LengthId, out uint length))
				{
					throw new NotSupportedException(
						$"The length of SPIR-V array type {typeId} is no plain constant (id {array.LengthId}): lengths computed from specialization constants are not read.");
				}
				return (int)length;
			}

			/// <summary>The type a variable holds: what its pointer type points to.</summary>
			private uint PointeeOf(Variable variable)
			{
				TypeInfo pointer = TypeOf(variable.TypeId);
				if (pointer.Opcode != Op.TypePointer)
				{
					throw new InvalidDataException($"Malformed SPIR-V: the type of variable {variable.Id} (id {variable.TypeId}) is not a pointer type.");
				}
				return pointer.Element;
			}

			/// <summary>
			///     Takes a type declaration. What a type is made of has to be declared before it, as SPIR-V has it, so
			///     following a type's parts always ends. A pointer is the exception (it may point to a structure
			///     declared after it, which may hold the pointer) and is never followed past a variable's own.
			/// </summary>
			private void Declare(uint id, TypeInfo type)
			{
				if (type.Opcode != Op.TypePointer)
				{
					if (type.Element != 0 && !types.ContainsKey(type.Element))
					{
						namedUndeclared.Add(type.Element);
					}
					if (type.Members != null)
					{
						foreach (uint member in type.Members)
						{
							if (!types.ContainsKey(member))
							{
								namedUndeclared.Add(member);
							}
						}
					}
					if (namedUndeclared.Contains(id))
					{
						throw new InvalidDataException($"Malformed SPIR-V: type {id} is declared after a type that is made of it.");
					}
				}
				if (!types.TryAdd(id, type))
				{
					throw new InvalidDataException($"Malformed SPIR-V: type {id} is declared twice.");
				}
			}

			private TypeInfo TypeOf(uint id)
			{
				return types.TryGetValue(id, out TypeInfo type) ? type : TypeInfo.Unknown;
			}

			private string NameOf(uint id)
			{
				return names.TryGetValue(id, out string name) ? name : "";
			}

			private string MemberNameOf(uint structId, int member)
			{
				return memberNames.TryGetValue(MemberKey(structId, (uint)member), out string name) ? name : "";
			}

			private DecorationSet DecorationsOf(uint id)
			{
				return decorations.TryGetValue(id, out DecorationSet set) ? set : DecorationSet.None;
			}

			private DecorationSet MemberDecorationsOf(uint structId, int member)
			{
				return memberDecorations.TryGetValue(MemberKey(structId, (uint)member), out DecorationSet set) ? set : DecorationSet.None;
			}

			private static ulong MemberKey(uint structId, uint member)
			{
				return ((ulong)structId << 32) | member;
			}

			/// <summary>Takes one decoration into a set; the word at an index is the decoration, its value follows.</summary>
			private static void Apply(DecorationSet set, uint[] words, int at)
			{
				switch ((int)words[at])
				{
					case Decoration.Block:
						set.Block = true;
						break;
					case Decoration.BufferBlock:
						set.BufferBlock = true;
						break;
					case Decoration.RowMajor:
						set.RowMajor = true;
						break;
					case Decoration.Flat:
						set.Flat = true;
						break;
					case Decoration.Patch:
						set.Patch = true;
						break;
					case Decoration.NonWritable:
						set.NonWritable = true;
						break;
					case Decoration.NonReadable:
						set.NonReadable = true;
						break;
					case Decoration.ArrayStride:
						set.ArrayStride = ValueOf(words, at);
						break;
					case Decoration.MatrixStride:
						set.MatrixStride = ValueOf(words, at);
						break;
					case Decoration.BuiltIn:
						set.BuiltIn = ValueOf(words, at);
						break;
					case Decoration.Location:
						set.Location = ValueOf(words, at);
						break;
					case Decoration.Component:
						set.Component = ValueOf(words, at);
						break;
					case Decoration.Binding:
						set.Binding = ValueOf(words, at);
						break;
					case Decoration.DescriptorSet:
						set.DescriptorSet = ValueOf(words, at);
						break;
					case Decoration.Offset:
						set.Offset = ValueOf(words, at);
						break;
				}
			}

			private static int ValueOf(uint[] words, int decorationAt)
			{
				if (decorationAt + 1 >= words.Length)
				{
					throw new InvalidDataException($"Malformed SPIR-V: decoration {words[decorationAt]} of id {words[1]} has no value.");
				}
				return (int)words[decorationAt + 1];
			}

			private static void Require(uint[] words, int count, string instruction)
			{
				if (words.Length < count)
				{
					throw new InvalidDataException($"Malformed SPIR-V: {instruction} with {words.Length} words, it has at least {count}.");
				}
			}

			/// <summary>A string of an instruction: UTF-8 bytes packed four to a word, the first in the lowest byte, ended by a zero.</summary>
			private static string ReadString(uint[] words, int start)
			{
				int capacity = (words.Length - start) * 4;
				Span<byte> bytes = capacity <= 256 ? stackalloc byte[256] : new byte[capacity];
				int length = 0;
				for (int i = start; i < words.Length; i++)
				{
					uint word = words[i];
					for (int shift = 0; shift < 32; shift += 8)
					{
						byte value = (byte)(word >> shift);
						if (value == 0)
						{
							return Encoding.UTF8.GetString(bytes.Slice(0, length));
						}
						bytes[length++] = value;
					}
				}
				throw new InvalidDataException($"Malformed SPIR-V: a string of instruction {OpcodeOf(words)} does not end.");
			}
		}
	}
}
