using System;
using LELEngine.Mathematics;

namespace LELEngine.Graphics
{
	/// <summary>Which graphics API the <see cref="GraphicsDevice" /> runs on.</summary>
	public enum GraphicsApi
	{
		OpenGL = 0,
		Vulkan = 1
	}

	/// <summary>
	///     Texel formats every backend has (three-component 8 bit and float formats are not among them: no GPU
	///     stores those, upload four components).
	/// </summary>
	public enum TextureFormat
	{
		None,
		R8,
		RG8,
		RGBA8,

		/// <summary>RGBA8 whose colour is sRGB encoded: sampling returns linear values.</summary>
		SRGBA8,
		R16,
		RG16,
		RGBA16,
		R16F,
		RG16F,
		RGBA16F,
		R32F,
		RG32F,
		RGBA32F,
		R11G11B10F,
		R8UI,
		R16UI,
		R32UI,
		RGBA32UI,

		/// <summary>Depth of at least 24 bits (a device without a 24 bit format stores 32 bit floats).</summary>
		Depth24,
		Depth32F,
		BC1,
		BC1SRGB,
		BC1A,
		BC1ASRGB,
		BC2,
		BC2SRGB,
		BC3,
		BC3SRGB,
		BC4,
		BC5,
		BC6H,
		BC6HSigned,
		BC7,
		BC7SRGB
	}

	public enum TextureKind
	{
		Texture2D,
		Texture3D,
		Cube,
		Texture2DArray
	}

	/// <summary>What a texture will be used for; a use not named when it is made is an error later.</summary>
	[Flags]
	public enum TextureUsage
	{
		/// <summary>Read by shaders through a sampler.</summary>
		Sampled = 1,

		/// <summary>Read or written by shaders as an image (<see cref="CommandList.SetImage" />).</summary>
		Storage = 2,

		/// <summary>Rendered into: a colour or, with a depth format, the depth attachment of a render pass.</summary>
		Attachment = 4,

		/// <summary>
		///     With <see cref="Storage" />: a slice of the 3D texture is read or written by itself as a 2D image
		///     (<see cref="CommandList.SetImage" /> with a layer). Some GPUs lay out a 3D texture made for this slice by slice,
		///     which samples it slower: only textures that need it ask for it.
		/// </summary>
		SliceImages = 8
	}

	public struct TextureDescriptor
	{
		public TextureKind Kind;
		public TextureFormat Format;
		public int Width;
		public int Height;

		/// <summary>Slices of a 3D texture or layers of an array (1 otherwise; a cube has its six faces as layers).</summary>
		public int Depth;

		/// <summary>Mip levels; 0 makes the whole chain down to one texel.</summary>
		public int MipLevels;

		public TextureUsage Usage;

		/// <summary>How shaders sample it when a binding names no sampler (null: linear, clamped).</summary>
		public GpuSampler Sampler;

		public string Name;

		public static TextureDescriptor Texture2D(TextureFormat format, int width, int height, int mipLevels = 1, TextureUsage usage = TextureUsage.Sampled, string name = null)
		{
			return new TextureDescriptor { Kind = TextureKind.Texture2D, Format = format, Width = width, Height = height, Depth = 1, MipLevels = mipLevels, Usage = usage, Name = name };
		}

		public static TextureDescriptor Texture3D(TextureFormat format, int width, int height, int depth, int mipLevels = 1, TextureUsage usage = TextureUsage.Sampled, string name = null)
		{
			return new TextureDescriptor { Kind = TextureKind.Texture3D, Format = format, Width = width, Height = height, Depth = depth, MipLevels = mipLevels, Usage = usage, Name = name };
		}

		public static TextureDescriptor Texture2DArray(TextureFormat format, int width, int height, int layers, int mipLevels = 1, TextureUsage usage = TextureUsage.Sampled, string name = null)
		{
			return new TextureDescriptor { Kind = TextureKind.Texture2DArray, Format = format, Width = width, Height = height, Depth = layers, MipLevels = mipLevels, Usage = usage, Name = name };
		}

