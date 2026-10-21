using System.Collections.Generic;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>
	///     The link of a prefab instance to its prefab: every GameObject and component created from the prefab knows
	///     its instance and its id in the prefab. How the instance differs from the prefab (overrides, objects and
	///     components added or removed) is worked out from the live objects when the scene is saved
	///     (<see cref="PrefabUtility.GetModifications" />). Nested prefabs inside the prefab are part of the outermost
	///     instance of the scene; <see cref="PrefabUtility" /> tells which nested prefab an object comes from.
	/// </summary>
	public sealed class PrefabInstance
	{
		#region PublicFields

		/// <summary>The instance's id: its objects' ids are <c>Id ^ id in the prefab</c> (<see cref="PrefabIds" />).</summary>
		public ulong Id { get; internal set; }

		/// <summary>The prefab asset (null when the file is missing).</summary>
		public Prefab Prefab { get; internal set; }

		/// <summary>Asset path of the prefab (the last known path when the file is missing).</summary>
		public string PrefabPath { get; internal set; }

		/// <summary>The instance's root GameObject (a placeholder when the prefab is missing).</summary>
		public GameObject Root { get; internal set; }

		/// <summary>The prefab file is missing or cannot be read: the instance's data is kept as it was read and written back unchanged.</summary>
		public bool IsMissing => Source == null;

		#endregion

		#region InternalFields

		/// <summary>The prefab data the instance was built from: the base its overrides are measured against.</summary>
		internal ResolvedPrefab Source;

		/// <summary>A missing prefab's instance as the document stored it (written back as it is).</summary>
		internal PrefabInstanceRecord MissingRecord;

		/// <summary>
		///     Overrides whose object or property the prefab does not have (any more): kept and written back, so they apply
		///     again if the prefab gets it back.
		/// </summary>
		internal readonly List<PropertyModification> UnusedModifications = new List<PropertyModification>();

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return (Root?.Name ?? "?") + " (instance of " + PrefabPath + ")";
		}

		#endregion
	}
}
