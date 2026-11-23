using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LELEngine.Graphics;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     The graphics API the editor runs a project on: the project's own setting (Project Settings > Graphics > Graphics
	///     API), so that the editor shows what the built game shows. The device is made once per process, with the first
	///     window: a project on another API than the running editor's opens in a new editor process
	///     (<see cref="Relaunch" />).
	/// </summary>
	internal static class ProjectGraphicsApi
	{
		#region PublicMethods

		/// <summary>The API a project's saved settings ask for: OpenGL when it has none or they cannot be read.</summary>
		public static GraphicsApi Of(string projectRoot)
		{
			try
			{
				string file = ProjectInfo.Load(projectRoot).ProjectSettingsFile;
				if (!File.Exists(file))
				{
					return GraphicsApi.OpenGL;
				}

				// Read without using the settings (ProjectSettings.Load would make them the current ones).
				var settings = Documents.ReadObject(File.ReadAllBytes(file), typeof(ProjectSettings), new ProjectSettings()) as ProjectSettings;
				return settings?.Graphics?.GraphicsApi ?? GraphicsApi.OpenGL;
			}
			catch (Exception)
			{
				return GraphicsApi.OpenGL;
			}
		}

		/// <summary>
		///     The API the editor's device is to be made for: graphics=opengl|vulkan on the command line (-graphics in batch
		///     mode) wins, else the project's setting, else OpenGL.
		/// </summary>
		public static GraphicsApi Requested(string[] args, string projectRoot)
		{
			string text = CommandValue(args);
			if (text != null)
			{
				if (GraphicsDevice.TryParseApi(text, out GraphicsApi api))
				{
					return api;
				}

				Console.WriteLine($"[Editor] Unknown graphics API '{text}': the project's");
			}

			return string.IsNullOrEmpty(projectRoot) ? GraphicsApi.OpenGL : Of(projectRoot);
		}

		/// <summary>
		///     A project that wants this API cannot run in this process: its device is on another one. Not when the device
		///     is on OpenGL because this machine has no device for the API wanted (a new process would fall back again), nor
		///     when the command line chose the API.
		/// </summary>
		public static bool NeedsNewProcess(GraphicsApi wanted, string[] args)
		{
			GraphicsDevice device = GraphicsDevice.Current;
			return device != null && device.Api != wanted && GraphicsDevice.FellBackFrom != wanted && CommandValue(args) == null;
		}

		/// <summary>
		///     Starts the editor again on a project, with this process's command line otherwise (the project given on it
		///     replaced). True when the new process started.
		/// </summary>
		public static bool Relaunch(string[] args, string projectRoot)
		{
			try
			{
				var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The editor's executable is not known."))
				{
					UseShellExecute = false,
					WorkingDirectory = Environment.CurrentDirectory
				};

				foreach (string arg in WithProject(args, projectRoot))
				{
					start.ArgumentList.Add(arg);
				}

				Console.WriteLine($"[Editor] Starting the editor again for {projectRoot} ({Of(projectRoot)})");
				return Process.Start(start) != null;
			}
			catch (Exception e)
			{
				Debug.LogError("[Editor] Cannot start the editor again: " + e.Message);
				return false;
			}
		}

		/// <summary>The command line with the project instead of any project given before.</summary>
		public static string[] WithProject(string[] args, string projectRoot)
		{
			var result = new List<string>();
			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];
				if (arg.StartsWith("project=", StringComparison.OrdinalIgnoreCase) || arg.StartsWith("newproject=", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (string.Equals(arg, "-projectPath", StringComparison.OrdinalIgnoreCase))
				{
					i++;
					continue;
				}

				result.Add(arg);
			}

			result.Add("project=" + projectRoot);
			return result.ToArray();
		}

		#endregion

		#region PrivateMethods

		/// <summary>graphics=x, or -graphics x (batch mode's style).</summary>
		private static string CommandValue(string[] args)
		{
			for (int i = 0; i < args.Length; i++)
			{
				string arg = args[i];
				if (arg.StartsWith("graphics=", StringComparison.OrdinalIgnoreCase))
				{
					return arg.Substring("graphics=".Length).Trim().Trim('"');
				}

				if (string.Equals(arg, "-graphics", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
				{
					return args[i + 1];
				}
			}

			return null;
		}

		#endregion
	}
}