		public static TextureDescriptor Cube(TextureFormat format, int size, int mipLevels = 1, TextureUsage usage = TextureUsage.Sampled, string name = null)
		{
			return new TextureDescriptor { Kind = TextureKind.Cube, Format = format, Width = size, Height = size, Depth = 1, MipLevels = mipLevels, Usage = usage, Name = name };
		}
	}

	public enum SamplerFilter : byte
	{
		Nearest,
		Linear
	}

	public enum SamplerMipFilter : byte
	{
		/// <summary>Only the first mip level is read.</summary>
		None,
		Nearest,
		Linear
	}

	public enum SamplerAddress : byte
	{
		Repeat,
		ClampToEdge,
		MirroredRepeat,
		ClampToBorder
	}

	public enum SamplerBorder : byte
	{
		TransparentBlack,
		OpaqueBlack,
		OpaqueWhite
	}

	public enum CompareFunction : byte
	{
		Never,
		Less,
		Equal,
		LessEqual,
		Greater,
		NotEqual,
		GreaterEqual,
		Always
	}

	/// <summary>How a texture is sampled. Samplers are shared: <see cref="GraphicsDevice.GetSampler" /> gives one per description.</summary>
	public struct SamplerDescriptor : IEquatable<SamplerDescriptor>
	{
		public SamplerFilter Min;
		public SamplerFilter Mag;
		public SamplerMipFilter Mip;
		public SamplerAddress AddressU;
		public SamplerAddress AddressV;
		public SamplerAddress AddressW;
		public SamplerBorder Border;

		/// <summary>The sampler compares the texture's depth with a reference (sampler2DShadow).</summary>
		public bool Compare;

		public CompareFunction CompareFunction;

		/// <summary>1 or less: no anisotropic filtering.</summary>
		public float MaxAnisotropy;

		public SamplerDescriptor(SamplerFilter filter, SamplerMipFilter mip, SamplerAddress address)
		{
			Min = filter;
			Mag = filter;
			Mip = mip;
			AddressU = address;
			AddressV = address;
			AddressW = address;
			Border = SamplerBorder.TransparentBlack;
			Compare = false;
			CompareFunction = CompareFunction.LessEqual;
			MaxAnisotropy = 1f;
		}

		public bool Equals(SamplerDescriptor other)
		{
			return Min == other.Min && Mag == other.Mag && Mip == other.Mip && AddressU == other.AddressU && AddressV == other.AddressV && AddressW == other.AddressW
				&& Border == other.Border && Compare == other.Compare && CompareFunction == other.CompareFunction && MaxAnisotropy == other.MaxAnisotropy;
		}

		public override bool Equals(object obj)
		{
			return obj is SamplerDescriptor other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine((int)Min | (int)Mag << 4 | (int)Mip << 8, (int)AddressU | (int)AddressV << 4 | (int)AddressW << 8, (int)Border, Compare, (int)CompareFunction, MaxAnisotropy);
		}
	}

	/// <summary>What a buffer will be bound as; a use not named when it is made is an error later.</summary>
	[Flags]
	public enum BufferUsage
	{
		Vertex = 1,
		Index = 2,
		Uniform = 4,
		Storage = 8,

		/// <summary>Draw and dispatch arguments, and the draw counts of counted indirect draws.</summary>
		Indirect = 16,

		/// <summary>Read back often (<see cref="GpuBuffer.GetData" />): kept where the CPU reads fast.</summary>
		Readback = 32
	}

	public struct BufferDescriptor
	{
		/// <summary>Bytes; 0 leaves the buffer without storage until its first <see cref="GpuBuffer.SetData" />.</summary>
		public long Size;

		public BufferUsage Usage;

		/// <summary>Rewritten by the CPU every frame or so (kept where the CPU writes fast).</summary>
		public bool Dynamic;

		public string Name;

		public BufferDescriptor(BufferUsage usage, long size = 0, bool dynamic = false, string name = null)
		{
			Usage = usage;
			Size = size;
			Dynamic = dynamic;
			Name = name;
		}
	}

