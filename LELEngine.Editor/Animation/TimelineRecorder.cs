using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using OpenTK.Mathematics;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The Timeline window's Record. While it is on, the armed channels' objects are watched: every value animation can
	///     reach on them (the transform, the active flag, serialized fields of any component, your scripts' included, and
	///     [Animatable] members). Paused: a value that changes (inspector, scene view, a script) becomes a key at the playhead.
	///     Playing: the armed channels stop writing and what their objects do is keyed as it happens, at the timeline's frame
	///     rate, thinned out when it ends. Keys go into the clip under the playhead, or a new .anim file next to the timeline
	///     (the way a sound recording lands in the project's audio folder).
	/// </summary>
	internal sealed class TimelineRecorder
	{
		#region PublicFields

		public bool Recording { get; private set; }

		/// <summary>An undo step is open for what is being recorded.</summary>
		public bool HoldsEdit => editOpen;

		#endregion

		#region PrivateFields

		private const float ChangeTolerance = 1e-5f;

		private sealed class Watched
		{
			public string TypeName;
			public string Property;
			public AnimationValueKind Kind;
			public BoundProperty Bound;
			public int Channels;
			public readonly float[] Last = new float[4];
			public readonly float[] Current = new float[4];
			public readonly float[] Original = new float[4];
			public object LastDiscrete;
			public object OriginalDiscrete;
			public bool Changing;

			public bool IsDiscrete => Channels == 0;
		}

		private sealed class Watch
		{
			public TimelineChannel Channel;
			public GameObject Target;
			public int ComponentCount;
			public int Signature;
			public readonly List<Watched> Properties = new List<Watched>();
			public TimelineClip CaptureClip;
		}

		private readonly TimelineWindow window;
		private readonly EditorApplication editor;
		private readonly HashSet<string> armed = new HashSet<string>(StringComparer.Ordinal);
		private readonly Dictionary<string, Watch> watches = new Dictionary<string, Watch>(StringComparer.Ordinal);
		private readonly List<AnimationBinding> capturedBindings = new List<AnimationBinding>();
		private readonly List<AnimationClip> capturedClips = new List<AnimationClip>();
		private bool capturing;
		private bool editOpen;
		private double lastTick;
		private TimelinePlayer capturePlayer;

		#endregion

		#region Constructors

		public TimelineRecorder(TimelineWindow window, EditorApplication editor)
		{
			this.window = window;
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public bool IsArmed(string channelId)
		{
			return armed.Contains(channelId);
		}

		public void SetArmed(string channelId, bool value)
		{
			if (value)
			{
				armed.Add(channelId);
			}
			else
			{
				armed.Remove(channelId);
				capturePlayer?.SetCapture(channelId, false);
			}
		}

		/// <summary>Record on: with nothing armed yet, every channel with an object.</summary>
		public void StartRecording()
		{
			Recording = true;
			if (armed.Count == 0 && window.Asset != null)
			{
				foreach (TimelineChannel channel in window.Asset.channels)
				{
					if (window.TargetOf(channel) != null)
					{
						armed.Add(channel.id);
					}
				}
			}

			watches.Clear();
		}

		public void StopRecording()
		{
			if (capturing)
			{
				EndCapture();
			}

			if (editOpen)
			{
				editOpen = false;
				window.Document?.EndEdit();
			}

			Recording = false;
			watches.Clear();
		}

		/// <summary>Before the player's frame: what changed since the last frame's pose, and the record of a running cursor.</summary>
		public void BeforePose(TimelinePlayer player)
		{
			if (!Recording)
			{
				return;
			}

			if (player == null || window.Document == null || !player.IsPlaying)
			{
				if (capturing)
				{
					EndCapture();
				}

				return;
			}

			if (!player.Paused && player.State != TimelinePlaybackState.Sustaining)
			{
				if (!capturing)
				{
					BeginCapture(player);
				}

				CaptureTick(player);
				return;
			}

			if (capturing)
			{
				EndCapture();
			}

			// While the game runs its scripts change values all the time: only a paused game's changes are someone's edits.
			if (editor.PlayState != PlayState.Playing)
			{
				DetectKeys(player);
			}
		}

		/// <summary>The window wrote a pose (the playhead moved): that is no change of anyone's.</summary>
		public void Resync()
		{
			if (!Recording || capturing)
			{
				return;
			}

			foreach (Watch watch in watches.Values)
			{
				foreach (Watched watched in watch.Properties)
				{
					if (watched.Bound.IsAlive)
					{
						Sync(watched);
					}
				}
			}
		}

		/// <summary>After the player's frame: what the pose wrote is no change of anyone's. An edit ends when nothing is being dragged any more.</summary>
		public void AfterPose()
		{
			if (!Recording || capturing)
			{
				return;
			}

			Resync();
			if (editOpen && !ImGui.IsAnyMouseDown() && !ImGui.IsAnyItemActive())
			{
				editOpen = false;
				window.Document.EndEdit();
			}
		}

		/// <summary>The folder recorded clips go to: next to the timeline, under its name.</summary>
		public string ClipFolder()
		{
			string path = window.Asset.AssetPath ?? "Timelines/Timeline.timeline";
			string folder = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
			return (folder.Length > 0 ? folder + "/" : "") + Path.GetFileNameWithoutExtension(path) + " Clips";
		}

		/// <summary>A path in a folder (created) no file has yet.</summary>
		public static string UniquePath(string folder, string name, string extension)
		{
			Directory.CreateDirectory(AssetDatabase.ToAbsolute(folder));
			foreach (char invalid in Path.GetInvalidFileNameChars())
			{
				name = name.Replace(invalid, '_');
			}

			string path = folder + "/" + name + extension;
			for (int i = 2; File.Exists(AssetDatabase.ToAbsolute(path)); i++)
			{
				path = folder + "/" + name + " " + i + extension;
			}

			return path;
		}

		/// <summary>A new .anim file placed on a channel at a time, up to the next clip (a second at most). Runs inside an edit.</summary>
		public TimelineClip CreateClip(TimelineChannel channel, double at)
		{
			TimelineAsset asset = window.Asset;
			string name = (window.TargetOf(channel)?.Name ?? channel.name) + " Recorded";
			var clip = new AnimationClip { name = name, frameRate = asset.frameRate };
			clip.Save(UniquePath(ClipFolder(), name, AnimationClip.Extension));
			double length = 1.0;
			foreach (TimelineClip other in channel.clips)
			{
				if (other.start > at + 1e-6)
				{
					length = Math.Min(length, other.start - at);
				}
			}

			var placed = new TimelineClip { id = TimelineAsset.NewId(), clip = AnimationClip.Load(clip.AssetPath) ?? clip, start = at, length = Math.Max(length, 1.0 / Math.Max(asset.frameRate, 1f)) };
			channel.clips.Add(placed);
			return placed;
		}

		/// <summary>A key with a property's current value at a time, in the clip there. Runs inside an edit.</summary>
		public void KeyCurrentValue(TimelineChannel channel, GameObject target, string path, string typeName, string property, AnimationValueKind kind, double at)
		{
			BoundProperty bound = AnimationBinder.Bind(target, new AnimationBinding { path = path, type = typeName, property = property, kind = kind });
			if (bound == null)
			{
				return;
			}

			TimelineClip placed = ClipAt(channel, at) ?? CreateClip(channel, at);
			window.Document.TouchClip(placed.clip);
			AnimationBinding binding = placed.clip.EnsureBinding(path, typeName, property, kind);
			float clipTime = ClipTimeOf(placed, at);
			if (AnimationBinding.ChannelCountOf(kind) == 0)
			{
				SetDiscreteKey(binding, clipTime, bound.ReadDiscrete());
			}
			else
			{
				var storage = new float[4];
				bound.ReadChannels(storage, 0);
				SetChannelKeys(binding, clipTime, storage);
			}

			placed.clip.MarkChanged();
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- watching

		private Watch WatchOf(TimelineChannel channel, GameObject target)
		{
			int signature = BindingSignature(channel);
			if (watches.TryGetValue(channel.id, out Watch watch) && watch.Target == target && watch.ComponentCount == target.Components.Count && watch.Signature == signature)
			{
				return watch;
			}

			watch = new Watch { Channel = channel, Target = target, ComponentCount = target.Components.Count, Signature = signature };
			AddWatched(watch, target, "", AnimationBinder.GetAnimatableProperties(target));
			foreach (Behaviour component in target.Components)
			{
				if (component is Timeline || component is LELEngine.Animation.Animator)
				{
					continue;
				}

				// Fields and exposed members, not every C# property: most are the same values again.
				AddWatched(watch, target, AnimationBinder.TypeName(component.GetType()), AnimationBinder.GetAnimatableProperties(component, false));
			}

			// What the channel's clips animate anyway (a property added by hand).
			foreach (TimelineClip clip in channel.clips)
			{
				if (clip.clip == null)
				{
					continue;
				}

				foreach (AnimationBinding binding in clip.clip.bindings)
				{
					if (string.IsNullOrEmpty(binding.path) && !watch.Properties.Exists(w => w.Property == binding.property && AnimationBinder.ShortTypeName(w.TypeName) == AnimationBinder.ShortTypeName(binding.type)))
					{
						AddWatched(watch, target, binding.type, binding.property, binding.kind);
					}
				}
			}

			watches[channel.id] = watch;
			return watch;
		}

		private static int BindingSignature(TimelineChannel channel)
		{
			int signature = channel.clips.Count;
			foreach (TimelineClip clip in channel.clips)
			{
				signature = signature * 31 + (clip.clip?.bindings.Count ?? 0);
			}

			return signature;
		}

		private static void AddWatched(Watch watch, GameObject target, string typeName, List<AnimatableProperty> properties)
		{
			foreach (AnimatableProperty property in properties)
			{
				// A rotation is listed as angles and as a quaternion: one watcher, as angles.
				if (property.Kind == AnimationValueKind.Quaternion && properties.Exists(p => p.Property == property.Property && p.Kind == AnimationValueKind.Euler))
				{
					continue;
				}

				if (property.Property == AnimationBinder.LocalRotation && properties.Exists(p => p.Property == AnimationBinder.LocalEulerAngles))
				{
					continue;
				}

				AddWatched(watch, target, typeName, property.Property, property.Kind);
			}
		}

		private static void AddWatched(Watch watch, GameObject target, string typeName, string property, AnimationValueKind kind)
		{
			BoundProperty bound = AnimationBinder.Bind(target, new AnimationBinding { path = "", type = typeName, property = property, kind = kind });
			if (bound == null)
			{
				return;
			}

			var watched = new Watched { TypeName = typeName, Property = property, Kind = kind, Bound = bound, Channels = AnimationBinding.ChannelCountOf(bound.Kind) };
			Sync(watched);
			watch.Properties.Add(watched);
		}

		/// <summary>The value as it is now is no change.</summary>
		private static void Sync(Watched watched)
		{
			if (watched.IsDiscrete)
			{
				watched.LastDiscrete = watched.Bound.ReadDiscrete();
			}
			else
			{
				watched.Bound.ReadChannels(watched.Last, 0);
			}
		}

		private static bool HasChanged(Watched watched, out object discrete)
		{
			discrete = null;
			if (watched.IsDiscrete)
			{
				discrete = watched.Bound.ReadDiscrete();
				return !Equals(discrete, watched.LastDiscrete);
			}

			watched.Bound.ReadChannels(watched.Current, 0);
			for (int c = 0; c < watched.Channels; c++)
			{
				if (Math.Abs(watched.Current[c] - watched.Last[c]) > ChangeTolerance)
				{
					return true;
				}
			}

			return false;
		}

		// ---------------------------------------------------------------- keys while paused

		private void DetectKeys(TimelinePlayer player)
		{
			double time = window.ShownTime;
			foreach (TimelineChannel channel in window.Asset.channels)
			{
				GameObject target = armed.Contains(channel.id) ? window.TargetOf(channel) : null;
				if (target == null)
				{
					continue;
				}

				Watch watch = WatchOf(channel, target);
				foreach (Watched watched in watch.Properties)
				{
					if (!watched.Bound.IsAlive || !HasChanged(watched, out object discrete))
					{
						continue;
					}

					OpenEdit();
					TimelineClip placed = ClipAt(channel, time) ?? CreateClip(channel, time);
					KeyChange(placed, watched, time, discrete);
					placed.clip.MarkChanged();
					window.Document.Changed();

					// The key is what poses the object now; its own value stays what it was (Stop puts that back).
					if (watched.IsDiscrete)
					{
						watched.Bound.WriteDiscrete(watched.LastDiscrete);
					}
					else
					{
						watched.Bound.WriteChannels(watched.Last, 0);
					}
				}
			}
		}

		private void OpenEdit()
		{
			if (!editOpen)
			{
				editOpen = true;
				window.Document.BeginEdit("Record keys");
			}
		}

		/// <summary>A key with the new value; a property the clip did not have also gets its old value at the clip's start, so what came before stays.</summary>
		private void KeyChange(TimelineClip placed, Watched watched, double time, object discrete)
		{
			window.Document.TouchClip(placed.clip);
			float clipTime = ClipTimeOf(placed, time);
			AnimationBinding binding = placed.clip.FindBinding("", watched.TypeName, watched.Property);
			bool created = binding == null;
			binding = binding ?? placed.clip.EnsureBinding("", watched.TypeName, watched.Property, watched.Kind);
			float start = (float)placed.startOffset;
			if (watched.IsDiscrete)
			{
				if (created && clipTime > start + 1e-4f)
				{
					SetDiscreteKey(binding, start, watched.LastDiscrete);
				}

				SetDiscreteKey(binding, clipTime, discrete);
				return;
			}

			if (created && clipTime > start + 1e-4f)
			{
				SetChannelKeys(binding, start, watched.Last);
			}

			SetChannelKeys(binding, clipTime, watched.Current);
		}

		private static void SetChannelKeys(AnimationBinding binding, float clipTime, float[] storage)
		{
			if (binding.kind == AnimationValueKind.Euler)
			{
				Vector3 euler = QuaternionHelper.ToEulerDegrees(new Quaternion(storage[0], storage[1], storage[2], storage[3]));
				for (int c = 0; c < 3; c++)
				{
					// The angle nearest the curve's: no spin of 360 degrees between two keys.
					FloatCurve curve = binding.channels[c];
					float value = euler[c];
					if (curve.Length > 0)
					{
						float previous = curve.Evaluate(clipTime);
						value = previous + DeltaAngle(previous, value);
					}

					curve.AddKey(clipTime, value);
				}

				return;
			}

			for (int c = 0; c < binding.ChannelCount; c++)
			{
				binding.channels[c].AddKey(clipTime, storage[c]);
			}
		}

		private static float DeltaAngle(float from, float to)
		{
			float delta = (to - from) % 360f;
			if (delta > 180f) delta -= 360f;
			if (delta < -180f) delta += 360f;
			return delta;
		}

		private static void SetDiscreteKey(AnimationBinding binding, float time, object value)
		{
			int index = binding.FindDiscreteKey(time);
			if (binding.kind == AnimationValueKind.String)
			{
				if (index >= 0) binding.stringKeys[index].value = value as string ?? "";
				else binding.stringKeys.Add(new StringKey { time = time, value = value as string ?? "" });
			}
			else
			{
				ReferenceKey key = index >= 0 ? binding.referenceKeys[index] : new ReferenceKey { time = time };
				key.reference = value as EngineObject;
				key.asset = value as IAsset;
				if (index < 0) binding.referenceKeys.Add(key);
			}

			binding.SortDiscreteKeys();
		}

		/// <summary>The clip of a channel at a time, the one drawn last when they overlap (null: none).</summary>
		private static TimelineClip ClipAt(TimelineChannel channel, double time)
		{
			for (int i = channel.clips.Count - 1; i >= 0; i--)
			{
				TimelineClip clip = channel.clips[i];
				if (clip.clip != null && time >= clip.start - 1e-6 && time < clip.End - 1e-6 && !AssetDatabase.IsEngineResource(clip.clip.AssetPath ?? ""))
				{
					return clip;
				}
			}

			return null;
		}

		/// <summary>Where a time of the timeline falls in the clip, past its current end too (a recording makes it longer); inside a looping clip's cycle.</summary>
		private static float ClipTimeOf(TimelineClip clip, double time)
		{
			double raw = clip.startOffset + (time - clip.start) * Math.Max(clip.speed, 1e-4f);
			return (float)(clip.loop ? clip.WrapClipTime(raw) : Math.Max(raw, 0.0));
		}

		// ---------------------------------------------------------------- capture

		private void BeginCapture(TimelinePlayer player)
		{
			capturing = true;
			capturePlayer = player;
			player.Persistent = true;
			lastTick = player.Time;
			capturedBindings.Clear();
			capturedClips.Clear();
			OpenEdit();
			foreach (TimelineChannel channel in window.Asset.channels)
			{
				GameObject target = armed.Contains(channel.id) ? window.TargetOf(channel) : null;
				if (target == null)
				{
					continue;
				}

				player.SetCapture(channel.id, true);
				Watch watch = WatchOf(channel, target);
				watch.CaptureClip = null;
				foreach (Watched watched in watch.Properties)
				{
					watched.Changing = false;
					Sync(watched);
					watched.OriginalDiscrete = watched.LastDiscrete;
					Array.Copy(watched.Last, watched.Original, 4);
				}
			}
		}

		private void CaptureTick(TimelinePlayer player)
		{
			double time = player.Time;
			double step = 1.0 / Math.Max(window.Asset.frameRate, 1f);
			if (time >= lastTick && time - lastTick < step)
			{
				return;
			}

			double previous = lastTick;
			lastTick = time;
			foreach (Watch watch in watches.Values)
			{
				if (!armed.Contains(watch.Channel.id))
				{
					continue;
				}

				foreach (Watched watched in watch.Properties)
				{
					if (!watched.Bound.IsAlive)
					{
						continue;
					}

					bool changed = HasChanged(watched, out object discrete);
					if (changed)
					{
						// Flat until now: the last still value right before the move.
						if (!watched.Changing && time > previous)
						{
							CaptureKey(watch, watched, previous, true, discrete);
						}

						CaptureKey(watch, watched, time, false, discrete);
						watched.Changing = true;
						if (watched.IsDiscrete)
						{
							watched.LastDiscrete = discrete;
						}
						else
						{
							Array.Copy(watched.Current, watched.Last, 4);
						}
					}
					else if (watched.Changing)
					{
						CaptureKey(watch, watched, time, false, discrete);
						watched.Changing = false;
					}
				}
			}
		}

		private void CaptureKey(Watch watch, Watched watched, double time, bool lastValue, object discrete)
		{
			if (watch.CaptureClip == null)
			{
				watch.CaptureClip = ClipAt(watch.Channel, time) ?? CreateClip(watch.Channel, time);
				window.Document.TouchClip(watch.CaptureClip.clip);
				if (!capturedClips.Contains(watch.CaptureClip.clip))
				{
					capturedClips.Add(watch.CaptureClip.clip);
				}
			}

			TimelineClip placed = watch.CaptureClip;

			// The region grows with the recording.
			if (time >= placed.End)
			{
				placed.length = time - placed.start + 1.0 / Math.Max(window.Asset.frameRate, 1f);
			}

			float clipTime = ClipTimeOf(placed, time);
			AnimationBinding binding = placed.clip.EnsureBinding("", watched.TypeName, watched.Property, watched.Kind);
			if (!capturedBindings.Contains(binding))
			{
				capturedBindings.Add(binding);
			}

			if (watched.IsDiscrete)
			{
				SetDiscreteKey(binding, clipTime, lastValue ? watched.LastDiscrete : discrete);
			}
			else
			{
				SetChannelKeys(binding, clipTime, lastValue ? watched.Last : watched.Current);
			}
		}

		private void EndCapture()
		{
			capturing = false;
			TimelinePlayer player = capturePlayer;
			capturePlayer = null;
			if (player != null)
			{
				player.Persistent = false;
			}

			foreach (Watch watch in watches.Values)
			{
				player?.SetCapture(watch.Channel.id, false);

				// In edit mode the objects go back to their own values: the clip has what they did.
				if (!editor.IsPlaying)
				{
					foreach (Watched watched in watch.Properties)
					{
						if (!watched.Bound.IsAlive)
						{
							continue;
						}

						if (watched.IsDiscrete)
						{
							watched.Bound.WriteDiscrete(watched.OriginalDiscrete);
						}
						else if (watched.Channels > 0)
						{
							watched.Bound.WriteChannels(watched.Original, 0);
						}
					}
				}
			}

			foreach (AnimationBinding binding in capturedBindings)
			{
				foreach (FloatCurve curve in binding.channels)
				{
					Simplify(curve, binding.IsContinuous);
				}
			}

			foreach (AnimationClip clip in capturedClips)
			{
				clip.MarkChanged();
			}

			capturedBindings.Clear();
			capturedClips.Clear();
			if (editOpen)
			{
				editOpen = false;
				window.Document.EndEdit();
			}

			// A fresh look at the values next time.
			watches.Clear();
		}

		/// <summary>Keeps only the keys the shape needs (a line within a small tolerance of what was recorded); numbers that step keep only their changes.</summary>
		private static void Simplify(FloatCurve curve, bool continuous)
		{
			int count = curve.Length;
			if (count < 3)
			{
				return;
			}

			var keep = new bool[count];
			keep[0] = true;
			keep[count - 1] = true;
			if (continuous)
			{
				float low = float.MaxValue;
				float high = float.MinValue;
				foreach (float value in curve.Values)
				{
					low = Math.Min(low, value);
					high = Math.Max(high, value);
				}

				KeepFarthest(curve, 0, count - 1, Math.Max((high - low) * 0.002f, 1e-5f), keep);
			}
			else
			{
				for (int i = 1; i < count; i++)
				{
					keep[i] |= curve.Values[i] != curve.Values[i - 1];
				}
			}

			var keys = new List<Keyframe>();
			for (int i = 0; i < count; i++)
			{
				if (keep[i])
				{
					keys.Add(new Keyframe(curve.Times[i], curve.Values[i]) { tangentMode = continuous ? TangentMode.Linear : TangentMode.Constant });
				}
			}

			curve.SetKeys(keys);
			curve.RecalculateTangents();
		}

		private static void KeepFarthest(FloatCurve curve, int first, int last, float tolerance, bool[] keep)
		{
			if (last - first < 2)
			{
				return;
			}

			float t0 = curve.Times[first];
			float t1 = curve.Times[last];
			float v0 = curve.Values[first];
			float v1 = curve.Values[last];
			int farthest = -1;
			float worst = tolerance;
			for (int i = first + 1; i < last; i++)
			{
				float line = v0 + (v1 - v0) * ((curve.Times[i] - t0) / Math.Max(t1 - t0, 1e-6f));
				float error = Math.Abs(curve.Values[i] - line);
				if (error > worst)
				{
					worst = error;
					farthest = i;
				}
			}

			if (farthest >= 0)
			{
				keep[farthest] = true;
				KeepFarthest(curve, first, farthest, tolerance, keep);
				KeepFarthest(curve, farthest, last, tolerance, keep);
			}
		}

		#endregion
	}
}
