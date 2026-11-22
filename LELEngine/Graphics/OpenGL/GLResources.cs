using System;
using Brutal.OpenGlApi;
using static Brutal.OpenGlApi.OpenGl;

namespace LELEngine.Graphics.OpenGL
{
	/// <summary>OpenGL's names of a texel format: the storage format and how its texels are given and read.</summary>
	internal readonly struct GLFormat
	{
		public readonly int Internal;
		public readonly PixelFormat Format;
		public readonly PixelType Type;

		public GLFormat(int internalFormat, PixelFormat format, PixelType type)
		{
			Internal = internalFormat;
			Format = format;
			Type = type;
		}

		public static GLFormat Of(TextureFormat format)
		{
			const PixelFormat Red = PixelFormat.Red;
			const PixelFormat Rg = PixelFormat.Rg;
			const PixelFormat Rgba = PixelFormat.Rgba;
			const PixelType Byte = PixelType.UnsignedByte;
			const PixelType Short = PixelType.UnsignedShort;
			const PixelType Half = PixelType.HalfFloat;
			const PixelType Float = PixelType.Float;
			switch (format)
			{
				case TextureFormat.R8: return new GLFormat(0x8229, Red, Byte);
				case TextureFormat.RG8: return new GLFormat(0x822B, Rg, Byte);
				case TextureFormat.RGBA8: return new GLFormat(0x8058, Rgba, Byte);
				case TextureFormat.SRGBA8: return new GLFormat(0x8C43, Rgba, Byte);
				case TextureFormat.R16: return new GLFormat(0x822A, Red, Short);
				case TextureFormat.RG16: return new GLFormat(0x822C, Rg, Short);
				case TextureFormat.RGBA16: return new GLFormat(0x805B, Rgba, Short);
				case TextureFormat.R16F: return new GLFormat(0x822D, Red, Half);
				case TextureFormat.RG16F: return new GLFormat(0x822F, Rg, Half);
				case TextureFormat.RGBA16F: return new GLFormat(0x881A, Rgba, Half);
				case TextureFormat.R32F: return new GLFormat(0x822E, Red, Float);
				case TextureFormat.RG32F: return new GLFormat(0x8230, Rg, Float);
				case TextureFormat.RGBA32F: return new GLFormat(0x8814, Rgba, Float);
				// GL_UNSIGNED_INT_10F_11F_11F_REV: the packed texel as it is stored.
				case TextureFormat.R11G11B10F: return new GLFormat(0x8C3A, PixelFormat.Rgb, (PixelType)0x8C3B);
				case TextureFormat.R8UI: return new GLFormat(0x8232, PixelFormat.RedInteger, Byte);
				case TextureFormat.R16UI: return new GLFormat(0x8234, PixelFormat.RedInteger, Short);
				case TextureFormat.R32UI: return new GLFormat(0x8236, PixelFormat.RedInteger, PixelType.UnsignedInt);
				case TextureFormat.RGBA32UI: return new GLFormat(0x8D70, PixelFormat.RgbaInteger, PixelType.UnsignedInt);
				// Depth is given and read as 32 bit floats whatever the storage.
				case TextureFormat.Depth24: return new GLFormat(0x81A6, PixelFormat.DepthComponent, Float);
				case TextureFormat.Depth32F: return new GLFormat(0x8CAC, PixelFormat.DepthComponent, Float);
				case TextureFormat.BC1: return new GLFormat(0x83F0, 0, 0);
				case TextureFormat.BC1SRGB: return new GLFormat(0x8C4C, 0, 0);
				case TextureFormat.BC1A: return new GLFormat(0x83F1, 0, 0);
				case TextureFormat.BC1ASRGB: return new GLFormat(0x8C4D, 0, 0);
				case TextureFormat.BC2: return new GLFormat(0x83F2, 0, 0);
				case TextureFormat.BC2SRGB: return new GLFormat(0x8C4E, 0, 0);
				case TextureFormat.BC3: return new GLFormat(0x83F3, 0, 0);
				case TextureFormat.BC3SRGB: return new GLFormat(0x8C4F, 0, 0);
				case TextureFormat.BC4: return new GLFormat(0x8DBB, 0, 0);
				case TextureFormat.BC5: return new GLFormat(0x8DBD, 0, 0);
				case TextureFormat.BC6H: return new GLFormat(0x8E8F, 0, 0);
				case TextureFormat.BC6HSigned: return new GLFormat(0x8E8E, 0, 0);
				case TextureFormat.BC7: return new GLFormat(0x8E8C, 0, 0);
				case TextureFormat.BC7SRGB: return new GLFormat(0x8E8D, 0, 0);
				default: throw new ArgumentOutOfRangeException(nameof(format), format, "No OpenGL format for this texel format.");
			}
		}
	}