	public enum ShaderStage
	{
		Vertex,
		Fragment,
		Geometry,
		Compute,
		TessControl,
		TessEvaluation
	}

	/// <summary>Types of the values and resources a program takes by name.</summary>
	public enum UniformType
	{
		Unknown,
		Float,
		Vector2,
		Vector3,
		Vector4,
		Int,
		IntVector2,
		IntVector3,
		IntVector4,
		UInt,
		UIntVector2,
		UIntVector3,
		UIntVector4,
		Bool,
		BoolVector2,
		BoolVector3,
		BoolVector4,
		Matrix2,
		Matrix3,
		Matrix4,
		Sampler2D,
		Sampler3D,
		SamplerCube,
		Sampler2DArray,
		Sampler2DShadow,
		Sampler2DArrayShadow,
		SamplerCubeShadow,
		IntSampler2D,
		IntSampler3D,
		UIntSampler2D,
		UIntSampler3D,
		UIntSampler2DArray,
		Image
	}

	/// <summary>A value or sampled texture a program takes by name (<see cref="GpuProgram.Uniforms" />).</summary>
	public struct UniformInfo
	{
		public string Name;
		public UniformType Type;

		/// <summary>Elements of an array (1 otherwise).</summary>
		public int ArraySize;

		/// <summary>What the program's setters take; elements of an array follow at slot + index.</summary>
		public int Slot;

		public bool IsSampler => Type >= UniformType.Sampler2D && Type <= UniformType.UIntSampler2DArray;
	}

	public enum PrimitiveTopology : byte
	{
		Triangles,
		Lines,
		Points,
		TriangleStrip,
		LineStrip
	}

	public enum IndexFormat : byte
	{
		UInt16,
		UInt32
	}

	public enum FaceCulling : byte
	{
		None,
		Back,
		Front
	}

	public enum BlendFactor : byte
	{
		Zero,
		One,
		SrcColor,
		OneMinusSrcColor,
		DstColor,
		OneMinusDstColor,
		SrcAlpha,
		OneMinusSrcAlpha,
		DstAlpha,
		OneMinusDstAlpha,
		ConstantColor,
		OneMinusConstantColor,
		ConstantAlpha,
		OneMinusConstantAlpha,
		SrcAlphaSaturate
	}

	public enum BlendOperation : byte
	{
		Add,
		Subtract,
		ReverseSubtract,
		Min,
		Max
	}

	[Flags]
	public enum ColorWriteMask : byte
	{
		None = 0,
		Red = 1,
		Green = 2,
		Blue = 4,
		Alpha = 8,
		All = 15
	}

	/// <summary>What a render pass does with an attachment's contents when it begins.</summary>
	public enum LoadOperation : byte
	{
		/// <summary>Keeps what the attachment holds.</summary>
		Load,
		Clear,

		/// <summary>The contents are undefined until drawn (every pixel will be).</summary>
		DontCare
	}

	public enum ImageAccess : byte
	{
		Read,
		Write,
		ReadWrite
	}

	/// <summary>Components and type of a vertex attribute in a vertex buffer.</summary>
	public enum VertexFormat : byte
	{
		Float1,
		Float2,
		Float3,
		Float4,

		/// <summary>Four bytes read as 0..1 (colours).</summary>
		UByte4Normalized,
		Int1,
		Int2,
		Int3,
		Int4,
		UInt1,
		UInt2,
		UInt3,
		UInt4
	}

	public struct VertexAttributeDescriptor
	{
		/// <summary>The attribute's location in the vertex stage.</summary>
		public int Location;

		/// <summary>Which of the bound vertex buffers holds it.</summary>
		public int Buffer;

		public VertexFormat Format;

		/// <summary>Bytes from the start of a vertex.</summary>
		public int Offset;

		public VertexAttributeDescriptor(int location, VertexFormat format, int offset, int buffer = 0)
		{
			Location = location;
			Buffer = buffer;
			Format = format;
			Offset = offset;
		}
	}

