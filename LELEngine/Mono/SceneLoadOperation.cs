using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace LELEngine
{
	/// <summary>How a loaded scene joins the scenes already loaded.</summary>
	public enum LoadSceneMode
	{
		/// <summary>In place of every loaded scene (but the DontDestroyOnLoad scene); it becomes the active scene.</summary>
		Single,

		/// <summary>With the loaded scenes: updated and drawn together with them; the active scene stays.</summary>
		Additive
	}

	/// <summary>
	///     A scene loading in the background (<see cref="SceneManager.LoadSceneAsync" />). It is read on the loading
	///     thread (objects, components and the assets they use: files read, images decoded, shader sources prepared),
	///     then prepared for the GPU on the main thread a few milliseconds per frame (shaders compiled, textures and
	///     meshes uploaded: <see cref="SceneManager.BackgroundLoadingBudget" />), and activated: it joins the loaded
	///     scenes (Single: in place of them) and its lifecycle starts. With <see cref="AllowActivation" /> off it stays
	///     loaded but not activated, nothing of it running or drawn, until activation is allowed (<see cref="Activate" />).
	///     Scenes activate in the order they were requested. The ScriptableObjects it loads get Awake and OnEnable on the
	///     main thread once it is prepared; the glyphs of its UI are rasterized while it is read.
	/// </summary>
	public sealed class SceneLoadOperation : IBackgroundLoad
	{
		#region PublicFields

		/// <summary>Asset path of the scene file (as requested).</summary>
		public string Path { get; }

		public LoadSceneMode Mode { get; }

		/// <summary>The scene once it is read: its objects exist but nothing of it runs or is drawn before activation. Null before that and after a failure.</summary>
		public Scene Scene { get; private set; }

		/// <summary>0..1: reading (most of the time), then the GPU preparation; 1 once the scene is loaded.</summary>
		public float Progress
		{
			get
			{
				switch (state)
				{
					case LoadState.Queued:
						return 0f;
					case LoadState.Reading:
						return 0.9f * readProgress;
					case LoadState.Preparing:
						return 0.9f + 0.1f * (assets.Count == 0 ? 1f : (float)prepared / assets.Count);
					default:
						return 1f;
				}
			}
		}

		/// <summary>Read and prepared: activation takes no more loading.</summary>
		public bool IsLoaded => state >= LoadState.Loaded && state != LoadState.Failed && state != LoadState.Cancelled;

		/// <summary>Activated, failed or cancelled.</summary>
		public bool IsDone => state >= LoadState.Activated;

		/// <summary>Activated: the scene is one of <see cref="SceneManager.LoadedScenes" />.</summary>
		public bool IsActivated => state == LoadState.Activated;

		/// <summary>The scene activates as soon as it is loaded (and the scenes requested before it are activated). Off: it waits loaded.</summary>
		public bool AllowActivation { get; set; }

		/// <summary>Why reading the scene failed (null when it did not).</summary>
		public Exception Error { get; private set; }

		/// <summary>Where the time went: reading per asset kind, GPU preparation, activation.</summary>
		public string Summary { get; private set; } = "";

		/// <summary>Raised on the main thread when the operation is done: activated, failed or cancelled.</summary>
		public event Action<SceneLoadOperation> Completed;

		#endregion

		#region InternalFields

		internal enum LoadState
		{
			Queued,
			Reading,
			Preparing,
			Loaded,
			Activated,
			Failed,
			Cancelled
		}

		internal LoadState State => state;
		internal bool CancelRequested => cancelRequested;
		bool IBackgroundLoad.CancelRequested => cancelRequested;
		internal readonly string AbsolutePath;

		#endregion

		#region PrivateFields

		private volatile LoadState state = LoadState.Queued;
		private volatile bool cancelRequested;
		private volatile float readProgress;
		private readonly bool background;
		private List<object> assets = new List<object>();
		private List<ScriptableObject> held;
		private int prepared;
		private string readSummary = "";
		private long readTicks;
		private long prepareTicks;
		private int prepareFrames;

		#endregion

		#region Constructors

		/// <param name="background">Read on the loading thread (not by the caller of a synchronous load).</param>
		internal SceneLoadOperation(string path, string absolutePath, LoadSceneMode mode, bool allowActivation, bool background)
		{
			Path = path;
			AbsolutePath = absolutePath;
			Mode = mode;
			AllowActivation = allowActivation;
			this.background = background;
		}

		#endregion

		#region PublicMethods

		/// <summary>Allows the activation (the scene activates once it is loaded).</summary>
		public void Activate()
		{
			AllowActivation = true;
		}

		/// <summary>Drops the load: a scene not activated yet is unloaded (now, or when its reading ends); an activated one stays.</summary>
		public void Cancel()
		{
			cancelRequested = true;
		}

		public override string ToString()
		{
			return $"{Path} ({state}, {Progress:P0})";
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Loading thread (or the caller of a synchronous load): reads the scene and records the assets it uses; a
		///     background read holds back the callbacks of the ScriptableObjects it loads and rasterizes the glyphs of its UI.
		/// </summary>
		internal void Read()
		{
			state = LoadState.Reading;
			long start = Stopwatch.GetTimestamp();
			LoadState next;
			using (AssetLoadScope scope = AssetLoadScope.Begin(background))
			{
				try
				{
					Scene = Serialization.SceneSerializer.Load(AbsolutePath, progress => readProgress = progress);
					var recorded = new List<object>(scope.Assets);
					int fonts = background ? UIElements.UIAssets.WarmUp(recorded) : 0;
					assets = recorded;
					readSummary = scope.Summary() + (fonts > 0 ? $", glyphs of {fonts} font{(fonts == 1 ? "" : "s")}" : "");
					next = LoadState.Preparing;
				}
				catch (Exception e)
				{
					Error = e;
					Scene = null;
					next = LoadState.Failed;
				}

				// Published with the state: the main thread runs them once the scene is prepared.
				held = scope.TakeDeferred();
			}

			readTicks = Stopwatch.GetTimestamp() - start;
			state = next;
		}

		void IBackgroundLoad.Read()
		{
			Read();
		}

		/// <summary>The assets the scene uses (recorded while it was read).</summary>
		internal IReadOnlyList<object> Assets => assets;

		/// <summary>
		///     Main thread: GPU work for the scene's assets until <paramref name="deadline" /> (a Stopwatch timestamp;
		///     at least one asset per call). True when everything is prepared.
		/// </summary>
		internal bool Prepare(long deadline, bool gpuDriven)
		{
			long start = Stopwatch.GetTimestamp();
			prepareFrames++;
			while (prepared < assets.Count)
			{
				SceneManager.PrepareAsset(assets[prepared], gpuDriven);
				prepared++;
				if (Stopwatch.GetTimestamp() >= deadline)
				{
					break;
				}
			}

			prepareTicks += Stopwatch.GetTimestamp() - start;
			if (prepared < assets.Count)
			{
				return false;
			}

			// The scene's ScriptableObjects are enabled before any of its components wake up.
			AssetLoadScope.RunDeferred(held);
			state = LoadState.Loaded;
			return true;
		}

		internal void MarkActivated(long activationTicks)
		{
			state = LoadState.Activated;
			Summary = $"read {Milliseconds(readTicks)} ms ({readSummary}), GPU {Milliseconds(prepareTicks)} ms in {prepareFrames} frame{(prepareFrames == 1 ? "" : "s")}, activated in {Milliseconds(activationTicks)} ms";
			Completed?.Invoke(this);
		}

		internal void MarkFailed()
		{
			AssetLoadScope.RunDeferred(held);
			Summary = $"failed after {Milliseconds(readTicks)} ms: {Error?.Message}";
			Completed?.Invoke(this);
		}

		internal void MarkCancelled()
		{
			state = LoadState.Cancelled;
			Scene = null;
			Summary = "cancelled";
			Completed?.Invoke(this);
		}

		#endregion

		#region PrivateMethods

		private static long Milliseconds(long ticks)
		{
			return ticks * 1000 / Stopwatch.Frequency;
		}

		#endregion
	}
}
