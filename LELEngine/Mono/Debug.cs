using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace LELEngine
{
	public enum LogType
	{
		Log,
		Warning,
		Error,
		Exception
	}

	/// <summary>How much of the call stack a log message records (<see cref="Debug.SetStackTraceLogType" />, Unity's).</summary>
	public enum StackTraceLogType
	{
		None,

		/// <summary>The game's and the engine's frames, without the .NET runtime and third-party libraries.</summary>
		ScriptOnly,

		Full
	}

	/// <summary>
	///     Engine log. Writes to the console and raises <see cref="MessageLogged" /> so hosts (the editor console)
	///     can collect messages. Exceptions thrown by component callbacks are reported here instead of escaping
	///     the game loop. Every message records the call stack that logged it (<see cref="SetStackTraceLogType" />);
	///     the console output shows it for exceptions only.
	/// </summary>
	public static class Debug
	{
		#region PublicFields

		/// <summary>Message, stack trace (may be null), type, context object (may be null).</summary>
		public static event Action<string, string, LogType, object> MessageLogged;

		/// <summary>
		///     Where the log text is written (null: <see cref="Console.Out" />). The editor writes it past its console
		///     capture: its console gets the message from <see cref="MessageLogged" />.
		/// </summary>
		public static TextWriter Output;

		#endregion

		#region PrivateFields

		private static readonly StackTraceLogType[] stackTraceTypes = { StackTraceLogType.ScriptOnly, StackTraceLogType.ScriptOnly, StackTraceLogType.ScriptOnly, StackTraceLogType.ScriptOnly };

		/// <summary>Namespaces of the frames <see cref="StackTraceLogType.ScriptOnly" /> leaves out.</summary>
		private static readonly string[] libraryNamespaces = { "System", "Microsoft", "Internal", "OpenTK", "Hexa", "Silk", "Mono.Cecil" };

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

		/// <summary>The exception's message and stack trace, then its inner exceptions' (the innermost of a reflection call is the one reported).</summary>
		public static void LogException(Exception exception, object context = null)
		{
			Exception inner = exception is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : exception;
			string stackTrace = inner.StackTrace;
			for (Exception cause = inner.InnerException; cause != null; cause = cause.InnerException)
			{
				stackTrace += "\n--- Caused by " + cause.GetType().Name + ": " + cause.Message + "\n" + cause.StackTrace;
			}

			Write(inner.GetType().Name + ": " + inner.Message, stackTrace, LogType.Exception, context);
		}

		/// <summary>How much of the call stack messages of this type record (ScriptOnly by default, as Unity's).</summary>
		public static StackTraceLogType GetStackTraceLogType(LogType type)
		{
			return stackTraceTypes[(int)type];
		}

		public static void SetStackTraceLogType(LogType type, StackTraceLogType value)
		{
			stackTraceTypes[(int)type] = value;
		}

		#endregion

		#region PrivateMethods

		private static void Write(string message, string stackTrace, LogType type, object context)
		{
			string prefix = type == LogType.Log ? "" : "[" + type + "] ";
			(Output ?? Console.Out).WriteLine(prefix + message + (stackTrace != null ? "\n" + stackTrace : ""));
			MessageLogged?.Invoke(message, stackTrace ?? CaptureStackTrace(stackTraceTypes[(int)type]), type, context);
		}

		/// <summary>The caller's stack in the format of exception stack traces ("   at Type.Method(...) in file:line N").</summary>
		private static string CaptureStackTrace(StackTraceLogType mode)
		{
			if (mode == StackTraceLogType.None || MessageLogged == null)
			{
				return null;
			}

			var builder = new StringBuilder();
			bool caller = false;
			foreach (StackFrame frame in new StackTrace(2, true).GetFrames())
			{
				MethodBase method = frame.GetMethod();
				Type declaring = method?.DeclaringType;
				if (!caller)
				{
					// Debug's own frames (and a wrapper's call into them) are not the caller.
					if (declaring == typeof(Debug))
					{
						continue;
					}

					caller = true;
				}

				if (method == null || (mode == StackTraceLogType.ScriptOnly && IsLibrary(declaring)))
				{
					continue;
				}

				if (builder.Length > 0)
				{
					builder.Append('\n');
				}

				builder.Append("   at ");
				AppendMethod(builder, method);
				string file = frame.GetFileName();
				if (file != null)
				{
					builder.Append(" in ").Append(file).Append(":line ").Append(frame.GetFileLineNumber());
				}
			}

			return builder.Length > 0 ? builder.ToString() : null;
		}

		private static bool IsLibrary(Type type)
		{
			string space = type?.Namespace;
			if (space == null)
			{
				return false;
			}

			foreach (string library in libraryNamespaces)
			{
				if (space.StartsWith(library, StringComparison.Ordinal) && (space.Length == library.Length || space[library.Length] == '.'))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Type.Method(ParameterType name, ...) as .NET writes it in exception stack traces.</summary>
		private static void AppendMethod(StringBuilder builder, MethodBase method)
		{
			Type declaring = method.DeclaringType;
			if (declaring != null)
			{
				builder.Append(declaring.FullName?.Replace('+', '.') ?? declaring.Name).Append('.');
			}

			builder.Append(method.Name).Append('(');
			ParameterInfo[] parameters = method.GetParameters();
			for (int i = 0; i < parameters.Length; i++)
			{
				if (i > 0)
				{
					builder.Append(", ");
				}

				builder.Append(parameters[i].ParameterType.Name).Append(' ').Append(parameters[i].Name);
			}

			builder.Append(')');
		}

		#endregion
	}
}
