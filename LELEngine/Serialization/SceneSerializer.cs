using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Scene files (.scene) and prefab files (.prefab, <see cref="Prefab" />) in the two document encodings
	///     (<see cref="Documents" />). A document holds entries in hierarchy order (a parent before its children):
	///     GameObjects with their components, and prefab instances (<see cref="PrefabInstanceRecord" />: the prefab and
	///     how the instance differs from it; objects added under an instance's objects are ordinary entries after it).
	///     A prefab document has one root and no settings. Binary body (snapshots, builds):
	///     <code>
	///     name:string-id
	///     settings: Object value (SceneSettings; null in prefabs)
	///     entryCount:int32, then per entry (version 3+): kind:byte (0 GameObject, 1 prefab instance), then
	///       GameObject: id:uint64, name:string-id, active:bool, parent:uint64 (0 = root), hideFlags:int32, layer:int32 (version 2+), componentCount:int32,
	///                   per component: typeId:int32, id:uint64, enabled:bool, length:int32, fields
	///       instance:   id:uint64, prefab: Asset value, parent:uint64, modificationCount:int32,
	///                   per modification: target:uint64, path:string-id, flags:byte (1 key, 2 removed), [keyTag:byte, key], [tag:byte, elementTag:byte, value],
	///                   removedObjects: count:int32, uint64*, removedComponents: count:int32, uint64*,
	///                   addedComponents: count:int32, per component: gameObject:uint64, then as above
	///     </code>
	///     JSON body (the editor's files):
	///     <code>
	///     "name": "Main",
	///     "settings": { "$type": "LELEngine.SceneSettings, LELEngine", ... },
	///     "gameObjects": [ { "id": 1, "name": "Cube", "active": false, "parent": 0, "hideFlags": 0, "layer": 0,
	///                        "components": [ { "$type": "Transform, LELEngine", "$id": 2, "$enabled": true, "m_LocalPosition": [0, 0, 0], ... } ] },
	///                      { "prefabInstance": 77, "prefab": { "guid": "...", "path": "Enemy.prefab" }, "modifications": [ ... ] } ]
	///     </code>
	///     (defaults are left out: active true, parent 0, hideFlags 0, layer 0, $enabled true). Loading creates every
	///     object and component first (prefab instances from their prefabs, <see cref="PrefabCache" />), then reads all
	///     fields (so references in any direction resolve), then calls
	///     <see cref="ISerializationCallbackReceiver.OnAfterDeserialize" />. The component lifecycle does not start:
	///     the caller decides between play and edit mode.
	/// </summary>
	public static partial class SceneSerializer
	{
		#region PublicFields

		public const string Extension = ".scene";

		#endregion

		#region InternalFields

		internal const string PrefabInstanceKey = "prefabInstance";

		#endregion

		#region PublicMethods

		/// <summary>Writes the scene file in <see cref="Documents.FileFormat" /> (JSON in the editor).</summary>
		public static void Save(Scene scene, string absolutePath)
		{
			Save(scene, absolutePath, Documents.FileFormat);
		}

		public static void Save(Scene scene, string absolutePath, DocumentFormat format)
		{
			WriteFile(absolutePath, SaveToBytes(scene, format));
		}

		/// <summary>The whole scene as a binary document (play mode and undo snapshots).</summary>
		public static byte[] SaveToBytes(Scene scene)
		{
			return SaveToBytes(scene, DocumentFormat.Binary);
		}

		public static byte[] SaveToBytes(Scene scene, DocumentFormat format)
		{
			var state = new WriteState();
			if (format == DocumentFormat.Json)
			{
				using (var writer = new JsonDocumentWriter(new SerializationContext { Scene = scene }, BinaryDocument.KindScene))
				{
					writer.Json.WriteString("name", scene.Name);
					writer.Context.Location = "settings";
					writer.WriteObjectProperty("settings", scene.Settings);
					WriteEntries(writer, scene.GetRootGameObjects(), state);
					return writer.ToArray();
				}
			}

			var binary = new BinaryDocumentWriter(new SerializationContext { Scene = scene }) { Kind = BinaryDocument.KindScene };
			binary.Body.WriteInt32(binary.StringId(scene.Name));
			binary.Context.Location = "settings";
			binary.WriteObjectValue(scene.Settings, 0);
			WriteEntries(binary, scene.GetRootGameObjects(), state);
			return binary.ToArray();
		}

		/// <summary>The scene as readable JSON text.</summary>
		public static string SaveToText(Scene scene)
		{
			return System.Text.Encoding.UTF8.GetString(SaveToBytes(scene, DocumentFormat.Json));
		}

		/// <summary>
		///     A prefab document of <paramref name="root" /> and its descendants. <paramref name="mapId" /> maps the ids
		///     written (objects, components, prefab instances; null: as they are), <paramref name="mapReference" /> the
		///     references of the fields (null: the scene's ids); the root is written without a parent.
		/// </summary>
		public static byte[] SavePrefabToBytes(GameObject root, DocumentFormat format, Func<ulong, ulong> mapId = null, Func<object, ulong> mapReference = null)
		{
			var state = new WriteState { MapId = mapId, Root = root };
			var context = new SerializationContext { Scene = root.scene, MapReference = mapReference };
			var roots = new List<GameObject> { root };
			if (format == DocumentFormat.Json)
			{
				using (var writer = new JsonDocumentWriter(context, BinaryDocument.KindPrefab))
				{
					writer.Json.WriteString("name", root.Name);
					WriteEntries(writer, roots, state);
					return writer.ToArray();
				}
			}

			var binary = new BinaryDocumentWriter(context) { Kind = BinaryDocument.KindPrefab };
			binary.Body.WriteInt32(binary.StringId(root.Name));
			binary.WriteObjectValue(null, 0);
			WriteEntries(binary, roots, state);
			return binary.ToArray();
		}

		/// <summary>Serializes GameObjects with their children (copy, duplicate); binary. Prefab instances stay instances.</summary>
		public static byte[] SerializeObjects(Scene scene, IEnumerable<GameObject> roots)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene }) { Kind = BinaryDocument.KindScene };
			writer.Body.WriteInt32(-1);
			writer.Body.WriteInt32(-1);
			WriteEntries(writer, roots, new WriteState());
			return writer.ToArray();
		}

		/// <summary>
		///     Creates copies of objects written by <see cref="SerializeObjects" />. Every copy gets new ids (a copied prefab
		///     instance a new instance id); references between the copied objects point at the copies, other references at
		///     the scene's objects with those ids. The copied roots are parented to <paramref name="parent" /> keeping their
		///     local transforms. In a running scene the copies' lifecycle starts after their fields are read.
		/// </summary>
		public static List<GameObject> InstantiateObjects(byte[] data, Scene scene, Transform parent = null)
		{
			return InstantiateObjects(data, scene, parent, true);
		}

		/// <summary>
		///     <see cref="InstantiateObjects(byte[], Scene, Transform)" />; without <paramref name="remap" /> the objects keep the
		///     ids of the data (objects copied into a prefab being edited, whose ids were chosen for it).
		/// </summary>
		internal static List<GameObject> InstantiateObjects(byte[] data, Scene scene, Transform parent, bool remap, Action<List<GameObject>> beforeActivation = null)
		{
			var roots = new List<GameObject>();
			var context = new SerializationContext { Scene = scene };
			foreach (GameObject go in scene.GameObjects)
			{
				context.Objects[go.Id] = go;
				foreach (Behaviour component in go.Components)
				{
					context.Objects[component.Id] = component;
				}
			}

			var reader = new BinaryDocumentReader(data, context);
			reader.Body.ReadInt32();
			reader.SkipValue(ValueTag.Object, ValueTag.Null);

			scene.SuspendActivation();
			try
			{
				var pending = new List<PendingRead>();
				var instancePending = new List<PendingRead>();
				var copied = new HashSet<ulong>();
				int count = reader.Body.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					byte kind = reader.Version >= 3 ? reader.Body.ReadByte() : BinaryDocument.EntryObject;
					GameObject go = kind == BinaryDocument.EntryPrefabInstance
						? LoadInstance(ReadInstanceRecord(reader), scene, context, instancePending, remap, copied, parent)
						: ReadObject(reader, scene, pending, remap, copied, parent);
					if (go != null && go.transform.parent == parent)
					{
						roots.Add(go);
					}
				}

				pending.AddRange(instancePending);
				ReadPendingFields(pending, null);
				beforeActivation?.Invoke(roots);
			}
			finally
			{
				scene.ResumeActivation();
			}

			return roots;
		}

		/// <summary>
		///     Loads a scene (or prefab) file of either format. The returned scene is not active and its lifecycle has not
		///     started. <paramref name="progress" /> gets the fraction of components whose fields (and the assets they use) are read.
		/// </summary>
		public static Scene Load(string absolutePath, Action<float> progress = null)
		{
			byte[] data = File.ReadAllBytes(absolutePath);
			string name = Path.GetFileNameWithoutExtension(absolutePath);
			Scene scene = LoadFromBytes(data, name, progress);
			scene.Path = AssetDatabase.ToAssetPath(absolutePath) ?? absolutePath;
			return scene;
		}

		/// <summary>Loads a scene (or prefab) document of either format (snapshots are binary, project files JSON).</summary>
		public static Scene LoadFromBytes(byte[] data, string fallbackName = null, Action<float> progress = null)
		{
			if (Documents.IsJson(data))
			{
				return LoadFromJson(data, fallbackName, progress);
			}

			var context = new SerializationContext();
			var reader = new BinaryDocumentReader(data, context);
			string name = reader.String(reader.Body.ReadInt32());
			var scene = new Scene(name ?? fallbackName ?? "Untitled");
			context.Scene = scene;

			context.Location = "settings";
			scene.Settings = reader.ReadObjectValue(typeof(SceneSettings), scene.Settings, 0) as SceneSettings ?? new SceneSettings();

			// Pass 1: objects and components with their ids, hierarchy and enabled flags (prefab instances built from their prefabs).
			var pending = new List<PendingRead>();
			var instancePending = new List<PendingRead>();
			int count = reader.Body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				byte kind = reader.Version >= 3 ? reader.Body.ReadByte() : BinaryDocument.EntryObject;
				if (kind == BinaryDocument.EntryPrefabInstance)
				{
					LoadInstance(ReadInstanceRecord(reader), scene, context, instancePending, false, null, null);
				}
				else
				{
					ReadObject(reader, scene, pending, false, null, null);
				}
			}

			// Pass 2 and 3: field values (references to any object of the file resolve now), then derived state.
			pending.AddRange(instancePending);
			ReadPendingFields(pending, progress);
			return scene;
		}

		/// <summary>
		///     The document's data without resolving any type: the settings, every component and every prefab instance's
		///     data (prefab reference, override values, added components) as generic nodes (build dependency scans read the
		///     asset references and type names in them).
		/// </summary>
		public static List<SerializedNode> ReadGenericNodes(byte[] data)
		{
			SceneDocument document = ReadDocument(data);
			var nodes = new List<SerializedNode>();
			if (document.Settings != null)
			{
				nodes.Add(document.Settings);
			}

			foreach (DocumentEntry entry in document.Entries)
			{
				if (entry.Object != null)
				{
					foreach (DocumentComponent component in entry.Object.Components)
					{
						nodes.Add(component.Data);
					}

					continue;
				}

				PrefabInstanceRecord record = entry.Instance;
				var node = new SerializedNode { TypeName = "Object" };
				node.Properties.Add(new SerializedProperty { Name = "prefab", Tag = ValueTag.Asset, Value = record.Prefab });
				foreach (PropertyModification modification in record.Modifications)
				{
					node.Properties.Add(new SerializedProperty { Name = modification.Path, Tag = modification.Tag, ElementTag = modification.ElementTag, Value = modification.Value });
					if (modification.HasKey)
					{
						node.Properties.Add(new SerializedProperty { Name = modification.Path, Tag = modification.KeyTag, Value = modification.Key });
					}
				}

				nodes.Add(node);
				foreach (DocumentComponent component in record.AddedComponents)
				{
					nodes.Add(component.Data);
				}
			}

			nodes.RemoveAll(node => node == null);
			return nodes;
		}

		#endregion

		#region PrivateMethods

		/// <summary>A component whose fields are read in pass 2 (after every object exists): from which reader, where, in which instance.</summary>
		internal struct PendingRead
		{
			public Behaviour Component;
			public BinaryDocumentReader Reader;
			public int TypeId;
			public int Position;
			public string Location;

			/// <summary>The file's instance id of a prefab instance's component (its prefab's ids resolve through it).</summary>
			public ulong InstanceId;
		}

		private struct PendingJsonComponent
		{
			public Behaviour Component;
			public JsonElement Element;
			public string Location;
		}

		/// <summary>How a document is written: the ids it maps (a prefab saved from scene objects) and the root written without a parent.</summary>
		private sealed class WriteState
		{
			public Func<ulong, ulong> MapId;
			public GameObject Root;

			public ulong Map(ulong id)
			{
				return MapId != null && id != 0 ? MapId(id) : id;
			}

			public ulong ParentOf(GameObject go)
			{
				if (go == Root)
				{
					return 0;
				}

				Transform parent = go.transform.parent;
				return parent != null ? Map(parent.gameObject.Id) : Map(go.unresolvedParent);
			}
		}

		private static void WriteFile(string absolutePath, byte[] bytes)
		{
			string directory = Path.GetDirectoryName(absolutePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(absolutePath, bytes);
		}

		// ---------------------------------------------------------------- JSON

		private static void WriteEntries(JsonDocumentWriter writer, IEnumerable<GameObject> roots, WriteState state)
		{
			writer.Json.WriteStartArray("gameObjects");
			foreach (GameObject root in roots)
			{
				WriteObjectTree(writer, root, null, state);
			}

			writer.Json.WriteEndArray();
		}

		/// <summary>
		///     An object and its descendants. A prefab instance's root writes the instance; the objects of the instance
		///     (<paramref name="covered" />) are part of it, the objects added under them are written after it.
		/// </summary>
		private static void WriteObjectTree(JsonDocumentWriter writer, GameObject go, PrefabInstance covered, WriteState state)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.dead)
			{
				return;
			}

			PrefabInstance instance = go.prefabInstance;
			if (instance != null && instance.Root == go)
			{
				WriteInstanceRecord(writer, InstanceRecordOf(instance, go, writer.Context, state));
				covered = instance;
			}
			else if (instance == null || instance != covered)
			{
				WriteObject(writer, go, state);
				covered = null;
			}

			foreach (Transform child in go.transform.Children)
			{
				WriteObjectTree(writer, child.gameObject, covered, state);
			}
		}

		private static PrefabInstanceRecord InstanceRecordOf(PrefabInstance instance, GameObject root, SerializationContext context, WriteState state)
		{
			context.Location = root.Name + " (prefab instance)";
			PrefabInstanceRecord record = PrefabOverrides.Compute(instance, context, state.MapId);
			record.Parent = state.ParentOf(root);
			return record;
		}

		private static void WriteObject(JsonDocumentWriter writer, GameObject go, WriteState state)
		{
			Utf8JsonWriter json = writer.Json;
			json.WriteStartObject();
			json.WriteNumber("id", state.Map(go.Id));
			json.WriteString("name", go.Name);
			if (!go.activeSelf) json.WriteBoolean("active", false);
			ulong parent = state.ParentOf(go);
			if (parent != 0) json.WriteNumber("parent", parent);
			if (go.hideFlags != HideFlags.None) json.WriteNumber("hideFlags", (int)go.hideFlags);
			if (go.layer != 0) json.WriteNumber("layer", go.layer);

			json.WriteStartArray("components");
			foreach (Behaviour component in go.Components)
			{
				if (!component.dead)
				{
					WriteComponent(writer, component, state.Map(component.Id), null);
				}
			}

			json.WriteEndArray();
			json.WriteEndObject();
		}

		/// <summary>
		///     A live component as a JSON object: "$type", "$id", "$enabled" and its fields; a missing script's data as it
		///     was read. <paramref name="addedTo" /> writes the GameObject of a component added to a prefab instance.
		/// </summary>
		private static void WriteComponent(JsonDocumentWriter writer, Behaviour component, ulong id, ulong? addedTo)
		{
			Utf8JsonWriter json = writer.Json;
			json.WriteStartObject();
			if (addedTo.HasValue) json.WriteNumber("gameObject", addedTo.Value);
			if (component is MissingComponent missing)
			{
				SerializedNode node = missing.Data ?? new SerializedNode { TypeName = missing.TypeName };
				node.TypeName = missing.TypeName ?? node.TypeName ?? "Missing";
				writer.Register(node);
				json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
				json.WriteNumber("$id", id);
				if (!component.enabled) json.WriteBoolean("$enabled", false);
				writer.WriteGenericFields(node);
				json.WriteEndObject();
				return;
			}

			if (component is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnBeforeSerialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}

			writer.Context.Location = component.gameObject?.Name + "/" + component.GetType().Name;
			json.WriteString(JsonDocumentFormat.TypeKey, TypeRegistry.GetName(component.GetType()));
			json.WriteNumber("$id", id);
			if (!component.enabled) json.WriteBoolean("$enabled", false);
			writer.WriteFields(component);
			json.WriteEndObject();
		}

		private static Scene LoadFromJson(byte[] data, string fallbackName, Action<float> progress)
		{
			var context = new SerializationContext();
			using (var reader = new JsonDocumentReader(data, context))
			{
				JsonElement root = reader.Root;
				string name = GetString(root, "name");
				var scene = new Scene(name ?? fallbackName ?? "Untitled");
				context.Scene = scene;

				JsonElement value;
				if (root.TryGetProperty("settings", out value))
				{
					context.Location = "settings";
					scene.Settings = reader.ReadObjectValue(typeof(SceneSettings), scene.Settings, value, 0) as SceneSettings ?? new SceneSettings();
				}

				var pending = new List<PendingJsonComponent>();
				var instancePending = new List<PendingRead>();
				if (root.TryGetProperty("gameObjects", out value) && value.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement entry in value.EnumerateArray())
					{
						if (entry.ValueKind != JsonValueKind.Object)
						{
							continue;
						}

						if (IsInstanceEntry(entry))
						{
							LoadInstance(ReadInstanceRecord(reader, entry), scene, context, instancePending, false, null, null);
						}
						else
						{
							ReadObject(reader, entry, scene, pending);
						}
					}
				}

				int total = pending.Count + instancePending.Count;
				for (int i = 0; i < pending.Count; i++)
				{
					PendingJsonComponent item = pending[i];
					context.Location = item.Location;
					reader.ReadFields(item.Component, item.Element);
					progress?.Invoke((i + 1f) / total);
				}

				ReadFields(instancePending, i => progress?.Invoke((pending.Count + i + 1f) / total));
				foreach (PendingJsonComponent item in pending)
				{
					CallAfterDeserialize(item.Component);
				}

				foreach (PendingRead item in instancePending)
				{
					CallAfterDeserialize(item.Component);
				}

				return scene;
			}
		}

		private static void ReadObject(JsonDocumentReader reader, JsonElement entry, Scene scene, List<PendingJsonComponent> pending)
		{
			SerializationContext context = reader.Context;
			ulong id = GetUInt64(entry, "id");
			string name = GetString(entry, "name") ?? "GameObject";
			var go = new GameObject(name, scene, id);
			context.Objects[go.Id] = go;
			go.hideFlags = (HideFlags)GetInt32(entry, "hideFlags");
			go.layer = GetInt32(entry, "layer");
			SetParent(go, GetUInt64(entry, "parent"), context);

			if (!GetBool(entry, "active", true))
			{
				go.SetActive(false);
			}

			JsonElement components;
			if (!entry.TryGetProperty("components", out components) || components.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			foreach (JsonElement element in components.EnumerateArray())
			{
				if (element.ValueKind != JsonValueKind.Object)
				{
					continue;
				}

				string typeName = JsonDocumentReader.TypeNameOf(element);
				ulong componentId = GetUInt64(element, "$id");
				Behaviour component = CreateComponentOf(go, TypeRegistry.Resolve(typeName), typeName, componentId, out bool missing);
				context.Objects[component.Id] = component;
				if (!GetBool(element, "$enabled", true))
				{
					component.enabled = false;
				}

				if (missing)
				{
					((MissingComponent)component).Data = reader.ReadGenericObject(element);
				}
				else
				{
					pending.Add(new PendingJsonComponent { Component = component, Element = element, Location = name + "/" + component.GetType().Name });
				}
			}
		}

		/// <summary>Puts a read object under its parent (an object read before it); a parent the file lacks is kept for writing back.</summary>
		private static void SetParent(GameObject go, ulong parentId, SerializationContext context)
		{
			if (parentId == 0)
			{
				return;
			}

			if (context.Objects.TryGetValue(parentId, out object parent) && parent is GameObject parentObject)
			{
				go.transform.SetParent(parentObject.transform, false);
				return;
			}

			go.unresolvedParent = parentId;
			Debug.LogWarning($"[Scene] '{go.Name}': parent {parentId} not found (objects must follow their parent; or its prefab is missing); kept at the root");
		}

		/// <summary>
		///     The component of a read entry: the object's Transform, a new instance of the type, or a missing component
		///     holding the data of a type that does not exist (any more).
		/// </summary>
		private static Behaviour CreateComponentOf(GameObject go, Type type, string typeName, ulong id, out bool missing)
		{
			missing = false;
			Behaviour component;
			if (type == typeof(Transform))
			{
				component = go.transform;
				if (id != 0)
				{
					component.Id = id;
				}

				return component;
			}

			if (type != null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract)
			{
				component = CreateComponent(type, typeName, out missing);
			}
			else
			{
				Debug.LogWarning($"[Scene] '{go.Name}': component type '{typeName}' not found; kept as a missing component");
				component = new MissingComponent { TypeName = typeName };
				missing = true;
			}

			go.LinkComponent(component, id);
			return component;
		}

		private static Behaviour CreateComponent(Type type, string typeName, out bool missing)
		{
			missing = false;
			try
			{
				return (Behaviour)Activator.CreateInstance(type);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
				missing = true;
				return new MissingComponent { TypeName = typeName };
			}
		}

		private static void CallAfterDeserialize(Behaviour component)
		{
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

		private static string GetString(JsonElement element, string name)
		{
			JsonElement value;
			return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
		}

		private static ulong GetUInt64(JsonElement element, string name)
		{
			JsonElement value;
			if (!element.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Number)
			{
				return 0;
			}

			ulong result;
			if (value.TryGetUInt64(out result))
			{
				return result;
			}

			long signed;
			return value.TryGetInt64(out signed) ? unchecked((ulong)signed) : 0;
		}

		private static int GetInt32(JsonElement element, string name)
		{
			JsonElement value;
			return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
		}

		private static bool GetBool(JsonElement element, string name, bool fallback)
		{
			JsonElement value;
			if (!element.TryGetProperty(name, out value))
			{
				return fallback;
			}

			return value.ValueKind == JsonValueKind.True || (value.ValueKind != JsonValueKind.False && fallback);
		}

		// ---------------------------------------------------------------- binary

		private static void WriteEntries(BinaryDocumentWriter writer, IEnumerable<GameObject> roots, WriteState state)
		{
			int countPosition = writer.Body.ReserveInt32();
			int count = 0;
			foreach (GameObject root in roots)
			{
				count += WriteObjectTree(writer, root, null, state);
			}

			writer.Body.PatchInt32(countPosition, count);
		}

		/// <summary>Writes the object and its descendants (see the JSON overload); returns how many entries were written.</summary>
		private static int WriteObjectTree(BinaryDocumentWriter writer, GameObject go, PrefabInstance covered, WriteState state)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.dead)
			{
				return 0;
			}

			int written = 0;
			PrefabInstance instance = go.prefabInstance;
			if (instance != null && instance.Root == go)
			{
				WriteInstanceRecord(writer, InstanceRecordOf(instance, go, writer.Context, state));
				covered = instance;
				written++;
			}
			else if (instance == null || instance != covered)
			{
				WriteObject(writer, go, state);
				covered = null;
				written++;
			}

			foreach (Transform child in go.transform.Children)
			{
				written += WriteObjectTree(writer, child.gameObject, covered, state);
			}

			return written;
		}

		private static void WriteObject(BinaryDocumentWriter writer, GameObject go, WriteState state)
		{
			ByteWriter body = writer.Body;
			body.WriteByte(BinaryDocument.EntryObject);
			body.WriteUInt64(state.Map(go.Id));
			body.WriteInt32(writer.StringId(go.Name));
			body.WriteBool(go.activeSelf);
			body.WriteUInt64(state.ParentOf(go));
			body.WriteInt32((int)go.hideFlags);
			body.WriteInt32(go.layer);

			int countPosition = body.ReserveInt32();
			int count = 0;
			foreach (Behaviour component in go.Components)
			{
				if (component.dead)
				{
					continue;
				}

				count++;
				WriteComponent(writer, component, state.Map(component.Id));
			}

			body.PatchInt32(countPosition, count);
		}

		/// <summary>typeId, id, enabled, length, fields of a live component (a missing script's data written back as it was read).</summary>
		private static void WriteComponent(BinaryDocumentWriter writer, Behaviour component, ulong id)
		{
			ByteWriter body = writer.Body;
			if (component is MissingComponent missing)
			{
				// Written back as read: its type name becomes a schema of the stored fields.
				SerializedNode node = missing.Data ?? new SerializedNode { TypeName = missing.TypeName };
				node.TypeName = missing.TypeName ?? node.TypeName ?? "Missing";
				body.WriteInt32(writer.GenericTypeId(node));
				body.WriteUInt64(id);
				body.WriteBool(component.enabled);
				int genericLengthPosition = body.ReserveInt32();
				writer.WriteGenericFields(node);
				body.PatchInt32(genericLengthPosition, body.Length - genericLengthPosition - 4);
				return;
			}

			if (component is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnBeforeSerialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e, component);
				}
			}

			writer.Context.Location = component.gameObject?.Name + "/" + component.GetType().Name;
			body.WriteInt32(writer.TypeId(component.GetType()));
			body.WriteUInt64(id);
			body.WriteBool(component.enabled);
			int lengthPosition = body.ReserveInt32();
			writer.WriteFields(component);
			body.PatchInt32(lengthPosition, body.Length - lengthPosition - 4);
		}

		/// <param name="remap">Instantiate copies: new ids; file ids map to the copies in the reader's context.</param>
		/// <param name="copied">With remap: file ids of the objects copied so far (parents inside the copy).</param>
		/// <param name="rootParent">With remap: parent of copied objects whose parent is not part of the copy.</param>
		private static GameObject ReadObject(BinaryDocumentReader reader, Scene scene, List<PendingRead> pending, bool remap, HashSet<ulong> copied, Transform rootParent)
		{
			ByteReader body = reader.Body;
			SerializationContext context = reader.Context;
			ulong id = body.ReadUInt64();
			string name = reader.String(body.ReadInt32()) ?? "GameObject";
			bool active = body.ReadBool();
			ulong parentId = body.ReadUInt64();
			var hideFlags = (HideFlags)body.ReadInt32();
			int layer = reader.Version >= 2 ? body.ReadInt32() : 0;
			int componentCount = body.ReadInt32();

			var go = new GameObject(name, scene, remap ? 0 : id);
			context.Objects[id != 0 ? id : go.Id] = go;
			if (remap && id != 0)
			{
				copied.Add(id);
			}

			go.hideFlags = hideFlags;
			go.layer = layer;
			if (parentId == 0 || (remap && !copied.Contains(parentId)))
			{
				if (rootParent != null)
				{
					go.transform.SetParent(rootParent, false);
				}
			}
			else
			{
				SetParent(go, parentId, context);
			}

			if (!active)
			{
				go.SetActive(false);
			}

			for (int i = 0; i < componentCount; i++)
			{
				int typeId = body.ReadInt32();
				ulong fileComponentId = body.ReadUInt64();
				bool enabled = body.ReadBool();
				int length = body.ReadInt32();
				int fieldsPosition = body.Position;
				int end = fieldsPosition + length;

				string typeName = reader.Schema(typeId)?.Name;
				Behaviour component = CreateComponentOf(go, reader.ResolveType(typeId), typeName, remap ? 0 : fileComponentId, out bool missing);
				context.Objects[fileComponentId != 0 ? fileComponentId : component.Id] = component;
				if (!enabled)
				{
					component.enabled = false;
				}

				if (missing)
				{
					// Generic data has no references to resolve: read it now.
					body.Position = fieldsPosition - 4;
					((MissingComponent)component).Data = reader.ReadGenericBlock(typeId);
				}
				else
				{
					// Fields are read in pass 2 (after every object exists).
					pending.Add(new PendingRead { Component = component, Reader = reader, TypeId = typeId, Position = fieldsPosition - 4, Location = name + "/" + component.GetType().Name });
				}

				body.Position = end;
			}

			return go;
		}

		private static void ReadPendingFields(List<PendingRead> pending, Action<float> progress)
		{
			ReadFields(pending, progress == null ? (Action<int>)null : i => progress((i + 1f) / pending.Count));
			foreach (PendingRead item in pending)
			{
				CallAfterDeserialize(item.Component);
			}
		}

		/// <summary>Reads the fields of pending components (each from its reader, references of instances through their instance id).</summary>
		private static void ReadFields(List<PendingRead> pending, Action<int> progress)
		{
			for (int i = 0; i < pending.Count; i++)
			{
				PendingRead item = pending[i];
				SerializationContext context = item.Reader.Context;
				context.Location = item.Location;
				context.InstanceId = item.InstanceId;
				item.Reader.Body.Position = item.Position;
				item.Reader.ReadFieldBlock(item.Component, item.TypeId);
				context.InstanceId = 0;
				progress?.Invoke(i);
			}
		}

		#endregion
	}
}
