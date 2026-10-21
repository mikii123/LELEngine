using System;
using System.Collections.Generic;

namespace LELEngine.Serialization
{
	/// <summary>
	///     How a live prefab instance differs from the prefab data it was built from: the record a document stores
	///     (<see cref="PrefabInstanceRecord" />). Values are compared as generic data, so the granularity follows the
	///     data: a field of a nested object (or of a polymorphic object whose class did not change) is its own override,
	///     an object of another class (or null) replaces the whole value; list elements and the list's length are
	///     separate overrides (a list changed nearly everywhere becomes one), dictionary entries by key, with removed
	///     keys. A value equal to the prefab's is no override, whatever was edited before.
	/// </summary>
	internal static class PrefabOverrides
	{
		#region PublicFields

		/// <summary>Lists with more element overrides than this (and more than half their length) are written whole.</summary>
		public const int MaxElementOverrides = 32;

		private const string LocalPosition = "m_LocalPosition";
		private const string LocalRotation = "m_LocalRotation";

		#endregion

		#region PublicMethods

		/// <summary>
		///     The record of a live instance. <paramref name="context" /> maps the live references to written ids,
		///     <paramref name="mapId" /> the ids of the instance itself (both identity in scenes; a prefab saved from scene
		///     objects maps into its own space). The record's parent is left for the caller.
		/// </summary>
		public static PrefabInstanceRecord Compute(PrefabInstance instance, SerializationContext context, Func<ulong, ulong> mapId, bool includeUnused = true)
		{
			mapId ??= id => id;
			var record = new PrefabInstanceRecord { Id = mapId(instance.Id), Prefab = ReferenceOf(instance) };
			if (instance.Source == null)
			{
				CopyMissing(instance, record, mapId);
				return record;
			}

			ResolvedPrefab source = instance.Source;
			var objects = new Dictionary<ulong, GameObject>();
			var components = new Dictionary<ulong, Behaviour>();
			Collect(instance, objects, components);
			Func<ulong, ulong> mapSource = id => mapId(instance.Id ^ id);
			var removed = new HashSet<ulong>();
			foreach (PrefabObject obj in source.Objects)
			{
				if (!objects.TryGetValue(obj.Id, out GameObject go))
				{
					if (obj.Parent == 0 || !removed.Contains(obj.Parent))
					{
						record.RemovedObjects.Add(obj.Id);
					}

					removed.Add(obj.Id);
					continue;
				}

				bool isRoot = obj == source.Root;
				if (isRoot || go.Name != obj.Name)
				{
					record.Modifications.Add(Value(obj.Id, PropertyModification.NameProperty, ValueTag.String, go.Name));
				}

				if (go.activeSelf != obj.Active)
				{
					record.Modifications.Add(Value(obj.Id, PropertyModification.ActiveProperty, ValueTag.Bool, go.activeSelf));
				}

				if (go.layer != obj.Layer)
				{
					record.Modifications.Add(Value(obj.Id, PropertyModification.LayerProperty, ValueTag.Int32, go.layer));
				}

				foreach (PrefabComponent component in obj.Components)
				{
					if (!components.TryGetValue(component.Id, out Behaviour live))
					{
						record.RemovedComponents.Add(component.Id);
						continue;
					}

					if (live.enabled != component.Enabled)
					{
						record.Modifications.Add(Value(component.Id, PropertyModification.EnabledProperty, ValueTag.Bool, live.enabled));
					}

					// Where the instance is placed is always its own (the prefab's root position does not move instances).
					HashSet<string> always = isRoot && live is Transform ? new HashSet<string> { LocalPosition, LocalRotation } : null;
					DiffNode(component.Data, NodeOf(live, context), "", component.Id, record.Modifications, mapSource, always);
				}
			}

			foreach (PrefabObject obj in source.Objects)
			{
				if (!objects.TryGetValue(obj.Id, out GameObject go))
				{
					continue;
				}

				foreach (Behaviour component in go.Components)
				{
					if (!component.dead && component.prefabInstance != instance)
					{
						record.AddedComponents.Add(new DocumentComponent { GameObject = obj.Id, Id = mapId(component.Id), Enabled = component.enabled, Live = component });
					}
				}
			}

			foreach (PropertyModification unused in includeUnused ? instance.UnusedModifications : new List<PropertyModification>())
			{
				if (!record.Modifications.Exists(m => m.SameProperty(unused)))
				{
					record.Modifications.Add(CloneModification(unused, mapId));
				}
			}

			return record;
		}

		/// <summary>
		///     The instance properties every instance stores whatever their value (where it is and its name): not shown
		///     as overrides, not applied to the prefab by Apply All.
		/// </summary>
		public static bool IsDefaultOverride(ResolvedPrefab source, PropertyModification modification)
		{
			PrefabObject root = source?.Root;
			if (root == null || modification.HasKey)
			{
				return false;
			}

			if (modification.Target == root.Id)
			{
				return modification.Path == PropertyModification.NameProperty;
			}

			foreach (PrefabComponent component in root.Components)
			{
				if (component.Id == modification.Target)
				{
					return IsTransform(component) && (modification.Path == LocalPosition || modification.Path == LocalRotation);
				}
			}

			return false;
		}

