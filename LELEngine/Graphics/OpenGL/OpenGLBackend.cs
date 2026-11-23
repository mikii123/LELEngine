using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using static Brutal.OpenGlApi.OpenGl;

namespace LELEngine.Graphics.OpenGL
{
	/// <summary>
	///     OpenGL's functions as the OpenGL device calls them: each is the OpenGL function of the same name, called
	///     through the BRUTAL framework's bindings (<see cref="OpenGl.Native" />, loaded by GLAD for the context current
	///     when the backend is made), with the engine's enumerations of OpenGL's values (<c>GLEnums.cs</c>).
	/// </summary>
	internal sealed unsafe class OpenGLBackend
	{
		// KHR / ARB_parallel_shader_compile: extension functions, not in the bindings.
		private readonly delegate* unmanaged[Cdecl]<uint, void> maxShaderCompilerThreadsKhr;
		private readonly delegate* unmanaged[Cdecl]<uint, void> maxShaderCompilerThreadsArb;

		/// <summary>Loads OpenGL's functions for the context current on the calling thread.</summary>
		public OpenGLBackend()
		{
			if (!Glad.Initialize(Glfw.PInvoke.GetProcAddress))
			{
				throw new InvalidOperationException("OpenGL's functions could not be loaded (GLAD): no OpenGL context is current on this thread, or the driver gave none.");
			}

			maxShaderCompilerThreadsKhr = (delegate* unmanaged[Cdecl]<uint, void>)Glfw.GetProcAddress("glMaxShaderCompilerThreadsKHR\0"u8);
			maxShaderCompilerThreadsArb = (delegate* unmanaged[Cdecl]<uint, void>)Glfw.GetProcAddress("glMaxShaderCompilerThreadsARB\0"u8);
		}


		#region State

		public void Enable(EnableCap cap) => Native.glEnable((GlCapability)(uint)cap);
		public void Disable(EnableCap cap) => Native.glDisable((GlCapability)(uint)cap);
		public void Viewport(int x, int y, int width, int height) => Native.glViewport(x, y, width, height);
		public void Scissor(int x, int y, int width, int height) => Native.glScissor(x, y, width, height);
		public void ClearColor(float red, float green, float blue, float alpha) => Native.glClearColor(red, green, blue, alpha);
		public void ClearDepth(double depth) => Native.glClearDepth(depth);
		public void Clear(ClearBufferMask mask) => Native.glClear((uint)mask);
		public void ClearBufferfv(ClearBuffer buffer, int drawBuffer, float* value) => Native.glClearBufferfv(new GlBuffer((uint)buffer), drawBuffer, value);
		public void ClearBufferuiv(ClearBuffer buffer, int drawBuffer, uint* value) => Native.glClearBufferuiv(new GlBuffer((uint)buffer), drawBuffer, value);
		public void ColorMask(bool red, bool green, bool blue, bool alpha) => Native.glColorMask(red, green, blue, alpha);
		public void DepthMask(bool flag) => Native.glDepthMask(flag);
		public void DepthFunc(DepthFunction function) => Native.glDepthFunc((GlFunc)(uint)function);
		public void CullFace(TriangleFace mode) => Native.glCullFace((GlFace)(uint)mode);
		public void PolygonMode(TriangleFace face, PolygonMode mode) => Native.glPolygonMode((GlFace)(uint)face, (uint)mode);
		public void PolygonOffset(float factor, float units) => Native.glPolygonOffset(factor, units);
		public void BlendEquation(BlendEquationMode mode) => Native.glBlendEquation((uint)mode);
		public void BlendEquationSeparate(BlendEquationMode modeRgb, BlendEquationMode modeAlpha) => Native.glBlendEquationSeparate((uint)modeRgb, (uint)modeAlpha);
		public void BlendFunc(BlendingFactor source, BlendingFactor destination) => Native.glBlendFunc((uint)source, (uint)destination);

