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

	/// <summary>
	///     Exposes a field or property of a script to animation (clips, timelines, recording keys) even when the
	///     inspector does not show it: private, not serialized, or hidden. Public and serialized values of a script
	///     are exposed already.
	/// </summary>
	/// <example><c>[Animatable] private float thrust; [Animatable("Heat")] public float Temperature { get; set; }</c></example>
	[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
	public sealed class AnimatableAttribute : Attribute
	{
		/// <summary>The name shown in the editor (null: the member's).</summary>
		public readonly string DisplayName;

		public AnimatableAttribute(string displayName = null)
		{
			DisplayName = displayName;
		}
	}

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

	/// <summary>
	///     The inspector shows the field only while a member of the same object says so (Odin's ShowIf): a bool field,
	///     property or parameterless method, or any of them equal to <see cref="Value" />. Several on one field must all hold.
	/// </summary>
	/// <example><c>[ShowIf(nameof(Type), LightType.Spot)] public float SpotAngle;</c> <c>[ShowIf(nameof(UseGravity))] public float Gravity;</c></example>
	[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
	public sealed class ShowIfAttribute : Attribute
	{
		/// <summary>The member read (field, property or method without parameters).</summary>
		public readonly string Member;

		/// <summary>The value it must equal; without one the member is read as a bool.</summary>
		public readonly object Value;

		public readonly bool HasValue;

		public ShowIfAttribute(string member)
		{
			Member = member;
		}

		public ShowIfAttribute(string member, object value)
		{
			Member = member;
			Value = value;
			HasValue = true;
		}
	}

	/// <summary>The inspector hides the field while a member of the same object says so (Odin's HideIf; see <see cref="ShowIfAttribute" />).</summary>
	[AttributeUsage(AttributeTargets.Field, AllowMultiple = true)]
	public sealed class HideIfAttribute : Attribute
	{
		public readonly string Member;
		public readonly object Value;
		public readonly bool HasValue;

		public HideIfAttribute(string member)
		{
			Member = member;
		}

		public HideIfAttribute(string member, object value)
		{
			Member = member;
			Value = value;
			HasValue = true;
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

	/// <summary>
	///     A reference to a ScriptableObject asset can be unfolded in the inspector to edit the asset's fields in place
	///     (Odin's InlineEditor); without it the field shows the reference only. On a list or dictionary it applies to the
	///     elements.
	/// </summary>
	[AttributeUsage(AttributeTargets.Field)]
	public sealed class InlineEditorAttribute : Attribute
	{ }

	/// <summary>Theme colors of component kinds in the inspector (Preferences > Theme: "Component ...").</summary>
	public enum InspectorColorKind
	{
		Physics,
		Rendering,
		Audio,
		Camera,
		Lighting,
		Navigation,
		Effects,
		UI
	}

	/// <summary>
	///     Colors the component's inspector header (a bar on the left, a light tint) and a faint background behind its
	///     fields, as the engine's own components: a kind's color from the editor theme, or a fixed color. Subclasses
	///     inherit it.
	/// </summary>
	/// <example><c>[InspectorColor(InspectorColorKind.Physics)] public class Thruster : Behaviour { }</c></example>
	[AttributeUsage(AttributeTargets.Class, Inherited = true)]
	public sealed class InspectorColorAttribute : Attribute
	{
		/// <summary>The theme's color of this kind; null for a fixed color (<see cref="R" />, <see cref="G" />, <see cref="B" />).</summary>
		public readonly InspectorColorKind? Kind;

		/// <summary>A fixed color (0..1), the same in every theme.</summary>
		public readonly float R, G, B;

		public InspectorColorAttribute(InspectorColorKind kind)
		{
			Kind = kind;
		}

		public InspectorColorAttribute(float r, float g, float b)
		{
			R = r;
			G = g;
			B = b;
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
