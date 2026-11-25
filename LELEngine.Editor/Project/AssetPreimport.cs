using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;

namespace LELEngine.Editor
{
	/// <summary>
	///     The assets' full import (the user's ask, 2026-11-25: a preimport when a project opens): what the game loads an
	///     asset from is made in the project's Library before anything uses it, not when a scene or play mode first needs
	///     it (an image's first bake took seconds of a scene's open or of entering play mode). An asset's full import:
	///     <list type="bullet">
	///         <item>a model is imported (Library/ModelCache, its materials extracted as its settings ask);</item>
	///         <item>
	///             an image a build bakes is baked as the build ships it (Library/TextureCache,
	///             <see cref="EditorTextureBakes" />: BC7 or as its settings say, every mip level made);
	///         </item>
	///         <item>
	///             every mesh of a model gets its distance field at the uniform shape (Library/DistanceFieldCache,
	///             <see cref="DistanceFieldBaker.Store" />): the one every renderer without a stretched scale uses.
	///         </item>
	///     </list>
	///     A project chosen in the project chooser or named on the command line gets it over every asset before the editor's
	///     window shows (<see cref="Opening" />), its progress bar a small window of its own shown at once; with the Library complete it
	///     only looks at the caches and the editor opens the project as before. Hidden runs and batch editors run it in the
	///     load's own step. The import of files added or changed on disk runs it over the project too (only what changed
	///     has work), Apply of import settings and Reimport for their assets. What the game does without until it is made stays made on first use: thumbnails, the
	///     fields of stretched shapes, static batches, clouds.
	///     <para>
	///         Models import first, on the main thread (their material assets; an import may extract images and change its
	///         meta file, so the images and fields are listed again after); then the images bake on worker threads while
	///         the fields build here (the GPU). Without <see cref="EditorTextureBakes" /> or a store of fields (batch
	///         editors: a build bakes what it ships) only models import.
	///     </para>
	/// </summary>
	internal sealed class AssetPreimport
	{
		#region PrivateFields

		private readonly EditorTextureBakes textures;
		private readonly DistanceFieldBaker.Store fields;

		// The assets to look at (null: every asset of the project) and whether their caches are made again even when current.
		private readonly List<string> scope;
		private readonly bool force;
		private readonly bool verifyMaterials;

		private List<string> models = new List<string>();
		private List<string> images = new List<string>();
		private List<string> fieldModels = new List<string>();

		// Parts done (models, images, models' fields) and of how many, for the bar; images count on worker threads. The same
		// for the assets looked at (FindWork).
		private int done;
		private int total;
		private int checkedCount;
		private int checkTotal;
		private int imagesBaked;
		private int fieldsBuilt;
		private volatile bool stopping;
		private DistanceFieldAtlas atlas;

		#endregion

		#region Constructors

		/// <param name="textures">The editor's bakes (null: no image is baked).</param>
		/// <param name="fields">The editor's distance fields (null: none is built).</param>
		/// <param name="assetPaths">The assets to import (null: every asset of the project).</param>
		/// <param name="force">Their caches are made again even when current (Reimport).</param>
		/// <param name="verifyMaterials">A model whose extracted materials are gone is imported again (a project opening).</param>
		public AssetPreimport(EditorTextureBakes textures, DistanceFieldBaker.Store fields, IEnumerable<string> assetPaths, bool force, bool verifyMaterials)
		{
			this.textures = textures;
			this.fields = fields;
			scope = assetPaths != null ? new List<string>(assetPaths) : null;
			this.force = force && scope != null;
			this.verifyMaterials = verifyMaterials;
		}

		#endregion

		#region PublicFields

		/// <summary>Something is missing or outdated (after <see cref="FindWork" />).</summary>
		public bool HasWork => models.Count + images.Count + fieldModels.Count > 0;

		/// <summary>The models imported (loaded ones still hold their old meshes: the caller replaces them).</summary>
		public List<string> ImportedModels { get; } = new List<string>();

