using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LELEngine.Editor
{
	internal static class Program
	{
		#region PrivateMethods

		[STAThread]
		private static int Main(string[] args)
		{
			CommandLine options = CommandLine.Parse(args);
			bool help = options.Has("help") || options.Has("h");

			// -batchmode: no window, no UI, a console (build machines, scripts).
			if (help || options.Has("batchmode"))
			{
				bool ownConsole = ConsoleHost.Ensure();
				int code = help ? ShowHelp() : BatchMode.Run(args);
				ConsoleHost.Release(ownConsole);
				return code;
			}

			using (EditorWindow window = EditorWindow.Create(args))
			{
				window.Run();
			}

			return 0;
		}

		private static int ShowHelp()
		{
			Console.WriteLine(BatchMode.Usage);
			return 0;
		}

		#endregion
	}

	/// <summary>
	///     Console of batch mode. The editor is a window application (no console when it shows its UI); in batch mode it
	///     writes to the terminal it was started from, or to a console window of its own when there is none (started
	///     from Explorer). Redirected output (build machines) and the console twin LELEngine.Editor.Console.exe need
	///     nothing.
	/// </summary>
	internal static class ConsoleHost
	{
		#region PrivateFields

		private const int AttachParentProcess = -1;
		private const int StdOutputHandle = -11;
		private const uint FileTypeUnknown = 0;

		#endregion

		#region PublicMethods

		/// <summary>Makes the console output visible; true when a console window was created for this process.</summary>
		public static bool Ensure()
		{
			if (!OperatingSystem.IsWindows() || GetConsoleWindow() != IntPtr.Zero || IsOutputRedirected())
			{
				return false;
			}

			if (AttachConsole(AttachParentProcess))
			{
				ReopenStreams();
				// The calling shell printed its prompt already: start on a fresh line.
				Console.WriteLine();
				return false;
			}

			if (AllocConsole())
			{
				ReopenStreams();
				return true;
			}

			return false;
		}

		/// <summary>A console window of its own would close with the process: keep it until a key is pressed.</summary>
		public static void Release(bool ownConsole)
		{
			if (!ownConsole)
			{
				return;
			}

			Console.WriteLine("Press any key to close.");
			try
			{
				Console.ReadKey(true);
			}
			catch (InvalidOperationException)
			{
				// No keyboard input available.
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsOutputRedirected()
		{
			IntPtr handle = GetStdHandle(StdOutputHandle);
			return handle != IntPtr.Zero && handle != new IntPtr(-1) && GetFileType(handle) != FileTypeUnknown;
		}

		private static void ReopenStreams()
		{
			var output = new StreamWriter(new FileStream("CONOUT$", FileMode.Open, FileAccess.Write)) { AutoFlush = true };
			Console.SetOut(output);
			Console.SetError(output);
			Console.SetIn(new StreamReader(new FileStream("CONIN$", FileMode.Open, FileAccess.Read)));
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AttachConsole(int processId);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool AllocConsole();

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetConsoleWindow();

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetStdHandle(int handle);

		[DllImport("kernel32.dll")]
		private static extern uint GetFileType(IntPtr handle);

		#endregion
	}
}