	public struct VertexBufferDescriptor
	{
		/// <summary>Bytes from one vertex to the next.</summary>
		public int Stride;

		/// <summary>One element per instance instead of per vertex.</summary>
		public bool PerInstance;

		public VertexBufferDescriptor(int stride, bool perInstance = false)
		{
			Stride = stride;
			PerInstance = perInstance;
		}
	}

	/// <summary>A value an attachment or a texture is cleared to: floats for normalized and float formats, integers for integer ones.</summary>
	public struct ClearValue
	{
		public float R;
		public float G;
		public float B;
		public float A;
		public uint IntegerR;
		public uint IntegerG;
		public uint IntegerB;
		public uint IntegerA;

		public ClearValue(float r, float g, float b, float a)
		{
			R = r;
			G = g;
			B = b;
			A = a;
			IntegerR = IntegerG = IntegerB = IntegerA = 0;
		}

		public static ClearValue Integer(uint r, uint g = 0, uint b = 0, uint a = 0)
		{
			return new ClearValue { IntegerR = r, IntegerG = g, IntegerB = b, IntegerA = a };
		}

		public static implicit operator ClearValue(Color4 color)
		{
			return new ClearValue(color.R, color.G, color.B, color.A);
		}

		public static readonly ClearValue Zero = default;
		public static readonly ClearValue OpaqueBlack = new ClearValue(0f, 0f, 0f, 1f);
	}

	/// <summary>A colour attachment of a render pass: one mip level of one layer (or slice) of a texture.</summary>
	public struct ColorAttachment
	{
		public GpuTexture Texture;
		public int Mip;

		/// <summary>Layer of an array, face of a cube or slice of a 3D texture.</summary>
		public int Layer;

		public LoadOperation Load;
		public ClearValue Clear;

		public ColorAttachment(GpuTexture texture, LoadOperation load = LoadOperation.Load, ClearValue clear = default, int mip = 0, int layer = 0)
		{
			Texture = texture;
			Mip = mip;
			Layer = layer;
			Load = load;
			Clear = clear;
		}
	}

	public struct DepthAttachment
	{
		public GpuTexture Texture;
		public int Mip;
		public int Layer;
		public LoadOperation Load;
		public float Clear;

		public DepthAttachment(GpuTexture texture, LoadOperation load = LoadOperation.Load, float clear = 1f, int mip = 0, int layer = 0)
		{
			Texture = texture;
			Mip = mip;
			Layer = layer;
			Load = load;
			Clear = clear;
		}
	}

	/// <summary>
	///     What a render pass draws into. Without attachments it has a size of its own (<see cref="Width" />,
	///     <see cref="Height" />): draws then only run their shaders (which write images and buffers).
	/// </summary>
	public ref struct RenderPassDescriptor
	{
		public ReadOnlySpan<ColorAttachment> Colors;
		public DepthAttachment Depth;

		/// <summary>Size of a pass without attachments.</summary>
		public int Width;

		public int Height;

		/// <summary>
		///     Samples a pixel of a pass without attachments (0 or 1: one): fragments run per covered sample position
		///     set (gl_SampleMaskIn), up to <see cref="DeviceCapabilities.MaxPassSamples" />.
		/// </summary>
		public int Samples;
	}

	/// <summary>
	///     The fixed-function state draws run with: part of the pipeline a draw is made with (the program, the vertex
	///     layout, the topology and the pass's attachment formats are the rest).
	/// </summary>
	public struct RenderState : IEquatable<RenderState>
	{
		public bool DepthTest;
		public bool DepthWrite;
		public CompareFunction DepthCompare;
		public FaceCulling Culling;

		/// <summary>Triangles drawn as their edges.</summary>
		public bool Wireframe;

		public bool Blend;
		public BlendFactor SourceColor;
		public BlendFactor DestinationColor;
		public BlendFactor SourceAlpha;
		public BlendFactor DestinationAlpha;
		public BlendOperation ColorOperation;
		public BlendOperation AlphaOperation;
		public ColorWriteMask ColorMask;