		public void BlendFuncSeparate(BlendingFactorSrc sourceRgb, BlendingFactorDest destinationRgb, BlendingFactorSrc sourceAlpha, BlendingFactorDest destinationAlpha)
		{
			Native.glBlendFuncSeparate((uint)sourceRgb, (uint)destinationRgb, (uint)sourceAlpha, (uint)destinationAlpha);
		}

		public void PixelStore(PixelStoreParameter name, int value) => Native.glPixelStorei((uint)name, value);
		public void Finish() => Native.glFinish();
		public void Flush() => Native.glFlush();
		public void MemoryBarrier(MemoryBarrierFlags barriers) => Native.glMemoryBarrier((uint)barriers);

		public int GetInteger(GetPName name)
		{
			int value = 0;
			Native.glGetIntegerv((uint)name, &value);
			return value;
		}

		public void GetIntegerv(GetPName name, int* data) => Native.glGetIntegerv((uint)name, data);
		public void GetIntegeri(GetIndexedPName target, int index, int* data) => Native.glGetIntegeri_v((uint)target, (uint)index, data);

		public float GetFloat(GetPName name)
		{
			float value = 0f;
			Native.glGetFloatv((uint)name, &value);
			return value;
		}

		public string GetString(StringName name) => Marshal.PtrToStringUTF8((IntPtr)Native.glGetString((uint)name));
		public string GetStringi(StringNameIndexed name, int index) => Marshal.PtrToStringUTF8((IntPtr)Native.glGetStringi((uint)name, (uint)index));

		#endregion

		#region Buffers

		public int GenBuffer()
		{
			GlBuffer buffer;
			Native.glGenBuffers(1, &buffer);
			return (int)buffer.Id;
		}

		public void DeleteBuffer(int buffer)
		{
			GlBuffer name = new GlBuffer((uint)buffer);
			Native.glDeleteBuffers(1, &name);
		}

		public void BindBuffer(BufferTarget target, int buffer) => Native.glBindBuffer((uint)target, new GlBuffer((uint)buffer));
		public void BindBufferBase(BufferRangeTarget target, int index, int buffer) => Native.glBindBufferBase((uint)target, (uint)index, new GlBuffer((uint)buffer));

		public void BindBufferRange(BufferRangeTarget target, int index, int buffer, IntPtr offset, IntPtr size)
		{
			Native.glBindBufferRange((uint)target, (uint)index, new GlBuffer((uint)buffer), offset, size);
		}

		public void BufferData(BufferTarget target, IntPtr size, void* data, BufferUsageHint usage) => Native.glBufferData((uint)target, size, data, (uint)usage);
		public void BufferSubData(BufferTarget target, IntPtr offset, IntPtr size, void* data) => Native.glBufferSubData((uint)target, offset, size, data);
		public void GetBufferSubData(BufferTarget target, IntPtr offset, IntPtr size, void* data) => Native.glGetBufferSubData((uint)target, offset, size, data);
		public void GetNamedBufferSubData(int buffer, IntPtr offset, IntPtr size, void* data) => Native.glGetNamedBufferSubData(new GlBuffer((uint)buffer), offset, size, data);

		public void CopyBufferSubData(BufferTarget readTarget, BufferTarget writeTarget, IntPtr readOffset, IntPtr writeOffset, IntPtr size)
		{
			Native.glCopyBufferSubData((uint)readTarget, (uint)writeTarget, readOffset, writeOffset, size);
		}

		public IntPtr MapBufferRange(BufferTarget target, IntPtr offset, IntPtr length, MapBufferAccessMask access)
		{
			return (IntPtr)Native.glMapBufferRange((uint)target, offset, length, (uint)access);
		}

		public bool UnmapBuffer(BufferTarget target) => Native.glUnmapBuffer((uint)target);

		#endregion

		#region Vertex arrays

		public int GenVertexArray()
		{
			GlVertexArray array;
			Native.glGenVertexArrays(1, &array);
			return (int)array.Id;
		}

		public void DeleteVertexArray(int array)
		{
			GlVertexArray name = new GlVertexArray((uint)array);
			Native.glDeleteVertexArrays(1, &name);
		}

