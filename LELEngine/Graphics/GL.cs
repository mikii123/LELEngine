using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LELEngine.Mathematics;

namespace LELEngine.Graphics
{
	/// <summary>
	///     The engine's graphics commands, in OpenGL's vocabulary: what the renderer, the passes and every other piece
	///     of drawing code call. They are executed by the graphics backend chosen for the process
	///     (<see cref="Api" />): OpenGL or Vulkan, both through the BRUTAL framework's bindings. Commands run on the
	///     thread that holds the graphics context.
	/// </summary>
	public static unsafe class GL
	{
		private static GraphicsBackend backend;

		/// <summary>The graphics API executing the commands.</summary>
		public static GraphicsApi Api => backend != null ? backend.Api : GraphicsApi.OpenGL;

		/// <summary>The commands can be called: a backend was started.</summary>
		public static bool IsLoaded => backend != null;

		/// <summary>
		///     Starts the OpenGL backend for the context current on the calling thread (its functions are loaded once
		///     for the process: every context of the process is the same driver's).
		/// </summary>
		public static void LoadBindings()
		{
			if (backend == null || backend.Api != GraphicsApi.OpenGL)
			{
				backend = new OpenGLBackend();
			}
		}

		internal static void SetBackend(GraphicsBackend value)
		{
			backend = value;
		}

		#region Pinning

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static ref byte First<T>(T[] data) where T : struct
		{
			if (data == null || data.Length == 0)
			{
				return ref Unsafe.NullRef<byte>();
			}

			return ref Unsafe.As<T, byte>(ref MemoryMarshal.GetArrayDataReference(data));
		}

		#endregion

		#region State

		public static void Enable(EnableCap cap) => backend.Enable(cap);
		public static void Disable(EnableCap cap) => backend.Disable(cap);
		public static void Viewport(int x, int y, int width, int height) => backend.Viewport(x, y, width, height);
		public static void Scissor(int x, int y, int width, int height) => backend.Scissor(x, y, width, height);
		public static void ClearColor(float red, float green, float blue, float alpha) => backend.ClearColor(red, green, blue, alpha);
		public static void ClearColor(Color4 color) => backend.ClearColor(color.R, color.G, color.B, color.A);
		public static void ClearDepth(double depth) => backend.ClearDepth(depth);
		public static void Clear(ClearBufferMask mask) => backend.Clear(mask);

		public static void ClearBuffer(ClearBuffer buffer, int drawbuffer, float[] value)
		{
			fixed (float* pointer = value)
			{
				backend.ClearBufferfv(buffer, drawbuffer, pointer);
			}
		}

		public static void ClearBuffer(ClearBuffer buffer, int drawbuffer, uint[] value)
		{
			fixed (uint* pointer = value)
			{
				backend.ClearBufferuiv(buffer, drawbuffer, pointer);
			}
		}

		public static void ColorMask(bool red, bool green, bool blue, bool alpha) => backend.ColorMask(red, green, blue, alpha);
		public static void DepthMask(bool flag) => backend.DepthMask(flag);
		public static void DepthFunc(DepthFunction func) => backend.DepthFunc(func);
		public static void CullFace(TriangleFace mode) => backend.CullFace(mode);
		public static void PolygonMode(TriangleFace face, PolygonMode mode) => backend.PolygonMode(face, mode);
		public static void PolygonOffset(float factor, float units) => backend.PolygonOffset(factor, units);
		public static void BlendEquation(BlendEquationMode mode) => backend.BlendEquation(mode);
		public static void BlendEquationSeparate(BlendEquationMode modeRGB, BlendEquationMode modeAlpha) => backend.BlendEquationSeparate(modeRGB, modeAlpha);
		public static void BlendFunc(BlendingFactor sfactor, BlendingFactor dfactor) => backend.BlendFunc(sfactor, dfactor);

		public static void BlendFuncSeparate(BlendingFactorSrc sfactorRGB, BlendingFactorDest dfactorRGB, BlendingFactorSrc sfactorAlpha, BlendingFactorDest dfactorAlpha)
		{
			backend.BlendFuncSeparate(sfactorRGB, dfactorRGB, sfactorAlpha, dfactorAlpha);
		}

