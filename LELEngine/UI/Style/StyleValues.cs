using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	/// <summary>A length in pixels or percent, or the keywords auto / none.</summary>
	public struct Length : IEquatable<Length>
	{
		#region PublicFields

		public float value;
		public LengthUnit unit;

		/// <summary>Auto: the layout decides (sizes, margins).</summary>
		public bool IsAuto => kind == 1;

		/// <summary>None: no constraint (max-width / max-height).</summary>
		public bool IsNone => kind == 2;

		public bool IsPercent => unit == LengthUnit.Percent && kind == 0;
		public bool IsPixels => unit == LengthUnit.Pixel && kind == 0;

		#endregion

		#region PrivateFields

		private byte kind; // 0 value, 1 auto, 2 none

		#endregion

		#region Constructors

		public Length(float value, LengthUnit unit = LengthUnit.Pixel)
		{
			this.value = value;
			this.unit = unit;
			kind = 0;
		}

		#endregion

		#region PublicMethods

		public static Length Pixels(float value)
		{
			return new Length(value, LengthUnit.Pixel);
		}

		public static Length Percent(float value)
		{
			return new Length(value, LengthUnit.Percent);
		}

		public static Length Auto()
		{
			return new Length { kind = 1 };
		}

		public static Length None()
		{
			return new Length { kind = 2 };
		}

		public static implicit operator Length(float pixels)
		{
			return Pixels(pixels);
		}

		public static implicit operator Length(int pixels)
		{
			return Pixels(pixels);
		}

		/// <summary>Resolves the length against a reference size (percent) to pixels; auto / none give NaN.</summary>
		public float Resolve(float reference)
		{
			if (kind != 0)
			{
				return float.NaN;
			}

			return unit == LengthUnit.Percent ? reference * value * 0.01f : value;
		}

		public bool Equals(Length other)
		{
			return kind == other.kind && (kind != 0 || (value == other.value && unit == other.unit));
		}

		public override bool Equals(object obj)
		{
			return obj is Length other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(kind, value, unit);
		}

		public static bool operator ==(Length a, Length b)
		{
			return a.Equals(b);
		}

		public static bool operator !=(Length a, Length b)
		{
			return !a.Equals(b);
		}

		public override string ToString()
		{
			if (IsAuto) return "auto";
			if (IsNone) return "none";
			return unit == LengthUnit.Percent ? value + "%" : value + "px";
		}

		#endregion
	}

	/// <summary>An angle (rotate).</summary>
	public struct Angle : IEquatable<Angle>
	{
		#region PublicFields

		public float value;
		public AngleUnit unit;

		#endregion

		#region Constructors

		public Angle(float value, AngleUnit unit = AngleUnit.Degree)
		{
			this.value = value;
			this.unit = unit;
		}

		#endregion

		#region PublicMethods

		public static Angle Degrees(float value)
		{
			return new Angle(value, AngleUnit.Degree);
		}

		public static Angle Radians(float value)
		{
			return new Angle(value, AngleUnit.Radian);
		}

		public static Angle Turns(float value)
		{
			return new Angle(value, AngleUnit.Turn);
		}

		public static implicit operator Angle(float degrees)
		{
			return Degrees(degrees);
		}

		public float ToDegrees()
		{
			switch (unit)
			{
				case AngleUnit.Gradian: return value * 0.9f;
				case AngleUnit.Radian: return value * (float)(180.0 / Math.PI);
				case AngleUnit.Turn: return value * 360f;
				default: return value;
			}
		}

		public bool Equals(Angle other)
		{
			return value == other.value && unit == other.unit;
		}

		public override bool Equals(object obj)
		{
			return obj is Angle other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(value, unit);
		}

		public override string ToString()
		{
			return value + (unit == AngleUnit.Degree ? "deg" : unit == AngleUnit.Radian ? "rad" : unit == AngleUnit.Turn ? "turn" : "grad");
		}

		#endregion
	}

	/// <summary>A duration (transitions).</summary>
	public struct TimeValue : IEquatable<TimeValue>
	{
		#region PublicFields

		public float value;
		public TimeUnit unit;

		#endregion

		#region Constructors

		public TimeValue(float value, TimeUnit unit = TimeUnit.Second)
		{
			this.value = value;
			this.unit = unit;
		}

		#endregion

		#region PublicMethods

		public float ToSeconds()
		{
			return unit == TimeUnit.Millisecond ? value * 0.001f : value;
		}

		public static implicit operator TimeValue(float seconds)
		{
			return new TimeValue(seconds);
		}

		public bool Equals(TimeValue other)
		{
			return value == other.value && unit == other.unit;
		}

		public override bool Equals(object obj)
		{
			return obj is TimeValue other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(value, unit);
		}

		public override string ToString()
		{
			return value + (unit == TimeUnit.Millisecond ? "ms" : "s");
		}

		#endregion
	}

	public struct EasingFunction : IEquatable<EasingFunction>
	{
		public EasingMode mode;

		public EasingFunction(EasingMode mode)
		{
			this.mode = mode;
		}

		public static implicit operator EasingFunction(EasingMode mode)
		{
			return new EasingFunction(mode);
		}

		public bool Equals(EasingFunction other)
		{
			return mode == other.mode;
		}

		public override bool Equals(object obj)
		{
			return obj is EasingFunction other && Equals(other);
		}

		public override int GetHashCode()
		{
			return (int)mode;
		}
	}

	public struct Translate : IEquatable<Translate>
	{
		public Length x;
		public Length y;

		public Translate(Length x, Length y)
		{
			this.x = x;
			this.y = y;
		}

		public static Translate None()
		{
			return new Translate(0f, 0f);
		}

		public bool Equals(Translate other)
		{
			return x == other.x && y == other.y;
		}

		public override bool Equals(object obj)
		{
			return obj is Translate other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(x, y);
		}
	}

	public struct Rotate : IEquatable<Rotate>
	{
		public Angle angle;

		public Rotate(Angle angle)
		{
			this.angle = angle;
		}

		public static Rotate None()
		{
			return new Rotate(0f);
		}

		public bool Equals(Rotate other)
		{
			return angle.Equals(other.angle);
		}

		public override bool Equals(object obj)
		{
			return obj is Rotate other && Equals(other);
		}

		public override int GetHashCode()
		{
			return angle.GetHashCode();
		}
	}

	public struct Scale : IEquatable<Scale>
	{
		public Vector2 value;

		public Scale(Vector2 value)
		{
			this.value = value;
		}

		public static Scale None()
		{
			return new Scale(Vector2.One);
		}

		public bool Equals(Scale other)
		{
			return value == other.value;
		}

		public override bool Equals(object obj)
		{
			return obj is Scale other && Equals(other);
		}

		public override int GetHashCode()
		{
			return value.GetHashCode();
		}
	}

	public struct TransformOrigin : IEquatable<TransformOrigin>
	{
		public Length x;
		public Length y;

		public TransformOrigin(Length x, Length y)
		{
			this.x = x;
			this.y = y;
		}

		/// <summary>The center of the element (the default).</summary>
		public static TransformOrigin Initial()
		{
			return new TransformOrigin(Length.Percent(50f), Length.Percent(50f));
		}

		public bool Equals(TransformOrigin other)
		{
			return x == other.x && y == other.y;
		}

		public override bool Equals(object obj)
		{
			return obj is TransformOrigin other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(x, y);
		}
	}

	public struct TextShadow : IEquatable<TextShadow>
	{
		public Vector2 offset;
		public float blurRadius;
		public Color4 color;

		public bool Equals(TextShadow other)
		{
			return offset == other.offset && blurRadius == other.blurRadius && color == other.color;
		}

		public override bool Equals(object obj)
		{
			return obj is TextShadow other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(offset, blurRadius, color);
		}
	}

	public struct BackgroundPosition : IEquatable<BackgroundPosition>
	{
		public BackgroundPositionKeyword keyword;
		public Length offset;

		public BackgroundPosition(BackgroundPositionKeyword keyword)
		{
			this.keyword = keyword;
			offset = 0f;
		}

		public BackgroundPosition(BackgroundPositionKeyword keyword, Length offset)
		{
			this.keyword = keyword;
			this.offset = offset;
		}

		public bool Equals(BackgroundPosition other)
		{
			return keyword == other.keyword && offset == other.offset;
		}

		public override bool Equals(object obj)
		{
			return obj is BackgroundPosition other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(keyword, offset);
		}
	}

	public struct BackgroundRepeat : IEquatable<BackgroundRepeat>
	{
		public Repeat x;
		public Repeat y;

		public BackgroundRepeat(Repeat x, Repeat y)
		{
			this.x = x;
			this.y = y;
		}

		public bool Equals(BackgroundRepeat other)
		{
			return x == other.x && y == other.y;
		}

		public override bool Equals(object obj)
		{
			return obj is BackgroundRepeat other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(x, y);
		}
	}

	public struct BackgroundSize : IEquatable<BackgroundSize>
	{
		public BackgroundSizeType sizeType;
		public Length x;
		public Length y;

		public BackgroundSize(Length x, Length y)
		{
			sizeType = BackgroundSizeType.Length;
			this.x = x;
			this.y = y;
		}

		public BackgroundSize(BackgroundSizeType sizeType)
		{
			this.sizeType = sizeType;
			x = Length.Auto();
			y = Length.Auto();
		}

		public bool Equals(BackgroundSize other)
		{
			return sizeType == other.sizeType && x == other.x && y == other.y;
		}

		public override bool Equals(object obj)
		{
			return obj is BackgroundSize other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(sizeType, x, y);
		}
	}

	/// <summary>A background image: a texture (or none).</summary>
	public struct Background : IEquatable<Background>
	{
		public Texture texture;

		/// <summary>A render target drawn as the image (a camera's <see cref="Camera.targetTexture" />); rows bottom up, flipped when drawn.</summary>
		public LELEngine.Rendering.RenderTexture renderTexture;

		public Background(Texture texture)
		{
			this.texture = texture;
			renderTexture = null;
		}

		public Background(LELEngine.Rendering.RenderTexture renderTexture)
		{
			texture = null;
			this.renderTexture = renderTexture;
		}

		public static Background FromTexture(Texture texture)
		{
			return new Background(texture);
		}

		public static Background FromRenderTexture(LELEngine.Rendering.RenderTexture renderTexture)
		{
			return new Background(renderTexture);
		}

		/// <summary>The background a style value's reference stands for: a texture, a render texture, or none.</summary>
		public static Background FromReference(object reference)
		{
			return reference is LELEngine.Rendering.RenderTexture renderTexture ? new Background(renderTexture) : new Background(reference as Texture);
		}

		/// <summary>Nothing to draw.</summary>
		public bool IsEmpty => texture == null && renderTexture == null;

		public bool Equals(Background other)
		{
			return ReferenceEquals(texture, other.texture) && ReferenceEquals(renderTexture, other.renderTexture);
		}

		public override bool Equals(object obj)
		{
			return obj is Background other && Equals(other);
		}

		public override int GetHashCode()
		{
			return texture != null ? texture.GetHashCode() : renderTexture != null ? renderTexture.GetHashCode() : 0;
		}
	}

	/// <summary>The font of a text element (-unity-font-definition / -unity-font).</summary>
	public struct FontDefinition : IEquatable<FontDefinition>
	{
		public FontAsset fontAsset;

		public FontDefinition(FontAsset fontAsset)
		{
			this.fontAsset = fontAsset;
		}

		public static FontDefinition FromFont(FontAsset font)
		{
			return new FontDefinition(font);
		}

		public bool Equals(FontDefinition other)
		{
			return ReferenceEquals(fontAsset, other.fontAsset);
		}

		public override bool Equals(object obj)
		{
			return obj is FontDefinition other && Equals(other);
		}

		public override int GetHashCode()
		{
			return fontAsset != null ? fontAsset.GetHashCode() : 0;
		}
	}

	/// <summary>Mouse cursor shown over an element: a built-in cursor type, or a texture with a hotspot.</summary>
	public struct Cursor : IEquatable<Cursor>
	{
		public CursorType type;
		public Texture texture;
		public Vector2 hotspot;

		public Cursor(CursorType type)
		{
			this.type = type;
			texture = null;
			hotspot = Vector2.Zero;
		}

		public bool Equals(Cursor other)
		{
			return type == other.type && ReferenceEquals(texture, other.texture) && hotspot == other.hotspot;
		}

		public override bool Equals(object obj)
		{
			return obj is Cursor other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(type, texture, hotspot);
		}
	}

	/// <summary>Named property reference in transition-property (a USS property name, "all" or "none").</summary>
	public struct StylePropertyName : IEquatable<StylePropertyName>
	{
		public string name;

		public StylePropertyName(string name)
		{
			this.name = name;
		}

		public static implicit operator StylePropertyName(string name)
		{
			return new StylePropertyName(name);
		}

		public bool Equals(StylePropertyName other)
		{
			return string.Equals(name, other.name, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is StylePropertyName other && Equals(other);
		}

		public override int GetHashCode()
		{
			return name != null ? name.GetHashCode() : 0;
		}

		public override string ToString()
		{
			return name;
		}
	}

	internal static class StyleListUtility
	{
		public static bool ListEquals<T>(List<T> a, List<T> b)
			where T : IEquatable<T>
		{
			if (ReferenceEquals(a, b))
			{
				return true;
			}

			if (a == null || b == null || a.Count != b.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Count; i++)
			{
				if (!a[i].Equals(b[i]))
				{
					return false;
				}
			}

			return true;
		}
	}
}
