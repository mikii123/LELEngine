namespace LELEngine.UIElements
{
	/// <summary>A control with a value that raises <see cref="ChangeEvent{T}" /> when it changes.</summary>
	public interface INotifyValueChanged<T>
	{
		T value { get; set; }

		/// <summary>Sets the value without sending a change event (programmatic updates).</summary>
		void SetValueWithoutNotify(T newValue);
	}

	public static class INotifyValueChangedExtensions
	{
		/// <summary>Registers a callback for the control's ChangeEvent.</summary>
		public static bool RegisterValueChangedCallback<T>(this INotifyValueChanged<T> control, EventCallback<ChangeEvent<T>> callback)
		{
			if (control is CallbackEventHandler handler)
			{
				handler.RegisterCallback(callback);
				return true;
			}

			return false;
		}

		public static bool UnregisterValueChangedCallback<T>(this INotifyValueChanged<T> control, EventCallback<ChangeEvent<T>> callback)
		{
			if (control is CallbackEventHandler handler)
			{
				handler.UnregisterCallback(callback);
				return true;
			}

			return false;
		}
	}
}
