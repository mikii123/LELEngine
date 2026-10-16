using System;
using System.Collections.Generic;
using System.Reflection;

namespace LELEngine.UIElements
{
	/// <summary>Name of the element property a binding writes ("text", "value", "label", "style.backgroundColor", ...).</summary>
	public readonly struct BindingId : IEquatable<BindingId>
	{
		public readonly string name;

		public static readonly BindingId Invalid = new BindingId(null);

		public BindingId(string name)
		{
			this.name = name;
		}

		public static implicit operator BindingId(string name)
		{
			return new BindingId(name);
		}

		public bool Equals(BindingId other)
		{
			return string.Equals(name, other.name, StringComparison.Ordinal);
		}

		public override bool Equals(object obj)
		{
			return obj is BindingId other && Equals(other);
		}

		public override int GetHashCode()
		{
			return name != null ? name.GetHashCode() : 0;
		}

		public override string ToString()
		{
			return name ?? "";
		}
	}

	public enum BindingMode
	{
		/// <summary>Source to UI, and UI changes (ChangeEvent) back to the source.</summary>
		TwoWay,

		/// <summary>UI changes go to the source only.</summary>
		ToSource,

		/// <summary>Source to UI only.</summary>
		ToTarget,

		/// <summary>Source to UI once, then the binding is removed.</summary>
		ToTargetOnce
	}

	public enum BindingUpdateTrigger
	{
		/// <summary>
		///     When the source raises <see cref="INotifyBindablePropertyChanged.propertyChanged" /> (sources without it are
		///     polled every frame, which costs one comparison per binding).
		/// </summary>
		OnSourceChanged,

		/// <summary>Every frame.</summary>
		EveryUpdate,

		/// <summary>Only after <see cref="Binding.MarkDirty" />.</summary>
		WhenDirty
	}

	public enum BindingStatus
	{
		Pending,
		Success,
		Failure
	}

	public readonly struct BindingResult
	{
		public readonly BindingStatus status;
		public readonly string message;

		public BindingResult(BindingStatus status, string message = null)
		{
			this.status = status;
			this.message = message;
		}
	}

	/// <summary>What a binding update sees: the element, the target property and the resolved data source.</summary>
	public readonly struct BindingContext
	{
		public readonly VisualElement targetElement;
		public readonly BindingId bindingId;
		public readonly object dataSource;
		public readonly PropertyPath dataSourcePath;

		public BindingContext(VisualElement targetElement, BindingId bindingId, object dataSource, PropertyPath dataSourcePath)
		{
			this.targetElement = targetElement;
			this.bindingId = bindingId;
			this.dataSource = dataSource;
			this.dataSourcePath = dataSourcePath;
		}
	}

	/// <summary>A data source that announces property changes (bindings with OnSourceChanged update only then).</summary>
	public interface INotifyBindablePropertyChanged
	{
		event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;
	}

	public sealed class BindablePropertyChangedEventArgs : EventArgs
	{
		public BindingId propertyName { get; }

		public BindablePropertyChangedEventArgs(BindingId propertyName)
		{
			this.propertyName = propertyName;
		}
	}