		/// <summary>
		///     The project (root path) <see cref="BeforeOpen" /> has just imported whole: the open's own step need not look
		///     again (<see cref="TakeImported" />).
		/// </summary>
		private static string importedBeforeOpen;

		/// <summary>The project's assets were imported by <see cref="BeforeOpen" /> right before this open (asked once).</summary>
		public static bool TakeImported(string projectRoot)
		{
			bool imported = string.Equals(importedBeforeOpen, projectRoot, StringComparison.OrdinalIgnoreCase);
			importedBeforeOpen = null;
			return imported;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Lists the assets whose caches are missing or outdated, every one counted on the progress bar ("Looking at the
		///     assets"). Any one thread while nothing else imports (the models are looked at on it, the images and fields on
		///     worker threads); the distance fields are listed without a graphics context, built only with one (<see cref="Run" />).
		/// </summary>
		public void FindWork()
		{
			using Progress.Scope look = Progress.Begin("Looking at the assets");
			List<string> modelPaths = scope != null ? scope.FindAll(ModelAssets.IsModelFile) : AssetDatabase.GetAllAssetPaths(ModelAssets.Extensions);
			List<string> imagePaths = ImageCandidates();
			checkedCount = 0;
			checkTotal = modelPaths.Count + imagePaths.Count + (fields != null ? modelPaths.Count : 0);
			models = new List<string>();
			foreach (string path in modelPaths)
			{
				Checked(look, path);
				if (force ? IsSourceModel(path) : ModelAssets.FindOutdated(verifyMaterials, new[] { path }).Count > 0)
				{
					models.Add(path);
				}
			}

			images = textures != null ? Filter(imagePaths, path => force || textures.NeedsBake(path), look) : new List<string>();
			fieldModels = fields != null ? Filter(modelPaths, path => !fields.ModelFieldsCurrent(path), look) : new List<string>();
		}

		/// <summary>
		///     Makes what <see cref="FindWork" /> found, under one progress task (<paramref name="title" />). In steps: each
		///     yield is a moment the caller may draw a frame (<paramref name="overFrames" />: the project's open); else it runs
		///     through. Main thread. Ended early (the enumerator disposed: the project closed), it stops after the images being
		///     baked.
		/// </summary>
		public IEnumerable<bool> Run(string title, bool overFrames)
		{
			if (!HasWork)
			{
				yield break;
			}

			long started = Stopwatch.GetTimestamp();
			long slice = started;
			int modelCount = models.Count;
			Progress.Scope progress = Progress.Begin(title);
			Task bakes = null;
			stopping = false;
			done = 0;
			if (fieldModels.Count > 0 && !GraphicsThread.HasContext)
			{
				// The fields build on the GPU: without a graphics context here they are left to their first use.
				fieldModels = new List<string>();
			}

			total = models.Count + images.Count + fieldModels.Count;
			try
			{
				foreach (string model in models)
				{
					Report(progress, model);
					if (ImportModel(model))
					{
						ImportedModels.Add(model);
					}

					Interlocked.Increment(ref done);
					if (overFrames && SliceOver(ref slice))
					{
						yield return true;
					}
				}

				// The imports extracted images and wrote their meta files (the fields' stamps): what to make next is listed again.
				if (ImportedModels.Count > 0)
				{
					FindImagesAndFields();
					total = done + images.Count + fieldModels.Count;
				}

				if (images.Count > 0)
				{
					bakes = Task.Run(() => BakeImages(progress));
				}

				foreach (string model in fieldModels)
				{
					Report(progress, model);
					BuildFields(model);
					Interlocked.Increment(ref done);
					if (overFrames && SliceOver(ref slice))
					{
						yield return true;
					}
				}

				while (bakes != null && !bakes.IsCompleted)
				{
					if (!overFrames)
					{
						bakes.Wait();
						break;
					}

					yield return true;
				}
			}
			finally
			{
				stopping = true;
				try
				{
					bakes?.Wait();
				}
				catch (AggregateException e)
				{
					Debug.LogException(e.InnerException ?? e);
				}

				atlas?.Delete();
				atlas = null;
				fields?.SaveImported();
				progress.Dispose();
			}

			Debug.Log($"[Editor] Assets imported in {Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s: {ImportedModels.Count} of {modelCount} models, "
				+ $"{imagesBaked} of {images.Count} images baked, {fieldsBuilt} distance fields built for {fieldModels.Count} models");
		}

		/// <summary>
		///     A project's full import before the editor opens it (<see cref="Opening" />) at once: the editor's window exists
		///     (hidden), its graphics context current. Main thread.
		/// </summary>
		public static void BeforeOpen(string projectRoot)
		{
			Opening.Begin(projectRoot)?.Finish();
		}

		/// <summary>Runs it through (<see cref="Run" /> without frames between). Main thread.</summary>
		public void RunNow(string title)
		{
			foreach (bool _ in Run(title, false))
			{
			}
		}

		#endregion

		#region PrivateMethods

		// A model file an importer reads (not a mesh asset, which is the engine's data already).
		private static bool IsSourceModel(string assetPath)
		{
			return ModelAssets.IsModelFile(assetPath) && !ModelAssets.IsMeshFile(assetPath);
		}

		// The image assets to look at.
		private List<string> ImageCandidates()
		{
			if (textures == null)
			{
				return new List<string>();
			}

			return scope != null ? scope.FindAll(TextureAssets.IsImageFile) : AssetDatabase.GetAllAssetPaths(TextureAssets.ImageExtensions);
		}

		// The images whose bakes and the models whose fields are missing or outdated, listed again after the models' imports
		// (forced: every one in the scope), looked at on worker threads (the checks read small files: stamps, a record; a
		// fresh checkout hashes the files once).
		private void FindImagesAndFields()
		{
			images = textures != null ? Filter(ImageCandidates(), path => force || textures.NeedsBake(path), null) : new List<string>();
			if (fields != null)
			{
				List<string> candidates = scope != null ? scope.FindAll(ModelAssets.IsModelFile) : AssetDatabase.GetAllAssetPaths(ModelAssets.Extensions);
				fieldModels = Filter(candidates, path => !fields.ModelFieldsCurrent(path), null);
			}
		}

		// One more asset looked at (the bar of FindWork).
		private void Checked(Progress.Scope look, string path)
		{
			look?.Report(Interlocked.Increment(ref checkedCount) - 1, Math.Max(checkTotal, 1), path);
		}

		// The paths that pass, in their order (one whose check fails is logged and left as it is), each counted on the bar.
		private List<string> Filter(List<string> paths, Func<string, bool> needs, Progress.Scope look)
		{
			var pass = new bool[paths.Count];
			Parallel.For(0, paths.Count, i =>
			{
				Checked(look, paths[i]);
				try
				{
					pass[i] = needs(paths[i]);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Editor] Cannot check the import of {paths[i]}: {e.Message}");
				}
			});
			var result = new List<string>();
			for (int i = 0; i < paths.Count; i++)
			{
				if (pass[i])
				{
					result.Add(paths[i]);
				}
			}

			return result;
		}

		private static bool ImportModel(string assetPath)
		{
			try
			{
				// A fresh import: the cached one may match the file and still miss its material files.
				ModelAssets.LoadData(assetPath, true);
				return true;
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[Model] Cannot import {assetPath}: {e.Message}");
				return false;
			}
		}

		private void BuildFields(string modelPath)
		{
			try
			{
				fieldsBuilt += fields.BuildModelFields(modelPath, ref atlas);
			}
			catch (Exception e)
			{
				Debug.LogWarning($"[SDF] Cannot build the distance fields of {modelPath}: {e.Message}");
			}
		}

		/// <summary>
		///     The images, a few at a time (each bake spreads its blocks and mip levels over the cores; the decode of the image
		///     before it is one thread's), until done or stopped.
		/// </summary>
		private void BakeImages(Progress.Scope progress)
		{
			var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 4, 2, 6) };
			Parallel.ForEach(images, options, (path, state) =>
			{
				if (stopping)
				{
					state.Stop();
					return;
				}

				Report(progress, path);
				if (textures.BakeNow(path, force, false))
				{
					Interlocked.Increment(ref imagesBaked);
				}

				Interlocked.Increment(ref done);
			});
		}

