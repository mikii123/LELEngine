using System;
using System.Collections.Generic;

namespace LELEngine.Serialization
{
	public static partial class SceneSerializer
	{
		#region InternalFields

		/// <summary>Mixed into the instance id for the placeholder of an instance whose prefab is missing (no object of the prefab has that id).</summary>
		private const ulong MissingRootSalt = 0x6D697373696E6721UL;

		#endregion

		#region InternalMethods

		/// <summary>
		///     A new instance of a prefab in a scene (scripts' <see cref="Prefab.Instantiate(Transform)" />): built, its
		///     fields read, <paramref name="place" /> run on its root, then its lifecycle starts (in a running scene).
		/// </summary>
		internal static PrefabInstance InstantiatePrefab(Prefab asset, ResolvedPrefab source, Scene scene, Transform parent, Action<GameObject> place)
		{
			var context = new SerializationContext { Scene = scene };
			ulong id = SceneIds.Next();
			scene.SuspendActivation();
			try
			{
				PrefabInstance instance = BuildInstance(asset, source, scene, context, null, id, id, parent, null, null);
				if (instance.Root != null)
				{
					place?.Invoke(instance.Root);
				}

				return instance;
			}
			finally
			{
				scene.ResumeActivation();
			}
		}

		/// <summary>
		///     The objects of a prefab instance in a scene (or in a prefab being edited) from its prefab and the instance's
		///     changes. The objects get the ids <c>id ^ prefab id</c> and are registered in <paramref name="context" /> under
		///     <c>fileId ^ prefab id</c> (the ids the document's references use; equal to the ids unless a copy is made).
		///     Component fields come from the prefab's data, those the instance changes from a patch of their own; with
		///     <paramref name="pending" /> they are read with the rest of the document, else now.
		/// </summary>
		internal static PrefabInstance BuildInstance(Prefab asset, ResolvedPrefab source, Scene scene, SerializationContext context, PrefabInstanceRecord record, ulong id, ulong fileId, Transform parent, List<PendingRead> pending, HashSet<ulong> copied)
		{
			var instance = new PrefabInstance { Id = id, Prefab = asset, PrefabPath = source.AssetPath, Source = source };
			var removedObjects = record != null ? new HashSet<ulong>(record.RemovedObjects) : null;
			var removedComponents = record != null ? new HashSet<ulong>(record.RemovedComponents) : null;
			Dictionary<ulong, List<PropertyModification>> modifications = record != null ? PrefabResolver.GroupByTarget(record.Modifications) : null;
			var used = new HashSet<ulong>();
			var skipped = new HashSet<ulong>();

			byte[] binary = source.GetBinary(out Dictionary<ulong, (int typeId, int offset)> blocks);
			var baseReader = new BinaryDocumentReader(binary, context);
			int baseStart = baseReader.Body.Position;
			BinaryDocumentWriter patch = null;
			var patched = new List<(int index, int typeId, int offset)>();
			List<PendingRead> reads = pending ?? new List<PendingRead>();
			int firstRead = reads.Count;

			foreach (PrefabObject obj in source.Objects)
			{
				if ((removedObjects != null && removedObjects.Contains(obj.Id)) || (obj.Parent != 0 && skipped.Contains(obj.Parent)))
				{
					skipped.Add(obj.Id);
					continue;
				}

				used.Add(obj.Id);
				string name = obj.Name;
				bool active = obj.Active;
				int layer = obj.Layer;
				if (modifications != null && modifications.TryGetValue(obj.Id, out List<PropertyModification> header))
				{
					foreach (PropertyModification modification in header)
					{
						switch (modification.Path)
						{
							case PropertyModification.NameProperty:
								name = modification.Value as string ?? name;
								break;
							case PropertyModification.ActiveProperty:
								active = modification.Value is bool flag && flag;
								break;
							case PropertyModification.LayerProperty:
								layer = modification.Value is int value ? value : layer;
								break;
							default:
								instance.UnusedModifications.Add(modification);
								break;
						}
					}
				}

				var go = new GameObject(name, scene, id ^ obj.Id) { hideFlags = (HideFlags)obj.HideFlags };
				go.layer = layer;
				go.prefabInstance = instance;
				go.prefabSourceId = obj.Id;
				context.Objects[fileId ^ obj.Id] = go;
				copied?.Add(fileId ^ obj.Id);
				if (obj.Parent != 0)
				{
					go.transform.SetParent(((GameObject)context.Objects[fileId ^ obj.Parent]).transform, false);
				}
				else
				{
					instance.Root ??= go;
					if (parent != null)
					{
						go.transform.SetParent(parent, false);
					}
				}

				if (!active)
				{
					go.SetActive(false);
				}

				foreach (PrefabComponent component in obj.Components)
				{
					if (removedComponents != null && removedComponents.Contains(component.Id))
					{
						continue;
					}

					used.Add(component.Id);
					Behaviour live = CreateComponentOf(go, TypeRegistry.Resolve(component.TypeName), component.TypeName, id ^ component.Id, out bool missing);
					live.prefabInstance = instance;
					live.prefabSourceId = component.Id;
					context.Objects[fileId ^ component.Id] = live;

					bool enabled = component.Enabled;
					List<PropertyModification> fieldModifications = null;
					if (modifications != null && modifications.TryGetValue(component.Id, out List<PropertyModification> componentModifications))
					{
						foreach (PropertyModification modification in componentModifications)
						{
							if (modification.Path == PropertyModification.EnabledProperty && !modification.HasKey)
							{
								enabled = modification.Value is bool flag && flag;
							}
							else
							{
								(fieldModifications ??= new List<PropertyModification>()).Add(modification);
							}
						}
					}

					if (!enabled)
					{
						live.enabled = false;
					}

					string location = name + "/" + (missing ? component.TypeName : live.GetType().Name);
					if (missing)
					{
						// Generic data in the scene's ids (the prefab's references mapped like the instance's objects).
						SerializedNode data = GenericValues.CloneNode(component.Data, prefabId => id ^ prefabId) ?? new SerializedNode { TypeName = component.TypeName };
						ApplyModifications(data, fieldModifications, instance);
						((MissingComponent)live).Data = data;
						continue;
					}

					if (fieldModifications != null)
					{
						SerializedNode data = GenericValues.CloneNode(component.Data) ?? new SerializedNode { TypeName = component.TypeName };
						ApplyModifications(data, fieldModifications, instance);
						patch ??= new BinaryDocumentWriter(null) { Kind = BinaryDocument.KindObject };
						patched.Add((reads.Count, patch.GenericTypeId(data), WriteBlock(patch, data)));
						reads.Add(new PendingRead { Component = live, Location = location, InstanceId = fileId });
					}
					else if (blocks.TryGetValue(component.Id, out (int typeId, int offset) block))
					{
						reads.Add(new PendingRead { Component = live, Reader = baseReader, TypeId = block.typeId, Position = baseStart + block.offset, Location = location, InstanceId = fileId });
					}
				}
			}

			if (record != null)
			{
				foreach (DocumentComponent added in record.AddedComponents)
				{
					GameObject host = context.Objects.TryGetValue(fileId ^ added.GameObject, out object found) ? found as GameObject : null;
					if (host == null || host.prefabInstance != instance)
					{
						Debug.LogWarning($"[Prefab] An instance of {source.AssetPath}: a component added to it ({added.Data?.TypeName}) has lost its GameObject in the prefab; left out");
						continue;
					}

					string typeName = added.Data?.TypeName;
					Behaviour live = CreateComponentOf(host, TypeRegistry.Resolve(typeName), typeName, copied != null ? 0 : added.Id, out bool missing);
					context.Objects[added.Id != 0 ? added.Id : live.Id] = live;
					if (!added.Enabled)
					{
						live.enabled = false;
					}

					if (missing)
					{
						((MissingComponent)live).Data = added.Data;
						continue;
					}

					patch ??= new BinaryDocumentWriter(null) { Kind = BinaryDocument.KindObject };
					SerializedNode data = added.Data ?? new SerializedNode { TypeName = typeName };
					patched.Add((reads.Count, patch.GenericTypeId(data), WriteBlock(patch, data)));
					reads.Add(new PendingRead { Component = live, Location = host.Name + "/" + live.GetType().Name, InstanceId = fileId });
				}

				foreach (PropertyModification modification in record.Modifications)
				{
					if (!used.Contains(modification.Target))
					{
						instance.UnusedModifications.Add(modification);
					}
				}
			}

			if (patch != null)
			{
				var patchReader = new BinaryDocumentReader(patch.ToArray(), context);
				int patchStart = patchReader.Body.Position;
				foreach ((int index, int typeId, int offset) in patched)
				{
					PendingRead read = reads[index];
					read.Reader = patchReader;
					read.TypeId = typeId;
					read.Position = patchStart + offset;
					reads[index] = read;
				}
			}

			if (pending == null)
			{
				List<PendingRead> own = firstRead == 0 ? reads : reads.GetRange(firstRead, reads.Count - firstRead);
				ReadPendingFields(own, null);
			}

			return instance;
		}

