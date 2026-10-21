using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine.Audio
{
	/// <summary>
	///     The audio project asset (".lelaudio" document: readable JSON in the editor, binary in builds): every event
	///     with its tracks and logic, the global parameters, the mixer buses and the banks. The Audio Studio window
	///     edits it; game builds compile it into bank files (<c>Audio/Banks/&lt;Bank&gt;.bank</c>) the player loads
	///     without this asset. A game project has one audio project, found by <see cref="AudioSettings.Project" /> or
	///     as the only ".lelaudio" file under Assets.
	/// </summary>
	public sealed class AudioProject : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".lelaudio";
		public const string MasterBankId = "master";
		public const string MasterBusId = "master";
		public const string MasterBankName = "Master";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;

		[NonSerialized] public string name;

		public List<AudioEvent> Events = new List<AudioEvent>();

		/// <summary>Event folders that exist without events in them ("Music/Combat").</summary>
		public List<string> Folders = new List<string>();

		public List<AudioParameter> GlobalParameters = new List<AudioParameter>();
		public List<AudioBus> Buses = new List<AudioBus>();
		public List<AudioBank> Banks = new List<AudioBank>();

		/// <summary>Mixer snapshots, in priority order (the first wins over the ones below it).</summary>
		public List<AudioSnapshot> Snapshots = new List<AudioSnapshot>();

		public int FormatVersion = 1;

		/// <summary>Real voices mixed at once at most; quieter ones beyond that go virtual (advance silently).</summary>
		public int MaxVoices = 64;

		public string Notes = "";

		/// <summary>Bumped by every change (<see cref="MarkChanged" />).</summary>
		[NonSerialized] public int Version = 1;

		/// <summary>Raised after <see cref="MarkChanged" />.</summary>
		public event Action Changed;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, AudioProject> cache = new Dictionary<string, AudioProject>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, DateTime> fileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		/// <summary>Loads (or returns the cached) project at a root-relative path. Unreadable files give an empty project.</summary>
		public static AudioProject Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			assetPath = assetPath.Replace('\\', '/');
			lock (cache)
			{
				AudioProject cached;
				if (cache.TryGetValue(assetPath, out cached))
				{
					return cached;
				}
			}

			AudioProject project = null;
			string file = AssetDatabase.ToAbsolute(assetPath);
			try
			{
				byte[] data = File.ReadAllBytes(file);
				if (Documents.IsDocument(data))
				{
					project = Documents.ReadObject(data, typeof(AudioProject), null, new SerializationContext { Location = assetPath }) as AudioProject;
				}
				else
				{
					Debug.LogError($"[Audio] {assetPath} is not an audio project document");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Audio] Cannot load {assetPath}: {e.Message}");
			}

			project = project ?? new AudioProject();
			project.AssetPath = assetPath;
			project.name = Path.GetFileNameWithoutExtension(assetPath);
			project.EnsureDefaults();
			lock (cache)
			{
				cache[assetPath] = project;
				fileTimes[assetPath] = SafeWriteTime(file);
			}

			return project;
		}

		/// <summary>The cached project of an asset path when it was loaded (null otherwise).</summary>
		public static AudioProject FindLoaded(string assetPath)
		{
			lock (cache)
			{
				AudioProject project;
				return assetPath != null && cache.TryGetValue(assetPath.Replace('\\', '/'), out project) ? project : null;
			}
		}

		/// <summary>Every loaded project.</summary>
		public static List<AudioProject> Loaded
		{
			get
			{
				lock (cache)
				{
					return new List<AudioProject>(cache.Values);
				}
			}
		}

		/// <summary>Writes the project to its asset file (or a new one) in <see cref="Documents.FileFormat" />.</summary>
		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The audio project has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			string file = AssetDatabase.ToAbsolute(AssetPath);
			Documents.WriteObjectFile(file, this);
			lock (cache)
			{
				cache[AssetPath] = this;
				fileTimes[AssetPath] = SafeWriteTime(file);
			}
		}

		/// <summary>The project as a document (undo snapshots, copies).</summary>
		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name ?? "AudioProject" });
		}

		/// <summary>Replaces the content from a document written by <see cref="ToBytes" /> and marks the project changed.</summary>
		public void ApplyBytes(byte[] data)
		{
			Events.Clear();
			Folders.Clear();
			GlobalParameters.Clear();
			Buses.Clear();
			Banks.Clear();
			Snapshots.Clear();
			Documents.ReadObject(data, typeof(AudioProject), this, new SerializationContext { Location = name ?? "AudioProject" });
			EnsureDefaults();
			MarkChanged();
		}

		/// <summary>Independent copy (same asset path, own objects).</summary>
		public AudioProject Clone()
		{
			var copy = Documents.ReadObject(ToBytes(), typeof(AudioProject), null) as AudioProject ?? new AudioProject();
			copy.AssetPath = AssetPath;
			copy.name = name;
			copy.EnsureDefaults();
			return copy;
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
				fileTimes.Clear();
			}
		}

		/// <summary>Drops a cached project (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void Forget(string assetPath)
		{
			lock (cache)
			{
				cache.Remove(assetPath.Replace('\\', '/'));
				fileTimes.Remove(assetPath.Replace('\\', '/'));
			}
		}

		/// <summary>
		///     Loaded projects whose files changed outside (git, a text editor) read their files again (unless
		///     <paramref name="skip" /> says a project must keep its unsaved edits). Returns the reloaded ones.
		/// </summary>
		public static List<AudioProject> ReloadChanged(Func<AudioProject, bool> skip = null)
		{
			var reloaded = new List<AudioProject>();
			List<KeyValuePair<string, AudioProject>> entries;
			lock (cache)
			{
				entries = new List<KeyValuePair<string, AudioProject>>(cache);
			}

			foreach (KeyValuePair<string, AudioProject> entry in entries)
			{
				string file = AssetDatabase.ToAbsolute(entry.Key);
				if (!File.Exists(file))
				{
					continue;
				}

				DateTime time = SafeWriteTime(file);
				DateTime known;
				lock (cache)
				{
					fileTimes.TryGetValue(entry.Key, out known);
				}

				if (time == known || (skip != null && skip(entry.Value)))
				{
					continue;
				}

				try
				{
					entry.Value.ApplyBytes(File.ReadAllBytes(file));
					lock (cache)
					{
						fileTimes[entry.Key] = time;
					}

					reloaded.Add(entry.Value);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Audio] {entry.Key} changed on disk but cannot be read: {e.Message}");
				}
			}

			return reloaded;
		}

		/// <summary>The asset path of the game project's audio project: the one project settings name, else the only ".lelaudio" file; null when there is none.</summary>
		public static string FindProjectPath()
		{
			string guid = ProjectSettings.Current.Audio?.Project;
			string path = string.IsNullOrEmpty(guid) ? null : AssetDatabase.GetPath(guid);
			if (path != null)
			{
				return path;
			}

			List<string> all = AssetDatabase.GetAllAssetPaths(Extension);
			return all.Count > 0 ? all[0] : null;
		}

		// ---------------------------------------------------------------- content

		/// <summary>A new project with the master bus and bank.</summary>
		public static AudioProject CreateDefault(string projectName)
		{
			var project = new AudioProject { name = projectName };
			project.EnsureDefaults();
			return project;
		}

		/// <summary>Random id for a new object.</summary>
		public static string NewId()
		{
			return Guid.NewGuid().ToString("N");
		}

		/// <summary>Makes sure the master bus and bank exist and every object has an id.</summary>
		public void EnsureDefaults()
		{
			if (FindBus(MasterBusId) == null)
			{
				Buses.Insert(0, new AudioBus { Id = MasterBusId, Name = "Master", ParentId = null });
			}

			if (FindBank(MasterBankId) == null)
			{
				Banks.Insert(0, new AudioBank { Id = MasterBankId, Name = MasterBankName, AutoLoad = true });
			}

			foreach (AudioBus bus in Buses)
			{
				bus.Id ??= NewId();
				bus.Effects.RemoveAll(e => e == null);
				foreach (AudioEffect effect in bus.Effects)
				{
					effect.Id ??= NewId();
				}
			}

			foreach (AudioBank bank in Banks)
			{
				bank.Id ??= NewId();
			}

			foreach (AudioParameter parameter in GlobalParameters)
			{
				parameter.Id ??= NewId();
			}

			Snapshots.RemoveAll(s => s == null);
			foreach (AudioSnapshot snapshot in Snapshots)
			{
				snapshot.Id ??= NewId();
				snapshot.Intensity ??= new AudioEnvelope();
				snapshot.Buses.RemoveAll(b => b == null);
				foreach (SnapshotBusValue bus in snapshot.Buses)
				{
					bus.Effects ??= new List<SnapshotEffectValue>();
				}
			}

			foreach (AudioEvent audioEvent in Events)
			{
				audioEvent.Id ??= NewId();
				audioEvent.Master ??= new AudioTrack { Id = "master", Name = "Master" };
				audioEvent.Master.Id ??= "master";
				audioEvent.Spatializer ??= new AudioSpatializer();
				audioEvent.Envelope ??= new AudioEnvelope();
				audioEvent.BankId ??= MasterBankId;
				audioEvent.BusId ??= MasterBusId;
				if (FindBank(audioEvent.BankId) == null)
				{
					audioEvent.BankId = MasterBankId;
				}

				if (FindBus(audioEvent.BusId) == null)
				{
					audioEvent.BusId = MasterBusId;
				}

				foreach (AudioParameter parameter in audioEvent.Parameters)
				{
					parameter.Id ??= NewId();
				}

				audioEvent.Markers.RemoveAll(m => m == null);
				foreach (AudioMarker marker in audioEvent.Markers)
				{
					marker.Id ??= NewId();
					if (marker is TransitionMarker transition)
					{
						transition.Timeline?.Clamp();
					}
				}

				EnsureTrackDefaults(audioEvent.Master);
				audioEvent.Tracks.RemoveAll(t => t == null);
				foreach (AudioTrack track in audioEvent.Tracks)
				{
					EnsureTrackDefaults(track);
				}
			}
		}

		/// <summary>Adds an event at a folder path with one audio track and returns it.</summary>
		public AudioEvent CreateEvent(string path)
		{
			var audioEvent = new AudioEvent { Id = NewId(), Path = UniqueEventPath(path) };
			audioEvent.Tracks.Add(CreateTrack("Audio 1"));
			Events.Add(audioEvent);
			return audioEvent;
		}

		public static AudioTrack CreateTrack(string trackName)
		{
			return new AudioTrack { Id = NewId(), Name = trackName };
		}

		/// <summary>A path no event has yet ("Name", "Name 2", ...).</summary>
		public string UniqueEventPath(string path)
		{
			path = (path ?? "New Event").Replace('\\', '/').Trim('/');
			if (FindEventByPath(path) == null)
			{
				return path;
			}

			for (int i = 2; ; i++)
			{
				string candidate = path + " " + i;
				if (FindEventByPath(candidate) == null)
				{
					return candidate;
				}
			}
		}

		public AudioEvent FindEvent(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}

			foreach (AudioEvent audioEvent in Events)
			{
				if (audioEvent.Id == id)
				{
					return audioEvent;
				}
			}

			return null;
		}

		public AudioEvent FindEventByPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				return null;
			}

			foreach (AudioEvent audioEvent in Events)
			{
				if (string.Equals(audioEvent.Path, path, StringComparison.OrdinalIgnoreCase))
				{
					return audioEvent;
				}
			}

			return null;
		}

		/// <summary>The event a reference names: by GUID, then by path.</summary>
		public AudioEvent FindEvent(AudioEventReference reference)
		{
			return FindEvent(reference.Guid) ?? FindEventByPath(reference.Path);
		}

		public AudioBus FindBus(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}

			foreach (AudioBus bus in Buses)
			{
				if (bus.Id == id)
				{
					return bus;
				}
			}

			return null;
		}

		/// <summary>"Master/Music/Combat".</summary>
		public string BusPath(AudioBus bus)
		{
			string path = bus.Name;
			for (AudioBus parent = FindBus(bus.ParentId); parent != null && parent != bus; parent = FindBus(parent.ParentId))
			{
				path = parent.Name + "/" + path;
			}

			return path;
		}

		/// <summary>Buses in depth-first order, parents before children (the mixer and the editor list them so).</summary>
		public List<AudioBus> BusesInTreeOrder()
		{
			var result = new List<AudioBus>();
			void Add(string parentId, int depth)
			{
				if (depth > 32)
				{
					return;
				}

				foreach (AudioBus bus in Buses)
				{
					if ((parentId == null ? string.IsNullOrEmpty(bus.ParentId) || FindBus(bus.ParentId) == null : bus.ParentId == parentId) && !result.Contains(bus))
					{
						result.Add(bus);
						Add(bus.Id, depth + 1);
					}
				}
			}

			Add(null, 0);
			foreach (AudioBus bus in Buses)
			{
				if (!result.Contains(bus))
				{
					result.Add(bus);
				}
			}

			return result;
		}

		public AudioBank FindBank(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}

			foreach (AudioBank bank in Banks)
			{
				if (bank.Id == id)
				{
					return bank;
				}
			}

			return null;
		}

		public AudioBank FindBankByName(string bankName)
		{
			foreach (AudioBank bank in Banks)
			{
				if (string.Equals(bank.Name, bankName, StringComparison.OrdinalIgnoreCase))
				{
					return bank;
				}
			}

			return null;
		}

		public AudioSnapshot FindSnapshot(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}

			foreach (AudioSnapshot snapshot in Snapshots)
			{
				if (snapshot.Id == id)
				{
					return snapshot;
				}
			}

			return null;
		}

		public AudioSnapshot FindSnapshotByName(string snapshotName)
		{
			foreach (AudioSnapshot snapshot in Snapshots)
			{
				if (string.Equals(snapshot.Name, snapshotName, StringComparison.OrdinalIgnoreCase))
				{
					return snapshot;
				}
			}

			return null;
		}

		public AudioParameter FindGlobalParameter(string id)
		{
			if (string.IsNullOrEmpty(id))
			{
				return null;
			}

			foreach (AudioParameter parameter in GlobalParameters)
			{
				if (parameter.Id == id)
				{
					return parameter;
				}
			}

			return null;
		}

		public AudioParameter FindGlobalParameterByName(string parameterName)
		{
			foreach (AudioParameter parameter in GlobalParameters)
			{
				if (string.Equals(parameter.Name, parameterName, StringComparison.OrdinalIgnoreCase))
				{
					return parameter;
				}
			}

			return null;
		}

		/// <summary>A parameter an event can use: its own, else a global one.</summary>
		public AudioParameter FindParameter(AudioEvent audioEvent, string id)
		{
			return audioEvent?.FindLocalParameter(id) ?? FindGlobalParameter(id);
		}

		/// <summary>Every parameter an event sees: its local ones, then the global ones.</summary>
		public List<AudioParameter> ParametersOf(AudioEvent audioEvent)
		{
			var result = new List<AudioParameter>();
			if (audioEvent != null)
			{
				result.AddRange(audioEvent.Parameters);
			}

			result.AddRange(GlobalParameters);
			return result;
		}

		/// <summary>Events of a bank.</summary>
		public List<AudioEvent> EventsOfBank(string bankId)
		{
			var result = new List<AudioEvent>();
			foreach (AudioEvent audioEvent in Events)
			{
				if ((audioEvent.BankId ?? MasterBankId) == bankId)
				{
					result.Add(audioEvent);
				}
			}

			return result;
		}

		/// <summary>Every folder path events or empty folders use, sorted.</summary>
		public List<string> AllFolders()
		{
			var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string folder in Folders)
			{
				AddFolderChain(set, folder);
			}

			foreach (AudioEvent audioEvent in Events)
			{
				AddFolderChain(set, audioEvent.Folder);
			}

			var list = new List<string>(set);
			list.Sort(StringComparer.OrdinalIgnoreCase);
			return list;
		}

		/// <summary>Call after any edit: bumps <see cref="Version" /> and raises <see cref="Changed" />.</summary>
		public void MarkChanged()
		{
			Version++;
			Changed?.Invoke();
		}

		public void OnBeforeSerialize()
		{ }

		public void OnAfterDeserialize()
		{
			EnsureDefaults();
		}

		public override string ToString()
		{
			return name ?? AssetPath ?? "AudioProject";
		}

		#endregion

		#region PrivateMethods

		private static void EnsureTrackDefaults(AudioTrack track)
		{
			track.Id ??= NewId();
			track.Effects.RemoveAll(e => e == null);
			foreach (AudioEffect effect in track.Effects)
			{
				effect.Id ??= NewId();
			}

			track.Instruments.RemoveAll(i => i == null);
			foreach (AudioInstrument instrument in track.Instruments)
			{
				instrument.Id ??= NewId();
				instrument.Envelope ??= new AudioEnvelope();
				instrument.Conditions ??= new List<AudioCondition>();
			}

			track.Automation.RemoveAll(a => a == null);
		}

		private static void AddFolderChain(HashSet<string> set, string folder)
		{
			if (string.IsNullOrEmpty(folder))
			{
				return;
			}

			string[] parts = folder.Split('/');
			string current = "";
			foreach (string part in parts)
			{
				if (part.Length == 0)
				{
					continue;
				}

				current = current.Length == 0 ? part : current + "/" + part;
				set.Add(current);
			}
		}

		private static DateTime SafeWriteTime(string file)
		{
			try
			{
				return File.GetLastWriteTimeUtc(file);
			}
			catch (Exception)
			{
				return DateTime.MinValue;
			}
		}

		#endregion
	}

	/// <summary>How a sound's data is held when its bank loads (the meta file's importer of an audio file).</summary>
	public enum AudioLoadType
	{
		/// <summary>Decoded PCM in memory, written to the bank as PCM: nothing to decode when the bank loads (short sounds).</summary>
		DecompressOnLoad,

		/// <summary>The encoded file bytes in the bank, decoded to PCM when the bank loads (smaller builds, slower loads).</summary>
		CompressedInMemory,

		/// <summary>Decoded while playing from the bank file on disk (long music and ambiences; transitions into a stream are not sample accurate).</summary>
		Streaming
	}

	/// <summary>Import settings of an audio file (its meta file).</summary>
	[Serializable]
	public sealed class AudioImporter : AssetImporter
	{
		#region PublicFields

		public AudioLoadType loadType = AudioLoadType.DecompressOnLoad;

		/// <summary>Mixes stereo files down to one channel (spatialized sounds need no stereo).</summary>
		public bool forceMono;

		#endregion
	}

	/// <summary>Project-wide audio settings (<see cref="ProjectSettings.Audio" />).</summary>
	[Serializable]
	public sealed class AudioSettings
	{
		#region PublicFields

		/// <summary>GUID of the game's audio project asset; empty: the only ".lelaudio" file under Assets.</summary>
		public string Project = "";

		/// <summary>Volume of the master bus at start, in decibels.</summary>
		public float MasterVolumeDb;

		/// <summary>Real voices mixed at once at most (the audio project's value when 0).</summary>
		public int MaxVoices;

		/// <summary>Length of the output buffer in milliseconds (latency against safety from dropouts).</summary>
		public int OutputBufferMs = 30;

		/// <summary>Load every bank marked AutoLoad when the game starts (the master bank always loads).</summary>
		public bool LoadBanksAtStart = true;

		/// <summary>No audio device is opened (the system runs silently, paced by a timer).</summary>
		public bool Disabled;

		/// <summary>Speed of sound in world units per second, for the Doppler shift.</summary>
		public float SpeedOfSound = 343f;

		/// <summary>Multiplies every event's Doppler scale (0 switches Doppler off for the game).</summary>
		public float DopplerScale = 1f;

		#endregion
	}
}
