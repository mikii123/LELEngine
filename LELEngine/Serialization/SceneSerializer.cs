using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Scene files (.scene, JSON):
	///     <code>
	///     {
	///       "format": "LELEngine.Scene", "version": 1, "name": "...",
	///       "settings": { "Environment": {...}, "Shadows": {...}, "GI": {...} },
	///       "gameObjects": [
	///         { "id": 123, "name": "Floor", "active": true, "parent": null,
	///           "components": [ { "type": "Transform, LELEngine", "id": 124, "enabled": true, "fields": {...} }, ... ] },
	///         ...
	///       ]
	///     }
	///     </code>
	///     Objects are written depth first (a parent before its children, children in sibling order). Loading
	///     creates every object and component first, then reads all fields (so references in any direction
	///     resolve), then calls <see cref="ISerializationCallbackReceiver.OnAfterDeserialize" />. The component
	///     lifecycle does not start: the caller decides between play and edit mode.
	/// </summary>
	public static class SceneSerializer
	{
		#region PublicFields

		public const string Extension = ".scene";
		public const string Format = "LELEngine.Scene";
		public const int Version = 1;

		#endregion

		#region PublicMethods

		public static void Save(Scene scene, string absolutePath)
		{
			string directory = Path.GetDirectoryName(absolutePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllText(absolutePath, SaveToString(scene), new UTF8Encoding(false));
		}

		public static string SaveToString(Scene scene)
		{
			var context = new SerializationContext { Scene = scene };
			var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
			using (var stream = new MemoryStream())
			{
				using (var writer = new Utf8JsonWriter(stream, options))
				{
					writer.WriteStartObject();
					writer.WriteString("format", Format);
					writer.WriteNumber("version", Version);
					writer.WriteString("name", scene.Name);

					writer.WritePropertyName("settings");
					writer.WriteStartObject();
					context.Location = "settings";
					ValueSerializer.WriteFields(writer, scene.Settings, context);
					writer.WriteEndObject();

					writer.WritePropertyName("gameObjects");
					writer.WriteStartArray();
					foreach (GameObject root in scene.GetRootGameObjects())
					{
						WriteObjectTree(writer, root, context);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		/// <summary>Serializes GameObjects with their children (copy, duplicate, prefab-like snapshots).</summary>
		public static string SerializeObjects(Scene scene, IEnumerable<GameObject> roots)
		{
			var context = new SerializationContext { Scene = scene };
			var options = new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
			using (var stream = new MemoryStream())
			{
				using (var writer = new Utf8JsonWriter(stream, options))
				{
					writer.WriteStartObject();
					writer.WritePropertyName("gameObjects");
					writer.WriteStartArray();
					foreach (GameObject root in roots)
					{
						WriteObjectTree(writer, root, context);
					}

					writer.WriteEndArray();
					writer.WriteEndObject();
				}

				return Encoding.UTF8.GetString(stream.ToArray());
			}
		}

		/// <summary>
		///     Creates copies of objects written by <see cref="SerializeObjects" />. Every copy gets new ids; references
		///     between the copied objects point at the copies, other references at the scene's objects with those ids.
		///     The copied roots are parented to <paramref name="parent" /> keeping their local transforms. In a running
		///     scene the copies' lifecycle starts after their fields are read.
		/// </summary>
		public static List<GameObject> InstantiateObjects(string json, Scene scene, Transform parent = null)
		{
			var roots = new List<GameObject>();
			using (JsonDocument document = JsonDocument.Parse(json))
			{
				var context = new SerializationContext { Scene = scene };
				foreach (GameObject go in scene.GameObjects)
				{
					context.Objects[go.Id] = go;
					foreach (Behaviour component in go.Components)
					{
						context.Objects[component.Id] = component;
					}
				}

				scene.SuspendActivation();
				try
				{
					var pending = new List<(Behaviour component, JsonElement fields, string location)>();
					var copied = new HashSet<ulong>();
					JsonElement objects;
					if (document.RootElement.TryGetProperty("gameObjects", out objects))
					{
						foreach (JsonElement entry in objects.EnumerateArray())
						{
							GameObject go = CreateObject(entry, scene, context, pending, true, copied, parent);
							if (go.transform.parent == parent)
							{
								roots.Add(go);
							}
						}
					}

					foreach ((Behaviour component, JsonElement fields, string location) item in pending)
					{
						context.Location = item.location;
						ValueSerializer.ReadFields(item.fields, item.component, context);
					}

					foreach ((Behaviour component, JsonElement fields, string location) item in pending)
					{
						if (item.component is ISerializationCallbackReceiver receiver)
						{
							try
							{
								receiver.OnAfterDeserialize();
							}
							catch (Exception e)
							{
								Debug.LogException(e, item.component);
							}
						}
					}
				}
				finally
				{
					scene.ResumeActivation();
				}
			}

			return roots;
		}

		/// <summary>Loads a scene file. The returned scene is not active and its lifecycle has not started.</summary>
		public static Scene Load(string absolutePath)
		{
			Scene scene = LoadFromString(File.ReadAllText(absolutePath), Path.GetFileNameWithoutExtension(absolutePath));
			scene.Path = AssetDatabase.ToAssetPath(absolutePath) ?? absolutePath;
			return scene;
		}

		public static Scene LoadFromString(string json, string fallbackName = null)
		{
			using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
			{
				JsonElement root = document.RootElement;
				JsonElement value;
				string name = root.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : fallbackName ?? "Untitled";
				var scene = new Scene(name);
				var context = new SerializationContext { Scene = scene };

				if (root.TryGetProperty("settings", out value))
				{
					context.Location = "settings";
					ValueSerializer.ReadFields(value, scene.Settings, context);
				}

				// Pass 1: objects and components with their ids, hierarchy and enabled flags.
				var pending = new List<(Behaviour component, JsonElement fields, string location)>();
				if (root.TryGetProperty("gameObjects", out value))
				{
					foreach (JsonElement entry in value.EnumerateArray())
					{
						CreateObject(entry, scene, context, pending, false, null, null);
					}
				}

				// Pass 2: field values (references to any object of the file resolve now).
				foreach ((Behaviour component, JsonElement fields, string location) item in pending)
				{
					context.Location = item.location;
					ValueSerializer.ReadFields(item.fields, item.component, context);
				}

				// Pass 3: derived state.
				foreach ((Behaviour component, JsonElement fields, string location) item in pending)
				{
					if (item.component is ISerializationCallbackReceiver receiver)
					{
						try
						{
							receiver.OnAfterDeserialize();
						}
						catch (Exception e)
						{
							Debug.LogException(e, item.component);
						}
					}
				}

				return scene;
			}
		}

		#endregion

		#region PrivateMethods

		private static void WriteObjectTree(Utf8JsonWriter writer, GameObject go, SerializationContext context)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.destroyed)
			{
				return;
			}

			writer.WriteStartObject();
			writer.WriteNumber("id", go.Id);
			writer.WriteString("name", go.Name);
			writer.WriteBoolean("active", go.activeSelf);
			Transform parent = go.transform.parent;
			if (parent != null)
			{
				writer.WriteNumber("parent", parent.gameObject.Id);
			}
			else
			{
				writer.WriteNull("parent");
			}

			if (go.hideFlags != HideFlags.None)
			{
				writer.WriteNumber("hideFlags", (int)go.hideFlags);
			}

			writer.WritePropertyName("components");
			writer.WriteStartArray();
			foreach (Behaviour component in go.Components)
			{
				if (component.destroyed)
				{
					continue;
				}

				if (component is MissingComponent missing && missing.RawJson != null)
				{
					writer.WriteRawValue(missing.RawJson);
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

				context.Location = go.Name + "/" + component.GetType().Name;
				writer.WriteStartObject();
				writer.WriteString("type", TypeRegistry.GetName(component.GetType()));
				writer.WriteNumber("id", component.Id);
				writer.WriteBoolean("enabled", component.enabled);
				writer.WritePropertyName("fields");
				writer.WriteStartObject();
				ValueSerializer.WriteFields(writer, component, context);
				writer.WriteEndObject();
				writer.WriteEndObject();
			}

			writer.WriteEndArray();
			writer.WriteEndObject();

			foreach (Transform child in go.transform.Children)
			{
				WriteObjectTree(writer, child.gameObject, context);
			}
		}

		/// <param name="remap">Instantiate copies: new ids; file ids map to the copies in <paramref name="context" />.</param>
		/// <param name="copied">With remap: file ids of the objects copied so far (parents inside the copy).</param>
		/// <param name="rootParent">With remap: parent of copied objects whose parent is not part of the copy.</param>
		private static GameObject CreateObject(JsonElement entry, Scene scene, SerializationContext context, List<(Behaviour, JsonElement, string)> pending, bool remap, HashSet<ulong> copied, Transform rootParent)
		{
			JsonElement value;
			ulong id = entry.TryGetProperty("id", out value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : 0;
			string name = entry.TryGetProperty("name", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "GameObject";
			var go = new GameObject(name, scene, remap ? 0 : id);
			context.Objects[id != 0 ? id : go.Id] = go;
			if (remap && id != 0)
			{
				copied.Add(id);
			}

			if (entry.TryGetProperty("hideFlags", out value) && value.ValueKind == JsonValueKind.Number)
			{
				go.hideFlags = (HideFlags)value.GetInt32();
			}

			ulong parentId = entry.TryGetProperty("parent", out value) && value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : 0;
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

			if (entry.TryGetProperty("active", out value) && value.ValueKind == JsonValueKind.False)
			{
				go.SetActive(false);
			}

			if (!entry.TryGetProperty("components", out value))
			{
				return go;
			}

			foreach (JsonElement componentEntry in value.EnumerateArray())
			{
				JsonElement property;
				string typeName = componentEntry.TryGetProperty("type", out property) ? property.GetString() : null;
				ulong fileComponentId = componentEntry.TryGetProperty("id", out property) && property.ValueKind == JsonValueKind.Number ? property.GetUInt64() : 0;
				ulong componentId = remap ? 0 : fileComponentId;
				bool enabled = !componentEntry.TryGetProperty("enabled", out property) || property.ValueKind != JsonValueKind.False;
				JsonElement fields = componentEntry.TryGetProperty("fields", out property) ? property : default;

				Type type = TypeRegistry.Resolve(typeName);
				Behaviour component;
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
					try
					{
						component = (Behaviour)Activator.CreateInstance(type);
					}
					catch (Exception e)
					{
						Debug.LogException(e);
						component = null;
					}

					if (component == null)
					{
						component = Missing(typeName, componentEntry);
					}

					go.LinkComponent(component, componentId);
				}
				else
				{
					Debug.LogWarning($"[Scene] '{name}': component type '{typeName}' not found; kept as a missing component");
					component = Missing(typeName, componentEntry);
					go.LinkComponent(component, componentId);
				}

				context.Objects[fileComponentId != 0 ? fileComponentId : component.Id] = component;
				if (!enabled)
				{
					component.enabled = false;
				}

				if (!(component is MissingComponent) && fields.ValueKind == JsonValueKind.Object)
				{
					// Fields are read in pass 2; the element stays valid while the document is alive.
					pending.Add((component, fields, name + "/" + type?.Name));
				}
			}

			return go;
		}

		private static MissingComponent Missing(string typeName, JsonElement entry)
		{
			return new MissingComponent { TypeName = typeName, RawJson = entry.GetRawText() };
		}

		#endregion
	}
}
