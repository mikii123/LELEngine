using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     A rectangle of a <see cref="Texture2D" /> with a pivot and a pixels-per-unit scale: the 2D drawing and
	///     physics unit of a texture imported in Sprite mode. A sheet imported in Multiple mode holds several, each
	///     addressed by name (the sub-asset of the image file).
	/// </summary>
	public sealed class Sprite : IAsset
	{
		#region PublicFields

		/// <summary>Path of the image file the sprite comes from.</summary>
		public string AssetPath { get; internal set; }

		/// <summary>The sprite's name inside its sheet (Multiple mode); null for the single sprite of a texture.</summary>
		public string SubAssetName => isSubAsset ? name : null;

		public string name;
		public Texture2D texture;

		/// <summary>Rectangle in texture pixels, origin at the bottom-left of the texture.</summary>
		public Rect rect;

		/// <summary>Pivot in normalized rect coordinates (0..1, (0.5, 0.5) = center).</summary>
		public Vector2 pivot = new Vector2(0.5f, 0.5f);

		public float pixelsPerUnit = 100f;

		/// <summary>9-slice borders in pixels: left, bottom, right, top.</summary>
		public Vector4 border;

		/// <summary>Texture coordinates of the rect: (uMin, vMin, uMax, vMax).</summary>
		public Vector4 uv => texture != null && texture.width > 0 && texture.height > 0
			? new Vector4(rect.xMin / texture.width, rect.yMin / texture.height, rect.xMax / texture.width, rect.yMax / texture.height)
			: new Vector4(0f, 0f, 1f, 1f);

		/// <summary>Size in world units (rect size / pixels per unit).</summary>
		public Vector2 size => new Vector2(rect.width / pixelsPerUnit, rect.height / pixelsPerUnit);

		/// <summary>Local bounds (min, max) in world units around the pivot.</summary>
		public Rect bounds
		{
			get
			{
				Vector2 s = size;
				return new Rect(-pivot.X * s.X, -pivot.Y * s.Y, s.X, s.Y);
			}
		}

		#endregion

		#region PrivateFields

		private readonly bool isSubAsset;

		#endregion

		#region Constructors

		/// <summary>A runtime sprite over a texture region.</summary>
		public Sprite(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit = 100f)
		{
			this.texture = texture;
			this.rect = rect;
			this.pivot = pivot;
			this.pixelsPerUnit = pixelsPerUnit;
			name = texture?.name;
		}

		/// <summary>A sprite covering the whole texture.</summary>
		public Sprite(Texture2D texture)
			: this(texture, new Rect(0f, 0f, texture?.width ?? 0, texture?.height ?? 0), new Vector2(0.5f, 0.5f))
		{ }

		internal Sprite(Texture2D texture, Rect rect, Vector2 pivot, float pixelsPerUnit, Vector4 border, string name, string assetPath, bool subAsset)
		{
			this.texture = texture;
			this.rect = rect;
			this.pivot = pivot;
			this.pixelsPerUnit = pixelsPerUnit;
			this.border = border;
			this.name = name;
			AssetPath = assetPath;
			isSubAsset = subAsset;
		}

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return "Sprite(" + (AssetPath ?? "runtime") + (SubAssetName != null ? "/" + SubAssetName : "") + " " + rect + ")";
		}

		#endregion
	}
}