		public static void PixelStore(PixelStoreParameter pname, int param) => backend.PixelStore(pname, param);
		public static void Finish() => backend.Finish();
		public static void Flush() => backend.Flush();
		public static void MemoryBarrier(MemoryBarrierFlags barriers) => backend.MemoryBarrier(barriers);
		public static int GetInteger(GetPName pname) => backend.GetInteger(pname);

		public static void GetInteger(GetPName pname, out int data)
		{
			data = backend.GetInteger(pname);
		}

		public static void GetInteger(GetPName pname, int[] data)
		{
			fixed (int* pointer = data)
			{
				backend.GetIntegerv(pname, pointer);
			}
		}

		public static void GetInteger(GetIndexedPName target, int index, out int data)
		{
			int value = 0;
			backend.GetIntegeri(target, index, &value);
			data = value;
		}

		public static float GetFloat(GetPName pname) => backend.GetFloat(pname);
		public static string GetString(StringName name) => backend.GetString(name);
		public static string GetString(StringNameIndexed name, int index) => backend.GetStringi(name, index);

		#endregion

		#region Buffers

		public static int GenBuffer() => backend.GenBuffer();
		public static void DeleteBuffer(int buffers) => backend.DeleteBuffer(buffers);

		public static void DeleteBuffers(int n, int[] buffers)
		{
			for (int i = 0; i < n; i++)
			{
				backend.DeleteBuffer(buffers[i]);
			}
		}

		public static void BindBuffer(BufferTarget target, int buffer) => backend.BindBuffer(target, buffer);
		public static void BindBufferBase(BufferRangeTarget target, int index, int buffer) => backend.BindBufferBase(target, index, buffer);
		public static void BindBufferRange(BufferRangeTarget target, int index, int buffer, IntPtr offset, int size) => backend.BindBufferRange(target, index, buffer, offset, size);
		public static void BufferData(BufferTarget target, int size, IntPtr data, BufferUsageHint usage) => backend.BufferData(target, size, (void*)data, usage);
		public static void BufferData(BufferTarget target, IntPtr size, IntPtr data, BufferUsageHint usage) => backend.BufferData(target, size, (void*)data, usage);

		public static void BufferData<T>(BufferTarget target, int size, T[] data, BufferUsageHint usage) where T : struct
		{
			fixed (byte* pointer = &First(data))
			{
				backend.BufferData(target, size, pointer, usage);
			}
		}

		public static void BufferData<T>(BufferTarget target, IntPtr size, T[] data, BufferUsageHint usage) where T : struct
		{
			fixed (byte* pointer = &First(data))
			{
				backend.BufferData(target, size, pointer, usage);
			}
		}

		public static void BufferSubData(BufferTarget target, IntPtr offset, int size, IntPtr data) => backend.BufferSubData(target, offset, size, (void*)data);

		public static void BufferSubData<T>(BufferTarget target, IntPtr offset, int size, T[] data) where T : struct
		{
			fixed (byte* pointer = &First(data))
			{
				backend.BufferSubData(target, offset, size, pointer);
			}
		}

		public static void BufferSubData<T>(BufferTarget target, IntPtr offset, int size, ref T data) where T : struct
		{
			fixed (byte* pointer = &Unsafe.As<T, byte>(ref data))
			{
				backend.BufferSubData(target, offset, size, pointer);
			}
		}

		public static void GetBufferSubData<T>(BufferTarget target, IntPtr offset, int size, T[] data) where T : struct
		{
			fixed (byte* pointer = &First(data))
			{
				backend.GetBufferSubData(target, offset, size, pointer);
			}
		}

		public static void GetNamedBufferSubData<T>(int buffer, IntPtr offset, int size, T[] data) where T : struct
		{
			fixed (byte* pointer = &First(data))
			{
				backend.GetNamedBufferSubData(buffer, offset, size, pointer);
			}
		}