		/// <summary>Depth offset by slope (glPolygonOffset's factor); both 0: none.</summary>
		public float DepthBiasSlope;

		/// <summary>Depth offset in the depth buffer's smallest steps (glPolygonOffset's units).</summary>
		public float DepthBiasConstant;

		/// <summary>Depth tested and written with Less, back faces culled, no blending, every channel written.</summary>
		public static RenderState Default => new RenderState
		{
			DepthTest = true,
			DepthWrite = true,
			DepthCompare = CompareFunction.Less,
			Culling = FaceCulling.Back,
			SourceColor = BlendFactor.One,
			DestinationColor = BlendFactor.Zero,
			SourceAlpha = BlendFactor.One,
			DestinationAlpha = BlendFactor.Zero,
			ColorMask = ColorWriteMask.All
		};

		/// <summary>No depth test or write, no culling, no blending: what full-screen passes draw with.</summary>
		public static RenderState Fullscreen
		{
			get
			{
				RenderState state = Default;
				state.DepthTest = false;
				state.DepthWrite = false;
				state.Culling = FaceCulling.None;
				return state;
			}
		}

		public void SetDepth(bool test, bool write, CompareFunction compare = CompareFunction.Less)
		{
			DepthTest = test;
			DepthWrite = write;
			DepthCompare = compare;
		}

		/// <summary>Blending with one pair of factors for colour and alpha.</summary>
		public void SetBlend(BlendFactor source, BlendFactor destination)
		{
			Blend = true;
			SourceColor = source;
			DestinationColor = destination;
			SourceAlpha = source;
			DestinationAlpha = destination;
		}

		public void SetBlend(BlendFactor sourceColor, BlendFactor destinationColor, BlendFactor sourceAlpha, BlendFactor destinationAlpha)
		{
			Blend = true;
			SourceColor = sourceColor;
			DestinationColor = destinationColor;
			SourceAlpha = sourceAlpha;
			DestinationAlpha = destinationAlpha;
		}

		public bool Equals(RenderState other)
		{
			return DepthTest == other.DepthTest && DepthWrite == other.DepthWrite && DepthCompare == other.DepthCompare && Culling == other.Culling && Wireframe == other.Wireframe
				&& Blend == other.Blend && SourceColor == other.SourceColor && DestinationColor == other.DestinationColor && SourceAlpha == other.SourceAlpha
				&& DestinationAlpha == other.DestinationAlpha && ColorOperation == other.ColorOperation && AlphaOperation == other.AlphaOperation && ColorMask == other.ColorMask
				&& DepthBiasSlope == other.DepthBiasSlope && DepthBiasConstant == other.DepthBiasConstant;
		}

		public override bool Equals(object obj)
		{
			return obj is RenderState other && Equals(other);
		}

		public override int GetHashCode()
		{
			HashCode hash = new HashCode();
			hash.Add((DepthTest ? 1 : 0) | (DepthWrite ? 2 : 0) | (Wireframe ? 4 : 0) | (Blend ? 8 : 0) | (int)DepthCompare << 4 | (int)Culling << 8 | (int)ColorMask << 12);
			hash.Add((int)SourceColor | (int)DestinationColor << 4 | (int)SourceAlpha << 8 | (int)DestinationAlpha << 12 | (int)ColorOperation << 16 | (int)AlphaOperation << 20);
			hash.Add(DepthBiasSlope);
			hash.Add(DepthBiasConstant);
			return hash.ToHashCode();
		}
	}

	/// <summary>Sizes and tests of texel formats.</summary>
	public static class TextureFormats
	{
		public static bool IsDepth(this TextureFormat format)
		{
			return format == TextureFormat.Depth24 || format == TextureFormat.Depth32F;
		}

		public static bool IsCompressed(this TextureFormat format)
		{
			return format >= TextureFormat.BC1;
		}

		public static bool IsInteger(this TextureFormat format)
		{
			return format >= TextureFormat.R8UI && format <= TextureFormat.RGBA32UI;
		}

