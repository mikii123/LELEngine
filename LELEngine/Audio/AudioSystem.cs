using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace LELEngine.Audio
{
	/// <summary>A bank loading in the background (<see cref="AudioSystem.LoadBankAsync" />).</summary>
	public sealed class BankLoadOperation
	{
		#region PublicFields

		public string Name { get; internal set; }
		public bool IsDone { get; internal set; }

		/// <summary>The bank once loaded, null when the load failed.</summary>
		public LoadedBank Bank { get; internal set; }

		public Exception Error { get; internal set; }

		/// <summary>Raised on the main thread when the load is done (a handler added after that runs at once).</summary>
		public event Action<BankLoadOperation> Completed
		{
			add
			{
				if (IsDone)
				{
					value?.Invoke(this);
				}
				else
				{
					completed += value;
				}
			}
			remove => completed -= value;
		}

		#endregion

		#region PrivateFields

		private Action<BankLoadOperation> completed;

		#endregion

		#region InternalMethods

		internal void Finish()
		{
			IsDone = true;
			Action<BankLoadOperation> handlers = completed;
			completed = null;
			handlers?.Invoke(this);
		}

		#endregion
	}

	/// <summary>
	///     The audio system (FMOD Studio's system, in the engine): loads banks (from files in a build, from the audio
	///     project in the editor), creates event instances, sets global parameters, bus volumes and the listener, and
	///     delivers the mixer's callbacks on the main thread. Hosts call <see cref="Initialize" /> once,
	///     <see cref="Update" /> every frame and <see cref="Shutdown" /> at the end. Every public call is for the main
	///     thread (the audio thread is told through commands; nothing here blocks on it).
	/// </summary>
	public static class AudioSystem
	{
		#region PublicFields

		public static bool IsInitialized { get; private set; }

		/// <summary>No device and no audio thread: <see cref="RenderOffline" /> drives the mixer (tests, offline rendering).</summary>
		public static bool IsOffline { get; private set; }

		/// <summary>The folder bank files are loaded from (a build's Data/Audio/Banks).</summary>
		public static string BanksFolder { get; set; }

		/// <summary>Relative folder of the banks inside a build's data folder.</summary>
		public const string BanksSubfolder = "Audio/Banks";

		/// <summary>The audio project banks are built from in memory (the editor); null in a build.</summary>
		public static AudioProject SourceProject { get; private set; }

		public static IReadOnlyList<LoadedBank> LoadedBanks => banks;

		public static event Action<LoadedBank> BankLoaded;
		public static event Action<string> BankUnloaded;

		/// <summary>The mixer (audio thread object; read its published fields only).</summary>
		public static Mixer Mixer => mixer;

		public static string DeviceStatus => output?.Status ?? (IsOffline ? "offline" : "not started");
		public static bool IsDeviceOpen => output != null && output.IsDeviceOpen;
		public static float CpuLoad => output?.CpuLoad ?? 0f;
		public static int Underruns => output?.Underruns ?? 0;
		public static int ActiveVoices => mixer?.ActiveVoices ?? 0;
		public static int VirtualVoices => mixer?.VirtualVoices ?? 0;
		public static int ActiveInstances => mixer?.ActiveInstances ?? 0;
		public static int StreamUnderruns => mixer?.StreamUnderruns ?? 0;

		/// <summary>Handles the game holds (released ones that stopped are gone).</summary>
		public static int InstanceHandles => handles.Count;

		/// <summary>Every instance handle alive (main thread): the editor's live view of what the game plays.</summary>
		public static IReadOnlyCollection<EventInstance> Instances => handles.Values;

		public static Vector3 ListenerPosition { get; private set; }

		#endregion

		#region PrivateFields

		private static Mixer mixer;
		private static AudioOutput output;
		private static AudioSettings settings = new AudioSettings();
		private static readonly List<LoadedBank> banks = new List<LoadedBank>();
		private static readonly Dictionary<string, CompiledEvent> eventsByGuid = new Dictionary<string, CompiledEvent>(StringComparer.Ordinal);
		private static readonly Dictionary<string, CompiledEvent> eventsByPath = new Dictionary<string, CompiledEvent>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<int, EventInstance> handles = new Dictionary<int, EventInstance>();
		private static readonly Dictionary<int, SnapshotInstance> snapshotHandles = new Dictionary<int, SnapshotInstance>();
		private static readonly Dictionary<string, AudioSnapshot> snapshotsByName = new Dictionary<string, AudioSnapshot>(StringComparer.OrdinalIgnoreCase);
		private static int nextSnapshotId = 1;
		private static readonly Dictionary<string, AudioParameter> globalsByName = new Dictionary<string, AudioParameter>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, float> globalValues = new Dictionary<string, float>(StringComparer.Ordinal);
		private static readonly Dictionary<string, AudioBus> busesByName = new Dictionary<string, AudioBus>(StringComparer.OrdinalIgnoreCase);
		private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);
		private static readonly ConcurrentQueue<Action> mainThreadActions = new ConcurrentQueue<Action>();
		private static readonly List<BankLoadOperation> pendingLoads = new List<BankLoadOperation>();
		private static int nextInstanceId = 1;
		private static int nextSeed = 1;
		private static bool listenerSetThisFrame;
		private static MixerGlobals lastGlobals;
		private static List<int> sweep;

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- lifecycle

		/// <summary>
		///     Starts the mixer and the audio thread with the device (unless <paramref name="offline" />, or the settings
		///     disable the device: then the mixer runs silently, paced by the thread).
		/// </summary>
		public static void Initialize(AudioSettings audioSettings = null, bool offline = false)
		{
			if (IsInitialized)
			{
				return;
			}

			settings = audioSettings ?? ProjectSettings.Current.Audio ?? new AudioSettings();
			IsOffline = offline;
			mixer = new Mixer();
			if (settings.MaxVoices > 0)
			{
				mixer.MaxVoices = settings.MaxVoices;
			}

			mixer.Post(MixerCommandKind.SetMasterVolume, 0, AudioMath.DbToGain(settings.MasterVolumeDb));
			mixer.Post(new MixerCommand { Kind = MixerCommandKind.SetDoppler, V0 = new Vector3(settings.SpeedOfSound, settings.DopplerScale, 0f) });
			if (!offline)
			{
				output = new AudioOutput(mixer, new AudioOutputOptions { BufferMilliseconds = Math.Clamp(settings.OutputBufferMs, 10, 500), Disabled = settings.Disabled });
				output.Start();
			}

			IsInitialized = true;
			Debug.Log("[Audio] Initialized (" + (offline ? "offline" : settings.Disabled ? "no device" : "device on the audio thread") + ", " + Mixer.SampleRate + " Hz, " + mixer.MaxVoices + " voices)");
		}

		/// <summary>Applies the settings that can change while the system runs (master volume, voice count).</summary>
		public static void ApplySettings(AudioSettings audioSettings)
		{
			if (!IsInitialized || audioSettings == null)
			{
				return;
			}

			settings = audioSettings;
			mixer.Post(MixerCommandKind.SetMasterVolume, 0, AudioMath.DbToGain(audioSettings.MasterVolumeDb));
			mixer.Post(new MixerCommand { Kind = MixerCommandKind.SetDoppler, V0 = new Vector3(audioSettings.SpeedOfSound, audioSettings.DopplerScale, 0f) });
			int voices = audioSettings.MaxVoices > 0 ? audioSettings.MaxVoices : SourceProject?.MaxVoices ?? (banks.Count > 0 ? banks[0].Document.MaxVoices : 64);
			mixer.Post(MixerCommandKind.SetMaxVoices, 0, 0f, false, null, Math.Max(1, voices));
		}

		/// <summary>Stops everything, closes the device and drops the banks.</summary>
		public static void Shutdown()
		{
			if (!IsInitialized)
			{
				return;
			}

			output?.Stop();
			output = null;
			AudioStreamer.Shutdown();
			foreach (EventInstance handle in handles.Values)
			{
				handle.MarkGone();
			}

			handles.Clear();
			foreach (SnapshotInstance snapshot in snapshotHandles.Values)
			{
				snapshot.MarkStopped();
			}

			snapshotHandles.Clear();
			snapshotsByName.Clear();
			banks.Clear();
			eventsByGuid.Clear();
			eventsByPath.Clear();
			globalsByName.Clear();
			globalValues.Clear();
			busesByName.Clear();
			pendingLoads.Clear();
			while (mainThreadActions.TryDequeue(out _))
			{ }

			mixer = null;
			SourceProject = null;
			lastGlobals = null;
			IsInitialized = false;
		}

		/// <summary>Once per frame on the main thread: callbacks, finished handles, bank loads, the listener fallback.</summary>
		public static void Update()
		{
			if (!IsInitialized)
			{
				return;
			}

			Action action;
			while (mainThreadActions.TryDequeue(out action))
			{
				try
				{
					action();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			AudioNotification notification;
			while (mixer.Notifications.TryDequeue(out notification))
			{
				if (notification.Kind == AudioNotificationKind.SnapshotStopped)
				{
					SnapshotInstance snapshot;
					if (snapshotHandles.TryGetValue(notification.InstanceId, out snapshot))
					{
						snapshotHandles.Remove(notification.InstanceId);
						snapshot.MarkStopped();
					}

					continue;
				}

				EventInstance handle;
				if (!handles.TryGetValue(notification.InstanceId, out handle))
				{
					continue;
				}

				handle.Dispatch(notification);
				if ((notification.Kind == AudioNotificationKind.Stopped || notification.Kind == AudioNotificationKind.StartRefused) && handle.Released)
				{
					handle.MarkGone();
					handles.Remove(notification.InstanceId);
				}
			}

			// Handles released after their instance had stopped already (the notification came earlier).
			if (handles.Count > 0)
			{
				sweep ??= new List<int>();
				sweep.Clear();
				foreach (KeyValuePair<int, EventInstance> entry in handles)
				{
					if (entry.Value.Released && entry.Value.State == PlaybackState.Stopped)
					{
						sweep.Add(entry.Key);
					}
				}

				foreach (int id in sweep)
				{
					handles[id].MarkGone();
					handles.Remove(id);
				}
			}

			if (!listenerSetThisFrame)
			{
				Camera camera = Camera.main;
				Transform transform = camera != null && !camera.IsDestroyed ? camera.transform : null;
				if (transform != null)
				{
					SetListener(transform.position, transform.forward, transform.up, Vector3.Zero);
				}
			}

			listenerSetThisFrame = false;
		}

		/// <summary>Offline mode: renders <paramref name="frames" /> stereo frames into <paramref name="buffer" /> (commands apply first).</summary>
		public static void RenderOffline(float[] buffer, int frames)
		{
			if (!IsInitialized || !IsOffline)
			{
				throw new InvalidOperationException("RenderOffline needs the audio system initialized offline");
			}

			int done = 0;
			while (done < frames)
			{
				int count = Math.Min(AudioOutput.MaxBlockFrames, frames - done);
				float[] block = count == frames ? buffer : offlineBlock ??= new float[AudioOutput.MaxBlockFrames * 2];
				mixer.Render(block, count);
				if (block != buffer)
				{
					Array.Copy(block, 0, buffer, done * 2, count * 2);
				}

				done += count;
			}
		}

		private static float[] offlineBlock;

		// ---------------------------------------------------------------- banks

		/// <summary>Banks come from this project (built in memory from its clips): the editor's mode.</summary>
		public static void UseProject(AudioProject project)
		{
			SourceProject = project;
			if (project != null && mixer != null && settings.MaxVoices <= 0)
			{
				mixer.Post(MixerCommandKind.SetMaxVoices, 0, 0f, false, null, project.MaxVoices);
			}
		}

		public static bool IsBankLoaded(string name)
		{
			return FindBank(name) != null;
		}

		public static LoadedBank FindBank(string name)
		{
			foreach (LoadedBank bank in banks)
			{
				if (string.Equals(bank.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					return bank;
				}
			}

			return null;
		}

		/// <summary>Loads a bank now (from the project in the editor, from its file in a build); the loaded bank, or null with the error logged.</summary>
		public static LoadedBank LoadBank(string name)
		{
			if (!IsInitialized)
			{
				Debug.LogWarning("[Audio] LoadBank before Initialize");
				return null;
			}

			LoadedBank existing = FindBank(name);
			if (existing != null)
			{
				return existing;
			}

			long start = Stopwatch.GetTimestamp();
			LoadedBank bank;
			try
			{
				bank = ReadBank(name);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Audio] Cannot load bank '{name}': {e.Message}");
				return null;
			}

			if (bank == null)
			{
				return null;
			}

			Register(bank, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
			return bank;
		}

		/// <summary>Loads a bank in the background; the operation completes on the main thread (<see cref="Update" />).</summary>
		public static BankLoadOperation LoadBankAsync(string name)
		{
			var operation = new BankLoadOperation { Name = name };
			LoadedBank existing = IsInitialized ? FindBank(name) : null;
			if (existing != null || !IsInitialized)
			{
				operation.Bank = existing;
				operation.Error = IsInitialized ? null : new InvalidOperationException("The audio system is not initialized");
				operation.Finish();
				return operation;
			}

			foreach (BankLoadOperation pending in pendingLoads)
			{
				if (string.Equals(pending.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					return pending;
				}
			}

			pendingLoads.Add(operation);
			long start = Stopwatch.GetTimestamp();
			AudioProject project = SourceProject;
			Task.Run(() =>
			{
				LoadedBank bank = null;
				Exception error = null;
				try
				{
					bank = ReadBank(name, project);
				}
				catch (Exception e)
				{
					error = e;
				}

				mainThreadActions.Enqueue(() =>
				{
					pendingLoads.Remove(operation);
					if (bank != null && IsInitialized && FindBank(name) == null)
					{
						Register(bank, (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);
					}

					operation.Bank = bank != null ? FindBank(name) : null;
					operation.Error = error;
					if (error != null)
					{
						Debug.LogError($"[Audio] Cannot load bank '{name}': {error.Message}");
					}

					operation.Finish();
				});
			});
			return operation;
		}

		/// <summary>Loads the master bank and every bank marked for loading at start.</summary>
		public static void LoadStartupBanks()
		{
			LoadedBank master = LoadBank(AudioProject.MasterBankName);
			if (master == null || !settings.LoadBanksAtStart)
			{
				return;
			}

			foreach (AudioBank bank in master.Document.Banks)
			{
				if (bank.AutoLoad && !string.Equals(bank.Name, AudioProject.MasterBankName, StringComparison.OrdinalIgnoreCase))
				{
					LoadBank(bank.Name);
				}
			}
		}

		/// <summary>Unloads a bank: instances of its events stop at once.</summary>
		public static void UnloadBank(string name)
		{
			LoadedBank bank = FindBank(name);
			if (bank == null)
			{
				return;
			}

			banks.Remove(bank);
			foreach (CompiledEvent compiled in bank.Events)
			{
				CompiledEvent registered;
				if (eventsByGuid.TryGetValue(compiled.Id, out registered) && registered == compiled)
				{
					eventsByGuid.Remove(compiled.Id);
					eventsByPath.Remove(compiled.Path ?? "");
				}
			}

			mixer.Post(MixerCommandKind.UnregisterBank, 0, 0f, false, bank.Name);
			RebuildGlobals();
			BankUnloaded?.Invoke(name);
			Debug.Log("[Audio] Unloaded bank " + name);
		}

		/// <summary>
		///     The editor changed the source project: the loaded banks are rebuilt from it and every event description
		///     replaced live (playing instances take the new description).
		/// </summary>
		public static void ReloadFromProject()
		{
			if (!IsInitialized || SourceProject == null)
			{
				return;
			}

			var names = new List<string>();
			foreach (LoadedBank bank in banks)
			{
				names.Add(bank.Name);
			}

			foreach (string name in names)
			{
				AudioBank definition = SourceProject.FindBankByName(name);
				LoadedBank old = FindBank(name);
				if (definition == null)
				{
					UnloadBank(name);
					continue;
				}

				LoadedBank rebuilt;
				try
				{
					rebuilt = AudioBankCompiler.BuildInMemory(SourceProject, definition);
				}
				catch (Exception e)
				{
					Debug.LogError($"[Audio] Cannot rebuild bank '{name}': {e.Message}");
					continue;
				}

				banks[banks.IndexOf(old)] = rebuilt;
				foreach (CompiledEvent previous in old.Events)
				{
					if (rebuilt.FindEvent(previous.Id) == null)
					{
						eventsByGuid.Remove(previous.Id);
						eventsByPath.Remove(previous.Path ?? "");
						mixer.Post(MixerCommandKind.UnregisterEvent, 0, 0f, false, previous.Id);
					}
				}

				foreach (CompiledEvent compiled in rebuilt.Events)
				{
					RegisterEvent(compiled);
				}

				foreach (string warning in rebuilt.Warnings)
				{
					WarnOnce("bank:" + name + ":" + warning, "[Audio] " + warning);
				}
			}

			RebuildGlobals();
		}

		// ---------------------------------------------------------------- events

		/// <summary>The description of a loaded event, or null.</summary>
		public static CompiledEvent FindEvent(AudioEventReference reference)
		{
			CompiledEvent compiled;
			if (!string.IsNullOrEmpty(reference.Guid) && eventsByGuid.TryGetValue(reference.Guid, out compiled))
			{
				return compiled;
			}

			if (!string.IsNullOrEmpty(reference.Path) && eventsByPath.TryGetValue(reference.Path, out compiled))
			{
				return compiled;
			}

			return null;
		}

		public static CompiledEvent FindEvent(string pathOrGuid)
		{
			return FindEvent(new AudioEventReference { Guid = pathOrGuid, Path = pathOrGuid });
		}

		/// <summary>Every loaded event, sorted by path.</summary>
		public static List<CompiledEvent> LoadedEvents
		{
			get
			{
				var result = new List<CompiledEvent>(eventsByGuid.Values);
				result.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
				return result;
			}
		}

		/// <summary>A new, not yet started instance of an event; null (logged once) when the event is not loaded.</summary>
		public static EventInstance CreateInstance(AudioEventReference reference)
		{
			if (!IsInitialized)
			{
				return null;
			}

			CompiledEvent compiled = FindEvent(reference);
			if (compiled == null)
			{
				if (!reference.IsNull)
				{
					WarnOnce("event:" + reference, $"[Audio] Event '{reference}' is not loaded (is its bank loaded?)");
				}

				return null;
			}

			return CreateInstance(compiled);
		}

		public static EventInstance CreateInstance(string pathOrGuid)
		{
			return CreateInstance(new AudioEventReference { Guid = pathOrGuid, Path = pathOrGuid });
		}

		public static EventInstance CreateInstance(CompiledEvent compiled)
		{
			if (!IsInitialized || compiled == null)
			{
				return null;
			}

			var playback = new EventPlayback(nextInstanceId++, compiled, 0, new Random(nextSeed++), mixer);
			var handle = new EventInstance(playback);
			handles[playback.Id] = handle;
			mixer.Post(MixerCommandKind.CreateInstance, playback.Id, 0f, false, playback);
			return handle;
		}

		/// <summary>Starts an instance that releases itself when it stops; with a position for spatialized events.</summary>
		public static EventInstance PlayOneShot(AudioEventReference reference, Vector3? position = null)
		{
			EventInstance instance = CreateInstance(reference);
			if (instance == null)
			{
				return null;
			}

			if (position.HasValue)
			{
				instance.Set3DAttributes(position.Value);
			}

			instance.Start();
			instance.Release();
			return instance;
		}

		public static EventInstance PlayOneShot(string pathOrGuid, Vector3? position = null)
		{
			return PlayOneShot(new AudioEventReference { Guid = pathOrGuid, Path = pathOrGuid }, position);
		}

		/// <summary>
		///     Plays a clip as it is (the editor's asset preview): a one-shot instance of an event made for the clip, on the
		///     master bus. Null when the clip cannot be read.
		/// </summary>
		public static EventInstance PreviewClip(AudioClip clip)
		{
			if (!IsInitialized || clip == null)
			{
				return null;
			}

			SampleData sample = AudioBankCompiler.SampleFromClip(clip, out string error);
			if (sample == null)
			{
				WarnOnce("preview:" + clip.AssetPath, $"[Audio] Cannot preview {clip.AssetPath}: {error}");
				return null;
			}

			var audioEvent = new AudioEvent { Id = "preview:" + clip.AssetPath, Path = "__preview/" + clip.name };
			var track = AudioProject.CreateTrack("Audio");
			track.Instruments.Add(new SingleInstrument { Id = "clip", Clip = AudioClipReference.FromPath(clip.AssetPath), Start = 0.0, Length = Math.Max(0.1, clip.Duration), Async = true });
			audioEvent.Tracks.Add(track);
			CompiledEvent compiled = EventCompiler.Compile(audioEvent, null, new SingleSampleResolver(sample));
			compiled.BankName = "__preview";
			mixer.Post(MixerCommandKind.RegisterEvent, 0, 0f, false, compiled);
			EventInstance instance = CreateInstance(compiled);
			instance.Start();
			instance.Release();
			return instance;
		}

		private sealed class SingleSampleResolver : ISampleResolver
		{
			private readonly SampleData sample;

			public SingleSampleResolver(SampleData sample)
			{
				this.sample = sample;
			}

			public SampleData Resolve(AudioClipReference clip)
			{
				return sample;
			}
		}

		/// <summary>Stops every instance.</summary>
		public static void StopAll(AudioStopMode mode = AudioStopMode.Immediate, bool releaseHandles = true)
		{
			if (!IsInitialized)
			{
				return;
			}

			mixer.Post(MixerCommandKind.StopAll, 0, 0f, mode == AudioStopMode.Immediate, null, releaseHandles ? 1 : 0);
			if (releaseHandles)
			{
				foreach (EventInstance handle in handles.Values)
				{
					handle.Released = true;
				}
			}
		}

		/// <summary>Freezes (or resumes) every instance: the editor's pause.</summary>
		public static void PauseAll(bool paused)
		{
			if (IsInitialized)
			{
				mixer.Post(MixerCommandKind.PauseAll, 0, 0f, paused);
			}
		}

		// ---------------------------------------------------------------- snapshots

		/// <summary>The loaded snapshots (the project's, in priority order).</summary>
		public static IEnumerable<AudioSnapshot> Snapshots => snapshotsByName.Values;

		/// <summary>A handle of a snapshot by name, not started; null (logged once) when there is no such snapshot.</summary>
		public static SnapshotInstance CreateSnapshotInstance(string snapshotName)
		{
			if (!IsInitialized)
			{
				return null;
			}

			AudioSnapshot definition;
			if (string.IsNullOrEmpty(snapshotName) || !snapshotsByName.TryGetValue(snapshotName, out definition))
			{
				WarnOnce("snapshot:" + snapshotName, $"[Audio] There is no snapshot '{snapshotName}'");
				return null;
			}

			return new SnapshotInstance(nextSnapshotId++, definition);
		}

		/// <summary>Starts a snapshot by name at an intensity; the handle stops it. Null when the snapshot is unknown.</summary>
		public static SnapshotInstance StartSnapshot(string snapshotName, float intensity = 1f)
		{
			SnapshotInstance instance = CreateSnapshotInstance(snapshotName);
			instance?.Start(intensity);
			return instance;
		}

		/// <summary>Fades out every snapshot the game started (and the ones instruments hold).</summary>
		public static void StopAllSnapshots(bool immediate = false)
		{
			if (IsInitialized)
			{
				mixer.Post(MixerCommandKind.StopAllSnapshots, 0, 0f, immediate);
			}
		}

		/// <summary>Snapshot handles started and not yet faded out.</summary>
		public static int ActiveSnapshots => mixer?.ActiveSnapshotCount ?? 0;

		internal static void RegisterSnapshotHandle(SnapshotInstance instance)
		{
			snapshotHandles[instance.Id] = instance;
		}

		// ---------------------------------------------------------------- globals

		public static void SetGlobalParameter(string name, float value, bool ignoreSeekSpeed = false)
		{
			if (!IsInitialized)
			{
				return;
			}

			AudioParameter parameter;
			if (!globalsByName.TryGetValue(name, out parameter))
			{
				WarnOnce("global:" + name, $"[Audio] There is no global parameter '{name}'");
				return;
			}

			globalValues[parameter.Id] = parameter.Clamp(value);
			mixer.Post(MixerCommandKind.SetGlobalParameter, 0, value, ignoreSeekSpeed, parameter.Id);
		}

		/// <summary>The value last set for a global parameter (its default before), NaN when unknown.</summary>
		public static float GetGlobalParameter(string name)
		{
			AudioParameter parameter;
			if (!globalsByName.TryGetValue(name, out parameter))
			{
				return float.NaN;
			}

			float value;
			return globalValues.TryGetValue(parameter.Id, out value) ? value : parameter.Default;
		}

		public static IEnumerable<AudioParameter> GlobalParameters => globalsByName.Values;

		/// <summary>Volume of a bus by name or path ("Music", "Master/Music"), decibels.</summary>
		public static void SetBusVolume(string bus, float volumeDb)
		{
			AudioBus definition = FindBusDefinition(bus);
			if (definition != null)
			{
				mixer.Post(MixerCommandKind.SetBusVolume, 0, AudioMath.DbToGain(volumeDb), false, definition.Id);
			}
		}

		public static void SetBusMute(string bus, bool mute)
		{
			AudioBus definition = FindBusDefinition(bus);
			if (definition != null)
			{
				mixer.Post(MixerCommandKind.SetBusMute, 0, 0f, mute, definition.Id);
			}
		}

		/// <summary>The master volume (decibels), on top of the master bus.</summary>
		public static void SetMasterVolume(float volumeDb)
		{
			if (IsInitialized)
			{
				mixer.Post(MixerCommandKind.SetMasterVolume, 0, AudioMath.DbToGain(volumeDb));
			}
		}

		/// <summary>Where the listener is; called every frame by the listener component (or the editor's scene camera).</summary>
		public static void SetListener(Vector3 position, Vector3 forward, Vector3 up, Vector3 velocity)
		{
			if (!IsInitialized)
			{
				return;
			}

			ListenerPosition = position;
			listenerSetThisFrame = true;
			mixer.Post(new MixerCommand { Kind = MixerCommandKind.SetListener, V0 = position, V1 = forward, V2 = up, V3 = velocity });
		}

		// ---------------------------------------------------------------- internals shared with the handles

		internal static void Post(MixerCommandKind kind, int id = 0, float value = 0f, bool flag = false, object payload = null)
		{
			mixer?.Post(kind, id, value, flag, payload);
		}

		internal static void Post(MixerCommand command)
		{
			mixer?.Post(command);
		}

		internal static void WarnOnce(string key, string message)
		{
			if (warned.Add(key))
			{
				Debug.LogWarning(message);
			}
		}

		#endregion

		#region PrivateMethods

		private static LoadedBank ReadBank(string name)
		{
			return ReadBank(name, SourceProject);
		}

		private static LoadedBank ReadBank(string name, AudioProject project)
		{
			if (project != null)
			{
				AudioBank definition = project.FindBankByName(name);
				if (definition == null)
				{
					Debug.LogWarning($"[Audio] The audio project has no bank '{name}'");
					return null;
				}

				return AudioBankCompiler.BuildInMemory(project, definition);
			}

			string folder = BanksFolder ?? Path.Combine(AssetDatabase.Root, BanksSubfolder.Replace('/', Path.DirectorySeparatorChar));
			string file = Path.Combine(folder, name + AudioBankFile.Extension);
			if (!File.Exists(file))
			{
				Debug.LogWarning($"[Audio] Bank file not found: {file}");
				return null;
			}

			return AudioBankFile.Load(file);
		}

		private static void Register(LoadedBank bank, double milliseconds)
		{
			banks.Add(bank);
			foreach (CompiledEvent compiled in bank.Events)
			{
				RegisterEvent(compiled);
			}

			RebuildGlobals();
			foreach (string warning in bank.Warnings)
			{
				WarnOnce("bank:" + bank.Name + ":" + warning, "[Audio] " + warning);
			}

			Debug.Log($"[Audio] Loaded bank {bank.Name}: {bank.Events.Count} events, {bank.SamplesByPath.Count} sounds ({bank.MemoryBytes / 1024.0 / 1024.0:0.0} MB in memory, {bank.StreamingCount} streamed) in {milliseconds:0} ms" + (bank.FilePath != null ? "" : " from the project"));
			BankLoaded?.Invoke(bank);
		}

		private static void RegisterEvent(CompiledEvent compiled)
		{
			eventsByGuid[compiled.Id] = compiled;
			if (!string.IsNullOrEmpty(compiled.Path))
			{
				eventsByPath[compiled.Path] = compiled;
			}

			mixer.Post(MixerCommandKind.RegisterEvent, 0, 0f, false, compiled);
		}

		/// <summary>Global parameters and buses from the loaded banks (the first bank's view of them; all carry the same).</summary>
		private static void RebuildGlobals()
		{
			var globals = new MixerGlobals { MaxVoices = settings.MaxVoices > 0 ? settings.MaxVoices : SourceProject?.MaxVoices ?? (banks.Count > 0 ? banks[0].Document.MaxVoices : mixer.MaxVoices) };
			globalsByName.Clear();
			busesByName.Clear();
			var seenParameters = new HashSet<string>(StringComparer.Ordinal);
			var seenBuses = new HashSet<string>(StringComparer.Ordinal);
			IEnumerable<BankDocument> documents = Documents();
			foreach (BankDocument document in documents)
			{
				foreach (AudioParameter parameter in document.GlobalParameters)
				{
					if (parameter != null && seenParameters.Add(parameter.Id))
					{
						globals.Parameters.Add(parameter);
						globalsByName[parameter.Name] = parameter;
						if (!globalValues.ContainsKey(parameter.Id))
						{
							globalValues[parameter.Id] = parameter.Clamp(parameter.Default);
						}
					}
				}
			}

			BankDocument first = null;
			foreach (BankDocument document in documents)
			{
				first = document;
				break;
			}

			snapshotsByName.Clear();
			if (first != null)
			{
				AudioProject stub = LoadedBank.StubProject(first);
				foreach (AudioBus bus in stub.BusesInTreeOrder())
				{
					if (seenBuses.Add(bus.Id))
					{
						globals.Buses.Add(bus);
						busesByName[bus.Name] = bus;
						busesByName[stub.BusPath(bus)] = bus;
					}
				}

				foreach (AudioSnapshot snapshot in first.Snapshots)
				{
					if (snapshot != null && !string.IsNullOrEmpty(snapshot.Id))
					{
						globals.Snapshots.Add(snapshot);
						snapshotsByName[snapshot.Name ?? snapshot.Id] = snapshot;
					}
				}
			}
			else
			{
				var master = new AudioBus { Id = AudioProject.MasterBusId, Name = "Master" };
				globals.Buses.Add(master);
				busesByName["Master"] = master;
			}

			lastGlobals = globals;
			mixer.Post(MixerCommandKind.SetGlobals, 0, 0f, false, globals);
		}

		private static IEnumerable<BankDocument> Documents()
		{
			if (SourceProject != null && banks.Count == 0)
			{
				yield break;
			}

			foreach (LoadedBank bank in banks)
			{
				yield return bank.Document;
			}
		}

		private static AudioBus FindBusDefinition(string nameOrPath)
		{
			if (!IsInitialized || string.IsNullOrEmpty(nameOrPath))
			{
				return null;
			}

			AudioBus bus;
			if (busesByName.TryGetValue(nameOrPath, out bus))
			{
				return bus;
			}

			WarnOnce("bus:" + nameOrPath, $"[Audio] There is no bus '{nameOrPath}'");
			return null;
		}

		#endregion
	}
}