	/// <summary>Marks a property as intended for binding (documentation; any public member can be bound).</summary>
	[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
	public sealed class CreatePropertyAttribute : Attribute
	{ }

	/// <summary>
	///     Base of bindings: an object attached to an element property with <see cref="VisualElement.SetBinding" />
	///     that updates it from the panel's update loop.
	/// </summary>
	public abstract class Binding
	{
		#region PublicFields

		public BindingUpdateTrigger updateTrigger { get; set; } = BindingUpdateTrigger.OnSourceChanged;

		/// <summary>The binding updates on the next frame.</summary>
		public bool isDirty { get; private set; } = true;

		/// <summary>Last update outcome (the inspector shows it).</summary>
		public BindingResult lastResult { get; internal set; }

		#endregion

		#region PublicMethods

		public void MarkDirty()
		{
			isDirty = true;
		}

		#endregion

		#region InternalMethods

		internal void ClearDirty()
		{
			isDirty = false;
		}

		/// <summary>Called when the binding starts working on an element (attached to a panel).</summary>
		protected internal virtual void OnActivated(in BindingContext context)
		{ }

		protected internal virtual void OnDeactivated(in BindingContext context)
		{ }

		/// <summary>The data source changed (dirty state is already set); bindings can re-subscribe here.</summary>
		protected internal virtual void OnDataSourceChanged(in BindingContext context)
		{ }

		/// <summary>Pushes the current source value to the element.</summary>
		protected internal abstract BindingResult Update(in BindingContext context);

		#endregion
	}

	/// <summary>A binding implemented in code: override <see cref="Update" /> to compute and apply the value.</summary>
	public abstract class CustomBinding : Binding
	{ }

	/// <summary>Named value converters for bindings (source to UI and UI to source).</summary>
	public sealed class ConverterGroup
	{
		#region PublicFields

		public string id { get; }

		#endregion

		#region PrivateFields

		private readonly Dictionary<(Type, Type), Func<object, object>> converters = new Dictionary<(Type, Type), Func<object, object>>();

		#endregion

		#region Constructors

		public ConverterGroup(string id)
		{
			this.id = id;
		}

		#endregion

		#region PublicMethods

		public void AddConverter<TSource, TDestination>(Func<TSource, TDestination> converter)
		{
			converters[(typeof(TSource), typeof(TDestination))] = value => converter((TSource)value);
		}

		/// <summary>Converts with a registered converter whose source type matches the value and destination the target.</summary>
		public bool TryConvert(object value, Type destination, out object result)
		{
			result = value;
			if (value == null)
			{
				return false;
			}

			Type sourceType = value.GetType();
			foreach (KeyValuePair<(Type, Type), Func<object, object>> pair in converters)
			{
				if (pair.Key.Item1.IsAssignableFrom(sourceType) && destination.IsAssignableFrom(pair.Key.Item2))
				{
					result = pair.Value(value);
					return true;
				}
			}

			return false;
		}

		#endregion
	}

	/// <summary>Converter groups by id (UXML bindings reference them by name).</summary>
	public static class ConverterGroups
	{
		#region PrivateFields

		private static readonly Dictionary<string, ConverterGroup> groups = new Dictionary<string, ConverterGroup>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		public static void RegisterConverterGroup(ConverterGroup group)
		{
			if (group != null && !string.IsNullOrEmpty(group.id))
			{
				groups[group.id] = group;
			}
		}

		public static ConverterGroup GetConverterGroup(string id)
		{
			ConverterGroup group;
			return id != null && groups.TryGetValue(id, out group) ? group : null;
		}

		#endregion
	}

	/// <summary>
	///     Binds an element property to a member of a data source: the value flows from the source (resolved from
	///     the binding, the element or an ancestor's <see cref="VisualElement.dataSource" />) to the property, and
	///     with TwoWay / ToSource modes the element's ChangeEvent writes it back.
	/// </summary>
	public sealed class DataBinding : Binding
	{
		#region PublicFields

		/// <summary>Source of this binding; null uses the element's (or an ancestor's) data source.</summary>
		public object dataSource
		{
			get => m_DataSource;
			set
			{
				if (!ReferenceEquals(m_DataSource, value))
				{
					m_DataSource = value;
					MarkDirty();
				}
			}
		}

		/// <summary>Member path relative to the data source (appended to the elements' paths).</summary>
		public PropertyPath dataSourcePath
		{
			get => m_DataSourcePath;
			set
			{
				m_DataSourcePath = value;
				MarkDirty();
			}
		}

		public BindingMode bindingMode
		{
			get => m_BindingMode;
			set
			{
				m_BindingMode = value;
				MarkDirty();
			}
		}

		/// <summary>Source type hint for the UI Builder (unused at runtime).</summary>
		public Type dataSourceType { get; set; }

		public ConverterGroup sourceToUiConverters { get; set; }
		public ConverterGroup uiToSourceConverters { get; set; }

		#endregion

		#region PrivateFields

		private object m_DataSource;
		private PropertyPath m_DataSourcePath;
		private BindingMode m_BindingMode = BindingMode.TwoWay;
		private object lastSourceValue;
		private bool hasLastValue;
		private INotifyBindablePropertyChanged subscribedSource;
		private string subscribedMember;
		private Delegate changeCallback;
		private VisualElement changeTarget;
		private bool pushingToSource;

		#endregion

		#region PublicMethods

		public static DataBinding Create(PropertyPath dataSourcePath, BindingMode mode = BindingMode.TwoWay)
		{
			return new DataBinding { dataSourcePath = dataSourcePath, bindingMode = mode };
		}

		#endregion

		#region ProtectedMethods

		protected internal override void OnActivated(in BindingContext context)
		{
			RegisterChangeCallback(context.targetElement, context.bindingId);
			Subscribe(context);
		}

		protected internal override void OnDeactivated(in BindingContext context)
		{
			UnregisterChangeCallback();
			Unsubscribe();
			hasLastValue = false;
		}

		protected internal override void OnDataSourceChanged(in BindingContext context)
		{
			hasLastValue = false;
			Subscribe(context);
		}

		protected internal override BindingResult Update(in BindingContext context)
		{
			if (m_BindingMode == BindingMode.ToSource)
			{
				return new BindingResult(BindingStatus.Success);
			}

			object source = m_DataSource ?? context.dataSource;
			if (source == null)
			{
				return new BindingResult(BindingStatus.Pending, "no data source");
			}

			PropertyPath path = PropertyPath.Combine(m_DataSource != null ? default : context.dataSourcePath, m_DataSourcePath);
			object value;
			Type valueType;
			if (!PropertyPathAccess.TryGet(source, path, out value, out valueType))
			{
				return new BindingResult(BindingStatus.Failure, "'" + path + "' not found on " + source.GetType().Name);
			}

			if (hasLastValue && Equals(value, lastSourceValue) && subscribedSource != null)
			{
				return new BindingResult(BindingStatus.Success);
			}

			if (hasLastValue && Equals(value, lastSourceValue))
			{
				return new BindingResult(BindingStatus.Success);
			}

			lastSourceValue = value;
			hasLastValue = true;

			VisualElement element = context.targetElement;
			Type targetType = PropertyPathAccess.GetValueType(element, context.bindingId.name);
			if (targetType == null)
			{
				return new BindingResult(BindingStatus.Failure, "'" + context.bindingId + "' not found on " + element.typeName);
			}

			object converted;
			if (sourceToUiConverters == null || !sourceToUiConverters.TryConvert(value, targetType, out converted))
			{
				if (!BindingConversion.TryConvert(value, targetType, out converted))
				{
					return new BindingResult(BindingStatus.Failure, "cannot convert " + (value?.GetType().Name ?? "null") + " to " + targetType.Name);
				}
			}

			// Skip the write when the element already shows the value (no change events, no repaint).
			object current;
			Type ignored;
			if (PropertyPathAccess.TryGet(element, context.bindingId.name, out current, out ignored) && Equals(current, converted))
			{
				return new BindingResult(BindingStatus.Success);
			}

			pushingToSource = true;
			try
			{
				if (!SetWithoutNotify(element, context.bindingId.name, converted) && !PropertyPathAccess.TrySet(element, context.bindingId.name, converted))
				{
					return new BindingResult(BindingStatus.Failure, "'" + context.bindingId + "' is read-only");
				}
			}
			finally
			{
				pushingToSource = false;
			}

			return new BindingResult(BindingStatus.Success);
		}

		#endregion

		#region PrivateMethods

		/// <summary>"value" on a field goes through SetValueWithoutNotify so the write does not echo back to the source.</summary>
		private static bool SetWithoutNotify(VisualElement element, string property, object value)
		{
			if (property != "value")
			{
				return false;
			}

			foreach (Type i in element.GetType().GetInterfaces())
			{
				if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>))
				{
					Type valueType = i.GetGenericArguments()[0];
					object converted;
					if (!BindingConversion.TryConvert(value, valueType, out converted))
					{
						return false;
					}

					MethodInfo method = i.GetMethod("SetValueWithoutNotify");
					method?.Invoke(element, new[] { converted });
					return method != null;
				}
			}

			return false;
		}

