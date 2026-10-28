using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Animation
{
	/// <summary>What an animated property holds, and so how its channels blend and are written back.</summary>
	public enum AnimationValueKind
	{
		/// <summary>One channel, interpolated.</summary>
		Float,

		/// <summary>One channel, rounded to the nearest integer when written; blends pick the heavier value.</summary>
		Int,

		/// <summary>One channel, true above 0.5; blends pick the heavier value.</summary>
		Bool,

		/// <summary>One channel holding the enum's integer value; blends pick the heavier value.</summary>
		Enum,

		Vector2,
		Vector3,
		Vector4,

		/// <summary>Four channels (x, y, z, w), normalized after interpolation; blends use normalized lerp.</summary>
		Quaternion,

		/// <summary>Three channels of degrees written as a rotation (Transform.localRotation edited as angles).</summary>
		Euler,

		/// <summary>Four channels (r, g, b, a).</summary>
		Color,

		Vector2i,
		Vector3i,
		Vector4i,

		/// <summary>Keys holding a string each (steps).</summary>
		String,

		/// <summary>Keys holding a scene object, component, ScriptableObject or asset each (steps): sprite animation and the like.</summary>
		Reference
	}

	/// <summary>Which parts of the root's transform animation the animator takes as root motion instead of writing them to the root.</summary>
	[Flags]
	public enum RootMotionChannels
	{
		None = 0,
		PositionX = 1,
		PositionY = 2,
		PositionZ = 4,
		Rotation = 8,
		Position = PositionX | PositionY | PositionZ,
		All = Position | Rotation
	}

	/// <summary>A key of a <see cref="AnimationValueKind.Reference" /> binding: the object the property holds from this time on.</summary>
	[Serializable]
	public sealed class ReferenceKey
	{
		public float time;

		/// <summary>A scene object, component or ScriptableObject.</summary>
		public EngineObject reference;

		/// <summary>An asset (texture, sprite, material, mesh, ...).</summary>
		public IAsset asset;

		public object Value => (object)reference ?? asset;
	}

	/// <summary>A key of a <see cref="AnimationValueKind.String" /> binding.</summary>
	[Serializable]
	public sealed class StringKey
	{
		public float time;
		public string value = "";
	}

	/// <summary>
	///     One animated property of a clip: which object (by path from the animated root and component type), which
	///     property (a serialized field, nested with dots and list indices, or a special one: see
	///     <see cref="AnimationBinder" />), and its channels' curves (one <see cref="FloatCurve" /> per channel: x, y,
	///     z for a Vector3; r, g, b, a for a color) or its keys for strings and references.
	/// </summary>
	[Serializable]
	public sealed class AnimationBinding
	{
		#region PublicFields

		/// <summary>Transform path from the animated root ("Body/Arm/Hand"); empty for the root itself.</summary>
		public string path = "";

		/// <summary>Component type ("Namespace.Type" as the scene stores it, a short name is accepted); empty: the GameObject.</summary>
		public string type = "";

		/// <summary>Property path: a serialized field name, "a.b" for nested objects, "items[2]" for list elements, or a special property.</summary>
		public string property = "";

		public AnimationValueKind kind = AnimationValueKind.Float;

		/// <summary>One curve per channel (<see cref="ChannelCount" />); the clip editor keeps them the same length.</summary>
		public List<FloatCurve> channels = new List<FloatCurve>();

		public List<ReferenceKey> referenceKeys = new List<ReferenceKey>();
		public List<StringKey> stringKeys = new List<StringKey>();

		/// <summary>Number of float channels of the binding's kind (0 for strings and references).</summary>
		public int ChannelCount => ChannelCountOf(kind);

		/// <summary>Channels are interpolated (floats, vectors, colors, rotations); the rest holds its key's value until the next.</summary>
		public bool IsContinuous => IsContinuousKind(kind);

		/// <summary>True when the binding animates a Transform's position, rotation or scale.</summary>
		public bool IsTransform => AnimationBinder.IsTransformProperty(type, property);

		#endregion

		#region PublicMethods

		public static int ChannelCountOf(AnimationValueKind kind)
		{
			switch (kind)
			{
				case AnimationValueKind.Float:
				case AnimationValueKind.Int:
				case AnimationValueKind.Bool:
				case AnimationValueKind.Enum:
					return 1;
				case AnimationValueKind.Vector2:
				case AnimationValueKind.Vector2i:
					return 2;
				case AnimationValueKind.Vector3:
				case AnimationValueKind.Vector3i:
				case AnimationValueKind.Euler:
					return 3;
				case AnimationValueKind.Vector4:
				case AnimationValueKind.Vector4i:
				case AnimationValueKind.Quaternion:
				case AnimationValueKind.Color:
					return 4;
				default:
					return 0;
			}
		}

		public static bool IsContinuousKind(AnimationValueKind kind)
		{
			switch (kind)
			{
				case AnimationValueKind.Float:
				case AnimationValueKind.Vector2:
				case AnimationValueKind.Vector3:
				case AnimationValueKind.Vector4:
				case AnimationValueKind.Quaternion:
				case AnimationValueKind.Euler:
				case AnimationValueKind.Color:
					return true;
				default:
					return false;
			}
		}

		/// <summary>The kind a property of this type animates as (null: the type cannot be animated).</summary>
		public static AnimationValueKind? KindOf(Type valueType)
		{
			if (valueType == null) return null;
			if (valueType.IsEnum) return AnimationValueKind.Enum;
			switch (Type.GetTypeCode(valueType))
			{
				case TypeCode.Single:
				case TypeCode.Double:
					return AnimationValueKind.Float;
				case TypeCode.Int32:
				case TypeCode.Int16:
				case TypeCode.Int64:
				case TypeCode.Byte:
				case TypeCode.SByte:
				case TypeCode.UInt16:
				case TypeCode.UInt32:
				case TypeCode.UInt64:
					return AnimationValueKind.Int;
				case TypeCode.Boolean:
					return AnimationValueKind.Bool;
				case TypeCode.String:
					return AnimationValueKind.String;
			}

			if (valueType == typeof(Vector2)) return AnimationValueKind.Vector2;
			if (valueType == typeof(Vector3)) return AnimationValueKind.Vector3;
			if (valueType == typeof(Vector4)) return AnimationValueKind.Vector4;
			if (valueType == typeof(Quaternion)) return AnimationValueKind.Quaternion;
			if (valueType == typeof(Color4)) return AnimationValueKind.Color;
			if (valueType == typeof(Vector2i)) return AnimationValueKind.Vector2i;
			if (valueType == typeof(Vector3i)) return AnimationValueKind.Vector3i;
			if (valueType == typeof(Vector4i)) return AnimationValueKind.Vector4i;
			if (typeof(EngineObject).IsAssignableFrom(valueType) || typeof(IAsset).IsAssignableFrom(valueType) || valueType.IsInterface)
			{
				return AnimationValueKind.Reference;
			}

			return null;
		}

		/// <summary>Name of channel <paramref name="index" /> for the kind ("x", "r", ...).</summary>
		public static string ChannelName(AnimationValueKind kind, int index)
		{
			switch (kind)
			{
				case AnimationValueKind.Color:
					return index == 0 ? "r" : index == 1 ? "g" : index == 2 ? "b" : "a";
				case AnimationValueKind.Float:
				case AnimationValueKind.Int:
				case AnimationValueKind.Bool:
				case AnimationValueKind.Enum:
					return "";
				default:
					return index == 0 ? "x" : index == 1 ? "y" : index == 2 ? "z" : "w";
			}
		}

		/// <summary>Creates the missing channel curves (and drops extra ones) for the kind.</summary>
		public void EnsureChannels()
		{
			int count = ChannelCount;
			channels = channels ?? new List<FloatCurve>();
			while (channels.Count < count)
			{
				channels.Add(new FloatCurve());
			}

			if (channels.Count > count)
			{
				channels.RemoveRange(count, channels.Count - count);
			}

			foreach (FloatCurve channel in channels)
			{
				channel.Validate();
			}

			referenceKeys = referenceKeys ?? new List<ReferenceKey>();
			stringKeys = stringKeys ?? new List<StringKey>();
		}

		/// <summary>The time of the last key of any channel (0 without keys).</summary>
		public float EndTime
		{
			get
			{
				float end = 0f;
				if (channels != null)
				{
					foreach (FloatCurve channel in channels)
					{
						end = Math.Max(end, channel.EndTime);
					}
				}

				if (referenceKeys != null)
				{
					foreach (ReferenceKey key in referenceKeys)
					{
						end = Math.Max(end, key.time);
					}
				}

				if (stringKeys != null)
				{
					foreach (StringKey key in stringKeys)
					{
						end = Math.Max(end, key.time);
					}
				}

				return end;
			}
		}

		/// <summary>True when every channel and key list is empty.</summary>
		public bool IsEmpty
		{
			get
			{
				if (channels != null)
				{
					foreach (FloatCurve channel in channels)
					{
						if (channel.Length > 0)
						{
							return false;
						}
					}
				}

				return (referenceKeys == null || referenceKeys.Count == 0) && (stringKeys == null || stringKeys.Count == 0);
			}
		}

		/// <summary>The same property on the same object (path, type and property, case sensitive).</summary>
		public bool SameTarget(string otherPath, string otherType, string otherProperty)
		{
			return string.Equals(path ?? "", otherPath ?? "", StringComparison.Ordinal)
				&& string.Equals(AnimationBinder.ShortTypeName(type), AnimationBinder.ShortTypeName(otherType), StringComparison.Ordinal)
				&& string.Equals(property ?? "", otherProperty ?? "", StringComparison.Ordinal);
		}

		/// <summary>A unique key of the target property, for lookups ("Body/Arm|Transform|localPosition").</summary>
		public string TargetKey => (path ?? "") + "|" + AnimationBinder.ShortTypeName(type) + "|" + (property ?? "");

		/// <summary>The display name of the binding ("Body/Arm : Transform.localPosition").</summary>
		public string DisplayName => (string.IsNullOrEmpty(path) ? "" : path + " : ") + (string.IsNullOrEmpty(type) ? "GameObject" : AnimationBinder.ShortTypeName(type)) + "." + property;

		/// <summary>The index of the string or reference key at the time, or -1.</summary>
		public int FindDiscreteKey(float time, float tolerance = 1e-5f)
		{
			if (kind == AnimationValueKind.String)
			{
				for (int i = 0; i < stringKeys.Count; i++)
				{
					if (Math.Abs(stringKeys[i].time - time) <= tolerance) return i;
				}
			}
			else if (kind == AnimationValueKind.Reference)
			{
				for (int i = 0; i < referenceKeys.Count; i++)
				{
					if (Math.Abs(referenceKeys[i].time - time) <= tolerance) return i;
				}
			}

			return -1;
		}

		/// <summary>The string value at a time (the last key at or before it; the first key before the first key's time).</summary>
		public string EvaluateString(float time)
		{
			string value = null;
			for (int i = 0; i < stringKeys.Count; i++)
			{
				if (stringKeys[i].time <= time)
				{
					value = stringKeys[i].value;
				}
				else
				{
					break;
				}
			}

			return value ?? (stringKeys.Count > 0 ? stringKeys[0].value : null);
		}

		/// <summary>The reference at a time (the last key at or before it; the first key before the first key's time).</summary>
		public object EvaluateReference(float time)
		{
			ReferenceKey found = null;
			for (int i = 0; i < referenceKeys.Count; i++)
			{
				if (referenceKeys[i].time <= time)
				{
					found = referenceKeys[i];
				}
				else
				{
					break;
				}
			}

			found = found ?? (referenceKeys.Count > 0 ? referenceKeys[0] : null);
			return found?.Value;
		}

		public void SortDiscreteKeys()
		{
			stringKeys?.Sort((a, b) => a.time.CompareTo(b.time));
			referenceKeys?.Sort((a, b) => a.time.CompareTo(b.time));
		}

		public AnimationBinding Clone()
		{
			var copy = new AnimationBinding { path = path, type = type, property = property, kind = kind };
			foreach (FloatCurve channel in channels)
			{
				copy.channels.Add(channel.Clone());
			}

			foreach (ReferenceKey key in referenceKeys)
			{
				copy.referenceKeys.Add(new ReferenceKey { time = key.time, reference = key.reference, asset = key.asset });
			}

			foreach (StringKey key in stringKeys)
			{
				copy.stringKeys.Add(new StringKey { time = key.time, value = key.value });
			}

			return copy;
		}

		public override string ToString()
		{
			return DisplayName + " (" + kind + ")";
		}

		#endregion
	}

	/// <summary>
	///     An animation clip asset (".anim": readable JSON in the project, binary in builds): curves of properties of a
	///     GameObject hierarchy (<see cref="AnimationBinding" />s, any serialized field of any component, transforms
	///     included) plus events, played by an <see cref="Animator" /> through an <see cref="AnimatorController" />,
	///     or sampled directly (<see cref="SampleAnimation" />). Loaded once per path and shared
	///     (<see cref="Load" />); the clip editor changes it in place and bumps <see cref="Version" />, so animators
	///     playing it re-bind.
	/// </summary>
	public sealed class AnimationClip : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".anim";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;

		public string name = "";

		/// <summary>Frames per second the clip editor snaps keys to (the curves themselves are in seconds).</summary>
		public float frameRate = 60f;

		/// <summary>The clip repeats; an animator state playing it never ends (exit times are per cycle).</summary>
		public bool loop;

		/// <summary>Start of a loop in cycles (0.5: the clip starts half way through), applied by the animator.</summary>
		public float cycleOffset;

		/// <summary>
		///     Clip length in seconds: the time of the last key or event when <see cref="lengthOverride" /> is 0, else that
		///     (a clip may be longer than its keys).
		/// </summary>
		public float length => lengthOverride > 0f ? lengthOverride : cachedLength;

		public float lengthOverride;

		/// <summary>Which root transform channels the animator extracts as root motion (when it applies root motion).</summary>
		public RootMotionChannels rootMotion = RootMotionChannels.Position | RootMotionChannels.Rotation;

		public List<AnimationBinding> bindings = new List<AnimationBinding>();
		public List<AnimationEvent> events = new List<AnimationEvent>();

		public int FormatVersion = 1;

		/// <summary>Bumped by every edit (<see cref="MarkChanged" />); animators re-bind when it changes.</summary>
		[NonSerialized] public int Version = 1;

		/// <summary>Raised by <see cref="MarkChanged" />.</summary>
		public event Action Changed;

		/// <summary>True when the clip has no keys at all.</summary>
		public bool IsEmpty
		{
			get
			{
				foreach (AnimationBinding binding in bindings)
				{
					if (!binding.IsEmpty)
					{
						return false;
					}
				}

				return events.Count == 0;
			}
		}

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, AnimationClip> cache = new Dictionary<string, AnimationClip>(StringComparer.OrdinalIgnoreCase);
		private float cachedLength;

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		/// <summary>Loads (or returns the cached) clip at a root-relative path. Unreadable files give an empty clip.</summary>
		public static AnimationClip Load(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			lock (cache)
			{
				if (cache.TryGetValue(assetPath, out AnimationClip cached))
				{
					return cached;
				}
			}

			AnimationClip clip = null;
			try
			{
				byte[] data = File.ReadAllBytes(AssetDatabase.ToAbsolute(assetPath));
				if (Documents.IsDocument(data))
				{
					clip = Documents.ReadObject(data, typeof(AnimationClip), null, new SerializationContext { Location = assetPath }) as AnimationClip;
				}
				else
				{
					Debug.LogError($"[Animation] {assetPath} is not a clip document");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Animation] Cannot load {assetPath}: {e.Message}");
			}

			clip = clip ?? new AnimationClip();
			clip.AssetPath = assetPath.Replace('\\', '/');
			clip.name = Path.GetFileNameWithoutExtension(assetPath);
			lock (cache)
			{
				cache[assetPath] = clip;
			}

			return clip;
		}

		/// <summary>The cached clip of an asset path when it was loaded (null otherwise).</summary>
		public static AnimationClip FindLoaded(string assetPath)
		{
			lock (cache)
			{
				return assetPath != null && cache.TryGetValue(assetPath, out AnimationClip clip) ? clip : null;
			}
		}

		/// <summary>Writes the clip to its asset file (or a new one) in <see cref="Documents.FileFormat" />.</summary>
		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The clip has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
			AssetDatabase.EnsureMeta(AssetPath);
			lock (cache)
			{
				cache[AssetPath] = this;
			}
		}

		/// <summary>The clip as a document (undo snapshots, copies).</summary>
		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name ?? "AnimationClip" });
		}

		/// <summary>Replaces the content from a document written by <see cref="ToBytes" /> and marks the clip changed.</summary>
		public void ApplyBytes(byte[] data)
		{
			bindings.Clear();
			events.Clear();
			Documents.ReadObject(data, typeof(AnimationClip), this, new SerializationContext { Location = name ?? "AnimationClip" });
			MarkChanged();
		}

		/// <summary>Independent copy (same asset path, own curve objects).</summary>
		public AnimationClip Clone()
		{
			var copy = Documents.ReadObject(ToBytes(), typeof(AnimationClip), null) as AnimationClip ?? new AnimationClip();
			copy.AssetPath = AssetPath;
			copy.name = name;
			return copy;
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
			}
		}

		/// <summary>A file or folder moved: the loaded clips keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (clip, path) =>
				{
					clip.AssetPath = path;
					clip.name = Path.GetFileNameWithoutExtension(path);
				});
			}
		}

		/// <summary>Drops a cached clip (its file was deleted or replaced) so the next use reloads it.</summary>
		public static void Forget(string assetPath)
		{
			lock (cache)
			{
				cache.Remove(assetPath);
			}
		}

		/// <summary>Every loaded clip (the editor's live update after outside edits).</summary>
		public static List<AnimationClip> GetLoaded()
		{
			lock (cache)
			{
				return new List<AnimationClip>(cache.Values);
			}
		}

		// ---------------------------------------------------------------- editing

		/// <summary>Call after any edit: recomputes the length, bumps <see cref="Version" /> and raises <see cref="Changed" />.</summary>
		public void MarkChanged()
		{
			Validate();
			Version++;
			Changed?.Invoke();
		}

		/// <summary>Repairs the data (channel counts, key order) and recomputes the cached length.</summary>
		public void Validate()
		{
			bindings = bindings ?? new List<AnimationBinding>();
			events = events ?? new List<AnimationEvent>();
			float end = 0f;
			foreach (AnimationBinding binding in bindings)
			{
				binding.EnsureChannels();
				binding.SortDiscreteKeys();
				end = Math.Max(end, binding.EndTime);
			}

			events.Sort((a, b) => a.time.CompareTo(b.time));
			foreach (AnimationEvent e in events)
			{
				end = Math.Max(end, e.time);
			}

			cachedLength = end;
			if (frameRate <= 0f)
			{
				frameRate = 60f;
			}
		}

		/// <summary>The binding of a property, or null.</summary>
		public AnimationBinding FindBinding(string path, string type, string property)
		{
			foreach (AnimationBinding binding in bindings)
			{
				if (binding.SameTarget(path, type, property))
				{
					return binding;
				}
			}

			return null;
		}

		public AnimationBinding FindBinding(string path, Type type, string property)
		{
			return FindBinding(path, type != null ? AnimationBinder.TypeName(type) : "", property);
		}

		/// <summary>The binding of a property, created when missing (with empty channels of the kind).</summary>
		public AnimationBinding EnsureBinding(string path, string type, string property, AnimationValueKind kind)
		{
			AnimationBinding binding = FindBinding(path, type, property);
			if (binding == null)
			{
				binding = new AnimationBinding { path = path ?? "", type = type ?? "", property = property ?? "", kind = kind };
				binding.EnsureChannels();
				bindings.Add(binding);
			}
			else if (binding.kind != kind)
			{
				binding.kind = kind;
				binding.EnsureChannels();
			}

			return binding;
		}

		public AnimationBinding EnsureBinding(string path, Type type, string property, AnimationValueKind kind)
		{
			return EnsureBinding(path, type != null ? AnimationBinder.TypeName(type) : "", property, kind);
		}

		/// <summary>Sets a float property's curve (Unity's SetCurve); a null curve removes the binding.</summary>
		public void SetCurve(string path, Type type, string property, AnimationCurve curve)
		{
			if (curve == null)
			{
				RemoveBinding(path, type != null ? AnimationBinder.TypeName(type) : "", property);
				return;
			}

			AnimationBinding binding = EnsureBinding(path, type, property, AnimationValueKind.Float);
			binding.channels[0] = FloatCurve.FromAnimationCurve(curve);
		}

		/// <summary>Sets one channel of a property's binding.</summary>
		public void SetCurve(string path, Type type, string property, AnimationValueKind kind, int channel, FloatCurve curve)
		{
			AnimationBinding binding = EnsureBinding(path, type, property, kind);
			binding.channels[channel] = curve ?? new FloatCurve();
		}

		/// <summary>Sets a Vector3 property's three channels at once.</summary>
		public void SetVector3Curves(string path, Type type, string property, FloatCurve x, FloatCurve y, FloatCurve z)
		{
			AnimationBinding binding = EnsureBinding(path, type, property, AnimationValueKind.Vector3);
			binding.channels[0] = x ?? new FloatCurve();
			binding.channels[1] = y ?? new FloatCurve();
			binding.channels[2] = z ?? new FloatCurve();
		}

		/// <summary>Adds keys for a Vector3 value at a time on all three channels (creates the binding).</summary>
		public void AddVector3Key(string path, Type type, string property, float time, Vector3 value)
		{
			AnimationBinding binding = EnsureBinding(path, type, property, AnimationValueKind.Vector3);
			binding.channels[0].AddKey(time, value.X);
			binding.channels[1].AddKey(time, value.Y);
			binding.channels[2].AddKey(time, value.Z);
		}

		/// <summary>Adds keys for a rotation at a time on the four quaternion channels (creates the binding).</summary>
		public void AddQuaternionKey(string path, Type type, string property, float time, Quaternion value)
		{
			AnimationBinding binding = EnsureBinding(path, type, property, AnimationValueKind.Quaternion);
			binding.channels[0].AddKey(time, value.X);
			binding.channels[1].AddKey(time, value.Y);
			binding.channels[2].AddKey(time, value.Z);
			binding.channels[3].AddKey(time, value.W);
		}

		/// <summary>Adds a key of a float property at a time (creates the binding).</summary>
		public void AddFloatKey(string path, Type type, string property, float time, float value)
		{
			EnsureBinding(path, type, property, AnimationValueKind.Float).channels[0].AddKey(time, value);
		}

		public bool RemoveBinding(string path, string type, string property)
		{
			AnimationBinding binding = FindBinding(path, type, property);
			return binding != null && bindings.Remove(binding);
		}

		public AnimationEvent AddEvent(float time, string function)
		{
			var e = new AnimationEvent { time = time, function = function ?? "" };
			events.Add(e);
			events.Sort((a, b) => a.time.CompareTo(b.time));
			return e;
		}

		/// <summary>The transform paths the clip animates (every binding's path, distinct, root first).</summary>
		public List<string> GetAnimatedPaths()
		{
			var paths = new List<string>();
			foreach (AnimationBinding binding in bindings)
			{
				if (!paths.Contains(binding.path ?? ""))
				{
					paths.Add(binding.path ?? "");
				}
			}

			paths.Sort(StringComparer.Ordinal);
			return paths;
		}

		/// <summary>The clip's time of a frame number, snapped to the frame rate.</summary>
		public float FrameToTime(int frame)
		{
			return frame / Math.Max(frameRate, 1e-3f);
		}

		public int TimeToFrame(float time)
		{
			return (int)Math.Round(time * frameRate);
		}

		/// <summary>
		///     Writes the clip's values at <paramref name="time" /> to the GameObject's hierarchy (editor previews, one-off
		///     poses). Binds on every call: play clips through an <see cref="Animator" /> for speed.
		/// </summary>
		public void SampleAnimation(GameObject root, float time)
		{
			if (root == null)
			{
				return;
			}

			var layout = new PoseLayout();
			layout.AddClip(this);
			layout.Bind(root);
			var pose = new Pose(layout);
			pose.Clear();
			var sampler = new ClipSampler(this, layout);
			sampler.Sample(time, 1f, pose);
			pose.Apply(layout, false);
		}

		public void OnBeforeSerialize()
		{ }

		public void OnAfterDeserialize()
		{
			Validate();
		}

		public override string ToString()
		{
			return $"{name} ({length:0.###} s, {bindings.Count} properties{(loop ? ", loop" : "")})";
		}

		#endregion
	}
}