		public static void CopyBufferSubData(BufferTarget readTarget, BufferTarget writeTarget, IntPtr readOffset, IntPtr writeOffset, int size)
		{
			backend.CopyBufferSubData(readTarget, writeTarget, readOffset, writeOffset, size);
		}

		public static IntPtr MapBufferRange(BufferTarget target, IntPtr offset, int length, MapBufferAccessMask access) => backend.MapBufferRange(target, offset, length, access);
		public static IntPtr MapBufferRange(BufferTarget target, IntPtr offset, IntPtr length, MapBufferAccessMask access) => backend.MapBufferRange(target, offset, length, access);
		public static bool UnmapBuffer(BufferTarget target) => backend.UnmapBuffer(target);

		#endregion

		#region Vertex arrays

		public static int GenVertexArray() => backend.GenVertexArray();

		public static void GenVertexArrays(int n, out int arrays)
		{
			arrays = backend.GenVertexArray();
		}

		public static void DeleteVertexArray(int arrays) => backend.DeleteVertexArray(arrays);
		public static void BindVertexArray(int array) => backend.BindVertexArray(array);
		public static void EnableVertexAttribArray(int index) => backend.EnableVertexAttribArray(index);

		public static void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, int offset)
		{
			backend.VertexAttribPointer(index, size, type, normalized, stride, offset);
		}