		private void Subscribe(in BindingContext context)
		{
			Unsubscribe();
			if (updateTrigger != BindingUpdateTrigger.OnSourceChanged)
			{
				return;
			}

			object source = m_DataSource ?? context.dataSource;
			PropertyPath path = PropertyPath.Combine(m_DataSource != null ? default : context.dataSourcePath, m_DataSourcePath);
			// Subscribe to the object holding the last member of the path.
			string fullPath = path.path ?? "";
			int lastDot = fullPath.LastIndexOf('.');
			object holder = source;
			string member = fullPath;
			if (lastDot >= 0)
			{
				object parent;
				Type ignored;
				if (PropertyPathAccess.TryGet(source, fullPath.Substring(0, lastDot), out parent, out ignored))
				{
					holder = parent;
				}

				member = fullPath.Substring(lastDot + 1);
			}

			if (holder is INotifyBindablePropertyChanged notifying)
			{
				subscribedSource = notifying;
				subscribedMember = member;
				notifying.propertyChanged += OnSourcePropertyChanged;
			}
		}

		private void Unsubscribe()
		{
			if (subscribedSource != null)
			{
				subscribedSource.propertyChanged -= OnSourcePropertyChanged;
				subscribedSource = null;
			}
		}

		private void OnSourcePropertyChanged(object sender, BindablePropertyChangedEventArgs e)
		{
			if (string.IsNullOrEmpty(e.propertyName.name) || string.Equals(e.propertyName.name, subscribedMember, StringComparison.Ordinal))
			{
				MarkDirty();
			}
		}