	internal sealed unsafe class GLSampler : GpuSampler
	{
		public readonly int Handle;

		public GLSampler(in SamplerDescriptor descriptor, float maxAnisotropy)
		{
			Descriptor = descriptor;
			OpenGLBackend gl = GLDevice.Gl;
			Handle = gl.GenSampler();
			TextureMinFilter min;
			if (descriptor.Mip == SamplerMipFilter.None)
			{
				min = descriptor.Min == SamplerFilter.Linear ? TextureMinFilter.Linear : TextureMinFilter.Nearest;
			}
			else if (descriptor.Mip == SamplerMipFilter.Nearest)
			{
				min = descriptor.Min == SamplerFilter.Linear ? TextureMinFilter.LinearMipmapNearest : TextureMinFilter.NearestMipmapNearest;
			}
			else
			{
				min = descriptor.Min == SamplerFilter.Linear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.NearestMipmapLinear;
			}

			gl.SamplerParameteri(Handle, SamplerParameterName.TextureMinFilter, (int)min);
			gl.SamplerParameteri(Handle, SamplerParameterName.TextureMagFilter, (int)(descriptor.Mag == SamplerFilter.Linear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));
			gl.SamplerParameteri(Handle, SamplerParameterName.TextureWrapS, Wrap(descriptor.AddressU));
			gl.SamplerParameteri(Handle, SamplerParameterName.TextureWrapT, Wrap(descriptor.AddressV));
			gl.SamplerParameteri(Handle, SamplerParameterName.TextureWrapR, Wrap(descriptor.AddressW));
			if (descriptor.Compare)
			{
				gl.SamplerParameteri(Handle, SamplerParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
				gl.SamplerParameteri(Handle, SamplerParameterName.TextureCompareFunc, GLCommandList.Compare(descriptor.CompareFunction));
			}

			unsafe
			{
				float* border = stackalloc float[4];
				float value = descriptor.Border == SamplerBorder.OpaqueWhite ? 1f : 0f;
				border[0] = border[1] = border[2] = value;
				border[3] = descriptor.Border == SamplerBorder.TransparentBlack ? 0f : 1f;
				// GL_TEXTURE_BORDER_COLOR
				Native.glSamplerParameterfv(new GlSampler((uint)Handle), 0x1004, border);
			}

			if (descriptor.MaxAnisotropy > 1f && maxAnisotropy > 1f)
			{
				// GL_TEXTURE_MAX_ANISOTROPY
				Native.glSamplerParameterf(new GlSampler((uint)Handle), 0x84FE, Math.Min(descriptor.MaxAnisotropy, maxAnisotropy));
			}
		}

		private static int Wrap(SamplerAddress address)
		{
			switch (address)
			{
				case SamplerAddress.Repeat: return (int)TextureWrapMode.Repeat;
				case SamplerAddress.MirroredRepeat: return (int)TextureWrapMode.MirroredRepeat;
				case SamplerAddress.ClampToBorder: return (int)TextureWrapMode.ClampToBorder;
				default: return (int)TextureWrapMode.ClampToEdge;
			}
		}
	}

	internal sealed unsafe class GLTexture : GpuTexture
	{
		/// <summary>The OpenGL texture (0 for a window's back buffer).</summary>
		public readonly int Handle;

		public readonly TextureTarget Target;
		public readonly GLFormat GL;
		private readonly bool backbuffer;

