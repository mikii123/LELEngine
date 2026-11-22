using System;

namespace LELEngine.Graphics
{
	/// <summary>
	///     What executes the engine's graphics commands: every command of <see cref="GL" /> in its plainest form
	///     (pointers and sizes), implemented once per graphics API over the BRUTAL framework's bindings.
	/// </summary>
	internal abstract unsafe class GraphicsBackend
	{
		public abstract GraphicsApi Api { get; }

		#region State

		public abstract void Enable(EnableCap cap);
		public abstract void Disable(EnableCap cap);
		public abstract void Viewport(int x, int y, int width, int height);
		public abstract void Scissor(int x, int y, int width, int height);
		public abstract void ClearColor(float red, float green, float blue, float alpha);
		public abstract void ClearDepth(double depth);
		public abstract void Clear(ClearBufferMask mask);
		public abstract void ClearBufferfv(ClearBuffer buffer, int drawBuffer, float* value);
		public abstract void ClearBufferuiv(ClearBuffer buffer, int drawBuffer, uint* value);
		public abstract void ColorMask(bool red, bool green, bool blue, bool alpha);
		public abstract void DepthMask(bool flag);
		public abstract void DepthFunc(DepthFunction function);
		public abstract void CullFace(TriangleFace mode);
		public abstract void PolygonMode(TriangleFace face, PolygonMode mode);
		public abstract void PolygonOffset(float factor, float units);
		public abstract void BlendEquation(BlendEquationMode mode);
		public abstract void BlendEquationSeparate(BlendEquationMode modeRgb, BlendEquationMode modeAlpha);
		public abstract void BlendFunc(BlendingFactor source, BlendingFactor destination);
		public abstract void BlendFuncSeparate(BlendingFactorSrc sourceRgb, BlendingFactorDest destinationRgb, BlendingFactorSrc sourceAlpha, BlendingFactorDest destinationAlpha);
		public abstract void PixelStore(PixelStoreParameter name, int value);
		public abstract void Finish();
		public abstract void Flush();
		public abstract void MemoryBarrier(MemoryBarrierFlags barriers);
		public abstract int GetInteger(GetPName name);
		public abstract void GetIntegerv(GetPName name, int* data);
		public abstract void GetIntegeri(GetIndexedPName target, int index, int* data);
		public abstract float GetFloat(GetPName name);
		public abstract string GetString(StringName name);
		public abstract string GetStringi(StringNameIndexed name, int index);

		#endregion

		#region Buffers

		public abstract int GenBuffer();
		public abstract void DeleteBuffer(int buffer);
		public abstract void BindBuffer(BufferTarget target, int buffer);
		public abstract void BindBufferBase(BufferRangeTarget target, int index, int buffer);
		public abstract void BindBufferRange(BufferRangeTarget target, int index, int buffer, IntPtr offset, IntPtr size);
		public abstract void BufferData(BufferTarget target, IntPtr size, void* data, BufferUsageHint usage);
		public abstract void BufferSubData(BufferTarget target, IntPtr offset, IntPtr size, void* data);
		public abstract void GetBufferSubData(BufferTarget target, IntPtr offset, IntPtr size, void* data);
		public abstract void GetNamedBufferSubData(int buffer, IntPtr offset, IntPtr size, void* data);
		public abstract void CopyBufferSubData(BufferTarget readTarget, BufferTarget writeTarget, IntPtr readOffset, IntPtr writeOffset, IntPtr size);
		public abstract IntPtr MapBufferRange(BufferTarget target, IntPtr offset, IntPtr length, MapBufferAccessMask access);
		public abstract bool UnmapBuffer(BufferTarget target);

		#endregion

		#region Vertex arrays

		public abstract int GenVertexArray();
		public abstract void DeleteVertexArray(int array);
		public abstract void BindVertexArray(int array);
		public abstract void EnableVertexAttribArray(int index);
		public abstract void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, IntPtr offset);
		public abstract void VertexAttribIPointer(int index, int size, VertexAttribIntegerType type, int stride, IntPtr offset);
		public abstract void VertexAttribDivisor(int index, int divisor);

		#endregion

		#region Textures and samplers

