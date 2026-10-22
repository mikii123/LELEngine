using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;
using StbImageSharp;

namespace LELEngine
{
	/// <summary>Decoded image: RGBA8 or RGBA float pixels, row-major; row 0 is the top row of the file unless flipped.</summary>
	internal sealed class ImageData
	{
		#region PublicFields

		public readonly int Width;
		public readonly int Height;
		public readonly byte[] Pixels;
		public readonly float[] FloatPixels;
		public bool IsHdr => FloatPixels != null;

		#endregion

		#region Constructors

		public ImageData(int width, int height, byte[] pixels)
		{
			Width = width;
			Height = height;
			Pixels = pixels;
		}

		public ImageData(int width, int height, float[] pixels)
		{
			Width = width;
			Height = height;
			FloatPixels = pixels;
		}

		#endregion

		#region PublicMethods

		/// <summary>The rectangle (x, y from the top-left) as a new image.</summary>
		public ImageData Crop(int x, int y, int width, int height)
		{
			if (IsHdr)
			{
				var result = new float[width * height * 4];
				for (int row = 0; row < height; row++)
				{
					Array.Copy(FloatPixels, ((y + row) * Width + x) * 4, result, row * width * 4, width * 4);
				}

				return new ImageData(width, height, result);
			}

			var bytes = new byte[width * height * 4];
			for (int row = 0; row < height; row++)
			{
				Array.Copy(Pixels, ((y + row) * Width + x) * 4, bytes, row * width * 4, width * 4);
			}

			return new ImageData(width, height, bytes);
		}

		/// <summary>Copies <paramref name="source" /> into this image at (x, y) (same pixel kind; clipped).</summary>
		public void Blit(ImageData source, int x, int y)
		{
			int width = Math.Min(source.Width, Width - x);
			int height = Math.Min(source.Height, Height - y);
			for (int row = 0; row < height; row++)
			{
				if (IsHdr && source.IsHdr)
				{
					Array.Copy(source.FloatPixels, row * source.Width * 4, FloatPixels, ((y + row) * Width + x) * 4, width * 4);
				}
				else if (!IsHdr && !source.IsHdr)
				{
					Array.Copy(source.Pixels, row * source.Width * 4, Pixels, ((y + row) * Width + x) * 4, width * 4);
				}
			}
		}

		public ImageData FlipVertical()
		{
			int stride = Width * 4;
			if (IsHdr)
			{
				var result = new float[FloatPixels.Length];
				for (int row = 0; row < Height; row++)
				{
					Array.Copy(FloatPixels, row * stride, result, (Height - 1 - row) * stride, stride);
				}

				return new ImageData(Width, Height, result);
			}

			var bytes = new byte[Pixels.Length];
			for (int row = 0; row < Height; row++)
			{
				Array.Copy(Pixels, row * stride, bytes, (Height - 1 - row) * stride, stride);
			}

			return new ImageData(Width, Height, bytes);
		}

		/// <summary>Swaps the rows top to bottom in this image's own pixels.</summary>
		public void FlipVerticalInPlace()
		{
			int stride = Width * 4;
			if (IsHdr)
			{
				var row = new float[stride];
				for (int top = 0, bottom = Height - 1; top < bottom; top++, bottom--)
				{
					Array.Copy(FloatPixels, top * stride, row, 0, stride);
					Array.Copy(FloatPixels, bottom * stride, FloatPixels, top * stride, stride);
					Array.Copy(row, 0, FloatPixels, bottom * stride, stride);
				}

				return;
			}

			var bytes = new byte[stride];
			for (int top = 0, bottom = Height - 1; top < bottom; top++, bottom--)
			{
				Array.Copy(Pixels, top * stride, bytes, 0, stride);
				Array.Copy(Pixels, bottom * stride, Pixels, top * stride, stride);
				Array.Copy(bytes, 0, Pixels, bottom * stride, stride);
			}
		}

		public ImageData Rotate180()
		{
			int count = Width * Height;
			if (IsHdr)
			{
				var result = new float[FloatPixels.Length];
				for (int i = 0; i < count; i++)
				{
					Array.Copy(FloatPixels, i * 4, result, (count - 1 - i) * 4, 4);
				}

				return new ImageData(Width, Height, result);
			}

			var bytes = new byte[Pixels.Length];
			for (int i = 0; i < count; i++)
			{
				Array.Copy(Pixels, i * 4, bytes, (count - 1 - i) * 4, 4);
			}

			return new ImageData(Width, Height, bytes);
		}

		/// <summary>HDR pixels tone-mapped (Reinhard) and gamma encoded to RGBA8 (previews).</summary>
		public ImageData ToLdr()
		{
			if (!IsHdr)
			{
				return this;
			}

			var bytes = new byte[Width * Height * 4];
			for (int i = 0; i < Width * Height; i++)
			{
				for (int c = 0; c < 3; c++)
				{
					float v = Math.Max(0f, FloatPixels[i * 4 + c]);
					v = v / (1f + v);
					bytes[i * 4 + c] = (byte)Math.Clamp(MathF.Pow(v, 1f / 2.2f) * 255f + 0.5f, 0f, 255f);
				}

				bytes[i * 4 + 3] = 255;
			}

			return new ImageData(Width, Height, bytes);
		}

