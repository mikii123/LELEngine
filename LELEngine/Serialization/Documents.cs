using System;
using System.IO;

namespace LELEngine.Serialization
{
	/// <summary>The two encodings of a document: the editor's readable project files and the player's fast binary.</summary>
	public enum DocumentFormat
	{
		Json,
		Binary
	}

	/// <summary>
	///     Documents are the engine's serialized files: scenes (<see cref="SceneSerializer" />), and single-object
	///     assets such as materials, meta files and physics materials (<see cref="WriteObject" /> /
	///     <see cref="ReadObject" />). The editor writes them as JSON (<see cref="FileFormat" />); a game build
	///     converts every document to binary (<see cref="Convert" />), which only moves data: no type has to be
	///     loaded, so the build can convert scenes of a game whose scripts it never ran. Readers accept both forms
	///     by looking at the bytes.
	/// </summary>
	public static class Documents
	{
		#region PublicFields

		/// <summary>Format written by Save methods (scenes, materials, meta files); builds convert to binary themselves.</summary>
		public static DocumentFormat FileFormat = DocumentFormat.Json;

		/// <summary>Extensions of the files that are documents (converted to binary in builds).</summary>
		public static readonly string[] Extensions = { ".scene", ".material", ".meta", ".physicsMaterial2D", ".asset", ".vfx" };

		#endregion

		#region PublicMethods

		public static bool IsBinary(byte[] data)
		{
			return BinaryDocument.IsBinary(data);
		}

		public static bool IsJson(byte[] data)
		{
			return JsonDocumentFormat.IsJson(data);
		}

		/// <summary>True when the bytes are a document in either format.</summary>
		public static bool IsDocument(byte[] data)
		{
			return IsBinary(data) || IsJson(data);
		}

		public static DocumentFormat FormatOf(byte[] data)
		{
			if (IsBinary(data))
			{
				return DocumentFormat.Binary;
			}

			if (IsJson(data))
			{
				return DocumentFormat.Json;
			}

			throw new InvalidOperationException("Not a LELEngine document (neither binary nor JSON)");
		}

		public static bool IsDocumentFile(string path)
		{
			string extension = Path.GetExtension(path);
			foreach (string known in Extensions)
			{
				if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The kind of a document ("Scene" or "Object") without reading its body.</summary>
		public static string KindOf(byte[] data)
		{
			if (IsBinary(data))
			{
				return new BinaryDocumentReader(data, null).Kind;
			}

			using (var reader = new JsonDocumentReader(data, null))
			{
				return reader.Kind;
			}
		}

		/// <summary>A document holding one root object (its fields with their runtime type).</summary>
		public static byte[] WriteObject(object root, DocumentFormat format, SerializationContext context = null)
		{
			if (root == null)
			{
				throw new ArgumentNullException(nameof(root));
			}

			context = context ?? new SerializationContext { Location = root.GetType().Name };
			if (format == DocumentFormat.Binary)
			{
				var writer = new BinaryDocumentWriter(context) { Kind = BinaryDocument.KindObject };
				writer.WriteObjectValue(root, 0);
				return writer.ToArray();
			}

			using (var writer = new JsonDocumentWriter(context, BinaryDocument.KindObject))
			{
				// The root object's fields sit at the top level, next to the header keys.
				writer.Json.WriteString(JsonDocumentFormat.TypeKey, TypeRegistry.GetName(root.GetType()));
				if (root is ISerializationCallbackReceiver receiver)
				{
					receiver.OnBeforeSerialize();
				}

				writer.WriteFields(root);
				return writer.ToArray();
			}
		}

		public static byte[] WriteObject(object root)
		{
			return WriteObject(root, FileFormat);
		}

		/// <summary>
		///     Reads a root object document of either format into <paramref name="existing" /> (when its type matches the
		///     stored one) or a new instance assignable to <paramref name="type" />.
		/// </summary>
		public static object ReadObject(byte[] data, Type type, object existing = null, SerializationContext context = null)
		{
			context = context ?? new SerializationContext { Location = type.Name };
			if (IsBinary(data))
			{
				var reader = new BinaryDocumentReader(data, context);
				return reader.ReadObjectValue(type, existing, 0);
			}

			using (var reader = new JsonDocumentReader(data, context))
			{
				if (JsonDocumentReader.TypeNameOf(reader.Root) == null && existing != null)
				{
					// A plain object without "$type" (hand-written file): fill the given instance.
					reader.ReadFields(existing, reader.Root);
					if (existing is ISerializationCallbackReceiver receiver)
					{
						receiver.OnAfterDeserialize();
					}

					return existing;
				}

				return reader.ReadObjectValue(type, existing, reader.Root, 0);
			}
		}

		public static T ReadObject<T>(byte[] data, T existing = default)
			where T : class
		{
			return ReadObject(data, typeof(T), existing) as T;
		}

		public static T ReadObjectFile<T>(string path, T existing = default)
			where T : class
		{
			return ReadObject(File.ReadAllBytes(path), typeof(T), existing) as T;
		}

		public static void WriteObjectFile(string path, object root, DocumentFormat? format = null)
		{
			string directory = Path.GetDirectoryName(path);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(path, WriteObject(root, format ?? FileFormat));
		}

		/// <summary>
		///     Re-encodes a document (returns the input when it already has the target format). Pure data transform:
		///     types are never resolved, so it runs anywhere (build machines, before scripts are loaded).
		/// </summary>
		public static byte[] Convert(byte[] data, DocumentFormat target)
		{
			if (FormatOf(data) == target)
			{
				return data;
			}

			if (target == DocumentFormat.Binary)
			{
				using (var reader = new JsonDocumentReader(data, null))
				{
					var writer = new BinaryDocumentWriter(null) { Kind = reader.Kind };
					if (reader.Kind == BinaryDocument.KindScene)
					{
						SceneSerializer.ConvertToBinary(reader, writer);
					}
					else
					{
						writer.WriteObjectValue(reader.ReadGenericObject(reader.Root), 0);
					}

					return writer.ToArray();
				}
			}

			var binary = new BinaryDocumentReader(data, null);
			using (var writer = new JsonDocumentWriter(null, binary.Kind))
			{
				if (binary.Kind == BinaryDocument.KindScene)
				{
					SceneSerializer.ConvertToJson(binary, writer);
				}
				else
				{
					var node = binary.ReadBoxed(ValueTag.Object, ValueTag.Null, null, null, 0) as SerializedNode;
					if (node != null)
					{
						writer.Register(node);
						writer.Json.WriteString(JsonDocumentFormat.TypeKey, node.TypeName);
						writer.WriteGenericFields(node);
					}
				}

				return writer.ToArray();
			}
		}

		/// <summary>Converts a document file in place or into another file (game builds: Assets JSON -> Data binary).</summary>
		public static void ConvertFile(string source, string destination, DocumentFormat target)
		{
			byte[] data = File.ReadAllBytes(source);
			byte[] converted = IsDocument(data) ? Convert(data, target) : data;
			string directory = Path.GetDirectoryName(destination);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}

			File.WriteAllBytes(destination, converted);
		}

		#endregion
	}
}