		/// <summary>Generic data of a live component as a document would store it (references mapped by <paramref name="context" />).</summary>
		public static SerializedNode NodeOf(Behaviour component, SerializationContext context)
		{
			if (component is MissingComponent missing)
			{
				return missing.Data ?? new SerializedNode { TypeName = missing.TypeName };
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

			return DocumentValues.ToNode(component, context);
		}

		/// <summary>The live objects and components of an instance by their ids in the prefab.</summary>
		public static void Collect(PrefabInstance instance, Dictionary<ulong, GameObject> objects, Dictionary<ulong, Behaviour> components)
		{
			if (instance.Root == null || instance.Root.dead)
			{
				return;
			}

			var stack = new Stack<GameObject>();
			stack.Push(instance.Root);
			while (stack.Count > 0)
			{
				GameObject go = stack.Pop();
				if (go.dead || go.prefabInstance != instance)
				{
					continue;
				}

				objects[go.prefabSourceId] = go;
				foreach (Behaviour component in go.Components)
				{
					if (!component.dead && component.prefabInstance == instance)
					{
						components[component.prefabSourceId] = component;
					}
				}

				foreach (Transform child in go.transform.Children)
				{
					stack.Push(child.gameObject);
				}
			}
		}

		/// <summary>The reference a document stores for an instance's prefab.</summary>
		public static AssetReference ReferenceOf(PrefabInstance instance)
		{
			if (instance.MissingRecord != null && instance.Prefab == null)
			{
				return instance.MissingRecord.Prefab;
			}

			string path = instance.Prefab?.AssetPath ?? instance.PrefabPath;
			return new AssetReference { Guid = AssetDatabase.GetGuid(path), Path = path };
		}

		public static bool IsTransform(PrefabComponent component)
		{
			string name = component.TypeName;
			return name != null && TypeRegistry.Resolve(name) == typeof(Transform);
		}

		// ---------------------------------------------------------------- comparison

		/// <summary>Adds the overrides of <paramref name="live" /> against <paramref name="source" /> (a node of the same class) under <paramref name="path" />.</summary>
		public static void DiffNode(SerializedNode source, SerializedNode live, string path, ulong target, List<PropertyModification> modifications, Func<ulong, ulong> mapSource, HashSet<string> always = null)
		{
			if (live == null)
			{
				return;
			}

			foreach (SerializedProperty property in live.Properties)
			{
				string childPath = PropertyPath.Field(path, property.Name);
				SerializedProperty stored = source?.Find(property.Name) ?? GenericValues.DefaultProperty(source?.TypeName ?? live.TypeName, property.Name);
				if (always != null && always.Contains(childPath))
				{
					modifications.Add(Value(target, childPath, property.Tag, property.Value, property.ElementTag));
					continue;
				}

				if (stored == null)
				{
					continue;
				}

				DiffValue(stored.Value, stored.Tag, property.Value, property.Tag, property.ElementTag, childPath, target, modifications, mapSource);
			}
		}

		#endregion

		#region PrivateMethods

		private static void DiffValue(object source, ValueTag sourceTag, object live, ValueTag tag, ValueTag elementTag, string path, ulong target, List<PropertyModification> modifications, Func<ulong, ulong> mapSource)
		{
			if (sourceTag != tag)
			{
				modifications.Add(Value(target, path, tag, live, elementTag));
				return;
			}

			switch (tag)
			{
				case ValueTag.Object when source is SerializedNode sourceNode && live is SerializedNode liveNode && sourceNode.TypeName == liveNode.TypeName:
					// The same class (a polymorphic field that kept its class too): field by field.
					DiffNode(sourceNode, liveNode, path, target, modifications, mapSource);
					return;
				case ValueTag.Array when source is SerializedArray sourceArray && live is SerializedArray liveArray && sourceArray.ElementTag == liveArray.ElementTag:
					DiffArray(sourceArray, liveArray, path, target, modifications, mapSource);
					return;
				case ValueTag.Dictionary when source is SerializedDictionary sourceDictionary && live is SerializedDictionary liveDictionary
					&& sourceDictionary.KeyTag == liveDictionary.KeyTag && sourceDictionary.ValueTag == liveDictionary.ValueTag:
					DiffDictionary(sourceDictionary, liveDictionary, path, target, modifications, mapSource);
					return;
			}

			if (!GenericValues.AreEqual(source, live, tag, mapSource))
			{
				modifications.Add(Value(target, path, tag, live, elementTag));
			}
		}

		private static void DiffArray(SerializedArray source, SerializedArray live, string path, ulong target, List<PropertyModification> modifications, Func<ulong, ulong> mapSource)
		{
			if (source.Bytes != null && live.Bytes != null && source.Bytes.AsSpan().SequenceEqual(live.Bytes))
			{
				return;
			}

			List<object> sourceItems = DocumentValues.ExpandItems(source);
			List<object> liveItems = DocumentValues.ExpandItems(live);
			ValueTag element = live.ElementTag;
			var local = new List<PropertyModification>();
			if (sourceItems.Count != liveItems.Count)
			{
				local.Add(Value(target, PropertyPath.SizeOf(path), ValueTag.Int32, liveItems.Count));
			}

			int changedElements = 0;
			for (int i = 0; i < liveItems.Count; i++)
			{
				string elementPath = PropertyPath.Element(path, i);
				object item = liveItems[i];
				int before = local.Count;
				if (i < sourceItems.Count && element == ValueTag.Object && sourceItems[i] is SerializedNode sourceNode && item is SerializedNode liveNode && sourceNode.TypeName == liveNode.TypeName)
				{
					DiffNode(sourceNode, liveNode, elementPath, target, local, mapSource);
				}
				else if (i >= sourceItems.Count || !GenericValues.AreEqual(sourceItems[i], item, element, mapSource))
				{
					local.Add(Value(target, elementPath, element, item, SerializationUtility.ElementTagOf(item)));
				}

				if (local.Count > before)
				{
					changedElements++;
				}
			}

			if (changedElements > MaxElementOverrides && changedElements > liveItems.Count / 2)
			{
				modifications.Add(Value(target, path, ValueTag.Array, live, element));
				return;
			}

			modifications.AddRange(local);
		}

		private static void DiffDictionary(SerializedDictionary source, SerializedDictionary live, string path, ulong target, List<PropertyModification> modifications, Func<ulong, ulong> mapSource)
		{
			var sourceKeys = new List<object>(source.Entries.Count);
			foreach (KeyValuePair<object, object> entry in source.Entries)
			{
				sourceKeys.Add(GenericValues.Clone(entry.Key, source.KeyTag, mapSource));
			}

			var matched = new bool[source.Entries.Count];
			foreach (KeyValuePair<object, object> entry in live.Entries)
			{
				int index = -1;
				for (int i = 0; i < sourceKeys.Count; i++)
				{
					if (!matched[i] && GenericValues.KeyEquals(sourceKeys[i], source.KeyTag, entry.Key, live.KeyTag))
					{
						index = i;
						break;
					}
				}

				if (index >= 0)
				{
					matched[index] = true;
					if (GenericValues.AreEqual(source.Entries[index].Value, entry.Value, live.ValueTag, mapSource))
					{
						continue;
					}
				}

				modifications.Add(new PropertyModification
				{
					Target = target, Path = path, HasKey = true, Key = entry.Key, KeyTag = live.KeyTag,
					Tag = live.ValueTag, ElementTag = SerializationUtility.ElementTagOf(entry.Value), Value = entry.Value
				});
			}

			for (int i = 0; i < sourceKeys.Count; i++)
			{
				if (!matched[i])
				{
					modifications.Add(new PropertyModification { Target = target, Path = path, HasKey = true, Key = sourceKeys[i], KeyTag = source.KeyTag, Removed = true });
				}
			}
		}

		private static PropertyModification Value(ulong target, string path, ValueTag tag, object value, ValueTag elementTag = ValueTag.Null)
		{
			return new PropertyModification { Target = target, Path = path, Tag = tag, ElementTag = elementTag != ValueTag.Null ? elementTag : SerializationUtility.ElementTagOf(value), Value = value };
		}

		private static PropertyModification CloneModification(PropertyModification source, Func<ulong, ulong> mapReference)
		{
			return new PropertyModification
			{
				Target = source.Target, Path = source.Path, HasKey = source.HasKey, Removed = source.Removed,
				Key = GenericValues.Clone(source.Key, source.KeyTag, mapReference), KeyTag = source.KeyTag,
				Tag = source.Tag, ElementTag = source.ElementTag, Value = GenericValues.Clone(source.Value, source.Tag, mapReference)
			};
		}

		/// <summary>A missing prefab's instance: the record as it was read (ids of a moved copy mapped).</summary>
		private static void CopyMissing(PrefabInstance instance, PrefabInstanceRecord record, Func<ulong, ulong> mapId)
		{
			PrefabInstanceRecord stored = instance.MissingRecord;
			if (stored == null)
			{
				return;
			}

			foreach (PropertyModification modification in stored.Modifications)
			{
				record.Modifications.Add(CloneModification(modification, mapId));
			}

			record.RemovedObjects.AddRange(stored.RemovedObjects);
			record.RemovedComponents.AddRange(stored.RemovedComponents);
			foreach (DocumentComponent component in stored.AddedComponents)
			{
				record.AddedComponents.Add(new DocumentComponent { GameObject = component.GameObject, Id = mapId(component.Id), Enabled = component.Enabled, Data = GenericValues.CloneNode(component.Data, mapId) });
			}
		}

		#endregion
	}
}
