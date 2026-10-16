using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>A binding attached to an element property.</summary>
	public readonly struct BindingInfo
	{
		public readonly VisualElement targetElement;
		public readonly BindingId bindingId;
		public readonly Binding binding;

		public BindingInfo(VisualElement targetElement, BindingId bindingId, Binding binding)
		{
			this.targetElement = targetElement;
			this.bindingId = bindingId;
			this.binding = binding;
		}
	}

	public partial class VisualElement
	{
		#region PublicFields

		/// <summary>
		///     Object the element's (and its descendants') bindings read from. Descendants inherit the nearest
		///     ancestor's data source; their <see cref="dataSourcePath" /> values chain onto it.
		/// </summary>
		public object dataSource
		{
			get => m_DataSource;
			set
			{
				if (ReferenceEquals(m_DataSource, value))
				{
					return;
				}

				m_DataSource = value;
				NotifyDataSourceChanged();
			}
		}

		/// <summary>Path relative to the data source in effect for this element (chains below ancestors).</summary>
		public PropertyPath dataSourcePath
		{
			get => m_DataSourcePath;
			set
			{
				if (m_DataSourcePath.Equals(value))
				{
					return;
				}

				m_DataSourcePath = value;
				NotifyDataSourceChanged();
			}
		}

		/// <summary>Type hint of the data source for tools (UXML data-source-type).</summary>
		public string dataSourceTypeName { get; set; }

		#endregion

		#region PrivateFields

		private object m_DataSource;
		private PropertyPath m_DataSourcePath;
		internal List<BindingInfo> bindings;
		private bool bindingsActive;

		#endregion

		#region PublicMethods

		/// <summary>Binds the property named by <paramref name="bindingId" /> to the binding (replacing any previous one).</summary>
		public void SetBinding(BindingId bindingId, Binding binding)
		{
			if (binding == null)
			{
				throw new ArgumentNullException(nameof(binding));
			}

			ClearBinding(bindingId);
			bindings ??= new List<BindingInfo>(2);
			bindings.Add(new BindingInfo(this, bindingId, binding));
			binding.MarkDirty();
			if (elementPanel != null)
			{
				elementPanel.RegisterBindingTarget(this);
				ActivateBinding(bindingId, binding);
			}
		}

		public bool TryGetBinding(BindingId bindingId, out Binding binding)
		{
			if (bindings != null)
			{
				foreach (BindingInfo info in bindings)
				{
					if (info.bindingId.Equals(bindingId))
					{
						binding = info.binding;
						return true;
					}
				}
			}

			binding = null;
			return false;
		}

		public Binding GetBinding(BindingId bindingId)
		{
			Binding binding;
			return TryGetBinding(bindingId, out binding) ? binding : null;
		}

		public bool HasBinding(BindingId bindingId)
		{
			return TryGetBinding(bindingId, out _);
		}

		public void ClearBinding(BindingId bindingId)
		{
			if (bindings == null)
			{
				return;
			}

			for (int i = 0; i < bindings.Count; i++)
			{
				if (bindings[i].bindingId.Equals(bindingId))
				{
					BindingInfo info = bindings[i];
					bindings.RemoveAt(i);
					if (bindingsActive)
					{
						DeactivateBinding(info.bindingId, info.binding);
					}

					if (bindings.Count == 0)
					{
						elementPanel?.UnregisterBindingTarget(this);
					}

					return;
				}
			}
		}

		public void ClearBindings()
		{
			if (bindings == null)
			{
				return;
			}

			foreach (BindingInfo info in bindings.ToArray())
			{
				ClearBinding(info.bindingId);
			}
		}

		public IEnumerable<BindingInfo> GetBindingInfos()
		{
			return bindings ?? (IEnumerable<BindingInfo>)Array.Empty<BindingInfo>();
		}

		/// <summary>The data source in effect for this element and the chained path leading to this element's data.</summary>
		public object ResolveDataSource(out PropertyPath path)
		{
			path = default;
			var chain = new List<PropertyPath>();
			for (VisualElement e = this; e != null; e = e.m_PhysicalParent)
			{
				chain.Add(e.m_DataSourcePath);
				if (e.m_DataSource != null)
				{
					for (int i = chain.Count - 1; i >= 0; i--)
					{
						path = PropertyPath.Combine(path, chain[i]);
					}

					return e.m_DataSource;
				}
			}

			return null;
		}

		#endregion

		#region InternalMethods

		internal void ActivateBindings()
		{
			if (bindings == null || bindingsActive)
			{
				return;
			}

			bindingsActive = true;
			foreach (BindingInfo info in bindings.ToArray())
			{
				ActivateBinding(info.bindingId, info.binding);
			}
		}

		internal void DeactivateBindings()
		{
			if (bindings == null || !bindingsActive)
			{
				return;
			}

			bindingsActive = false;
			foreach (BindingInfo info in bindings.ToArray())
			{
				DeactivateBinding(info.bindingId, info.binding);
			}
		}

		/// <summary>Runs the due bindings; returns false when none is left.</summary>
		internal bool UpdateBindings()
		{
			if (bindings == null || bindings.Count == 0)
			{
				return false;
			}

			PropertyPath path;
			object source = ResolveDataSource(out path);
			for (int i = bindings.Count - 1; i >= 0; i--)
			{
				BindingInfo info = bindings[i];
				Binding binding = info.binding;
				bool due = binding.isDirty || binding.updateTrigger == BindingUpdateTrigger.EveryUpdate;
				if (!due && binding.updateTrigger == BindingUpdateTrigger.OnSourceChanged && binding is DataBinding dataBinding && !(((dataBinding.dataSource ?? source) is INotifyBindablePropertyChanged)))
				{
					// Plain objects cannot notify: poll (the binding skips the write when the value is unchanged).
					due = true;
				}

				if (!due)
				{
					continue;
				}

				var context = new BindingContext(this, info.bindingId, source, path);
				BindingResult result;
				try
				{
					result = binding.Update(context);
				}
				catch (Exception e)
				{
					result = new BindingResult(BindingStatus.Failure, e.Message);
					Debug.LogException(e, this);
				}

				binding.lastResult = result;
				if (result.status != BindingStatus.Pending)
				{
					binding.ClearDirty();
				}

				if (result.status == BindingStatus.Success && binding is DataBinding once && once.bindingMode == BindingMode.ToTargetOnce)
				{
					ClearBinding(info.bindingId);
				}
			}

			return bindings.Count > 0;
		}

		#endregion

		#region PrivateMethods

		private void ActivateBinding(BindingId id, Binding binding)
		{
			if (!bindingsActive)
			{
				bindingsActive = true;
			}

			PropertyPath path;
			object source = ResolveDataSource(out path);
			try
			{
				binding.OnActivated(new BindingContext(this, id, source, path));
			}
			catch (Exception e)
			{
				Debug.LogException(e, this);
			}
		}

		private void DeactivateBinding(BindingId id, Binding binding)
		{
			PropertyPath path;
			object source = ResolveDataSource(out path);
			try
			{
				binding.OnDeactivated(new BindingContext(this, id, source, path));
			}
			catch (Exception e)
			{
				Debug.LogException(e, this);
			}
		}

		/// <summary>The data source of this subtree changed: every binding below re-resolves on its next update.</summary>
		private void NotifyDataSourceChanged()
		{
			MarkBindingsForNewSource(this);
		}

		private static void MarkBindingsForNewSource(VisualElement element)
		{
			if (element.bindings != null)
			{
				PropertyPath path;
				object source = element.ResolveDataSource(out path);
				foreach (BindingInfo info in element.bindings)
				{
					info.binding.MarkDirty();
					if (element.bindingsActive)
					{
						info.binding.OnDataSourceChanged(new BindingContext(element, info.bindingId, source, path));
					}
				}
			}

			List<VisualElement> children = element.m_Children;
			if (children == null)
			{
				return;
			}

			for (int i = 0; i < children.Count; i++)
			{
				if (children[i].m_DataSource == null)
				{
					MarkBindingsForNewSource(children[i]);
				}
			}
		}

		#endregion
	}
}
