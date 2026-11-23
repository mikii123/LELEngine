using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Import settings of an image asset (Unity's texture importer): what the file becomes (2D texture, cubemap,
	///     volume, sprites), color space and sampler state, with a preview. Edits go to a working copy until Apply
	///     writes the meta file and reimports the loaded asset in place; Revert discards them.
	/// </summary>
	internal static class TextureImporterInspector
	{
		#region PrivateFields

		private static readonly string[] TypeNames = { "Default", "Normal Map", "Sprite (2D and UI)" };
		private static readonly string[] ShapeNames = { "2D", "Cube", "3D" };
		private static readonly string[] FilterNames = { "Point (no filter)", "Bilinear", "Trilinear" };
		private static readonly string[] WrapNames = { "Repeat", "Clamp", "Mirror" };
		private static readonly string[] LayoutNames = { "Auto", "Horizontal Cross", "Vertical Cross", "Horizontal Strip", "Vertical Strip", "Equirectangular (panorama)", "Six Files (_px, _nx, _py, _ny, _pz, _nz)" };
		private static readonly string[] VolumeSourceNames = { "Grid of tiles in this image", "Numbered files next to this image" };
		private static readonly string[] SpriteModeNames = { "Single", "Multiple" };
		private static readonly int[] MaxSizes = { 0, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192 };
		private static readonly string[] MaxSizeNames = { "No limit", "32", "64", "128", "256", "512", "1024", "2048", "4096", "8192" };
		private static readonly string[] CompressionNames = { "BC7 (compressed)", "None", "BC7 High Quality (slow build)" };

		private static string path;
		private static TextureImporter working;
		private static bool dirty;
		private static int previewSlice;
		private static Vector2i sliceCell = new Vector2i(32, 32);
		private static Vector2i sliceOffset;
		private static Vector2i slicePadding;

		// Several images edited together: the others' paths and working copies (the shown one is path / working), and
		// where their settings differ.
		private static readonly List<string> otherPaths = new List<string>();
		private static readonly List<TextureImporter> otherWorking = new List<TextureImporter>();
		private static MixedValues mixed;

		#endregion

		#region PublicMethods

		public static void Draw(string assetPath, InspectorContext context)
		{
			Draw(new[] { assetPath }, context);
		}

		/// <summary>
		///     The import settings of one image or of several edited together: their shared settings, "—" where they
		///     differ; an edit sets that setting on all of them, Apply writes and reimports each, Revert discards them all.
		/// </summary>
		public static void Draw(IReadOnlyList<string> assetPaths, InspectorContext context)
		{
			if (!IsLoaded(assetPaths))
			{
				Load(assetPaths);
			}

			string assetPath = assetPaths[0];
			// Importer edits are not undo steps (Apply / Revert instead).
			context.UndoTarget = null;
			SerializedNode before = otherWorking.Count > 0 ? MultiEdit.NodeOf(working, new SerializationContext()) : null;
			mixed = otherWorking.Count > 0 ? MixedValues.Compare(working, otherWorking, new SerializationContext()) : null;
			context.Mixed = mixed;
			bool changed = false;
			try
			{
				changed = DrawSettings(context);
			}
			finally
			{
				context.Mixed = null;
				context.Path = "";
			}

			if (before != null && MultiEdit.Propagate(working, before, otherWorking, () => new SerializationContext(), null, context.Touched) > 0)
			{
				changed = true;
			}

			context.Touched.Clear();

			dirty |= changed;

			ImGui.Spacing();
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(!dirty);
			if (EditorGui.Button("Revert", new NVector2(width, 0)))
			{
				Load(assetPaths);
			}

			ImGui.SameLine();
			if (EditorGui.Button(otherPaths.Count > 0 ? "Apply to " + (otherPaths.Count + 1) + " images##apply" : "Apply", new NVector2(width, 0)))
			{
				Apply(context.Editor);
			}

			ImGui.EndDisabled();

			ImGui.Spacing();
			if (otherPaths.Count > 0)
			{
				ImGui.TextDisabled("The preview is of " + Path.GetFileName(assetPath) + ".");
			}

			DrawPreview(assetPath);
		}

		/// <summary>Forgets the working copy (selection changed to something else).</summary>
		public static void Reset()
		{
			path = null;
			working = null;
			dirty = false;
			otherPaths.Clear();
			otherWorking.Clear();
			mixed = null;
		}

		#endregion

		#region PrivateMethods

		private static bool DrawSettings(InspectorContext context)
		{
			bool changed = false;
			int type = (int)working.textureType;
			changed |= Combo("Texture Type", nameof(TextureImporter.textureType), ref type, TypeNames, context);
			working.textureType = (TextureImporterType)type;

			int shape = (int)working.shape;
			changed |= Combo("Texture Shape", nameof(TextureImporter.shape), ref shape, ShapeNames, context);
			working.shape = (TextureImporterShape)shape;

			ImGui.BeginDisabled(working.textureType == TextureImporterType.NormalMap);
			changed |= Checkbox("sRGB (Color Texture)", nameof(TextureImporter.sRGB), ref working.sRGB, context);
			ImGui.EndDisabled();
			changed |= Checkbox("Generate Mip Maps", nameof(TextureImporter.generateMipmaps), ref working.generateMipmaps, context);

			int filter = (int)working.filterMode;
			changed |= Combo("Filter Mode", nameof(TextureImporter.filterMode), ref filter, FilterNames, context);
			working.filterMode = (FilterMode)filter;

			if (working.shape != TextureImporterShape.Cube)
			{
				int wrap = (int)working.wrapMode;
				changed |= Combo("Wrap Mode", nameof(TextureImporter.wrapMode), ref wrap, WrapNames, context);
				working.wrapMode = (TextureWrap)wrap;
			}

			if (working.shape != TextureImporterShape.Texture3D)
			{
				context.Path = nameof(TextureImporter.anisoLevel);
				FieldDrawer.Label("Aniso Level", null, context);
				int aniso = working.anisoLevel;
				if (EditorGui.SliderInt("##aniso", ref aniso, 1, 16))
				{
					working.anisoLevel = aniso;
					changed = true;
					context.Touch();
				}

				if (context.IsMixed() && !ImGui.IsItemActive())
				{
					EditorGui.MixedOverlay();
				}
			}

			int sizeIndex = Math.Max(0, Array.IndexOf(MaxSizes, working.maxSize));
			if (Combo("Max Size", nameof(TextureImporter.maxSize), ref sizeIndex, MaxSizeNames, context))
			{
				working.maxSize = MaxSizes[sizeIndex];
				changed = true;
			}

			if (working.textureType != TextureImporterType.Sprite)
			{
				int compression = (int)working.compression;
				changed |= Combo("Build Compression", nameof(TextureImporter.compression), ref compression, CompressionNames, context);
				working.compression = (TextureCompression)compression;
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("How game builds store the texture (the editor shows the source image).\nBC7: compressed with every mip level baked, loads without decoding, a quarter of the memory.\nNone: uncompressed texels.\nBC7 High Quality: a search over every block mode, a little closer to the source, about 50x slower to build.");
				}
			}

			switch (working.shape)
			{
				case TextureImporterShape.Cube:
					ImGui.SeparatorText("Cubemap");
					int layout = (int)working.cubemapLayout;
					changed |= Combo("Layout", nameof(TextureImporter.cubemapLayout), ref layout, LayoutNames, context);
					working.cubemapLayout = (CubemapLayout)layout;
					if (working.cubemapLayout != CubemapLayout.SixFiles)
					{
						changed |= Int("Face Size (0 = auto)", nameof(TextureImporter.cubemapFaceSize), ref working.cubemapFaceSize, context);
					}

					break;
				case TextureImporterShape.Texture3D:
					ImGui.SeparatorText("Volume");
					int source = (int)working.volumeSource;
					changed |= Combo("Slices", nameof(TextureImporter.volumeSource), ref source, VolumeSourceNames, context);
					working.volumeSource = (Texture3DSource)source;
					if (working.volumeSource == Texture3DSource.Grid)
					{
						changed |= Int("Columns", nameof(TextureImporter.columns), ref working.columns, context);
						changed |= Int("Rows", nameof(TextureImporter.rows), ref working.rows, context);
						working.columns = Math.Max(1, working.columns);
						working.rows = Math.Max(1, working.rows);
					}

					break;
			}

			if (working.textureType == TextureImporterType.Sprite && working.shape == TextureImporterShape.Texture2D)
			{
				changed |= DrawSpriteSettings(context);
			}

			return changed;
		}

		private static bool IsLoaded(IReadOnlyList<string> assetPaths)
		{
			if (working == null || !string.Equals(path, assetPaths[0], StringComparison.OrdinalIgnoreCase) || otherPaths.Count != assetPaths.Count - 1)
			{
				return false;
			}

			for (int i = 1; i < assetPaths.Count; i++)
			{
				if (!string.Equals(otherPaths[i - 1], assetPaths[i], StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
			}

			return true;
		}

		private static void Load(IReadOnlyList<string> assetPaths)
		{
			path = assetPaths[0];
			working = Clone(ImporterOf(path));
			otherPaths.Clear();
			otherWorking.Clear();
			for (int i = 1; i < assetPaths.Count; i++)
			{
				otherPaths.Add(assetPaths[i]);
				otherWorking.Add(Clone(ImporterOf(assetPaths[i])));
			}

			dirty = false;
			previewSlice = 0;
		}

		private static TextureImporter ImporterOf(string assetPath)
		{
			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			var importer = meta.importer as TextureImporter;
			if (importer == null)
			{
				importer = TextureImporter.CreateDefault(assetPath);
				meta.importer = importer;
			}

			return importer;
		}

		private static TextureImporter Clone(TextureImporter importer)
		{
			return Documents.ReadObject<TextureImporter>(Documents.WriteObject(importer, DocumentFormat.Binary)) ?? new TextureImporter();
		}

		/// <summary>Writes every working copy into its meta file and reimports the image.</summary>
		private static void Apply(EditorApplication editor)
		{
			Apply(path, working);
			for (int i = 0; i < otherPaths.Count; i++)
			{
				Apply(otherPaths[i], otherWorking[i]);
			}

			dirty = false;
			editor.NotifyAssetsChanged();
			Lighting.GI.InvalidateStatic();
		}

		private static void Apply(string assetPath, TextureImporter settings)
		{
			AssetMeta meta = AssetDatabase.GetMeta(assetPath);
			settings.version++;
			meta.importer = Clone(settings);
			AssetDatabase.SaveMeta(assetPath, meta);
			TextureAssets.Reimport(assetPath);
			AssetThumbnails.Invalidate(assetPath);
			Debug.Log("[Editor] Reimported " + assetPath);
		}

		private static bool DrawSpriteSettings(InspectorContext context)
		{
			bool changed = false;
			ImGui.SeparatorText("Sprite");
			int mode = (int)working.spriteMode;
			changed |= Combo("Sprite Mode", nameof(TextureImporter.spriteMode), ref mode, SpriteModeNames, context);
			working.spriteMode = (SpriteImportMode)mode;

			context.Path = nameof(TextureImporter.pixelsPerUnit);
			FieldDrawer.Label("Pixels Per Unit", null, context);
			float ppu = working.pixelsPerUnit;
			if (EditorGui.DragFloat("##ppu", ref ppu, 1f, 0.01f, 100000f, context.IsMixed() ? EditorGui.MixedValue : "%.3f"))
			{
				working.pixelsPerUnit = ppu;
				changed = true;
				context.Touch();
			}

			context.Path = nameof(TextureImporter.spritePivot);
			FieldDrawer.Label("Pivot", null, context);
			Span<float> pivot = stackalloc float[] { working.spritePivot.X, working.spritePivot.Y };
			if (EditorGui.DragFloats("##pivot", pivot, context.MixedComponents(2), 0.01f))
			{
				working.spritePivot = new Vector2(pivot[0], pivot[1]);
				changed = true;
			}

			context.Path = nameof(TextureImporter.spriteBorder);
			FieldDrawer.Label("Border (L, B, R, T)", null, context);
			Span<float> border = stackalloc float[] { working.spriteBorder.X, working.spriteBorder.Y, working.spriteBorder.Z, working.spriteBorder.W };
			if (EditorGui.DragFloats("##border", border, context.MixedComponents(4), 1f))
			{
				working.spriteBorder = new Vector4(border[0], border[1], border[2], border[3]);
				changed = true;
			}

			context.Path = "";
			// Slicing makes a sheet's own sprites: one image at a time.
			if (working.spriteMode != SpriteImportMode.Multiple || otherPaths.Count > 0)
			{
				return changed;
			}

			ImGui.SeparatorText("Slice (grid)");
			Texture texture = TextureAssets.LoadTexture(path);
			int cellX = sliceCell.X, cellY = sliceCell.Y;
			FieldDrawer.Label("Cell Size", null, context);
			int[] cell = { cellX, cellY };
			if (EditorGui.DragInt2("##cell", ref cell[0], 1f, 1, 8192))
			{
				sliceCell = new Vector2i(Math.Max(1, cell[0]), Math.Max(1, cell[1]));
			}

			FieldDrawer.Label("Offset", null, context);
			int[] offset = { sliceOffset.X, sliceOffset.Y };
			if (EditorGui.DragInt2("##offset", ref offset[0], 1f, 0, 8192))
			{
				sliceOffset = new Vector2i(offset[0], offset[1]);
			}

			FieldDrawer.Label("Padding", null, context);
			int[] padding = { slicePadding.X, slicePadding.Y };
			if (EditorGui.DragInt2("##padding", ref padding[0], 1f, 0, 8192))
			{
				slicePadding = new Vector2i(padding[0], padding[1]);
			}

			ImGui.BeginDisabled(texture == null);
			if (EditorGui.Button("Slice", new NVector2(-1, 0)) && texture != null)
			{
				working.SliceGrid(texture.width, texture.height, sliceCell, sliceOffset, slicePadding, Path.GetFileNameWithoutExtension(path));
				changed = true;
			}

			ImGui.EndDisabled();

			object sprites = working.sprites;
			if (FieldDrawer.DrawValue("Sprites", typeof(List<SpriteMetaData>), ref sprites, null, context, 0))
			{
				working.sprites = sprites as List<SpriteMetaData> ?? working.sprites;
				changed = true;
			}

			return changed;
		}

		private static void DrawPreview(string assetPath)
		{
			Texture texture;
			try
			{
				texture = TextureAssets.LoadTexture(assetPath);
			}
			catch (Exception e)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot load: " + e.Message);
				return;
			}

			if (texture == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "The image could not be imported with these settings (see the console).");
				return;
			}

			ImGui.SeparatorText("Preview");
			string info;
			switch (texture)
			{
				case Cubemap cube:
					info = $"Cubemap {cube.size} x {cube.size} x 6, {(cube.isHdr ? "RGBA float" : cube.sRGB ? "sRGB" : "linear RGBA8")}";
					break;
				case Texture3D volume:
					info = $"Volume {volume.width} x {volume.height} x {volume.depth}, {(volume.sRGB ? "sRGB" : "linear RGBA8")}";
					break;
				case Texture2D image:
					info = $"{image.width} x {image.height}, {(image.isHdr ? "RGBA float" : image.sRGB ? "sRGB" : "linear RGBA8")}{(image.mipmapEnabled ? ", mipmaps" : "")}";
					break;
				default:
					info = texture.ToString();
					break;
			}

			try
			{
				long bytes = new FileInfo(AssetDatabase.ToAbsolute(assetPath)).Length;
				info += bytes >= 1024 * 1024 ? $", {bytes / (1024f * 1024f):0.0} MB" : $", {bytes / 1024f:0.0} KB";
			}
			catch (Exception)
			{
				// Size unavailable.
			}

			ImGui.TextDisabled(info);

			if (texture is Texture3D volumeTexture)
			{
				ImGui.SetNextItemWidth(-1);
				EditorGui.SliderInt("##slice", ref previewSlice, 0, Math.Max(0, volumeTexture.depth - 1), "slice %d");
			}

			int handle;
			NVector2 uv0;
			NVector2 uv1;
			if (texture is Texture3D slices)
			{
				Texture2D slice = slices.GetSlice(previewSlice);
				handle = slice.Handle;
				uv0 = new NVector2(0f, 1f);
				uv1 = new NVector2(1f, 0f);
				DrawImage(handle, slice.width, slice.height, uv0, uv1, null);
				slice.Release();
				return;
			}

			if (!AssetThumbnails.TryGet(texture, out handle, out uv0, out uv1))
			{
				return;
			}

			int width = texture is Cubemap c ? c.size * 4 : texture.width;
			int height = texture is Cubemap c2 ? c2.size * 3 : texture.height;
			List<SpriteMetaData> overlay = working.textureType == TextureImporterType.Sprite && working.spriteMode == SpriteImportMode.Multiple ? working.sprites : null;
			DrawImage(handle, width, height, uv0, uv1, overlay);
		}

		private static void DrawImage(int handle, int width, int height, NVector2 uv0, NVector2 uv1, List<SpriteMetaData> sprites)
		{
			float available = ImGui.GetContentRegionAvail().X;
			float scale = Math.Min(available / Math.Max(1, width), 320f / Math.Max(1, height));
			NVector2 size = new NVector2(Math.Max(1f, width * scale), Math.Max(1f, height * scale));
			NVector2 origin = ImGui.GetCursorScreenPos();
			ImGui.Image(EditorGui.Texture(handle), size, uv0, uv1);

			if (sprites == null || sprites.Count == 0)
			{
				return;
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			uint color = ImGui.GetColorU32(EditorStyle.Selection);
			foreach (SpriteMetaData sprite in sprites)
			{
				// Sprite rects are bottom-left based; the image is drawn top-down.
				float x0 = origin.X + sprite.rect.xMin * scale;
				float x1 = origin.X + sprite.rect.xMax * scale;
				float y0 = origin.Y + (height - sprite.rect.yMax) * scale;
				float y1 = origin.Y + (height - sprite.rect.yMin) * scale;
				draw.AddRect(new NVector2(x0, y0), new NVector2(x1, y1), color);
			}
		}

		// The settings' widgets; field is the importer's field (several images: "—" where theirs differ).
		private static bool Combo(string label, string field, ref int value, string[] names, InspectorContext context)
		{
			context.Path = field;
			FieldDrawer.Label(label, null, context);
			bool isMixed = context.IsMixed();
			int before = value;
			if (EditorGui.BeginCombo("##" + label, isMixed ? EditorGui.MixedValue : value >= 0 && value < names.Length ? names[value] : ""))
			{
				for (int i = 0; i < names.Length; i++)
				{
					if (EditorGui.Selectable(names[i], !isMixed && i == value))
					{
						value = i;
						if (isMixed && i == before)
						{
							// The shown image's own value picked: the others take it.
							before = -1;
						}
					}
				}

				EditorGui.EndCombo();
			}

			if (value != before)
			{
				context.Touch();
			}

			return value != before;
		}

		private static bool Checkbox(string label, string field, ref bool value, InspectorContext context)
		{
			context.Path = field;
			FieldDrawer.Label(label, null, context);
			bool isMixed = context.IsMixed();
			bool shown = !isMixed && value;
			ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, isMixed);
			bool changed = EditorGui.Checkbox("##" + label, ref shown);
			ImGui.PopItemFlag();
			if (changed)
			{
				// A change from the shown value always marks the field changed, also when it equals the image's own.
				value = shown;
				context.Touch();
			}

			return changed;
		}

		private static bool Int(string label, string field, ref int value, InspectorContext context)
		{
			context.Path = field;
			FieldDrawer.Label(label, null, context);
			bool changed = EditorGui.DragInt("##" + label, ref value, 0.2f, 0, 1 << 14, context.IsMixed() ? EditorGui.MixedValue : "%d");
			if (changed)
			{
				context.Touch();
			}

			return changed;
		}

		#endregion
	}
}
