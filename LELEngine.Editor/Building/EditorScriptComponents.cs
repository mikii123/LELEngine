using System;
using System.Collections.Generic;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     Components of the editor scripts (classes in Editor folders, built into Game.Editor.dll) exist in the editor only:
	///     scenes and prefabs may hold them (tools such as a generator with its settings), a player has no such class. Game
	///     builds leave them out of every scene and prefab they write, and what only they reference is not shipped for them.
	///     Works on documents as generic data: no type is resolved.
	/// </summary>
	internal static class EditorScriptComponents
	{
		#region PublicMethods

		/// <summary>A stored type name ("Namespace.Type, Assembly") of the editor scripts' assembly.</summary>
		public static bool IsEditorType(string typeName)
		{
			if (string.IsNullOrEmpty(typeName))
			{
				return false;
			}

			int comma = typeName.IndexOf(',');
			if (comma < 0)
			{
				return false;
			}

			string rest = typeName.Substring(comma + 1);
			int next = rest.IndexOf(',');
			string assembly = (next >= 0 ? rest.Substring(0, next) : rest).Trim();
			return string.Equals(assembly, ProjectGenerator.EditorAssemblyName, StringComparison.Ordinal);
		}

		/// <summary>
		///     Removes the editor scripts' components from a scene or prefab document: those on its objects and those added to
		///     its prefab instances, with the instances' records about them. Returns how many went.
		/// </summary>
		public static int Strip(SceneDocument document)
		{
			var removed = new HashSet<ulong>();
			foreach (DocumentEntry entry in document.Entries)
			{
				if (entry.Object != null)
				{
					Remove(entry.Object.Components, removed);
				}

				if (entry.Instance != null)
				{
					Remove(entry.Instance.AddedComponents, removed);
				}
			}

			return removed.Count;
		}

		/// <summary>
		///     A scene or prefab document's bytes without the editor scripts' components, in the document's own format; the same
		///     bytes when it holds none or is another kind of document. <paramref name="removed" />: how many went.
		/// </summary>
		public static byte[] Strip(byte[] data, out int removed)
		{
			removed = 0;
			if (!Documents.IsDocument(data) || !SceneSerializer.IsSceneKind(Documents.KindOf(data)))
			{
				return data;
			}

			SceneDocument document = SceneSerializer.ReadDocument(data);
			removed = Strip(document);
			return removed == 0 ? data : SceneSerializer.WriteDocument(document, Documents.FormatOf(data));
		}

		#endregion

		#region PrivateMethods

		private static void Remove(List<DocumentComponent> components, HashSet<ulong> removed)
		{
			for (int i = components.Count - 1; i >= 0; i--)
			{
				if (IsEditorType(components[i].Data?.TypeName))
				{
					removed.Add(components[i].Id);
					components.RemoveAt(i);
				}
			}
		}

		#endregion
	}
}
