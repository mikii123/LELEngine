using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Graphics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Draws Dear ImGui draw data through the graphics device into the open render pass, and serves the ImGui 1.92
	///     texture protocol (creates, updates and destroys the textures ImGui asks for, e.g. the font atlas as glyphs are
	///     added). ImGui names a texture by its <see cref="GpuTexture.Id" />. One per window (graphics context).
	/// </summary>
	internal sealed unsafe class ImGuiRenderer : IDisposable
	{
		#region PrivateFields

		// ImDrawVert: position, UV, colour as four bytes.
		private static readonly VertexInputLayout vertexLayout = new VertexInputLayout(sizeof(ImDrawVert),
			new VertexAttributeDescriptor(0, VertexFormat.Float2, 0),
			new VertexAttributeDescriptor(1, VertexFormat.Float2, 8),
			new VertexAttributeDescriptor(2, VertexFormat.UByte4Normalized, 16));

		// The textures made for ImGui (font atlases), by id: ImGui keeps only the number.
		private static readonly Dictionary<int, GpuTexture> ownTextures = new Dictionary<int, GpuTexture>();
		private static byte[] updateRows = Array.Empty<byte>();

		private GpuBuffer vertexBuffer;
		private GpuBuffer indexBuffer;
		private int vertexBufferSize;
		private int indexBufferSize;

		// Base vertex and first index of each draw list in this frame's buffers.
		private (int vertex, int index)[] drawLists = new (int, int)[16];
		private readonly List<(nint list, NVector4 clip)> allLists = new List<(nint, NVector4)>();
		private GpuProgram program;
		private int projectionSlot;
		private int textureSlot;

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

		/// <summary>Draws the current context's ended frame into the open render pass.</summary>
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
		///     <paramref name="displayPos" /> of <paramref name="displaySize" />, into the open render pass's target of that size; each
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
			if (program == null || !program.IsLinked)
			{
				return;
			}

			CommandList commands = Gfx.Commands;
			ref RenderState state = ref commands.State;
			state.SetBlend(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
			state.ColorOperation = BlendOperation.Add;
			state.AlphaOperation = BlendOperation.Add;
			state.Culling = FaceCulling.None;
			state.DepthTest = false;
			state.Wireframe = false;
			commands.SetViewport(0, 0, framebufferWidth, framebufferHeight);

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

			commands.SetProgram(program);
			program.SetMatrices(projectionSlot, projection, 1);

			// All draw lists go into one fresh buffer store per frame. New storage (Allocate) never waits for the GPU
			// to finish reading the previous frame's data; rewriting the store in place would wait for it, which
			// stalls the UI for as long as the driver holds the GPU (seconds right after a window resize).
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
			vertexBuffer.Allocate(vertexBufferSize);
			indexBuffer.Allocate(indexBufferSize);
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
				if (vertexBytes > 0)
				{
					vertexBuffer.SetSubData(vertexOffset, list->VtxBuffer.Data, vertexBytes);
				}

				if (indexBytes > 0)
				{
					indexBuffer.SetSubData(indexOffset, list->IdxBuffer.Data, indexBytes);
				}

				drawLists[n] = (vertexOffset / sizeof(ImDrawVert), indexOffset / sizeof(ushort));
				vertexOffset += vertexBytes;
				indexOffset += indexBytes;
			}

			commands.SetVertexInput(vertexLayout);
			commands.SetVertexBuffer(0, vertexBuffer);
			commands.SetIndexBuffer(indexBuffer, IndexFormat.UInt16);

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

					// A texture freed since the UI named it draws nothing.
					GpuTexture texture = GpuTexture.FromId((int)new ImDrawCmdPtr(cmd).GetTexID().Handle);
					if (texture == null)
					{
						continue;
					}

					commands.SetScissor((int)clipMinX, (int)(framebufferHeight - clipMaxY), (int)(clipMaxX - clipMinX), (int)(clipMaxY - clipMinY));
					program.SetTexture(textureSlot, texture);
					commands.DrawIndexed((int)cmd->ElemCount, baseIndex + (int)cmd->IdxOffset, baseVertex + (int)cmd->VtxOffset);
				}
			}

			// Back to the state the engine's passes expect.
			commands.DisableScissor();
			state = ref commands.State;
			state.Blend = false;
			state.DepthTest = true;
			program.SetTexture(textureSlot, null);
			commands.SetProgram(null);
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
					DestroyTexture(texture);
				}
			}
		}

		public void Dispose()
		{
			vertexBuffer?.Dispose();
			indexBuffer?.Dispose();
			program?.Dispose();
			vertexBuffer = null;
			indexBuffer = null;
			program = null;
		}

		#endregion

		#region PrivateMethods

		private void CreateDeviceObjects()
		{
			var descriptor = new ProgramDescriptor { Name = "Editor/ImGui", Asynchronous = false };
			descriptor.Stages.Add(new KeyValuePair<ShaderStage, string>(ShaderStage.Vertex, VertexSource));
			descriptor.Stages.Add(new KeyValuePair<ShaderStage, string>(ShaderStage.Fragment, FragmentSource));
			descriptor.AttributeLocations.Add(new KeyValuePair<string, int>("Position", 0));
			descriptor.AttributeLocations.Add(new KeyValuePair<string, int>("UV", 1));
			descriptor.AttributeLocations.Add(new KeyValuePair<string, int>("Color", 2));
			program = Gfx.Device.CreateProgram(descriptor);
			program.Finish();
			if (!program.IsLinked)
			{
				Debug.LogError("[ImGui] Shader build failed: " + program.Log);
			}

			projectionSlot = program.GetUniformSlot("ProjMtx");
			textureSlot = program.GetUniformSlot("Texture");

			vertexBufferSize = 10000 * sizeof(ImDrawVert);
			indexBufferSize = 20000 * sizeof(ushort);
			vertexBuffer = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Vertex, vertexBufferSize, true, "ImGui vertices"));
			indexBuffer = Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Index, indexBufferSize, true, "ImGui indices"));
		}

		// ImGui 1.92 texture protocol: create / update / destroy what ImGui asks for.
		private static void UpdateTexture(ImTextureDataPtr texture)
		{
			switch (texture.Status)
			{
				case ImTextureStatus.WantCreate:
				{
					GpuTexture created = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA8, texture.Width, texture.Height, 1, TextureUsage.Sampled, "ImGui texture"));
					created.Sampler = Samplers.LinearClamp;
					created.SetData(0, texture.GetPixels(), (long)texture.Width * texture.Height * 4);
					ownTextures[created.Id] = created;
					texture.SetTexID(new ImTextureID((ulong)created.Id));
					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantUpdates:
				{
					ImTextureRect rect = texture.UpdateRect;
					GpuTexture existing = GpuTexture.FromId((int)texture.TexID.Handle);
					if (existing != null && rect.W > 0 && rect.H > 0)
					{
						// The rectangle's rows lie a whole texture row apart in ImGui's pixels: the texture takes them packed.
						int rowBytes = rect.W * 4;
						int size = rowBytes * rect.H;
						if (updateRows.Length < size)
						{
							updateRows = new byte[size];
						}

						fixed (byte* rows = updateRows)
						{
							for (int y = 0; y < rect.H; y++)
							{
								System.Buffer.MemoryCopy(texture.GetPixelsAt(rect.X, rect.Y + y), rows + (long)y * rowBytes, rowBytes, rowBytes);
							}

							existing.SetData(0, rect.X, rect.Y, 0, rect.W, rect.H, 1, rows, size);
						}
					}

					texture.SetStatus(ImTextureStatus.Ok);
					break;
				}
				case ImTextureStatus.WantDestroy when texture.UnusedFrames > 0:
					DestroyTexture(texture);
					break;
			}
		}

		private static void DestroyTexture(ImTextureDataPtr texture)
		{
			int id = (int)texture.TexID.Handle;
			if (ownTextures.Remove(id, out GpuTexture owned))
			{
				owned.Dispose();
			}

			texture.SetTexID(ImTextureID.Null);
			texture.SetStatus(ImTextureStatus.Destroyed);
		}

		#endregion
	}
}