		/// <summary>The image reduced by an integer factor with a box filter (import size limits).</summary>
		public ImageData Downscale(int factor)
		{
			if (factor <= 1)
			{
				return this;
			}

			int width = Math.Max(1, Width / factor);
			int height = Math.Max(1, Height / factor);
			float[] accum = new float[4];
			if (IsHdr)
			{
				var result = new float[width * height * 4];
				for (int y = 0; y < height; y++)
				{
					for (int x = 0; x < width; x++)
					{
						Box(x, y, factor, accum);
						int index = (y * width + x) * 4;
						result[index] = accum[0];
						result[index + 1] = accum[1];
						result[index + 2] = accum[2];
						result[index + 3] = accum[3];
					}
				}

				return new ImageData(width, height, result);
			}

			var bytes = new byte[width * height * 4];
			for (int y = 0; y < height; y++)
			{
				for (int x = 0; x < width; x++)
				{
					Box(x, y, factor, accum);
					int index = (y * width + x) * 4;
					for (int c = 0; c < 4; c++)
					{
						bytes[index + c] = (byte)Math.Clamp(accum[c] + 0.5f, 0f, 255f);
					}
				}
			}

			return new ImageData(width, height, bytes);
		}

		/// <summary>The largest reduction factor (a power of two) that keeps both sides within <paramref name="maxSize" />.</summary>
		public int FactorFor(int maxSize)
		{
			if (maxSize <= 0)
			{
				return 1;
			}

			int factor = 1;
			while ((Width / factor > maxSize || Height / factor > maxSize) && factor < 1 << 14)
			{
				factor *= 2;
			}

			return factor;
		}

		private void Box(int x, int y, int factor, float[] accum)
		{
			accum[0] = accum[1] = accum[2] = accum[3] = 0f;
			int count = 0;
			for (int sy = 0; sy < factor; sy++)
			{
				int row = Math.Min(Height - 1, y * factor + sy);
				for (int sx = 0; sx < factor; sx++)
				{
					int column = Math.Min(Width - 1, x * factor + sx);
					int index = (row * Width + column) * 4;
					if (IsHdr)
					{
						accum[0] += FloatPixels[index];
						accum[1] += FloatPixels[index + 1];
						accum[2] += FloatPixels[index + 2];
						accum[3] += FloatPixels[index + 3];
					}
					else
					{
						accum[0] += Pixels[index];
						accum[1] += Pixels[index + 1];
						accum[2] += Pixels[index + 2];
						accum[3] += Pixels[index + 3];
					}

					count++;
				}
			}

			for (int c = 0; c < 4; c++)
			{
				accum[c] /= Math.Max(1, count);
			}
		}

		/// <summary>Bilinear sample; u wraps (panoramas), v clamps. Returns RGBA in the image's own range.</summary>
		public Vector4 Sample(float u, float v)
		{
			float x = (u - MathF.Floor(u)) * Width - 0.5f;
			float y = Math.Clamp(v, 0f, 1f) * Height - 0.5f;
			int x0 = (int)MathF.Floor(x);
			int y0 = (int)MathF.Floor(y);
			float fx = x - x0;
			float fy = y - y0;
			int x1 = x0 + 1;
			int y1 = Math.Clamp(y0 + 1, 0, Height - 1);
			y0 = Math.Clamp(y0, 0, Height - 1);
			x0 = ((x0 % Width) + Width) % Width;
			x1 = ((x1 % Width) + Width) % Width;

			Vector4 a = Pixel(x0, y0);
			Vector4 b = Pixel(x1, y0);
			Vector4 c = Pixel(x0, y1);
			Vector4 d = Pixel(x1, y1);
			return Vector4.Lerp(Vector4.Lerp(a, b, fx), Vector4.Lerp(c, d, fx), fy);
		}

		public Vector4 Pixel(int x, int y)
		{
			int index = (y * Width + x) * 4;
			if (IsHdr)
			{
				return new Vector4(FloatPixels[index], FloatPixels[index + 1], FloatPixels[index + 2], FloatPixels[index + 3]);
			}

			return new Vector4(Pixels[index], Pixels[index + 1], Pixels[index + 2], Pixels[index + 3]);
		}

		#endregion
	}

	/// <summary>An image file imported by its settings, CPU only (<see cref="TextureAssets.Import" />).</summary>
	internal sealed class ImportedTexture
	{
		#region PublicFields

		public TextureImporterShape Shape;

		/// <summary>2D: the image, bottom row first.</summary>
		public ImageData Image;

		/// <summary>Cube: the faces in GL order, top row first.</summary>
		public ImageData[] Faces;

		/// <summary>3D: RGBA8 slices, each bottom row first.</summary>
		public byte[] Voxels;

