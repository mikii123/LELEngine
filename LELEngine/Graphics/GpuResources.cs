using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LELEngine.Mathematics;

namespace LELEngine.Graphics
{
	/// <summary>How a texture is sampled; shared, never disposed (<see cref="GraphicsDevice.GetSampler" />).</summary>
	public abstract class GpuSampler
	{
		public SamplerDescriptor Descriptor { get; protected set; }
	}

	/// <summary>
	///     A texture on the GPU. Its size, format and levels are fixed when it is made
	///     (<see cref="GraphicsDevice.CreateTexture" />). The first row of a level is the lowest (v = 0).
	/// </summary>
	public abstract unsafe class GpuTexture : IDisposable
	{
		private static readonly object idSync = new object();
		private static readonly Dictionary<int, WeakReference<GpuTexture>> byId = new Dictionary<int, WeakReference<GpuTexture>>();
		private static int lastId;

		protected GpuTexture(in TextureDescriptor descriptor)
		{
			Kind = descriptor.Kind;
			Format = descriptor.Format;
			Width = Math.Max(1, descriptor.Width);
			Height = Math.Max(1, descriptor.Height);
			Depth = descriptor.Kind == TextureKind.Cube ? 6 : Math.Max(1, descriptor.Depth);
			int depthForMips = descriptor.Kind == TextureKind.Texture3D ? Depth : 1;
			int full = TextureFormats.MipCount(Width, Height, depthForMips);
			MipLevels = descriptor.MipLevels <= 0 ? full : Math.Min(descriptor.MipLevels, full);
			Usage = descriptor.Usage == 0 ? TextureUsage.Sampled : descriptor.Usage;
			Sampler = descriptor.Sampler;
			Name = descriptor.Name;
			lock (idSync)
			{
				Id = ++lastId;
				byId[Id] = new WeakReference<GpuTexture>(this);
			}
		}

		public TextureKind Kind { get; }
		public TextureFormat Format { get; }
		public int Width { get; }
		public int Height { get; }

		/// <summary>Slices of a 3D texture, layers of an array, 6 for a cube, 1 otherwise.</summary>
		public int Depth { get; }

		public int MipLevels { get; }
		public TextureUsage Usage { get; }
		public string Name { get; set; }

		/// <summary>How shaders sample the texture when a binding names no sampler (null: linear, clamped).</summary>
		public GpuSampler Sampler { get; set; }

		/// <summary>A number of this texture alone, for systems that name textures by number (UI draw lists).</summary>
		public int Id { get; }

		public bool IsDisposed { get; protected set; }

		/// <summary>Layers a view of the texture has (array layers or cube faces; 1 for 2D and 3D textures).</summary>
		public int Layers => Kind == TextureKind.Texture2DArray || Kind == TextureKind.Cube ? Depth : 1;

		/// <summary>The window's back buffer: only a render pass's single colour target, a blit's destination or a read's source.</summary>
		public virtual bool IsBackbuffer => false;

		public static GpuTexture FromId(int id)
		{
			lock (idSync)
			{
				return byId.TryGetValue(id, out WeakReference<GpuTexture> reference) && reference.TryGetTarget(out GpuTexture texture) && !texture.IsDisposed ? texture : null;
			}
		}

		/// <summary>The textures alive now (for reports of what the GPU's memory holds).</summary>
		internal static List<GpuTexture> Alive()
		{
			List<GpuTexture> alive = new List<GpuTexture>();
			lock (idSync)
			{
				foreach (WeakReference<GpuTexture> reference in byId.Values)
				{
					if (reference.TryGetTarget(out GpuTexture texture) && !texture.IsDisposed)
					{
						alive.Add(texture);
					}
				}
			}

			return alive;
		}

		/// <summary>
		///     Writes texels of the texture's own format (rows packed, no padding) into a box of a mip level;
		///     <paramref name="z" /> and <paramref name="depth" /> are slices of a 3D texture, layers of an array or
		///     faces of a cube. Ordered with the commands: draws given before see the old texels.
		/// </summary>
		public abstract void SetData(int mip, int x, int y, int z, int width, int height, int depth, void* data, long size);

		/// <summary>
		///     Fills every mip level below the first by filtering the level above, as part of uploading the texture:
		///     ordered with the commands like <see cref="SetData(int, int, int, int, int, int, int, void*, long)" />, and like
		///     it allowed inside a render pass (a texture made and filled the moment a draw first needs it).
		/// </summary>
		public abstract void GenerateMipmaps();

		/// <summary>Reads a box of a mip level in the texture's own format (rows packed); waits for the GPU.</summary>
		public abstract void GetData(int mip, int x, int y, int z, int width, int height, int depth, void* destination, long size);