		public static bool IsSrgb(this TextureFormat format)
		{
			switch (format)
			{
				case TextureFormat.SRGBA8:
				case TextureFormat.BC1SRGB:
				case TextureFormat.BC1ASRGB:
				case TextureFormat.BC2SRGB:
				case TextureFormat.BC3SRGB:
				case TextureFormat.BC7SRGB:
					return true;
				default:
					return false;
			}
		}

		/// <summary>Bytes of a texel; of a 4 x 4 block for compressed formats.</summary>
		public static int TexelSize(this TextureFormat format)
		{
			switch (format)
			{
				case TextureFormat.R8:
				case TextureFormat.R8UI:
					return 1;
				case TextureFormat.RG8:
				case TextureFormat.R16:
				case TextureFormat.R16F:
				case TextureFormat.R16UI:
					return 2;
				case TextureFormat.RGBA8:
				case TextureFormat.SRGBA8:
				case TextureFormat.RG16:
				case TextureFormat.RG16F:
				case TextureFormat.R32F:
				case TextureFormat.R32UI:
				case TextureFormat.R11G11B10F:
				case TextureFormat.Depth24:
				case TextureFormat.Depth32F:
					return 4;
				case TextureFormat.RGBA16:
				case TextureFormat.RGBA16F:
				case TextureFormat.RG32F:
					return 8;
				case TextureFormat.RGBA32F:
				case TextureFormat.RGBA32UI:
					return 16;
				case TextureFormat.BC1:
				case TextureFormat.BC1SRGB:
				case TextureFormat.BC1A:
				case TextureFormat.BC1ASRGB:
				case TextureFormat.BC4:
					return 8;
				case TextureFormat.BC2:
				case TextureFormat.BC2SRGB:
				case TextureFormat.BC3:
				case TextureFormat.BC3SRGB:
				case TextureFormat.BC5:
				case TextureFormat.BC6H:
				case TextureFormat.BC6HSigned:
				case TextureFormat.BC7:
				case TextureFormat.BC7SRGB:
					return 16;
				default:
					return 0;
			}
		}

		/// <summary>Components a texel has (depth formats: 1).</summary>
		public static int ComponentCount(this TextureFormat format)
		{
			switch (format)
			{
				case TextureFormat.R8:
				case TextureFormat.R16:
				case TextureFormat.R16F:
				case TextureFormat.R32F:
				case TextureFormat.R8UI:
				case TextureFormat.R16UI:
				case TextureFormat.R32UI:
				case TextureFormat.Depth24:
				case TextureFormat.Depth32F:
				case TextureFormat.BC4:
					return 1;
				case TextureFormat.RG8:
				case TextureFormat.RG16:
				case TextureFormat.RG16F:
				case TextureFormat.RG32F:
				case TextureFormat.BC5:
					return 2;
				case TextureFormat.R11G11B10F:
				case TextureFormat.BC1:
				case TextureFormat.BC1SRGB:
				case TextureFormat.BC6H:
				case TextureFormat.BC6HSigned:
					return 3;
				default:
					return 4;
			}
		}

		/// <summary>Bytes of one mip level of the given size, rows packed without padding.</summary>
		public static long LevelSize(this TextureFormat format, int width, int height, int depth = 1)
		{
			if (format.IsCompressed())
			{
				return (long)((width + 3) / 4) * ((height + 3) / 4) * depth * format.TexelSize();
			}

			return (long)width * height * depth * format.TexelSize();
		}

		/// <summary>Levels of a whole mip chain.</summary>
		public static int MipCount(int width, int height, int depth = 1)
		{
			int size = Math.Max(width, Math.Max(height, depth));
			int levels = 1;
			while (size > 1)
			{
				size >>= 1;
				levels++;
			}

			return levels;
		}

		/// <summary>Size of a mip level along one axis.</summary>
		public static int MipSize(int size, int level)
		{
			return Math.Max(1, size >> level);
		}
	}
}
