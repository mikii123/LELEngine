using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace LELEngine
{
	/// <summary>
	///     An asset loading in the background (<see cref="Resources.LoadAsync{T}" />). It is read on the loading thread
	///     (shared with <see cref="SceneManager.LoadSceneAsync" />, requests and scenes in request order): the file and
	///     every asset it uses, images decoded, documents and style sheets parsed, and for UI the glyphs its texts show
	///     rasterized into the fonts' atlases. Then it is prepared for the GPU on the main thread a few milliseconds per
	///     frame (textures, font atlases and meshes uploaded, shaders compiled: <see cref="SceneManager.BackgroundLoadingBudget" />,
	///     shared with scene loads). Then it is done, on the main thread: the ScriptableObjects it loaded get Awake and
	///     OnEnable, <see cref="Asset" /> is set, <see cref="Completed" /> is raised and code awaiting it continues:
	///     <code>
	///     VisualTreeAsset hud = await Resources.LoadAsync&lt;VisualTreeAsset&gt;("UI/Hud");
	///     </code>
	///     <see cref="SceneManager.UnloadAll" /> (the editor leaving play mode, the game quitting) drops the requests not
	///     done: their handlers and awaiting code never run.
	/// </summary>
	public class ResourceRequest : IBackgroundLoad
	{
		#region PublicFields

		/// <summary>The path requested (relative to a Resources folder, no extension).</summary>
		public string Path { get; }

		/// <summary>The type requested; null: the file loads as what it is.</summary>
		public Type Type { get; }

		/// <summary>The asset once the request is done; null before, and when nothing was found.</summary>
		public object Asset => state == RequestState.Done ? loaded : null;

		/// <summary>Done (the asset is ready, or there was none), failed, or dropped.</summary>
		public bool IsDone => state >= RequestState.Done;

		/// <summary>0..1: reading (0 until the read ends), then the GPU preparation; 1 when done.</summary>
		public float Progress
		{
			get
			{
				switch (state)
				{
					case RequestState.Queued:
					case RequestState.Reading:
						return 0f;
					case RequestState.Preparing:
						return 0.9f + 0.1f * (assets.Count == 0 ? 1f : (float)prepared / assets.Count);
					default:
						return 1f;
				}
			}
		}

		/// <summary>Why the load failed (null when it did not; an asset that does not exist is no failure: <see cref="Asset" /> is null).</summary>
		public Exception Error { get; private set; }

		/// <summary>Where the time went: reading per asset kind, GPU preparation.</summary>
		public string Summary { get; private set; } = "";

		/// <summary>Raised on the main thread when the request is done; a handler added after that runs at once.</summary>
		public event Action<ResourceRequest> Completed
		{
			add
			{
				if (value == null)
				{
					return;
				}

				lock (gate)
				{
					if (state < RequestState.Done)
					{
						completed += value;
						return;
					}

					if (state == RequestState.Cancelled)
					{
						return;
					}
				}

				Invoke(value);
			}
			remove
			{
				lock (gate)
				{
					completed -= value;
				}
			}
		}

		#endregion

		#region InternalFields

		internal enum RequestState
		{
			Queued,
			Reading,
			Preparing,

			/// <summary>The read threw; the main thread completes the request as failed.</summary>
			ReadFailed,

			Done,
			Failed,
			Cancelled
		}

		internal RequestState State => state;

		/// <summary>The assets the request loaded (recorded while it was read).</summary>
		internal IReadOnlyList<object> Assets => assets;

		#endregion

		#region PrivateFields

		private readonly object gate = new object();
		private volatile RequestState state = RequestState.Queued;
		private volatile bool cancelRequested;
		private object loaded;
		private List<object> assets = new List<object>();
		private List<ScriptableObject> held;
		private Action<ResourceRequest> completed;
		private List<Action> continuations;
		private int prepared;
		private string readSummary = "";
		private long readTicks;
		private long prepareTicks;
		private int prepareFrames;

		#endregion

		#region Constructors

		internal ResourceRequest(string path, Type type)
		{
			Path = path;
			Type = type;
		}

		#endregion

		#region PublicMethods

		/// <summary>Lets <c>await</c> wait for the request (continues on the main thread with the asset).</summary>
		public ResourceRequestAwaiter GetAwaiter()
		{
			return new ResourceRequestAwaiter(this);
		}

		public override string ToString()
		{
			return $"{Path} ({state}, {Progress:P0})";
		}

		#endregion

		#region InternalMethods

		bool IBackgroundLoad.CancelRequested => cancelRequested;

		/// <summary>Loading thread: reads the asset and everything it uses, recording them (and the UI's glyphs).</summary>
		void IBackgroundLoad.Read()
		{
			lock (gate)
			{
				if (state == RequestState.Cancelled)
				{
					return;
				}

				state = RequestState.Reading;
			}

			long start = Stopwatch.GetTimestamp();
			RequestState next;
			using (AssetLoadScope scope = AssetLoadScope.Begin(true))
			{
				try
				{
					loaded = Type != null ? Resources.Load(Path, Type) : Resources.Load(Path);
					var recorded = new List<object>(scope.Assets);
					if (loaded != null && !recorded.Contains(loaded))
					{
						recorded.Add(loaded);
					}

					int fonts = UIElements.UIAssets.WarmUp(recorded);
					assets = recorded;
					readSummary = scope.Summary() + (fonts > 0 ? $", glyphs of {fonts} font{(fonts == 1 ? "" : "s")}" : "");
					next = RequestState.Preparing;
				}
				catch (Exception e)
				{
					Error = e;
					loaded = null;
					next = RequestState.ReadFailed;
				}

				// Published with the state: the main thread runs them when the request completes.
				held = scope.TakeDeferred();
			}

			readTicks = Stopwatch.GetTimestamp() - start;
			lock (gate)
			{
				// Dropped while it was read (the host quit without waiting): it stays dropped.
				if (state != RequestState.Cancelled)
				{
					state = next;
				}
			}
		}

		/// <summary>
		///     Main thread: GPU work for the request's assets until <paramref name="deadline" /> (a Stopwatch timestamp; at
		///     least one asset per call). True when everything is prepared.
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
			return prepared >= assets.Count;
		}

		/// <summary>Main thread: the request is done (or failed): held-back ScriptableObject callbacks, then handlers and awaiting code.</summary>
		internal void Complete()
		{
			AssetLoadScope.RunDeferred(held);
			Action<ResourceRequest> handlers;
			List<Action> waiting;
			lock (gate)
			{
				if (state == RequestState.ReadFailed)
				{
					Summary = $"failed after {Milliseconds(readTicks)} ms: {Error?.Message}";
					state = RequestState.Failed;
				}
				else
				{
					Summary = $"read {Milliseconds(readTicks)} ms ({readSummary}), GPU {Milliseconds(prepareTicks)} ms in {prepareFrames} frame{(prepareFrames == 1 ? "" : "s")}";
					state = RequestState.Done;
				}

				handlers = completed;
				waiting = continuations;
				completed = null;
				continuations = null;

				// Needed only while it was prepared: a request kept by game code holds just its asset.
				assets = new List<object>();
			}

			if (handlers != null)
			{
				foreach (Delegate handler in handlers.GetInvocationList())
				{
					Invoke((Action<ResourceRequest>)handler);
				}
			}

			if (waiting != null)
			{
				foreach (Action continuation in waiting)
				{
					try
					{
						continuation();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}
				}
			}
		}

		/// <summary>Stops the request before it is read (the loading thread skips it).</summary>
		internal void Cancel()
		{
			cancelRequested = true;
		}

		/// <summary>Main thread: the request is dropped; its handlers and awaiting code never run.</summary>
		internal void MarkCancelled()
		{
			lock (gate)
			{
				cancelRequested = true;
				state = RequestState.Cancelled;
				completed = null;
				continuations = null;
				loaded = null;
				Summary = "cancelled";
			}
		}

		/// <summary>An awaiter's continuation: run when the request completes, on the main thread.</summary>
		internal void AddContinuation(Action continuation)
		{
			lock (gate)
			{
				if (state < RequestState.Done)
				{
					(continuations ??= new List<Action>()).Add(continuation);
					return;
				}

				if (state == RequestState.Cancelled)
				{
					return;
				}
			}

			// Done meanwhile (awaited off the main thread): continues on the main thread.
			SceneManager.PostToMainThread(continuation);
		}

		#endregion

		#region PrivateMethods

		private void Invoke(Action<ResourceRequest> handler)
		{
			try
			{
				handler(this);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}
		}

		private static long Milliseconds(long ticks)
		{
			return ticks * 1000 / Stopwatch.Frequency;
		}

		#endregion
	}

	/// <summary>A background load of an asset of type <typeparamref name="T" /> (<see cref="Resources.LoadAsync{T}" />).</summary>
	public sealed class ResourceRequest<T> : ResourceRequest
		where T : class
	{
		#region PublicFields

		/// <summary>The asset once the request is done; null before, and when nothing was found.</summary>
		public new T Asset => base.Asset as T;

		#endregion

		#region Constructors

		internal ResourceRequest(string path)
			: base(path, typeof(T))
		{ }

		#endregion

		#region PublicMethods

		/// <summary>Lets <c>await</c> wait for the request (continues on the main thread with the asset).</summary>
		public new ResourceRequestAwaiter<T> GetAwaiter()
		{
			return new ResourceRequestAwaiter<T>(this);
		}

		#endregion
	}

	/// <summary>Awaits a <see cref="ResourceRequest" />: the code after the await runs on the main thread when it is done.</summary>
	public readonly struct ResourceRequestAwaiter : INotifyCompletion
	{
		private readonly ResourceRequest request;

		internal ResourceRequestAwaiter(ResourceRequest request)
		{
			this.request = request;
		}

		public bool IsCompleted => request.IsDone;

		public object GetResult()
		{
			return request.Asset;
		}

		public void OnCompleted(Action continuation)
		{
			request.AddContinuation(continuation);
		}
	}

	/// <summary>Awaits a <see cref="ResourceRequest{T}" />: the await gives the asset, on the main thread.</summary>
	public readonly struct ResourceRequestAwaiter<T> : INotifyCompletion
		where T : class
	{
		private readonly ResourceRequest<T> request;

		internal ResourceRequestAwaiter(ResourceRequest<T> request)
		{
			this.request = request;
		}

		public bool IsCompleted => request.IsDone;

		public T GetResult()
		{
			return request.Asset;
		}

		public void OnCompleted(Action continuation)
		{
			request.AddContinuation(continuation);
		}
	}
}
