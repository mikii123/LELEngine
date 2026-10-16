namespace LELEngine.UIElements
{
	// USS enumerations. The names match Unity's UI Toolkit so styles and code port over directly.

	public enum Align
	{
		Auto,
		FlexStart,
		Center,
		FlexEnd,
		Stretch
	}

	public enum Justify
	{
		FlexStart,
		Center,
		FlexEnd,
		SpaceBetween,
		SpaceAround,
		SpaceEvenly
	}

	public enum FlexDirection
	{
		Column,
		ColumnReverse,
		Row,
		RowReverse
	}

	public enum Wrap
	{
		NoWrap,
		Wrap,
		WrapReverse
	}

	public enum Position
	{
		Relative,
		Absolute
	}

	public enum DisplayStyle
	{
		Flex,
		None
	}

	public enum Visibility
	{
		Visible,
		Hidden
	}

	public enum Overflow
	{
		Visible,
		Hidden
	}

	/// <summary>Box the overflow clipping uses (-unity-overflow-clip-box).</summary>
	public enum OverflowClipBox
	{
		PaddingBox,
		ContentBox
	}

	public enum TextAnchor
	{
		UpperLeft,
		UpperCenter,
		UpperRight,
		MiddleLeft,
		MiddleCenter,
		MiddleRight,
		LowerLeft,
		LowerCenter,
		LowerRight
	}

	public enum WhiteSpace
	{
		Normal,
		NoWrap,
		Pre,
		PreWrap
	}

	public enum TextOverflow
	{
		Clip,
		Ellipsis
	}

	public enum TextOverflowPosition
	{
		End,
		Start,
		Middle
	}

	public enum FontStyle
	{
		Normal,
		Bold,
		Italic,
		BoldAndItalic
	}

	public enum ScaleMode
	{
		StretchToFill,
		ScaleAndCrop,
		ScaleToFit
	}

	public enum EasingMode
	{
		Ease,
		EaseIn,
		EaseOut,
		EaseInOut,
		Linear,
		EaseInSine,
		EaseOutSine,
		EaseInOutSine,
		EaseInCubic,
		EaseOutCubic,
		EaseInOutCubic,
		EaseInCirc,
		EaseOutCirc,
		EaseInOutCirc,
		EaseInElastic,
		EaseOutElastic,
		EaseInOutElastic,
		EaseInBack,
		EaseOutBack,
		EaseInOutBack,
		EaseInBounce,
		EaseOutBounce,
		EaseInOutBounce
	}

	public enum Repeat
	{
		NoRepeat,
		Repeat,
		Space,
		Round
	}

	public enum BackgroundPositionKeyword
	{
		Center,
		Top,
		Bottom,
		Left,
		Right
	}

	public enum BackgroundSizeType
	{
		Length,
		Cover,
		Contain
	}

	public enum SliceType
	{
		Stretch,
		Repeat
	}

	public enum LengthUnit
	{
		Pixel,
		Percent
	}

	public enum TimeUnit
	{
		Second,
		Millisecond
	}

	public enum AngleUnit
	{
		Degree,
		Gradian,
		Radian,
		Turn
	}

	/// <summary>Keywords a style property can hold instead of a value.</summary>
	public enum StyleKeyword
	{
		/// <summary>The property is not set (inline style unset: the USS cascade applies).</summary>
		Undefined,

		/// <summary>Explicitly unset by the inline style (same as Undefined for the cascade).</summary>
		Null,
		Auto,
		None,
		Initial,
		Inherit
	}

	/// <summary>Mouse cursors a panel host can show (USS cursor property).</summary>
	public enum CursorType
	{
		Arrow,
		Text,
		ResizeVertical,
		ResizeHorizontal,
		Link,
		SlideArrow,
		ResizeUpRight,
		ResizeUpLeft,
		MoveArrow,
		RotateArrow,
		ScaleArrow,
		ArrowPlus,
		ArrowMinus,
		Pan,
		Orbit,
		Zoom,
		FPS,
		SplitResizeUpDown,
		SplitResizeLeftRight
	}

	/// <summary>How an element responds to pointer picking.</summary>
	public enum PickingMode
	{
		/// <summary>The element receives pointer events when hit.</summary>
		Position,

		/// <summary>The element is transparent to pointer events (its children can still be picked).</summary>
		Ignore
	}

	/// <summary>Hints that let the renderer prepare for frequent changes (accepted for API compatibility).</summary>
	[System.Flags]
	public enum UsageHints
	{
		None = 0,
		DynamicTransform = 1,
		GroupTransform = 2,
		MaskContainer = 4,
		DynamicColor = 8
	}
}