		public int Width;
		public int Height;
		public int Depth;
		public bool IsHdr;
		public bool Srgb;
		public bool Mipmaps;

		#endregion
	}

	/// <summary>
	///     Loads image files into texture assets by their import settings (<see cref="TextureImporter" />): 2D textures,
	///     cubemaps built from crosses, strips or panoramas, volumes from slice grids, and sprites. Loaded assets are
	///     cached per file; <see cref="Reimport" /> rebuilds them in place when their settings change.
	/// </summary>
	public static class TextureAssets
	{
		#region PublicFields

		public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".gif", ".psd", ".hdr" };

		/// <summary>Raised when a reimport had to replace the texture object (its shape changed): holders swap their references.</summary>
		public static event Action<string, Texture, Texture> TextureReplaced;

		#endregion

		#region PrivateFields

		private sealed class Entry
		{
			public Texture Texture;
			public Sprite Single;
			public List<Sprite> Sprites;
			public TextureImporterShape Shape;
		}

		private static readonly object sync = new object();
		private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, Texture2D> uiTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static bool IsImageFile(string path)
		{
			foreach (string extension in ImageExtensions)
			{
				if (path != null && path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Asset loader: the texture of the file as its imported shape, or one of its sprites.</summary>
		public static object Load(Type type, string assetPath, string subAsset)
		{
			Entry entry = GetOrLoad(assetPath);
			if (entry == null)
			{
				return null;
			}

			if (type == typeof(Sprite))
			{
				if (subAsset != null)
				{
					if (entry.Sprites != null)
					{
						foreach (Sprite sprite in entry.Sprites)
						{
							if (string.Equals(sprite.name, subAsset, StringComparison.Ordinal))
							{
								return sprite;
							}
						}
					}

					Debug.LogWarning($"[Assets] {assetPath} has no sprite named '{subAsset}'");
					return null;
				}

				return entry.Single ?? (entry.Sprites != null && entry.Sprites.Count > 0 ? entry.Sprites[0] : null);
			}

			if (entry.Texture == null)
			{
				return null;
			}

			if (!type.IsInstanceOfType(entry.Texture))
			{
				Debug.LogWarning($"[Assets] {assetPath} is imported as {entry.Texture.GetType().Name}; a {type.Name} was requested (change its texture shape in the import settings)");
				return null;
			}

			return entry.Texture;
		}

		/// <summary>The texture of an image file as imported (any shape), or null.</summary>
		public static Texture LoadTexture(string assetPath)
		{
			return GetOrLoad(assetPath)?.Texture;
		}

		/// <summary>The sprites of an image file (Multiple mode), or the single sprite.</summary>
		public static IReadOnlyList<Sprite> LoadSprites(string assetPath)
		{
			Entry entry = GetOrLoad(assetPath);
			if (entry == null)
			{
				return Array.Empty<Sprite>();
			}

			return entry.Sprites ?? (entry.Single != null ? new List<Sprite> { entry.Single } : (IReadOnlyList<Sprite>)Array.Empty<Sprite>());
		}

		/// <summary>
		///     Loads an image for the UI (top row first, gamma storage, trilinear, clamped): the UI composes in display
		///     space and draws images with top-down texture coordinates. Separate from the material cache.
		/// </summary>
		public static Texture2D LoadForUI(string path, string assetPath = null)
		{
			string key = assetPath ?? path;
			lock (sync)
			{
				Texture2D cached;
				if (uiTextures.TryGetValue(key, out cached))
				{
					return cached;
				}
			}

			try
			{
				string absolute = File.Exists(path) ? path : AssetDatabase.ToAbsolute(path);
				byte[] file = File.ReadAllBytes(absolute);
				if (KtxFile.IsKtx(file))
				{
					// Builds keep the images of sprites and UI documents as they are; any other was baked for materials
					// (rows bottom up, sRGB storage): drawn by the UI it may show flipped and darker.
					Debug.LogWarning("[UI] " + (assetPath ?? path) + " is a texture baked for materials in this build; set its Texture Type to Sprite to use it in the UI");
					return LoadTexture(assetPath ?? AssetDatabase.ToAssetPath(absolute)) as Texture2D;
				}

				ImageData image = Decode(absolute, false, file);
				var texture = new Texture2D { name = Path.GetFileName(path), AssetPath = assetPath };
				texture.SetImage(image.IsHdr ? image.ToLdr() : image, false, false, true);
				texture.SetSamplerState(FilterMode.Trilinear, TextureWrap.Clamp, 1);
				lock (sync)
				{
					// The loading thread and the main thread may both have read the file: the first one kept wins.
					Texture2D raced;
					if (uiTextures.TryGetValue(key, out raced))
					{
						return raced;
					}

					uiTextures[key] = texture;
				}

				return texture;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot load texture " + path + ": " + e.Message);
				return null;
			}
		}

		/// <summary>
		///     Forgets a loaded texture and frees its GPU memory (its asset was unloaded with the last scene using it).
		///     The object stays usable: used again, it uploads again; a new load of the file reads it again.
		/// </summary>
		public static void Release(Texture texture)
		{
			if (texture == null)
			{
				return;
			}

			lock (sync)
			{
				string key = null;
				foreach (KeyValuePair<string, Entry> entry in entries)
				{
					if (ReferenceEquals(entry.Value.Texture, texture))
					{
						key = entry.Key;
						break;
					}
				}

				if (key != null)
				{
					entries.Remove(key);
				}
			}

			texture.Release();
		}

		/// <summary>Rebuilds a loaded image asset from its current import settings (editor Apply); textures keep their identity when the shape is unchanged.</summary>
		public static void Reimport(string assetPath)
		{
			string key = Normalize(assetPath);
			Entry entry;
			lock (sync)
			{
				if (!entries.TryGetValue(key, out entry))
				{
					return;
				}
			}

			Texture old = entry.Texture;
			Entry rebuilt = Build(key, entry);
			if (rebuilt == null)
			{
				return;
			}

			lock (sync)
			{
				entries[key] = rebuilt;
			}

			if (!ReferenceEquals(old, rebuilt.Texture))
			{
				old?.Release();
				TextureReplaced?.Invoke(key, old, rebuilt.Texture);
			}
		}

		/// <summary>A file or folder moved: the loaded textures and sprites keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (sync)
			{
				AssetDatabase.RekeyMoved(entries, fromPath, toPath, (entry, path) =>
				{
					if (entry.Texture != null)
					{
						entry.Texture.AssetPath = path;
					}
					if (entry.Single != null)
					{
						entry.Single.AssetPath = path;
					}
					if (entry.Sprites != null)
					{
						foreach (Sprite sprite in entry.Sprites)
						{
							sprite.AssetPath = path;
						}
					}
				});
				AssetDatabase.RekeyMoved(uiTextures, fromPath, toPath, (texture, path) => texture.AssetPath = path);
			}
		}

		/// <summary>Drops every loaded texture (project closed) and releases their GL textures.</summary>
		public static void Clear()
		{
			lock (sync)
			{
				foreach (Entry entry in entries.Values)
				{
					entry.Texture?.Release();
				}

				entries.Clear();
				foreach (Texture2D texture in uiTextures.Values)
				{
					texture.Release();
				}

				uiTextures.Clear();
			}
		}

		/// <summary>
		///     Decodes an image file (png / jpg / tga / bmp / gif / psd as RGBA8, hdr as float) with row 0 at the top or,
		///     flipped, at the bottom; <paramref name="file" /> holds its bytes when they were read already.
		/// </summary>
		internal static ImageData Decode(string absolutePath, bool flipVertical, byte[] file = null)
		{
			// stb's flip switch is one global for every thread (scenes and resources decode on the loading thread while
			// the main thread decodes too): it stays off and the rows are flipped here.
			ImageData decoded;
			using (Stream stream = file != null ? new MemoryStream(file, false) : File.OpenRead(absolutePath))
			{
				if (absolutePath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase))
				{
					ImageResultFloat hdr = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
					decoded = new ImageData(hdr.Width, hdr.Height, hdr.Data);
				}
				else
				{
					ImageResult image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
					decoded = new ImageData(image.Width, image.Height, image.Data);
				}
			}

			if (flipVertical)
			{
				decoded.FlipVerticalInPlace();
			}

			return decoded;
		}

		/// <summary>The faces of a cubemap image in GL order, each with its top row first; null when the layout does not fit the image.</summary>
		internal static ImageData[] ExtractFaces(ImageData image, CubemapLayout layout, int faceSize)
		{
			if (layout == CubemapLayout.Auto)
			{
				layout = GuessLayout(image.Width, image.Height);
			}

			switch (layout)
			{
				case CubemapLayout.HorizontalCross:
				{
					int s = image.Width / 4;
					if (s <= 0 || image.Height < s * 3)
					{
						return null;
					}

					(int x, int y)[] cells = { (2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (3, 1) };
					return CropCells(image, cells, s);
				}
				case CubemapLayout.VerticalCross:
				{
					int s = image.Width / 3;
					if (s <= 0 || image.Height < s * 4)
					{
						return null;
					}

					(int x, int y)[] cells = { (2, 1), (0, 1), (1, 0), (1, 2), (1, 1), (1, 3) };
					ImageData[] faces = CropCells(image, cells, s);
					faces[5] = faces[5].Rotate180();
					return faces;
				}
				case CubemapLayout.HorizontalStrip:
				{
					int s = image.Height;
					if (image.Width < s * 6)
					{
						return null;
					}

					(int x, int y)[] cells = { (0, 0), (1, 0), (2, 0), (3, 0), (4, 0), (5, 0) };
					return CropCells(image, cells, s);
				}
				case CubemapLayout.VerticalStrip:
				{
					int s = image.Width;
					if (image.Height < s * 6)
					{
						return null;
					}

					(int x, int y)[] cells = { (0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5) };
					return CropCells(image, cells, s);
				}
				default:
					return FromEquirectangular(image, faceSize > 0 ? faceSize : Math.Clamp(image.Height / 2, 16, 2048));
			}
		}

		#endregion

		#region PrivateMethods

		private static Entry GetOrLoad(string assetPath)
		{
			string key = Normalize(assetPath);
			lock (sync)
			{
				Entry cached;
				if (entries.TryGetValue(key, out cached))
				{
					return cached;
				}
			}

			Entry entry = Build(key, null);
			if (entry == null)
			{
				return null;
			}

			lock (sync)
			{
				Entry raced;
				if (entries.TryGetValue(key, out raced))
				{
					return raced;
				}

				entries[key] = entry;
			}

			return entry;
		}

		/// <summary>
		///     Imports the file by its settings (a build's baked KTX file: its levels as they are), reusing
		///     <paramref name="previous" />'s texture when the shape is unchanged.
		/// </summary>
		private static Entry Build(string assetPath, Entry previous)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			if (!File.Exists(absolute))
			{
				Debug.LogWarning("[Assets] Image not found: " + assetPath);
				return null;
			}

			TextureImporter importer = GetImporter(assetPath);
			try
			{
				var entry = new Entry { Shape = importer.shape };
				string name = Path.GetFileNameWithoutExtension(assetPath);
				byte[] file = File.ReadAllBytes(absolute);
				if (KtxFile.IsKtx(file))
				{
					KtxFile ktx = KtxFile.Read(file);
					Texture baked = CreateBaked(ktx, previous?.Texture, importer);
					baked.name = name;
					baked.AssetPath = assetPath;
					entry.Shape = ktx.Faces == 6 ? TextureImporterShape.Cube : ktx.Depth > 0 ? TextureImporterShape.Texture3D : TextureImporterShape.Texture2D;
					entry.Texture = baked;
					if (baked is Texture2D baked2D)
					{
						BuildSprites(entry, baked2D, importer, assetPath, name);
					}

					return entry;
				}

				ImportedTexture imported = Import(assetPath, importer, file);
				if (imported == null)
				{
					return null;
				}

				switch (imported.Shape)
				{
					case TextureImporterShape.Cube:
					{
						Cubemap cubemap = previous?.Texture as Cubemap ?? new Cubemap();
						byte[][] rgba = imported.IsHdr ? null : new byte[6][];
						float[][] hdr = imported.IsHdr ? new float[6][] : null;
						for (int i = 0; i < 6; i++)
						{
							if (hdr != null) hdr[i] = imported.Faces[i].FloatPixels;
							else rgba[i] = imported.Faces[i].Pixels;
						}

						cubemap.SetFaces(imported.Faces[0].Width, rgba, hdr, imported.Srgb, imported.Mipmaps);
						cubemap.SetSamplerState(importer.filterMode, TextureWrap.Clamp, importer.anisoLevel);
						cubemap.name = name;
						cubemap.AssetPath = assetPath;
						entry.Texture = cubemap;
						break;
					}
					case TextureImporterShape.Texture3D:
					{
						Texture3D volume = previous?.Texture as Texture3D ?? new Texture3D();
						volume.SetVolume(imported.Width, imported.Height, imported.Depth, imported.Voxels, imported.Srgb, imported.Mipmaps);
						volume.SetSamplerState(importer.filterMode, importer.wrapMode, 1);
						volume.name = name;
						volume.AssetPath = assetPath;
						entry.Texture = volume;
						break;
					}
					default:
					{
						Texture2D texture = previous?.Texture as Texture2D ?? new Texture2D();
						texture.SetImage(imported.Image, imported.Srgb, true, imported.Mipmaps);
						texture.SetSamplerState(importer.filterMode, importer.wrapMode, importer.anisoLevel);
						texture.name = name;
						texture.AssetPath = assetPath;
						entry.Texture = texture;
						BuildSprites(entry, texture, importer, assetPath, name);
						break;
					}
				}

				return entry;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Assets] Cannot import " + assetPath + ": " + e.Message);
				return null;
			}
		}

