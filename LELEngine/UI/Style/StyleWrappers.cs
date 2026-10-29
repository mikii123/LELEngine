using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	// Inline style values: a value or a keyword (auto, none, initial, null = unset). The implicit conversions let
	// code write style.width = 100 or style.width = StyleKeyword.Auto, as with Unity's IStyle.

	public struct StyleLength : IEquatable<StyleLength>
	{
		public Length value;
		public StyleKeyword keyword;

		public StyleLength(Length value)
		{
			this.value = value;
			keyword = value.IsAuto ? StyleKeyword.Auto : value.IsNone ? StyleKeyword.None : StyleKeyword.Undefined;
		}

		public StyleLength(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleLength(Length value) => new StyleLength(value);
		public static implicit operator StyleLength(float pixels) => new StyleLength(Length.Pixels(pixels));
		public static implicit operator StyleLength(int pixels) => new StyleLength(Length.Pixels(pixels));
		public static implicit operator StyleLength(StyleKeyword keyword) => new StyleLength(keyword);

		public bool Equals(StyleLength other) => keyword == other.keyword && value == other.value;
		public override bool Equals(object obj) => obj is StyleLength other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
		public override string ToString() => keyword != StyleKeyword.Undefined ? keyword.ToString() : value.ToString();
	}

	public struct StyleFloat : IEquatable<StyleFloat>
	{
		public float value;
		public StyleKeyword keyword;

		public StyleFloat(float value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleFloat(StyleKeyword keyword)
		{
			value = 0f;
			this.keyword = keyword;
		}

		public static implicit operator StyleFloat(float value) => new StyleFloat(value);
		public static implicit operator StyleFloat(StyleKeyword keyword) => new StyleFloat(keyword);

		public bool Equals(StyleFloat other) => keyword == other.keyword && value == other.value;
		public override bool Equals(object obj) => obj is StyleFloat other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleInt : IEquatable<StyleInt>
	{
		public int value;
		public StyleKeyword keyword;

		public StyleInt(int value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleInt(StyleKeyword keyword)
		{
			value = 0;
			this.keyword = keyword;
		}

		public static implicit operator StyleInt(int value) => new StyleInt(value);
		public static implicit operator StyleInt(StyleKeyword keyword) => new StyleInt(keyword);

		public bool Equals(StyleInt other) => keyword == other.keyword && value == other.value;
		public override bool Equals(object obj) => obj is StyleInt other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleColor : IEquatable<StyleColor>
	{
		public Color4 value;
		public StyleKeyword keyword;

		public StyleColor(Color4 value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleColor(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleColor(Color4 value) => new StyleColor(value);
		public static implicit operator StyleColor(StyleKeyword keyword) => new StyleColor(keyword);

		public bool Equals(StyleColor other) => keyword == other.keyword && value == other.value;
		public override bool Equals(object obj) => obj is StyleColor other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleEnum<T> : IEquatable<StyleEnum<T>>
		where T : struct, Enum
	{
		public T value;
		public StyleKeyword keyword;

		public StyleEnum(T value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleEnum(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleEnum<T>(T value) => new StyleEnum<T>(value);
		public static implicit operator StyleEnum<T>(StyleKeyword keyword) => new StyleEnum<T>(keyword);

		public bool Equals(StyleEnum<T> other) => keyword == other.keyword && EqualityComparer<T>.Default.Equals(value, other.value);
		public override bool Equals(object obj) => obj is StyleEnum<T> other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleBackground : IEquatable<StyleBackground>
	{
		public Background value;
		public StyleKeyword keyword;

		public StyleBackground(Background value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleBackground(Texture texture)
		{
			value = new Background(texture);
			keyword = StyleKeyword.Undefined;
		}

		public StyleBackground(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleBackground(Background value) => new StyleBackground(value);
		public static implicit operator StyleBackground(Texture texture) => new StyleBackground(texture);
		public static implicit operator StyleBackground(LELEngine.Rendering.RenderTexture renderTexture) => new StyleBackground(Background.FromRenderTexture(renderTexture));
		public static implicit operator StyleBackground(StyleKeyword keyword) => new StyleBackground(keyword);

		public bool Equals(StyleBackground other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleBackground other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleFont : IEquatable<StyleFont>
	{
		public FontAsset value;
		public StyleKeyword keyword;

		public StyleFont(FontAsset value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleFont(StyleKeyword keyword)
		{
			value = null;
			this.keyword = keyword;
		}

		public static implicit operator StyleFont(FontAsset value) => new StyleFont(value);
		public static implicit operator StyleFont(StyleKeyword keyword) => new StyleFont(keyword);

		public bool Equals(StyleFont other) => keyword == other.keyword && ReferenceEquals(value, other.value);
		public override bool Equals(object obj) => obj is StyleFont other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleFontDefinition : IEquatable<StyleFontDefinition>
	{
		public FontDefinition value;
		public StyleKeyword keyword;

		public StyleFontDefinition(FontDefinition value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleFontDefinition(FontAsset font)
		{
			value = new FontDefinition(font);
			keyword = StyleKeyword.Undefined;
		}

		public StyleFontDefinition(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleFontDefinition(FontDefinition value) => new StyleFontDefinition(value);
		public static implicit operator StyleFontDefinition(FontAsset font) => new StyleFontDefinition(font);
		public static implicit operator StyleFontDefinition(StyleKeyword keyword) => new StyleFontDefinition(keyword);

		public bool Equals(StyleFontDefinition other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleFontDefinition other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleCursor : IEquatable<StyleCursor>
	{
		public Cursor value;
		public StyleKeyword keyword;

		public StyleCursor(Cursor value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleCursor(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleCursor(Cursor value) => new StyleCursor(value);
		public static implicit operator StyleCursor(CursorType type) => new StyleCursor(new Cursor(type));
		public static implicit operator StyleCursor(StyleKeyword keyword) => new StyleCursor(keyword);

		public bool Equals(StyleCursor other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleCursor other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleTranslate : IEquatable<StyleTranslate>
	{
		public Translate value;
		public StyleKeyword keyword;

		public StyleTranslate(Translate value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleTranslate(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleTranslate(Translate value) => new StyleTranslate(value);
		public static implicit operator StyleTranslate(StyleKeyword keyword) => new StyleTranslate(keyword);

		public bool Equals(StyleTranslate other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleTranslate other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleRotate : IEquatable<StyleRotate>
	{
		public Rotate value;
		public StyleKeyword keyword;

		public StyleRotate(Rotate value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleRotate(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleRotate(Rotate value) => new StyleRotate(value);
		public static implicit operator StyleRotate(StyleKeyword keyword) => new StyleRotate(keyword);

		public bool Equals(StyleRotate other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleRotate other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleScale : IEquatable<StyleScale>
	{
		public Scale value;
		public StyleKeyword keyword;

		public StyleScale(Scale value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleScale(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleScale(Scale value) => new StyleScale(value);
		public static implicit operator StyleScale(StyleKeyword keyword) => new StyleScale(keyword);

		public bool Equals(StyleScale other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleScale other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleTransformOrigin : IEquatable<StyleTransformOrigin>
	{
		public TransformOrigin value;
		public StyleKeyword keyword;

		public StyleTransformOrigin(TransformOrigin value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleTransformOrigin(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleTransformOrigin(TransformOrigin value) => new StyleTransformOrigin(value);
		public static implicit operator StyleTransformOrigin(StyleKeyword keyword) => new StyleTransformOrigin(keyword);

		public bool Equals(StyleTransformOrigin other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleTransformOrigin other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleTextShadow : IEquatable<StyleTextShadow>
	{
		public TextShadow value;
		public StyleKeyword keyword;

		public StyleTextShadow(TextShadow value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleTextShadow(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleTextShadow(TextShadow value) => new StyleTextShadow(value);
		public static implicit operator StyleTextShadow(StyleKeyword keyword) => new StyleTextShadow(keyword);

		public bool Equals(StyleTextShadow other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleTextShadow other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleBackgroundPosition : IEquatable<StyleBackgroundPosition>
	{
		public BackgroundPosition value;
		public StyleKeyword keyword;

		public StyleBackgroundPosition(BackgroundPosition value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleBackgroundPosition(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleBackgroundPosition(BackgroundPosition value) => new StyleBackgroundPosition(value);
		public static implicit operator StyleBackgroundPosition(StyleKeyword keyword) => new StyleBackgroundPosition(keyword);

		public bool Equals(StyleBackgroundPosition other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleBackgroundPosition other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleBackgroundRepeat : IEquatable<StyleBackgroundRepeat>
	{
		public BackgroundRepeat value;
		public StyleKeyword keyword;

		public StyleBackgroundRepeat(BackgroundRepeat value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleBackgroundRepeat(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleBackgroundRepeat(BackgroundRepeat value) => new StyleBackgroundRepeat(value);
		public static implicit operator StyleBackgroundRepeat(StyleKeyword keyword) => new StyleBackgroundRepeat(keyword);

		public bool Equals(StyleBackgroundRepeat other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleBackgroundRepeat other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	public struct StyleBackgroundSize : IEquatable<StyleBackgroundSize>
	{
		public BackgroundSize value;
		public StyleKeyword keyword;

		public StyleBackgroundSize(BackgroundSize value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleBackgroundSize(StyleKeyword keyword)
		{
			value = default;
			this.keyword = keyword;
		}

		public static implicit operator StyleBackgroundSize(BackgroundSize value) => new StyleBackgroundSize(value);
		public static implicit operator StyleBackgroundSize(StyleKeyword keyword) => new StyleBackgroundSize(keyword);

		public bool Equals(StyleBackgroundSize other) => keyword == other.keyword && value.Equals(other.value);
		public override bool Equals(object obj) => obj is StyleBackgroundSize other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value, keyword);
	}

	/// <summary>A list value (transition properties, durations, delays and easing functions).</summary>
	public struct StyleList<T> : IEquatable<StyleList<T>>
		where T : IEquatable<T>
	{
		public List<T> value;
		public StyleKeyword keyword;

		public StyleList(List<T> value)
		{
			this.value = value;
			keyword = StyleKeyword.Undefined;
		}

		public StyleList(StyleKeyword keyword)
		{
			value = null;
			this.keyword = keyword;
		}

		public static implicit operator StyleList<T>(List<T> value) => new StyleList<T>(value);
		public static implicit operator StyleList<T>(StyleKeyword keyword) => new StyleList<T>(keyword);

		public bool Equals(StyleList<T> other) => keyword == other.keyword && StyleListUtility.ListEquals(value, other.value);
		public override bool Equals(object obj) => obj is StyleList<T> other && Equals(other);
		public override int GetHashCode() => HashCode.Combine(value != null ? value.Count : 0, keyword);
	}
}
