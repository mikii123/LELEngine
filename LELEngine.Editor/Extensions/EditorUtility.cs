namespace LELEngine.Editor
{
	/// <summary>
	///     Unity's progress bar for editor scripts: a long task shows its progress over the editor's window (the bar moves while
	///     the script blocks the editor). Clear it when the task ends, also when it fails (a finally). The engine's own tasks
	///     use <see cref="Progress" />, which editor scripts may use too (nested tasks, a using block).
	/// </summary>
	public static class EditorUtility
	{
		#region PrivateFields

		private static Progress.Scope scope;
		private static string scopeTitle;
		private static bool scopeCancelable;

		#endregion

		#region PublicMethods

		/// <summary>Shows (or updates) the bar: <paramref name="title" /> above it, <paramref name="info" /> below, <paramref name="progress" /> 0..1.</summary>
		public static void DisplayProgressBar(string title, string info, float progress)
		{
			Show(title, info, progress, false);
		}

		/// <summary>The bar with a Cancel button; true once it was clicked (stop the task and call <see cref="ClearProgressBar" />).</summary>
		public static bool DisplayCancelableProgressBar(string title, string info, float progress)
		{
			Show(title, info, progress, true);
			return scope.Canceled;
		}

		/// <summary>Hides the bar.</summary>
		public static void ClearProgressBar()
		{
			scope?.Dispose();
			scope = null;
			scopeTitle = null;
		}

		#endregion

		#region PrivateMethods

		private static void Show(string title, string info, float progress, bool cancelable)
		{
			if (scope == null || scopeTitle != title || scopeCancelable != cancelable)
			{
				scope?.Dispose();
				scope = Progress.Begin(title, cancelable);
				scopeTitle = title;
				scopeCancelable = cancelable;
			}

			scope.Report(progress, info);
		}

		#endregion
	}
}