		public abstract int GenTexture();
		public abstract void DeleteTexture(int texture);
		public abstract void ActiveTexture(TextureUnit unit);
		public abstract void BindTexture(TextureTarget target, int texture);
		public abstract void BindImageTexture(int unit, int texture, int level, bool layered, int layer, TextureAccess access, SizedInternalFormat format);
		public abstract void TexParameteri(TextureTarget target, TextureParameterName name, int value);
		public abstract void TexParameterf(TextureTarget target, TextureParameterName name, float value);
		public abstract void TexParameterfv(TextureTarget target, TextureParameterName name, float* values);
		public abstract void TextureParameteri(int texture, TextureParameterName name, int value);
		public abstract void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, void* pixels);
		public abstract void TexImage3D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int depth, int border, PixelFormat format, PixelType type, void* pixels);
		public abstract void TexStorage2D(TextureTarget2d target, int levels, SizedInternalFormat internalFormat, int width, int height);
		public abstract void TexStorage3D(TextureTarget3d target, int levels, SizedInternalFormat internalFormat, int width, int height, int depth);
		public abstract void TexSubImage2D(TextureTarget target, int level, int x, int y, int width, int height, PixelFormat format, PixelType type, void* pixels);
		public abstract void TexSubImage3D(TextureTarget target, int level, int x, int y, int z, int width, int height, int depth, PixelFormat format, PixelType type, void* pixels);
		public abstract void CompressedTexImage2D(TextureTarget target, int level, InternalFormat internalFormat, int width, int height, int border, int imageSize, void* data);
		public abstract void GenerateMipmap(GenerateMipmapTarget target);
		public abstract void ClearTexImage(int texture, int level, PixelFormat format, PixelType type, void* data);
		public abstract void CopyImageSubData(int source, ImageTarget sourceTarget, int sourceLevel, int sourceX, int sourceY, int sourceZ, int destination, ImageTarget destinationTarget, int destinationLevel, int destinationX, int destinationY, int destinationZ, int width, int height, int depth);
		public abstract void GetTexImage(TextureTarget target, int level, PixelFormat format, PixelType type, void* pixels);
		public abstract void GetTextureImage(int texture, int level, PixelFormat format, PixelType type, int bufferSize, void* pixels);
		public abstract void GetTextureSubImage(int texture, int level, int x, int y, int z, int width, int height, int depth, PixelFormat format, PixelType type, int bufferSize, void* pixels);
		public abstract int GenSampler();
		public abstract void DeleteSampler(int sampler);
		public abstract void BindSampler(int unit, int sampler);
		public abstract void SamplerParameteri(int sampler, SamplerParameterName name, int value);

		#endregion

		#region Framebuffers

		public abstract int GenFramebuffer();
		public abstract void DeleteFramebuffer(int framebuffer);
		public abstract void BindFramebuffer(FramebufferTarget target, int framebuffer);
		public abstract FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target);
		public abstract void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textureTarget, int texture, int level);
		public abstract void FramebufferRenderbuffer(FramebufferTarget target, FramebufferAttachment attachment, RenderbufferTarget renderbufferTarget, int renderbuffer);
		public abstract void FramebufferParameteri(FramebufferTarget target, FramebufferDefaultParameter name, int value);
		public abstract void DrawBuffer(DrawBufferMode buffer);
		public abstract void DrawBuffers(int count, DrawBuffersEnum* buffers);
		public abstract void ReadBuffer(ReadBufferMode source);
		public abstract void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, void* pixels);
		public abstract void BlitFramebuffer(int sourceX0, int sourceY0, int sourceX1, int sourceY1, int destinationX0, int destinationY0, int destinationX1, int destinationY1, ClearBufferMask mask, BlitFramebufferFilter filter);
		public abstract int GenRenderbuffer();
		public abstract void DeleteRenderbuffer(int renderbuffer);
		public abstract void BindRenderbuffer(RenderbufferTarget target, int renderbuffer);
		public abstract void RenderbufferStorage(RenderbufferTarget target, RenderbufferStorage internalFormat, int width, int height);

		#endregion

		#region Shaders and programs

		public abstract int CreateShader(ShaderType type);
		public abstract void DeleteShader(int shader);
		public abstract void ShaderSource(int shader, string source);
		public abstract void CompileShader(int shader);
		public abstract void GetShader(int shader, ShaderParameter name, out int value);
		public abstract string GetShaderInfoLog(int shader);
		public abstract int CreateProgram();
		public abstract void DeleteProgram(int program);
		public abstract void AttachShader(int program, int shader);
		public abstract void DetachShader(int program, int shader);
		public abstract void LinkProgram(int program);
		public abstract void UseProgram(int program);
		public abstract void GetProgram(int program, GetProgramParameterName name, out int value);
		public abstract string GetProgramInfoLog(int program);
		public abstract void ProgramParameter(int program, ProgramParameterName name, int value);
		public abstract void GetProgramBinary(int program, int bufferSize, out int length, out BinaryFormat format, void* binary);
		public abstract void ProgramBinary(int program, BinaryFormat format, void* binary, int length);
		public abstract void BindAttribLocation(int program, int index, string name);
		public abstract int GetAttribLocation(int program, string name);
		public abstract int GetUniformLocation(int program, string name);
		public abstract string GetActiveUniform(int program, int index, out int size, out ActiveUniformType type);
		public abstract void MaxShaderCompilerThreads(uint count, bool khr);

		public abstract void Uniform1i(int location, int value);
		public abstract void Uniform1ui(int location, uint value);
		public abstract void Uniform1f(int location, float value);
		public abstract void Uniform2i(int location, int x, int y);
		public abstract void Uniform2f(int location, float x, float y);
		public abstract void Uniform3i(int location, int x, int y, int z);
		public abstract void Uniform3f(int location, float x, float y, float z);
		public abstract void Uniform4i(int location, int x, int y, int z, int w);
		public abstract void Uniform4ui(int location, uint x, uint y, uint z, uint w);
		public abstract void Uniform4f(int location, float x, float y, float z, float w);
		public abstract void Uniform1iv(int location, int count, int* values);
		public abstract void Uniform1fv(int location, int count, float* values);
		public abstract void Uniform4fv(int location, int count, float* values);
		public abstract void UniformMatrix4fv(int location, int count, bool transpose, float* values);

		#endregion

		#region Drawing and compute

		public abstract void DrawArrays(PrimitiveType mode, int first, int count);
		public abstract void DrawArraysIndirect(PrimitiveType mode, IntPtr indirect);
		public abstract void DrawElements(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices);
		public abstract void DrawElementsBaseVertex(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int baseVertex);
		public abstract void DrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect);
		public abstract void DrawElementsInstancedBaseVertexBaseInstance(PrimitiveType mode, int count, DrawElementsType type, IntPtr indices, int instanceCount, int baseVertex, int baseInstance);
		public abstract void MultiDrawElementsBaseVertex(PrimitiveType mode, int* counts, DrawElementsType type, IntPtr* indices, int drawCount, int* baseVertices);
		public abstract void MultiDrawElementsIndirect(PrimitiveType mode, DrawElementsType type, IntPtr indirect, int drawCount, int stride);
		public abstract void MultiDrawElementsIndirectCount(PrimitiveType mode, DrawElementsType type, IntPtr indirect, IntPtr drawCount, int maxDrawCount, int stride);
		public abstract void DispatchCompute(int groupsX, int groupsY, int groupsZ);
		public abstract void DispatchComputeIndirect(IntPtr indirect);

		#endregion

		#region Queries and sync

		public abstract void GenQueries(int count, int* ids);
		public abstract void DeleteQueries(int count, int* ids);
		public abstract void BeginQuery(QueryTarget target, int id);
		public abstract void EndQuery(QueryTarget target);
		public abstract void QueryCounter(int id, QueryCounterTarget target);
		public abstract void GetQueryObject(int id, GetQueryObjectParam name, out int value);
		public abstract void GetQueryObject(int id, GetQueryObjectParam name, out long value);
		public abstract IntPtr FenceSync(SyncCondition condition, WaitSyncFlags flags);
		public abstract WaitSyncStatus ClientWaitSync(IntPtr sync, ClientWaitSyncFlags flags, long timeout);
		public abstract void DeleteSync(IntPtr sync);

		#endregion
	}
}
