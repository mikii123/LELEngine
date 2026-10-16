using System;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     Axis-aligned rectangle: position of a corner and size. The UI uses it in y-down panel coordinates (top-left
	///     corner); sprites and 2D physics in y-up texture / world units (bottom-left corner).
	/// </summary>
	[Serializable]
	public struct Rect : IEquatable<Rect>
	{
		#region PublicFields

		public float x;
		public float y;
		public float width;
		public float height;

		public static readonly Rect zero = new Rect(0f, 0f, 0f, 0f);

		public float xMin
		{
			get => x;
			set
			{
				width += x - value;
				x = value;
			}
		}

		public float yMin
		{
			get => y;
			set
			{
				height += y - value;
				y = value;
			}
		}

		public float xMax
		{
			get => x + width;
			set => width = value - x;
		}

		public float yMax
		{
			get => y + height;
			set => height = value - y;
		}

		public Vector2 position
		{
			get => new Vector2(x, y);
			set
			{
				x = value.X;
				y = value.Y;
			}
		}

		public Vector2 size
		{
			get => new Vector2(width, height);
			set
			{
				width = value.X;
				height = value.Y;
			}
		}

		public Vector2 min => new Vector2(x, y);
		public Vector2 max => new Vector2(x + width, y + height);
		public Vector2 center => new Vector2(x + width * 0.5f, y + height * 0.5f);

		#endregion

		#region Constructors

		public Rect(float x, float y, float width, float height)
		{
			this.x = x;
			this.y = y;
			this.width = width;
			this.height = height;
		}

		public Rect(Vector2 position, Vector2 size)
		{
			x = position.X;
			y = position.Y;
			width = size.X;
			height = size.Y;
		}

		#endregion

		#region PublicMethods

		public static Rect MinMaxRect(float xmin, float ymin, float xmax, float ymax)
		{
			return new Rect(xmin, ymin, xmax - xmin, ymax - ymin);
		}

		public bool Contains(Vector2 point)
		{
			return point.X >= x && point.X < x + width && point.Y >= y && point.Y < y + height;
		}

		public bool Overlaps(Rect other)
		{
			return other.xMax > xMin && other.xMin < xMax && other.yMax > yMin && other.yMin < yMax;
		}

		/// <summary>Intersection of two rectangles (an empty rectangle when they do not overlap).</summary>
		public static Rect Intersect(Rect a, Rect b)
		{
			float x0 = Math.Max(a.xMin, b.xMin);
			float y0 = Math.Max(a.yMin, b.yMin);
			float x1 = Math.Min(a.xMax, b.xMax);
			float y1 = Math.Min(a.yMax, b.yMax);
			if (x1 <= x0 || y1 <= y0)
			{
				return new Rect(x0, y0, 0f, 0f);
			}

			return new Rect(x0, y0, x1 - x0, y1 - y0);
		}

		public static Rect Union(Rect a, Rect b)
		{
			float x0 = Math.Min(a.xMin, b.xMin);
			float y0 = Math.Min(a.yMin, b.yMin);
			float x1 = Math.Max(a.xMax, b.xMax);
			float y1 = Math.Max(a.yMax, b.yMax);
			return new Rect(x0, y0, x1 - x0, y1 - y0);
		}

		public bool IsEmpty => width <= 0f || height <= 0f;

		public bool Equals(Rect other)
		{
			return x == other.x && y == other.y && width == other.width && height == other.height;
		}

		public override bool Equals(object obj)
		{
			return obj is Rect other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(x, y, width, height);
		}

		public static bool operator ==(Rect a, Rect b)
		{
			return a.Equals(b);
		}

		public static bool operator !=(Rect a, Rect b)
		{
			return !a.Equals(b);
		}

		public override string ToString()
		{
			return $"(x:{x:0.##}, y:{y:0.##}, w:{width:0.##}, h:{height:0.##})";
		}

		#endregion
	}
}

namespace LELEngine.UIElements
{
	/// <summary>
	///     2D affine transform (row-major 2x3: x' = m00 * x + m01 * y + tx, y' = m10 * x + m11 * y + ty) used for
	///     element-to-panel transforms. Cheaper than a 4x4 matrix and enough for translate / rotate / scale.
	/// </summary>
	public struct Matrix2x3
	{
		#region PublicFields

		public float m00, m01, tx;
		public float m10, m11, ty;