		#endregion

		#region PrivateMethods

		/// <summary>A prefab instance entry of a document being loaded: built from its prefab, or a placeholder keeping its data when the prefab is missing.</summary>
		private static GameObject LoadInstance(PrefabInstanceRecord record, Scene scene, SerializationContext context, List<PendingRead> pending, bool remap, HashSet<ulong> copied, Transform rootParent)
		{
			Transform parent = null;
			if (record.Parent == 0 || (remap && !copied.Contains(record.Parent)))
			{
				parent = rootParent;
			}
			else if (context.Objects.TryGetValue(record.Parent, out object found) && found is GameObject parentObject)
			{
				parent = parentObject.transform;
			}
			else
			{
				Debug.LogWarning($"[Scene] The instance of '{record.Prefab.Path}': parent {record.Parent} not found; kept at the root");
			}

			string path = PrefabCache.PathOf(record.Prefab);
			Prefab asset = path != null ? Prefab.Load(path) : null;
			ResolvedPrefab source = asset?.Resolved;
			ulong id = remap ? SceneIds.Next() : record.Id;
			if (source?.Root == null)
			{
				return CreateMissingInstance(record, scene, context, parent, id, path, source?.Error);
			}

			return BuildInstance(asset, source, scene, context, record, id, record.Id, parent, pending, remap ? copied : null).Root;
		}

