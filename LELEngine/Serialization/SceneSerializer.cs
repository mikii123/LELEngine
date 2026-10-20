using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Scene files (.scene) in the two document encodings (<see cref="Documents" />). Binary body (snapshots, builds):
	///     <code>
	///     name:string-id
	///     settings: Object value (SceneSettings)
	///     objectCount:int32, then per GameObject (depth first, a parent before its children, children in sibling order):
	///       id:uint64, name:string-id, active:bool, parent:uint64 (0 = root), hideFlags:int32, layer:int32 (version 2+), componentCount:int32,
	///       per component: typeId:int32, id:uint64, enabled:bool, length:int32, fields
	///     </code>
	///     JSON body (the editor's scene files):
	///     <code>
	///     "name": "Main",
	///     "settings": { "$type": "LELEngine.SceneSettings, LELEngine", ... },
	///     "gameObjects": [ { "id": 1, "name": "Cube", "active": false, "parent": 0, "hideFlags": 0, "layer": 0,
	///                        "components": [ { "$type": "Transform, LELEngine", "$id": 2, "$enabled": true, "m_LocalPosition": [0, 0, 0], ... } ] } ]
	///     </code>
	///     (defaults are left out: active true, parent 0, hideFlags 0, layer 0, $enabled true). Loading creates every
	///     object and component first, then reads all fields (so references in any direction resolve), then calls
	///     <see cref="ISerializationCallbackReceiver.OnAfterDeserialize" />. The component lifecycle does not start:
	///     the caller decides between play and edit mode.
	/// </summary>
	public static class SceneSerializer
	{
		#region PublicFields

		public const string Extension = ".scene";

		#endregion

		#region PublicMethods

		/// <summary>Writes the scene file in <see cref="Documents.FileFormat" /> (JSON in the editor).</summary>
		public static void Save(Scene scene, string absolutePath)
		{
			Save(scene, absolutePath, Documents.FileFormat);
		}

		public static void Save(Scene scene, string absolutePath, DocumentFormat format)
		{
			string directory = Path.GetDirectoryName(absolutePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(absolutePath, SaveToBytes(scene, format));
		}

		/// <summary>The whole scene as a binary document (play mode and undo snapshots).</summary>
		public static byte[] SaveToBytes(Scene scene)
		{
			return SaveToBytes(scene, DocumentFormat.Binary);
		}

		public static byte[] SaveToBytes(Scene scene, DocumentFormat format)
		{
			if (format == DocumentFormat.Json)
			{
				return SaveToJson(scene);
			}

			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene }) { Kind = BinaryDocument.KindScene };
			writer.Body.WriteInt32(writer.StringId(scene.Name));
			writer.Context.Location = "settings";
			writer.WriteObjectValue(scene.Settings, 0);

			List<GameObject> roots = scene.GetRootGameObjects();
			int countPosition = writer.Body.ReserveInt32();
			int count = 0;
			foreach (GameObject root in roots)
			{
				count += WriteObjectTree(writer, root);
			}

			writer.Body.PatchInt32(countPosition, count);
			return writer.ToArray();
		}

		/// <summary>The scene as readable JSON text.</summary>
		public static string SaveToText(Scene scene)
		{
			return System.Text.Encoding.UTF8.GetString(SaveToJson(scene));
		}

		/// <summary>Serializes GameObjects with their children (copy, duplicate, prefab-like snapshots); binary.</summary>
		public static byte[] SerializeObjects(Scene scene, IEnumerable<GameObject> roots)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene }) { Kind = BinaryDocument.KindScene };
			writer.Body.WriteInt32(-1);
			writer.Body.WriteInt32(-1);
			int countPosition = writer.Body.ReserveInt32();
			int count = 0;
			foreach (GameObject root in roots)
			{
				count += WriteObjectTree(writer, root);
			}

			writer.Body.PatchInt32(countPosition, count);
			return writer.ToArray();
		}

		/// <summary>
		///     Creates copies of objects written by <see cref="SerializeObjects" />. Every copy gets new ids; references
		///     between the copied objects point at the copies, other references at the scene's objects with those ids.
		///     The copied roots are parented to <paramref name="parent" /> keeping their local transforms. In a running
		///     scene the copies' lifecycle starts after their fields are read.
		/// </summary>
		public static List<GameObject> InstantiateObjects(byte[] data, Scene scene, Transform parent = null)
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
				var pending = new List<PendingComponent>();
				var copied = new HashSet<ulong>();
				int count = reader.Body.ReadInt32();
				for (int i = 0; i < count; i++)
				{
					GameObject go = ReadObject(reader, scene, pending, true, copied, parent);
					if (go.transform.parent == parent)
					{
						roots.Add(go);
					}
				}

				ReadPendingFields(reader, pending);
			}
			finally
			{
				scene.ResumeActivation();
			}

			return roots;
		}

		/// <summary>
		///     Loads a scene file of either format. The returned scene is not active and its lifecycle has not started.
		///     <paramref name="progress" /> gets the fraction of components whose fields (and the assets they use) are read.
		/// </summary>
		public static Scene Load(string absolutePath, Action<float> progress = null)
		{
			byte[] data = File.ReadAllBytes(absolutePath);
			string name = Path.GetFileNameWithoutExtension(absolutePath);
			Scene scene = LoadFromBytes(data, name, progress);
			scene.Path = AssetDatabase.ToAssetPath(absolutePath) ?? absolutePath;
			return scene;
		}

		/// <summary>Loads a scene document of either format (snapshots are binary, project files JSON).</summary>
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

			// Pass 1: objects and components with their ids, hierarchy and enabled flags.
			var pending = new List<PendingComponent>();
			int count = reader.Body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				ReadObject(reader, scene, pending, false, null, null);
			}

			// Pass 2 and 3: field values (references to any object of the file resolve now), then derived state.
			ReadPendingFields(reader, pending, progress);
			return scene;
		}

		// ---------------------------------------------------------------- generic conversion (no types)

		/// <summary>Re-encodes a JSON scene as binary without resolving any type (game builds).</summary>
		public static void ConvertToBinary(JsonDocumentReader reader, BinaryDocumentWriter writer)
		{
			JsonElement root = reader.Root;
			JsonElement value;
			string name = root.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
			writer.Body.WriteInt32(writer.StringId(name));
			SerializedNode settings = root.TryGetProperty("settings", out value) && value.ValueKind == JsonValueKind.Object ? reader.ReadGenericObject(value) : null;
			writer.WriteObjectValue(settings, 0);

			int countPosition = writer.Body.ReserveInt32();
			int count = 0;
			if (root.TryGetProperty("gameObjects", out value) && value.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement entry in value.EnumerateArray())
				{
					ByteWriter body = writer.Body;
					body.WriteUInt64(GetUInt64(entry, "id"));
					body.WriteInt32(writer.StringId(GetString(entry, "name")));
					body.WriteBool(GetBool(entry, "active", true));
					body.WriteUInt64(GetUInt64(entry, "parent"));
					body.WriteInt32(GetInt32(entry, "hideFlags"));
					body.WriteInt32(GetInt32(entry, "layer"));

					int componentCountPosition = body.ReserveInt32();
					int componentCount = 0;
					JsonElement components;
					if (entry.TryGetProperty("components", out components) && components.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement component in components.EnumerateArray())
						{
							if (component.ValueKind != JsonValueKind.Object)
							{
								continue;
							}

							SerializedNode node = reader.ReadGenericObject(component);
							body.WriteInt32(writer.GenericTypeId(node));
							body.WriteUInt64(GetUInt64(component, "$id"));
							body.WriteBool(GetBool(component, "$enabled", true));
							int lengthPosition = body.ReserveInt32();
							writer.WriteGenericFields(node);
							body.PatchInt32(lengthPosition, body.Length - lengthPosition - 4);
							componentCount++;
						}
					}

					body.PatchInt32(componentCountPosition, componentCount);
					count++;
				}
			}

			writer.Body.PatchInt32(countPosition, count);
		}

		/// <summary>Re-encodes a binary scene as JSON without resolving any type (inspection, repair).</summary>
		public static void ConvertToJson(BinaryDocumentReader reader, JsonDocumentWriter writer)
		{
			ByteReader body = reader.Body;
			Utf8JsonWriter json = writer.Json;
			json.WriteString("name", reader.String(body.ReadInt32()));
			var settings = reader.ReadBoxed(ValueTag.Object, ValueTag.Null, null, null, 0) as SerializedNode;
			writer.WriteObjectProperty("settings", settings);

			json.WriteStartArray("gameObjects");
			int count = body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				json.WriteStartObject();
				json.WriteNumber("id", body.ReadUInt64());
				json.WriteString("name", reader.String(body.ReadInt32()));
				bool active = body.ReadBool();
				if (!active) json.WriteBoolean("active", false);
				ulong parent = body.ReadUInt64();
				if (parent != 0) json.WriteNumber("parent", parent);
				int hideFlags = body.ReadInt32();
				if (hideFlags != 0) json.WriteNumber("hideFlags", hideFlags);
				int layer = reader.Version >= 2 ? body.ReadInt32() : 0;
				if (layer != 0) json.WriteNumber("layer", layer);

				json.WriteStartArray("components");
				int componentCount = body.ReadInt32();
				for (int c = 0; c < componentCount; c++)
				{
					int typeId = body.ReadInt32();
					ulong id = body.ReadUInt64();
					bool enabled = body.ReadBool();
					SerializedNode node = reader.ReadGenericBlock(typeId);
					writer.Register(node);
					json.WriteStartObject();
					json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
					json.WriteNumber("$id", id);
					if (!enabled) json.WriteBoolean("$enabled", false);
					writer.WriteGenericFields(node);
					json.WriteEndObject();
				}

				json.WriteEndArray();
				json.WriteEndObject();
			}

			json.WriteEndArray();
		}

		#endregion

		#region PrivateMethods

		private struct PendingComponent
		{
			public Behaviour Component;
			public int TypeId;
			public int Position;
			public string Location;
		}

		private struct PendingJsonComponent
		{
			public Behaviour Component;
			public JsonElement Element;
			public string Location;
		}

		// ---------------------------------------------------------------- JSON

		private static byte[] SaveToJson(Scene scene)
		{
			using (var writer = new JsonDocumentWriter(new SerializationContext { Scene = scene }, BinaryDocument.KindScene))
			{
				Utf8JsonWriter json = writer.Json;
				json.WriteString("name", scene.Name);
				writer.Context.Location = "settings";
				writer.WriteObjectProperty("settings", scene.Settings);

				json.WriteStartArray("gameObjects");
				foreach (GameObject root in scene.GetRootGameObjects())
				{
					WriteObjectTree(writer, root);
				}

				json.WriteEndArray();
				return writer.ToArray();
			}
		}

		private static void WriteObjectTree(JsonDocumentWriter writer, GameObject go)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.dead)
			{
				return;
			}

			Utf8JsonWriter json = writer.Json;
			json.WriteStartObject();
			json.WriteNumber("id", go.Id);
			json.WriteString("name", go.Name);
			if (!go.activeSelf) json.WriteBoolean("active", false);
			Transform parent = go.transform.parent;
			if (parent != null) json.WriteNumber("parent", parent.gameObject.Id);
			if (go.hideFlags != HideFlags.None) json.WriteNumber("hideFlags", (int)go.hideFlags);
			if (go.layer != 0) json.WriteNumber("layer", go.layer);

			json.WriteStartArray("components");
			foreach (Behaviour component in go.Components)
			{
				if (component.dead)
				{
					continue;
				}

				json.WriteStartObject();
				if (component is MissingComponent missing)
				{
					SerializedNode node = missing.Data ?? new SerializedNode { TypeName = missing.TypeName };
					node.TypeName = missing.TypeName ?? node.TypeName ?? "Missing";
					writer.Register(node);
					json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
					json.WriteNumber("$id", component.Id);
					if (!component.enabled) json.WriteBoolean("$enabled", false);
					writer.WriteGenericFields(node);
					json.WriteEndObject();
					continue;
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

				writer.Context.Location = go.Name + "/" + component.GetType().Name;
				json.WriteString(JsonDocumentFormat.TypeKey, TypeRegistry.GetName(component.GetType()));
				json.WriteNumber("$id", component.Id);
				if (!component.enabled) json.WriteBoolean("$enabled", false);
				writer.WriteFields(component);
				json.WriteEndObject();
			}

			json.WriteEndArray();
			json.WriteEndObject();

			foreach (Transform child in go.transform.Children)
			{
				WriteObjectTree(writer, child.gameObject);
			}
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
				if (root.TryGetProperty("gameObjects", out value) && value.ValueKind == JsonValueKind.Array)
				{
					foreach (JsonElement entry in value.EnumerateArray())
					{
						if (entry.ValueKind == JsonValueKind.Object)
						{
							ReadObject(reader, entry, scene, pending);
						}
					}
				}

				for (int i = 0; i < pending.Count; i++)
				{
					PendingJsonComponent item = pending[i];
					context.Location = item.Location;
					reader.ReadFields(item.Component, item.Element);
					progress?.Invoke((i + 1f) / pending.Count);
				}

				foreach (PendingJsonComponent item in pending)
				{
					if (item.Component is ISerializationCallbackReceiver receiver)
					{
						try
						{
							receiver.OnAfterDeserialize();
						}
						catch (Exception e)
						{
							Debug.LogException(e, item.Component);
						}
					}
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

			ulong parentId = GetUInt64(entry, "parent");
			if (parentId != 0)
			{
				object parent;
				if (context.Objects.TryGetValue(parentId, out parent) && parent is GameObject parentObject)
				{
					go.transform.SetParent(parentObject.transform, false);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': parent {parentId} not found (objects must follow their parent); kept at the root");
				}
			}

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
				bool enabled = GetBool(element, "$enabled", true);
				Type type = TypeRegistry.Resolve(typeName);
				Behaviour component;
				bool missing = false;
				if (type == typeof(Transform))
				{
					component = go.transform;
					if (componentId != 0)
					{
						component.Id = componentId;
					}
				}
				else if (type != null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract)
				{
					component = CreateComponent(type, typeName, out missing);
					go.LinkComponent(component, componentId);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': component type '{typeName}' not found; kept as a missing component");
					component = new MissingComponent { TypeName = typeName };
					missing = true;
					go.LinkComponent(component, componentId);
				}

				context.Objects[component.Id] = component;
				if (!enabled)
				{
					component.enabled = false;
				}

				if (missing)
				{
					((MissingComponent)component).Data = reader.ReadGenericObject(element);
				}
				else
				{
					pending.Add(new PendingJsonComponent { Component = component, Element = element, Location = name + "/" + type.Name });
				}
			}
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

		/// <summary>Writes the object and its descendants; returns how many objects were written.</summary>
		private static int WriteObjectTree(BinaryDocumentWriter writer, GameObject go)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.dead)
			{
				return 0;
			}

			ByteWriter body = writer.Body;
			body.WriteUInt64(go.Id);
			body.WriteInt32(writer.StringId(go.Name));
			body.WriteBool(go.activeSelf);
			Transform parent = go.transform.parent;
			body.WriteUInt64(parent != null ? parent.gameObject.Id : 0);
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
				if (component is MissingComponent missing)
				{
					// Written back as read: its type name becomes a schema of the stored fields.
					SerializedNode node = missing.Data ?? new SerializedNode { TypeName = missing.TypeName };
					node.TypeName = missing.TypeName ?? node.TypeName ?? "Missing";
					body.WriteInt32(writer.GenericTypeId(node));
					body.WriteUInt64(component.Id);
					body.WriteBool(component.enabled);
					int genericLengthPosition = body.ReserveInt32();
					writer.WriteGenericFields(node);
					body.PatchInt32(genericLengthPosition, body.Length - genericLengthPosition - 4);
					continue;
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

				writer.Context.Location = go.Name + "/" + component.GetType().Name;
				body.WriteInt32(writer.TypeId(component.GetType()));
				body.WriteUInt64(component.Id);
				body.WriteBool(component.enabled);
				int lengthPosition = body.ReserveInt32();
				writer.WriteFields(component);
				body.PatchInt32(lengthPosition, body.Length - lengthPosition - 4);
			}

			body.PatchInt32(countPosition, count);

			int written = 1;
			foreach (Transform child in go.transform.Children)
			{
				written += WriteObjectTree(writer, child.gameObject);
			}

			return written;
		}

		/// <param name="remap">Instantiate copies: new ids; file ids map to the copies in the reader's context.</param>
		/// <param name="copied">With remap: file ids of the objects copied so far (parents inside the copy).</param>
		/// <param name="rootParent">With remap: parent of copied objects whose parent is not part of the copy.</param>
		private static GameObject ReadObject(BinaryDocumentReader reader, Scene scene, List<PendingComponent> pending, bool remap, HashSet<ulong> copied, Transform rootParent)
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
			if (remap && (parentId == 0 || !copied.Contains(parentId)))
			{
				if (rootParent != null)
				{
					go.transform.SetParent(rootParent, false);
				}
			}
			else if (parentId != 0)
			{
				object parent;
				if (context.Objects.TryGetValue(parentId, out parent) && parent is GameObject parentObject)
				{
					go.transform.SetParent(parentObject.transform, false);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': parent {parentId} not found (objects must follow their parent); kept at the root");
				}
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
				ulong componentId = remap ? 0 : fileComponentId;
				int length = body.ReadInt32();
				int fieldsPosition = body.Position;
				int end = fieldsPosition + length;

				Type type = reader.ResolveType(typeId);
				string typeName = reader.Schema(typeId)?.Name;
				Behaviour component;
				bool missing = false;
				if (type == typeof(Transform))
				{
					component = go.transform;
					if (componentId != 0)
					{
						component.Id = componentId;
					}
				}
				else if (type != null && typeof(Behaviour).IsAssignableFrom(type) && !type.IsAbstract)
				{
					component = CreateComponent(type, typeName, out missing);
					go.LinkComponent(component, componentId);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': component type '{typeName}' not found; kept as a missing component");
					component = new MissingComponent { TypeName = typeName };
					missing = true;
					go.LinkComponent(component, componentId);
				}

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
					pending.Add(new PendingComponent { Component = component, TypeId = typeId, Position = fieldsPosition - 4, Location = name + "/" + type.Name });
				}

				body.Position = end;
			}

			return go;
		}

		private static void ReadPendingFields(BinaryDocumentReader reader, List<PendingComponent> pending, Action<float> progress = null)
		{
			for (int i = 0; i < pending.Count; i++)
			{
				PendingComponent item = pending[i];
				reader.Context.Location = item.Location;
				reader.Body.Position = item.Position;
				reader.ReadFieldBlock(item.Component, item.TypeId);
				progress?.Invoke((i + 1f) / pending.Count);
			}

			foreach (PendingComponent item in pending)
			{
				if (item.Component is ISerializationCallbackReceiver receiver)
				{
					try
					{
						receiver.OnAfterDeserialize();
					}
					catch (Exception e)
					{
						Debug.LogException(e, item.Component);
					}
				}
			}
		}

		#endregion
	}
}