		public static void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr pointer)
		{
			backend.VertexAttribIPointer(index, size, type, stride, pointer);
		}

		public static void VertexAttribDivisor(int index, int divisor) => backend.VertexAttribDivisor(index, divisor);

		#endregion

		#region Textures and samplers

		public static int GenTexture() => backend.GenTexture();
		public static void DeleteTexture(int textures) => backend.DeleteTexture(textures);
		public static void ActiveTexture(TextureUnit texture) => backend.ActiveTexture(texture);
		public static void BindTexture(TextureTarget target, int texture) => backend.BindTexture(target, texture);

		public static void BindImageTexture(int unit, int texture, int level, bool layered, int layer, TextureAccess access, SizedInternalFormat format)
		{
			backend.BindImageTexture(unit, texture, level, layered, layer, access, format);
		}

		public static void TexParameter(TextureTarget target, TextureParameterName pname, int param) => backend.TexParameteri(target, pname, param);
		public static void TexParameter(TextureTarget target, TextureParameterName pname, float param) => backend.TexParameterf(target, pname, param);

		public static void TexParameter(TextureTarget target, TextureParameterName pname, float[] @params)
		{
			fixed (float* pointer = @params)
			{
				backend.TexParameterfv(target, pname, pointer);
			}
		}

		public static void TextureParameter(int texture, TextureParameterName pname, int param) => backend.TextureParameteri(texture, pname, param);

		public static void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int border, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.TexImage2D(target, level, internalformat, width, height, border, format, type, (void*)pixels);
		}

		public static void TexImage2D<T>(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int border, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.TexImage2D(target, level, internalformat, width, height, border, format, type, pointer);
			}
		}

		public static void TexImage2D<T>(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int border, PixelFormat format, PixelType type, ref T pixels) where T : struct
		{
			fixed (byte* pointer = &Unsafe.As<T, byte>(ref pixels))
			{
				backend.TexImage2D(target, level, internalformat, width, height, border, format, type, pointer);
			}
		}

		public static void TexImage3D(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int depth, int border, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.TexImage3D(target, level, internalformat, width, height, depth, border, format, type, (void*)pixels);
		}

		public static void TexImage3D<T>(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int depth, int border, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.TexImage3D(target, level, internalformat, width, height, depth, border, format, type, pointer);
			}
		}

		public static void TexImage3D<T>(TextureTarget target, int level, PixelInternalFormat internalformat, int width, int height, int depth, int border, PixelFormat format, PixelType type, ref T pixels) where T : struct
		{
			fixed (byte* pointer = &Unsafe.As<T, byte>(ref pixels))
			{
				backend.TexImage3D(target, level, internalformat, width, height, depth, border, format, type, pointer);
			}
		}

		public static void TexStorage2D(TextureTarget2d target, int levels, SizedInternalFormat internalformat, int width, int height)
		{
			backend.TexStorage2D(target, levels, internalformat, width, height);
		}

		public static void TexStorage3D(TextureTarget3d target, int levels, SizedInternalFormat internalformat, int width, int height, int depth)
		{
			backend.TexStorage3D(target, levels, internalformat, width, height, depth);
		}

		public static void TexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, int width, int height, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.TexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, (void*)pixels);
		}

		public static void TexSubImage2D<T>(TextureTarget target, int level, int xoffset, int yoffset, int width, int height, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.TexSubImage2D(target, level, xoffset, yoffset, width, height, format, type, pointer);
			}
		}

		public static void TexSubImage3D(TextureTarget target, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.TexSubImage3D(target, level, xoffset, yoffset, zoffset, width, height, depth, format, type, (void*)pixels);
		}

		public static void TexSubImage3D<T>(TextureTarget target, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.TexSubImage3D(target, level, xoffset, yoffset, zoffset, width, height, depth, format, type, pointer);
			}
		}

		public static void TexSubImage3D<T>(TextureTarget target, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, PixelFormat format, PixelType type, ref T pixels) where T : struct
		{
			fixed (byte* pointer = &Unsafe.As<T, byte>(ref pixels))
			{
				backend.TexSubImage3D(target, level, xoffset, yoffset, zoffset, width, height, depth, format, type, pointer);
			}
		}

		public static void CompressedTexImage2D<T>(TextureTarget target, int level, InternalFormat internalformat, int width, int height, int border, int imageSize, ref T data) where T : struct
		{
			fixed (byte* pointer = &Unsafe.As<T, byte>(ref data))
			{
				backend.CompressedTexImage2D(target, level, internalformat, width, height, border, imageSize, pointer);
			}
		}

		public static void GenerateMipmap(GenerateMipmapTarget target) => backend.GenerateMipmap(target);

		public static void ClearTexImage(int texture, int level, PixelFormat format, PixelType type, IntPtr data)
		{
			backend.ClearTexImage(texture, level, format, type, (void*)data);
		}

		public static void CopyImageSubData(int srcName, ImageTarget srcTarget, int srcLevel, int srcX, int srcY, int srcZ, int dstName, ImageTarget dstTarget, int dstLevel, int dstX, int dstY, int dstZ, int srcWidth, int srcHeight, int srcDepth)
		{
			backend.CopyImageSubData(srcName, srcTarget, srcLevel, srcX, srcY, srcZ, dstName, dstTarget, dstLevel, dstX, dstY, dstZ, srcWidth, srcHeight, srcDepth);
		}

		public static void GetTexImage(TextureTarget target, int level, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.GetTexImage(target, level, format, type, (void*)pixels);
		}

		public static void GetTexImage<T>(TextureTarget target, int level, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.GetTexImage(target, level, format, type, pointer);
			}
		}

		public static void GetTextureImage<T>(int texture, int level, PixelFormat format, PixelType type, int bufSize, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.GetTextureImage(texture, level, format, type, bufSize, pointer);
			}
		}

		public static void GetTextureSubImage(int texture, int level, int xoffset, int yoffset, int zoffset, int width, int height, int depth, PixelFormat format, PixelType type, int bufSize, IntPtr pixels)
		{
			backend.GetTextureSubImage(texture, level, xoffset, yoffset, zoffset, width, height, depth, format, type, bufSize, (void*)pixels);
		}

		public static int GenSampler() => backend.GenSampler();
		public static void DeleteSampler(int samplers) => backend.DeleteSampler(samplers);
		public static void BindSampler(int unit, int sampler) => backend.BindSampler(unit, sampler);
		public static void SamplerParameter(int sampler, SamplerParameterName pname, int param) => backend.SamplerParameteri(sampler, pname, param);

		#endregion

		#region Framebuffers

		public static int GenFramebuffer() => backend.GenFramebuffer();
		public static void DeleteFramebuffer(int framebuffers) => backend.DeleteFramebuffer(framebuffers);
		public static void BindFramebuffer(FramebufferTarget target, int framebuffer) => backend.BindFramebuffer(target, framebuffer);
		public static FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target) => backend.CheckFramebufferStatus(target);

		public static void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, int texture, int level)
		{
			backend.FramebufferTexture2D(target, attachment, textarget, texture, level);
		}

		public static void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbuffertarget, int renderbuffer)
		{
			backend.FramebufferRenderbuffer(target, attachment, renderbuffertarget, renderbuffer);
		}

		public static void FramebufferParameter(FramebufferTarget target, FramebufferDefaultParameter pname, int param) => backend.FramebufferParameteri(target, pname, param);
		public static void DrawBuffer(DrawBufferMode buf) => backend.DrawBuffer(buf);

		public static void DrawBuffers(int n, DrawBuffersEnum[] bufs)
		{
			fixed (DrawBuffersEnum* pointer = bufs)
			{
				backend.DrawBuffers(n, pointer);
			}
		}

		public static void ReadBuffer(ReadBufferMode src) => backend.ReadBuffer(src);

		public static void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, IntPtr pixels)
		{
			backend.ReadPixels(x, y, width, height, format, type, (void*)pixels);
		}

		public static void ReadPixels<T>(int x, int y, int width, int height, PixelFormat format, PixelType type, T[] pixels) where T : struct
		{
			fixed (byte* pointer = &First(pixels))
			{
				backend.ReadPixels(x, y, width, height, format, type, pointer);
			}
		}

		public static void BlitFramebuffer(int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, ClearBufferMask mask, BlitFramebufferFilter filter)
		{
			backend.BlitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter);
		}

		public static int GenRenderbuffer() => backend.GenRenderbuffer();
		public static void DeleteRenderbuffer(int renderbuffers) => backend.DeleteRenderbuffer(renderbuffers);
		public static void BindRenderbuffer(RenderbufferTarget target, int renderbuffer) => backend.BindRenderbuffer(target, renderbuffer);

		public static void RenderbufferStorage(RenderbufferTarget target, RenderbufferStorage internalformat, int width, int height)
		{
			backend.RenderbufferStorage(target, internalformat, width, height);
		}

		#endregion

		#region Shaders and programs

		public static int CreateShader(ShaderType type) => backend.CreateShader(type);
		public static void DeleteShader(int shader) => backend.DeleteShader(shader);
		public static void ShaderSource(int shader, string @string) => backend.ShaderSource(shader, @string);
		public static void CompileShader(int shader) => backend.CompileShader(shader);
		public static void GetShader(int shader, ShaderParameter pname, out int @params) => backend.GetShader(shader, pname, out @params);
		public static string GetShaderInfoLog(int shader) => backend.GetShaderInfoLog(shader);
		public static int CreateProgram() => backend.CreateProgram();
		public static void DeleteProgram(int program) => backend.DeleteProgram(program);
		public static void AttachShader(int program, int shader) => backend.AttachShader(program, shader);
		public static void DetachShader(int program, int shader) => backend.DetachShader(program, shader);
		public static void LinkProgram(int program) => backend.LinkProgram(program);
		public static void UseProgram(int program) => backend.UseProgram(program);
		public static void GetProgram(int program, GetProgramParameterName pname, out int @params) => backend.GetProgram(program, pname, out @params);
		public static string GetProgramInfoLog(int program) => backend.GetProgramInfoLog(program);
		public static void ProgramParameter(int program, ProgramParameterName pname, int value) => backend.ProgramParameter(program, pname, value);

		public static void GetProgramBinary<T>(int program, int bufSize, out int length, out BinaryFormat binaryFormat, T[] binary) where T : struct
		{
			fixed (byte* pointer = &First(binary))
			{
				backend.GetProgramBinary(program, bufSize, out length, out binaryFormat, pointer);
			}
		}

		public static void ProgramBinary<T>(int program, BinaryFormat binaryFormat, T[] binary, int length) where T : struct
		{
			fixed (byte* pointer = &First(binary))
			{
				backend.ProgramBinary(program, binaryFormat, pointer, length);
			}
		}

		public static void BindAttribLocation(int program, int index, string name) => backend.BindAttribLocation(program, index, name);
		public static int GetAttribLocation(int program, string name) => backend.GetAttribLocation(program, name);
		public static int GetUniformLocation(int program, string name) => backend.GetUniformLocation(program, name);

		public static string GetActiveUniform(int program, int uniformIndex, out int size, out ActiveUniformType type)
		{
			return backend.GetActiveUniform(program, uniformIndex, out size, out type);
		}

		public static void Uniform1(int location, int v0) => backend.Uniform1i(location, v0);
		public static void Uniform1(int location, uint v0) => backend.Uniform1ui(location, v0);
		public static void Uniform1(int location, float v0) => backend.Uniform1f(location, v0);

		public static void Uniform1(int location, int count, int[] value)
		{
			fixed (int* pointer = value)
			{
				backend.Uniform1iv(location, count, pointer);
			}
		}

		public static void Uniform1(int location, int count, float[] value)
		{
			fixed (float* pointer = value)
			{
				backend.Uniform1fv(location, count, pointer);
			}
		}

		public static void Uniform2(int location, int v0, int v1) => backend.Uniform2i(location, v0, v1);
		public static void Uniform2(int location, float v0, float v1) => backend.Uniform2f(location, v0, v1);
		public static void Uniform2(int location, Vector2 vector) => backend.Uniform2f(location, vector.X, vector.Y);
		public static void Uniform3(int location, int v0, int v1, int v2) => backend.Uniform3i(location, v0, v1, v2);
		public static void Uniform3(int location, float v0, float v1, float v2) => backend.Uniform3f(location, v0, v1, v2);
		public static void Uniform3(int location, Vector3 vector) => backend.Uniform3f(location, vector.X, vector.Y, vector.Z);
		public static void Uniform3(int location, ref Vector3 vector) => backend.Uniform3f(location, vector.X, vector.Y, vector.Z);
		public static void Uniform4(int location, int v0, int v1, int v2, int v3) => backend.Uniform4i(location, v0, v1, v2, v3);
		public static void Uniform4(int location, uint v0, uint v1, uint v2, uint v3) => backend.Uniform4ui(location, v0, v1, v2, v3);
		public static void Uniform4(int location, Vector4 vector) => backend.Uniform4f(location, vector.X, vector.Y, vector.Z, vector.W);
		public static void Uniform4(int location, ref Vector4 vector) => backend.Uniform4f(location, vector.X, vector.Y, vector.Z, vector.W);
		public static void Uniform4(int location, Color4 color) => backend.Uniform4f(location, color.R, color.G, color.B, color.A);

		public static void Uniform4(int location, int count, float[] value)
		{
			fixed (float* pointer = value)
			{
				backend.Uniform4fv(location, count, pointer);
			}
		}

		/// <summary>Uploads a matrix as it lies in memory (row vectors: the shader's column-major matrix is its transpose's transpose).</summary>
		public static void UniformMatrix4(int location, bool transpose, ref Matrix4 matrix)
		{
			fixed (float* pointer = &matrix.Row0.X)
			{
				backend.UniformMatrix4fv(location, 1, transpose, pointer);
			}
		}

		public static void UniformMatrix4(int location, int count, bool transpose, float* value) => backend.UniformMatrix4fv(location, count, transpose, value);

		#endregion

		#region Drawing and compute

		public static void DrawArrays(PrimitiveType mode, int first, int count) => backend.DrawArrays(mode, first, count);
		public static void DrawArraysIndirect(PrimitiveType mode, IntPtr indirect) => backend.DrawArraysIndirect(mode, indirect);
		public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, int indices) => backend.DrawElements(mode, count, type, indices);
		public static void DrawElements(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices) => backend.DrawElements(mode, count, type, indices);

		public static void DrawElementsBaseVertex(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int basevertex)
		{
			backend.DrawElementsBaseVertex(mode, count, type, indices, basevertex);
		}

		public static void DrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect) => backend.DrawElementsIndirect(mode, type, indirect);

		public static void DrawElementsInstancedBaseVertexBaseInstance(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int instancecount, int basevertex, int baseinstance)
		{
			backend.DrawElementsInstancedBaseVertexBaseInstance(mode, count, type, indices, instancecount, basevertex, baseinstance);
		}

		/// <summary>Several indexed draws in one call; <paramref name="indices" /> holds each draw's byte offset into the index buffer (pointer-sized values).</summary>
		public static void MultiDrawElementsBaseVertex<T>(PrimitiveType mode, int[] count, DrawElementsType type, T[] indices, int drawcount, int[] basevertex) where T : struct
		{
			if (Unsafe.SizeOf<T>() != IntPtr.Size)
			{
				throw new ArgumentException("The offsets of a multi-draw are pointer-sized values (IntPtr).", nameof(indices));
			}

			fixed (int* counts = count)
			fixed (int* bases = basevertex)
			fixed (byte* offsets = &First(indices))
			{
				backend.MultiDrawElementsBaseVertex(mode, counts, type, (IntPtr*)offsets, drawcount, bases);
			}
		}

		public static void MultiDrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect, int drawcount, int stride)
		{
			backend.MultiDrawElementsIndirect(mode, type, indirect, drawcount, stride);
		}

		public static void MultiDrawElementsIndirectCount(PrimitiveType mode, DrawElementsType type, IntPtr indirect, IntPtr drawcount, int maxdrawcount, int stride)
		{
			backend.MultiDrawElementsIndirectCount(mode, type, indirect, drawcount, maxdrawcount, stride);
		}

		public static void DispatchCompute(int num_groups_x, int num_groups_y, int num_groups_z) => backend.DispatchCompute(num_groups_x, num_groups_y, num_groups_z);
		public static void DispatchComputeIndirect(IntPtr indirect) => backend.DispatchComputeIndirect(indirect);

		#endregion

		#region Queries and sync

		public static void GenQueries(int n, int[] ids)
		{
			fixed (int* pointer = ids)
			{
				backend.GenQueries(n, pointer);
			}
		}

		public static void DeleteQueries(int n, int[] ids)
		{
			fixed (int* pointer = ids)
			{
				backend.DeleteQueries(n, pointer);
			}
		}

		public static void BeginQuery(QueryTarget target, int id) => backend.BeginQuery(target, id);
		public static void EndQuery(QueryTarget target) => backend.EndQuery(target);
		public static void QueryCounter(int id, QueryCounterTarget target) => backend.QueryCounter(id, target);
		public static void GetQueryObject(int id, GetQueryObjectParam pname, out int @params) => backend.GetQueryObject(id, pname, out @params);
		public static void GetQueryObject(int id, GetQueryObjectParam pname, out long @params) => backend.GetQueryObject(id, pname, out @params);
		public static IntPtr FenceSync(SyncCondition condition, WaitSyncFlags flags) => backend.FenceSync(condition, flags);
		public static WaitSyncStatus ClientWaitSync(IntPtr sync, ClientWaitSyncFlags flags, long timeout) => backend.ClientWaitSync(sync, flags, timeout);
		public static void DeleteSync(IntPtr sync) => backend.DeleteSync(sync);

		#endregion

		#region Extensions

		/// <summary>KHR_parallel_shader_compile.</summary>
		public static class Khr
		{
			public static void MaxShaderCompilerThreads(int count) => backend.MaxShaderCompilerThreads((uint)count, true);
			public static void MaxShaderCompilerThreads(uint count) => backend.MaxShaderCompilerThreads(count, true);
		}

		/// <summary>ARB_parallel_shader_compile.</summary>
		public static class Arb
		{
			public static void MaxShaderCompilerThreads(int count) => backend.MaxShaderCompilerThreads((uint)count, false);
			public static void MaxShaderCompilerThreads(uint count) => backend.MaxShaderCompilerThreads(count, false);
		}

		#endregion
	}
}