		/// <summary>
		///     The placeholder of an instance whose prefab is missing or broken: an empty object named after it holding the
		///     instance's data, written back unchanged.
		/// </summary>
		private static GameObject CreateMissingInstance(PrefabInstanceRecord record, Scene scene, SerializationContext context, Transform parent, ulong id, string path, string error)
		{
			string name = System.IO.Path.GetFileNameWithoutExtension(record.Prefab.Path ?? path ?? "Prefab");
			Debug.LogWarning($"[Scene] Missing prefab '{record.Prefab.Path ?? record.Prefab.Guid}'{(error != null ? " (" + error + ")" : "")}: its instance is kept as a placeholder");
			var instance = new PrefabInstance { Id = id, PrefabPath = path ?? record.Prefab.Path, MissingRecord = record };
			var go = new GameObject("Missing Prefab (" + name + ")", scene, id ^ MissingRootSalt) { prefabInstance = instance };
			context.Objects[record.Id ^ MissingRootSalt] = go;
			instance.Root = go;
			if (parent != null)
			{
				go.transform.SetParent(parent, false);
			}

			return go;
		}

		private static void ApplyModifications(SerializedNode data, List<PropertyModification> modifications, PrefabInstance instance)
		{
			if (modifications == null)
			{
				return;
			}

			foreach (PropertyModification modification in modifications)
			{
				if (!GenericValues.Apply(data, modification))
				{
					instance.UnusedModifications.Add(modification);
				}
			}
		}

		/// <summary>Writes a component's generic fields as a block ("length, fields"); returns its offset in the body.</summary>
		private static int WriteBlock(BinaryDocumentWriter writer, SerializedNode data)
		{
			int offset = writer.Body.Length;
			int lengthPosition = writer.Body.ReserveInt32();
			writer.WriteGenericFields(data);
			writer.Body.PatchInt32(lengthPosition, writer.Body.Length - lengthPosition - 4);
			return offset;
		}

		#endregion
	}
}
