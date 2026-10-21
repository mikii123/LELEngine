using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LELEngine.Serialization
{
	public static partial class SceneSerializer
	{
		#region PublicMethods

		/// <summary>
		///     A scene or prefab document of either format as generic data (no type is resolved): builds convert it,
		///     prefabs are resolved from it, dependency scans read it.
		/// </summary>
		public static SceneDocument ReadDocument(byte[] data)
		{
			var document = new SceneDocument();
			if (Documents.IsJson(data))
			{
				using (var reader = new JsonDocumentReader(data, null))
				{
					JsonElement root = reader.Root;
					document.Kind = reader.Kind;
					document.Name = GetString(root, "name");
					JsonElement value;
					if (root.TryGetProperty("settings", out value) && value.ValueKind == JsonValueKind.Object)
					{
						document.Settings = reader.ReadGenericObject(value);
					}

					if (root.TryGetProperty("gameObjects", out value) && value.ValueKind == JsonValueKind.Array)
					{
						foreach (JsonElement entry in value.EnumerateArray())
						{
							if (entry.ValueKind != JsonValueKind.Object)
							{
								continue;
							}

							document.Entries.Add(IsInstanceEntry(entry)
								? new DocumentEntry { Instance = ReadInstanceRecord(reader, entry) }
								: new DocumentEntry { Object = ReadDocumentObject(reader, entry) });
						}
					}
				}

				return document;
			}

			var binary = new BinaryDocumentReader(data, null);
			document.Kind = binary.Kind;
			ByteReader body = binary.Body;
			document.Name = binary.String(body.ReadInt32());
			document.Settings = binary.ReadBoxed(ValueTag.Object, ValueTag.Null, null, null, 0) as SerializedNode;
			int count = body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				byte kind = binary.Version >= 3 ? body.ReadByte() : BinaryDocument.EntryObject;
				document.Entries.Add(kind == BinaryDocument.EntryPrefabInstance
					? new DocumentEntry { Instance = ReadInstanceRecord(binary) }
					: new DocumentEntry { Object = ReadDocumentObject(binary) });
			}

			return document;
		}

		/// <summary>Writes a generic scene or prefab document (conversion between the formats, prefab edits without types).</summary>
		public static byte[] WriteDocument(SceneDocument document, DocumentFormat format)
		{
			if (format == DocumentFormat.Json)
			{
				using (var writer = new JsonDocumentWriter(null, document.Kind))
				{
					Utf8JsonWriter json = writer.Json;
					json.WriteString("name", document.Name);
					if (document.Settings != null)
					{
						writer.WriteObjectProperty("settings", document.Settings);
					}

					json.WriteStartArray("gameObjects");
					foreach (DocumentEntry entry in document.Entries)
					{
						if (entry.Instance != null)
						{
							WriteInstanceRecord(writer, entry.Instance);
						}
						else if (entry.Object != null)
						{
							WriteDocumentObject(writer, entry.Object);
						}
					}

					json.WriteEndArray();
					return writer.ToArray();
				}
			}

			var binaryWriter = new BinaryDocumentWriter(null) { Kind = document.Kind };
			binaryWriter.Body.WriteInt32(binaryWriter.StringId(document.Name));
			binaryWriter.WriteObjectValue(document.Settings, 0);
			binaryWriter.Body.WriteInt32(document.Entries.Count);
			foreach (DocumentEntry entry in document.Entries)
			{
				if (entry.Instance != null)
				{
					WriteInstanceRecord(binaryWriter, entry.Instance);
				}
				else
				{
					WriteDocumentObject(binaryWriter, entry.Object ?? new DocumentObject());
				}
			}

			return binaryWriter.ToArray();
		}

		/// <summary>True for the kinds this serializer reads and writes (scenes and prefabs).</summary>
		public static bool IsSceneKind(string kind)
		{
			return kind == BinaryDocument.KindScene || kind == BinaryDocument.KindPrefab;
		}

		#endregion

		#region InternalMethods

		// ---------------------------------------------------------------- JSON entries

		internal static bool IsInstanceEntry(JsonElement entry)
		{
			return entry.TryGetProperty(PrefabInstanceKey, out JsonElement id) && id.ValueKind == JsonValueKind.Number;
		}

		private static DocumentObject ReadDocumentObject(JsonDocumentReader reader, JsonElement entry)
		{
			var result = new DocumentObject
			{
				Id = GetUInt64(entry, "id"),
				Name = GetString(entry, "name") ?? "GameObject",
				Active = GetBool(entry, "active", true),
				Parent = GetUInt64(entry, "parent"),
				HideFlags = GetInt32(entry, "hideFlags"),
				Layer = GetInt32(entry, "layer")
			};

			JsonElement components;
			if (entry.TryGetProperty("components", out components) && components.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement component in components.EnumerateArray())
				{
					if (component.ValueKind == JsonValueKind.Object)
					{
						result.Components.Add(ReadDocumentComponent(reader, component));
					}
				}
			}

			return result;
		}

		private static DocumentComponent ReadDocumentComponent(JsonDocumentReader reader, JsonElement component)
		{
			return new DocumentComponent
			{
				Id = GetUInt64(component, "$id"),
				Enabled = GetBool(component, "$enabled", true),
				GameObject = GetUInt64(component, "gameObject"),
				Data = reader.ReadGenericObject(component)
			};
		}

		internal static PrefabInstanceRecord ReadInstanceRecord(JsonDocumentReader reader, JsonElement entry)
		{
			var record = new PrefabInstanceRecord { Id = GetUInt64(entry, PrefabInstanceKey), Parent = GetUInt64(entry, "parent") };
			JsonElement value;
			if (entry.TryGetProperty("prefab", out value) && value.ValueKind == JsonValueKind.Object)
			{
				record.Prefab = JsonDocumentReader.ReadAssetReference(value);
			}

			if (entry.TryGetProperty("modifications", out value) && value.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement item in value.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					var modification = new PropertyModification
					{
						Target = GetUInt64(item, "target"),
						Path = GetString(item, "path"),
						Removed = GetBool(item, "removed", false)
					};

					JsonElement key;
					if (item.TryGetProperty("key", out key))
					{
						modification.HasKey = true;
						modification.Key = reader.ReadGenericValue(key, ParseSpec(GetString(item, "keyTag")), out modification.KeyTag, out _);
					}

					JsonElement data;
					if (!modification.Removed && item.TryGetProperty("value", out data))
					{
						modification.Value = reader.ReadGenericValue(data, ParseSpec(GetString(item, "tag")), out modification.Tag, out modification.ElementTag);
					}

					if (modification.Path != null)
					{
						record.Modifications.Add(modification);
					}
				}
			}

			ReadIds(entry, "removedObjects", record.RemovedObjects);
			ReadIds(entry, "removedComponents", record.RemovedComponents);
			if (entry.TryGetProperty("addedComponents", out value) && value.ValueKind == JsonValueKind.Array)
			{
				foreach (JsonElement component in value.EnumerateArray())
				{
					if (component.ValueKind == JsonValueKind.Object)
					{
						record.AddedComponents.Add(ReadDocumentComponent(reader, component));
					}
				}
			}

			return record;
		}

		private static SchemaField ParseSpec(string spec)
		{
			return spec != null && SerializationUtility.TryParseTagSpec(spec, out SchemaField field) ? field : null;
		}

		private static void ReadIds(JsonElement entry, string name, List<ulong> ids)
		{
			JsonElement value;
			if (!entry.TryGetProperty(name, out value) || value.ValueKind != JsonValueKind.Array)
			{
				return;
			}

			foreach (JsonElement item in value.EnumerateArray())
			{
				if (item.ValueKind == JsonValueKind.Number && item.TryGetUInt64(out ulong id))
				{
					ids.Add(id);
				}
			}
		}

		private static void WriteDocumentObject(JsonDocumentWriter writer, DocumentObject entry)
		{
			Utf8JsonWriter json = writer.Json;
			json.WriteStartObject();
			json.WriteNumber("id", entry.Id);
			json.WriteString("name", entry.Name);
			if (!entry.Active) json.WriteBoolean("active", false);
			if (entry.Parent != 0) json.WriteNumber("parent", entry.Parent);
			if (entry.HideFlags != 0) json.WriteNumber("hideFlags", entry.HideFlags);
			if (entry.Layer != 0) json.WriteNumber("layer", entry.Layer);
			json.WriteStartArray("components");
			foreach (DocumentComponent component in entry.Components)
			{
				WriteDocumentComponent(writer, component, false);
			}

			json.WriteEndArray();
			json.WriteEndObject();
		}

		/// <summary>A component object: "$type", "$id", "$enabled" (and "gameObject" for components added to an instance), then the fields.</summary>
		private static void WriteDocumentComponent(JsonDocumentWriter writer, DocumentComponent component, bool added)
		{
			Utf8JsonWriter json = writer.Json;
			if (component.Live != null)
			{
				WriteComponent(writer, component.Live, component.Id, added ? component.GameObject : (ulong?)null);
				return;
			}

			SerializedNode node = component.Data ?? new SerializedNode { TypeName = "Missing" };
			writer.Register(node);
			json.WriteStartObject();
			if (added) json.WriteNumber("gameObject", component.GameObject);
			json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
			json.WriteNumber("$id", component.Id);
			if (!component.Enabled) json.WriteBoolean("$enabled", false);
			writer.WriteGenericFields(node);
			json.WriteEndObject();
		}

		internal static void WriteInstanceRecord(JsonDocumentWriter writer, PrefabInstanceRecord record)
		{
			Utf8JsonWriter json = writer.Json;
			json.WriteStartObject();
			json.WriteNumber(PrefabInstanceKey, record.Id);
			json.WritePropertyName("prefab");
			writer.WriteValue(record.Prefab, ValueTag.Asset, ValueTag.Null, 0);
			if (record.Parent != 0) json.WriteNumber("parent", record.Parent);
			if (record.Modifications.Count > 0)
			{
				json.WriteStartArray("modifications");
				foreach (PropertyModification modification in record.Modifications)
				{
					json.WriteStartObject();
					json.WriteNumber("target", modification.Target);
					json.WriteString("path", modification.Path);
					if (modification.HasKey)
					{
						json.WritePropertyName("key");
						writer.WriteValue(modification.Key, modification.KeyTag, ValueTag.Null, 0);
						json.WriteString("keyTag", SerializationUtility.TagSpec(JsonDocumentWriter.GenericSpec(null, modification.KeyTag, ValueTag.Null, modification.Key, 0)));
					}

					if (modification.Removed)
					{
						json.WriteBoolean("removed", true);
					}
					else
					{
						json.WriteString("tag", SerializationUtility.TagSpec(JsonDocumentWriter.GenericSpec(null, modification.Tag, modification.ElementTag, modification.Value, 0)));
						json.WritePropertyName("value");
						writer.WriteValue(modification.Value, modification.Tag, modification.ElementTag, 0);
					}

					json.WriteEndObject();
				}

				json.WriteEndArray();
			}

			WriteIds(json, "removedObjects", record.RemovedObjects);
			WriteIds(json, "removedComponents", record.RemovedComponents);
			if (record.AddedComponents.Count > 0)
			{
				json.WriteStartArray("addedComponents");
				foreach (DocumentComponent component in record.AddedComponents)
				{
					WriteDocumentComponent(writer, component, true);
				}

				json.WriteEndArray();
			}

			json.WriteEndObject();
		}

		private static void WriteIds(Utf8JsonWriter json, string name, List<ulong> ids)
		{
			if (ids.Count == 0)
			{
				return;
			}

			json.WriteStartArray(name);
			foreach (ulong id in ids)
			{
				json.WriteNumberValue(id);
			}

			json.WriteEndArray();
		}

		// ---------------------------------------------------------------- binary entries

		private static DocumentObject ReadDocumentObject(BinaryDocumentReader reader)
		{
			ByteReader body = reader.Body;
			var result = new DocumentObject
			{
				Id = body.ReadUInt64(),
				Name = reader.String(body.ReadInt32()),
				Active = body.ReadBool(),
				Parent = body.ReadUInt64(),
				HideFlags = body.ReadInt32(),
				Layer = reader.Version >= 2 ? body.ReadInt32() : 0
			};

			int componentCount = body.ReadInt32();
			for (int c = 0; c < componentCount; c++)
			{
				int typeId = body.ReadInt32();
				ulong id = body.ReadUInt64();
				bool enabled = body.ReadBool();
				result.Components.Add(new DocumentComponent { Id = id, Enabled = enabled, Data = reader.ReadGenericBlock(typeId) });
			}

			return result;
		}

		internal static PrefabInstanceRecord ReadInstanceRecord(BinaryDocumentReader reader)
		{
			ByteReader body = reader.Body;
			var record = new PrefabInstanceRecord { Id = body.ReadUInt64(), Prefab = reader.ReadAssetReference(), Parent = body.ReadUInt64() };
			int count = body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				var modification = new PropertyModification { Target = body.ReadUInt64(), Path = reader.String(body.ReadInt32()) };
				byte flags = body.ReadByte();
				modification.HasKey = (flags & 1) != 0;
				modification.Removed = (flags & 2) != 0;
				if (modification.HasKey)
				{
					modification.KeyTag = (ValueTag)body.ReadByte();
					modification.Key = reader.ReadBoxed(modification.KeyTag, ValueTag.Null, null, null, 0);
				}

				if (!modification.Removed)
				{
					modification.Tag = (ValueTag)body.ReadByte();
					modification.ElementTag = (ValueTag)body.ReadByte();
					modification.Value = reader.ReadBoxed(modification.Tag, modification.ElementTag, null, null, 0);
				}

				record.Modifications.Add(modification);
			}

			ReadIds(body, record.RemovedObjects);
			ReadIds(body, record.RemovedComponents);
			int added = body.ReadInt32();
			for (int i = 0; i < added; i++)
			{
				ulong gameObject = body.ReadUInt64();
				int typeId = body.ReadInt32();
				ulong id = body.ReadUInt64();
				bool enabled = body.ReadBool();
				record.AddedComponents.Add(new DocumentComponent { GameObject = gameObject, Id = id, Enabled = enabled, Data = reader.ReadGenericBlock(typeId) });
			}

			return record;
		}

		private static void ReadIds(ByteReader body, List<ulong> ids)
		{
			int count = body.ReadInt32();
			for (int i = 0; i < count; i++)
			{
				ids.Add(body.ReadUInt64());
			}
		}

		private static void WriteDocumentObject(BinaryDocumentWriter writer, DocumentObject entry)
		{
			ByteWriter body = writer.Body;
			body.WriteByte(BinaryDocument.EntryObject);
			body.WriteUInt64(entry.Id);
			body.WriteInt32(writer.StringId(entry.Name));
			body.WriteBool(entry.Active);
			body.WriteUInt64(entry.Parent);
			body.WriteInt32(entry.HideFlags);
			body.WriteInt32(entry.Layer);
			body.WriteInt32(entry.Components.Count);
			foreach (DocumentComponent component in entry.Components)
			{
				WriteDocumentComponent(writer, component);
			}
		}

		/// <summary>typeId, id, enabled, length, fields (a live component with its type, generic data with its stored layout).</summary>
		private static void WriteDocumentComponent(BinaryDocumentWriter writer, DocumentComponent component)
		{
			if (component.Live != null)
			{
				WriteComponent(writer, component.Live, component.Id);
				return;
			}

			ByteWriter body = writer.Body;
			SerializedNode node = component.Data ?? new SerializedNode { TypeName = "Missing" };
			body.WriteInt32(writer.GenericTypeId(node));
			body.WriteUInt64(component.Id);
			body.WriteBool(component.Enabled);
			int lengthPosition = body.ReserveInt32();
			writer.WriteGenericFields(node);
			body.PatchInt32(lengthPosition, body.Length - lengthPosition - 4);
		}

		internal static void WriteInstanceRecord(BinaryDocumentWriter writer, PrefabInstanceRecord record)
		{
			ByteWriter body = writer.Body;
			body.WriteByte(BinaryDocument.EntryPrefabInstance);
			body.WriteUInt64(record.Id);
			writer.WriteValue(record.Prefab, ValueTag.Asset, ValueTag.Null, 0);
			body.WriteUInt64(record.Parent);
			body.WriteInt32(record.Modifications.Count);
			foreach (PropertyModification modification in record.Modifications)
			{
				body.WriteUInt64(modification.Target);
				body.WriteInt32(writer.StringId(modification.Path));
				body.WriteByte((byte)((modification.HasKey ? 1 : 0) | (modification.Removed ? 2 : 0)));
				if (modification.HasKey)
				{
					body.WriteByte((byte)modification.KeyTag);
					writer.WriteValue(modification.Key, modification.KeyTag, ValueTag.Null, 0);
				}

				if (!modification.Removed)
				{
					body.WriteByte((byte)modification.Tag);
					body.WriteByte((byte)modification.ElementTag);
					writer.WriteValue(modification.Value, modification.Tag, modification.ElementTag, 0);
				}
			}

			WriteIds(body, record.RemovedObjects);
			WriteIds(body, record.RemovedComponents);
			body.WriteInt32(record.AddedComponents.Count);
			foreach (DocumentComponent component in record.AddedComponents)
			{
				body.WriteUInt64(component.GameObject);
				WriteDocumentComponent(writer, component);
			}
		}

		private static void WriteIds(ByteWriter body, List<ulong> ids)
		{
			body.WriteInt32(ids.Count);
			foreach (ulong id in ids)
			{
				body.WriteUInt64(id);
			}
		}

		#endregion
	}
}
