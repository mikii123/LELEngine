using System.Collections.Generic;

namespace LELEngine.Serialization
{
	/// <summary>
	///     A scene or prefab document read without resolving any type (<see cref="SceneSerializer.ReadDocument" />):
	///     its objects and prefab instances in file order (a parent before its children), components as generic
	///     data. Builds convert documents through it, prefabs are resolved from it (<see cref="PrefabResolver" />).
	/// </summary>
	public sealed class SceneDocument
	{
		#region PublicFields

		/// <summary><see cref="BinaryDocument.KindScene" /> or <see cref="BinaryDocument.KindPrefab" />.</summary>
		public string Kind = BinaryDocument.KindScene;

		public string Name;

		/// <summary>The scene's settings (null in prefab documents).</summary>
		public SerializedNode Settings;

		public readonly List<DocumentEntry> Entries = new List<DocumentEntry>();

		#endregion
	}

	/// <summary>One entry of a scene or prefab document: a GameObject or a prefab instance.</summary>
	public sealed class DocumentEntry
	{
		public DocumentObject Object;
		public PrefabInstanceRecord Instance;
	}

	/// <summary>A GameObject of a document with its components (generic data).</summary>
	public sealed class DocumentObject
	{
		public ulong Id;
		public string Name;
		public bool Active = true;
		public ulong Parent;
		public int HideFlags;
		public int Layer;
		public readonly List<DocumentComponent> Components = new List<DocumentComponent>();
	}

	/// <summary>A component of a document: its id, enabled flag and fields (generic data named after the component's type).</summary>
	public sealed class DocumentComponent
	{
		public ulong Id;
		public bool Enabled = true;
		public SerializedNode Data;

		/// <summary>Components added to a prefab instance: the GameObject they are on (an id of the prefab's space).</summary>
		public ulong GameObject;

		/// <summary>Writing a live scene: the component itself (written with its type instead of <see cref="Data" />).</summary>
		internal Behaviour Live;
	}

	/// <summary>
	///     A prefab instance as a document stores it: the prefab, the instance id (its objects' ids are
	///     <c>Id ^ id in the prefab</c>, <see cref="PrefabIds" />), the parent of its root, and how the instance differs
	///     from the prefab: modified values, objects and components removed, components added. Objects added under the
	///     instance's objects are ordinary entries of the document (their parent is an object of the instance).
	///     <code>
	///     { "prefabInstance": 7771, "prefab": { "guid": "...", "path": "Prefabs/Enemy.prefab" }, "parent": 12,
	///       "modifications": [ { "target": 51, "path": "m_LocalPosition", "tag": "Vector3", "value": [1, 0, 2] },
	///                          { "target": 52, "path": "loot", "key": "gold", "keyTag": "String", "tag": "Int32", "value": 5 },
	///                          { "target": 52, "path": "loot", "key": "rope", "keyTag": "String", "removed": true } ],
	///       "removedObjects": [ 53 ], "removedComponents": [ 54 ],
	///       "addedComponents": [ { "gameObject": 51, "$type": "Game.Glow, Game", "$id": 99, "power": 2 } ] }
	///     </code>
	/// </summary>
	public sealed class PrefabInstanceRecord
	{
		public ulong Id;
		public AssetReference Prefab;
		public ulong Parent;
		public readonly List<PropertyModification> Modifications = new List<PropertyModification>();
		public readonly List<ulong> RemovedObjects = new List<ulong>();
		public readonly List<ulong> RemovedComponents = new List<ulong>();
		public readonly List<DocumentComponent> AddedComponents = new List<DocumentComponent>();
	}
}
