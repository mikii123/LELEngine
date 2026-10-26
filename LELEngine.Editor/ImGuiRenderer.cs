using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using OpenTK.Graphics.OpenGL4;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Draws Dear ImGui draw data with OpenGL 4 into the bound framebuffer, and serves the ImGui 1.92 texture protocol
	///     (creates, updates and destroys the textures ImGui asks for, e.g. the font atlas as glyphs are added). Its objects
	///     belong to the GL context it was made in (vertex arrays are not shared between contexts): one per context.
	/// </summary>
	internal sealed unsafe class ImGuiRenderer : IDisposable
	{
		#region PrivateFields

		private int vertexArray;
		private int vertexBuffer;
		private int indexBuffer;
		private int vertexBufferSize;
		private int indexBufferSize;

		// Base vertex and first index of each draw list in this frame's buffers.
		private (int vertex, int index)[] drawLists = new (int, int)[16];
		private readonly List<(nint list, NVector4 clip)> allLists = new List<(nint, NVector4)>();
		private int program;
		private int projectionLocation;
		private int textureLocation;

		private const string VertexSource = @"#version 410 core
layout(location = 0) in vec2 Position;
layout(location = 1) in vec2 UV;
layout(location = 2) in vec4 Color;
uniform mat4 ProjMtx;
out vec2 Frag_UV;
out vec4 Frag_Color;
void main()
{
	Frag_UV = UV;
	Frag_Color = Color;
	gl_Position = ProjMtx * vec4(Position.xy, 0.0, 1.0);
}";

		private const string FragmentSource = @"#version 410 core
in vec2 Frag_UV;
in vec4 Frag_Color;
uniform sampler2D Texture;
layout(location = 0) out vec4 Out_Color;
void main()
{
	Out_Color = Frag_Color * texture(Texture, Frag_UV.st);
}";

		#endregion

		#region Constructors

		public ImGuiRenderer()
		{
			CreateDeviceObjects();
		}

		#endregion

		#region PublicMethods

		/// <summary>Draws the current context's ended frame into the bound framebuffer.</summary>
		public void Render(int framebufferWidth, int framebufferHeight)
		{
			ImDrawData* raw = ImGui.GetDrawData().Handle;
			if (raw == null)
			{
				return;
			}

			if (raw->Textures != null)
			{
				for (int i = 0; i < raw->Textures->Size; i++)
				{
					UpdateTexture(raw->Textures->Data[i]);
				}
			}

			if (raw->CmdListsCount == 0 || framebufferWidth <= 0 || framebufferHeight <= 0)
			{
				return;
			}

			allLists.Clear();
			for (int n = 0; n < raw->CmdListsCount; n++)
			{
				allLists.Add(((nint)raw->CmdLists.Data[n].Handle, NoClip));
			}

			Draw(allLists, raw->DisplayPos, raw->DisplaySize, framebufferWidth, framebufferHeight);
		}

		/// <summary>No clip beyond the draw commands' own.</summary>
		public static readonly NVector4 NoClip = new NVector4(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue);

		/// <summary>
		///     Draws some draw lists of a context's last frame (still valid until its next frame) as seen in the rectangle at
		///     <paramref name="displayPos" /> of <paramref name="displaySize" />, into the bound framebuffer of that size; each
		///     list draws only inside its clip rectangle (display coordinates: min x, min y, max x, max y). The textures are
		///     the ones the context's own frame uploaded.
		/// </summary>
		public void RenderLists(IReadOnlyList<(nint list, NVector4 clip)> lists, NVector2 displayPos, NVector2 displaySize, int framebufferWidth, int framebufferHeight)
		{
			if (lists.Count > 0 && framebufferWidth > 0 && framebufferHeight > 0)
			{
				Draw(lists, displayPos, displaySize, framebufferWidth, framebufferHeight);
			}
		}

		private void Draw(IReadOnlyList<(nint list, NVector4 clip)> lists, NVector2 displayPos, NVector2 displaySize, int framebufferWidth, int framebufferHeight)
		{
			GL.Enable(EnableCap.Blend);
			GL.BlendEquation(BlendEquationMode.FuncAdd);
			GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
			GL.Disable(EnableCap.CullFace);
			GL.Disable(EnableCap.DepthTest);
			GL.Disable(EnableCap.StencilTest);
			GL.Enable(EnableCap.ScissorTest);
			GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
			GL.Viewport(0, 0, framebufferWidth, framebufferHeight);

			float left = displayPos.X;
			float right = displayPos.X + displaySize.X;
			float top = displayPos.Y;
			float bottom = displayPos.Y + displaySize.Y;
			float* projection = stackalloc float[16]
			{
				2f / (right - left), 0f, 0f, 0f,
				0f, 2f / (top - bottom), 0f, 0f,
				0f, 0f, -1f, 0f,
				(right + left) / (left - right), (top + bottom) / (bottom - top), 0f, 1f
			};

			GL.UseProgram(program);
			GL.UniformMatrix4(projectionLocation, 1, false, projection);
			GL.Uniform1(textureLocation, 0);
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			GL.ActiveTexture(TextureUnit.Texture0);

			// All draw lists go into one fresh buffer store per frame. Orphaning (BufferData with no data) never waits
			// for the GPU to finish reading the previous frame's data; rewriting the store in place would wait for
			// it, which stalls the UI for as long as the driver holds the GPU (seconds right after a window resize).
			int totalVertexBytes = 0;
			int totalIndexBytes = 0;
			for (int n = 0; n < lists.Count; n++)
			{
				var list = (ImDrawList*)lists[n].list;
				totalVertexBytes += list->VtxBuffer.Size * sizeof(ImDrawVert);
				totalIndexBytes += list->IdxBuffer.Size * sizeof(ushort);
			}

			vertexBufferSize = Math.Max(vertexBufferSize, totalVertexBytes);
			indexBufferSize = Math.Max(indexBufferSize, totalIndexBytes);
			GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			if (drawLists.Length < lists.Count)
			{
				Array.Resize(ref drawLists, lists.Count * 2);
			}

			int vertexOffset = 0;
			int indexOffset = 0;
			for (int n = 0; n < lists.Count; n++)
			{
				var list = (ImDrawList*)lists[n].list;
				int vertexBytes = list->VtxBuffer.Size * sizeof(ImDrawVert);
				int indexBytes = list->IdxBuffer.Size * sizeof(ushort);
				GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)vertexOffset, vertexBytes, (IntPtr)list->VtxBuffer.Data);
				GL.BufferSubData(BufferTarget.ElementArrayBuffer, (IntPtr)indexOffset, indexBytes, (IntPtr)list->IdxBuffer.Data);
				drawLists[n] = (vertexOffset / sizeof(ImDrawVert), indexOffset / sizeof(ushort));
				vertexOffset += vertexBytes;
				indexOffset += indexBytes;
			}

			NVector2 clipOffset = displayPos;
			for (int n = 0; n < lists.Count; n++)
			{
				var list = (ImDrawList*)lists[n].list;
				NVector4 limit = lists[n].clip;
				(int baseVertex, int baseIndex) = drawLists[n];

				for (int c = 0; c < list->CmdBuffer.Size; c++)
				{
					ImDrawCmd* cmd = &list->CmdBuffer.Data[c];
					if (cmd->UserCallback != null)
					{
						continue;
					}

					float clipMinX = Math.Max(cmd->ClipRect.X, limit.X) - clipOffset.X;
					float clipMinY = Math.Max(cmd->ClipRect.Y, limit.Y) - clipOffset.Y;
					float clipMaxX = Math.Min(cmd->ClipRect.Z, limit.Z) - clipOffset.X;
					float clipMaxY = Math.Min(cmd->ClipRect.W, limit.W) - clipOffset.Y;
					if (clipMaxX <= clipMinX || clipMaxY <= clipMinY)
					{
						continue;
					}

					GL.Scissor((int)clipMinX, (int)(framebufferHeight - clipMaxY), (int)(clipMaxX - clipMinX), (int)(clipMaxY - clipMinY));
					ImTextureID texture = new ImDrawCmdPtr(cmd).GetTexID();
					GL.BindTexture(TextureTarget.Texture2D, (int)texture.Handle);
					GL.DrawElementsBaseVertex(PrimitiveType.Triangles, (int)cmd->ElemCount, DrawElementsType.UnsignedShort, (IntPtr)((baseIndex + cmd->IdxOffset) * sizeof(ushort)), baseVertex + (int)cmd->VtxOffset);
				}
			}

			// Back to the state the engine's passes expect.
			GL.Disable(EnableCap.ScissorTest);
			GL.Disable(EnableCap.Blend);
			GL.Enable(EnableCap.DepthTest);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.UseProgram(0);
		}

		/// <summary>Deletes the textures the current context alone uses (call with that context current, before destroying it).</summary>
		public static void DestroyOwnTextures()
		{
			ImGuiPlatformIOPtr platform = ImGui.GetPlatformIO();
			for (int i = 0; i < platform.Textures.Size; i++)
			{
				ImTextureDataPtr texture = platform.Textures.Data[i];
				if (texture.RefCount == 1 && texture.TexID.Handle != 0)
				{
					GL.DeleteTexture((int)texture.TexID.Handle);
					texture.SetTexID(ImTextureID.Null);
					texture.SetStatus(ImTextureStatus.Destroyed);
				}
			}
		}

		public void Dispose()
		{
			GL.DeleteBuffer(vertexBuffer);
			GL.DeleteBuffer(indexBuffer);
			GL.DeleteVertexArray(vertexArray);
			GL.DeleteProgram(program);
		}

		#endregion

		#region PrivateMethods

		private void CreateDeviceObjects()
		{
			int vertex = CompileShader(ShaderType.VertexShader, VertexSource);
			int fragment = CompileShader(ShaderType.FragmentShader, FragmentSource);
			program = GL.CreateProgram();
			GL.AttachShader(program, vertex);
			GL.AttachShader(program, fragment);
			GL.LinkProgram(program);
			GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
			if (linked == 0)
			{
				Debug.LogError("[ImGui] Shader link failed: " + GL.GetProgramInfoLog(program));
			}

			GL.DetachShader(program, vertex);
			GL.DetachShader(program, fragment);
			GL.DeleteShader(vertex);
			GL.DeleteShader(fragment);
			projectionLocation = GL.GetUniformLocation(program, "ProjMtx");
			textureLocation = GL.GetUniformLocation(program, "Texture");

			vertexArray = GL.GenVertexArray();
			vertexBuffer = GL.GenBuffer();
			indexBuffer = GL.GenBuffer();
			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			vertexBufferSize = 10000 * sizeof(ImDrawVert);
			indexBufferSize = 20000 * sizeof(ushort);
			GL.BufferData(BufferTarget.ArrayBuffer, vertexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);
			GL.BufferData(BufferTarget.ElementArrayBuffer, indexBufferSize, IntPtr.Zero, BufferUsageHint.StreamDraw);

			int stride = sizeof(ImDrawVert);
			GL.EnableVertexAttribArray(0);
			GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, 0);
			GL.EnableVertexAttribArray(1);
			GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 8);
			GL.EnableVertexAttribArray(2);
			GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, 16);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
		}

		private static int CompileShader(ShaderType type, string source)
		{
			int shader = GL.CreateShader(type);
			GL.ShaderSource(shader, source);
			GL.CompileShader(shader);
			GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
			if (compiled == 0)
			{
				Debug.LogError("[ImGui] Shader compile failed: " + GL.GetShaderInfoLog(shader));
			}

			return shader;
		}

		// ImGui 1.92 texture protocol: create / update / destroy what ImGui asks for.
		private static void UpdateTexture(ImTextureDataPtr texture)
		{
			switch (texture.Status)
			{
				case ImTextureStatus.WantCreate:
				{
					int handle = GL.GenTexture();
					GL.BindTexture(TextureTarget.Texture2D, handle);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
					GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, texture.Width, texture.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)texture.GetPixels());
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					GL.BindTexture(TextureTarget.Texture2D, 0);
					texture.SetTexID(new ImTextureID((ulong)handle));
					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantUpdates:
				{
					ImTextureRect rect = texture.UpdateRect;
					GL.BindTexture(TextureTarget.Texture2D, (int)texture.TexID.Handle);
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, texture.Width);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
					GL.TexSubImage2D(TextureTarget.Texture2D, 0, rect.X, rect.Y, rect.W, rect.H, PixelFormat.Rgba, PixelType.UnsignedByte, (IntPtr)texture.GetPixelsAt(rect.X, rect.Y));
					GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
					GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
					GL.BindTexture(TextureTarget.Texture2D, 0);
					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantDestroy when texture.UnusedFrames > 0:
					GL.DeleteTexture((int)texture.TexID.Handle);
					texture.SetTexID(ImTextureID.Null);
					texture.SetStatus(ImTextureStatus.Destroyed);
					break;
			}
		}

		#endregion
	}
}
