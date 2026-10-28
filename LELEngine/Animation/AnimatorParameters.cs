using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>
	///     A typed value of an animator parameter (its default in the controller, a condition's threshold): the
	///     class gives the type, so a parameter can hold anything the serializer stores, not only numbers and bools.
	///     Derive from <see cref="AnimatorValue{T}" /> in game scripts to add a parameter type (it appears in the
	///     controller's Add Parameter menu); the built-ins are below.
	/// </summary>
	[Serializable]
	public abstract class AnimatorValue
	{
		#region PublicFields

		/// <summary>The type the value holds.</summary>
		public abstract Type ValueType { get; }

		/// <summary>The value, boxed.</summary>
		public abstract object BoxedValue { get; set; }

		/// <summary>A trigger: a bool the animator resets after a transition consumed it.</summary>
		public virtual bool IsTrigger => false;

		/// <summary>Name shown in the parameter type menus ("Float", "Vector3", ...).</summary>
		public virtual string TypeLabel => SerializationUtility_NicifyType(GetType().Name);

		#endregion

		#region PublicMethods

		/// <summary>A runtime slot for a parameter of this type, holding this value initially.</summary>
		public abstract ParameterState CreateState(AnimatorParameter parameter);

		public abstract AnimatorValue Clone();

		/// <summary>Compiles a comparison of a runtime slot against this value (or another slot) into a delegate.</summary>
		public abstract Func<bool> CompileCondition(ParameterState state, ConditionMode mode, ParameterState other);

		public override string ToString()
		{
			return BoxedValue?.ToString() ?? "null";
		}

		private static string SerializationUtility_NicifyType(string name)
		{
			return name.EndsWith("Value", StringComparison.Ordinal) ? name.Substring(0, name.Length - 5) : name;
		}

		#endregion
	}

	/// <summary>An <see cref="AnimatorValue" /> of type <typeparamref name="T" /> (any serializable type).</summary>
	[Serializable]
	public abstract class AnimatorValue<T> : AnimatorValue
	{
		#region PublicFields

		public T value;

		public override Type ValueType => typeof(T);

		public override object BoxedValue
		{
			get => value;
			set => this.value = value is T typed ? typed : default;
		}

		#endregion

		#region PublicMethods

		public override ParameterState CreateState(AnimatorParameter parameter)
		{
			return new ParameterState<T>(parameter) { value = value };
		}

		public override AnimatorValue Clone()
		{
			var copy = (AnimatorValue<T>)MemberwiseClone();
			return copy;
		}

		public override Func<bool> CompileCondition(ParameterState state, ConditionMode mode, ParameterState other)
		{
			var typed = state as ParameterState<T>;
			var otherTyped = other as ParameterState<T>;
			if (typed == null)
			{
				return null;
			}

			T threshold = value;
			EqualityComparer<T> equality = EqualityComparer<T>.Default;
			Comparer<T> comparer = Comparer<T>.Default;
			bool comparable = typeof(IComparable<T>).IsAssignableFrom(typeof(T)) || typeof(IComparable).IsAssignableFrom(typeof(T));
			switch (mode)
			{
				case ConditionMode.If:
				case ConditionMode.Trigger:
					return () => IsTruthy(typed.value);
				case ConditionMode.IfNot:
					return () => !IsTruthy(typed.value);
				case ConditionMode.Equals:
					return otherTyped != null ? () => equality.Equals(typed.value, otherTyped.value) : () => equality.Equals(typed.value, threshold);
				case ConditionMode.NotEquals:
					return otherTyped != null ? () => !equality.Equals(typed.value, otherTyped.value) : () => !equality.Equals(typed.value, threshold);
				case ConditionMode.Greater:
					if (!comparable) return null;
					return otherTyped != null ? () => comparer.Compare(typed.value, otherTyped.value) > 0 : () => comparer.Compare(typed.value, threshold) > 0;
				case ConditionMode.Less:
					if (!comparable) return null;
					return otherTyped != null ? () => comparer.Compare(typed.value, otherTyped.value) < 0 : () => comparer.Compare(typed.value, threshold) < 0;
				case ConditionMode.GreaterOrEqual:
					if (!comparable) return null;
					return otherTyped != null ? () => comparer.Compare(typed.value, otherTyped.value) >= 0 : () => comparer.Compare(typed.value, threshold) >= 0;
				case ConditionMode.LessOrEqual:
					if (!comparable) return null;
					return otherTyped != null ? () => comparer.Compare(typed.value, otherTyped.value) <= 0 : () => comparer.Compare(typed.value, threshold) <= 0;
				case ConditionMode.IsNull:
					return () => IsNull(typed.value);
				case ConditionMode.IsNotNull:
					return () => !IsNull(typed.value);
				default:
					return null;
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsTruthy(T value)
		{
			switch (value)
			{
				case null: return false;
				case bool b: return b;
				case float f: return f != 0f;
				case int i: return i != 0;
				case double d: return d != 0d;
				case long l: return l != 0;
				case string s: return s.Length > 0;
				case EngineObject e: return !e.IsDestroyed;
				default: return true;
			}
		}

		private static bool IsNull(T value)
		{
			if (value == null)
			{
				return true;
			}

			return value is EngineObject e && e.IsDestroyed;
		}

		#endregion
	}

	[Serializable] public sealed class FloatValue : AnimatorValue<float> { public override string TypeLabel => "Float"; }
	[Serializable] public sealed class IntValue : AnimatorValue<int> { public override string TypeLabel => "Int"; }
	[Serializable] public sealed class BoolValue : AnimatorValue<bool> { public override string TypeLabel => "Bool"; }

	/// <summary>A bool the animator resets to false once a transition with a Trigger condition on it was taken.</summary>
	[Serializable]
	public sealed class TriggerValue : AnimatorValue<bool>
	{
		public override bool IsTrigger => true;
		public override string TypeLabel => "Trigger";
	}

	[Serializable] public sealed class StringValue : AnimatorValue<string> { public override string TypeLabel => "String"; }
	[Serializable] public sealed class Vector2Value : AnimatorValue<Vector2> { public override string TypeLabel => "Vector2"; }
	[Serializable] public sealed class Vector3Value : AnimatorValue<Vector3> { public override string TypeLabel => "Vector3"; }
	[Serializable] public sealed class Vector4Value : AnimatorValue<Vector4> { public override string TypeLabel => "Vector4"; }
	[Serializable] public sealed class QuaternionValue : AnimatorValue<Quaternion> { public QuaternionValue() { value = Quaternion.Identity; } public override string TypeLabel => "Quaternion"; }
	[Serializable] public sealed class ColorValue : AnimatorValue<Color4> { public ColorValue() { value = Color4.White; } public override string TypeLabel => "Color"; }

	/// <summary>A reference to a scene object, component or ScriptableObject (compare with Equals / Is Null).</summary>
	[Serializable] public sealed class ObjectValue : AnimatorValue<EngineObject> { public override string TypeLabel => "Object"; }

	/// <summary>A reference to an asset file (texture, material, mesh, clip, ...).</summary>
	[Serializable] public sealed class AssetValue : AnimatorValue<IAsset> { public override string TypeLabel => "Asset"; }

	/// <summary>A parameter of an <see cref="AnimatorController" />: a name, a stable id and a typed default value.</summary>
	[Serializable]
	public sealed class AnimatorParameter
	{
		#region PublicFields

		public string name = "";

		/// <summary>Stable id: conditions and blend trees reference the parameter by it, so renaming never breaks them.</summary>
		public ulong id;

		/// <summary>The default (its class is the parameter's type).</summary>
		public AnimatorValue defaultValue;

		/// <summary>A note shown in the parameter list's tooltip.</summary>
		public string note = "";

		public Type ValueType => defaultValue?.ValueType;
		public bool IsTrigger => defaultValue != null && defaultValue.IsTrigger;
		public string TypeLabel => defaultValue?.TypeLabel ?? "?";

		#endregion

		#region PublicMethods

		public AnimatorParameter Clone()
		{
			return new AnimatorParameter { name = name, id = id, defaultValue = defaultValue?.Clone(), note = note };
		}

		public override string ToString()
		{
			return name + " (" + TypeLabel + ")";
		}

		#endregion
	}

	/// <summary>The live value of a parameter in one <see cref="Animator" />.</summary>
	public abstract class ParameterState
	{
		#region PublicFields

		public readonly AnimatorParameter Parameter;
		public string Name => Parameter.name;
		public ulong Id => Parameter.id;
		public bool IsTrigger => Parameter.IsTrigger;

		/// <summary>Bumped on every write.</summary>
		public int Version;

		public abstract object Boxed { get; set; }
		public abstract Type ValueType { get; }

		#endregion

		#region Constructors

		protected ParameterState(AnimatorParameter parameter)
		{
			Parameter = parameter;
		}

		#endregion

		#region PublicMethods

		/// <summary>Resets to the parameter's default.</summary>
		public abstract void Reset();

		/// <summary>Triggers: back to false (after a transition consumed it, or ResetTrigger).</summary>
		public abstract void ResetTrigger();

		public abstract float AsFloat();

		#endregion
	}

	public sealed class ParameterState<T> : ParameterState
	{
		#region PublicFields

		public T value;

		public override object Boxed
		{
			get => value;
			set
			{
				if (value is T typed)
				{
					this.value = typed;
					Version++;
				}
				else if (value == null && !typeof(T).IsValueType)
				{
					this.value = default;
					Version++;
				}
				else if (value != null)
				{
					// Numbers convert between each other (SetFloat on an int parameter and the like).
					try
					{
						this.value = (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
						Version++;
					}
					catch (Exception)
					{
						// Incompatible value: ignored.
					}
				}
			}
		}

		public override Type ValueType => typeof(T);

		#endregion

		#region Constructors

		public ParameterState(AnimatorParameter parameter)
			: base(parameter)
		{ }

		#endregion

		#region PublicMethods

		public void Set(T newValue)
		{
			value = newValue;
			Version++;
		}

		public override void Reset()
		{
			value = Parameter.defaultValue is AnimatorValue<T> typed ? typed.value : default;
			Version++;
		}

		public override void ResetTrigger()
		{
			if (typeof(T) == typeof(bool))
			{
				value = (T)(object)false;
				Version++;
			}
		}

		public override float AsFloat()
		{
			switch (value)
			{
				case float f: return f;
				case int i: return i;
				case bool b: return b ? 1f : 0f;
				case double d: return (float)d;
				case long l: return l;
				default: return 0f;
			}
		}

		public override string ToString()
		{
			return Name + " = " + (value?.ToString() ?? "null");
		}

		#endregion
	}
}
