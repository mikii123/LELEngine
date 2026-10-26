using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LELEngine.Editor.Automation;

namespace LELEngine.Editor
{
	/// <summary>
	///     Batch mode with the editor itself (-executeMethod, -listen): the editor opens the project as it does with its
	///     window (scripts with the editor scripts' windows, menus and inspectors, the last or -scene scene, selection, undo),
	///     but draws no UI: a hidden window holds the GL context, and the editor only ticks (update, the script windows'
	///     Update, deferred actions, automation). A window is drawn only when captured (<see cref="IEditorPanel.GetScreenshot" />).
	///     <para>
	///         Order: the project opens and its scripts build (the run fails on errors), -scene opens, -executeMethod runs
	///         inside the editor (awaits continue tick by tick; its result is the exit code), then -listen serves automation
	///         commands until "quit". The caller builds the game afterwards (-build).
	///     </para>
	/// </summary>
	internal static class BatchEditor
	{
		#region PrivateFields

		/// <summary>Ticks per second while nothing is waiting (play mode and waits run at this rate too).</summary>
		private const double TickRate = 60.0;

		#endregion

		#region PublicMethods

		/// <param name="build">Builds the game with the open editor's project (null: no build); false when it failed.</param>
		public static int Run(CommandLine options, string[] args, string projectPath, Func<ProjectInfo, bool> build)
		{
			// The editor's arguments: the project, and "automated" (focus=0): defaults for preferences and window states,
			// nothing of the user's saved.
			var editorArgs = new List<string>(args) { "project=" + projectPath, "focus=0" };
			string[] all = editorArgs.ToArray();
			EditorPreferences.Initialize(all);
			ImGuiController.ReadOnlyLayouts = true;
			HierarchyExpansion.Initialize(all);
			Window.StartFocused = false;

			using MainWindow window = MainWindow.CreateWithoutUI(all);
			EditorApplication editor = window.Editor;
			try
			{
				if (editor.Project == null)
				{
					Debug.LogError("[Batch] " + (editor.OpenError ?? "The project did not open."));
					return 1;
				}

				var clock = Stopwatch.StartNew();
				double last = 0;
				void Tick()
				{
					double now = clock.Elapsed.TotalSeconds;
					double delta = Math.Max(1.0 / 1000, now - last);
					last = now;
					window.TickWithoutUI(delta);
					double spare = 1.0 / TickRate - (clock.Elapsed.TotalSeconds - now);
					if (spare > 0.001)
					{
						Thread.Sleep(TimeSpan.FromSeconds(spare));
					}
				}

				// The scripts as they are now: built (or the built ones loaded) before anything runs.
				if (!TickUntil(Tick, () => !editor.IsBusy, 900))
				{
					Debug.LogError("[Batch] The editor is still busy after 15 minutes (scripts, assets).");
					return 1;
				}

				if (editor.Scripts != null && editor.Scripts.HasErrors)
				{
					Debug.LogError("[Batch] The scripts do not compile (see the errors above).");
					return 1;
				}

				string scene = options.Get("scene");
				if (scene != null)
				{
					if (!System.IO.File.Exists(AssetDatabase.ToAbsolute(scene)))
					{
						Debug.LogError("[Batch] Scene not found: " + scene);
						return 1;
					}

					editor.OpenScene(scene);
					TickUntil(Tick, () => !editor.IsBusy, 300);
				}

				bool listen = options.Has("listen");
				EditorAutomation.Start(editor, args, listen);
				int code = 0;
				string method = options.Get("executemethod");
				if (method != null)
				{
					Task<int> run = EditorAutomation.ExecuteMethod(method, args);
					TickUntil(Tick, () => run.IsCompleted, double.MaxValue);
					if (run.IsFaulted)
					{
						Debug.LogException(run.Exception?.InnerException ?? run.Exception);
						code = 1;
					}
					else
					{
						code = run.Result;
						if (code != 0)
						{
							Debug.LogError($"[Batch] {method} returned {code}");
						}
					}
				}

				if (listen && code == 0)
				{
					Console.WriteLine($"[Batch] Serving automation commands on {EditorAutomation.ServerName} until 'quit' (LELEngine.Editor.Console -command help)");
					TickUntil(Tick, () => EditorAutomation.QuitRequested, double.MaxValue);
				}

				if (code == 0 && build != null && !build(editor.Project))
				{
					code = 1;
				}

				return code;
			}
			finally
			{
				EditorAutomation.Shutdown();
				editor?.DiscardChangesOnClose();
				editor?.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		private static bool TickUntil(Action tick, Func<bool> done, double timeoutSeconds)
		{
			var clock = Stopwatch.StartNew();
			// A few ticks first: the file watchers and the first build request need them.
			for (int i = 0; i < 3; i++)
			{
				tick();
			}

			while (!done())
			{
				if (clock.Elapsed.TotalSeconds > timeoutSeconds)
				{
					return false;
				}

				tick();
			}

			return true;
		}

		#endregion
	}
}
