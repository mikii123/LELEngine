using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Scene files (.scene): a <see cref="BinaryDocument" /> whose body is
	///     <code>
	///     name:string-id
	///     settings: Object value (SceneSettings)
	///     objectCount:int32, then per GameObject (depth first, a parent before its children, children in sibling order):
	///       id:uint64, name:string-id, active:bool, parent:uint64 (0 = root), hideFlags:int32, componentCount:int32,
	///       per component: typeId:int32, id:uint64, enabled:bool, length:int32, fields
	///     </code>
	///     Loading creates every object and component first, then reads all fields (so references in any direction
	///     resolve), then calls <see cref="ISerializationCallbackReceiver.OnAfterDeserialize" />. The component
	///     lifecycle does not start: the caller decides between play and edit mode. Scenes saved by older versions
	///     as JSON still load (<see cref="LegacySceneJson" />); saving always writes the binary format.
	/// </summary>
	public static class SceneSerializer
	{
		#region PublicFields

		public const string Extension = ".scene";

		#endregion

		#region PublicMethods

		public static void Save(Scene scene, string absolutePath)
		{
			string directory = Path.GetDirectoryName(absolutePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(absolutePath, SaveToBytes(scene));
		}

		/// <summary>The whole scene as a document (files, play mode and undo snapshots).</summary>
		public static byte[] SaveToBytes(Scene scene)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene });
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

		/// <summary>Serializes GameObjects with their children (copy, duplicate, prefab-like snapshots).</summary>
		public static byte[] SerializeObjects(Scene scene, IEnumerable<GameObject> roots)
		{
			var writer = new BinaryDocumentWriter(new SerializationContext { Scene = scene });
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
			SkipObjectValue(reader);

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

		/// <summary>Loads a scene file (binary, or a legacy JSON scene). The returned scene is not active and its lifecycle has not started.</summary>
		public static Scene Load(string absolutePath)
		{
			byte[] data = File.ReadAllBytes(absolutePath);
			string name = Path.GetFileNameWithoutExtension(absolutePath);
			Scene scene = BinaryDocument.IsBinary(data) ? LoadFromBytes(data, name) : LegacySceneJson.Load(System.Text.Encoding.UTF8.GetString(data), name);
			scene.Path = AssetDatabase.ToAssetPath(absolutePath) ?? absolutePath;
			return scene;
		}

		public static Scene LoadFromBytes(byte[] data, string fallbackName = null)
		{
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
			ReadPendingFields(reader, pending);
			return scene;
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

		/// <summary>Writes the object and its descendants; returns how many objects were written.</summary>
		private static int WriteObjectTree(BinaryDocumentWriter writer, GameObject go)
		{
			if ((go.hideFlags & HideFlags.DontSave) != 0 || go.destroyed)
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

			int countPosition = body.ReserveInt32();
			int count = 0;
			foreach (Behaviour component in go.Components)
			{
				if (component.destroyed)
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
			int componentCount = body.ReadInt32();

			var go = new GameObject(name, scene, remap ? 0 : id);
			context.Objects[id != 0 ? id : go.Id] = go;
			if (remap && id != 0)
			{
				copied.Add(id);
			}

			go.hideFlags = hideFlags;
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
						component = new MissingComponent { TypeName = typeName };
						missing = true;
					}

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

		private static void ReadPendingFields(BinaryDocumentReader reader, List<PendingComponent> pending)
		{
			foreach (PendingComponent item in pending)
			{
				reader.Context.Location = item.Location;
				reader.Body.Position = item.Position;
				reader.ReadFieldBlock(item.Component, item.TypeId);
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

		private static void SkipObjectValue(BinaryDocumentReader reader)
		{
			reader.SkipValue(ValueTag.Object, ValueTag.Null);
		}

		#endregion
	}
}
