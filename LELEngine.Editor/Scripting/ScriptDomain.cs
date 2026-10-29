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
	///     Scripts in Tests folders build into Game.Tests.dll after the game's assemblies (dotnet build of the tests'
	///     project alone), loaded into a collectible context of their own that resolves Game.dll from the game's
	///     (<see cref="TestLoadContext" />) and handed to <see cref="EditorTests" />. When only tests changed, only the
	///     tests build and reload (the scene stays); errors in the tests never keep the game scripts from reloading.
	/// </summary>
	internal sealed class ScriptDomain : IDisposable
	{
		#region PublicFields

		/// <summary>Bumped on every domain load (caches keyed by script types compare it).</summary>
		public int Version { get; private set; }

		public bool HasErrors { get; private set; }
		public bool IsBuilding => buildTask != null;

		/// <summary>No build running or waiting (sources changed, a requested build) and no reload waiting for play mode to end.</summary>
		public bool IsIdle => buildTask == null && !sourcesChanged && pendingReload == null;
		public string StatusText { get; private set; } = "";
		public Assembly GameAssembly { get; private set; }

		/// <summary>The editor scripts (null when the project has none or they did not load).</summary>
		public Assembly EditorAssembly { get; private set; }

		/// <summary>The tests (null when the project has none, they do not compile or did not load).</summary>
		public Assembly TestAssembly { get; private set; }

		/// <summary>The project had test scripts at the last load or build.</summary>
		public bool HasTests { get; private set; }

		/// <summary>Diagnostics of the last build of the tests (their errors keep the tests unloaded).</summary>
		public System.Collections.Generic.IReadOnlyList<BuildDiagnostic> TestDiagnostics { get; private set; } = Array.Empty<BuildDiagnostic>();

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
		private string buildTestSources;
		private bool buildIncludesEditor;

		// The next build compiles the game scripts even when they are current ("Rebuild Scripts").
		private bool fullBuildRequested;
		private BuildResult pendingReload;
		private GameLoadContext context;
		private TestLoadContext testContext;
		private byte[] assemblyBytes;
		private byte[] symbolBytes;
		private byte[] editorAssemblyBytes;
		private byte[] editorSymbolBytes;
		private byte[] testAssemblyBytes;
		private byte[] testSymbolBytes;

		// The game build's diagnostics: a build of the tests alone shows them again (it clears the console's build entries).
		private System.Collections.Generic.List<BuildDiagnostic> gameDiagnostics = new System.Collections.Generic.List<BuildDiagnostic>();
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
			HasTests = HasTestScripts(project);
			if (File.Exists(dll))
			{
				// Scripts built before the weaver existed (or by an IDE): rewritten before they load.
				var woven = new BuildResult();
				string testsDll = TestsAssemblyFile(project);
				if (!ScriptBuilder.Weave(dll, outputFolder, woven) || (editorScripts && File.Exists(EditorAssemblyFile(project)) && !ScriptBuilder.Weave(EditorAssemblyFile(project), outputFolder, woven))
					|| (HasTests && File.Exists(testsDll) && !ScriptBuilder.Weave(testsDll, outputFolder, woven)))
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

				// Tests built from other sources than the scripts now (or not recorded) wait for their build.
				if (HasTests && File.Exists(testsDll) && IsTestsCurrent(project))
				{
					testAssemblyBytes = File.ReadAllBytes(testsDll);
					testSymbolBytes = ReadSymbols(testsDll);
				}

				LoadDomain();
				if (!IsAssemblyCurrent(project, editorScripts) || HasTests && testAssemblyBytes == null)
				{
					RequestBuild(false);
				}
			}
			else if (HasScripts(project))
			{
				RequestBuild(false);
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

		/// <summary>The tests (Library/ScriptAssemblies/Game.Tests.dll).</summary>
		public static string TestsAssemblyFile(ProjectInfo project)
		{
			return Path.Combine(project.LibraryPath, "ScriptAssemblies", ProjectGenerator.TestsAssemblyName + ".dll");
		}

		public static bool HasScripts(ProjectInfo project)
		{
			return Directory.Exists(project.AssetsPath) && Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).Any();
		}

		/// <summary>Some script lies in a Tests folder.</summary>
		public static bool HasTestScripts(ProjectInfo project)
		{
			return Directory.Exists(project.AssetsPath) && Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories).Any(file => ProjectGenerator.IsTestScript(project, file));
		}

		/// <summary>Game.Tests.dll exists and was built from the scripts as they are now (every script: tests and what they test).</summary>
		public static bool IsTestsCurrent(ProjectInfo project)
		{
			string projectFile = ProjectGenerator.ProjectFile(project);
			return IsBuildCurrent(TestsAssemblyFile(project), SourceStamp(project, true), ProjectGenerator.TestsProjectFile(project), Path.ChangeExtension(projectFile, ".packages.props"));
		}

		/// <summary>Records the scripts (<see cref="SourceStamp" /> with the tests) a successful build of the tests compiled.</summary>
		public static void RecordTestSources(ProjectInfo project, string sources)
		{
			try
			{
				File.WriteAllText(SourcesFile(TestsAssemblyFile(project)), sources);
			}
			catch (IOException e)
			{
				Debug.LogWarning("[Tests] Cannot record the built tests: " + e.Message);
			}
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
		///     does an edit (a check of the newest modification time alone missed deletions and moves). Scripts in Tests
		///     folders count only with <paramref name="includeTests" /> (the tests' stamp): a test edited leaves the game's
		///     assemblies current.
		/// </summary>
		public static string SourceStamp(ProjectInfo project, bool includeTests = false)
		{
			if (!Directory.Exists(project.AssetsPath))
			{
				return "";
			}

			var entries = new System.Collections.Generic.List<string>();
			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories))
			{
				if (!includeTests && ProjectGenerator.IsTestScript(project, file))
				{
					continue;
				}

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

			// Script changes made while playing (or while tests run) apply when play mode (the test run) ends.
			if (pendingReload != null && !editor.IsPlaying && !editor.Tests.IsRunning)
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

		/// <param name="full">Build the game scripts even when they are current (else only what changed: the tests alone when only they did).</param>
		public void RequestBuild(bool full = true)
		{
			fullBuildRequested |= full;
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
			string file = projectFile;
			bool editorScripts = HasEditorScripts(project);
			bool testScripts = HasTestScripts(project);
			HasTests = testScripts;
			// The game's assemblies are loaded and built from the scripts as they are: only the tests changed.
			bool testsOnly = !fullBuildRequested && !HasErrors && assemblyBytes != null && IsAssemblyCurrent(project, editorScripts);
			fullBuildRequested = false;
			StatusText = testsOnly ? "Compiling tests..." : "Compiling scripts...";
			// The scripts as the build starts: an edit made while it runs is newer than what it compiles.
			buildSources = SourceStamp(project);
			buildTestSources = testScripts ? SourceStamp(project, true) : null;
			buildIncludesEditor = editorScripts;
			if (testsOnly)
			{
				// The console's build entries go with RemoveSticky: the game's come back.
				foreach (BuildDiagnostic diagnostic in gameDiagnostics)
				{
					editor.Log.Add("[Scripts] " + diagnostic, diagnostic.IsError ? LogType.Error : LogType.Warning, diagnostic.File, diagnostic.Line, true);
				}
			}

			buildTask = Task.Run(() =>
			{
				BuildResult result = testsOnly ? new BuildResult { Success = true, TestsOnly = true } : ScriptBuilder.Build(file, "Debug", null, editorScripts);
				if (result.Success && testScripts)
				{
					result.Tests = ScriptBuilder.BuildTests(file, "Debug");
				}

				return result;
			});
		}

		private void OnBuildFinished(BuildResult result)
		{
			if (!result.TestsOnly)
			{
				gameDiagnostics = new System.Collections.Generic.List<BuildDiagnostic>(result.Diagnostics);
			}

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

			if (!result.TestsOnly)
			{
				HasErrors = false;
				RecordSources(project, buildSources, buildIncludesEditor);
				StatusText = $"Scripts compiled ({result.Seconds:0.0} s)";
				if (result.WovenChecks > 0)
				{
					Debug.Log($"[Scripts] {result.WovenChecks} null checks treat destroyed objects as null");
				}
			}

			// Errors in the tests are the tests' (the Test Runner lists them too); the game reloads without tests.
			TestDiagnostics = result.Tests?.Diagnostics ?? (System.Collections.Generic.IReadOnlyList<BuildDiagnostic>)Array.Empty<BuildDiagnostic>();
			if (result.Tests != null)
			{
				foreach (BuildDiagnostic diagnostic in result.Tests.Diagnostics)
				{
					editor.Log.Add("[Tests] " + diagnostic, diagnostic.IsError ? LogType.Error : LogType.Warning, diagnostic.File, diagnostic.Line, true);
				}

				if (result.Tests.Success)
				{
					RecordTestSources(project, buildTestSources);
					if (result.TestsOnly)
					{
						StatusText = $"Tests compiled ({result.Tests.Seconds:0.0} s)";
					}
				}
				else
				{
					StatusText = (result.TestsOnly ? "" : StatusText + ", ") + $"test errors: {Math.Max(1, result.Tests.ErrorCount)}";
					Debug.LogError($"[Tests] The tests do not compile ({Math.Max(1, result.Tests.ErrorCount)} errors); they are unloaded until they do.");
				}
			}
			else if (result.TestsOnly)
			{
				StatusText = "Tests removed";
			}

			if (editor.IsPlaying || editor.Tests.IsRunning)
			{
				pendingReload = result;
				StatusText += editor.IsPlaying ? ", reload after play" : ", reload after the test run";
				return;
			}

			Apply(result);
		}

		private void Apply(BuildResult result)
		{
			bool testsLoad = result.Tests != null && result.Tests.Success;
			testAssemblyBytes = testsLoad ? result.Tests.Assembly : null;
			testSymbolBytes = testsLoad ? result.Tests.Symbols : null;
			if (result.TestsOnly)
			{
				// The game's scripts and the scene stay as they are.
				SwapTests();
				return;
			}

			assemblyBytes = result.Assembly;
			symbolBytes = result.Symbols;
			editorAssemblyBytes = result.EditorAssembly;
			editorSymbolBytes = result.EditorSymbols;
			editor.ReloadSceneAround(SwapDomain);
		}

		private void SwapTests()
		{
			var watch = Stopwatch.StartNew();
			WeakReference unloaded = UnloadTests();
			LoadTests();
			Debug.Log(TestAssembly != null ? $"[Tests] Tests reloaded in {watch.Elapsed.TotalMilliseconds:0} ms" : "[Tests] Tests unloaded");
			VerifyUnload(unloaded, new System.Collections.Generic.List<(WeakReference, string)>());
		}

		/// <summary>Loads Game.Tests.dll (when built) into a context of its own beside the game's and hands it to the Test Runner.</summary>
		private void LoadTests()
		{
			if (GameAssembly != null && testAssemblyBytes != null)
			{
				testContext = new TestLoadContext(context, outputFolder);
				try
				{
					TestAssembly = testContext.Load(testAssemblyBytes, testSymbolBytes);
				}
				catch (Exception e)
				{
					Debug.LogError("[Tests] Cannot load the test assembly: " + e.Message);
					TestAssembly = null;
				}
			}

			editor.Tests.OnTestsLoaded(TestAssembly, TestAssembly != null ? testSymbolBytes : null);
		}

		/// <summary>Drops every test type (runner, tree) and starts unloading the tests' context.</summary>
		private WeakReference UnloadTests()
		{
			editor.Tests.OnTestsUnloading();
			TestAssembly = null;
			if (testContext == null)
			{
				return null;
			}

			var weak = new WeakReference(testContext, false);
			testContext.Unload();
			testContext = null;
			return weak;
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
			editor.AnimatorWindow?.OnDomainLoaded();
			editor.AnimationWindow?.OnDomainLoaded();
			LoadTests();
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
			// The tests' context references the game's: it goes first (its own check is the game context's: one holds the other).
			UnloadTests();
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
			var components = new System.Collections.Generic.List<(WeakReference, string)>(trackedComponents);
			trackedComponents.Clear();
			VerifyUnload(unloaded, components);
		}

		/// <param name="components">Script components of the unloaded scene (weak) the report names when they are still alive.</param>
		private void VerifyUnload(WeakReference unloaded, System.Collections.Generic.List<(WeakReference, string)> components)
		{
			if (unloaded == null)
			{
				return;
			}

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

		/// <summary>
		///     Collectible context of the tests: Game.dll and the game's packages resolve to the game context's copies (the
		///     tests see the same types the scenes use), engine, test framework and runtime to the editor's; the tests'
		///     own packages load from the build output. It unloads alone when only the tests changed.
		/// </summary>
		internal sealed class TestLoadContext : AssemblyLoadContext
		{
			private readonly GameLoadContext game;
			private readonly string folder;

			public TestLoadContext(GameLoadContext game, string folder)
				: base("GameTests", true)
			{
				this.game = game;
				this.folder = folder;
			}

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

				foreach (Assembly loaded in game.Assemblies)
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

				// A package the game loads lazily resolves through the game's context (one copy of its types).
				Assembly fromGame = null;
				try
				{
					fromGame = game.LoadFromAssemblyName(assemblyName);
				}
				catch (FileNotFoundException)
				{
					// Not the game's: the tests' own package.
				}

				if (fromGame != null)
				{
					return fromGame;
				}

				string path = Path.Combine(folder, assemblyName.Name + ".dll");
				return File.Exists(path) ? LoadFromStream(new MemoryStream(File.ReadAllBytes(path))) : null;
			}
		}

		#endregion
	}
}