		/// <summary>Frees the texture once the GPU no longer uses it.</summary>
		public abstract void Dispose();

		protected void Forget()
		{
			IsDisposed = true;
			lock (idSync)
			{
				byId.Remove(Id);
			}
		}

		public int MipWidth(int mip)
		{
			return TextureFormats.MipSize(Width, mip);
		}

		public int MipHeight(int mip)
		{
			return TextureFormats.MipSize(Height, mip);
		}

		/// <summary>Slices of a mip level of a 3D texture; the layer count otherwise.</summary>
		public int MipDepth(int mip)
		{
			return Kind == TextureKind.Texture3D ? TextureFormats.MipSize(Depth, mip) : Depth;
		}

		/// <summary>Bytes of a whole mip level (every slice or layer).</summary>
		public long MipDataSize(int mip)
		{
			return Format.LevelSize(MipWidth(mip), MipHeight(mip), MipDepth(mip));
		}

		/// <summary>Writes a whole mip level (every slice or layer).</summary>
		public void SetData(int mip, void* data, long size)
		{
			SetData(mip, 0, 0, 0, MipWidth(mip), MipHeight(mip), MipDepth(mip), data, size);
		}

		public void SetData<T>(int mip, ReadOnlySpan<T> data)
			where T : unmanaged
		{
			fixed (T* pointer = data)
			{
				SetData(mip, pointer, (long)data.Length * sizeof(T));
			}
		}

		public void SetData<T>(int mip, T[] data)
			where T : unmanaged
		{
			SetData(mip, new ReadOnlySpan<T>(data));
		}

		/// <summary>Writes one layer, face or slice of a mip level.</summary>
		public void SetLayerData<T>(int mip, int layer, ReadOnlySpan<T> data)
			where T : unmanaged
		{
			fixed (T* pointer = data)
			{
				SetData(mip, 0, 0, layer, MipWidth(mip), MipHeight(mip), 1, pointer, (long)data.Length * sizeof(T));
			}
		}

		public void SetData<T>(int mip, int x, int y, int z, int width, int height, int depth, ReadOnlySpan<T> data)
			where T : unmanaged
		{
			fixed (T* pointer = data)
			{
				SetData(mip, x, y, z, width, height, depth, pointer, (long)data.Length * sizeof(T));
			}
		}

		/// <summary>Reads a whole mip level (every slice or layer).</summary>
		public void GetData<T>(int mip, Span<T> destination)
			where T : unmanaged
		{
			fixed (T* pointer = destination)
			{
				GetData(mip, 0, 0, 0, MipWidth(mip), MipHeight(mip), MipDepth(mip), pointer, (long)destination.Length * sizeof(T));
			}
		}

		public void GetData<T>(int mip, T[] destination)
			where T : unmanaged
		{
			GetData(mip, new Span<T>(destination));
		}

		public void GetData<T>(int mip, int x, int y, int z, int width, int height, int depth, Span<T> destination)
			where T : unmanaged
		{
			fixed (T* pointer = destination)
			{
				GetData(mip, x, y, z, width, height, depth, pointer, (long)destination.Length * sizeof(T));
			}
		}
	}

	/// <summary>A buffer on the GPU: vertices, indices, uniform and storage blocks, indirect arguments.</summary>
	public abstract unsafe class GpuBuffer : IDisposable
	{
		protected GpuBuffer(in BufferDescriptor descriptor)
		{
			Usage = descriptor.Usage;
			Dynamic = descriptor.Dynamic;
			Name = descriptor.Name;
		}

		/// <summary>Bytes of storage (0 before the first <see cref="SetData(void*, long)" /> of a buffer made without a size).</summary>
		public long Size { get; protected set; }

		public BufferUsage Usage { get; }
		public bool Dynamic { get; }
		public string Name { get; set; }
		public bool IsDisposed { get; protected set; }

		/// <summary>
		///     Replaces the buffer's storage and contents: <paramref name="size" /> bytes from <paramref name="data" />
		///     (null: undefined contents). Draws given before keep what they saw.
		/// </summary>
		public abstract void SetData(void* data, long size);

		/// <summary>Writes a part of the buffer, ordered with the commands: draws given before see the old bytes.</summary>
		public abstract void SetSubData(long offset, void* data, long size);

		/// <summary>
		///     New storage of <paramref name="size" /> bytes whose first <paramref name="keep" /> bytes are the old
		///     contents (the rest is undefined). Ordered with the commands like an upload, and like one it may be given
		///     inside a render pass.
		/// </summary>
		public abstract void Resize(long size, long keep);

