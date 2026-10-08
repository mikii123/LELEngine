using System;

namespace LELEngine
{
	/// <summary>
	///     Serializes a non-public field into scenes and shows it in the inspector (public fields are serialized
	///     by default). Use <see cref="NonSerializedAttribute" /> to exclude a public field.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class SerializeField : Attribute
	{ }

	/// <summary>Serialized, but not shown in the inspector.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class HideInInspector : Attribute
	{ }

	/// <summary>
	///     The component also runs in edit mode (Awake, OnEnable, Start, Update, ...), not only while the game plays.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = true)]
	public sealed class ExecuteAlways : Attribute
	{ }

	/// <summary>Inspector slider range for a numeric field.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class RangeAttribute : Attribute
	{
		public readonly float Min;
		public readonly float Max;

		public RangeAttribute(float min, float max)
		{
			Min = min;
			Max = max;
		}
	}

	/// <summary>Inspector tooltip of a field.</summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class TooltipAttribute : Attribute
	{
		public readonly string Text;

		public TooltipAttribute(string text)
		{
			Text = text;
		}
	}

	/// <summary>Inspector header shown above a field.</summary>
	[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
	public sealed class HeaderAttribute : Attribute
	{
		public readonly string Text;

		public HeaderAttribute(string text)
		{
			Text = text;
		}
	}

	/// <summary>Editor-only object flags.</summary>
	[Flags]
	public enum HideFlags
	{
		None = 0,

		/// <summary>Not listed in the hierarchy.</summary>
		HideInHierarchy = 1,

		/// <summary>Not written when the scene is saved.</summary>
		DontSave = 2,

		HideAndDontSave = HideInHierarchy | DontSave
	}
}