		private void Report(Progress.Scope progress, string what)
		{
			progress.Report(Volatile.Read(ref done), Math.Max(total, 1), what);
		}

		// The main thread's part ran 50 ms since the last frame: one is drawn (a load's window keeps drawing).
		private static bool SliceOver(ref long since)
		{
			long now = Stopwatch.GetTimestamp();
			if (Stopwatch.GetElapsedTime(since, now).TotalMilliseconds < 50)
			{
				return false;
			}

			since = now;
			return true;
		}

		#endregion

		/// <summary>
		///     A project's full import before the editor opens it (the user's ask, 2026-11-25: after a project is chosen or
		///     named on the command line, before the editor's window shows anything of it), under the progress bar as a small
		///     window of its own (<see cref="ProgressOverlay.Standalone" />), shown at once (the user's correction: with many
		///     assets it came late, after the index and the look). In two halves: <see cref="Begin" /> shows the bar and
		///     starts reading the asset index and looking at every asset on a worker thread, while the editor makes its window
		///     and graphics device; <see cref="Finish" />, with that window's context current, waits for the look, makes what
		///     is missing and lets go of what the imports loaded (an extracted model's materials, their shader and textures,
		///     read from the images: no bakes here): the editor loads the project as it does, from the Library. An error is
		///     logged and left to the project's open.
		/// </summary>
		public sealed class Opening
		{
			private readonly ProjectInfo project;
			private readonly Progress.Scope progress;
			private readonly EditorTextureBakes textures;
			private readonly AssetPreimport preimport;
			private readonly Task look;
			private readonly long started;

