using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>How far <see cref="PrefabUtility.UnpackPrefabInstance" /> breaks the link to the prefab.</summary>
	public enum PrefabUnpackMode
	{
		/// <summary>The outermost instance only: prefabs nested in it become instances of their own.</summary>
		OutermostRoot,

		/// <summary>Every object becomes a plain object, nested prefabs too.</summary>
		Completely
	}

	/// <summary>
	///     A prefab an override can be applied to: the instance's prefab, or a prefab nested in it (or the base of a
	///     variant), down to the prefab the object is written in. <see cref="TargetId" /> is the object's id there.
	/// </summary>
	public sealed class PrefabApplyTarget
	{
		public string PrefabPath;
		public ulong TargetId;

		/// <summary>Scene id XOR <see cref="Mask" /> = the id in that prefab (for objects of the same instance).</summary>
		public ulong Mask;

		/// <summary>The object is written in that prefab itself (not an override of a prefab nested in it).</summary>
		public bool Defines;

		public string Name => Path.GetFileNameWithoutExtension(PrefabPath);
	}

	/// <summary>What an instance changes of its prefab (<see cref="PrefabUtility.GetOverrides" />).</summary>
	public sealed class PrefabInstanceOverrides
	{
		public PrefabInstance Instance;

		/// <summary>Changed values (without the root's name, position and rotation, which every instance has).</summary>
		public readonly List<PropertyModification> Modifications = new List<PropertyModification>();

		/// <summary>Ids in the prefab of the GameObjects and components the instance does not have.</summary>
		public readonly List<ulong> RemovedObjects = new List<ulong>();

		public readonly List<ulong> RemovedComponents = new List<ulong>();
		public readonly List<Behaviour> AddedComponents = new List<Behaviour>();

		/// <summary>Objects added under the instance's objects (the topmost of each added hierarchy).</summary>
		public readonly List<GameObject> AddedObjects = new List<GameObject>();

		private Dictionary<ulong, HashSet<string>> paths;

		public bool IsEmpty => Modifications.Count == 0 && RemovedObjects.Count == 0 && RemovedComponents.Count == 0 && AddedComponents.Count == 0 && AddedObjects.Count == 0;

		/// <summary>The overrides of one object or component (its id in the prefab).</summary>
		public IEnumerable<PropertyModification> Of(ulong target)
		{
			foreach (PropertyModification modification in Modifications)
			{
				if (modification.Target == target)
				{
					yield return modification;
				}
			}
		}

		/// <summary>True when the property (or anything inside it, with <paramref name="within" />) is overridden.</summary>
		public bool IsOverridden(ulong target, string path, bool within = false)
		{
			if (paths == null)
			{
				paths = new Dictionary<ulong, HashSet<string>>();
				foreach (PropertyModification modification in Modifications)
				{
					if (!paths.TryGetValue(modification.Target, out HashSet<string> set))
					{
						set = new HashSet<string>(StringComparer.Ordinal);
						paths[modification.Target] = set;
					}

					set.Add(modification.FullPath);
				}
			}

			if (!paths.TryGetValue(target, out HashSet<string> overridden))
			{
				return false;
			}

			if (overridden.Contains(path))
			{
				return true;
			}

			// A whole value replaced overrides everything inside it; a length override marks its list.
			foreach (string candidate in overridden)
			{
				if (PropertyPath.IsWithin(path, candidate) || (within && PropertyPath.IsWithin(candidate, path)) || candidate == PropertyPath.SizeOf(path))
				{
					return true;
				}
			}

			return false;
		}
	}

	/// <summary>
	///     Prefab queries and operations (Unity's PrefabUtility): saving objects as a prefab (or a variant), unpacking
	///     instances, and applying or reverting their overrides to the prefab or a prefab nested in it. Operations that
	///     write prefab files change the files only: instances of the changed prefabs pick the change up when their
	///     scene is loaded again (the editor reloads it, <see cref="ReloadScene" />).
	/// </summary>
	public static class PrefabUtility
	{
		#region PublicMethods

		// ---------------------------------------------------------------- queries

		/// <summary>The (outermost) prefab instance a GameObject or component is part of; null for other objects, objects added to an instance included.</summary>
		public static PrefabInstance GetPrefabInstance(EngineObject obj)
		{
			return obj?.prefabInstance;
		}

		public static bool IsPartOfPrefabInstance(EngineObject obj)
		{
			return obj?.prefabInstance != null;
		}

		/// <summary>The root of an outermost prefab instance.</summary>
		public static bool IsPrefabInstanceRoot(GameObject go)
		{
			return go?.prefabInstance != null && go.prefabInstance.Root == go;
		}

		/// <summary>The prefab a GameObject or component comes from (the outermost instance's), or null.</summary>
		public static Prefab GetPrefab(EngineObject obj)
		{
			return obj?.prefabInstance?.Prefab;
		}

		/// <summary>The id of an instance's object or component in its prefab (0 for other objects).</summary>
		public static ulong GetSourceId(EngineObject obj)
		{
			return obj?.prefabInstance != null ? obj.prefabSourceId : 0;
		}

		/// <summary>An object added under the objects of a prefab instance (not part of the prefab).</summary>
		public static bool IsAddedGameObject(GameObject go)
		{
			return go != null && go.prefabInstance == null && go.transform.parent?.gameObject.prefabInstance is PrefabInstance parent && !parent.IsMissing;
		}

		/// <summary>A component added to an object of a prefab instance (not part of the prefab).</summary>
		public static bool IsAddedComponent(Behaviour component)
		{
			return component != null && component.prefabInstance == null && component.gameObject?.prefabInstance is PrefabInstance instance && !instance.IsMissing;
		}

		/// <summary>
		///     The prefab whose instance's root this object is when it is the root of a prefab nested in its instance's
		///     prefab (or of a variant's base), else null.
		/// </summary>
		public static Prefab GetNestedPrefab(GameObject go)
		{
			PrefabInstance instance = go?.prefabInstance;
			if (instance?.Source == null || !instance.Source.NestedByRoot.TryGetValue(go.prefabSourceId, out NestedPrefab nested))
			{
				return null;
			}

			return Prefab.Load(nested.PrefabPath);
		}

		/// <summary>The prefab closest to the object: of the innermost nested prefab whose instance holds it, else the instance's prefab.</summary>
		public static Prefab GetNearestPrefab(EngineObject obj)
		{
			PrefabInstance instance = obj?.prefabInstance;
			if (instance?.Source == null)
			{
				return instance?.Prefab;
			}

			List<PrefabApplyTarget> chain = GetApplyTargets(instance, obj.prefabSourceId, obj is Behaviour);
			return chain.Count > 0 ? Prefab.Load(chain[chain.Count - 1].PrefabPath) : instance.Prefab;
		}

		/// <summary>
		///     The prefabs an override of the object (its id in the instance's prefab) can go to, outermost first: the
		///     instance's prefab, then each prefab nested in it the object comes from, down to the prefab it is written in.
		/// </summary>
		public static List<PrefabApplyTarget> GetApplyTargets(PrefabInstance instance, ulong sourceId, bool component)
		{
			var result = new List<PrefabApplyTarget>();
			ResolvedPrefab current = instance?.Source;
			ulong id = sourceId;
			ulong mask = instance?.Id ?? 0;
			while (current != null)
			{
				var target = new PrefabApplyTarget { PrefabPath = current.AssetPath, TargetId = id, Mask = mask };
				result.Add(target);
				NestedPrefab origin;
				ulong originId;
				if (component)
				{
					PrefabComponent found = current.FindComponent(id);
					origin = found?.Origin;
					originId = found?.OriginId ?? 0;
				}
				else
				{
					PrefabObject found = current.FindObject(id);
					origin = found?.Origin;
					originId = found?.OriginId ?? 0;
				}

				if (origin == null)
				{
					target.Defines = true;
					break;
				}

				mask ^= origin.InstanceId;
				id = originId;
				current = PrefabCache.Get(origin.PrefabPath);
			}

			return result;
		}

		/// <summary>What the instance changes of its prefab, for tools and the inspector.</summary>
		public static PrefabInstanceOverrides GetOverrides(PrefabInstance instance)
		{
			var result = new PrefabInstanceOverrides { Instance = instance };
			if (instance?.Source == null || instance.Root == null || instance.Root.dead)
			{
				return result;
			}

			PrefabInstanceRecord record = PrefabOverrides.Compute(instance, ContextFor(instance.Root.scene), null, false);
			foreach (PropertyModification modification in record.Modifications)
			{
				if (!PrefabOverrides.IsDefaultOverride(instance.Source, modification))
				{
					result.Modifications.Add(modification);
				}
			}

			result.RemovedObjects.AddRange(record.RemovedObjects);
			result.RemovedComponents.AddRange(record.RemovedComponents);
			foreach (DocumentComponent added in record.AddedComponents)
			{
				result.AddedComponents.Add(added.Live);
			}

			var objects = new Dictionary<ulong, GameObject>();
			PrefabOverrides.Collect(instance, objects, new Dictionary<ulong, Behaviour>());
			foreach (GameObject go in objects.Values)
			{
				foreach (Transform child in go.transform.Children)
				{
					GameObject added = child.gameObject;
					if (added.prefabInstance != instance && !added.dead && (added.hideFlags & HideFlags.DontSave) == 0)
					{
						result.AddedObjects.Add(added);
					}
				}
			}

			return result;
		}

		/// <summary>The modifications a document would store for the instance (all of them, the root's name and placement included).</summary>
		public static List<PropertyModification> GetModifications(PrefabInstance instance)
		{
			if (instance?.Root == null)
			{
				return new List<PropertyModification>();
			}

			return PrefabOverrides.Compute(instance, ContextFor(instance.Root.scene), null).Modifications;
		}

		/// <summary>The GameObject of an instance with an id in its prefab (null when the instance does not have it).</summary>
		public static GameObject FindObject(PrefabInstance instance, ulong sourceId)
		{
			var objects = new Dictionary<ulong, GameObject>();
			PrefabOverrides.Collect(instance, objects, new Dictionary<ulong, Behaviour>());
			return objects.TryGetValue(sourceId, out GameObject go) ? go : null;
		}

		/// <summary>The component of an instance with an id in its prefab.</summary>
		public static Behaviour FindComponent(PrefabInstance instance, ulong sourceId)
		{
			var components = new Dictionary<ulong, Behaviour>();
			PrefabOverrides.Collect(instance, new Dictionary<ulong, GameObject>(), components);
			return components.TryGetValue(sourceId, out Behaviour component) ? component : null;
		}

		/// <summary>Name of a prefab object or component by its id in the instance's prefab ("Gun" or "Gun (Rigidbody2D)"), for lists of removed things.</summary>
		public static string DescribeSource(PrefabInstance instance, ulong sourceId)
		{
			ResolvedPrefab source = instance?.Source;
			PrefabObject obj = source?.FindObject(sourceId);
			if (obj != null)
			{
				return obj.Name;
			}

			PrefabComponent component = source?.FindComponent(sourceId);
			return component != null ? component.Owner?.Name + " (" + GenericValues.Describe(component.Data, ValueTag.Object) + ")" : "#" + sourceId.ToString("X");
		}

		// ---------------------------------------------------------------- creating prefabs

		/// <summary>
		///     Saves <paramref name="root" /> and its descendants as a prefab file at <paramref name="assetPath" /> (asset
		///     path, ".prefab"). A prefab instance's root becomes a variant of its prefab (unpack it first for an original
		///     prefab); prefab instances under the root become nested prefabs. References to objects outside the hierarchy
		///     are not saved in the prefab. With <paramref name="connect" /> the objects become an instance of the new prefab
		///     (those outside references then are its overrides).
		/// </summary>
		public static Prefab SaveAsPrefabAsset(GameObject root, string assetPath, bool connect = true)
		{
			if (root == null || root.dead)
			{
				throw new ArgumentNullException(nameof(root));
			}

			if (root.prefabInstance != null && root.prefabInstance.Root != root)
			{
				throw new InvalidOperationException($"'{root.Name}' is part of a prefab instance: unpack the instance first, or save its root.");
			}

			assetPath = NormalizePrefabPath(assetPath);
			ulong mask = SceneIds.Next();
			var inside = new HashSet<EngineObject>();
			CollectHierarchy(root, inside);
			byte[] bytes = SceneSerializer.SavePrefabToBytes(root, Documents.FileFormat, id => id ^ mask, value => inside.Contains((EngineObject)value) ? IdOf(value) ^ mask : 0);
			WritePrefabFile(assetPath, bytes);
			Prefab prefab = Prefab.Load(assetPath);
			if (connect && prefab != null)
			{
				Connect(root, prefab, mask, inside);
			}

			return prefab;
		}

		/// <summary>Creates a variant of a prefab: a prefab whose root is an instance of it, changing nothing yet.</summary>
		public static Prefab CreateVariant(Prefab basePrefab, string assetPath)
		{
			ResolvedPrefab source = basePrefab?.Resolved;
			if (source?.Root == null)
			{
				throw new InvalidOperationException("The base prefab cannot be read: " + (source?.Error ?? "missing"));
			}

			assetPath = NormalizePrefabPath(assetPath);
			string name = Path.GetFileNameWithoutExtension(assetPath);
			var record = new PrefabInstanceRecord { Id = SceneIds.Next(), Prefab = ReferenceOf(basePrefab.AssetPath) };
			record.Modifications.Add(new PropertyModification { Target = source.Root.Id, Path = PropertyModification.NameProperty, Tag = ValueTag.String, Value = name });
			var document = new SceneDocument { Kind = BinaryDocument.KindPrefab, Name = name };
			document.Entries.Add(new DocumentEntry { Instance = record });
			WritePrefabFile(assetPath, SceneSerializer.WriteDocument(document, Documents.FileFormat));
			return Prefab.Load(assetPath);
		}

		/// <summary>Instantiates a prefab in a scene (as the editor does: the lifecycle follows the scene's).</summary>
		public static GameObject InstantiatePrefab(Prefab prefab, Scene scene, Transform parent = null)
		{
			return prefab?.Instantiate(scene, parent);
		}

		/// <summary>
		///     Breaks the link of an instance (its root) to its prefab: the objects keep their values and become plain
		///     objects; with <see cref="PrefabUnpackMode.OutermostRoot" /> the prefabs nested in it become instances of their
		///     own (their objects keep their ids).
		/// </summary>
		public static void UnpackPrefabInstance(GameObject root, PrefabUnpackMode mode)
		{
			PrefabInstance instance = root?.prefabInstance;
			if (instance == null || instance.Root != root)
			{
				throw new InvalidOperationException($"'{root?.Name}' is not the root of a prefab instance.");
			}

			var objects = new Dictionary<ulong, GameObject>();
			var components = new Dictionary<ulong, Behaviour>();
			PrefabOverrides.Collect(instance, objects, components);
			var created = new Dictionary<NestedPrefab, PrefabInstance>();
			foreach (KeyValuePair<ulong, GameObject> entry in objects)
			{
				NestedPrefab origin = mode == PrefabUnpackMode.OutermostRoot ? instance.Source?.FindObject(entry.Key)?.Origin : null;
				Relink(entry.Value, entry.Key, origin, instance, objects, created);
			}

			foreach (KeyValuePair<ulong, Behaviour> entry in components)
			{
				NestedPrefab origin = mode == PrefabUnpackMode.OutermostRoot ? instance.Source?.FindComponent(entry.Key)?.Origin : null;
				Relink(entry.Value, entry.Key, origin, instance, objects, created);
			}

			if (instance.IsMissing && root.prefabInstance == instance)
			{
				root.prefabInstance = null;
			}
		}

		// ---------------------------------------------------------------- reverting (live objects of a scene)

		/// <summary>Sets an overridden property back to the prefab's value.</summary>
		public static void RevertProperty(PrefabInstance instance, PropertyModification modification)
		{
			ResolvedPrefab source = instance?.Source;
			if (source == null)
			{
				return;
			}

			PrefabObject obj = source.FindObject(modification.Target);
			if (obj != null)
			{
				GameObject go = FindObject(instance, obj.Id);
				if (go != null)
				{
					RevertHeader(go, obj, modification.Path);
				}

				return;
			}

			PrefabComponent stored = source.FindComponent(modification.Target);
			Behaviour live = stored != null ? FindComponent(instance, stored.Id) : null;
			if (live == null)
			{
				return;
			}

			if (modification.Path == PropertyModification.EnabledProperty && !modification.HasKey)
			{
				live.enabled = stored.Enabled;
				return;
			}

			SerializationContext context = ContextFor(live.gameObject.scene);
			SerializedNode sourceData = GenericValues.CloneNode(stored.Data, id => instance.Id ^ id);
			SerializedNode liveData = PrefabOverrides.NodeOf(live, context);
			CopyProperty(sourceData, liveData, modification);
			ReadInto(live, liveData, context);
		}

		/// <summary>Every override of the instance back to the prefab: values, added and removed objects and components (the root's placement and name stay).</summary>
		public static void RevertPrefabInstance(PrefabInstance instance)
		{
			if (instance?.Source == null)
			{
				return;
			}

			PrefabInstanceOverrides overrides = GetOverrides(instance);
			foreach (GameObject added in overrides.AddedObjects)
			{
				GameObject.DestroyImmediate(added);
			}

			foreach (Behaviour added in overrides.AddedComponents)
			{
				GameObject.DestroyImmediate(added);
			}

			foreach (ulong removed in overrides.RemovedObjects)
			{
				RevertRemovedGameObject(instance, removed);
			}

			foreach (ulong removed in overrides.RemovedComponents)
			{
				RevertRemovedComponent(instance, removed);
			}

			foreach (PropertyModification modification in GetOverrides(instance).Modifications)
			{
				RevertProperty(instance, modification);
			}

			instance.UnusedModifications.Clear();
		}

		public static void RevertAddedComponent(Behaviour component)
		{
			if (IsAddedComponent(component))
			{
				GameObject.DestroyImmediate(component);
			}
		}

		public static void RevertAddedGameObject(GameObject go)
		{
			if (IsAddedGameObject(go))
			{
				GameObject.DestroyImmediate(go);
			}
		}

		/// <summary>Brings back a component of the prefab the instance removed.</summary>
		public static Behaviour RevertRemovedComponent(PrefabInstance instance, ulong sourceId)
		{
			PrefabComponent stored = instance?.Source?.FindComponent(sourceId);
			GameObject host = stored != null ? FindObject(instance, stored.Owner.Id) : null;
			if (host == null || FindComponent(instance, sourceId) != null)
			{
				return null;
			}

			SerializationContext context = ContextFor(host.scene);
			Behaviour component = CreateLinked(host, stored, instance);
			ReadInto(component, GenericValues.CloneNode(stored.Data, id => instance.Id ^ id), context);
			return component;
		}

		/// <summary>Brings back an object of the prefab (with its descendants) the instance removed.</summary>
		public static GameObject RevertRemovedGameObject(PrefabInstance instance, ulong sourceId)
		{
			ResolvedPrefab source = instance?.Source;
			PrefabObject top = source?.FindObject(sourceId);
			if (top == null || top.Parent == 0 || FindObject(instance, sourceId) != null)
			{
				return null;
			}

			GameObject parent = FindObject(instance, top.Parent);
			if (parent == null)
			{
				return null;
			}

			Scene scene = parent.scene;
			var created = new Dictionary<ulong, GameObject>();
			var reads = new List<(Behaviour component, SerializedNode data)>();
			foreach (PrefabObject obj in source.Objects)
			{
				if (!source.IsInSubtree(obj.Id, top.Id))
				{
					continue;
				}

				Transform under = obj.Id == top.Id ? parent.transform : created.TryGetValue(obj.Parent, out GameObject p) ? p.transform : null;
				if (under == null)
				{
					continue;
				}

				var go = new GameObject(obj.Name, scene, instance.Id ^ obj.Id) { hideFlags = (HideFlags)obj.HideFlags };
				go.layer = obj.Layer;
				go.prefabInstance = instance;
				go.prefabSourceId = obj.Id;
				go.transform.SetParent(under, false);
				if (!obj.Active)
				{
					go.SetActive(false);
				}

				created[obj.Id] = go;
				foreach (PrefabComponent stored in obj.Components)
				{
					reads.Add((CreateLinked(go, stored, instance), GenericValues.CloneNode(stored.Data, id => instance.Id ^ id)));
				}
			}

			SerializationContext context = ContextFor(scene);
			foreach ((Behaviour component, SerializedNode data) in reads)
			{
				ReadInto(component, data, context);
			}

			return created.TryGetValue(top.Id, out GameObject result) ? result : null;
		}

		// ---------------------------------------------------------------- applying (prefab files)

		/// <summary>
		///     Writes an overridden value into a prefab: <paramref name="target" /> from <see cref="GetApplyTargets" /> (the
		///     instance's prefab by default: the value becomes the prefab's, or an override of the prefab nested in it the
		///     object comes from; a nested prefab further down the list takes the value itself). References to objects
		///     outside that prefab are not applied (they stay overrides). Returns the prefab file written.
		/// </summary>
		public static string ApplyProperty(PrefabInstance instance, PropertyModification modification, PrefabApplyTarget target = null)
		{
			if (instance?.Source == null)
			{
				return null;
			}

			bool isObject = instance.Source.FindObject(modification.Target) != null;
			target ??= GetApplyTargets(instance, modification.Target, !isObject)[0];
			EditPrefab(target.PrefabPath, stage => ApplyPropertyTo(stage, instance, modification, target, isObject));
			return target.PrefabPath;
		}

		/// <summary>
		///     Applies several overrides to one prefab of their chains (<paramref name="prefabPath" />: the instance's prefab
		///     or one nested in it), in one write of the file. Overrides whose object does not come from that prefab are skipped.
		/// </summary>
		public static void ApplyProperties(PrefabInstance instance, IEnumerable<PropertyModification> modifications, string prefabPath)
		{
			if (instance?.Source == null)
			{
				return;
			}

			var work = new List<(PropertyModification modification, PrefabApplyTarget target, bool isObject)>();
			foreach (PropertyModification modification in modifications)
			{
				bool isObject = instance.Source.FindObject(modification.Target) != null;
				PrefabApplyTarget target = GetApplyTargets(instance, modification.Target, !isObject).Find(t => string.Equals(t.PrefabPath, prefabPath, StringComparison.OrdinalIgnoreCase));
				if (target != null)
				{
					work.Add((modification, target, isObject));
				}
			}

			if (work.Count == 0)
			{
				return;
			}

			EditPrefab(prefabPath, stage =>
			{
				foreach ((PropertyModification modification, PrefabApplyTarget target, bool isObject) in work)
				{
					ApplyPropertyTo(stage, instance, modification, target, isObject);
				}
			});
		}

		/// <summary>Applies every override of the instance (values, added and removed objects and components) to its prefab.</summary>
		public static string ApplyPrefabInstance(PrefabInstance instance)
		{
			if (instance?.Source == null)
			{
				return null;
			}

			PrefabInstanceOverrides overrides = GetOverrides(instance);
			string path = instance.Source.AssetPath;
			EditPrefab(path, stage =>
			{
				foreach (PropertyModification modification in overrides.Modifications)
				{
					bool isObject = instance.Source.FindObject(modification.Target) != null;
					ApplyPropertyTo(stage, instance, modification, GetApplyTargets(instance, modification.Target, !isObject)[0], isObject);
				}

				foreach (ulong removed in overrides.RemovedComponents)
				{
					ApplyRemovedComponentTo(stage, GetApplyTargets(instance, removed, true)[0]);
				}

				foreach (ulong removed in overrides.RemovedObjects)
				{
					ApplyRemovedGameObjectTo(stage, GetApplyTargets(instance, removed, false)[0]);
				}

				foreach (Behaviour added in overrides.AddedComponents)
				{
					ApplyAddedComponentTo(stage, instance, added, GetApplyTargets(instance, added.gameObject.prefabSourceId, false)[0]);
				}

				foreach (GameObject added in overrides.AddedObjects)
				{
					ApplyAddedGameObjectTo(stage, instance, added, GetApplyTargets(instance, added.transform.parent.gameObject.prefabSourceId, false)[0]);
				}
			});
			instance.UnusedModifications.Clear();
			return path;
		}

		/// <summary>Puts a component added to an instance into a prefab (by default the instance's); the component becomes part of the instance.</summary>
		public static string ApplyAddedComponent(Behaviour component, PrefabApplyTarget target = null)
		{
			if (!IsAddedComponent(component))
			{
				return null;
			}

			PrefabInstance instance = component.gameObject.prefabInstance;
			target ??= GetApplyTargets(instance, component.gameObject.prefabSourceId, false)[0];
			EditPrefab(target.PrefabPath, stage => ApplyAddedComponentTo(stage, instance, component, target));
			return target.PrefabPath;
		}

		/// <summary>Puts an object added under an instance's object (with its descendants) into a prefab; it becomes part of the instance.</summary>
		public static string ApplyAddedGameObject(GameObject go, PrefabApplyTarget target = null)
		{
			if (!IsAddedGameObject(go))
			{
				return null;
			}

			PrefabInstance instance = go.transform.parent.gameObject.prefabInstance;
			target ??= GetApplyTargets(instance, go.transform.parent.gameObject.prefabSourceId, false)[0];
			EditPrefab(target.PrefabPath, stage => ApplyAddedGameObjectTo(stage, instance, go, target));
			return target.PrefabPath;
		}

		/// <summary>Removes from a prefab the component the instance removed.</summary>
		public static string ApplyRemovedComponent(PrefabInstance instance, ulong sourceId, PrefabApplyTarget target = null)
		{
			if (instance?.Source?.FindComponent(sourceId) == null)
			{
				return null;
			}

			target ??= GetApplyTargets(instance, sourceId, true)[0];
			EditPrefab(target.PrefabPath, stage => ApplyRemovedComponentTo(stage, target));
			return target.PrefabPath;
		}

		/// <summary>Removes from a prefab the object the instance removed.</summary>
		public static string ApplyRemovedGameObject(PrefabInstance instance, ulong sourceId, PrefabApplyTarget target = null)
		{
			if (instance?.Source?.FindObject(sourceId) == null)
			{
				return null;
			}

			target ??= GetApplyTargets(instance, sourceId, false)[0];
			EditPrefab(target.PrefabPath, stage => ApplyRemovedGameObjectTo(stage, target));
			return target.PrefabPath;
		}

		/// <summary>
		///     Loads a prefab file as a scene of its own (as the prefab editing mode shows it), lets <paramref name="edit" />
		///     change it and writes it back.
		/// </summary>
		public static void EditPrefab(string assetPath, Action<Scene> edit)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			Scene stage = SceneSerializer.Load(file);
			try
			{
				edit(stage);
				SavePrefabScene(stage, assetPath);
			}
			finally
			{
				stage.Unload();
			}
		}

		/// <summary>Writes a scene holding a prefab's contents (prefab editing) to the prefab file; it must have one root.</summary>
		public static void SavePrefabScene(Scene scene, string assetPath)
		{
			List<GameObject> roots = scene.GetRootGameObjects();
			roots.RemoveAll(go => (go.hideFlags & HideFlags.DontSave) != 0 || go.dead);
			if (roots.Count == 0)
			{
				throw new InvalidOperationException("A prefab needs a root object.");
			}

			for (int i = 1; i < roots.Count; i++)
			{
				Debug.LogWarning($"[Prefab] {assetPath}: '{roots[i].Name}' is a second root; moved under '{roots[0].Name}' (a prefab has one root)");
				roots[i].transform.SetParent(roots[0].transform, true);
			}

			WritePrefabFile(NormalizePrefabPath(assetPath), SceneSerializer.SavePrefabToBytes(roots[0], Documents.FileFormat));
		}

		/// <summary>
		///     The scene loaded again from its own saved state: every prefab instance is built again from the current
		///     prefab files (after files changed), with its overrides. Returns the new scene (not active, lifecycle not started).
		/// </summary>
		public static Scene ReloadScene(Scene scene)
		{
			Scene reloaded = SceneSerializer.LoadFromBytes(SceneSerializer.SaveToBytes(scene));
			reloaded.Path = scene.Path;
			return reloaded;
		}

		#endregion

		#region InternalMethods

		/// <summary>A context resolving every object and component of a scene by id.</summary>
		internal static SerializationContext ContextFor(Scene scene)
		{
			var context = new SerializationContext { Scene = scene };
			if (scene == null)
			{
				return context;
			}

			foreach (GameObject go in scene.GameObjects)
			{
				context.Objects[go.Id] = go;
				foreach (Behaviour component in go.Components)
				{
					context.Objects[component.Id] = component;
				}
			}

			return context;
		}

		/// <summary>Reads generic component data into a live component (with its types, references through <paramref name="context" />).</summary>
		internal static void ReadInto(Behaviour component, SerializedNode data, SerializationContext context)
		{
			if (data == null)
			{
				return;
			}

			if (component is MissingComponent missing)
			{
				missing.Data = data;
				return;
			}

			var writer = new BinaryDocumentWriter(null) { Kind = BinaryDocument.KindObject };
			int typeId = writer.GenericTypeId(data);
			int lengthPosition = writer.Body.ReserveInt32();
			writer.WriteGenericFields(data);
			writer.Body.PatchInt32(lengthPosition, writer.Body.Length - lengthPosition - 4);
			var reader = new BinaryDocumentReader(writer.ToArray(), context);
			context.Location = component.gameObject?.Name + "/" + component.GetType().Name;
			reader.ReadFieldBlock(component, typeId);
			if (component is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnAfterDeserialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void ApplyPropertyTo(Scene stage, PrefabInstance instance, PropertyModification modification, PrefabApplyTarget target, bool isObject)
		{
			SerializationContext stageContext = ContextFor(stage);
			if (isObject)
			{
				GameObject live = FindObject(instance, modification.Target);
				if (live != null && stageContext.Objects.TryGetValue(target.TargetId, out object found) && found is GameObject staged)
				{
					switch (modification.Path)
					{
						case PropertyModification.NameProperty:
							staged.Name = live.Name;
							break;
						case PropertyModification.ActiveProperty:
							staged.SetActive(live.activeSelf);
							break;
						case PropertyModification.LayerProperty:
							staged.layer = live.layer;
							break;
					}
				}

				return;
			}

			Behaviour component = FindComponent(instance, modification.Target);
			if (component == null || !stageContext.Objects.TryGetValue(target.TargetId, out object stagedObject) || !(stagedObject is Behaviour stagedComponent))
			{
				return;
			}

			if (modification.Path == PropertyModification.EnabledProperty && !modification.HasKey)
			{
				stagedComponent.enabled = component.enabled;
				return;
			}

			SerializedNode liveData = PrefabOverrides.NodeOf(component, MappingContext(component.gameObject.scene, instance, target.Mask, stageContext));
			SerializedNode stagedData = PrefabOverrides.NodeOf(stagedComponent, stageContext);
			CopyProperty(liveData, stagedData, modification);
			ReadInto(stagedComponent, stagedData, stageContext);
		}

		private static void ApplyRemovedComponentTo(Scene stage, PrefabApplyTarget target)
		{
			if (ContextFor(stage).Objects.TryGetValue(target.TargetId, out object found) && found is Behaviour component && !(component is Transform))
			{
				GameObject.DestroyImmediate(component);
			}
		}

		private static void ApplyRemovedGameObjectTo(Scene stage, PrefabApplyTarget target)
		{
			if (ContextFor(stage).Objects.TryGetValue(target.TargetId, out object found) && found is GameObject go && go.transform.parent != null)
			{
				GameObject.DestroyImmediate(go);
			}
		}

		/// <summary>Copies an added component into the prefab being edited (its id there chosen so the live one is its instance), then links the live one.</summary>
		private static void ApplyAddedComponentTo(Scene stage, PrefabInstance instance, Behaviour component, PrefabApplyTarget target)
		{
			SerializationContext stageContext = ContextFor(stage);
			if (!stageContext.Objects.TryGetValue(target.TargetId, out object found) || !(found is GameObject host))
			{
				return;
			}

			Behaviour copy = component is MissingComponent missing ? new MissingComponent { TypeName = missing.TypeName } : (Behaviour)Activator.CreateInstance(component.GetType());
			host.LinkComponent(copy, component.Id ^ target.Mask);
			copy.enabled = component.enabled;
			stageContext.Objects[copy.Id] = copy;
			ReadInto(copy, PrefabOverrides.NodeOf(component, MappingContext(component.gameObject.scene, instance, target.Mask, stageContext)), stageContext);
			component.prefabInstance = instance;
			component.prefabSourceId = component.Id ^ instance.Id;
		}

		/// <summary>Copies an added object (with its descendants, ids chosen so the live ones are their instances) into the prefab being edited, then links the live ones.</summary>
		private static void ApplyAddedGameObjectTo(Scene stage, PrefabInstance instance, GameObject go, PrefabApplyTarget target)
		{
			SerializationContext stageContext = ContextFor(stage);
			if (!stageContext.Objects.TryGetValue(target.TargetId, out object found) || !(found is GameObject host))
			{
				return;
			}

			var inside = new HashSet<EngineObject>();
			CollectHierarchy(go, inside);
			ulong mask = target.Mask;
			Func<object, ulong> mapReference = value =>
			{
				var obj = (EngineObject)value;
				ulong id = IdOf(value) ^ mask;
				return inside.Contains(obj) || (obj.prefabInstance == instance && stageContext.Objects.ContainsKey(id)) ? id : 0;
			};
			byte[] data = SceneSerializer.SavePrefabToBytes(go, DocumentFormat.Binary, id => id ^ mask, mapReference);
			SceneSerializer.InstantiateObjects(data, stage, host.transform, false);
			foreach (EngineObject obj in inside)
			{
				obj.prefabInstance = instance;
				obj.prefabSourceId = IdOf(obj) ^ instance.Id;
			}
		}

		/// <summary>
		///     A context writing an instance's live references as the ids of the prefab being edited: objects of the same
		///     instance that prefab has (id XOR <paramref name="mask" />); anything else as null.
		/// </summary>
		private static SerializationContext MappingContext(Scene scene, PrefabInstance instance, ulong mask, SerializationContext stageContext)
		{
			return new SerializationContext
			{
				Scene = scene,
				MapReference = value =>
				{
					var obj = (EngineObject)value;
					ulong id = IdOf(value) ^ mask;
					if (obj.prefabInstance == instance && stageContext.Objects.ContainsKey(id))
					{
						return id;
					}

					Debug.LogWarning($"[Prefab] A reference to {obj.DisplayName} is not applied to the prefab: the object is not part of it (it stays an override)");
					return 0;
				}
			};
		}

		/// <summary>Copies one property (a dictionary entry, a list with its length) from one component's data to another's.</summary>
		private static void CopyProperty(SerializedNode from, SerializedNode to, PropertyModification modification)
		{
			if (modification.HasKey)
			{
				SerializedDictionary source = GenericValues.FindDictionary(from, modification.Path);
				SerializedDictionary target = GenericValues.FindDictionary(to, modification.Path);
				if (target == null)
				{
					CopyPath(from, to, modification.Path);
					return;
				}

				int index = source != null ? GenericValues.FindEntry(source, modification.Key, modification.KeyTag) : -1;
				int existing = GenericValues.FindEntry(target, modification.Key, modification.KeyTag);
				if (index < 0)
				{
					if (existing >= 0)
					{
						target.Entries.RemoveAt(existing);
					}

					return;
				}

				var entry = new KeyValuePair<object, object>(source.Entries[index].Key, source.Entries[index].Value);
				if (existing >= 0)
				{
					target.Entries[existing] = entry;
				}
				else
				{
					target.Entries.Add(entry);
				}

				return;
			}

			CopyPath(from, to, modification.Path);
		}

		/// <summary>
		///     The value at a path copied over; a list length or an element beyond the other list's end copies the whole
		///     list (its elements with it).
		/// </summary>
		private static void CopyPath(SerializedNode from, SerializedNode to, string path)
		{
			string listPath = ListOf(path);
			if (listPath != null)
			{
				bool sameLength = GenericValues.TryGet(from, PropertyPath.SizeOf(listPath), out object a, out _, out _)
					&& GenericValues.TryGet(to, PropertyPath.SizeOf(listPath), out object b, out _, out _) && Equals(a, b);
				if (!sameLength || path.EndsWith(PropertyPath.Size, StringComparison.Ordinal))
				{
					path = listPath;
				}
			}

			if (GenericValues.TryGet(from, path, out object value, out ValueTag tag, out ValueTag elementTag))
			{
				GenericValues.TrySet(to, path, value, tag, elementTag);
			}
		}

		/// <summary>The list a length or element path belongs to ("drops" of "drops.#size" or "drops[2]"), null for other paths.</summary>
		private static string ListOf(string path)
		{
			if (path.EndsWith("." + PropertyPath.Size, StringComparison.Ordinal))
			{
				return path.Substring(0, path.Length - PropertyPath.Size.Length - 1);
			}

			if (path.EndsWith("]", StringComparison.Ordinal))
			{
				int open = path.LastIndexOf('[');
				return open > 0 ? path.Substring(0, open) : null;
			}

			return null;
		}

		private static void RevertHeader(GameObject go, PrefabObject obj, string path)
		{
			switch (path)
			{
				case PropertyModification.NameProperty:
					go.Name = obj.Name;
					break;
				case PropertyModification.ActiveProperty:
					go.SetActive(obj.Active);
					break;
				case PropertyModification.LayerProperty:
					go.layer = obj.Layer;
					break;
			}
		}

		/// <summary>A component of the prefab created on a live object of its instance (fields not read yet).</summary>
		private static Behaviour CreateLinked(GameObject host, PrefabComponent stored, PrefabInstance instance)
		{
			Type type = TypeRegistry.Resolve(stored.TypeName);
			Behaviour component;
			if (type == typeof(Transform))
			{
				component = host.transform;
			}
			else if (type != null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract)
			{
				component = (Behaviour)Activator.CreateInstance(type);
				host.LinkComponent(component, instance.Id ^ stored.Id);
			}
			else
			{
				component = new MissingComponent { TypeName = stored.TypeName };
				host.LinkComponent(component, instance.Id ^ stored.Id);
			}

			component.prefabInstance = instance;
			component.prefabSourceId = stored.Id;
			if (!stored.Enabled)
			{
				component.enabled = false;
			}

			return component;
		}

		/// <summary>Links a live object to the instance of the nested prefab it comes from (unpacking one level), or to nothing.</summary>
		private static void Relink(EngineObject obj, ulong sourceId, NestedPrefab origin, PrefabInstance outer, Dictionary<ulong, GameObject> objects, Dictionary<NestedPrefab, PrefabInstance> created)
		{
			while (origin?.Parent != null)
			{
				origin = origin.Parent;
			}

			if (origin == null)
			{
				obj.prefabInstance = null;
				obj.prefabSourceId = 0;
				return;
			}

			if (!created.TryGetValue(origin, out PrefabInstance nested))
			{
				nested = new PrefabInstance
				{
					Id = outer.Id ^ origin.InstanceId,
					Prefab = Prefab.Load(origin.PrefabPath),
					PrefabPath = origin.PrefabPath,
					Source = PrefabCache.Get(origin.PrefabPath),
					Root = objects.TryGetValue(origin.RootId, out GameObject root) ? root : null
				};
				created[origin] = nested;
			}

			obj.prefabInstance = nested.Source != null ? nested : null;
			obj.prefabSourceId = nested.Source != null ? sourceId ^ origin.InstanceId : 0;
		}

		/// <summary>The live objects become an instance of a prefab saved from them (ids in the prefab: id XOR <paramref name="mask" />).</summary>
		private static void Connect(GameObject root, Prefab prefab, ulong mask, HashSet<EngineObject> inside)
		{
			ResolvedPrefab source = prefab.Resolved;
			if (source?.Root == null)
			{
				return;
			}

			var instance = new PrefabInstance { Id = mask, Prefab = prefab, PrefabPath = prefab.AssetPath, Source = source, Root = root };
			foreach (EngineObject obj in inside)
			{
				ulong id = IdOf(obj) ^ mask;
				if (source.ObjectsById.ContainsKey(id) || source.ComponentsById.ContainsKey(id))
				{
					obj.prefabInstance = instance;
					obj.prefabSourceId = id;
				}
			}
		}

		/// <summary>Every saved object and component of a hierarchy.</summary>
		private static void CollectHierarchy(GameObject root, HashSet<EngineObject> result)
		{
			if (root.dead || (root.hideFlags & HideFlags.DontSave) != 0)
			{
				return;
			}

			result.Add(root);
			foreach (Behaviour component in root.Components)
			{
				if (!component.dead)
				{
					result.Add(component);
				}
			}

			foreach (Transform child in root.transform.Children)
			{
				CollectHierarchy(child.gameObject, result);
			}
		}

		private static ulong IdOf(object value)
		{
			return value is GameObject go ? go.Id : value is Behaviour component ? component.Id : 0;
		}

		private static string NormalizePrefabPath(string assetPath)
		{
			assetPath = PrefabCache.Normalize(assetPath ?? throw new ArgumentNullException(nameof(assetPath)));
			return assetPath.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase) ? assetPath : assetPath + Prefab.Extension;
		}

		private static AssetReference ReferenceOf(string assetPath)
		{
			return new AssetReference { Guid = AssetDatabase.GetGuid(assetPath), Path = assetPath };
		}

		/// <summary>Writes a prefab file (with its meta file) and drops the cached prefabs built from it.</summary>
		private static void WritePrefabFile(string assetPath, byte[] bytes)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			File.WriteAllBytes(file, bytes);
			AssetDatabase.EnsureMeta(assetPath);
			PrefabCache.Invalidate(assetPath);
		}

		#endregion
	}
}
