using System;
using System.Collections.Generic;

namespace LELEngine.Serialization
{
	/// <summary>
	///     What a document refers to, found without loading any type: the assets its values reference (scene
	///     components, materials, ScriptableObjects, VFX graphs... wherever an asset value sits, inside nested objects,
	///     interface slots, arrays and dictionaries) and the names of the types it stores (components and nested
	///     objects). Game builds use it to include only the assets their scenes use and to check that the player has
	///     every stored type.
	/// </summary>
	public sealed class DocumentReferences
	{
		#region PublicFields

		/// <summary>Every asset reference, in document order (duplicates included).</summary>
		public readonly List<AssetReference> Assets = new List<AssetReference>();

		/// <summary>Names of the stored types ("Namespace.Type, Assembly").</summary>
		public readonly HashSet<string> TypeNames = new HashSet<string>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		/// <summary>The references of a document of either format (a scene or an object document).</summary>
		public static DocumentReferences Collect(byte[] data)
		{
			var result = new DocumentReferences();
			if (BinaryDocument.IsBinary(data) ? new BinaryDocumentReader(data, null).Kind == BinaryDocument.KindScene : Documents.KindOf(data) == BinaryDocument.KindScene)
			{
				foreach (SerializedNode node in SceneSerializer.ReadGenericNodes(data))
				{
					result.Walk(node, 0);
				}

				return result;
			}

			if (BinaryDocument.IsBinary(data))
			{
				result.Walk(new BinaryDocumentReader(data, null).ReadBoxed(ValueTag.Object, ValueTag.Null, null, null, 0), 0);
				return result;
			}

			using (var reader = new JsonDocumentReader(data, null))
			{
				result.Walk(reader.ReadGenericObject(reader.Root), 0);
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private void Walk(object value, int depth)
		{
			if (depth > 64)
			{
				return;
			}

			switch (value)
			{
				case SerializedNode node:
					if (!string.IsNullOrEmpty(node.TypeName) && node.TypeName != "Object")
					{
						TypeNames.Add(node.TypeName);
					}

					foreach (SerializedProperty property in node.Properties)
					{
						Walk(property.Value, depth + 1);
					}

					break;
				case SerializedArray array:
					foreach (object item in array.Items)
					{
						Walk(item, depth + 1);
					}

					break;
				case SerializedDictionary dictionary:
					foreach (KeyValuePair<object, object> entry in dictionary.Entries)
					{
						Walk(entry.Key, depth + 1);
						Walk(entry.Value, depth + 1);
					}

					break;
				case AssetReference reference:
					if (!reference.IsNull)
					{
						Assets.Add(reference);
					}

					break;
			}
		}

		#endregion
	}
}