		public static readonly Matrix2x3 identity = new Matrix2x3 { m00 = 1f, m11 = 1f };

		public bool isIdentity => m00 == 1f && m01 == 0f && tx == 0f && m10 == 0f && m11 == 1f && ty == 0f;

		/// <summary>Only translation (no rotation, scale or skew): rectangles stay axis-aligned.</summary>
		public bool isTranslationOnly => m00 == 1f && m01 == 0f && m10 == 0f && m11 == 1f;

		/// <summary>No rotation or skew (translation and axis scale only): rectangles stay axis-aligned.</summary>
		public bool isAxisAligned => m01 == 0f && m10 == 0f;

		#endregion

		#region PublicMethods

		public static Matrix2x3 Translation(float x, float y)
		{
			return new Matrix2x3 { m00 = 1f, m11 = 1f, tx = x, ty = y };
		}

		/// <summary>Translate, then rotate (degrees, clockwise on screen) and scale about the origin point.</summary>
		public static Matrix2x3 TRS(Vector2 translation, float angleDegrees, Vector2 scale, Vector2 origin)
		{
			float radians = angleDegrees * (float)(Math.PI / 180.0);
			float c = (float)Math.Cos(radians);
			float s = (float)Math.Sin(radians);
			// M = T(translation + origin) * R * S * T(-origin)
			float a = c * scale.X;
			float b = -s * scale.Y;
			float d = s * scale.X;
			float e = c * scale.Y;
			return new Matrix2x3
			{
				m00 = a,
				m01 = b,
				m10 = d,
				m11 = e,
				tx = translation.X + origin.X - (a * origin.X + b * origin.Y),
				ty = translation.Y + origin.Y - (d * origin.X + e * origin.Y)
			};
		}

		/// <summary>this * other: applies <paramref name="other" /> first, then this.</summary>
		public Matrix2x3 Multiply(in Matrix2x3 other)
		{
			return new Matrix2x3
			{
				m00 = m00 * other.m00 + m01 * other.m10,
				m01 = m00 * other.m01 + m01 * other.m11,
				tx = m00 * other.tx + m01 * other.ty + tx,
				m10 = m10 * other.m00 + m11 * other.m10,
				m11 = m10 * other.m01 + m11 * other.m11,
				ty = m10 * other.tx + m11 * other.ty + ty
			};
		}

		public Vector2 MultiplyPoint(Vector2 p)
		{
			return new Vector2(m00 * p.X + m01 * p.Y + tx, m10 * p.X + m11 * p.Y + ty);
		}

		public Vector2 MultiplyVector(Vector2 v)
		{
			return new Vector2(m00 * v.X + m01 * v.Y, m10 * v.X + m11 * v.Y);
		}

		public Matrix2x3 Inverse()
		{
			float det = m00 * m11 - m01 * m10;
			if (Math.Abs(det) < 1e-12f)
			{
				return identity;
			}

			float inv = 1f / det;
			Matrix2x3 r;
			r.m00 = m11 * inv;
			r.m01 = -m01 * inv;
			r.m10 = -m10 * inv;
			r.m11 = m00 * inv;
			r.tx = -(r.m00 * tx + r.m01 * ty);
			r.ty = -(r.m10 * tx + r.m11 * ty);
			return r;
		}

		/// <summary>Axis-aligned bounds of a transformed rectangle.</summary>
		public Rect TransformRect(Rect rect)
		{
			if (isAxisAligned)
			{
				float x0 = m00 * rect.x + tx;
				float y0 = m11 * rect.y + ty;
				float x1 = m00 * rect.xMax + tx;
				float y1 = m11 * rect.yMax + ty;
				return Rect.MinMaxRect(Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Max(y0, y1));
			}

			Vector2 a = MultiplyPoint(rect.min);
			Vector2 b = MultiplyPoint(new Vector2(rect.xMax, rect.yMin));
			Vector2 c = MultiplyPoint(rect.max);
			Vector2 d = MultiplyPoint(new Vector2(rect.xMin, rect.yMax));
			float minX = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
			float minY = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
			float maxX = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
			float maxY = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
			return Rect.MinMaxRect(minX, minY, maxX, maxY);
		}

		public bool Equals(in Matrix2x3 other)
		{
			return m00 == other.m00 && m01 == other.m01 && tx == other.tx && m10 == other.m10 && m11 == other.m11 && ty == other.ty;
		}

		#endregion
	}
}
