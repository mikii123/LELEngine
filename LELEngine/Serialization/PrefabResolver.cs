using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Ids of prefab instances' objects. An object of a prefab has an id of the prefab's own space; its copy in an
	///     instance has the id <c>instanceId ^ sourceId</c>. XOR is associative, so the same rule holds for nested
	///     prefabs (an object of prefab B inside prefab A inside a scene: <c>sceneInstance ^ nestedInstance ^ idInB</c>)
	///     and ids stay the same when an instance is unpacked one level, or when objects become a prefab's instance.
	///     A reference read inside an instance resolves the prefab's ids through the same rule
	///     (<see cref="SerializationContext.InstanceId" />).
	/// </summary>
	public static class PrefabIds
	{
		public static ulong Combine(ulong instanceId, ulong sourceId)
		{
			return instanceId ^ sourceId;
		}
	}

	/// <summary>A prefab instance inside a prefab (a nested prefab, or the base of a variant), in the outer prefab's id space.</summary>
	internal sealed class NestedPrefab
	{
		public ulong InstanceId;
		public string PrefabPath;

		/// <summary>Id of its root object in the outer prefab's space.</summary>
		public ulong RootId;

		/// <summary>The nested instance this one is part of (null: written in the outer prefab itself).</summary>
		public NestedPrefab Parent;
	}

	/// <summary>A GameObject of a resolved prefab (generic data, ids of the prefab's space).</summary>
	internal sealed class PrefabObject
	{
		public ulong Id;
		public string Name;
		public bool Active = true;
		public ulong Parent;
		public int HideFlags;
		public int Layer;
		public readonly List<PrefabComponent> Components = new List<PrefabComponent>();

		/// <summary>The nested instance of this prefab file the object comes from (null: written in the file itself).</summary>
		public NestedPrefab Origin;

		/// <summary>With <see cref="Origin" />: the object's id in the nested prefab.</summary>
		public ulong OriginId;
	}

	internal sealed class PrefabComponent
	{
		public ulong Id;
		public bool Enabled = true;
		public SerializedNode Data;
		public PrefabObject Owner;
		public NestedPrefab Origin;
		public ulong OriginId;

		public string TypeName => Data?.TypeName;
	}

	/// <summary>
	///     A prefab file with its nested prefabs and variant bases expanded and their overrides applied: every object
	///     and component as generic data, in the prefab's id space, parents before children. Shared and never changed
	///     after it is built (instances copy what they change); built without loading a type, on any thread.
	/// </summary>
	internal sealed class ResolvedPrefab
	{
		#region PublicFields

		public string AssetPath;
		public string Name;
		public PrefabObject Root;
		public readonly List<PrefabObject> Objects = new List<PrefabObject>();
		public readonly Dictionary<ulong, PrefabObject> ObjectsById = new Dictionary<ulong, PrefabObject>();
		public readonly Dictionary<ulong, PrefabComponent> ComponentsById = new Dictionary<ulong, PrefabComponent>();

		/// <summary>Every prefab instance inside this prefab, nested ones of nested ones too, outer ones first.</summary>
		public readonly List<NestedPrefab> Nested = new List<NestedPrefab>();

		public readonly Dictionary<ulong, NestedPrefab> NestedByRoot = new Dictionary<ulong, NestedPrefab>();

		/// <summary>Every prefab file this one is built from (nested prefabs, variant bases, at any depth).</summary>
		public readonly HashSet<string> Dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		/// <summary>Why the prefab could not be read or resolved completely (null: fine).</summary>
		public string Error;

		/// <summary>The file when it was read (outside changes are noticed by <see cref="PrefabCache.ReloadChanged" />).</summary>
		public long FileLength;

		public DateTime FileWrittenUtc;

		/// <summary>A variant's base: the prefab instance its root is the root of; null for a plain prefab.</summary>
		public NestedPrefab Base => Root != null && NestedByRoot.TryGetValue(Root.Id, out NestedPrefab nested) && nested.Parent == null ? nested : null;

		#endregion

		#region PrivateFields

		private readonly object sync = new object();
		private byte[] binary;
		private Dictionary<ulong, (int typeId, int offset)> blocks;

		#endregion

		#region PublicMethods

		public PrefabObject FindObject(ulong id)
		{
			return ObjectsById.TryGetValue(id, out PrefabObject result) ? result : null;
		}

		public PrefabComponent FindComponent(ulong id)
		{
			return ComponentsById.TryGetValue(id, out PrefabComponent result) ? result : null;
		}

		/// <summary>
		///     Every component's fields as one binary document (generic data written with its stored layout), read with
		///     the live types when the prefab is instantiated; built on first use.
		/// </summary>
		public byte[] GetBinary(out Dictionary<ulong, (int typeId, int offset)> componentBlocks)
		{
			lock (sync)
			{
				if (binary == null)
				{
					var writer = new BinaryDocumentWriter(null) { Kind = BinaryDocument.KindObject };
					var table = new Dictionary<ulong, (int, int)>();
					foreach (PrefabObject obj in Objects)
					{
						foreach (PrefabComponent component in obj.Components)
						{
							SerializedNode node = component.Data ?? new SerializedNode { TypeName = "Missing" };
							int typeId = writer.GenericTypeId(node);
							int offset = writer.Body.Length;
							int lengthPosition = writer.Body.ReserveInt32();
							writer.WriteGenericFields(node);
							writer.Body.PatchInt32(lengthPosition, writer.Body.Length - lengthPosition - 4);
							table[component.Id] = (typeId, offset);
						}
					}

					binary = writer.ToArray();
					blocks = table;
				}

				componentBlocks = blocks;
				return binary;
			}
		}

		/// <summary>True when <paramref name="id" /> is the object's id or one of its ancestors' (prefab space).</summary>
		public bool IsInSubtree(ulong id, ulong ancestor)
		{
			for (PrefabObject obj = FindObject(id); obj != null; obj = obj.Parent != 0 ? FindObject(obj.Parent) : null)
			{
				if (obj.Id == ancestor)
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}

	/// <summary>Builds <see cref="ResolvedPrefab" />s from prefab documents (see <see cref="PrefabCache" />).</summary>
	internal static class PrefabResolver
	{
		#region PublicMethods

		public static ResolvedPrefab Resolve(string assetPath)
		{
			var result = new ResolvedPrefab { AssetPath = assetPath, Name = Path.GetFileNameWithoutExtension(assetPath) };
			string file = AssetDatabase.ToAbsolute(assetPath);
			if (!File.Exists(file))
			{
				result.Error = "the prefab file does not exist";
				return result;
			}

			SceneDocument document;
			try
			{
				var info = new FileInfo(file);
				result.FileLength = info.Length;
				result.FileWrittenUtc = info.LastWriteTimeUtc;
				document = SceneSerializer.ReadDocument(File.ReadAllBytes(file));
			}
			catch (Exception e)
			{
				result.Error = "cannot read the file (" + e.Message + ")";
				Debug.LogError($"[Prefab] {assetPath}: {result.Error}");
				return result;
			}

			if (document.Kind != BinaryDocument.KindPrefab)
			{
				result.Error = "not a prefab document (" + document.Kind + ")";
				Debug.LogError($"[Prefab] {assetPath}: {result.Error}");
				return result;
			}

			Build(result, document);
			return result;
		}

		/// <summary>Fills <paramref name="result" /> from a prefab document (also used for documents not yet written, e.g. edits).</summary>
		public static void Build(ResolvedPrefab result, SceneDocument document)
		{
			foreach (DocumentEntry entry in document.Entries)
			{
				if (entry.Object != null)
				{
					AddObject(result, entry.Object);
				}
				else if (entry.Instance != null)
				{
					AddInstance(result, entry.Instance);
				}
			}

			foreach (PrefabObject obj in result.Objects)
			{
				if (obj.Parent == 0)
				{
					result.Root = obj;
					break;
				}
			}

			if (result.Root == null && result.Error == null)
			{
				result.Error = "the prefab has no objects";
			}
		}

		#endregion

		#region PrivateMethods

		private static void AddObject(ResolvedPrefab result, DocumentObject source)
		{
			if (source.Parent != 0 && !result.ObjectsById.ContainsKey(source.Parent))
			{
				Debug.LogWarning($"[Prefab] {result.AssetPath}: '{source.Name}' has no parent in the prefab; left out");
				return;
			}

			var obj = new PrefabObject { Id = source.Id, Name = source.Name ?? "GameObject", Active = source.Active, Parent = source.Parent, HideFlags = source.HideFlags, Layer = source.Layer };
			foreach (DocumentComponent component in source.Components)
			{
				AddComponent(result, obj, new PrefabComponent { Id = component.Id, Enabled = component.Enabled, Data = component.Data });
			}

			Register(result, obj);
		}

		private static void AddComponent(ResolvedPrefab result, PrefabObject owner, PrefabComponent component)
		{
			component.Owner = owner;
			owner.Components.Add(component);
			result.ComponentsById[component.Id] = component;
		}

		private static void Register(ResolvedPrefab result, PrefabObject obj)
		{
			result.Objects.Add(obj);
			result.ObjectsById[obj.Id] = obj;
		}

		/// <summary>A prefab instance inside this prefab: the nested prefab's objects copied into this prefab's space with the instance's changes.</summary>
		private static void AddInstance(ResolvedPrefab result, PrefabInstanceRecord record)
		{
			string innerPath = PrefabCache.PathOf(record.Prefab);
			ResolvedPrefab inner = innerPath != null ? PrefabCache.Get(innerPath) : null;
			if (inner?.Root == null)
			{
				string why = innerPath == null ? "it is missing (guid " + record.Prefab.Guid + ", last path '" + record.Prefab.Path + "')" : inner?.Error ?? "it cannot be resolved";
				Debug.LogWarning($"[Prefab] {result.AssetPath}: nested prefab '{record.Prefab.Path}' left out: {why}");
				result.Error ??= "a nested prefab is missing";
				return;
			}

			if (record.Parent != 0 && !result.ObjectsById.ContainsKey(record.Parent))
			{
				Debug.LogWarning($"[Prefab] {result.AssetPath}: the instance of '{inner.Name}' has no parent in the prefab; left out");
				return;
			}

			ulong instance = record.Id;
			result.Dependencies.Add(inner.AssetPath);
			result.Dependencies.UnionWith(inner.Dependencies);

			var nested = new NestedPrefab { InstanceId = instance, PrefabPath = inner.AssetPath, RootId = instance ^ inner.Root.Id };
			result.Nested.Add(nested);
			result.NestedByRoot[nested.RootId] = nested;
			var mapped = new Dictionary<NestedPrefab, NestedPrefab>();
			foreach (NestedPrefab deeper in inner.Nested)
			{
				var copy = new NestedPrefab
				{
					InstanceId = instance ^ deeper.InstanceId,
					PrefabPath = deeper.PrefabPath,
					RootId = instance ^ deeper.RootId,
					Parent = deeper.Parent != null && mapped.TryGetValue(deeper.Parent, out NestedPrefab parent) ? parent : nested
				};
				mapped[deeper] = copy;
				result.Nested.Add(copy);
				result.NestedByRoot[copy.RootId] = copy;
			}

			var removedObjects = new HashSet<ulong>(record.RemovedObjects);
			var removedComponents = new HashSet<ulong>(record.RemovedComponents);
			var modifications = GroupByTarget(record.Modifications);
			var skipped = new HashSet<ulong>();
			Func<ulong, ulong> map = id => instance ^ id;
			foreach (PrefabObject source in inner.Objects)
			{
				if (removedObjects.Contains(source.Id) || (source.Parent != 0 && skipped.Contains(source.Parent)))
				{
					skipped.Add(source.Id);
					continue;
				}

				var obj = new PrefabObject
				{
					Id = instance ^ source.Id,
					Name = source.Name,
					Active = source.Active,
					Parent = source.Parent != 0 ? instance ^ source.Parent : record.Parent,
					HideFlags = source.HideFlags,
					Layer = source.Layer,
					Origin = nested,
					OriginId = source.Id
				};

				if (modifications.TryGetValue(source.Id, out List<PropertyModification> objectModifications))
				{
					ApplyHeader(obj, objectModifications);
				}

				foreach (PrefabComponent component in source.Components)
				{
					if (removedComponents.Contains(component.Id))
					{
						continue;
					}

					var copy = new PrefabComponent { Id = instance ^ component.Id, Enabled = component.Enabled, Data = GenericValues.CloneNode(component.Data, map), Origin = nested, OriginId = component.Id };
					if (modifications.TryGetValue(component.Id, out List<PropertyModification> componentModifications))
					{
						foreach (PropertyModification modification in componentModifications)
						{
							if (modification.Path == PropertyModification.EnabledProperty && !modification.HasKey)
							{
								copy.Enabled = modification.Value is bool enabled && enabled;
							}
							else if (!GenericValues.Apply(copy.Data, modification))
							{
								Debug.LogWarning($"[Prefab] {result.AssetPath}: override {modification.FullPath} of {component.TypeName} in '{inner.Name}' does not fit; ignored");
							}
						}
					}

					AddComponent(result, obj, copy);
				}

				Register(result, obj);
			}

			foreach (DocumentComponent added in record.AddedComponents)
			{
				PrefabObject host = result.FindObject(instance ^ added.GameObject);
				if (host == null)
				{
					Debug.LogWarning($"[Prefab] {result.AssetPath}: a component added to the instance of '{inner.Name}' has lost its GameObject; left out");
					continue;
				}

				AddComponent(result, host, new PrefabComponent { Id = added.Id, Enabled = added.Enabled, Data = added.Data });
			}
		}

		internal static Dictionary<ulong, List<PropertyModification>> GroupByTarget(List<PropertyModification> modifications)
		{
			var result = new Dictionary<ulong, List<PropertyModification>>();
			foreach (PropertyModification modification in modifications)
			{
				if (!result.TryGetValue(modification.Target, out List<PropertyModification> list))
				{
					list = new List<PropertyModification>();
					result[modification.Target] = list;
				}

				list.Add(modification);
			}

			return result;
		}

		/// <summary>The GameObject properties an instance overrides: name, active flag, layer.</summary>
		private static void ApplyHeader(PrefabObject obj, List<PropertyModification> modifications)
		{
			foreach (PropertyModification modification in modifications)
			{
				switch (modification.Path)
				{
					case PropertyModification.NameProperty:
						obj.Name = modification.Value as string ?? obj.Name;
						break;
					case PropertyModification.ActiveProperty:
						obj.Active = modification.Value is bool active && active;
						break;
					case PropertyModification.LayerProperty:
						obj.Layer = modification.Value is int layer ? layer : obj.Layer;
						break;
				}
			}
		}

		#endregion
	}

	/// <summary>
	///     Resolved prefabs by asset path, built on first use (any thread) and dropped when a prefab file changes, with
	///     every prefab built from it (<see cref="Invalidate" />).
	/// </summary>
	public static class PrefabCache
	{
		#region PublicFields

		/// <summary>Bumped whenever cached prefabs are dropped.</summary>
		public static int Version { get; private set; }

		/// <summary>Raised (on the thread that invalidated) with the prefab paths dropped.</summary>
		public static event Action<IReadOnlyCollection<string>> Invalidated;

		#endregion

		#region PrivateFields

		private static readonly object sync = new object();
		private static readonly Dictionary<string, ResolvedPrefab> cache = new Dictionary<string, ResolvedPrefab>(StringComparer.OrdinalIgnoreCase);

		[ThreadStatic] private static HashSet<string> resolving;

		#endregion

		#region PublicMethods

		/// <summary>Drops every cached prefab built from one of <paramref name="assetPaths" /> (and those files' own data).</summary>
		public static void Invalidate(IEnumerable<string> assetPaths)
		{
			var dropped = new List<string>();
			lock (sync)
			{
				var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (string path in assetPaths)
				{
					if (path != null)
					{
						changed.Add(Normalize(path));
					}
				}

				foreach (KeyValuePair<string, ResolvedPrefab> entry in new List<KeyValuePair<string, ResolvedPrefab>>(cache))
				{
					if (changed.Contains(entry.Key) || entry.Value.Dependencies.Overlaps(changed))
					{
						cache.Remove(entry.Key);
						dropped.Add(entry.Key);
					}
				}

				foreach (string path in changed)
				{
					if (!dropped.Contains(path))
					{
						dropped.Add(path);
					}
				}

				Version++;
			}

			Prefab.ForgetMissing(dropped);
			Invalidated?.Invoke(dropped);
		}

		public static void Invalidate(string assetPath)
		{
			Invalidate(new[] { assetPath });
		}

		/// <summary>Drops everything (project closed).</summary>
		public static void Clear()
		{
			lock (sync)
			{
				cache.Clear();
				Version++;
			}
		}

		/// <summary>Drops the prefabs whose files changed since they were read (edits outside the editor); returns their paths.</summary>
		public static List<string> ReloadChanged()
		{
			var changed = new List<string>();
			lock (sync)
			{
				foreach (KeyValuePair<string, ResolvedPrefab> entry in cache)
				{
					string file = AssetDatabase.ToAbsolute(entry.Key);
					var info = new FileInfo(file);
					bool existed = entry.Value.FileWrittenUtc != default;
					if (info.Exists != existed || (info.Exists && (info.Length != entry.Value.FileLength || info.LastWriteTimeUtc != entry.Value.FileWrittenUtc)))
					{
						changed.Add(entry.Key);
					}
				}
			}

			if (changed.Count > 0)
			{
				Invalidate(changed);
			}

			return changed;
		}

		/// <summary>True when the prefab at <paramref name="assetPath" /> is built from <paramref name="dependency" /> (or is it).</summary>
		public static bool DependsOn(string assetPath, string dependency)
		{
			if (assetPath == null || dependency == null)
			{
				return false;
			}

			if (string.Equals(Normalize(assetPath), Normalize(dependency), StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			ResolvedPrefab resolved = Get(assetPath);
			return resolved != null && resolved.Dependencies.Contains(Normalize(dependency));
		}

		#endregion

		#region InternalMethods

		/// <summary>The resolved prefab at an asset path (built now when not cached); null for a prefab that includes itself.</summary>
		internal static ResolvedPrefab Get(string assetPath)
		{
			if (assetPath == null)
			{
				return null;
			}

			string path = Normalize(assetPath);
			lock (sync)
			{
				if (cache.TryGetValue(path, out ResolvedPrefab cached))
				{
					return cached;
				}
			}

			resolving ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!resolving.Add(path))
			{
				Debug.LogError($"[Prefab] {path} contains itself (through its nested prefabs or variant bases); the nested instance is left out");
				return null;
			}

			ResolvedPrefab result;
			int version;
			lock (sync)
			{
				version = Version;
			}

			try
			{
				result = PrefabResolver.Resolve(path);
			}
			finally
			{
				resolving.Remove(path);
			}

			lock (sync)
			{
				if (cache.TryGetValue(path, out ResolvedPrefab other))
				{
					return other;
				}

				// Not cached when the files changed while it was built: the next use builds it again.
				if (version == Version)
				{
					cache[path] = result;
				}
			}

			return result;
		}

		/// <summary>The asset path a prefab reference points at (by GUID, else its path), or null.</summary>
		internal static string PathOf(AssetReference reference)
		{
			string path = reference.Guid != null ? AssetDatabase.GetPath(reference.Guid) : null;
			if (path == null && reference.Path != null)
			{
				path = AssetDatabase.Resolve(reference.Path, null);
			}

			return path != null ? Normalize(path) : null;
		}

		internal static string Normalize(string path)
		{
			return path.Replace('\\', '/').TrimStart('/');
		}

		#endregion
	}
}
