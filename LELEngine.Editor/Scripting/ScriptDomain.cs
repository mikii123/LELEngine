using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     The game scripts loaded into the editor. Edits to Assets/**/*.cs start a "dotnet build" in the background;
	///     the editor keeps working with the previous scripts meanwhile. A successful build is applied as a full
	///     reload (never a hot patch): the scene is saved to memory and unloaded, every editor cache holding script
	///     types is dropped, the collectible load context of the old assembly is unloaded, the new assembly is loaded
	///     into a fresh context and the scene is loaded back from its saved state. Compile errors keep the old
	///     scripts running. That the old context really went away is checked in the background; when something still
	///     references it, <see cref="LeakTracer" /> finds the paths from static fields to it on the main thread and a
	///     warning lists them.
	///     Scripts in Editor folders build into Game.Editor.dll (custom inspectors, editor windows, menu items), loaded
	///     into the same context after Game.dll and handed to <see cref="EditorExtensions" />.
	/// </summary>
	internal sealed class ScriptDomain : IDisposable
	{
		#region PublicFields

		/// <summary>Bumped on every domain load (caches keyed by script types compare it).</summary>
		public int Version { get; private set; }

		public bool HasErrors { get; private set; }
		public bool IsBuilding => buildTask != null;
		public string StatusText { get; private set; } = "";
		public Assembly GameAssembly { get; private set; }

		/// <summary>The editor scripts (null when the project has none or they did not load).</summary>
		public Assembly EditorAssembly { get; private set; }

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly ProjectInfo project;
		private readonly string projectFile;
		private readonly string outputFolder;
		private FileSystemWatcher watcher;
		private volatile bool sourcesChanged;
		private double changedAt = double.MinValue;
		private Task<BuildResult> buildTask;

		// The scripts the running build compiles (SourceStamp when it started) and whether it builds the editor scripts.
		private string buildSources;
		private bool buildIncludesEditor;
		private BuildResult pendingReload;
		private GameLoadContext context;
		private byte[] assemblyBytes;
		private byte[] symbolBytes;
		private byte[] editorAssemblyBytes;
		private byte[] editorSymbolBytes;
		private readonly System.Collections.Generic.List<(WeakReference, string)> trackedComponents = new System.Collections.Generic.List<(WeakReference, string)>();

		// Contexts that outlived their unload check (weak), with the scene components still alive: traced in Update.
		private readonly System.Collections.Concurrent.ConcurrentQueue<(WeakReference context, System.Collections.Generic.List<string> components)> survivors =
			new System.Collections.Concurrent.ConcurrentQueue<(WeakReference, System.Collections.Generic.List<string>)>();

		private volatile bool disposed;

		#endregion

		#region Constructors

		public ScriptDomain(EditorApplication editor, ProjectInfo project)
		{
			this.editor = editor;
			this.project = project;
			projectFile = ProjectGenerator.ProjectFile(project);
			outputFolder = Path.Combine(project.LibraryPath, "ScriptAssemblies");
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Writes the IDE project, loads the last built scripts (instant start) and builds in the background when
		///     sources are newer.
		/// </summary>
		public void Initialize()
		{
			ProjectGenerator.Generate(project);

			watcher = new FileSystemWatcher(project.AssetsPath, "*.cs") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
			watcher.Changed += (s, e) => OnSourcesChanged();
			watcher.Created += (s, e) => OnSourcesChanged();
			watcher.Deleted += (s, e) => OnSourcesChanged();
			watcher.Renamed += (s, e) => OnSourcesChanged();
			watcher.EnableRaisingEvents = true;

			string dll = AssemblyFile(project);
			bool editorScripts = HasEditorScripts(project);
			if (File.Exists(dll))
			{
				// Scripts built before the weaver existed (or by an IDE): rewritten before they load.
				var woven = new BuildResult();
				if (!ScriptBuilder.Weave(dll, outputFolder, woven) || (editorScripts && File.Exists(EditorAssemblyFile(project)) && !ScriptBuilder.Weave(EditorAssemblyFile(project), outputFolder, woven)))
				{
					Debug.LogWarning("[Scripts] " + woven.Diagnostics[0].Message);
				}

				assemblyBytes = File.ReadAllBytes(dll);
				symbolBytes = ReadSymbols(dll);
				string editorDll = EditorAssemblyFile(project);
				if (editorScripts && File.Exists(editorDll))
				{
					editorAssemblyBytes = File.ReadAllBytes(editorDll);
					editorSymbolBytes = ReadSymbols(editorDll);
				}

				LoadDomain();
				if (!IsAssemblyCurrent(project, editorScripts))
				{
					RequestBuild();
				}
			}
			else if (HasScripts(project))
			{
				RequestBuild();
			}
		}

		/// <summary>The editor build of the scripts (Library/ScriptAssemblies/Game.dll).</summary>
		public static string AssemblyFile(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, "ScriptAssemblies", ProjectGenerator.AssemblyName + ".dll");
		}

		/// <summary>The editor scripts (Library/ScriptAssemblies/Game.Editor.dll).</summary>
		public static string EditorAssemblyFile(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, "ScriptAssemblies", ProjectGenerator.EditorAssemblyName + ".dll");
		}

		public static bool HasScripts(ProjectInfo project)
		{
			return Directory.Exists(project.AssetsPath) && Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).Any();
		}

		/// <summary>Some script lies in an Editor folder.</summary>
		public static bool HasEditorScripts(ProjectInfo project)
		{
			if (!Directory.Exists(project.AssetsPath))
			{
				return false;
			}

			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories))
			{
				if (ProjectGenerator.IsEditorScript(project, file))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     The editor build exists, was built from the scripts as they are now (<see cref="SourceStamp" />) and is newer
		///     than the generated project (with <paramref name="includeEditor" />: Game.Editor.dll too).
		/// </summary>
		public static bool IsAssemblyCurrent(ProjectInfo project, bool includeEditor = false)
		{
			string sources = SourceStamp(project);
			string projectFile = ProjectGenerator.ProjectFile(project);
			string packages = Path.ChangeExtension(projectFile, ".packages.props");
			return IsBuildCurrent(AssemblyFile(project), sources, projectFile, packages)
				&& (!includeEditor || IsBuildCurrent(EditorAssemblyFile(project), sources, ProjectGenerator.EditorProjectFile(project), packages));
		}

		/// <summary>
		///     The scripts as one value: every .cs file under Assets with its size and modification time. A successful build
		///     records the value it started from next to its assemblies (<see cref="RecordSources" />), and the build stays
		///     current while the scripts give the same value: a deleted, added, renamed or moved script makes it stale, as
		///     does an edit (a check of the newest modification time alone missed deletions and moves).
		/// </summary>
		public static string SourceStamp(ProjectInfo project)
		{
			if (!Directory.Exists(project.AssetsPath))
			{
				return "";
			}

			var entries = new System.Collections.Generic.List<string>();
			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories))
			{
				var info = new FileInfo(file);
				entries.Add(Path.GetRelativePath(project.AssetsPath, file).Replace('\\', '/') + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks);
			}

			entries.Sort(StringComparer.Ordinal);
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries))));
		}

		/// <summary>
		///     Records the scripts a successful build compiled (<paramref name="sources" />: <see cref="SourceStamp" /> when
		///     it started) next to Game.dll, and next to Game.Editor.dll when it built the editor scripts too.
		/// </summary>
		public static void RecordSources(ProjectInfo project, string sources, bool includeEditor)
		{
			try
			{
				File.WriteAllText(SourcesFile(AssemblyFile(project)), sources);
				if (includeEditor)
				{
					File.WriteAllText(SourcesFile(EditorAssemblyFile(project)), sources);
				}
			}
			catch (IOException e)
			{
				// Without the record the next start builds again: slower, never stale.
				Debug.LogWarning("[Scripts] Cannot record the built scripts: " + e.Message);
			}
		}

		// Library/ScriptAssemblies/Game.dll.sources: the SourceStamp of the scripts the assembly was built from.
		private static string SourcesFile(string dll)
		{
			return dll + ".sources";
		}

		private static bool IsBuildCurrent(string dll, string sources, string projectFile, string packages)
		{
			string record = SourcesFile(dll);
			if (!File.Exists(dll) || !File.Exists(record) || File.ReadAllText(record) != sources)
			{
				return false;
			}

			DateTime built = File.GetLastWriteTimeUtc(dll);
			return (!File.Exists(projectFile) || File.GetLastWriteTimeUtc(projectFile) <= built)
				&& (!File.Exists(packages) || File.GetLastWriteTimeUtc(packages) <= built);
		}

		private static byte[] ReadSymbols(string dll)
		{
			string pdb = Path.ChangeExtension(dll, ".pdb");
			return File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
		}

		/// <summary>Called every frame on the main thread: starts debounced builds, applies finished ones.</summary>
		public void Update()
		{
			if (sourcesChanged && buildTask == null && Time.timeD - changedAt > 0.3)
			{
				sourcesChanged = false;
				StartBuild();
			}

			if (buildTask != null && buildTask.IsCompleted)
			{
				BuildResult result = buildTask.IsFaulted ? Failed(buildTask.Exception) : buildTask.Result;
				buildTask = null;
				OnBuildFinished(result);
			}

			// Script changes made while playing apply when play mode ends.
			if (pendingReload != null && !editor.IsPlaying)
			{
				BuildResult result = pendingReload;
				pendingReload = null;
				Apply(result);
			}

			while (survivors.TryDequeue(out (WeakReference context, System.Collections.Generic.List<string> components) survivor))
			{
				ReportLeak(survivor.context, survivor.components);
			}
		}

		public void RequestBuild()
		{
			changedAt = double.MinValue;
			sourcesChanged = true;
		}

		/// <summary>
		///     Fresh domain from the current assembly (play mode start: clean statics). The caller has unloaded the
		///     scene already.
		/// </summary>
		public void ReloadNow()
		{
			if (assemblyBytes != null)
			{
				SwapDomain();
			}
		}

		public void OpenInIde()
		{
			ProjectGenerator.Generate(project);
			try
			{
				Process.Start(new ProcessStartInfo(ProjectGenerator.SolutionFile(project)) { UseShellExecute = true });
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Scripts] Cannot open the script project: " + e.Message);
			}
		}

		public void Dispose()
		{
			disposed = true;
			watcher?.Dispose();
			watcher = null;
			WeakReference unloaded = UnloadDomain();
			VerifyUnload(unloaded);
		}

		#endregion

		#region PrivateMethods

		private void OnSourcesChanged()
		{
			changedAt = Time.timeD;
			sourcesChanged = true;
		}

		private void StartBuild()
		{
			// The previous build's diagnostics go (this build reports its own); Clear on Recompile clears the rest too.
			editor.Log.RemoveSticky();
			if (EditorPreferences.Current.Console.ClearOnRecompile)
			{
				editor.Log.Clear();
			}

			ProjectGenerator.Generate(project);
			StatusText = "Compiling scripts...";
			string file = projectFile;
			bool editorScripts = HasEditorScripts(project);
			// The scripts as the build starts: an edit made while it runs is newer than what it compiles.
			buildSources = SourceStamp(project);
			buildIncludesEditor = editorScripts;
			buildTask = Task.Run(() => ScriptBuilder.Build(file, "Debug", null, editorScripts));
		}

		private void OnBuildFinished(BuildResult result)
		{
			foreach (BuildDiagnostic diagnostic in result.Diagnostics)
			{
				editor.Log.Add("[Scripts] " + diagnostic, diagnostic.IsError ? LogType.Error : LogType.Warning, diagnostic.File, diagnostic.Line, true);
			}

			if (!result.Success)
			{
				HasErrors = true;
				StatusText = $"Script errors: {Math.Max(1, result.ErrorCount)}";
				Debug.LogError($"[Scripts] Build failed ({Math.Max(1, result.ErrorCount)} errors); the previous scripts stay loaded.");
				return;
			}

			HasErrors = false;
			RecordSources(project, buildSources, buildIncludesEditor);
			StatusText = $"Scripts compiled ({result.Seconds:0.0} s)";
			if (result.WovenChecks > 0)
			{
				Debug.Log($"[Scripts] {result.WovenChecks} null checks treat destroyed objects as null");
			}
			if (editor.IsPlaying)
			{
				pendingReload = result;
				StatusText += ", reload after play";
				return;
			}

			Apply(result);
		}

		private void Apply(BuildResult result)
		{
			assemblyBytes = result.Assembly;
			symbolBytes = result.Symbols;
			editorAssemblyBytes = result.EditorAssembly;
			editorSymbolBytes = result.EditorSymbols;
			editor.ReloadSceneAround(SwapDomain);
		}

		private void SwapDomain()
		{
			var watch = Stopwatch.StartNew();
			WeakReference unloaded = UnloadDomain();
			LoadDomain();
			Debug.Log($"[Scripts] Domain reloaded in {watch.Elapsed.TotalMilliseconds:0} ms");
			VerifyUnload(unloaded);
		}

		private void LoadDomain()
		{
			if (assemblyBytes == null)
			{
				return;
			}

			context = new GameLoadContext(outputFolder);
			try
			{
				GameAssembly = context.Load(assemblyBytes, symbolBytes);
				TypeRegistry.Register(GameAssembly);
			}
			catch (Exception e)
			{
				Debug.LogError("[Scripts] Cannot load the script assembly: " + e.Message);
				GameAssembly = null;
			}

			// Editor scripts: not registered for serialization (components there could not exist in a player).
			if (GameAssembly != null && editorAssemblyBytes != null)
			{
				try
				{
					EditorAssembly = context.Load(editorAssemblyBytes, editorSymbolBytes);
				}
				catch (Exception e)
				{
					Debug.LogError("[Scripts] Cannot load the editor script assembly: " + e.Message);
					EditorAssembly = null;
				}
			}

			Version++;
			editor.Extensions.OnDomainLoaded(EditorAssembly);
		}

		/// <summary>
		///     Script components of the scene about to be unloaded (weak): when the old context survives, the leak
		///     report says whether script objects are still reachable or only their types.
		/// </summary>
		public void TrackComponents(Scene scene)
		{
			trackedComponents.Clear();
			if (scene == null || GameAssembly == null)
			{
				return;
			}

			foreach (GameObject go in scene.GameObjects)
			{
				foreach (Behaviour component in go.Components)
				{
					if (component.GetType().Assembly == GameAssembly)
					{
						trackedComponents.Add((new WeakReference(component), go.Name + "/" + component.GetType().Name));
					}
				}
			}
		}

		/// <summary>Drops everything that holds script types, then starts unloading the context.</summary>
		private WeakReference UnloadDomain()
		{
			editor.ClearTypeCaches();
			// ScriptableObject assets are instances of script classes: OnDisable, then they load again on their next use.
			ScriptableObjectAssets.UnloadAll();
			SerializationUtility.ClearCache();
			if (GameAssembly != null)
			{
				TypeRegistry.Unregister(GameAssembly);
				GameAssembly = null;
			}

			EditorAssembly = null;

			if (context == null)
			{
				return null;
			}

			var weak = new WeakReference(context, false);
			context.Unload();
			context = null;
			Version++;
			return weak;
		}

		/// <summary>Background check that the old context was collected; a survivor is a leak worth reporting.</summary>
		private void VerifyUnload(WeakReference unloaded)
		{
			if (unloaded == null)
			{
				return;
			}

			var components = new System.Collections.Generic.List<(WeakReference, string)>(trackedComponents);
			trackedComponents.Clear();
			Task.Run(async () =>
			{
				for (int attempt = 0; attempt < 40; attempt++)
				{
					GC.Collect();
					GC.WaitForPendingFinalizers();
					GC.Collect();
					if (!unloaded.IsAlive)
					{
						Console.WriteLine($"[Scripts] Previous script assembly unloaded ({attempt + 1} GC passes)");
						return;
					}

					await Task.Delay(100);
				}

				var alive = new System.Collections.Generic.List<string>();
				foreach ((WeakReference reference, string name) in components)
				{
					if (reference.IsAlive)
					{
						alive.Add(name);
					}
				}

				if (disposed)
				{
					// The editor is closing: no frame left to trace in.
					Console.WriteLine("[Warning] [Scripts] The previous script assembly is still referenced after the reload.");
					return;
				}

				survivors.Enqueue((unloaded, alive));
			});
		}

		/// <summary>
		///     Main thread: the warning for a context that outlived its unload check, with the paths
		///     <see cref="LeakTracer" /> finds from the engine's and the editor's static fields, the editor and the runtime's
		///     hooks to what is left of the old scripts.
		/// </summary>
		private void ReportLeak(WeakReference unloaded, System.Collections.Generic.List<string> aliveComponents)
		{
			if (!(unloaded.Target is AssemblyLoadContext old))
			{
				Console.WriteLine("[Scripts] Previous script assembly unloaded (late)");
				return;
			}

			LeakTracer.Report report = LeakTracer.Trace(old, new[] { new System.Collections.Generic.KeyValuePair<string, object>("editor", editor) });
			old = null;
			var text = new System.Text.StringBuilder("[Warning] [Scripts] The previous script assembly is still referenced after the reload (it stays in memory with its static state).");
			string searched = report.Visited.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + $" objects searched in {report.Milliseconds:0} ms{(report.Stopped ? ", stopped early" : "")}";
			if (report.Paths.Count > 0)
			{
				text.Append($" Shortest paths from what holds it ({searched}):");
				foreach (string path in report.Paths)
				{
					text.Append("\n  ").Append(path);
				}

				if (report.Roots > report.Paths.Count)
				{
					text.Append($"\n  ... and {report.Roots - report.Paths.Count} more roots");
				}

				text.Append($"\n  {report.Found} object{(report.Found == 1 ? "" : "s")} of the old scripts reached in all (each path stops at the first one).");
			}
			else
			{
				text.Append($" Nothing reachable from the static fields of the engine and the editor, the editor itself or the runtime's events, timers and queued work holds it ({searched}): " +
					"look for a thread a script started that still runs, a library's static field or event, or a GC handle / native callback (a delegate passed to native code).");
			}

			if (aliveComponents.Count > 0)
			{
				text.Append("\n  Components of the scene unloaded at the reload still alive: ").Append(string.Join(", ", aliveComponents));
			}

			// One console entry for every line (written line by line, the paths would be plain log entries without the warning's color).
			editor.Log.Add(text.ToString(), LogType.Warning);
		}

		private static BuildResult Failed(Exception exception)
		{
			var result = new BuildResult();
			result.Diagnostics.Add(new BuildDiagnostic { IsError = true, Code = "LEL0003", Message = exception?.GetBaseException().Message ?? "Build failed" });
			return result;
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     Collectible context of the game scripts. Engine and framework assemblies resolve to the editor's copies
		///     (shared types); the assemblies loaded from memory (Game.dll for Game.Editor.dll) resolve to themselves;
		///     other dependencies (NuGet packages of the game) load from the build output.
		/// </summary>
		/// <summary>
		///     Game.dll and Game.Editor.dll in one context: the editor scripts resolve Game.dll through it (batch mode loads
		///     its scripts the same way).
		/// </summary>
		internal sealed class GameLoadContext : AssemblyLoadContext
		{
			private readonly string folder;

			public GameLoadContext(string folder)
				: base("GameScripts", true)
			{
				this.folder = folder;
			}

			/// <summary>Loads an assembly from its bytes (the files stay free for the next build).</summary>
			public Assembly Load(byte[] assembly, byte[] symbols)
			{
				return symbols != null
					? LoadFromStream(new MemoryStream(assembly), new MemoryStream(symbols))
					: LoadFromStream(new MemoryStream(assembly));
			}

			protected override Assembly Load(AssemblyName assemblyName)
			{
				foreach (Assembly loaded in Assemblies)
				{
					if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
					{
						return loaded;
					}
				}

				foreach (Assembly loaded in Default.Assemblies)
				{
					if (string.Equals(loaded.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase))
					{
						return null;
					}
				}

				string path = Path.Combine(folder, assemblyName.Name + ".dll");
				if (File.Exists(path))
				{
					// From bytes: the file stays free for the next build.
					return LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
				}

				return null;
			}
		}

		#endregion
	}
}