		private void RegisterChangeCallback(VisualElement element, BindingId id)
		{
			UnregisterChangeCallback();
			if (m_BindingMode == BindingMode.ToTarget || m_BindingMode == BindingMode.ToTargetOnce || id.name != "value")
			{
				return;
			}

			foreach (Type i in element.GetType().GetInterfaces())
			{
				if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(INotifyValueChanged<>))
				{
					Type valueType = i.GetGenericArguments()[0];
					MethodInfo register = typeof(DataBinding).GetMethod(nameof(RegisterTyped), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(valueType);
					register.Invoke(this, new object[] { element });
					changeTarget = element;
					return;
				}
			}
		}

		private void RegisterTyped<T>(VisualElement element)
		{
			EventCallback<ChangeEvent<T>> callback = evt => OnUiChanged(evt.newValue);
			changeCallback = callback;
			element.RegisterCallback(callback);
		}

		private void UnregisterChangeCallback()
		{
			if (changeTarget == null || changeCallback == null)
			{
				return;
			}

			Type valueType = changeCallback.GetType().GetGenericArguments()[0].GetGenericArguments()[0];
			MethodInfo unregister = typeof(DataBinding).GetMethod(nameof(UnregisterTyped), BindingFlags.Instance | BindingFlags.NonPublic).MakeGenericMethod(valueType);
			unregister.Invoke(this, new object[] { changeTarget });
			changeTarget = null;
			changeCallback = null;
		}

		private void UnregisterTyped<T>(VisualElement element)
		{
			element.UnregisterCallback((EventCallback<ChangeEvent<T>>)changeCallback);
		}

		private void OnUiChanged(object newValue)
		{
			if (pushingToSource || changeTarget == null)
			{
				return;
			}

			PropertyPath elementPath;
			object resolved = changeTarget.ResolveDataSource(out elementPath);
			object source = m_DataSource ?? resolved;
			if (source == null)
			{
				return;
			}

			PropertyPath path = PropertyPath.Combine(m_DataSource != null ? default : elementPath, m_DataSourcePath);
			object converted = newValue;
			Type sourceType = PropertyPathAccess.GetValueType(source, path);
			if (uiToSourceConverters != null && sourceType != null)
			{
				uiToSourceConverters.TryConvert(newValue, sourceType, out converted);
			}

			if (PropertyPathAccess.TrySet(source, path, converted))
			{
				lastSourceValue = newValue;
				hasLastValue = true;
				Type ignored;
				object stored;
				if (PropertyPathAccess.TryGet(source, path, out stored, out ignored))
				{
					lastSourceValue = stored;
				}
			}
			else
			{
				lastResult = new BindingResult(BindingStatus.Failure, "cannot write '" + path + "'");
			}
		}

		#endregion
	}
}
