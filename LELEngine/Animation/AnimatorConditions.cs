using System;
using System.Collections.Generic;
using System.Text;

namespace LELEngine.Animation
{
	/// <summary>How a <see cref="ParameterCondition" /> compares its parameter.</summary>
	public enum ConditionMode
	{
		/// <summary>The value is true (bools), non-zero (numbers), non-empty (strings) or alive (references).</summary>
		If,

		IfNot,
		Greater,
		Less,
		GreaterOrEqual,
		LessOrEqual,
		Equals,
		NotEquals,

		/// <summary>A trigger that is set; taking the transition resets it.</summary>
		Trigger,

		IsNull,
		IsNotNull
	}

	/// <summary>
	///     A condition of an <see cref="AnimatorTransition" />. Built-ins compare parameters
	///     (<see cref="ParameterCondition" />) and group others (<see cref="ConditionGroup" />, any or all); derive
	///     from <see cref="ScriptCondition" /> in game scripts for anything else (polymorphic, chosen in the
	///     transition inspector).
	/// </summary>
	[Serializable]
	public abstract class AnimatorCondition
	{
		#region PublicMethods

		/// <summary>One-line text for the editor ("Speed > 0.5").</summary>
		public abstract string Describe(AnimatorController controller);

		public abstract AnimatorCondition Clone();

		/// <summary>True when the condition reads the parameter (the editor's "used by" and delete checks).</summary>
		public virtual bool UsesParameter(ulong parameterId)
		{
			return false;
		}

		/// <summary>Replaces references to a parameter (the parameter was deleted: 0 drops the condition).</summary>
		public virtual void RemapParameter(ulong fromId, ulong toId)
		{ }

		#endregion
	}

	/// <summary>Compares a parameter with a constant of its type, or with another parameter of the same type.</summary>
	[Serializable]
	public sealed class ParameterCondition : AnimatorCondition
	{
		#region PublicFields

		/// <summary>Id of the parameter compared.</summary>
		public ulong parameter;

		public ConditionMode mode = ConditionMode.If;

		/// <summary>The constant compared against (an <see cref="AnimatorValue" /> of the parameter's class); unused by If / IfNot / Trigger / null tests.</summary>
		public AnimatorValue value;

		/// <summary>Compare against another parameter instead of the constant (0: the constant).</summary>
		public ulong otherParameter;

		#endregion

		#region PublicMethods

		public override string Describe(AnimatorController controller)
		{
			AnimatorParameter p = controller?.FindParameter(parameter);
			string name = p?.name ?? "?";
			string right = otherParameter != 0 ? controller?.FindParameter(otherParameter)?.name ?? "?" : value?.ToString() ?? "?";
			switch (mode)
			{
				case ConditionMode.If: return name;
				case ConditionMode.IfNot: return "!" + name;
				case ConditionMode.Trigger: return name + " (trigger)";
				case ConditionMode.Greater: return name + " > " + right;
				case ConditionMode.Less: return name + " < " + right;
				case ConditionMode.GreaterOrEqual: return name + " >= " + right;
				case ConditionMode.LessOrEqual: return name + " <= " + right;
				case ConditionMode.Equals: return name + " == " + right;
				case ConditionMode.NotEquals: return name + " != " + right;
				case ConditionMode.IsNull: return name + " == null";
				case ConditionMode.IsNotNull: return name + " != null";
				default: return name;
			}
		}

		public override AnimatorCondition Clone()
		{
			return new ParameterCondition { parameter = parameter, mode = mode, value = value?.Clone(), otherParameter = otherParameter };
		}

		public override bool UsesParameter(ulong parameterId)
		{
			return parameter == parameterId || otherParameter == parameterId;
		}

		public override void RemapParameter(ulong fromId, ulong toId)
		{
			if (parameter == fromId) parameter = toId;
			if (otherParameter == fromId) otherParameter = toId;
		}

		/// <summary>The modes that make sense for a parameter type (the inspector's mode list).</summary>
		public static List<ConditionMode> ModesFor(AnimatorValue value)
		{
			var modes = new List<ConditionMode>();
			if (value == null)
			{
				return modes;
			}

			Type type = value.ValueType;
			if (value.IsTrigger)
			{
				modes.Add(ConditionMode.Trigger);
				return modes;
			}

			if (type == typeof(bool))
			{
				modes.Add(ConditionMode.If);
				modes.Add(ConditionMode.IfNot);
				return modes;
			}

			bool comparable = typeof(IComparable).IsAssignableFrom(type) || typeof(IComparable<>).MakeGenericType(type).IsAssignableFrom(type);
			bool reference = !type.IsValueType;
			if (comparable && type != typeof(string))
			{
				modes.Add(ConditionMode.Greater);
				modes.Add(ConditionMode.Less);
				modes.Add(ConditionMode.GreaterOrEqual);
				modes.Add(ConditionMode.LessOrEqual);
			}

			modes.Add(ConditionMode.Equals);
			modes.Add(ConditionMode.NotEquals);
			if (type == typeof(string) || type == typeof(float) || type == typeof(int))
			{
				modes.Add(ConditionMode.If);
				modes.Add(ConditionMode.IfNot);
			}

			if (reference)
			{
				modes.Add(ConditionMode.IsNull);
				modes.Add(ConditionMode.IsNotNull);
			}

			return modes;
		}

		#endregion
	}

	/// <summary>Several conditions combined: all of them (and) or any of them (or). Groups nest.</summary>
	[Serializable]
	public sealed class ConditionGroup : AnimatorCondition
	{
		#region PublicFields

		/// <summary>True: any member suffices (or); false: every member must hold (and).</summary>
		public bool any;

		public List<AnimatorCondition> conditions = new List<AnimatorCondition>();

		#endregion

		#region PublicMethods

		public override string Describe(AnimatorController controller)
		{
			if (conditions.Count == 0)
			{
				return any ? "(none)" : "(always)";
			}

			var text = new StringBuilder("(");
			for (int i = 0; i < conditions.Count; i++)
			{
				if (i > 0)
				{
					text.Append(any ? " or " : " and ");
				}

				text.Append(conditions[i]?.Describe(controller) ?? "?");
			}

			return text.Append(')').ToString();
		}

		public override AnimatorCondition Clone()
		{
			var copy = new ConditionGroup { any = any };
			foreach (AnimatorCondition condition in conditions)
			{
				copy.conditions.Add(condition?.Clone());
			}

			return copy;
		}

		public override bool UsesParameter(ulong parameterId)
		{
			foreach (AnimatorCondition condition in conditions)
			{
				if (condition != null && condition.UsesParameter(parameterId))
				{
					return true;
				}
			}

			return false;
		}

		public override void RemapParameter(ulong fromId, ulong toId)
		{
			foreach (AnimatorCondition condition in conditions)
			{
				condition?.RemapParameter(fromId, toId);
			}
		}

		#endregion
	}

	/// <summary>
	///     A condition written in game scripts: derive, add serialized fields (edited in the transition inspector),
	///     and return whether the transition may be taken. Evaluated on the main thread before the animators update
	///     (animators with script conditions never evaluate in parallel), so it may read anything of the scene.
	/// </summary>
	[Serializable]
	public abstract class ScriptCondition : AnimatorCondition
	{
		#region PublicMethods

		public abstract bool Evaluate(Animator animator, int layerIndex);

		public override string Describe(AnimatorController controller)
		{
			return GetType().Name;
		}

		public override AnimatorCondition Clone()
		{
			return (AnimatorCondition)MemberwiseClone();
		}

		#endregion
	}
}