		/// <summary>Reads bytes back; waits until the GPU has executed the commands that write the buffer.</summary>
		public abstract void GetData(long offset, void* destination, long size);

		/// <summary>Frees the buffer once the GPU no longer uses it.</summary>
		public abstract void Dispose();

		/// <summary>New storage of a size with undefined contents.</summary>
		public void Allocate(long size)
		{
			SetData(null, size);
		}

		public void SetData<T>(ReadOnlySpan<T> data)
			where T : unmanaged
		{
			fixed (T* pointer = data)
			{
				SetData(pointer, (long)data.Length * sizeof(T));
			}
		}

		public void SetData<T>(T[] data)
			where T : unmanaged
		{
			SetData(new ReadOnlySpan<T>(data));
		}

		public void SetData<T>(T[] data, int count)
			where T : unmanaged
		{
			SetData(new ReadOnlySpan<T>(data, 0, count));
		}

		/// <summary>Storage of <paramref name="capacity" /> bytes whose start holds the data (the rest is undefined).</summary>
		public void SetData<T>(ReadOnlySpan<T> data, long capacity)
			where T : unmanaged
		{
			long bytes = (long)data.Length * sizeof(T);
			if (capacity <= bytes)
			{
				SetData(data);
				return;
			}

			SetData(null, capacity);
			SetSubData(0, data);
		}

		public void SetSubData<T>(long offset, ReadOnlySpan<T> data)
			where T : unmanaged
		{
			if (data.Length == 0)
			{
				return;
			}

			fixed (T* pointer = data)
			{
				SetSubData(offset, pointer, (long)data.Length * sizeof(T));
			}
		}

		public void SetSubData<T>(long offset, T[] data, int count)
			where T : unmanaged
		{
			SetSubData(offset, new ReadOnlySpan<T>(data, 0, count));
		}

		public void SetSubData<T>(long offset, T[] data)
			where T : unmanaged
		{
			SetSubData(offset, new ReadOnlySpan<T>(data));
		}

		public void SetSubData<T>(long offset, in T value)
			where T : unmanaged
		{
			fixed (T* pointer = &value)
			{
				SetSubData(offset, pointer, sizeof(T));
			}
		}

		public void GetData<T>(long offset, Span<T> destination)
			where T : unmanaged
		{
			if (destination.Length == 0)
			{
				return;
			}

			fixed (T* pointer = destination)
			{
				GetData(offset, pointer, (long)destination.Length * sizeof(T));
			}
		}

		public void GetData<T>(long offset, T[] destination, int count)
			where T : unmanaged
		{
			GetData(offset, new Span<T>(destination, 0, count));
		}

		public void GetData<T>(long offset, T[] destination)
			where T : unmanaged
		{
			GetData(offset, new Span<T>(destination));
		}
	}

	/// <summary>Signals when the GPU has executed every command given before it was made (<see cref="GraphicsDevice.CreateFence" />).</summary>
	public abstract class GpuFence : IDisposable
	{
		/// <summary>The commands have run. Does not wait; commands not yet sent to the GPU go with the next present or flush.</summary>
		public abstract bool IsSignaled { get; }

		/// <summary>Sends the commands and waits for them, at most the given time; true when they have run.</summary>
		public abstract bool Wait(long timeoutNanoseconds);

		public abstract void Dispose();
	}

	/// <summary>A moment of the GPU's clock, written by <see cref="CommandList.WriteTimestamp" /> and read some frames later.</summary>
	public abstract class GpuTimestamp : IDisposable
	{
		/// <summary>The GPU has written the stamp given last.</summary>
		public abstract bool IsAvailable { get; }

		/// <summary>Nanoseconds of the GPU's clock (differences between stamps are what counts); waits when not available.</summary>
		public abstract long Nanoseconds { get; }

		public abstract void Dispose();
	}

	/// <summary>
	///     An acceleration structure: triangles (bottom level) or instances of bottom levels (top level) in the form the
	///     GPU traces rays through (<see cref="GraphicsDevice.CreateAccelerationStructure" />). Built by commands
	///     (<see cref="CommandList.BuildBottomLevels" />, <see cref="CommandList.BuildTopLevel" />); shaders trace a top
	///     level bound by number (<see cref="CommandList.SetAccelerationStructure" />). Only on devices with
	///     <see cref="DeviceCapabilities.RayQuery" />.
	/// </summary>
	public abstract class GpuAccelerationStructure : IDisposable
	{
		protected GpuAccelerationStructure(AccelerationStructureLevel level, AccelerationStructureFlags flags, string name)
		{
			Level = level;
			Flags = flags;
			Name = name;
		}

