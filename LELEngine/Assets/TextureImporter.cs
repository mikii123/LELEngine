using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine
{
	public enum TextureImporterType
	{
		/// <summary>Color or data texture for materials.</summary>
		Default,

		/// <summary>Tangent-space normal map (stored linear).</summary>
		NormalMap,

		/// <summary>2D sprite(s) for sprite renderers and UI.</summary>
		Sprite
	}

	public enum TextureImporterShape
	{
		Texture2D,
		Cube,
		Texture3D
	}

	/// <summary>How the faces of a cubemap are arranged in the source image.</summary>
	public enum CubemapLayout
	{
		/// <summary>From the image proportions: 4:3 horizontal cross, 3:4 vertical cross, 6:1 / 1:6 strips, else a panorama.</summary>
		Auto,

		/// <summary>4 x 3 faces: +Y above the center +Z, -Y below it, -X +Z +X -Z in the middle row.</summary>
		HorizontalCross,

		/// <summary>3 x 4 faces: +Y, then -X +Z +X, then -Y, then -Z (rotated by 180 degrees) at the bottom.</summary>
		VerticalCross,

		/// <summary>6 x 1 faces in GL order +X -X +Y -Y +Z -Z.</summary>
		HorizontalStrip,

		/// <summary>1 x 6 faces in GL order.</summary>
		VerticalStrip,

		/// <summary>Equirectangular (latitude / longitude) panorama, usually 2:1.</summary>
		Equirectangular,

		/// <summary>
		///     Six image files next to this one, named base_px / _nx / _py / _ny / _pz / _nz (also _posx..., or
		///     _right / _left / _top / _bottom / _front / _back); this file is one of them.
		/// </summary>
		SixFiles
	}

	/// <summary>Where the slices of a 3D texture come from.</summary>
	public enum Texture3DSource
	{
		/// <summary>This image holds the slices as a grid of columns x rows tiles.</summary>
		Grid,

		/// <summary>Numbered image files next to this one (base_000, base_001, ...), one slice each; this file is one of them.</summary>
		NumberedFiles
	}

	public enum SpriteImportMode
	{
		/// <summary>One sprite covering the whole texture.</summary>
		Single,

		/// <summary>Several sprites (a sheet), listed in <see cref="TextureImporter.sprites" />.</summary>
		Multiple
	}

	/// <summary>One sprite of a sheet.</summary>
	[Serializable]
	public sealed class SpriteMetaData
	{
		public string name;

		/// <summary>Rectangle in texture pixels, origin bottom-left.</summary>
		public Rect rect;

		public Vector2 pivot = new Vector2(0.5f, 0.5f);

		/// <summary>9-slice borders: left, bottom, right, top (pixels).</summary>
		public Vector4 border;
	}

	/// <summary>
	///     Import settings of an image file (stored in its meta file): what the file becomes (a 2D texture, a cubemap,
	///     a volume, sprites), its color space and sampler state. Changing them in the editor and applying reimports
	///     the loaded asset in place.
	/// </summary>
	[Serializable]
	public sealed class TextureImporter : AssetImporter
	{
		#region PublicFields

		public TextureImporterType textureType = TextureImporterType.Default;
		public TextureImporterShape shape = TextureImporterShape.Texture2D;

		/// <summary>Color data authored in sRGB (off for data maps: roughness, masks, heights). Normal maps are always linear.</summary>
		public bool sRGB = true;

		public bool generateMipmaps = true;
		public FilterMode filterMode = FilterMode.Trilinear;
		public TextureWrap wrapMode = TextureWrap.Repeat;

		[Range(1, 16)] public int anisoLevel = 8;

		/// <summary>Largest side after import in texels (0 = the file's size); bigger images are box-filtered down.</summary>
		public int maxSize;

		// Cube
		public CubemapLayout cubemapLayout = CubemapLayout.Auto;

		/// <summary>Face size of a cubemap built from a panorama (0: from the image, height / 2).</summary>
		public int cubemapFaceSize;

		// 3D: the image holds columns x rows slices, or numbered files next to it hold one slice each.
		public Texture3DSource volumeSource = Texture3DSource.Grid;
		public int columns = 1;
		public int rows = 1;

		// Sprite
		public SpriteImportMode spriteMode = SpriteImportMode.Single;
		public float pixelsPerUnit = 100f;
		public Vector2 spritePivot = new Vector2(0.5f, 0.5f);
		public Vector4 spriteBorder;
		public List<SpriteMetaData> sprites = new List<SpriteMetaData>();

		#endregion

		#region PublicMethods

		/// <summary>Color textures are stored as sRGB; normal maps and data maps stay linear.</summary>
		public bool StoresSrgb => sRGB && textureType != TextureImporterType.NormalMap;

		/// <summary>Default settings guessed from the file name (normal / specular / roughness maps are linear data).</summary>
		public static TextureImporter CreateDefault(string assetPath)
		{
			var importer = new TextureImporter();
			string n = System.IO.Path.GetFileNameWithoutExtension(assetPath ?? "").ToLowerInvariant();
			if (n.Contains("normal") || n.Contains("norm") || n.Contains("bump") || n.Contains("nrm") || n.EndsWith("_n"))
			{
				importer.textureType = TextureImporterType.NormalMap;
				importer.sRGB = false;
			}
			else if (n.Contains("spec") || n.Contains("rough") || n.Contains("metal") || n.Contains("mask") || n.Contains("height") || n.Contains("_ao"))
			{
				importer.sRGB = false;
			}

			string extension = System.IO.Path.GetExtension(assetPath ?? "").ToLowerInvariant();
			if (extension == ".hdr")
			{
				// Panoramas are the common HDR source: a skybox cubemap.
				importer.shape = TextureImporterShape.Cube;
			}

			return importer;
		}

		/// <summary>
		///     Fills <see cref="sprites" /> with a grid of cells (editor "Slice"): cells of <paramref name="cellSize" />
		///     starting at <paramref name="offset" /> (from the top-left) with <paramref name="padding" /> between them,
		///     named "{baseName}_{index}" row by row from the top.
		/// </summary>
		public void SliceGrid(int textureWidth, int textureHeight, Vector2i cellSize, Vector2i offset, Vector2i padding, string baseName)
		{
			sprites.Clear();
			if (cellSize.X <= 0 || cellSize.Y <= 0)
			{
				return;
			}

			int index = 0;
			for (int y = offset.Y; y + cellSize.Y <= textureHeight; y += cellSize.Y + padding.Y)
			{
				for (int x = offset.X; x + cellSize.X <= textureWidth; x += cellSize.X + padding.X)
				{
					sprites.Add(new SpriteMetaData
					{
						name = baseName + "_" + index++,
						// Rects are bottom-left based; the grid is laid out from the top.
						rect = new Rect(x, textureHeight - y - cellSize.Y, cellSize.X, cellSize.Y),
						pivot = spritePivot,
						border = spriteBorder
					});
				}
			}
		}

		#endregion
	}
}
