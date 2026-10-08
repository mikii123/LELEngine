using System;

namespace LELEngine
{
	public enum LogType
	{
		Log,
		Warning,
		Error,
		Exception
	}

	/// <summary>
	///     Engine log. Writes to the console and raises <see cref="MessageLogged" /> so hosts (the editor console)
	///     can collect messages. Exceptions thrown by component callbacks are reported here instead of escaping
	///     the game loop.
	/// </summary>
	public static class Debug
	{
		#region PublicFields

		/// <summary>Message, stack trace (may be null), type, context object (may be null).</summary>
		public static event Action<string, string, LogType, object> MessageLogged;

		#endregion

		#region PublicMethods

		public static void Log(object message, object context = null)
		{
			Write(message?.ToString(), null, LogType.Log, context);
		}

		public static void LogWarning(object message, object context = null)
		{
			Write(message?.ToString(), null, LogType.Warning, context);
		}

		public static void LogError(object message, object context = null)
		{
			Write(message?.ToString(), null, LogType.Error, context);
		}

		public static void LogException(Exception exception, object context = null)
		{
			Exception inner = exception is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : exception;
			Write(inner.GetType().Name + ": " + inner.Message, inner.StackTrace, LogType.Exception, context);
		}

		#endregion

		#region PrivateMethods

		private static void Write(string message, string stackTrace, LogType type, object context)
		{
			string prefix = type == LogType.Log ? "" : "[" + type + "] ";
			Console.WriteLine(prefix + message + (stackTrace != null ? "\n" + stackTrace : ""));
			MessageLogged?.Invoke(message, stackTrace, type, context);
		}

		#endregion
	}
}
