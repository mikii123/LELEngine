using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>A property an animation can bind to, as the clip editor lists them (<see cref="AnimationBinder.GetAnimatableProperties" />).</summary>
	public sealed class AnimatableProperty
	{
		public string Property;
		public string DisplayName;
		public AnimationValueKind Kind;
		public Type ValueType;

		/// <summary>The value the object has now (channels of the kind; discrete kinds give the object or string).</summary>
		public object CurrentValue;

		public override string ToString()
		{
			return DisplayName;
		}
	}

	/// <summary>
	///     A property of an object an animation writes to: the typed setter chosen when the binding was resolved
	///     (<see cref="AnimationBinder.Bind" />), so writing a frame costs one delegate call, no reflection.
	///     Rotations bound as Euler angles are stored and written as quaternions (<see cref="Kind" /> says the storage).
	/// </summary>
	public sealed class BoundProperty
	{
		#region PublicFields

		/// <summary>The component or GameObject written to.</summary>
		public object Target;

		/// <summary>Storage kind of the pose slot (Euler bindings store a quaternion).</summary>
		public AnimationValueKind Kind;

		public Type ValueType;

		/// <summary>The GameObject that holds the property.</summary>
		public GameObject GameObject;

		#endregion

		#region InternalFields

		internal Action<float> SetFloat;
		internal Func<float> GetFloat;
		internal Action<Vector2> SetVector2;
		internal Func<Vector2> GetVector2;
		internal Action<Vector3> SetVector3;
		internal Func<Vector3> GetVector3;
		internal Action<Vector4> SetVector4;
		internal Func<Vector4> GetVector4;
		internal Action<Quaternion> SetQuaternion;
		internal Func<Quaternion> GetQuaternion;
		internal Action<Color4> SetColor;
		internal Func<Color4> GetColor;
		internal Action<object> SetBoxed;
		internal Func<object> GetBoxed;

		#endregion

		#region PublicMethods

		/// <summary>Writes the channels at <paramref name="offset" /> of <paramref name="values" /> as the property's value.</summary>
		public void WriteChannels(float[] values, int offset)
		{
			switch (Kind)
			{
				case AnimationValueKind.Float:
					if (SetFloat != null) SetFloat(values[offset]);
					else SetBoxed?.Invoke(ConvertFloat(values[offset]));
					break;
				case AnimationValueKind.Int:
				case AnimationValueKind.Enum:
					SetBoxed?.Invoke(ConvertInt((long)Math.Round(values[offset])));
					break;
				case AnimationValueKind.Bool:
					SetBoxed?.Invoke(values[offset] > 0.5f);
					break;
				case AnimationValueKind.Vector2:
					SetVector2?.Invoke(new Vector2(values[offset], values[offset + 1]));
					break;
				case AnimationValueKind.Vector3:
					SetVector3?.Invoke(new Vector3(values[offset], values[offset + 1], values[offset + 2]));
					break;
				case AnimationValueKind.Vector4:
					SetVector4?.Invoke(new Vector4(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]));
					break;
				case AnimationValueKind.Quaternion:
				case AnimationValueKind.Euler:
				{
					var q = new Quaternion(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]);
					float length = q.Length;
					if (length > 1e-8f)
					{
						q = new Quaternion(q.X / length, q.Y / length, q.Z / length, q.W / length);
					}
					else
					{
						q = Quaternion.Identity;
					}

					SetQuaternion?.Invoke(q);
					break;
				}
				case AnimationValueKind.Color:
					SetColor?.Invoke(new Color4(values[offset], values[offset + 1], values[offset + 2], values[offset + 3]));
					break;
				case AnimationValueKind.Vector2i:
					SetBoxed?.Invoke(new Vector2i((int)Math.Round(values[offset]), (int)Math.Round(values[offset + 1])));
					break;
				case AnimationValueKind.Vector3i:
					SetBoxed?.Invoke(new Vector3i((int)Math.Round(values[offset]), (int)Math.Round(values[offset + 1]), (int)Math.Round(values[offset + 2])));
					break;
				case AnimationValueKind.Vector4i:
					SetBoxed?.Invoke(new Vector4i((int)Math.Round(values[offset]), (int)Math.Round(values[offset + 1]), (int)Math.Round(values[offset + 2]), (int)Math.Round(values[offset + 3])));
					break;
			}
		}

		/// <summary>Reads the property's value into channels (the object's values at bind time, write-defaults).</summary>
		public void ReadChannels(float[] values, int offset)
		{
			switch (Kind)
			{
				case AnimationValueKind.Float:
					values[offset] = GetFloat != null ? GetFloat() : ToFloat(GetBoxed?.Invoke());
					break;
				case AnimationValueKind.Int:
				case AnimationValueKind.Enum:
				case AnimationValueKind.Bool:
					values[offset] = ToFloat(GetBoxed?.Invoke());
					break;
				case AnimationValueKind.Vector2:
				{
					Vector2 v = GetVector2 != null ? GetVector2() : Vector2.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					break;
				}
				case AnimationValueKind.Vector3:
				{
					Vector3 v = GetVector3 != null ? GetVector3() : Vector3.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					values[offset + 2] = v.Z;
					break;
				}
				case AnimationValueKind.Vector4:
				{
					Vector4 v = GetVector4 != null ? GetVector4() : Vector4.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					values[offset + 2] = v.Z;
					values[offset + 3] = v.W;
					break;
				}
				case AnimationValueKind.Quaternion:
				case AnimationValueKind.Euler:
				{
					Quaternion q = GetQuaternion != null ? GetQuaternion() : Quaternion.Identity;
					values[offset] = q.X;
					values[offset + 1] = q.Y;
					values[offset + 2] = q.Z;
					values[offset + 3] = q.W;
					break;
				}
				case AnimationValueKind.Color:
				{
					Color4 c = GetColor != null ? GetColor() : Color4.White;
					values[offset] = c.R;
					values[offset + 1] = c.G;
					values[offset + 2] = c.B;
					values[offset + 3] = c.A;
					break;
				}
				case AnimationValueKind.Vector2i:
				{
					var v = GetBoxed?.Invoke() is Vector2i i ? i : Vector2i.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					break;
				}
				case AnimationValueKind.Vector3i:
				{
					var v = GetBoxed?.Invoke() is Vector3i i ? i : Vector3i.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					values[offset + 2] = v.Z;
					break;
				}
				case AnimationValueKind.Vector4i:
				{
					var v = GetBoxed?.Invoke() is Vector4i i ? i : Vector4i.Zero;
					values[offset] = v.X;
					values[offset + 1] = v.Y;
					values[offset + 2] = v.Z;
					values[offset + 3] = v.W;
					break;
				}
			}
		}

		/// <summary>Writes a string or reference value.</summary>
		public void WriteDiscrete(object value)
		{
			if (SetBoxed == null)
			{
				return;
			}

			if (value != null && ValueType != null && !ValueType.IsInstanceOfType(value))
			{
				return;
			}

			SetBoxed(value);
		}

		public object ReadDiscrete()
		{
			return GetBoxed?.Invoke();
		}

		/// <summary>True when the target object still exists.</summary>
		public bool IsAlive => Target is EngineObject engineObject ? !engineObject.IsDestroyed : Target != null;

		#endregion

		#region PrivateMethods

		private object ConvertFloat(float value)
		{
			if (ValueType == typeof(double)) return (double)value;
			return value;
		}

		private object ConvertInt(long value)
		{
			if (ValueType == null) return (int)value;
			if (ValueType.IsEnum) return Enum.ToObject(ValueType, value);
			switch (Type.GetTypeCode(ValueType))
			{
				case TypeCode.Int16: return (short)value;
				case TypeCode.Int64: return value;
				case TypeCode.Byte: return (byte)value;
				case TypeCode.SByte: return (sbyte)value;
				case TypeCode.UInt16: return (ushort)value;
				case TypeCode.UInt32: return (uint)value;
				case TypeCode.UInt64: return (ulong)value;
				default: return (int)value;
			}
		}

		private static float ToFloat(object value)
		{
			switch (value)
			{
				case null: return 0f;
				case float f: return f;
				case double d: return (float)d;
				case bool b: return b ? 1f : 0f;
				case int i: return i;
				case long l: return l;
				case short s: return s;
				case byte by: return by;
				case sbyte sb: return sb;
				case ushort us: return us;
				case uint ui: return ui;
				case ulong ul: return ul;
				case Enum e: return Convert.ToInt64(e);
				default: return 0f;
			}
		}

		#endregion
	}

	/// <summary>
	///     Resolves <see cref="AnimationBinding" />s against a GameObject hierarchy: the object by path and component
	///     type, the property by its path, as a <see cref="BoundProperty" /> with typed setters. Property paths name
	///     serialized fields (as the inspector shows them, or their C# names), nested with "." and list elements with
	///     "[i]"; public properties with a setter work too. Special properties: the Transform's "localPosition",
	///     "localRotation" (as quaternion or Euler angles: "localEulerAngles"), "localScale"; the GameObject's
	///     "active"; a component's "enabled".
	/// </summary>
	public static class AnimationBinder
	{
		#region PublicFields

		public const string LocalPosition = "localPosition";
		public const string LocalRotation = "localRotation";
		public const string LocalEulerAngles = "localEulerAngles";
		public const string LocalScale = "localScale";
		public const string ActiveProperty = "active";
		public const string EnabledProperty = "enabled";

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		/// <summary>The name a binding stores for a component type ("Namespace.Type").</summary>
		public static string TypeName(Type type)
		{
			return type == null || type == typeof(GameObject) ? "" : type.FullName;
		}

		/// <summary>"Transform" of "LELEngine.Transform" or "LELEngine.Transform, LELEngine".</summary>
		public static string ShortTypeName(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
			{
				return "";
			}

			int comma = typeName.IndexOf(',');
			if (comma >= 0)
			{
				typeName = typeName.Substring(0, comma);
			}

			int dot = typeName.LastIndexOf('.');
			return dot >= 0 ? typeName.Substring(dot + 1) : typeName;
		}

		/// <summary>The component type a binding names (null: the GameObject, or an unknown type).</summary>
		public static Type ResolveType(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
			{
				return null;
			}

			lock (sync)
			{
				if (typeCache.TryGetValue(typeName, out Type cached))
				{
					return cached;
				}
			}

			Type type = TypeRegistry.Resolve(typeName);
			if (type == null && typeName.IndexOf('.') < 0)
			{
				// A short name: the engine's component of that name, else any registered type.
				type = typeof(Behaviour).Assembly.GetType("LELEngine." + typeName) ?? FindByShortName(typeName);
			}

			lock (sync)
			{
				typeCache[typeName] = type;
			}

			return type;
		}

		/// <summary>Drops the resolved types (script reload).</summary>
		public static void ClearCache()
		{
			lock (sync)
			{
				typeCache.Clear();
			}
		}

		public static bool IsTransformProperty(string typeName, string property)
		{
			return ShortTypeName(typeName) == nameof(Transform) && (property == LocalPosition || property == LocalRotation || property == LocalEulerAngles || property == LocalScale);
		}

		/// <summary>The transform at a "A/B/C" path under <paramref name="root" /> (the root itself for an empty path), or null.</summary>
		public static Transform FindPath(Transform root, string path)
		{
			if (root == null)
			{
				return null;
			}

			if (string.IsNullOrEmpty(path))
			{
				return root;
			}

			Transform current = root;
			int start = 0;
			while (start <= path.Length)
			{
				int end = path.IndexOf('/', start);
				if (end < 0)
				{
					end = path.Length;
				}

				string name = path.Substring(start, end - start);
				if (name.Length > 0)
				{
					Transform next = null;
					IReadOnlyList<Transform> children = current.Children;
					for (int i = 0; i < children.Count; i++)
					{
						if (children[i].gameObject != null && children[i].gameObject.Name == name)
						{
							next = children[i];
							break;
						}
					}

					if (next == null)
					{
						return null;
					}

					current = next;
				}

				start = end + 1;
			}

			return current;
		}

		/// <summary>The path of <paramref name="target" /> relative to <paramref name="root" /> ("" for the root; null when not under it).</summary>
		public static string PathOf(Transform root, Transform target)
		{
			if (target == null || root == null)
			{
				return null;
			}

			var names = new List<string>();
			for (Transform t = target; t != null; t = t.parent)
			{
				if (t == root)
				{
					names.Reverse();
					return string.Join("/", names);
				}

				names.Add(t.gameObject?.Name ?? "");
			}

			return null;
		}

		/// <summary>Resolves a binding under <paramref name="root" />; null when the object, component or property is missing.</summary>
		public static BoundProperty Bind(GameObject root, AnimationBinding binding)
		{
			if (root == null || binding == null)
			{
				return null;
			}

			Transform transform = FindPath(root.transform, binding.path);
			if (transform == null || transform.gameObject == null)
			{
				return null;
			}

			GameObject go = transform.gameObject;
			if (string.IsNullOrEmpty(binding.type))
			{
				return BindGameObject(go, binding);
			}

			Type type = ResolveType(binding.type);
			if (type == null)
			{
				return null;
			}

			Behaviour component = go.GetComponent(type);
			if (component == null)
			{
				return null;
			}

			if (component is Transform t)
			{
				BoundProperty special = BindTransform(t, binding);
				if (special != null)
				{
					return special;
				}
			}

			if (binding.property == EnabledProperty)
			{
				return new BoundProperty
				{
					Target = component, GameObject = go, Kind = AnimationValueKind.Bool, ValueType = typeof(bool),
					SetBoxed = value => component.enabled = value is bool b && b, GetBoxed = () => component.enabled
				};
			}

			return BindMember(component, go, binding);
		}

		/// <summary>
		///     The properties an animation can bind on an object (a component or a GameObject): its serialized fields
		///     (nested ones with dotted paths, lists by index), public properties with a setter, and the special ones.
		/// </summary>
		public static List<AnimatableProperty> GetAnimatableProperties(object target, bool includeProperties = true)
		{
			var result = new List<AnimatableProperty>();
			if (target is GameObject go)
			{
				result.Add(new AnimatableProperty { Property = ActiveProperty, DisplayName = "Active", Kind = AnimationValueKind.Bool, ValueType = typeof(bool), CurrentValue = go.activeSelf });
				return result;
			}

			if (target is Transform transform)
			{
				result.Add(new AnimatableProperty { Property = LocalPosition, DisplayName = "Position", Kind = AnimationValueKind.Vector3, ValueType = typeof(Vector3), CurrentValue = transform.localPosition });
				result.Add(new AnimatableProperty { Property = LocalEulerAngles, DisplayName = "Rotation", Kind = AnimationValueKind.Euler, ValueType = typeof(Quaternion), CurrentValue = transform.localRotation });
				result.Add(new AnimatableProperty { Property = LocalRotation, DisplayName = "Rotation (quaternion)", Kind = AnimationValueKind.Quaternion, ValueType = typeof(Quaternion), CurrentValue = transform.localRotation });
				result.Add(new AnimatableProperty { Property = LocalScale, DisplayName = "Scale", Kind = AnimationValueKind.Vector3, ValueType = typeof(Vector3), CurrentValue = transform.localScale });
				return result;
			}

			if (target is Behaviour component)
			{
				result.Add(new AnimatableProperty { Property = EnabledProperty, DisplayName = "Enabled", Kind = AnimationValueKind.Bool, ValueType = typeof(bool), CurrentValue = component.enabled });
			}

			if (target != null)
			{
				CollectFields(target, target.GetType(), "", "", result, 0, new HashSet<string>(StringComparer.Ordinal));
				if (includeProperties)
				{
					CollectProperties(target, result);
				}
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private static Type FindByShortName(string shortName)
		{
			foreach (Assembly assembly in TypeRegistry.Assemblies)
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types;
				}

				foreach (Type type in types)
				{
					if (type != null && type.Name == shortName && typeof(Behaviour).IsAssignableFrom(type))
					{
						return type;
					}
				}
			}

			return null;
		}

		private static BoundProperty BindGameObject(GameObject go, AnimationBinding binding)
		{
			if (binding.property == ActiveProperty || binding.property == "activeSelf" || binding.property == "m_IsActive")
			{
				return new BoundProperty
				{
					Target = go, GameObject = go, Kind = AnimationValueKind.Bool, ValueType = typeof(bool),
					SetBoxed = value =>
					{
						bool active = value is bool b && b;
						if (go.activeSelf != active)
						{
							go.SetActive(active);
						}
					},
					GetBoxed = () => go.activeSelf
				};
			}

			if (binding.property == "layer")
			{
				return new BoundProperty
				{
					Target = go, GameObject = go, Kind = AnimationValueKind.Int, ValueType = typeof(int),
					SetBoxed = value => go.layer = value is int i ? i : go.layer, GetBoxed = () => go.layer
				};
			}

			return null;
		}

		private static BoundProperty BindTransform(Transform transform, AnimationBinding binding)
		{
			switch (binding.property)
			{
				case LocalPosition:
				case "m_LocalPosition":
				case "position":
					return new BoundProperty
					{
						Target = transform, GameObject = transform.gameObject, Kind = AnimationValueKind.Vector3, ValueType = typeof(Vector3),
						SetVector3 = v => transform.localPosition = v, GetVector3 = () => transform.localPosition
					};
				case LocalRotation:
				case "m_LocalRotation":
				case "rotation":
				case LocalEulerAngles:
				case "eulerAngles":
					return new BoundProperty
					{
						Target = transform, GameObject = transform.gameObject, Kind = AnimationValueKind.Quaternion, ValueType = typeof(Quaternion),
						SetQuaternion = q => transform.localRotation = q, GetQuaternion = () => transform.localRotation
					};
				case LocalScale:
				case "m_LocalScale":
				case "scale":
					return new BoundProperty
					{
						Target = transform, GameObject = transform.gameObject, Kind = AnimationValueKind.Vector3, ValueType = typeof(Vector3),
						SetVector3 = v => transform.localScale = v, GetVector3 = () => transform.localScale
					};
				default:
					return null;
			}
		}

		private static BoundProperty BindMember(object target, GameObject go, AnimationBinding binding)
		{
			MemberPath path = MemberPath.Resolve(target, binding.property);
			if (path == null)
			{
				return null;
			}

			AnimationValueKind? kind = AnimationBinding.KindOf(path.ValueType);
			if (kind == null)
			{
				return null;
			}

			AnimationValueKind storage = kind.Value;
			if (binding.kind == AnimationValueKind.Euler && storage == AnimationValueKind.Quaternion)
			{
				storage = AnimationValueKind.Quaternion;
			}
			else if (binding.kind != kind.Value && !(binding.kind == AnimationValueKind.Euler && kind.Value == AnimationValueKind.Quaternion))
			{
				// A binding of another kind than the property (the property's type changed): the property's kind wins
				// when the channel counts agree, else the binding cannot be applied.
				if (AnimationBinding.ChannelCountOf(binding.kind) != AnimationBinding.ChannelCountOf(kind.Value))
				{
					return null;
				}
			}

			var bound = new BoundProperty { Target = target, GameObject = go, Kind = storage, ValueType = path.ValueType };
			if (path.Steps.Length == 1 && path.Steps[0].Field != null && !target.GetType().IsValueType && path.Steps[0].ListIndex < 0)
			{
				// Depth one on a class: the serializer's compiled typed accessor.
				FieldAccessor accessor = path.Steps[0].Field.Accessor;
				switch (accessor)
				{
					case FieldAccessor<float> f:
						bound.SetFloat = v => f.Set(target, v);
						bound.GetFloat = () => f.Get(target);
						break;
					case FieldAccessor<Vector2> v2:
						bound.SetVector2 = v => v2.Set(target, v);
						bound.GetVector2 = () => v2.Get(target);
						break;
					case FieldAccessor<Vector3> v3:
						bound.SetVector3 = v => v3.Set(target, v);
						bound.GetVector3 = () => v3.Get(target);
						break;
					case FieldAccessor<Vector4> v4:
						bound.SetVector4 = v => v4.Set(target, v);
						bound.GetVector4 = () => v4.Get(target);
						break;
					case FieldAccessor<Quaternion> q:
						bound.SetQuaternion = v => q.Set(target, v);
						bound.GetQuaternion = () => q.Get(target);
						break;
					case FieldAccessor<Color4> c:
						bound.SetColor = v => c.Set(target, v);
						bound.GetColor = () => c.Get(target);
						break;
				}

				bound.SetBoxed = value => accessor.SetBoxed(target, value);
				bound.GetBoxed = () => accessor.GetBoxed(target);
			}
			else if (path.Steps.Length == 1 && path.Steps[0].Property != null && path.Steps[0].TypedSetter != null)
			{
				// A C# property on a class: delegates compiled from expression trees.
				Delegate setter = path.Steps[0].TypedSetter;
				Delegate getter = path.Steps[0].TypedGetter;
				switch (setter)
				{
					case Action<float> f:
						bound.SetFloat = f;
						bound.GetFloat = getter as Func<float>;
						break;
					case Action<Vector2> v2:
						bound.SetVector2 = v2;
						bound.GetVector2 = getter as Func<Vector2>;
						break;
					case Action<Vector3> v3:
						bound.SetVector3 = v3;
						bound.GetVector3 = getter as Func<Vector3>;
						break;
					case Action<Vector4> v4:
						bound.SetVector4 = v4;
						bound.GetVector4 = getter as Func<Vector4>;
						break;
					case Action<Quaternion> q:
						bound.SetQuaternion = q;
						bound.GetQuaternion = getter as Func<Quaternion>;
						break;
					case Action<Color4> c:
						bound.SetColor = c;
						bound.GetColor = getter as Func<Color4>;
						break;
				}

				bound.SetBoxed = path.Set;
				bound.GetBoxed = path.Get;
			}
			else
			{
				bound.SetBoxed = path.Set;
				bound.GetBoxed = path.Get;
			}

			// Vector and color kinds without a typed setter (nested paths): through the boxed one.
			if (bound.SetVector2 == null && storage == AnimationValueKind.Vector2) { bound.SetVector2 = v => bound.SetBoxed(v); bound.GetVector2 = () => bound.GetBoxed() is Vector2 v ? v : Vector2.Zero; }
			if (bound.SetVector3 == null && storage == AnimationValueKind.Vector3) { bound.SetVector3 = v => bound.SetBoxed(v); bound.GetVector3 = () => bound.GetBoxed() is Vector3 v ? v : Vector3.Zero; }
			if (bound.SetVector4 == null && storage == AnimationValueKind.Vector4) { bound.SetVector4 = v => bound.SetBoxed(v); bound.GetVector4 = () => bound.GetBoxed() is Vector4 v ? v : Vector4.Zero; }
			if (bound.SetQuaternion == null && storage == AnimationValueKind.Quaternion) { bound.SetQuaternion = v => bound.SetBoxed(v); bound.GetQuaternion = () => bound.GetBoxed() is Quaternion v ? v : Quaternion.Identity; }
			if (bound.SetColor == null && storage == AnimationValueKind.Color) { bound.SetColor = v => bound.SetBoxed(v); bound.GetColor = () => bound.GetBoxed() is Color4 v ? v : Color4.White; }
			return bound;
		}

		private static void CollectFields(object owner, Type type, string prefix, string displayPrefix, List<AnimatableProperty> result, int depth, HashSet<string> visited)
		{
			if (depth > 4)
			{
				return;
			}

			foreach (SerializedField field in SerializationUtility.GetFields(type))
			{
				if (field.HideInInspector)
				{
					continue;
				}

				string path = prefix + field.Name;
				string display = displayPrefix + field.DisplayName;
				AnimationValueKind? kind = AnimationBinding.KindOf(field.FieldType);
				object value = owner != null ? SafeGet(field, owner) : null;
				if (kind != null)
				{
					if (field.FieldType == typeof(Quaternion))
					{
						result.Add(new AnimatableProperty { Property = path, DisplayName = display, Kind = AnimationValueKind.Euler, ValueType = field.FieldType, CurrentValue = value });
					}

					result.Add(new AnimatableProperty { Property = path, DisplayName = display + (field.FieldType == typeof(Quaternion) ? " (quaternion)" : ""), Kind = kind.Value, ValueType = field.FieldType, CurrentValue = value });
					continue;
				}

				Type element = SerializationUtility.GetElementType(field.FieldType);
				if (element != null)
				{
					if (value is IList list)
					{
						AnimationValueKind? elementKind = AnimationBinding.KindOf(element);
						for (int i = 0; i < Math.Min(list.Count, 64); i++)
						{
							string itemPath = path + "[" + i + "]";
							string itemDisplay = display + " [" + i + "]";
							if (elementKind != null)
							{
								result.Add(new AnimatableProperty { Property = itemPath, DisplayName = itemDisplay, Kind = elementKind.Value, ValueType = element, CurrentValue = list[i] });
							}
							else if (SerializationUtility.IsObjectType(element) && list[i] != null && visited.Add(itemPath))
							{
								CollectFields(list[i], list[i].GetType(), itemPath + ".", itemDisplay + " / ", result, depth + 1, visited);
							}
						}
					}

					continue;
				}

				if (SerializationUtility.IsObjectType(field.FieldType) && !SerializationUtility.IsDictionaryType(field.FieldType))
				{
					Type nestedType = value?.GetType() ?? field.FieldType;
					if (nestedType.IsValueType || value != null)
					{
						if (visited.Add(path))
						{
							CollectFields(value, nestedType, path + ".", display + " / ", result, depth + 1, visited);
						}
					}
				}
			}
		}

		private static void CollectProperties(object target, List<AnimatableProperty> result)
		{
			var fieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (AnimatableProperty existing in result)
			{
				fieldNames.Add(existing.Property);
			}

			foreach (PropertyInfo property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
			{
				if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length > 0 || property.GetSetMethod(false) == null || property.GetGetMethod(false) == null)
				{
					continue;
				}

				if (property.DeclaringType == typeof(Behaviour) || property.DeclaringType == typeof(EngineObject) || property.DeclaringType == typeof(ScriptableObject))
				{
					continue;
				}

				if (fieldNames.Contains(property.Name) || property.IsDefined(typeof(ObsoleteAttribute), true))
				{
					continue;
				}

				AnimationValueKind? kind = AnimationBinding.KindOf(property.PropertyType);
				if (kind == null)
				{
					continue;
				}

				object value = null;
				try
				{
					value = property.GetValue(target);
				}
				catch (Exception)
				{
					// A getter that throws is not worth listing.
					continue;
				}

				if (property.PropertyType == typeof(Quaternion))
				{
					result.Add(new AnimatableProperty { Property = property.Name, DisplayName = SerializationUtility.NicifyName(property.Name) + " (property)", Kind = AnimationValueKind.Euler, ValueType = property.PropertyType, CurrentValue = value });
				}

				result.Add(new AnimatableProperty { Property = property.Name, DisplayName = SerializationUtility.NicifyName(property.Name) + " (property" + (property.PropertyType == typeof(Quaternion) ? ", quaternion)" : ")"), Kind = kind.Value, ValueType = property.PropertyType, CurrentValue = value });
			}
		}

		private static object SafeGet(SerializedField field, object owner)
		{
			try
			{
				return field.GetValue(owner);
			}
			catch (Exception)
			{
				return null;
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>A resolved property path: steps of fields, properties and list indices from the target to the value.</summary>
		internal sealed class MemberPath
		{
			public sealed class Step
			{
				public SerializedField Field;
				public PropertyInfo Property;
				public FieldInfo PlainField;
				public int ListIndex = -1;
				public Type ValueType;

				/// <summary>Depth-one properties on classes: compiled typed delegates bound to the target.</summary>
				public Delegate TypedSetter;
				public Delegate TypedGetter;

				public object Get(object owner)
				{
					if (owner == null) return null;
					if (ListIndex >= 0) return owner is IList list && ListIndex < list.Count ? list[ListIndex] : null;
					if (Field != null) return Field.GetValue(owner);
					if (Property != null) return Property.GetValue(owner);
					return PlainField?.GetValue(owner);
				}

				public void Set(object owner, object value)
				{
					if (owner == null) return;
					if (ListIndex >= 0)
					{
						if (owner is IList list && ListIndex < list.Count) list[ListIndex] = value;
						return;
					}

					if (Field != null) Field.SetValue(owner, value);
					else if (Property != null) Property.SetValue(owner, value);
					else PlainField?.SetValue(owner, value);
				}
			}

			public object Target;
			public Step[] Steps;
			public Type ValueType;

			public static MemberPath Resolve(object target, string propertyPath)
			{
				if (target == null || string.IsNullOrEmpty(propertyPath))
				{
					return null;
				}

				var steps = new List<Step>();
				Type current = target.GetType();
				object sample = target;
				int position = 0;
				while (position < propertyPath.Length)
				{
					int end = position;
					while (end < propertyPath.Length && propertyPath[end] != '.' && propertyPath[end] != '[')
					{
						end++;
					}

					string name = propertyPath.Substring(position, end - position);
					if (name.Length > 0)
					{
						Step step = ResolveMember(current, name, sample, steps.Count == 0 && !current.IsValueType);
						if (step == null)
						{
							return null;
						}

						steps.Add(step);
						current = step.ValueType;
						sample = sample != null ? SafeStep(step, sample) : null;
					}

					position = end;
					while (position < propertyPath.Length && propertyPath[position] == '[')
					{
						int close = propertyPath.IndexOf(']', position);
						if (close < 0 || !int.TryParse(propertyPath.Substring(position + 1, close - position - 1), out int index))
						{
							return null;
						}

						Type element = SerializationUtility.GetElementType(current);
						if (element == null)
						{
							return null;
						}

						steps.Add(new Step { ListIndex = index, ValueType = element });
						current = element;
						sample = sample is IList list && index < list.Count ? list[index] : null;
						position = close + 1;
					}

					if (position < propertyPath.Length && propertyPath[position] == '.')
					{
						position++;
					}
				}

				if (steps.Count == 0)
				{
					return null;
				}

				return new MemberPath { Target = target, Steps = steps.ToArray(), ValueType = current };
			}

			public object Get()
			{
				object owner = Target;
				for (int i = 0; i < Steps.Length && owner != null; i++)
				{
					owner = Steps[i].Get(owner);
				}

				return owner;
			}

			public void Set(object value)
			{
				SetAt(Target, 0, value);
			}

			private void SetAt(object owner, int index, object value)
			{
				Step step = Steps[index];
				if (index == Steps.Length - 1)
				{
					step.Set(owner, value);
					return;
				}

				object child = step.Get(owner);
				if (child == null)
				{
					return;
				}

				SetAt(child, index + 1, value);
				if (step.ValueType.IsValueType)
				{
					// A struct along the path: the boxed copy was changed, write it back.
					step.Set(owner, child);
				}
			}

			private static object SafeStep(Step step, object owner)
			{
				try
				{
					return step.Get(owner);
				}
				catch (Exception)
				{
					return null;
				}
			}

			private static Step ResolveMember(Type type, string name, object sample, bool compile)
			{
				foreach (SerializedField field in SerializationUtility.GetFields(type))
				{
					if (field.Name == name || field.Field.Name == name || field.DisplayName == name)
					{
						return new Step { Field = field, ValueType = field.FieldType };
					}
				}

				PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
				if (property != null && property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
				{
					var step = new Step { Property = property, ValueType = property.PropertyType };
					if (compile && sample != null && !type.IsValueType)
					{
						Compile(step, sample, property);
					}

					return step;
				}

				FieldInfo plain = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				return plain != null ? new Step { PlainField = plain, ValueType = plain.FieldType } : null;
			}

			private static void Compile(Step step, object target, PropertyInfo property)
			{
				try
				{
					ConstantExpression instance = Expression.Constant(target, target.GetType());
					ParameterExpression value = Expression.Parameter(property.PropertyType, "value");
					MemberExpression member = Expression.Property(instance, property);
					Type setterType = typeof(Action<>).MakeGenericType(property.PropertyType);
					Type getterType = typeof(Func<>).MakeGenericType(property.PropertyType);
					step.TypedSetter = Expression.Lambda(setterType, Expression.Assign(member, value), value).Compile();
					step.TypedGetter = Expression.Lambda(getterType, member).Compile();
				}
				catch (Exception)
				{
					step.TypedSetter = null;
					step.TypedGetter = null;
				}
			}
		}

		#endregion
	}
}