			private Opening(ProjectInfo project)
			{
				this.project = project;
				started = Stopwatch.GetTimestamp();
				importedBeforeOpen = null;
				// GLFW first: it makes the process aware of each monitor's scale, so the bar's window is drawn at the screen's.
				LELEngine.Window.InitializeGlfw();
				ProgressOverlay.Standalone = true;
				ProgressOverlay.Start(IntPtr.Zero);
				progress = Progress.Begin("Importing assets of " + project.Name);
				progress.Report(-1f, "Reading the asset index");
				textures = new EditorTextureBakes(project);
				preimport = new AssetPreimport(textures, new DistanceFieldBaker.Store(project), null, false, true);
				look = Task.Run(() =>
				{
					AssetDatabase.Initialize(project.AssetsPath, true);
					ModelAssets.CacheFolder = project.ModelCachePath;
					// The look and the import fill the bar in turn.
					progress.Report(0, 1);
					preimport.FindWork();
				});
			}

			/// <summary>Shows the bar and starts the look (null: not a project, which the open reports). Main thread.</summary>
			public static Opening Begin(string projectRoot)
			{
				ProjectInfo project;
				try
				{
					project = ProjectInfo.Load(projectRoot);
				}
				catch (Exception)
				{
					return null;
				}

				return new Opening(project);
			}

			/// <summary>The rest of the import, with the editor's (hidden) window's graphics context current; then the bar goes. Main thread.</summary>
			public void Finish()
			{
				try
				{
					look.GetAwaiter().GetResult();
					if (preimport.HasWork)
					{
						preimport.RunNow("Importing assets of " + project.Name);
					}
					else
					{
						Debug.Log($"[Editor] The assets of {project.Name} are imported (looked at in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms)");
					}

					importedBeforeOpen = project.RootPath;
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					End();
					// What the imports loaded, in the context it was made in.
					InternalStorage.Clear();
				}
			}

			/// <summary>The editor's window could not be made: the look ends, nothing is imported, the bar goes. Main thread.</summary>
			public void Abandon()
			{
				try
				{
					look.Wait();
				}
				catch (AggregateException)
				{
					// Nothing comes of it.
				}

				End();
			}

			private void End()
			{
				progress.Dispose();
				ProgressOverlay.Standalone = false;
				textures.Close();
				ModelAssets.CacheFolder = null;
			}
		}
	}
}