		public void BindVertexArray(int array) => Native.glBindVertexArray(new GlVertexArray((uint)array));
		public void EnableVertexAttribArray(int index) => Native.glEnableVertexAttribArray((uint)index);

		public void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, IntPtr offset)
		{
			Native.glVertexAttribPointer((uint)index, size, (uint)type, normalized, stride, (void*)offset);
		}

		public void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr offset)
		{
			Native.glVertexAttribIPointer((uint)index, size, (uint)type, stride, (void*)offset);
		}

		public void VertexAttribDivisor(int index, int divisor) => Native.glVertexAttribDivisor((uint)index, (uint)divisor);

		#endregion

		#region Textures and samplers

		public int GenTexture()
		{
			GlTexture texture;
			Native.glGenTextures(1, &texture);
			return (int)texture.Id;
		}

		public void DeleteTexture(int texture)
		{
			GlTexture name = new GlTexture((uint)texture);
			Native.glDeleteTextures(1, &name);
		}

		public void ActiveTexture(TextureUnit unit) => Native.glActiveTexture((uint)unit);
		public void BindTexture(TextureTarget target, int texture) => Native.glBindTexture((uint)target, (uint)texture);

		public void BindImageTexture(int unit, int texture, int level, bool layered, int layer, TextureAccess access, SizedInternalFormat format)
		{
			Native.glBindImageTexture((uint)unit, (uint)texture, level, layered, layer, (uint)access, (uint)format);
		}

		public void TexParameteri(TextureTarget target, TextureParameterName name, int value) => Native.glTexParameteri((uint)target, (uint)name, value);
		public void TexParameterf(TextureTarget target, TextureParameterName name, float value) => Native.glTexParameterf((uint)target, (uint)name, value);
		public void TexParameterfv(TextureTarget target, TextureParameterName name, float* values) => Native.glTexParameterfv((uint)target, (uint)name, values);
		public void TextureParameteri(int texture, TextureParameterName name, int value) => Native.glTextureParameteri((uint)texture, (uint)name, value);

		public void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glTexImage2D((uint)target, level, (int)internalFormat, width, height, border, (uint)format, (uint)type, pixels);
		}

		public void TexImage3D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int depth, int border, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glTexImage3D((uint)target, level, (int)internalFormat, width, height, depth, border, (uint)format, (uint)type, pixels);
		}

		public void TexStorage2D(TextureTarget2d target, int levels, SizedInternalFormat internalFormat, int width, int height)
		{
			Native.glTexStorage2D((uint)target, levels, (uint)internalFormat, width, height);
		}

		public void TexStorage3D(TextureTarget3d target, int levels, SizedInternalFormat internalFormat, int width, int height, int depth)
		{
			Native.glTexStorage3D((uint)target, levels, (uint)internalFormat, width, height, depth);
		}

		public void TexSubImage2D(TextureTarget target, int level, int x, int y, int width, int height, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glTexSubImage2D((uint)target, level, x, y, width, height, (uint)format, (uint)type, pixels);
		}

		public void TexSubImage3D(TextureTarget target, int level, int x, int y, int z, int width, int height, int depth, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glTexSubImage3D((uint)target, level, x, y, z, width, height, depth, (uint)format, (uint)type, pixels);
		}

		public void CompressedTexImage2D(TextureTarget target, int level, InternalFormat internalFormat, int width, int height, int border, int imageSize, void* data)
		{
			Native.glCompressedTexImage2D((uint)target, level, (uint)internalFormat, width, height, border, imageSize, data);
		}

		public void GenerateMipmap(GenerateMipmapTarget target) => Native.glGenerateMipmap((uint)target);
		public void ClearTexImage(int texture, int level, PixelFormat format, PixelType type, void* data) => Native.glClearTexImage((uint)texture, level, (uint)format, (uint)type, data);

		public void CopyImageSubData(int source, ImageTarget sourceTarget, int sourceLevel, int sourceX, int sourceY, int sourceZ, int destination, ImageTarget destinationTarget, int destinationLevel, int destinationX, int destinationY, int destinationZ, int width, int height, int depth)
		{
			Native.glCopyImageSubData((uint)source, (uint)sourceTarget, sourceLevel, sourceX, sourceY, sourceZ, (uint)destination, (uint)destinationTarget, destinationLevel, destinationX, destinationY, destinationZ, width, height, depth);
		}

		public void GetTexImage(TextureTarget target, int level, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glGetTexImage((uint)target, level, (uint)format, (uint)type, pixels);
		}

		public void GetTextureImage(int texture, int level, PixelFormat format, PixelType type, int bufferSize, void* pixels)
		{
			Native.glGetTextureImage((uint)texture, level, (uint)format, (uint)type, bufferSize, pixels);
		}

		public void GetTextureSubImage(int texture, int level, int x, int y, int z, int width, int height, int depth, PixelFormat format, PixelType type, int bufferSize, void* pixels)
		{
			Native.glGetTextureSubImage((uint)texture, level, x, y, z, width, height, depth, (uint)format, (uint)type, bufferSize, pixels);
		}

		public int GenSampler()
		{
			GlSampler sampler;
			Native.glGenSamplers(1, &sampler);
			return (int)Unsafe.As<GlSampler, uint>(ref sampler);
		}

		public void DeleteSampler(int sampler)
		{
			GlSampler name = Unsafe.As<int, GlSampler>(ref sampler);
			Native.glDeleteSamplers(1, &name);
		}

		public void BindSampler(int unit, int sampler) => Native.glBindSampler((uint)unit, Unsafe.As<int, GlSampler>(ref sampler));
		public void SamplerParameteri(int sampler, SamplerParameterName name, int value) => Native.glSamplerParameteri(Unsafe.As<int, GlSampler>(ref sampler), (uint)name, value);

		#endregion

		#region Framebuffers

		public int GenFramebuffer()
		{
			GlFramebuffer framebuffer;
			Native.glGenFramebuffers(1, &framebuffer);
			return (int)framebuffer.Id;
		}

		public void DeleteFramebuffer(int framebuffer)
		{
			GlFramebuffer name = new GlFramebuffer((uint)framebuffer);
			Native.glDeleteFramebuffers(1, &name);
		}

		public void BindFramebuffer(FramebufferTarget target, int framebuffer) => Native.glBindFramebuffer((uint)target, new GlFramebuffer((uint)framebuffer));
		public FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target) => (FramebufferErrorCode)(uint)Native.glCheckFramebufferStatus((uint)target);

		public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textureTarget, int texture, int level)
		{
			Native.glFramebufferTexture2D((uint)target, (uint)attachment, (uint)textureTarget, (uint)texture, level);
		}

		public void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbufferTarget, int renderbuffer)
		{
			Native.glFramebufferRenderbuffer((uint)target, (uint)attachment, (uint)renderbufferTarget, Unsafe.As<int, GlRenderbuffer>(ref renderbuffer));
		}

		public void FramebufferParameteri(FramebufferTarget target, FramebufferDefaultParameter name, int value) => Native.glFramebufferParameteri((uint)target, (uint)name, value);
		public void DrawBuffer(DrawBufferMode buffer) => Native.glDrawBuffer((uint)buffer);
		public void DrawBuffers(int count, DrawBuffersEnum* buffers) => Native.glDrawBuffers(count, (GlEnum*)buffers);
		public void ReadBuffer(ReadBufferMode source) => Native.glReadBuffer((uint)source);

		public void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, void* pixels)
		{
			Native.glReadPixels(x, y, width, height, (uint)format, (uint)type, pixels);
		}

		public void BlitFramebuffer(int sourceX0, int sourceY0, int sourceX1, int sourceY1, int destinationX0, int destinationY0, int destinationX1, int destinationY1, ClearBufferMask mask, BlitFramebufferFilter filter)
		{
			Native.glBlitFramebuffer(sourceX0, sourceY0, sourceX1, sourceY1, destinationX0, destinationY0, destinationX1, destinationY1, (uint)mask, (uint)filter);
		}

		public int GenRenderbuffer()
		{
			GlRenderbuffer renderbuffer;
			Native.glGenRenderbuffers(1, &renderbuffer);
			return (int)Unsafe.As<GlRenderbuffer, uint>(ref renderbuffer);
		}

		public void DeleteRenderbuffer(int renderbuffer)
		{
			GlRenderbuffer name = Unsafe.As<int, GlRenderbuffer>(ref renderbuffer);
			Native.glDeleteRenderbuffers(1, &name);
		}

		public void BindRenderbuffer(RenderbufferTarget target, int renderbuffer) => Native.glBindRenderbuffer((uint)target, Unsafe.As<int, GlRenderbuffer>(ref renderbuffer));

		public void RenderbufferStorage(RenderbufferTarget target, RenderbufferStorage internalFormat, int width, int height)
		{
			Native.glRenderbufferStorage((uint)target, (uint)internalFormat, width, height);
		}

		#endregion

		#region Shaders and programs

		public int CreateShader(ShaderType type) => (int)Native.glCreateShader((GlShaderType)(uint)type).Id;
		public void DeleteShader(int shader) => Native.glDeleteShader(new GlShader((uint)shader));

		public void ShaderSource(int shader, string source)
		{
			int length = Encoding.UTF8.GetByteCount(source);
			byte[] rented = System.Buffers.ArrayPool<byte>.Shared.Rent(length + 1);
			try
			{
				Encoding.UTF8.GetBytes(source, 0, source.Length, rented, 0);
				rented[length] = 0;
				fixed (byte* text = rented)
				{
					byte* pointer = text;
					Native.glShaderSource(new GlShader((uint)shader), 1, &pointer, &length);
				}
			}
			finally
			{
				System.Buffers.ArrayPool<byte>.Shared.Return(rented);
			}
		}

		public void CompileShader(int shader) => Native.glCompileShader(new GlShader((uint)shader));

		public void GetShader(int shader, ShaderParameter name, out int value)
		{
			int result = 0;
			Native.glGetShaderiv(new GlShader((uint)shader), (uint)name, &result);
			value = result;
		}

		public string GetShaderInfoLog(int shader)
		{
			int length = 0;
			Native.glGetShaderiv(new GlShader((uint)shader), GL_INFO_LOG_LENGTH, &length);
			if (length <= 0)
			{
				return string.Empty;
			}

			byte[] buffer = new byte[length];
			int written = 0;
			fixed (byte* text = buffer)
			{
				Native.glGetShaderInfoLog(new GlShader((uint)shader), length, &written, text);
			}

			return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(written, length)));
		}

		public int CreateProgram() => (int)Native.glCreateProgram().Id;
		public void DeleteProgram(int program) => Native.glDeleteProgram(new GlProgram((uint)program));
		public void AttachShader(int program, int shader) => Native.glAttachShader(new GlProgram((uint)program), new GlShader((uint)shader));
		public void DetachShader(int program, int shader) => Native.glDetachShader(new GlProgram((uint)program), new GlShader((uint)shader));
		public void LinkProgram(int program) => Native.glLinkProgram(new GlProgram((uint)program));
		public void UseProgram(int program) => Native.glUseProgram(new GlProgram((uint)program));

		public void GetProgram(int program, GetProgramParameterName name, out int value)
		{
			int result = 0;
			Native.glGetProgramiv(new GlProgram((uint)program), (uint)name, &result);
			value = result;
		}

		public string GetProgramInfoLog(int program)
		{
			int length = 0;
			Native.glGetProgramiv(new GlProgram((uint)program), GL_INFO_LOG_LENGTH, &length);
			if (length <= 0)
			{
				return string.Empty;
			}

			byte[] buffer = new byte[length];
			int written = 0;
			fixed (byte* text = buffer)
			{
				Native.glGetProgramInfoLog(new GlProgram((uint)program), length, &written, text);
			}

			return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(written, length)));
		}

		public void ProgramParameter(int program, ProgramParameterName name, int value) => Native.glProgramParameteri(new GlProgram((uint)program), (uint)name, value);

		public void GetProgramBinary(int program, int bufferSize, out int length, out BinaryFormat format, void* binary)
		{
			int written = 0;
			GlEnum binaryFormat = default;
			Native.glGetProgramBinary(new GlProgram((uint)program), bufferSize, &written, &binaryFormat, binary);
			length = written;
			format = (BinaryFormat)(uint)binaryFormat;
		}

		public void ProgramBinary(int program, BinaryFormat format, void* binary, int length) => Native.glProgramBinary(new GlProgram((uint)program), (uint)format, binary, length);

		public void BindAttribLocation(int program, int index, string name)
		{
			fixed (byte* text = NullTerminated(name))
			{
				Native.glBindAttribLocation(new GlProgram((uint)program), (uint)index, text);
			}
		}

		public int GetAttribLocation(int program, string name)
		{
			fixed (byte* text = NullTerminated(name))
			{
				return Native.glGetAttribLocation(new GlProgram((uint)program), text);
			}
		}

		public int GetUniformLocation(int program, string name)
		{
			fixed (byte* text = NullTerminated(name))
			{
				return Native.glGetUniformLocation(new GlProgram((uint)program), text);
			}
		}

		public string GetActiveUniform(int program, int index, out int size, out ActiveUniformType type)
		{
			int maxLength = 0;
			Native.glGetProgramiv(new GlProgram((uint)program), GL_ACTIVE_UNIFORM_MAX_LENGTH, &maxLength);
			int capacity = maxLength <= 0 ? 1 : maxLength * 2;
			byte[] buffer = new byte[capacity];
			int written = 0;
			int uniformSize = 0;
			GlEnum uniformType = default;
			fixed (byte* text = buffer)
			{
				Native.glGetActiveUniform(new GlProgram((uint)program), (uint)index, capacity, &written, &uniformSize, &uniformType, text);
			}

			size = uniformSize;
			type = (ActiveUniformType)(uint)uniformType;
			return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, Math.Min(written, capacity)));
		}

		public void MaxShaderCompilerThreads(uint count, bool khr)
		{
			delegate* unmanaged[Cdecl]<uint, void> function = khr ? maxShaderCompilerThreadsKhr : maxShaderCompilerThreadsArb;
			if (function == null)
			{
				throw new NotSupportedException("The driver has no " + (khr ? "glMaxShaderCompilerThreadsKHR" : "glMaxShaderCompilerThreadsARB") + ".");
			}

			function(count);
		}

		public void Uniform1i(int location, int value) => Native.glUniform1i(location, value);
		public void Uniform1ui(int location, uint value) => Native.glUniform1ui(location, value);
		public void Uniform1f(int location, float value) => Native.glUniform1f(location, value);
		public void Uniform2i(int location, int x, int y) => Native.glUniform2i(location, x, y);
		public void Uniform2f(int location, float x, float y) => Native.glUniform2f(location, x, y);
		public void Uniform3i(int location, int x, int y, int z) => Native.glUniform3i(location, x, y, z);
		public void Uniform3f(int location, float x, float y, float z) => Native.glUniform3f(location, x, y, z);
		public void Uniform4i(int location, int x, int y, int z, int w) => Native.glUniform4i(location, x, y, z, w);
		public void Uniform4ui(int location, uint x, uint y, uint z, uint w) => Native.glUniform4ui(location, x, y, z, w);
		public void Uniform4f(int location, float x, float y, float z, float w) => Native.glUniform4f(location, x, y, z, w);
		public void Uniform1iv(int location, int count, int* values) => Native.glUniform1iv(location, count, values);
		public void Uniform1fv(int location, int count, float* values) => Native.glUniform1fv(location, count, values);
		public void Uniform4fv(int location, int count, float* values) => Native.glUniform4fv(location, count, values);
		public void UniformMatrix4fv(int location, int count, bool transpose, float* values) => Native.glUniformMatrix4fv(location, count, transpose, values);

		#endregion

		#region Drawing and compute

		public void DrawArrays(PrimitiveType mode, int first, int count) => Native.glDrawArrays((uint)mode, first, count);
		public void DrawArraysIndirect(PrimitiveType mode, IntPtr indirect) => Native.glDrawArraysIndirect((uint)mode, (void*)indirect);
		public void DrawElements(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices) => Native.glDrawElements((uint)mode, count, (uint)type, (void*)indices);

		public void DrawElementsBaseVertex(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int baseVertex)
		{
			Native.glDrawElementsBaseVertex((uint)mode, count, (uint)type, (void*)indices, baseVertex);
		}

		public void DrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect) => Native.glDrawElementsIndirect((uint)mode, (uint)type, (void*)indirect);

		public void DrawElementsInstancedBaseVertexBaseInstance(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int instanceCount, int baseVertex, int baseInstance)
		{
			Native.glDrawElementsInstancedBaseVertexBaseInstance((uint)mode, count, (uint)type, (void*)indices, instanceCount, baseVertex, (uint)baseInstance);
		}

		public void MultiDrawElementsBaseVertex(PrimitiveType mode, int* counts, DrawElementsType type, IntPtr* indices, int drawCount, int* baseVertices)
		{
			Native.glMultiDrawElementsBaseVertex((uint)mode, counts, (uint)type, (void**)indices, drawCount, baseVertices);
		}

		public void MultiDrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect, int drawCount, int stride)
		{
			Native.glMultiDrawElementsIndirect((uint)mode, (uint)type, (void*)indirect, drawCount, stride);
		}

		public void MultiDrawElementsIndirectCount(PrimitiveType mode, DrawElementsType type, IntPtr indirect, IntPtr drawCount, int maxDrawCount, int stride)
		{
			Native.glMultiDrawElementsIndirectCount((uint)mode, (uint)type, (void*)indirect, drawCount, maxDrawCount, stride);
		}

		public void DispatchCompute(int groupsX, int groupsY, int groupsZ) => Native.glDispatchCompute((uint)groupsX, (uint)groupsY, (uint)groupsZ);
		public void DispatchComputeIndirect(IntPtr indirect) => Native.glDispatchComputeIndirect(indirect);

		#endregion

		#region Queries and sync

		public void GenQueries(int count, int* ids) => Native.glGenQueries(count, (GlQuery*)ids);
		public void DeleteQueries(int count, int* ids) => Native.glDeleteQueries(count, (GlQuery*)ids);
		public void BeginQuery(QueryTarget target, int id) => Native.glBeginQuery((uint)target, new GlQuery((uint)id));
		public void EndQuery(QueryTarget target) => Native.glEndQuery((uint)target);
		public void QueryCounter(int id, QueryCounterTarget target) => Native.glQueryCounter((uint)id, (uint)target);

		public void GetQueryObject(int id, GetQueryObjectParam name, out int value)
		{
			int result = 0;
			Native.glGetQueryObjectiv((uint)id, (uint)name, &result);
			value = result;
		}

		public void GetQueryObject(int id, GetQueryObjectParam name, out long value)
		{
			long result = 0;
			Native.glGetQueryObjecti64v((uint)id, (uint)name, &result);
			value = result;
		}

		public IntPtr FenceSync(SyncCondition condition, WaitSyncFlags flags)
		{
			GlSync sync = Native.glFenceSync((uint)condition, (uint)flags);
			return Unsafe.As<GlSync, IntPtr>(ref sync);
		}

		public WaitSyncStatus ClientWaitSync(IntPtr sync, ClientWaitSyncFlags flags, long timeout)
		{
			return (WaitSyncStatus)(uint)Native.glClientWaitSync(new GlSync((void*)sync), (uint)flags, (ulong)timeout);
		}

		public void DeleteSync(IntPtr sync) => Native.glDeleteSync(new GlSync((void*)sync));

		#endregion

		private static byte[] NullTerminated(string text)
		{
			byte[] bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
			Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
			return bytes;
		}
	}
}
