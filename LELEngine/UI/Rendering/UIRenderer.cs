using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.UIElements.Rendering
{
	/// <summary>
	///     Draws one panel. Element geometry lives in a persistent vertex buffer (each element owns a chunk that is
	///     rewritten only when the element repaints); per-element transforms, clipping, opacity and box parameters
	///     live in a storage buffer (updated when transforms change, without touching geometry). Every frame the
	///     index buffer lists the visible elements in draw order, split into batches of up to eight textures, so a
	///     typical screen is one or two draw calls.
	/// </summary>
	internal sealed class UIRenderer : IDisposable
	{
		#region PrivateFields

		[StructLayout(LayoutKind.Sequential, Pack = 4)]
		private struct UIVertex
		{
			public float x, y;
			public float u, v;
			public uint color;
			public uint element;
			public uint flags;
			public float extra;

			public const int Size = 32;
		}

		[StructLayout(LayoutKind.Sequential, Pack = 4)]
		private struct ElementGpuData
		{
			public Vector4 transform0, transform1, clipRect, sizeOpacity, radii, borderWidths;
			public Vector4 borderColorTop, borderColorRight, borderColorBottom, borderColorLeft;
			public Vector4 roundClipRect, roundClipRadii, roundClipInv0, roundClipInv1;
			public Vector4 textOutlineColor, textParams;

			public const int Size = 16 * 16;
		}

		private sealed class MeshHandle
		{
			public int vertexStart;
			public int vertexCount;
			public int vertexCapacity;
			public int[] indices = Array.Empty<int>();
			public int indexCount;
			public int[] textureIds = Array.Empty<int>();
			public int textureCount;
		}

		private struct Chunk
		{
			public int start;
			public int capacity;
		}

		private struct Batch
		{
			public int indexStart;
			public int indexCount;
			public int[] textureIds;
			public int textureCount;
		}

		private const uint KindBox = 0, KindText = 1, KindTexture = 2, KindTextShadow = 3;
		private const uint FlagClipToBox = 0x10000u;
		private const int MaxBatchTextures = 8;

		private ShaderProgram program;
		private int vao, vbo, ibo, ssbo, whiteTexture;
		private int vboCapacity, iboCapacity, ssboCapacity;
		private bool resourcesReady;

		private UIVertex[] vertices = new UIVertex[4096];
		private int vertexHighWater;
		private readonly List<Chunk> freeChunks = new List<Chunk>();
		private int freeVertexCount;
		private int vertexDirtyMin = int.MaxValue, vertexDirtyMax = -1;
		private bool vertexBufferReallocated = true;

		private MeshHandle[] meshes = new MeshHandle[256];
		private ElementGpuData[] elementData = new ElementGpuData[256];
		private int elementDirtyMin = int.MaxValue, elementDirtyMax = -1;
		private bool elementBufferReallocated = true;

		private uint[] indices = new uint[8192];
		private int indexCount;
		private bool indexDirty = true;
		private int lastDrawOrderVersion = -1;
		private readonly List<Batch> batches = new List<Batch>();

		private readonly Dictionary<object, int> textureIds = new Dictionary<object, int>();
		private readonly List<object> textureKeys = new List<object> { null };

		// Mesh building scratch
		private readonly List<UIVertex> buildVertices = new List<UIVertex>(256);
		private readonly List<int> buildIndices = new List<int>(512);
		private readonly List<int> buildTextures = new List<int>(4);
		private readonly int[] fontPageKeyScratch = new int[2];

		private readonly struct FontPageKey : IEquatable<FontPageKey>
		{
			public readonly FontAsset font;
			public readonly int page;

			public FontPageKey(FontAsset font, int page)
			{
				this.font = font;
				this.page = page;
			}

			public bool Equals(FontPageKey other) => ReferenceEquals(font, other.font) && page == other.page;
			public override bool Equals(object obj) => obj is FontPageKey other && Equals(other);
			public override int GetHashCode() => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(font), page);
		}

		#endregion

		#region PublicMethods

		/// <summary>Draws the panel into the currently bound target of the given pixel size.</summary>
		public void Render(BaseVisualElementPanel panel, int targetWidth, int targetHeight)
		{
			EnsureResources();
			UpdateMeshes(panel);
			UpdateElementData(panel);
			if (indexDirty || lastDrawOrderVersion != panel.drawOrderVersion)
			{
				RebuildDrawList(panel);
				lastDrawOrderVersion = panel.drawOrderVersion;
				indexDirty = false;
			}

			Upload();
			if (indexCount == 0)
			{
				return;
			}

			Draw(panel, targetWidth, targetHeight);
		}

		/// <summary>Frees what the renderer keeps for an element that left the panel.</summary>
		public void ReleaseElement(VisualElement element)
		{
			int slot = element.renderSlot;
			if (slot < 0 || slot >= meshes.Length)
			{
				return;
			}

			MeshHandle mesh = meshes[slot];
			if (mesh != null)
			{
				FreeChunk(mesh);
				meshes[slot] = null;
			}

			if (slot < elementData.Length)
			{
				elementData[slot] = default;
				MarkElementDirty(slot);
			}

			indexDirty = true;
		}

		public void Dispose()
		{
			if (!resourcesReady)
			{
				return;
			}

			GL.DeleteVertexArray(vao);
			GL.DeleteBuffer(vbo);
			GL.DeleteBuffer(ibo);
			GL.DeleteBuffer(ssbo);
			GL.DeleteTexture(whiteTexture);
			program?.Delete();
			program = null;
			resourcesReady = false;
		}

		#endregion

		#region PrivateMethods

		// ------------------------------------------------------------------ resources

		private void EnsureResources()
		{
			if (resourcesReady)
			{
				return;
			}

			var vertex = new Shader(UIShaders.Vertex, ShaderType.VertexShader, "UI");
			var fragment = new Shader(UIShaders.Fragment, ShaderType.FragmentShader, "UI");
			program = new ShaderProgram("Builtin/UI", vertex, fragment);
			vertex.Delete();
			fragment.Delete();

			vao = GL.GenVertexArray();
			vbo = GL.GenBuffer();
			ibo = GL.GenBuffer();
			ssbo = GL.GenBuffer();

			GL.BindVertexArray(vao);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
			GL.EnableVertexAttribArray(0);
			GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, UIVertex.Size, 0);
			GL.EnableVertexAttribArray(1);
			GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, UIVertex.Size, 8);
			GL.EnableVertexAttribArray(2);
			GL.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, UIVertex.Size, 16);
			GL.EnableVertexAttribArray(3);
			GL.VertexAttribIPointer(3, 1, VertexAttribIntegerType.UnsignedInt, UIVertex.Size, (IntPtr)20);
			GL.EnableVertexAttribArray(4);
			GL.VertexAttribIPointer(4, 1, VertexAttribIntegerType.UnsignedInt, UIVertex.Size, (IntPtr)24);
			GL.EnableVertexAttribArray(5);
			GL.VertexAttribPointer(5, 1, VertexAttribPointerType.Float, false, UIVertex.Size, 28);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, ibo);
			GL.BindVertexArray(0);

			whiteTexture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, whiteTexture);
			GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
			GL.BindTexture(TextureTarget.Texture2D, 0);

			resourcesReady = true;
			vertexBufferReallocated = true;
			elementBufferReallocated = true;
		}

		// ------------------------------------------------------------------ meshes

		private void UpdateMeshes(BaseVisualElementPanel panel)
		{
			if (panel.repaintDirty.Count == 0)
			{
				return;
			}

			// A snapshot: an element's content callback may dirty other elements (they render next frame).
			repaintBatch.Clear();
			repaintBatch.AddRange(panel.repaintDirty);
			panel.repaintDirty.Clear();
			foreach (VisualElement element in repaintBatch)
			{
				if (element.elementPanel != panel || element.renderSlot < 0)
				{
					continue;
				}

				element.dirtyFlags &= ~VersionChangeType.Repaint;
				GenerateMesh(element);
			}

			repaintBatch.Clear();
			indexDirty = true;
		}

		private readonly List<VisualElement> repaintBatch = new List<VisualElement>();

		private void GenerateMesh(VisualElement element)
		{
			int slot = element.renderSlot;
			EnsureSlotCapacity(slot);
			buildVertices.Clear();
			buildIndices.Clear();
			buildTextures.Clear();

			ComputedStyle style = element.computedStyle;
			Rect rect = element.layoutNode.rect;
			bool visible = !element.resolvedStyleDisplayNone && element.resolvedStyleVisible && rect.width > 0f && rect.height > 0f;
			if (visible)
			{
				try
				{
					BuildElementGeometry(element, style, rect);
				}
				catch (Exception e)
				{
					Debug.LogException(e, element);
				}
			}

			MeshHandle mesh = meshes[slot];
			int vertexCount = buildVertices.Count;
			if (vertexCount == 0)
			{
				if (mesh != null)
				{
					FreeChunk(mesh);
					meshes[slot] = null;
				}

				return;
			}

			if (mesh == null)
			{
				mesh = new MeshHandle();
				meshes[slot] = mesh;
			}

			if (vertexCount > mesh.vertexCapacity || mesh.vertexCapacity == 0)
			{
				FreeChunk(mesh);
				AllocateChunk(mesh, vertexCount);
			}

			mesh.vertexCount = vertexCount;
			uint elementIndex = (uint)slot;
			for (int i = 0; i < vertexCount; i++)
			{
				UIVertex v = buildVertices[i];
				v.element = elementIndex;
				vertices[mesh.vertexStart + i] = v;
			}

			MarkVertexDirty(mesh.vertexStart, mesh.vertexStart + vertexCount);

			if (mesh.indices.Length < buildIndices.Count)
			{
				mesh.indices = new int[Math.Max(buildIndices.Count, mesh.indices.Length * 2)];
			}

			buildIndices.CopyTo(mesh.indices);
			mesh.indexCount = buildIndices.Count;

			if (mesh.textureIds.Length < buildTextures.Count)
			{
				mesh.textureIds = new int[Math.Max(buildTextures.Count, 4)];
			}

			buildTextures.CopyTo(mesh.textureIds);
			mesh.textureCount = buildTextures.Count;
		}

		private void BuildElementGeometry(VisualElement element, ComputedStyle style, Rect rect)
		{
			float w = rect.width, h = rect.height;

			bool hasBackground = style.backgroundColor.A > 0f;
			bool hasBorder = (style.borderTopWidth > 0f && style.borderTopColor.A > 0f) || (style.borderRightWidth > 0f && style.borderRightColor.A > 0f)
				|| (style.borderBottomWidth > 0f && style.borderBottomColor.A > 0f) || (style.borderLeftWidth > 0f && style.borderLeftColor.A > 0f);
			if (hasBackground || hasBorder)
			{
				AddQuad(-1f, -1f, w + 2f, h + 2f, 0f, 0f, 1f, 1f, hasBackground ? style.backgroundColor : new Color4(0f, 0f, 0f, 0f), KindBox, 0, 0f);
			}

			Texture backgroundTexture = style.backgroundImage.texture;
			if (backgroundTexture != null && backgroundTexture.IsValid)
			{
				AddBackgroundImage(element, style, backgroundTexture, backgroundTexture.width, backgroundTexture.height, false, w, h);
			}
			else if (style.backgroundImage.renderTexture is LELEngine.Rendering.RenderTexture renderTexture && renderTexture.Handle != 0)
			{
				// A framebuffer's rows start at the bottom: drawn upside down.
				AddBackgroundImage(element, style, renderTexture, renderTexture.Width, renderTexture.Height, true, w, h);
			}

			if (element is TextElement textElement)
			{
				AddText(textElement, style);
			}

			Action<MeshGenerationContext> custom = element.generateVisualContent;
			if (custom != null)
			{
				var context = new MeshGenerationContext(element);
				custom(context);
				foreach (MeshWriteData allocation in context.allocations)
				{
					AddCustom(allocation);
				}
			}
		}

		private void AddQuad(float x, float y, float w, float h, float u0, float v0, float u1, float v1, Color4 color, uint kind, int textureId, float extra, uint extraFlags = 0)
		{
			uint packed = PackColor(color);
			uint flags = kind | ((uint)textureId << 8) | extraFlags;
			int baseIndex = buildVertices.Count;
			buildVertices.Add(new UIVertex { x = x, y = y, u = u0, v = v0, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x + w, y = y, u = u1, v = v0, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x + w, y = y + h, u = u1, v = v1, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x, y = y + h, u = u0, v = v1, color = packed, flags = flags, extra = extra });
			buildIndices.Add(baseIndex);
			buildIndices.Add(baseIndex + 1);
			buildIndices.Add(baseIndex + 2);
			buildIndices.Add(baseIndex);
			buildIndices.Add(baseIndex + 2);
			buildIndices.Add(baseIndex + 3);
			UseTexture(textureId);
		}

		private void UseTexture(int textureId)
		{
			if (textureId != 0 && !buildTextures.Contains(textureId))
			{
				buildTextures.Add(textureId);
			}
		}

		private static uint PackColor(Color4 c)
		{
			uint r = (uint)Math.Clamp((int)(c.R * 255f + 0.5f), 0, 255);
			uint g = (uint)Math.Clamp((int)(c.G * 255f + 0.5f), 0, 255);
			uint b = (uint)Math.Clamp((int)(c.B * 255f + 0.5f), 0, 255);
			uint a = (uint)Math.Clamp((int)(c.A * 255f + 0.5f), 0, 255);
			return r | (g << 8) | (b << 16) | (a << 24);
		}

		// ------------------------------------------------------------------ background images

		private void AddBackgroundImage(VisualElement element, ComputedStyle style, object textureKey, float tw, float th, bool flipV, float w, float h)
		{
			LayoutNodeData node = element.layoutNode;
			// Images are drawn inside the borders.
			float boxX = node.borderLeft, boxY = node.borderTop;
			float boxW = Math.Max(0f, w - node.borderLeft - node.borderRight);
			float boxH = Math.Max(0f, h - node.borderTop - node.borderBottom);
			if (boxW <= 0f || boxH <= 0f || tw <= 0f || th <= 0f)
			{
				return;
			}

			int textureId = GetTextureId(textureKey);
			Color4 tint = style.unityBackgroundImageTintColor;
			bool rounded = HasRadius(style);
			uint clipFlag = rounded ? FlagClipToBox : 0u;

			bool sliced = style.unitySliceLeft > 0 || style.unitySliceRight > 0 || style.unitySliceTop > 0 || style.unitySliceBottom > 0;
			if (sliced)
			{
				AddSlicedImage(style, textureId, tint, clipFlag, boxX, boxY, boxW, boxH, tw, th);
				return;
			}

			bool legacy = style.backgroundSize.sizeType == BackgroundSizeType.Length && style.backgroundSize.x.IsAuto && style.backgroundSize.y.IsAuto
				&& style.backgroundPositionX.keyword == BackgroundPositionKeyword.Left && style.backgroundPositionX.offset.value == 0f
				&& style.backgroundPositionY.keyword == BackgroundPositionKeyword.Top && style.backgroundPositionY.offset.value == 0f
				&& style.backgroundRepeat.x == Repeat.NoRepeat && style.backgroundRepeat.y == Repeat.NoRepeat;

			float iw, ih, ix, iy;
			Repeat repeatX = style.backgroundRepeat.x, repeatY = style.backgroundRepeat.y;
			if (legacy)
			{
				switch (style.unityBackgroundScaleMode)
				{
					case ScaleMode.ScaleAndCrop:
					{
						float scale = Math.Max(boxW / tw, boxH / th);
						iw = tw * scale;
						ih = th * scale;
						ix = boxX + (boxW - iw) * 0.5f;
						iy = boxY + (boxH - ih) * 0.5f;
						break;
					}

					case ScaleMode.ScaleToFit:
					{
						float scale = Math.Min(boxW / tw, boxH / th);
						iw = tw * scale;
						ih = th * scale;
						ix = boxX + (boxW - iw) * 0.5f;
						iy = boxY + (boxH - ih) * 0.5f;
						break;
					}

					default:
						iw = boxW;
						ih = boxH;
						ix = boxX;
						iy = boxY;
						break;
				}
			}
			else
			{
				BackgroundSize size = style.backgroundSize;
				switch (size.sizeType)
				{
					case BackgroundSizeType.Cover:
					{
						float scale = Math.Max(boxW / tw, boxH / th);
						iw = tw * scale;
						ih = th * scale;
						break;
					}

					case BackgroundSizeType.Contain:
					{
						float scale = Math.Min(boxW / tw, boxH / th);
						iw = tw * scale;
						ih = th * scale;
						break;
					}

					default:
					{
						float sx = size.x.Resolve(boxW);
						float sy = size.y.Resolve(boxH);
						if (float.IsNaN(sx) && float.IsNaN(sy))
						{
							iw = tw;
							ih = th;
						}
						else if (float.IsNaN(sx))
						{
							ih = sy;
							iw = tw * (sy / th);
						}
						else if (float.IsNaN(sy))
						{
							iw = sx;
							ih = th * (sx / tw);
						}
						else
						{
							iw = sx;
							ih = sy;
						}

						break;
					}
				}

				if (repeatX == Repeat.Round && iw > 0f)
				{
					int count = Math.Max(1, (int)Math.Round(boxW / iw));
					iw = boxW / count;
				}

				if (repeatY == Repeat.Round && ih > 0f)
				{
					int count = Math.Max(1, (int)Math.Round(boxH / ih));
					ih = boxH / count;
				}

				ix = boxX + PositionOffset(style.backgroundPositionX, boxW, iw);
				iy = boxY + PositionOffset(style.backgroundPositionY, boxH, ih);
			}

			if (iw <= 0f || ih <= 0f)
			{
				return;
			}

			var box = new Rect(boxX, boxY, boxW, boxH);
			bool tileX = repeatX != Repeat.NoRepeat;
			bool tileY = repeatY != Repeat.NoRepeat;
			int startX = tileX ? (int)Math.Floor((boxX - ix) / iw) : 0;
			int endX = tileX ? (int)Math.Ceiling((boxX + boxW - ix) / iw) : 1;
			int startY = tileY ? (int)Math.Floor((boxY - iy) / ih) : 0;
			int endY = tileY ? (int)Math.Ceiling((boxY + boxH - iy) / ih) : 1;
			int tiles = 0;
			for (int ty = startY; ty < endY && tiles < 1024; ty++)
			{
				for (int tx = startX; tx < endX && tiles < 1024; tx++, tiles++)
				{
					var tile = new Rect(ix + tx * iw, iy + ty * ih, iw, ih);
					Rect clipped = Rect.Intersect(tile, box);
					if (clipped.IsEmpty)
					{
						continue;
					}

					float u0 = (clipped.xMin - tile.xMin) / iw;
					float v0 = (clipped.yMin - tile.yMin) / ih;
					float u1 = (clipped.xMax - tile.xMin) / iw;
					float v1 = (clipped.yMax - tile.yMin) / ih;
					if (flipV)
					{
						v0 = 1f - v0;
						v1 = 1f - v1;
					}

					AddQuad(clipped.x, clipped.y, clipped.width, clipped.height, u0, v0, u1, v1, tint, KindTexture, textureId, 0f, clipFlag);
				}
			}
		}

		private static float PositionOffset(BackgroundPosition position, float boxSize, float imageSize)
		{
			float offset = position.offset.Resolve(boxSize - imageSize);
			if (float.IsNaN(offset)) offset = 0f;
			switch (position.keyword)
			{
				case BackgroundPositionKeyword.Center: return (boxSize - imageSize) * 0.5f + offset;
				case BackgroundPositionKeyword.Right:
				case BackgroundPositionKeyword.Bottom: return boxSize - imageSize - offset;
				default: return offset;
			}
		}

		private void AddSlicedImage(ComputedStyle style, int textureId, Color4 tint, uint clipFlag, float x, float y, float w, float h, float tw, float th)
		{
			float scale = Math.Max(0f, style.unitySliceScale);
			float left = style.unitySliceLeft, right = style.unitySliceRight, top = style.unitySliceTop, bottom = style.unitySliceBottom;
			float dl = left * scale, dr = right * scale, dt = top * scale, db = bottom * scale;
			// Shrink the slices when the box is smaller than their sum.
			if (dl + dr > w && dl + dr > 0f)
			{
				float f = w / (dl + dr);
				dl *= f;
				dr *= f;
			}

			if (dt + db > h && dt + db > 0f)
			{
				float f = h / (dt + db);
				dt *= f;
				db *= f;
			}

			float[] xs = { x, x + dl, x + w - dr, x + w };
			float[] ys = { y, y + dt, y + h - db, y + h };
			float[] us = { 0f, left / tw, (tw - right) / tw, 1f };
			float[] vs = { 0f, top / th, (th - bottom) / th, 1f };
			for (int row = 0; row < 3; row++)
			{
				for (int column = 0; column < 3; column++)
				{
					float qw = xs[column + 1] - xs[column];
					float qh = ys[row + 1] - ys[row];
					if (qw <= 0f || qh <= 0f)
					{
						continue;
					}

					AddQuad(xs[column], ys[row], qw, qh, us[column], vs[row], us[column + 1], vs[row + 1], tint, KindTexture, textureId, 0f, clipFlag);
				}
			}
		}

		private static bool HasRadius(ComputedStyle style)
		{
			return style.borderTopLeftRadius.value > 0f || style.borderTopRightRadius.value > 0f || style.borderBottomRightRadius.value > 0f || style.borderBottomLeftRadius.value > 0f;
		}

		// ------------------------------------------------------------------ text

		private void AddText(TextElement element, ComputedStyle style)
		{
			TextLayoutResult layout = element.GetRenderLayout();
			if (layout == null || (layout.glyphs.Count == 0 && layout.decorations.Count == 0))
			{
				return;
			}

			Rect content = element.contentRect;
			float ox = content.x, oy = content.y;
			InheritedStyle inherited = style.inherited;
			TextShadow shadow = inherited.textShadow;
			bool hasShadow = shadow.color.A > 0f && (shadow.offset != Vector2.Zero || shadow.blurRadius > 0f);
			if (hasShadow)
			{
				for (int i = 0; i < layout.glyphs.Count; i++)
				{
					TextGlyphQuad g = layout.glyphs[i];
					Color4 color = new Color4(shadow.color.R, shadow.color.G, shadow.color.B, shadow.color.A * g.color.A);
					AddGlyph(g, ox + shadow.offset.X, oy + shadow.offset.Y, color, KindTextShadow, shadow.blurRadius);
				}
			}

			for (int i = 0; i < layout.glyphs.Count; i++)
			{
				TextGlyphQuad g = layout.glyphs[i];
				AddGlyph(g, ox, oy, g.color, KindText, g.boldness);
			}

			for (int i = 0; i < layout.decorations.Count; i++)
			{
				TextDecoration d = layout.decorations[i];
				AddQuad(ox + d.x, oy + d.y, d.width, d.height, 0f, 0f, 1f, 1f, d.color, KindTexture, 0, 0f);
			}
		}

		private void AddGlyph(in TextGlyphQuad g, float ox, float oy, Color4 color, uint kind, float extra)
		{
			int textureId = GetTextureId(new FontPageKey(g.font, g.page));
			uint packed = PackColor(color);
			uint flags = kind | ((uint)textureId << 8);
			float x = ox + g.x, y = oy + g.y;
			float topShear = g.shear * (g.baseline - g.y);
			float bottomShear = g.shear * (g.baseline - (g.y + g.height));
			int baseIndex = buildVertices.Count;
			buildVertices.Add(new UIVertex { x = x + topShear, y = y, u = g.u0, v = g.v0, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x + g.width + topShear, y = y, u = g.u1, v = g.v0, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x + g.width + bottomShear, y = y + g.height, u = g.u1, v = g.v1, color = packed, flags = flags, extra = extra });
			buildVertices.Add(new UIVertex { x = x + bottomShear, y = y + g.height, u = g.u0, v = g.v1, color = packed, flags = flags, extra = extra });
			buildIndices.Add(baseIndex);
			buildIndices.Add(baseIndex + 1);
			buildIndices.Add(baseIndex + 2);
			buildIndices.Add(baseIndex);
			buildIndices.Add(baseIndex + 2);
			buildIndices.Add(baseIndex + 3);
			UseTexture(textureId);
		}

		// ------------------------------------------------------------------ custom geometry

		private void AddCustom(MeshWriteData data)
		{
			if (data.writtenVertices == 0 || data.writtenIndices == 0)
			{
				return;
			}

			int textureId = data.texture != null && data.texture.IsValid ? GetTextureId(data.texture) : 0;
			uint flags = KindTexture | ((uint)textureId << 8);
			int baseIndex = buildVertices.Count;
			for (int i = 0; i < data.writtenVertices; i++)
			{
				Vertex v = data.vertices[i];
				buildVertices.Add(new UIVertex { x = v.position.X, y = v.position.Y, u = v.uv.X, v = v.uv.Y, color = PackColor(v.tint), flags = flags });
			}

			for (int i = 0; i < data.writtenIndices; i++)
			{
				int index = data.indices[i];
				if (index < data.writtenVertices)
				{
					buildIndices.Add(baseIndex + index);
				}
			}

			UseTexture(textureId);
		}

		// ------------------------------------------------------------------ element data

		private void UpdateElementData(BaseVisualElementPanel panel)
		{
			List<VisualElement> order = panel.drawOrder;
			bool any = false;
			for (int i = 0; i < order.Count; i++)
			{
				VisualElement element = order[i];
				if (!element.renderDataDirty || element.renderSlot < 0)
				{
					continue;
				}

				element.renderDataDirty = false;
				EnsureSlotCapacity(element.renderSlot);
				ComputeElementData(element, ref elementData[element.renderSlot]);
				MarkElementDirty(element.renderSlot);
				any = true;
			}

			if (any)
			{
				// Visibility and culling depend on transforms.
				indexDirty = true;
			}
		}

		private static void ComputeElementData(VisualElement element, ref ElementGpuData data)
		{
			ComputedStyle style = element.computedStyle;
			Matrix2x3 world = element.worldTransformRef;
			Rect rect = element.layoutNode.rect;
			data.transform0 = new Vector4(world.m00, world.m01, world.tx, 0f);
			data.transform1 = new Vector4(world.m10, world.m11, world.ty, 0f);
			Rect clip = element.worldClipRef;
			data.clipRect = new Vector4(clip.xMin, clip.yMin, clip.xMax, clip.yMax);

			float opacity = element.resolvedStyleVisible && !element.resolvedStyleDisplayNone ? element.worldOpacity : 0f;
			Vector4 radii = ResolveRadii(style, rect.width, rect.height);
			data.radii = radii;
			data.borderWidths = new Vector4(style.borderTopWidth, style.borderRightWidth, style.borderBottomWidth, style.borderLeftWidth);
			data.borderColorTop = ToVector(style.borderTopColor);
			data.borderColorRight = ToVector(style.borderRightColor);
			data.borderColorBottom = ToVector(style.borderBottomColor);
			data.borderColorLeft = ToVector(style.borderLeftColor);

			uint flags = 0;
			VisualElement clipper = FindRoundClipper(element);
			if (clipper != null)
			{
				flags |= 1;
				ComputedStyle cs = clipper.computedStyle;
				Rect box = TransformUpdater.ClipBox(clipper, cs);
				Vector4 clipperRadii = ResolveRadii(cs, clipper.layoutNode.rect.width, clipper.layoutNode.rect.height);
				// The clip box sits inside the borders: shrink the radii accordingly.
				clipperRadii.X = Math.Max(0f, clipperRadii.X - Math.Max(cs.borderTopWidth, cs.borderLeftWidth));
				clipperRadii.Y = Math.Max(0f, clipperRadii.Y - Math.Max(cs.borderTopWidth, cs.borderRightWidth));
				clipperRadii.Z = Math.Max(0f, clipperRadii.Z - Math.Max(cs.borderBottomWidth, cs.borderRightWidth));
				clipperRadii.W = Math.Max(0f, clipperRadii.W - Math.Max(cs.borderBottomWidth, cs.borderLeftWidth));
				Matrix2x3 inverse = clipper.GetWorldInverse();
				data.roundClipRect = new Vector4(box.x, box.y, box.width, box.height);
				data.roundClipRadii = clipperRadii;
				data.roundClipInv0 = new Vector4(inverse.m00, inverse.m01, inverse.tx, 0f);
				data.roundClipInv1 = new Vector4(inverse.m10, inverse.m11, inverse.ty, 0f);
			}

			data.sizeOpacity = new Vector4(rect.width, rect.height, opacity, BitConverter.UInt32BitsToSingle(flags));

			InheritedStyle inherited = style.inherited;
			data.textOutlineColor = ToVector(inherited.unityTextOutlineColor);
			data.textParams = new Vector4(Math.Max(0f, inherited.unityTextOutlineWidth), 0f, 0f, 0f);
		}

		/// <summary>Nearest ancestor that clips with rounded corners or a rotated / scaled transform.</summary>
		private static VisualElement FindRoundClipper(VisualElement element)
		{
			for (VisualElement e = element.m_PhysicalParent; e != null; e = e.m_PhysicalParent)
			{
				if (e.computedStyle.overflow != Overflow.Hidden)
				{
					continue;
				}

				if (HasRadius(e.computedStyle) || !e.worldTransformRef.isAxisAligned)
				{
					return e;
				}
			}

			return null;
		}

		private static Vector4 ResolveRadii(ComputedStyle style, float w, float h)
		{
			float reference = Math.Min(w, h);
			float tl = Radius(style.borderTopLeftRadius, reference);
			float tr = Radius(style.borderTopRightRadius, reference);
			float br = Radius(style.borderBottomRightRadius, reference);
			float bl = Radius(style.borderBottomLeftRadius, reference);
			// CSS: scale all radii down so adjacent ones never overlap.
			float f = 1f;
			if (tl + tr > w && tl + tr > 0f) f = Math.Min(f, w / (tl + tr));
			if (bl + br > w && bl + br > 0f) f = Math.Min(f, w / (bl + br));
			if (tl + bl > h && tl + bl > 0f) f = Math.Min(f, h / (tl + bl));
			if (tr + br > h && tr + br > 0f) f = Math.Min(f, h / (tr + br));
			return new Vector4(tl * f, tr * f, br * f, bl * f);
		}

		private static float Radius(Length length, float reference)
		{
			float v = length.Resolve(reference);
			return float.IsNaN(v) || v < 0f ? 0f : v;
		}

		private static Vector4 ToVector(Color4 c)
		{
			return new Vector4(c.R, c.G, c.B, c.A);
		}

		// ------------------------------------------------------------------ draw list

		private void RebuildDrawList(BaseVisualElementPanel panel)
		{
			indexCount = 0;
			batches.Clear();
			var panelRect = new Rect(0f, 0f, panel.size.X, panel.size.Y);
			var batch = new Batch { indexStart = 0, textureIds = new int[MaxBatchTextures], textureCount = 1 };
			batch.textureIds[0] = 0;

			List<VisualElement> order = panel.drawOrder;
			for (int i = 0; i < order.Count; i++)
			{
				VisualElement element = order[i];
				int slot = element.renderSlot;
				if (slot < 0 || slot >= meshes.Length)
				{
					continue;
				}

				MeshHandle mesh = meshes[slot];
				if (mesh == null || mesh.indexCount == 0 || !element.resolvedStyleVisible || element.resolvedStyleDisplayNone || element.worldOpacity <= 0f)
				{
					continue;
				}

				// Cull what lies entirely outside its clip or the panel.
				Rect bounds = element.worldTransformRef.TransformRect(new Rect(-1f, -1f, element.layoutNode.rect.width + 2f, element.layoutNode.rect.height + 2f));
				if (!bounds.Overlaps(element.worldClipRef) || !bounds.Overlaps(panelRect))
				{
					continue;
				}

				// Textures of this element must all fit the batch.
				for (int t = 0; t < mesh.textureCount; t++)
				{
					int id = mesh.textureIds[t];
					if (Array.IndexOf(batch.textureIds, id, 0, batch.textureCount) >= 0)
					{
						continue;
					}

					if (batch.textureCount >= MaxBatchTextures)
					{
						batch.indexCount = indexCount - batch.indexStart;
						if (batch.indexCount > 0)
						{
							batches.Add(batch);
						}

						batch = new Batch { indexStart = indexCount, textureIds = new int[MaxBatchTextures], textureCount = 1 };
						batch.textureIds[0] = 0;
						// Re-add every texture of this element to the fresh batch.
						t = -1;
						continue;
					}

					batch.textureIds[batch.textureCount++] = id;
				}

				EnsureIndexCapacity(indexCount + mesh.indexCount);
				uint vertexBase = (uint)mesh.vertexStart;
				int[] source = mesh.indices;
				for (int k = 0; k < mesh.indexCount; k++)
				{
					indices[indexCount++] = vertexBase + (uint)source[k];
				}
			}

			batch.indexCount = indexCount - batch.indexStart;
			if (batch.indexCount > 0)
			{
				batches.Add(batch);
			}
		}

		// ------------------------------------------------------------------ GPU upload and draw

		private void Upload()
		{
			// Vertices
			if (vertexBufferReallocated || vboCapacity < vertexHighWater)
			{
				vboCapacity = Math.Max(4096, Math.Max(vertexHighWater, vboCapacity * 2));
				if (vboCapacity > vertices.Length)
				{
					vboCapacity = vertices.Length;
				}

				GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
				GL.BufferData(BufferTarget.ArrayBuffer, vboCapacity * UIVertex.Size, vertices, BufferUsageHint.DynamicDraw);
				vertexBufferReallocated = false;
				vertexDirtyMin = int.MaxValue;
				vertexDirtyMax = -1;
			}
			else if (vertexDirtyMax > vertexDirtyMin)
			{
				GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
				int count = vertexDirtyMax - vertexDirtyMin;
				GL.BufferSubData(BufferTarget.ArrayBuffer, (IntPtr)(vertexDirtyMin * UIVertex.Size), count * UIVertex.Size, ref vertices[vertexDirtyMin]);
				vertexDirtyMin = int.MaxValue;
				vertexDirtyMax = -1;
			}

			// Element data
			if (elementBufferReallocated || ssboCapacity < elementData.Length)
			{
				ssboCapacity = elementData.Length;
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
				GL.BufferData(BufferTarget.ShaderStorageBuffer, ssboCapacity * ElementGpuData.Size, elementData, BufferUsageHint.DynamicDraw);
				elementBufferReallocated = false;
				elementDirtyMin = int.MaxValue;
				elementDirtyMax = -1;
			}
			else if (elementDirtyMax >= elementDirtyMin)
			{
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, ssbo);
				int count = elementDirtyMax - elementDirtyMin + 1;
				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)(elementDirtyMin * ElementGpuData.Size), count * ElementGpuData.Size, ref elementData[elementDirtyMin]);
				elementDirtyMin = int.MaxValue;
				elementDirtyMax = -1;
			}

			// Indices (rebuilt whole when the draw list changed)
			if (indexCount > 0)
			{
				GL.BindBuffer(BufferTarget.ElementArrayBuffer, ibo);
				if (iboCapacity < indexCount)
				{
					iboCapacity = Math.Max(indexCount, iboCapacity * 2);
					GL.BufferData(BufferTarget.ElementArrayBuffer, iboCapacity * sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
				}

				GL.BufferSubData(BufferTarget.ElementArrayBuffer, IntPtr.Zero, indexCount * sizeof(uint), indices);
			}

			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		private void Draw(BaseVisualElementPanel panel, int targetWidth, int targetHeight)
		{
			program.Use();
			program.SetVector2("uTargetSize", new Vector2(targetWidth, targetHeight));
			program.SetFloat("uScale", panel.scale);
			program.SetFloat("uSdfUnitRange", 2f * FontAsset.SdfSpread / FontAsset.AtlasSize);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, ssbo);

			GL.Disable(EnableCap.DepthTest);
			GL.DepthMask(false);
			GL.Disable(EnableCap.CullFace);
			GL.Disable(EnableCap.ScissorTest);
			GL.Enable(EnableCap.Blend);
			GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);

			GL.BindVertexArray(vao);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, ibo);
			int[] ids = new int[MaxBatchTextures];
			int samplerLocation = program.GetUniformLocation("uTextures[0]");
			int idsLocation = program.GetUniformLocation("uTextureIds[0]");
			int[] units = { 0, 1, 2, 3, 4, 5, 6, 7 };
			if (samplerLocation >= 0)
			{
				GL.Uniform1(samplerLocation, MaxBatchTextures, units);
			}

			foreach (Batch batch in batches)
			{
				for (int i = 0; i < MaxBatchTextures; i++)
				{
					int id = i < batch.textureCount ? batch.textureIds[i] : -1;
					ids[i] = id;
					GL.ActiveTexture(TextureUnit.Texture0 + i);
					GL.BindTexture(TextureTarget.Texture2D, id >= 0 ? ResolveTexture(id) : whiteTexture);
				}

				if (idsLocation >= 0)
				{
					GL.Uniform1(idsLocation, MaxBatchTextures, ids);
				}

				GL.DrawElements(PrimitiveType.Triangles, batch.indexCount, DrawElementsType.UnsignedInt, batch.indexStart * sizeof(uint));
			}

			GL.BindVertexArray(0);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.Disable(EnableCap.Blend);
			GL.DepthMask(true);
			GL.Enable(EnableCap.DepthTest);
		}

		// ------------------------------------------------------------------ textures

		private int GetTextureId(object key)
		{
			int id;
			if (textureIds.TryGetValue(key, out id))
			{
				return id;
			}

			id = textureKeys.Count;
			if (id > 255)
			{
				// Texture ids travel in 8 bits of the vertex flags; recycle the table.
				textureIds.Clear();
				textureKeys.Clear();
				textureKeys.Add(null);
				id = 1;
				indexDirty = true;
			}

			textureKeys.Add(key);
			textureIds[key] = id;
			return id;
		}

		private int ResolveTexture(int id)
		{
			if (id <= 0 || id >= textureKeys.Count)
			{
				return whiteTexture;
			}

			object key = textureKeys[id];
			switch (key)
			{
				case Texture texture:
				{
					int handle = texture.Handle;
					return handle != 0 ? handle : whiteTexture;
				}

				case FontPageKey page:
				{
					int handle = page.font.GetPageTexture(page.page);
					return handle != 0 ? handle : whiteTexture;
				}

				case LELEngine.Rendering.RenderTexture renderTexture:
					return renderTexture.Handle != 0 ? renderTexture.Handle : whiteTexture;
				default:
					return whiteTexture;
			}
		}

		// ------------------------------------------------------------------ memory management

		private void EnsureSlotCapacity(int slot)
		{
			if (slot < meshes.Length)
			{
				return;
			}

			int size = Math.Max(slot + 1, meshes.Length * 2);
			Array.Resize(ref meshes, size);
			Array.Resize(ref elementData, size);
			elementBufferReallocated = true;
		}

		private void MarkElementDirty(int slot)
		{
			elementDirtyMin = Math.Min(elementDirtyMin, slot);
			elementDirtyMax = Math.Max(elementDirtyMax, slot);
		}

		private void MarkVertexDirty(int start, int end)
		{
			vertexDirtyMin = Math.Min(vertexDirtyMin, start);
			vertexDirtyMax = Math.Max(vertexDirtyMax, end);
		}

		private void AllocateChunk(MeshHandle mesh, int vertexCount)
		{
			int capacity = (vertexCount + 7) & ~7;
			int bestIndex = -1;
			for (int i = 0; i < freeChunks.Count; i++)
			{
				if (freeChunks[i].capacity >= capacity && (bestIndex < 0 || freeChunks[i].capacity < freeChunks[bestIndex].capacity))
				{
					bestIndex = i;
					if (freeChunks[i].capacity == capacity)
					{
						break;
					}
				}
			}

			if (bestIndex >= 0)
			{
				Chunk chunk = freeChunks[bestIndex];
				mesh.vertexStart = chunk.start;
				mesh.vertexCapacity = capacity;
				freeVertexCount -= capacity;
				if (chunk.capacity > capacity)
				{
					freeChunks[bestIndex] = new Chunk { start = chunk.start + capacity, capacity = chunk.capacity - capacity };
				}
				else
				{
					freeChunks.RemoveAt(bestIndex);
				}

				return;
			}

			if (vertexHighWater + capacity > vertices.Length)
			{
				Array.Resize(ref vertices, Math.Max(vertices.Length * 2, vertexHighWater + capacity));
				vertexBufferReallocated = true;
			}

			mesh.vertexStart = vertexHighWater;
			mesh.vertexCapacity = capacity;
			vertexHighWater += capacity;
		}

		private void FreeChunk(MeshHandle mesh)
		{
			if (mesh.vertexCapacity == 0)
			{
				return;
			}

			if (mesh.vertexStart + mesh.vertexCapacity == vertexHighWater)
			{
				vertexHighWater -= mesh.vertexCapacity;
			}
			else
			{
				freeChunks.Add(new Chunk { start = mesh.vertexStart, capacity = mesh.vertexCapacity });
				freeVertexCount += mesh.vertexCapacity;
				if (freeChunks.Count > 64)
				{
					MergeFreeChunks();
				}
			}

			mesh.vertexCapacity = 0;
			mesh.vertexCount = 0;
		}

		private void MergeFreeChunks()
		{
			freeChunks.Sort((a, b) => a.start.CompareTo(b.start));
			for (int i = freeChunks.Count - 2; i >= 0; i--)
			{
				Chunk a = freeChunks[i];
				Chunk b = freeChunks[i + 1];
				if (a.start + a.capacity == b.start)
				{
					freeChunks[i] = new Chunk { start = a.start, capacity = a.capacity + b.capacity };
					freeChunks.RemoveAt(i + 1);
				}
			}

			// Trailing free space returns to the high-water mark.
			while (freeChunks.Count > 0)
			{
				Chunk last = freeChunks[freeChunks.Count - 1];
				if (last.start + last.capacity != vertexHighWater)
				{
					break;
				}

				vertexHighWater = last.start;
				freeVertexCount -= last.capacity;
				freeChunks.RemoveAt(freeChunks.Count - 1);
			}
		}

		private void EnsureIndexCapacity(int count)
		{
			if (count > indices.Length)
			{
				Array.Resize(ref indices, Math.Max(count, indices.Length * 2));
			}
		}

		#endregion
	}
}
