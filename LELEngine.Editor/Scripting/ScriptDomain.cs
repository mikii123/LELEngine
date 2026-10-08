using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
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
	///     references it, a warning names the leak.
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
		private BuildResult pendingReload;
		private GameLoadContext context;
		private byte[] assemblyBytes;
		private byte[] symbolBytes;
		private readonly System.Collections.Generic.List<(WeakReference, string)> trackedComponents = new System.Collections.Generic.List<(WeakReference, string)>();

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
			if (File.Exists(dll))
			{
				assemblyBytes = File.ReadAllBytes(dll);
				string pdb = Path.ChangeExtension(dll, ".pdb");
				symbolBytes = File.Exists(pdb) ? File.ReadAllBytes(pdb) : null;
				LoadDomain();
				if (!IsAssemblyCurrent(project))
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

		public static bool HasScripts(ProjectInfo project)
		{
			return NewestSource(project) != DateTime.MinValue;
		}

		/// <summary>The editor build exists and is newer than every script and the generated project.</summary>
		public static bool IsAssemblyCurrent(ProjectInfo project)
		{
			string dll = AssemblyFile(project);
			if (!File.Exists(dll))
			{
				return false;
			}

			DateTime built = File.GetLastWriteTimeUtc(dll);
			string projectFile = ProjectGenerator.ProjectFile(project);
			string packages = Path.ChangeExtension(projectFile, ".packages.props");
			return NewestSource(project) <= built
				&& (!File.Exists(projectFile) || File.GetLastWriteTimeUtc(projectFile) <= built)
				&& (!File.Exists(packages) || File.GetLastWriteTimeUtc(packages) <= built);
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
			ProjectGenerator.Generate(project);
			StatusText = "Compiling scripts...";
			string file = projectFile;
			buildTask = Task.Run(() => ScriptBuilder.Build(file, "Debug", null));
		}

		private void OnBuildFinished(BuildResult result)
		{
			foreach (BuildDiagnostic diagnostic in result.Diagnostics)
			{
				editor.Log.Add("[Scripts] " + diagnostic, diagnostic.IsError ? LogType.Error : LogType.Warning, diagnostic.File, diagnostic.Line);
			}

			if (!result.Success)
			{
				HasErrors = true;
				StatusText = $"Script errors: {Math.Max(1, result.ErrorCount)}";
				Debug.LogError($"[Scripts] Build failed ({Math.Max(1, result.ErrorCount)} errors); the previous scripts stay loaded.");
				return;
			}

			HasErrors = false;
			StatusText = $"Scripts compiled ({result.Seconds:0.0} s)";
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
				GameAssembly = symbolBytes != null
					? context.LoadFromStream(new MemoryStream(assemblyBytes), new MemoryStream(symbolBytes))
					: context.LoadFromStream(new MemoryStream(assemblyBytes));
				TypeRegistry.Register(GameAssembly);
			}
			catch (Exception e)
			{
				Debug.LogError("[Scripts] Cannot load the script assembly: " + e.Message);
				GameAssembly = null;
			}

			Version++;
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
			SerializationUtility.ClearCache();
			if (GameAssembly != null)
			{
				TypeRegistry.Unregister(GameAssembly);
				GameAssembly = null;
			}

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

				Console.WriteLine("[Warning] [Scripts] The previous script assembly is still referenced after the reload: something (a static " +
					"field, an event subscription, a running thread or timer started by a script) keeps it alive. " +
					(alive.Count > 0 ? "Script objects still reachable: " + string.Join(", ", alive) : "No script object is reachable (a type or delegate is held)."));
			});
		}

		private static DateTime NewestSource(ProjectInfo project)
		{
			DateTime newest = DateTime.MinValue;
			if (!Directory.Exists(project.AssetsPath))
			{
				return newest;
			}

			foreach (string file in Directory.EnumerateFiles(project.AssetsPath, "*.cs", SearchOption.AllDirectories))
			{
				DateTime time = File.GetLastWriteTimeUtc(file);
				if (time > newest)
				{
					newest = time;
				}
			}

			return newest;
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
		///     (shared types); other dependencies (NuGet packages of the game) load from the build output.
		/// </summary>
		private sealed class GameLoadContext : AssemblyLoadContext
		{
			private readonly string folder;

			public GameLoadContext(string folder)
				: base("GameScripts", true)
			{
				this.folder = folder;
			}

			protected override Assembly Load(AssemblyName assemblyName)
			{
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