		public GLTexture(in TextureDescriptor descriptor)
			: base(descriptor)
		{
			OpenGLBackend gl = GLDevice.Gl;
			GL = GLFormat.Of(Format);
			switch (Kind)
			{
				case TextureKind.Texture3D:
					Target = TextureTarget.Texture3D;
					break;
				case TextureKind.Cube:
					Target = TextureTarget.TextureCubeMap;
					break;
				case TextureKind.Texture2DArray:
					Target = TextureTarget.Texture2DArray;
					break;
				default:
					Target = TextureTarget.Texture2D;
					break;
			}

			Handle = gl.GenTexture();
			GLDevice.Current.BindForEdit(this);
			if (Kind == TextureKind.Texture3D || Kind == TextureKind.Texture2DArray)
			{
				gl.TexStorage3D((TextureTarget3d)Target, MipLevels, (SizedInternalFormat)GL.Internal, Width, Height, Depth);
			}
			else
			{
				gl.TexStorage2D((TextureTarget2d)Target, MipLevels, (SizedInternalFormat)GL.Internal, Width, Height);
			}

			// The texture's own sampling state is never used (a sampler object is bound with every texture); this only
			// keeps the texture complete for whatever reads it without one.
			gl.TexParameteri(Target, TextureParameterName.TextureMinFilter, (int)(MipLevels > 1 ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
			gl.TexParameteri(Target, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			gl.TexParameteri(Target, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			gl.TexParameteri(Target, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			if (!string.IsNullOrEmpty(Name))
			{
				GLDevice.Label(0x1702, Handle, Name);
			}

			GLDevice.Current.Register(this);
		}

		/// <summary>A window's back buffer.</summary>
		public GLTexture(int width, int height)
			: base(TextureDescriptor.Texture2D(TextureFormat.RGBA8, width, height, 1, TextureUsage.Attachment, "Backbuffer"))
		{
			backbuffer = true;
			Target = TextureTarget.Texture2D;
			GL = GLFormat.Of(TextureFormat.RGBA8);
		}

		public override bool IsBackbuffer => backbuffer;

		/// <summary>The target a face of a cube map, or the texture itself, is attached and uploaded through.</summary>
		public TextureTarget FaceTarget(int layer)
		{
			return Kind == TextureKind.Cube ? TextureTarget.TextureCubeMapPositiveX + layer : Target;
		}

		public override void SetData(int mip, int x, int y, int z, int width, int height, int depth, void* data, long size)
		{
			if (backbuffer)
			{
				throw new InvalidOperationException("A window's back buffer takes no texel uploads: blit or draw into it.");
			}

			long expected = Format.LevelSize(width, height, depth);
			if (size < expected)
			{
				throw new ArgumentException($"Texture '{Name}': {size} bytes given for a {width} x {height} x {depth} box of {Format} ({expected} bytes).");
			}

			OpenGLBackend gl = GLDevice.Gl;
			GLDevice.Current.BindForEdit(this);
			bool compressed = Format.IsCompressed();
			if (Kind == TextureKind.Texture3D || Kind == TextureKind.Texture2DArray)
			{
				if (compressed)
				{
					Native.glCompressedTexSubImage3D((uint)Target, mip, x, y, z, width, height, depth, (uint)GL.Internal, (int)expected, data);
				}
				else
				{
					gl.TexSubImage3D(Target, mip, x, y, z, width, height, depth, GL.Format, GL.Type, data);
				}
			}
			else if (Kind == TextureKind.Cube)
			{
				long faceSize = Format.LevelSize(width, height);
				for (int face = 0; face < depth; face++)
				{
					void* faceData = (byte*)data + face * faceSize;
					if (compressed)
					{
						Native.glCompressedTexSubImage2D((uint)FaceTarget(z + face), mip, x, y, width, height, (uint)GL.Internal, (int)faceSize, faceData);
					}
					else
					{
						gl.TexSubImage2D(FaceTarget(z + face), mip, x, y, width, height, GL.Format, GL.Type, faceData);
					}
				}
			}
			else if (compressed)
			{
				Native.glCompressedTexSubImage2D((uint)Target, mip, x, y, width, height, (uint)GL.Internal, (int)expected, data);
			}
			else
			{
				gl.TexSubImage2D(Target, mip, x, y, width, height, GL.Format, GL.Type, data);
			}
		}

		public override void GetData(int mip, int x, int y, int z, int width, int height, int depth, void* destination, long size)
		{
			long expected = Format.LevelSize(width, height, depth);
			if (size < expected)
			{
				throw new ArgumentException($"Texture '{Name}': a {width} x {height} x {depth} box of {Format} needs {expected} bytes, the destination has {size}.");
			}

			GLDevice.Current.ReadTexture(this, mip, x, y, z, width, height, depth, destination, (int)Math.Min(int.MaxValue, size), null, 0);
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			Forget();
			if (!backbuffer)
			{
				GLDevice.Current.Release(this);
			}
		}
	}

	internal sealed unsafe class GLBuffer : GpuBuffer
	{
		/// <summary>The OpenGL buffer object (another one after <see cref="Resize" />).</summary>
		public int Handle { get; private set; }

		public override void Resize(long size, long keep)
		{
			OpenGLBackend gl = GLDevice.Gl;
			int fresh = gl.GenBuffer();
			gl.BindBuffer(BufferTarget.CopyWriteBuffer, fresh);
			gl.BufferData(BufferTarget.CopyWriteBuffer, (IntPtr)size, null, Hint);
			long copied = Math.Min(Math.Min(keep, Size), size);
			if (copied > 0)
			{
				GLDevice.Current.CommandsOfThread.FlushBarriers();
				gl.BindBuffer(BufferTarget.CopyReadBuffer, Handle);
				gl.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero, (IntPtr)copied);
			}

			GLDevice.Current.ReleaseBuffer(Handle);
			Handle = fresh;
			Size = size;
		}

		public GLBuffer(in BufferDescriptor descriptor)
			: base(descriptor)
		{
			Handle = GLDevice.Gl.GenBuffer();
			if (descriptor.Size > 0)
			{
				SetData(null, descriptor.Size);
			}

			if (!string.IsNullOrEmpty(descriptor.Name))
			{
				// GL_BUFFER: a label needs the buffer object to exist, which the first bind makes it.
				GLDevice.Gl.BindBuffer(BufferTarget.CopyWriteBuffer, Handle);
				GLDevice.Label(0x82E0, Handle, descriptor.Name);
			}
		}

		private BufferUsageHint Hint => (Usage & BufferUsage.Readback) != 0 ? BufferUsageHint.DynamicRead : Dynamic ? BufferUsageHint.DynamicDraw : BufferUsageHint.StaticDraw;

		public override void SetData(void* data, long size)
		{
			OpenGLBackend gl = GLDevice.Gl;
			gl.BindBuffer(BufferTarget.CopyWriteBuffer, Handle);
			gl.BufferData(BufferTarget.CopyWriteBuffer, (IntPtr)size, data, Hint);
			Size = size;
		}

		public override void SetSubData(long offset, void* data, long size)
		{
			if (size <= 0)
			{
				return;
			}

			if (offset < 0 || offset + size > Size)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), $"Buffer '{Name}': {size} bytes at {offset} do not fit its {Size} bytes.");
			}

			OpenGLBackend gl = GLDevice.Gl;
			gl.BindBuffer(BufferTarget.CopyWriteBuffer, Handle);
			gl.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)offset, (IntPtr)size, data);
		}