		public AccelerationStructureLevel Level { get; }
		public AccelerationStructureFlags Flags { get; }
		public string Name { get; set; }

		/// <summary>Bytes the structure takes on the GPU.</summary>
		public long Size { get; protected set; }

		/// <summary>A top level: the most instances a build gives it (0 for a bottom level).</summary>
		public int MaxInstances { get; protected set; }

		/// <summary>A build or a copy into it has been given: rays may trace it (and an update may refit it).</summary>
		public bool IsBuilt { get; protected set; }

		public bool IsDisposed { get; protected set; }

		/// <summary>
		///     Bytes a compacted copy of the structure takes (<see cref="AccelerationStructureFlags.AllowCompaction" />):
		///     known once the GPU has run its last build, 0 before and for a structure made without the flag. Never waits.
		/// </summary>
		public abstract long CompactedSize { get; }

		/// <summary>Frees the structure once the GPU no longer uses it.</summary>
		public abstract void Dispose();
	}

	/// <summary>Layout of the vertex buffers a draw reads: part of the pipeline. Immutable; make one per layout and keep it.</summary>
	public sealed class VertexInputLayout
	{
		private static int lastId;

		/// <summary>No vertex buffers: the vertex stage makes its positions from the vertex index.</summary>
		public static readonly VertexInputLayout Empty = new VertexInputLayout(Array.Empty<VertexBufferDescriptor>(), Array.Empty<VertexAttributeDescriptor>());

		public VertexInputLayout(VertexBufferDescriptor[] buffers, VertexAttributeDescriptor[] attributes)
		{
			Buffers = buffers;
			Attributes = attributes;
			Id = System.Threading.Interlocked.Increment(ref lastId);
		}

		/// <summary>One vertex buffer of the given stride.</summary>
		public VertexInputLayout(int stride, params VertexAttributeDescriptor[] attributes)
			: this(new[] { new VertexBufferDescriptor(stride) }, attributes)
		{ }

		public VertexBufferDescriptor[] Buffers { get; }
		public VertexAttributeDescriptor[] Attributes { get; }

		/// <summary>A number of this layout alone (pipeline keys).</summary>
		public int Id { get; }
	}

	/// <summary>Sources of a program: one GLSL text per stage (complete: version line, defines, includes resolved).</summary>
	public sealed class ProgramDescriptor
	{
		public string Name;
		public readonly List<KeyValuePair<ShaderStage, string>> Stages = new List<KeyValuePair<ShaderStage, string>>();

		/// <summary>Locations of vertex attributes by name, for attributes whose source gives none.</summary>
		public readonly List<KeyValuePair<string, int>> AttributeLocations = new List<KeyValuePair<string, int>>();

		/// <summary>
		///     Uniforms (loose ones, by name) the caller sets for nearly every draw, such as an object's world matrix: a device
		///     keeps them where a change between draws costs least (Vulkan: push constants, as many as fit; a program whose
		///     loose uniforms all fit is given them all without being asked). OpenGL sets every loose uniform as such already.
		///     What a draw reads is the same either way.
		/// </summary>
		public readonly List<string> PerDrawUniforms = new List<string>();

		/// <summary>The device may compile on other threads: the program is ready when <see cref="GpuProgram.IsReady" /> says so.</summary>
		public bool Asynchronous = true;
	}

	/// <summary>
	///     Linked shader stages. A program holds the values of its uniforms (set by slot; a draw uses the values
	///     held when it is given) and the textures of its samplers.
	/// </summary>
	public abstract unsafe class GpuProgram : IDisposable
	{
		public string Name { get; protected set; }

		/// <summary>The build has ended (linked or failed) or would end without waiting; never blocks.</summary>
		public abstract bool IsReady { get; }

		/// <summary>Waits for the build, then reads its results (<see cref="IsLinked" />, <see cref="Log" />, <see cref="Uniforms" />). Idempotent.</summary>
		public abstract void Finish();

		/// <summary>Valid after <see cref="Finish" />.</summary>
		public bool IsLinked { get; protected set; }

		/// <summary>The compiler's and linker's messages (after <see cref="Finish" />).</summary>
		public string Log { get; protected set; } = "";

		/// <summary>The messages of each stage that failed to compile, by stage.</summary>
		public IReadOnlyList<KeyValuePair<ShaderStage, string>> StageErrors => stageErrors;

		protected readonly List<KeyValuePair<ShaderStage, string>> stageErrors = new List<KeyValuePair<ShaderStage, string>>();

		public bool IsCompute { get; protected set; }