		/// <summary>
		///     An image file as its import settings make it, on the CPU only (any thread): the source of a texture and of a
		///     build's baked KTX file. Null (with a warning) when the image does not fit the settings.
		/// </summary>
		internal static ImportedTexture Import(string assetPath, TextureImporter importer, byte[] file = null)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			var imported = new ImportedTexture { Shape = importer.shape, Mipmaps = importer.generateMipmaps };
			switch (importer.shape)
			{
				case TextureImporterShape.Cube:
				{
					ImageData image = Decode(absolute, false, file);
					ImageData[] faces = importer.cubemapLayout == CubemapLayout.SixFiles
						? LoadSixFaces(absolute, image)
						: ExtractFaces(image, importer.cubemapLayout, importer.cubemapFaceSize);
					if (faces == null)
					{
						Debug.LogWarning($"[Assets] {assetPath}: the image ({image.Width}x{image.Height}) does not fit the {importer.cubemapLayout} cubemap layout");
						return null;
					}

					int faceFactor = faces[0].FactorFor(importer.maxSize);
					if (faceFactor > 1)
					{
						for (int i = 0; i < 6; i++)
						{
							faces[i] = faces[i].Downscale(faceFactor);
						}
					}

					imported.Faces = faces;
					imported.IsHdr = faces[0].IsHdr;
					imported.Srgb = importer.StoresSrgb && !imported.IsHdr;
					imported.Width = faces[0].Width;
					imported.Height = faces[0].Height;
					return imported;
				}
				case TextureImporterShape.Texture3D:
				{
					int w;
					int h;
					int depth;
					byte[] voxels;
					if (importer.volumeSource == Texture3DSource.NumberedFiles)
					{
						List<ImageData> slices = LoadNumberedSlices(absolute, importer.maxSize);
						if (slices == null)
						{
							return null;
						}

						w = slices[0].Width;
						h = slices[0].Height;
						depth = slices.Count;
						voxels = new byte[w * h * depth * 4];
						for (int z = 0; z < depth; z++)
						{
							Array.Copy(slices[z].FlipVertical().Pixels, 0, voxels, z * w * h * 4, w * h * 4);
						}
					}
					else
					{
						ImageData image = Decode(absolute, false, file);
						if (image.IsHdr)
						{
							image = image.ToLdr();
						}

						int columns = Math.Max(1, importer.columns);
						int rows = Math.Max(1, importer.rows);
						w = image.Width / columns;
						h = image.Height / rows;
						depth = columns * rows;
						int sliceFactor = new ImageData(w, h, Array.Empty<byte>()).FactorFor(importer.maxSize);
						int sw = Math.Max(1, w / sliceFactor);
						int sh = Math.Max(1, h / sliceFactor);
						voxels = new byte[sw * sh * depth * 4];
						for (int z = 0; z < depth; z++)
						{
							ImageData slice = image.Crop(z % columns * w, z / columns * h, w, h).Downscale(sliceFactor).FlipVertical();
							Array.Copy(slice.Pixels, 0, voxels, z * sw * sh * 4, sw * sh * 4);
						}

						w = sw;
						h = sh;
					}

					imported.Width = w;
					imported.Height = h;
					imported.Depth = depth;
					imported.Voxels = voxels;
					imported.Srgb = importer.StoresSrgb;
					return imported;
				}
				default:
				{
					ImageData image = Decode(absolute, true, file);
					image = image.Downscale(image.FactorFor(importer.maxSize));
					imported.Image = image;
					imported.IsHdr = image.IsHdr;
					imported.Srgb = importer.StoresSrgb && !image.IsHdr;
					imported.Width = image.Width;
					imported.Height = image.Height;
					return imported;
				}
			}
		}

		/// <summary>A texture of the baked file's shape filled from its levels (no decoding, no GL work until it is used).</summary>
		private static Texture CreateBaked(KtxFile ktx, Texture previous, TextureImporter importer)
		{
			if (ktx.Faces == 6)
			{
				Cubemap cubemap = previous as Cubemap ?? new Cubemap();
				cubemap.SetBaked(ktx, importer.generateMipmaps);
				cubemap.SetSamplerState(importer.filterMode, TextureWrap.Clamp, importer.anisoLevel);
				return cubemap;
			}

			if (ktx.Depth > 0)
			{
				Texture3D volume = previous as Texture3D ?? new Texture3D();
				volume.SetBaked(ktx, importer.generateMipmaps);
				volume.SetSamplerState(importer.filterMode, importer.wrapMode, 1);
				return volume;
			}

			Texture2D texture = previous as Texture2D ?? new Texture2D();
			texture.SetBaked(ktx, importer.generateMipmaps);
			texture.SetSamplerState(importer.filterMode, importer.wrapMode, importer.anisoLevel);
			return texture;
		}

		private static void BuildSprites(Entry entry, Texture2D texture, TextureImporter importer, string assetPath, string name)
		{
			if (importer.textureType == TextureImporterType.Sprite && importer.spriteMode == SpriteImportMode.Multiple && importer.sprites.Count > 0)
			{
				entry.Sprites = new List<Sprite>(importer.sprites.Count);
				foreach (SpriteMetaData data in importer.sprites)
				{
					entry.Sprites.Add(new Sprite(texture, data.rect, data.pivot, importer.pixelsPerUnit, data.border, data.name, assetPath, true));
				}

				return;
			}

			entry.Single = new Sprite(texture, new Rect(0f, 0f, texture.width, texture.height), importer.spritePivot, importer.pixelsPerUnit, importer.spriteBorder, name, assetPath, false);
		}

		/// <summary>The import settings of an image (its meta file's, or defaults guessed from its name).</summary>
		internal static TextureImporter GetImporter(string assetPath)
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

		private static readonly string[][] FaceSuffixes =
		{
			new[] { "_px", "_posx", "_right" },
			new[] { "_nx", "_negx", "_left" },
			new[] { "_py", "_posy", "_top", "_up" },
			new[] { "_ny", "_negy", "_bottom", "_down" },
			new[] { "_pz", "_posz", "_front" },
			new[] { "_nz", "_negz", "_back" }
		};

		/// <summary>
		///     The image files an image asset is built from (root-relative): the file itself, plus the other faces of a
		///     six-file cubemap or the other slices of a numbered-files volume.
		/// </summary>
		public static List<string> GetSourceFiles(string assetPath)
		{
			var files = new List<string> { assetPath };
			TextureImporter importer = GetImporter(assetPath);
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			string folder = Path.GetDirectoryName(absolute) ?? "";
			string extension = Path.GetExtension(absolute);
			string stem = Path.GetFileNameWithoutExtension(absolute);
			if (importer.shape == TextureImporterShape.Cube && importer.cubemapLayout == CubemapLayout.SixFiles)
			{
				string baseName = stem;
				foreach (string[] suffixes in FaceSuffixes)
				{
					foreach (string suffix in suffixes)
					{
						if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
						{
							baseName = stem.Substring(0, stem.Length - suffix.Length);
						}
					}
				}

				foreach (string[] suffixes in FaceSuffixes)
				{
					foreach (string suffix in suffixes)
					{
						string candidate = Path.Combine(folder, baseName + suffix + extension);
						if (File.Exists(candidate))
						{
							files.Add(AssetDatabase.ToAssetPath(candidate));
						}
					}
				}
			}
			else if (importer.shape == TextureImporterShape.Texture3D && importer.volumeSource == Texture3DSource.NumberedFiles)
			{
				int digits = stem.Length;
				while (digits > 0 && char.IsDigit(stem[digits - 1]))
				{
					digits--;
				}

				string baseName = stem.Substring(0, digits).TrimEnd('_', '-', '.');
				foreach (string file in Directory.GetFiles(folder, "*" + extension))
				{
					string otherStem = Path.GetFileNameWithoutExtension(file);
					if (otherStem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase) && int.TryParse(otherStem.Substring(baseName.Length).TrimStart('_', '-', '.'), out _))
					{
						files.Add(AssetDatabase.ToAssetPath(file));
					}
				}
			}

			var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			files.RemoveAll(path => path == null || !seen.Add(path));
			return files;
		}

		/// <summary>
		///     The six faces from files next to <paramref name="absolute" /> that share its base name plus a face suffix;
		///     faces without a file reuse the decoded image.
		/// </summary>
		private static ImageData[] LoadSixFaces(string absolute, ImageData decoded)
		{
			string folder = Path.GetDirectoryName(absolute) ?? "";
			string extension = Path.GetExtension(absolute);
			string stem = Path.GetFileNameWithoutExtension(absolute);
			string baseName = stem;
			foreach (string[] suffixes in FaceSuffixes)
			{
				foreach (string suffix in suffixes)
				{
					if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
					{
						baseName = stem.Substring(0, stem.Length - suffix.Length);
						break;
					}
				}
			}

			var faces = new ImageData[6];
			int found = 0;
			for (int face = 0; face < 6; face++)
			{
				foreach (string suffix in FaceSuffixes[face])
				{
					string candidate = Path.Combine(folder, baseName + suffix + extension);
					if (!File.Exists(candidate))
					{
						continue;
					}

					faces[face] = string.Equals(candidate, absolute, StringComparison.OrdinalIgnoreCase) ? decoded : Decode(candidate, false);
					found++;
					break;
				}

				faces[face] = faces[face] ?? decoded;
			}

			if (found == 0)
			{
				Debug.LogWarning($"[Assets] {Path.GetFileName(absolute)}: no face files named {baseName}_px/_nx/_py/_ny/_pz/_nz{extension} found; the image is used for every face");
			}

			int size = Math.Min(faces[0].Width, faces[0].Height);
			for (int face = 0; face < 6; face++)
			{
				if (faces[face].Width != size || faces[face].Height != size || faces[face].IsHdr != faces[0].IsHdr)
				{
					ImageData square = faces[face].IsHdr == faces[0].IsHdr ? faces[face] : faces[0].IsHdr ? faces[face] : faces[face].ToLdr();
					int side = Math.Min(square.Width, square.Height);
					faces[face] = square.Crop(0, 0, side, side);
					if (side != size)
					{
						Debug.LogWarning($"[Assets] {Path.GetFileName(absolute)}: cubemap face {face} is {side}x{side}, expected {size}x{size}; faces must match");
						return null;
					}
				}
			}

			return faces;
		}

		/// <summary>
		///     The slices of a volume from numbered files next to <paramref name="absolute" /> (base_000, base_001, ...),
		///     sorted by number; null when they differ in size.
		/// </summary>
		private static List<ImageData> LoadNumberedSlices(string absolute, int maxSize)
		{
			string folder = Path.GetDirectoryName(absolute) ?? "";
			string extension = Path.GetExtension(absolute);
			string stem = Path.GetFileNameWithoutExtension(absolute);
			int digits = stem.Length;
			while (digits > 0 && char.IsDigit(stem[digits - 1]))
			{
				digits--;
			}

			string baseName = stem.Substring(0, digits).TrimEnd('_', '-', '.');
			var files = new List<(int number, string path)>();
			foreach (string file in Directory.GetFiles(folder, "*" + extension))
			{
				string otherStem = Path.GetFileNameWithoutExtension(file);
				if (!otherStem.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				string rest = otherStem.Substring(baseName.Length).TrimStart('_', '-', '.');
				int number;
				if (rest.Length > 0 && int.TryParse(rest, out number))
				{
					files.Add((number, file));
				}
			}

			if (files.Count == 0)
			{
				files.Add((0, absolute));
			}

			files.Sort((a, b) => a.number.CompareTo(b.number));
			var slices = new List<ImageData>(files.Count);
			foreach ((int number, string path) in files)
			{
				ImageData slice = Decode(path, false);
				if (slice.IsHdr)
				{
					slice = slice.ToLdr();
				}

				slice = slice.Downscale(slice.FactorFor(maxSize));
				if (slices.Count > 0 && (slice.Width != slices[0].Width || slice.Height != slices[0].Height))
				{
					Debug.LogWarning($"[Assets] {Path.GetFileName(path)}: slice size {slice.Width}x{slice.Height} differs from {slices[0].Width}x{slices[0].Height}");
					return null;
				}

				slices.Add(slice);
			}

			return slices;
		}

		private static CubemapLayout GuessLayout(int width, int height)
		{
			if (width * 3 == height * 4) return CubemapLayout.HorizontalCross;
			if (width * 4 == height * 3) return CubemapLayout.VerticalCross;
			if (width == height * 6) return CubemapLayout.HorizontalStrip;
			if (height == width * 6) return CubemapLayout.VerticalStrip;
			return CubemapLayout.Equirectangular;
		}

		private static ImageData[] CropCells(ImageData image, (int x, int y)[] cells, int size)
		{
			var faces = new ImageData[6];
			for (int i = 0; i < 6; i++)
			{
				faces[i] = image.Crop(cells[i].x * size, cells[i].y * size, size, size);
			}

			return faces;
		}

		/// <summary>
		///     Projects a latitude / longitude panorama onto the six faces. The panorama's center faces +Z (the engine's
		///     forward), its top is +Y; u grows towards -X (screen right when looking down +Z).
		/// </summary>
		private static ImageData[] FromEquirectangular(ImageData image, int size)
		{
			var faces = new ImageData[6];
			for (int face = 0; face < 6; face++)
			{
				var rgba = image.IsHdr ? null : new byte[size * size * 4];
				var hdr = image.IsHdr ? new float[size * size * 4] : null;
				for (int y = 0; y < size; y++)
				{
					float b = (y + 0.5f) / size * 2f - 1f;
					for (int x = 0; x < size; x++)
					{
						float a = (x + 0.5f) / size * 2f - 1f;
						Vector3 dir = FaceDirection(face, a, b).Normalized();
						float u = 0.5f + MathF.Atan2(-dir.X, dir.Z) / (2f * MathF.PI);
						float v = MathF.Acos(Math.Clamp(dir.Y, -1f, 1f)) / MathF.PI;
						Vector4 color = image.Sample(u, v);
						int index = (y * size + x) * 4;
						if (hdr != null)
						{
							hdr[index] = color.X;
							hdr[index + 1] = color.Y;
							hdr[index + 2] = color.Z;
							hdr[index + 3] = color.W;
						}
						else
						{
							rgba[index] = (byte)Math.Clamp(color.X + 0.5f, 0f, 255f);
							rgba[index + 1] = (byte)Math.Clamp(color.Y + 0.5f, 0f, 255f);
							rgba[index + 2] = (byte)Math.Clamp(color.Z + 0.5f, 0f, 255f);
							rgba[index + 3] = (byte)Math.Clamp(color.W + 0.5f, 0f, 255f);
						}
					}
				}

				faces[face] = hdr != null ? new ImageData(size, size, hdr) : new ImageData(size, size, rgba);
			}

			return faces;
		}

		/// <summary>Direction through a face texel in the GL cube map convention (a right, b down across the face image).</summary>
		internal static Vector3 FaceDirection(int face, float a, float b)
		{
			switch (face)
			{
				case 0: return new Vector3(1f, -b, -a);
				case 1: return new Vector3(-1f, -b, a);
				case 2: return new Vector3(a, 1f, b);
				case 3: return new Vector3(a, -1f, -b);
				case 4: return new Vector3(a, -b, 1f);
				default: return new Vector3(-a, -b, -1f);
			}
		}

		private static string Normalize(string path)
		{
			return path.Replace('\\', '/').TrimStart('/');
		}

		#endregion
	}
}