		public override void GetData(long offset, void* destination, long size)
		{
			if (size <= 0)
			{
				return;
			}

			if (offset < 0 || offset + size > Size)
			{
				throw new ArgumentOutOfRangeException(nameof(offset), $"Buffer '{Name}': {size} bytes at {offset} are outside its {Size} bytes.");
			}

			OpenGLBackend gl = GLDevice.Gl;
			GLDevice.Current.CommandsOfThread.FlushBarriers();
			gl.BindBuffer(BufferTarget.CopyReadBuffer, Handle);
			gl.GetBufferSubData(BufferTarget.CopyReadBuffer, (IntPtr)offset, (IntPtr)size, destination);
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			IsDisposed = true;
			GLDevice.Current.Release(this);
		}
	}

	internal sealed class GLFence : GpuFence
	{
		private IntPtr sync;
		private bool signaled;

		public GLFence()
		{
			sync = GLDevice.Gl.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
		}

		public override bool IsSignaled => Poll(ClientWaitSyncFlags.None, 0);

		public override bool Wait(long timeoutNanoseconds)
		{
			return Poll(ClientWaitSyncFlags.SyncFlushCommandsBit, timeoutNanoseconds);
		}

		public override void Dispose()
		{
			if (sync != IntPtr.Zero)
			{
				GLDevice.Gl.DeleteSync(sync);
				sync = IntPtr.Zero;
			}
		}

		private bool Poll(ClientWaitSyncFlags flags, long timeout)
		{
			if (signaled || sync == IntPtr.Zero)
			{
				return signaled;
			}

			WaitSyncStatus status = GLDevice.Gl.ClientWaitSync(sync, flags, timeout);
			signaled = status == WaitSyncStatus.AlreadySignaled || status == WaitSyncStatus.ConditionSatisfied;
			return signaled;
		}
	}

	internal sealed unsafe class GLTimestamp : GpuTimestamp
	{
		public readonly int Query;
		private bool written;

		public GLTimestamp()
		{
			int name;
			GLDevice.Gl.GenQueries(1, &name);
			Query = name;
		}

		public override bool IsAvailable
		{
			get
			{
				if (!written)
				{
					return false;
				}

				GLDevice.Gl.GetQueryObject(Query, GetQueryObjectParam.QueryResultAvailable, out int available);
				return available != 0;
			}
		}

		public override long Nanoseconds
		{
			get
			{
				if (!written)
				{
					return 0;
				}

				GLDevice.Gl.GetQueryObject(Query, GetQueryObjectParam.QueryResult, out long nanoseconds);
				return nanoseconds;
			}
		}

		public void Write()
		{
			GLDevice.Gl.QueryCounter(Query, QueryCounterTarget.Timestamp);
			written = true;
		}

		public override void Dispose()
		{
			int name = Query;
			GLDevice.Gl.DeleteQueries(1, &name);
		}
	}
}