		/// <summary>The values and sampled textures the program takes by name (after <see cref="Finish" />).</summary>
		public IReadOnlyList<UniformInfo> Uniforms => uniforms;

		protected readonly List<UniformInfo> uniforms = new List<UniformInfo>();

		public bool IsDisposed { get; protected set; }

		/// <summary>The slot of a uniform ("name", "array[3]", "block.member"), -1 when the program has none of that name.</summary>
		public abstract int GetUniformSlot(string name);

		/// <summary>The location of a vertex attribute, -1 when the vertex stage does not read it.</summary>
		public abstract int GetAttributeLocation(string name);

		/// <summary><paramref name="count" /> elements of <paramref name="components" /> floats each, from the slot on.</summary>
		public abstract void SetFloats(int slot, float* values, int components, int count);

		public abstract void SetInts(int slot, int* values, int components, int count);
		public abstract void SetUInts(int slot, uint* values, int components, int count);

		/// <summary><paramref name="count" /> 4 x 4 matrices of 16 floats as they lie in memory.</summary>
		public abstract void SetMatrices(int slot, float* values, int count);

		/// <summary>The texture a sampler reads (null: none) and how; a null sampler takes the texture's own.</summary>
		public abstract void SetTexture(int slot, GpuTexture texture, GpuSampler sampler = null);

		/// <summary>The program in a form <see cref="GraphicsDevice.CreateProgramFromBinary" /> takes back, or null.</summary>
		public abstract byte[] GetBinary();

		public abstract void Dispose();

		public void SetInt(int slot, int value)
		{
			if (slot >= 0)
			{
				SetInts(slot, &value, 1, 1);
			}
		}

		public void SetUInt(int slot, uint value)
		{
			if (slot >= 0)
			{
				SetUInts(slot, &value, 1, 1);
			}
		}

		public void SetFloat(int slot, float value)
		{
			if (slot >= 0)
			{
				SetFloats(slot, &value, 1, 1);
			}
		}

		public void SetInt2(int slot, int x, int y)
		{
			if (slot >= 0)
			{
				int* values = stackalloc int[2] { x, y };
				SetInts(slot, values, 2, 1);
			}
		}

		public void SetInt3(int slot, int x, int y, int z)
		{
			if (slot >= 0)
			{
				int* values = stackalloc int[3] { x, y, z };
				SetInts(slot, values, 3, 1);
			}
		}

		public void SetInt4(int slot, int x, int y, int z, int w)
		{
			if (slot >= 0)
			{
				int* values = stackalloc int[4] { x, y, z, w };
				SetInts(slot, values, 4, 1);
			}
		}

		public void SetUInt4(int slot, uint x, uint y, uint z, uint w)
		{
			if (slot >= 0)
			{
				uint* values = stackalloc uint[4] { x, y, z, w };
				SetUInts(slot, values, 4, 1);
			}
		}

		public void SetVector2(int slot, Vector2 value)
		{
			if (slot >= 0)
			{
				SetFloats(slot, (float*)&value, 2, 1);
			}
		}

		public void SetVector3(int slot, Vector3 value)
		{
			if (slot >= 0)
			{
				SetFloats(slot, (float*)&value, 3, 1);
			}
		}

		public void SetVector4(int slot, Vector4 value)
		{
			if (slot >= 0)
			{
				SetFloats(slot, (float*)&value, 4, 1);
			}
		}

		public void SetColor(int slot, Color4 value)
		{
			if (slot >= 0)
			{
				SetFloats(slot, (float*)&value, 4, 1);
			}
		}

		public void SetMatrix4(int slot, in Matrix4 value)
		{
			if (slot >= 0)
			{
				fixed (Matrix4* pointer = &value)
				{
					SetMatrices(slot, (float*)pointer, 1);
				}
			}
		}

		public void SetVector4Array(int slot, ReadOnlySpan<Vector4> values)
		{
			if (slot >= 0 && values.Length > 0)
			{
				fixed (Vector4* pointer = values)
				{
					SetFloats(slot, (float*)pointer, 4, values.Length);
				}
			}
		}

		public void SetFloatArray(int slot, ReadOnlySpan<float> values)
		{
			if (slot >= 0 && values.Length > 0)
			{
				fixed (float* pointer = values)
				{
					SetFloats(slot, pointer, 1, values.Length);
				}
			}
		}

		public void SetIntArray(int slot, ReadOnlySpan<int> values)
		{
			if (slot >= 0 && values.Length > 0)
			{
				fixed (int* pointer = values)
				{
					SetInts(slot, pointer, 1, values.Length);
				}
			}
		}
	}
}
