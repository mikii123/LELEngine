using System;
using System.Collections.Generic;
using System.Globalization;

namespace LELEngine.Serialization
{
	/// <summary>Which side's value a scene merge keeps where both sides changed it (<see cref="SceneMerge" />).</summary>
	public enum MergeSide
	{
		/// <summary>The editor's value (its unsaved changes).</summary>
		Mine,

		/// <summary>The file's value (changed on disk).</summary>
		Theirs
	}

	/// <summary>A value both sides of a <see cref="SceneMerge" /> changed, to different values.</summary>
	public sealed class SceneMergeConflict
	{
		#region PublicFields

		/// <summary>The value's identity, the same in every merge of the same documents: what a resolver keys on.</summary>
		public string Key;

		/// <summary>The GameObject or prefab instance the value belongs to (0: the scene's name or settings).</summary>
		public ulong ObjectId;

		public string ObjectName;

		/// <summary>The component's class (short name); null for the object itself.</summary>
		public string Component;

		/// <summary>What the value is: a property path, or the object's state ("deleted", "parent", "order of children").</summary>
		public string What;

		/// <summary>The editor's value, as text.</summary>
		public string Mine;

		/// <summary>The file's value, as text.</summary>
		public string Theirs;

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return ObjectName + (Component != null ? " / " + Component : "") + ": " + What + ": here " + Mine + ", on disk " + Theirs;
		}

		#endregion
	}

	/// <summary>The merged document of a <see cref="SceneMerge" /> and what it found.</summary>
	public sealed class SceneMergeResult
	{
		#region PublicFields

		public SceneDocument Document;

		/// <summary>Every value both sides changed differently, settled by the resolver (none: by the file's value).</summary>
		public readonly List<SceneMergeConflict> Conflicts = new List<SceneMergeConflict>();

		/// <summary>Changes of the editor's side the result keeps (0: the result is the file's document).</summary>
		public int MineKept;

		/// <summary>Changes of the file the result takes.</summary>
		public int TheirsTaken;

		#endregion
	}

	/// <summary>
	///     Three-way merge of scene (or prefab) documents, for a scene changed on disk while the editor has unsaved changes
	///     of it. The editor's changes are those from <c>mineBase</c> to <c>mine</c> (both written from live scenes with the
	///     same scripts, so a field the scripts gained or lost is no change), the file's those from <c>theirsBase</c> to
	///     <c>theirs</c> (both as read from the file). The result is the file with the editor's changes on top, value by
	///     value: the scene's name; every property of the settings and of every component (nested objects of the same
	///     class property by property; lists, dictionaries, references and assets as one value); a GameObject's name,
	///     active flag, parent, layer and hide flags; components added, removed and their order; a prefab instance's
	///     prefab, parent, each override, the objects and components it removes and the components it adds; objects and
	///     prefab instances added or deleted (a deletion takes the subtree with it); and the order of siblings. A value
	///     both sides changed differently (a deletion on one side and a change inside the deleted subtree on the other
	///     too) is a conflict, settled by <c>resolve</c> (none: the file's value) and listed in the result either way.
	///     The documents are generic data: no type is loaded, only class defaults are looked up (a property one side
	///     lacks compares as the class default).
	/// </summary>
	public static class SceneMerge
	{
		#region PublicMethods

		/// <param name="instanceObjects">
		///     The ids of a prefab instance's objects (where objects added under them belong); null: the prefab resolved from
		///     the project. Without them an object added under an instance stays after the instance that precedes it.
		/// </param>
		public static SceneMergeResult Merge(SceneDocument mineBase, SceneDocument mine, SceneDocument theirsBase, SceneDocument theirs,
			Func<SceneMergeConflict, MergeSide> resolve = null, Func<PrefabInstanceRecord, IEnumerable<ulong>> instanceObjects = null)
		{
			if (mine == null || theirs == null)
			{
				throw new ArgumentNullException(mine == null ? nameof(mine) : nameof(theirs));
			}

			var merger = new Merger(resolve, instanceObjects ?? ResolvedInstanceObjects);
			return merger.Run(mineBase ?? new SceneDocument(), mine, theirsBase ?? new SceneDocument(), theirs);
		}

		#endregion

		#region PrivateMethods

		/// <summary>The ids of a prefab instance's objects, from its prefab resolved in the project (none when it cannot be).</summary>
		private static IEnumerable<ulong> ResolvedInstanceObjects(PrefabInstanceRecord record)
		{
			ResolvedPrefab prefab = null;
			try
			{
				string path = record.Prefab.IsNull ? null : PrefabCache.PathOf(record.Prefab);
				prefab = path != null ? PrefabCache.Get(path) : null;
			}
			catch (Exception)
			{
				// No project (a merge of documents only): the order of the documents places such objects.
			}

			var ids = new List<ulong>();
			if (prefab != null)
			{
				foreach (PrefabObject o in prefab.Objects)
				{
					ids.Add(PrefabIds.Combine(record.Id, o.Id));
				}
			}

			return ids;
		}

		private static ulong KeyOf(DocumentEntry entry)
		{
			return entry.Object != null ? entry.Object.Id : entry.Instance?.Id ?? 0;
		}

		private static ulong ParentOf(DocumentEntry entry)
		{
			return entry.Object != null ? entry.Object.Parent : entry.Instance?.Parent ?? 0;
		}

		private static string NameOf(DocumentEntry entry)
		{
			if (entry?.Object != null)
			{
				return entry.Object.Name ?? "GameObject";
			}

			string path = entry?.Instance?.Prefab.Path;
			return (path != null ? System.IO.Path.GetFileNameWithoutExtension(path) : "prefab") + " (prefab instance)";
		}

		private static string ShortType(DocumentComponent component)
		{
			return component?.Data != null ? GenericValues.Describe(component.Data, ValueTag.Object) : "component";
		}

		private static string Quote(string text)
		{
			return text != null ? "\"" + text + "\"" : "none";
		}

		private static string Hex(ulong id)
		{
			return "#" + id.ToString("X", CultureInfo.InvariantCulture);
		}

		// ---------------------------------------------------------------- equality

		/// <summary>The same value, a missing property being the class default.</summary>
		private static bool SameProperty(string typeName, string name, SerializedProperty a, SerializedProperty b)
		{
			if (a == null || b == null)
			{
				a ??= GenericValues.DefaultProperty(typeName, name);
				b ??= GenericValues.DefaultProperty(typeName, name);
				if (a == null || b == null)
				{
					return a == null && b == null;
				}
			}

			return a.Tag == b.Tag && GenericValues.AreEqual(a.Value, b.Value, a.Tag);
		}

		private static bool DataEquals(SerializedNode a, SerializedNode b)
		{
			return a == null || b == null ? a == null && b == null : GenericValues.NodesEqual(a, b);
		}

		private static bool ComponentEquals(DocumentComponent a, DocumentComponent b)
		{
			if (a == null || b == null)
			{
				return a == null && b == null;
			}

			return a.Id == b.Id && a.Enabled == b.Enabled && a.GameObject == b.GameObject && DataEquals(a.Data, b.Data);
		}

		private static bool ComponentsEqual(List<DocumentComponent> a, List<DocumentComponent> b)
		{
			if (a.Count != b.Count)
			{
				return false;
			}

			for (int i = 0; i < a.Count; i++)
			{
				if (!ComponentEquals(a[i], b[i]))
				{
					return false;
				}
			}

			return true;
		}

		private static bool ModificationEquals(PropertyModification a, PropertyModification b)
		{
			if (a == null || b == null)
			{
				return a == null && b == null;
			}

			return a.SameProperty(b) && a.Removed == b.Removed && (a.Removed || (a.Tag == b.Tag && GenericValues.AreEqual(a.Value, b.Value, a.Tag)));
		}

		private static bool EntryEquals(DocumentEntry a, DocumentEntry b)
		{
			if (a == null || b == null)
			{
				return a == null && b == null;
			}

			if (a.Object != null && b.Object != null)
			{
				DocumentObject x = a.Object, y = b.Object;
				return x.Id == y.Id && x.Name == y.Name && x.Active == y.Active && x.Parent == y.Parent && x.HideFlags == y.HideFlags && x.Layer == y.Layer
					&& ComponentsEqual(x.Components, y.Components);
			}

			if (a.Instance != null && b.Instance != null)
			{
				PrefabInstanceRecord x = a.Instance, y = b.Instance;
				if (x.Id != y.Id || x.Parent != y.Parent || !GenericValues.SameAsset(x.Prefab, y.Prefab) || x.Modifications.Count != y.Modifications.Count
					|| !SameIds(x.RemovedObjects, y.RemovedObjects) || !SameIds(x.RemovedComponents, y.RemovedComponents) || !ComponentsEqual(x.AddedComponents, y.AddedComponents))
				{
					return false;
				}

				Dictionary<string, PropertyModification> other = Modifications(y);
				foreach (PropertyModification modification in x.Modifications)
				{
					if (!other.TryGetValue(ModificationKey(modification), out PropertyModification match) || !ModificationEquals(modification, match))
					{
						return false;
					}
				}

				return true;
			}

			return false;
		}

		private static bool SameIds(List<ulong> a, List<ulong> b)
		{
			return a.Count == b.Count && new HashSet<ulong>(a).SetEquals(b);
		}

		private static string ModificationKey(PropertyModification modification)
		{
			return modification.Target.ToString(CultureInfo.InvariantCulture) + ":" + modification.FullPath;
		}

		private static Dictionary<string, PropertyModification> Modifications(PrefabInstanceRecord record)
		{
			var map = new Dictionary<string, PropertyModification>(StringComparer.Ordinal);
			if (record != null)
			{
				foreach (PropertyModification modification in record.Modifications)
				{
					map[ModificationKey(modification)] = modification;
				}
			}

			return map;
		}

		// ---------------------------------------------------------------- order

		/// <summary>The keys both lists hold are in another order in one of them.</summary>
		private static bool RelativeOrderDiffers(List<ulong> a, List<ulong> b)
		{
			var inB = new HashSet<ulong>(b);
			var inA = new HashSet<ulong>(a);
			int j = 0;
			foreach (ulong key in a)
			{
				if (!inB.Contains(key))
				{
					continue;
				}

				while (j < b.Count && !inA.Contains(b[j]))
				{
					j++;
				}

				if (j >= b.Count || b[j] != key)
				{
					return true;
				}

				j++;
			}

			return false;
		}

		/// <summary>
		///     <paramref name="keep" /> in the order of <paramref name="primary" />; keys only <paramref name="secondary" />
		///     has go after the key they follow there, the rest at the end.
		/// </summary>
		private static List<ulong> OrderKeys(List<ulong> keep, List<ulong> primary, List<ulong> secondary)
		{
			var kept = new HashSet<ulong>(keep);
			var order = new List<ulong>();
			var placed = new HashSet<ulong>();
			foreach (ulong key in primary)
			{
				if (kept.Contains(key) && placed.Add(key))
				{
					order.Add(key);
				}
			}

			int at = 0;
			foreach (ulong key in secondary)
			{
				if (placed.Contains(key))
				{
					at = order.IndexOf(key) + 1;
					continue;
				}

				if (kept.Contains(key))
				{
					order.Insert(at++, key);
					placed.Add(key);
				}
			}

			foreach (ulong key in keep)
			{
				if (placed.Add(key))
				{
					order.Add(key);
				}
			}

			return order;
		}

		private static List<ulong> Ids(List<DocumentComponent> components)
		{
			var ids = new List<ulong>();
			if (components != null)
			{
				foreach (DocumentComponent component in components)
				{
					ids.Add(component.Id);
				}
			}

			return ids;
		}

		#endregion

		#region Nested types

		/// <summary>Where a value is, for conflicts: the object, the component, and the prefix of the values' keys.</summary>
		private readonly struct Where
		{
			public readonly ulong ObjectId;
			public readonly string ObjectName;
			public readonly string Component;
			public readonly string Prefix;

			public Where(ulong objectId, string objectName, string component, string prefix)
			{
				ObjectId = objectId;
				ObjectName = objectName;
				Component = component;
				Prefix = prefix;
			}

			public Where ForComponent(ulong id, string component)
			{
				return new Where(ObjectId, ObjectName, component, Prefix + "c:" + id.ToString(CultureInfo.InvariantCulture) + "/");
			}
		}

		/// <summary>A document's entries by id, in order, each with the entry it belongs under (its owner) and its children.</summary>
		private sealed class Index
		{
			public readonly Dictionary<ulong, DocumentEntry> Entries = new Dictionary<ulong, DocumentEntry>();
			public readonly List<ulong> Order = new List<ulong>();
			public readonly Dictionary<ulong, ulong> Owners = new Dictionary<ulong, ulong>();
			public readonly Dictionary<ulong, List<ulong>> Children = new Dictionary<ulong, List<ulong>>();

			private static readonly List<ulong> None = new List<ulong>();

			public Index(SceneDocument document, Func<PrefabInstanceRecord, IEnumerable<ulong>> instanceObjects)
			{
				var instanceOf = new Dictionary<ulong, ulong>();
				foreach (DocumentEntry entry in document.Entries)
				{
					ulong key = KeyOf(entry);
					if (key == 0 || !Entries.TryAdd(key, entry))
					{
						continue;
					}

					Order.Add(key);
					if (entry.Instance != null)
					{
						foreach (ulong id in instanceObjects(entry.Instance))
						{
							instanceOf.TryAdd(id, key);
						}
					}
				}

				ulong lastInstance = 0;
				foreach (ulong key in Order)
				{
					DocumentEntry entry = Entries[key];
					ulong owner = OwnerOf(ParentOf(entry), Entries, instanceOf, lastInstance);
					Owners[key] = owner;
					ChildList(owner).Add(key);
					if (entry.Instance != null)
					{
						lastInstance = key;
					}
				}
			}

			public DocumentEntry Get(ulong key)
			{
				return Entries.TryGetValue(key, out DocumentEntry entry) ? entry : null;
			}

			public bool Has(ulong key)
			{
				return Entries.ContainsKey(key);
			}

			public List<ulong> ChildrenOf(ulong owner)
			{
				return Children.TryGetValue(owner, out List<ulong> children) ? children : None;
			}

			/// <summary>Adds the key and every key under it (in this document) to <paramref name="into" />.</summary>
			public void CollectSubtree(ulong key, HashSet<ulong> into)
			{
				var visited = new HashSet<ulong>();
				var stack = new Stack<ulong>();
				stack.Push(key);
				while (stack.Count > 0)
				{
					ulong next = stack.Pop();
					if (!visited.Add(next))
					{
						continue;
					}

					into.Add(next);
					foreach (ulong child in ChildrenOf(next))
					{
						stack.Push(child);
					}
				}
			}

			/// <summary>
			///     The entry a parent id puts an entry under: the GameObject entry with that id, the prefab instance holding
			///     that object, or (the instance's prefab unknown) the last instance before it; 0 for the root.
			/// </summary>
			public static ulong OwnerOf(ulong parent, Dictionary<ulong, DocumentEntry> entries, Dictionary<ulong, ulong> instanceOf, ulong lastInstance)
			{
				if (parent == 0)
				{
					return 0;
				}

				if (entries.TryGetValue(parent, out DocumentEntry entry) && entry.Object != null)
				{
					return parent;
				}

				return instanceOf.TryGetValue(parent, out ulong instance) ? instance : lastInstance;
			}

			private List<ulong> ChildList(ulong owner)
			{
				if (!Children.TryGetValue(owner, out List<ulong> list))
				{
					list = new List<ulong>();
					Children[owner] = list;
				}

				return list;
			}
		}

		private enum Fate
		{
			Drop,
			Mine,
			Theirs
		}

		private sealed class Merger
		{
			private readonly Func<SceneMergeConflict, MergeSide> resolve;
			private readonly Func<PrefabInstanceRecord, IEnumerable<ulong>> instanceObjects;
			private readonly SceneMergeResult result = new SceneMergeResult();
			private Index mb, m, tb, t;

			public Merger(Func<SceneMergeConflict, MergeSide> resolve, Func<PrefabInstanceRecord, IEnumerable<ulong>> instanceObjects)
			{
				this.resolve = resolve;
				this.instanceObjects = instanceObjects;
			}

			public SceneMergeResult Run(SceneDocument mineBase, SceneDocument mine, SceneDocument theirsBase, SceneDocument theirs)
			{
				mb = new Index(mineBase, instanceObjects);
				m = new Index(mine, instanceObjects);
				tb = new Index(theirsBase, instanceObjects);
				t = new Index(theirs, instanceObjects);

				var document = new SceneDocument { Kind = theirs.Kind ?? mine.Kind };
				var scene = new Where(0, "Scene", null, "scene/");
				document.Name = Pick(scene, "name", "name", true, mineBase.Name, mine.Name, true, theirsBase.Name, theirs.Name, string.Equals, Quote);
				document.Settings = MergeSettings(new Where(0, "Scene settings", null, "settings/"), mineBase.Settings, mine.Settings, theirsBase.Settings, theirs.Settings);
				MergeEntries(document);
				result.Document = document;
				return result;
			}

			// ---------------------------------------------------------------- values

			/// <summary>
			///     One value: the file's unless the editor changed it, the editor's unless the file changed it too, a conflict
			///     when both changed it differently. A side without the value's owner in its base counts as having changed it.
			/// </summary>
			private T Pick<T>(Where where, string what, string key, bool inMineBase, T mineBase, T mine, bool inTheirsBase, T theirsBase, T theirs, Func<T, T, bool> same, Func<T, string> describe)
			{
				bool mineChanged = !inMineBase || !same(mineBase, mine);
				bool theirsChanged = !inTheirsBase || !same(theirsBase, theirs);
				if (!mineChanged)
				{
					if (theirsChanged)
					{
						result.TheirsTaken++;
					}

					return theirs;
				}

				if (same(mine, theirs))
				{
					return theirs;
				}

				if (!theirsChanged)
				{
					result.MineKept++;
					return mine;
				}

				return Conflict(where, what, key, describe(mine), describe(theirs)) == MergeSide.Mine ? mine : theirs;
			}

			private MergeSide Conflict(Where where, string what, string key, string mine, string theirs)
			{
				var conflict = new SceneMergeConflict
				{
					Key = where.Prefix + key,
					ObjectId = where.ObjectId,
					ObjectName = where.ObjectName,
					Component = where.Component,
					What = what,
					Mine = mine,
					Theirs = theirs
				};

				result.Conflicts.Add(conflict);
				MergeSide side = resolve != null ? resolve(conflict) : MergeSide.Theirs;
				if (side == MergeSide.Mine)
				{
					result.MineKept++;
				}
				else
				{
					result.TheirsTaken++;
				}

				return side;
			}

			private SerializedNode MergeSettings(Where where, SerializedNode mineBase, SerializedNode mine, SerializedNode theirsBase, SerializedNode theirs)
			{
				if (mine != null && theirs != null && mine.TypeName == theirs.TypeName)
				{
					return MergeNode(where, null, mineBase, mine, theirsBase, theirs);
				}

				return Pick(where, "settings", "settings", true, mineBase, mine, true, theirsBase, theirs, DataEquals, n => GenericValues.Describe(n, ValueTag.Object));
			}

			/// <summary>Two nodes of one class property by property (bases of another class or missing: every value changed).</summary>
			private SerializedNode MergeNode(Where where, string path, SerializedNode mineBase, SerializedNode mine, SerializedNode theirsBase, SerializedNode theirs)
			{
				if (mineBase != null && mineBase.TypeName != mine.TypeName)
				{
					mineBase = null;
				}

				if (theirsBase != null && theirsBase.TypeName != theirs.TypeName)
				{
					theirsBase = null;
				}

				var merged = new SerializedNode { TypeName = theirs.TypeName };
				var names = new List<string>();
				var seen = new HashSet<string>(StringComparer.Ordinal);
				foreach (SerializedProperty property in theirs.Properties)
				{
					if (seen.Add(property.Name))
					{
						names.Add(property.Name);
					}
				}

				foreach (SerializedProperty property in mine.Properties)
				{
					if (seen.Add(property.Name))
					{
						names.Add(property.Name);
					}
				}

				foreach (string name in names)
				{
					SerializedProperty value = MergeProperty(where, PropertyPath.Field(path, name), theirs.TypeName, name,
						mineBase, mineBase?.Find(name), mine.Find(name), theirsBase, theirsBase?.Find(name), theirs.Find(name));
					if (value != null)
					{
						merged.Properties.Add(value);
					}
				}

				return merged;
			}

			private SerializedProperty MergeProperty(Where where, string path, string typeName, string name, SerializedNode mineBaseNode, SerializedProperty mineBase,
				SerializedProperty mine, SerializedNode theirsBaseNode, SerializedProperty theirsBase, SerializedProperty theirs)
			{
				bool mineChanged = mineBaseNode == null || !SameProperty(typeName, name, mineBase, mine);
				bool theirsChanged = theirsBaseNode == null || !SameProperty(typeName, name, theirsBase, theirs);
				if (!mineChanged)
				{
					if (theirsChanged)
					{
						result.TheirsTaken++;
					}

					return theirs;
				}

				if (SameProperty(typeName, name, mine, theirs))
				{
					return theirs;
				}

				if (!theirsChanged)
				{
					result.MineKept++;
					return mine;
				}

				// Both changed a nested object of the same class: its properties one by one.
				if (mine?.Value is SerializedNode mineNode && theirs?.Value is SerializedNode theirsNode && mine.Tag == theirs.Tag && mineNode.TypeName == theirsNode.TypeName)
				{
					return new SerializedProperty
					{
						Name = theirs.Name,
						Tag = theirs.Tag,
						ElementTag = theirs.ElementTag,
						Value = MergeNode(where, path, mineBase?.Value as SerializedNode, mineNode, theirsBase?.Value as SerializedNode, theirsNode)
					};
				}

				return Conflict(where, path, path, Describe(mine), Describe(theirs)) == MergeSide.Mine ? mine : theirs;
			}

			private static string Describe(SerializedProperty property)
			{
				return property != null ? GenericValues.Describe(property.Value, property.Tag) : "none";
			}

			// ---------------------------------------------------------------- entries

			private void MergeEntries(SceneDocument document)
			{
				var keys = new List<ulong>();
				var seen = new HashSet<ulong>();
				foreach (Index index in new[] { t, m, tb, mb })
				{
					foreach (ulong key in index.Order)
					{
						if (seen.Add(key))
						{
							keys.Add(key);
						}
					}
				}

				Dictionary<ulong, Fate> fates = Deletions(keys);
				var kept = new Dictionary<ulong, DocumentEntry>();
				var keptOrder = new List<ulong>();
				foreach (ulong key in keys)
				{
					DocumentEntry mine = m.Get(key);
					DocumentEntry theirs = t.Get(key);
					DocumentEntry entry;
					if (fates.TryGetValue(key, out Fate fate))
					{
						entry = fate == Fate.Mine ? mine : fate == Fate.Theirs ? theirs : null;
					}
					else if (mine != null && theirs != null)
					{
						entry = MergeEntry(key, mb.Get(key), mine, tb.Get(key), theirs);
					}
					else if (mine != null)
					{
						// Added here (a deletion on disk is settled with its subtree above).
						bool changed = !EntryEquals(mb.Get(key), mine);
						entry = !tb.Has(key) || changed ? mine : null;
						if (entry != null)
						{
							result.MineKept++;
						}
					}
					else if (theirs != null)
					{
						bool changed = !EntryEquals(tb.Get(key), theirs);
						entry = !mb.Has(key) || changed ? theirs : null;
						if (entry != null)
						{
							result.TheirsTaken++;
						}
					}
					else
					{
						entry = null;
					}

					if (entry != null)
					{
						kept[key] = entry;
						keptOrder.Add(key);
					}
				}

				Arrange(document, kept, keptOrder);
			}

			/// <summary>
			///     Entries deleted on one side, a subtree at a time: gone when the other side changed nothing in the subtree,
			///     else a conflict deciding the whole subtree. The keys returned are settled (dropped or taken from one side).
			/// </summary>
			private Dictionary<ulong, Fate> Deletions(List<ulong> keys)
			{
				var fates = new Dictionary<ulong, Fate>();
				foreach (ulong key in keys)
				{
					// Deleted here: the top of a deleted subtree has its owner still here (or none).
					if (mb.Has(key) && !m.Has(key) && t.Has(key) && Kept(m, mb.Owners[key]))
					{
						var subtree = new HashSet<ulong>();
						mb.CollectSubtree(key, subtree);
						t.CollectSubtree(key, subtree);
						subtree.RemoveWhere(k => m.Has(k));
						// What the file still has of the subtree and changed (what it deleted too is no conflict).
						bool changedThere = false;
						foreach (ulong k in subtree)
						{
							changedThere |= t.Has(k) && !EntryEquals(tb.Get(k), t.Get(k));
						}

						if (!changedThere)
						{
							Settle(fates, subtree, Fate.Drop);
							result.MineKept++;
							continue;
						}

						DocumentEntry entry = t.Get(key);
						MergeSide side = Conflict(new Where(key, NameOf(entry), null, "o:" + key.ToString(CultureInfo.InvariantCulture) + "/"), "deleted", "deleted", "deleted", "changed");
						Settle(fates, subtree, side == MergeSide.Mine ? Fate.Drop : Fate.Theirs);
					}
					else if (tb.Has(key) && !t.Has(key) && m.Has(key) && Kept(t, tb.Owners[key]))
					{
						var subtree = new HashSet<ulong>();
						tb.CollectSubtree(key, subtree);
						m.CollectSubtree(key, subtree);
						subtree.RemoveWhere(k => t.Has(k));
						bool changedHere = false;
						foreach (ulong k in subtree)
						{
							changedHere |= m.Has(k) && !EntryEquals(mb.Get(k), m.Get(k));
						}

						if (!changedHere)
						{
							Settle(fates, subtree, Fate.Drop);
							result.TheirsTaken++;
							continue;
						}

						DocumentEntry entry = m.Get(key);
						MergeSide side = Conflict(new Where(key, NameOf(entry), null, "o:" + key.ToString(CultureInfo.InvariantCulture) + "/"), "deleted", "deleted", "changed", "deleted");
						Settle(fates, subtree, side == MergeSide.Mine ? Fate.Mine : Fate.Drop);
					}
				}

				return fates;
			}

			private static bool Kept(Index index, ulong owner)
			{
				return owner == 0 || index.Has(owner);
			}

			private static void Settle(Dictionary<ulong, Fate> fates, HashSet<ulong> keys, Fate fate)
			{
				foreach (ulong key in keys)
				{
					fates.TryAdd(key, fate);
				}
			}

			private DocumentEntry MergeEntry(ulong key, DocumentEntry mineBase, DocumentEntry mine, DocumentEntry theirsBase, DocumentEntry theirs)
			{
				if (mine.Object != null && theirs.Object != null)
				{
					return new DocumentEntry { Object = MergeObject(mineBase?.Object, mine.Object, theirsBase?.Object, theirs.Object) };
				}

				if (mine.Instance != null && theirs.Instance != null)
				{
					return new DocumentEntry { Instance = MergeInstance(mineBase?.Instance, mine.Instance, theirsBase?.Instance, theirs.Instance) };
				}

				// An object on one side, a prefab instance on the other: one or the other.
				var where = new Where(key, NameOf(theirs), null, "o:" + key.ToString(CultureInfo.InvariantCulture) + "/");
				return Pick(where, "replaced", "replaced", mineBase != null, mineBase, mine, theirsBase != null, theirsBase, theirs, EntryEquals, e => e.Instance != null ? "prefab instance" : "GameObject");
			}

			private DocumentObject MergeObject(DocumentObject mineBase, DocumentObject mine, DocumentObject theirsBase, DocumentObject theirs)
			{
				var where = new Where(theirs.Id, theirs.Name ?? mine.Name, null, "o:" + theirs.Id.ToString(CultureInfo.InvariantCulture) + "/");
				bool inMine = mineBase != null, inTheirs = theirsBase != null;
				var merged = new DocumentObject
				{
					Id = theirs.Id,
					Name = Pick(where, "name", "name", inMine, mineBase?.Name, mine.Name, inTheirs, theirsBase?.Name, theirs.Name, string.Equals, Quote),
					Active = Pick(where, "active", "active", inMine, mineBase?.Active ?? true, mine.Active, inTheirs, theirsBase?.Active ?? true, theirs.Active, (a, b) => a == b, a => a ? "active" : "inactive"),
					Parent = Pick(where, "parent", "parent", inMine, mineBase?.Parent ?? 0, mine.Parent, inTheirs, theirsBase?.Parent ?? 0, theirs.Parent, (a, b) => a == b, DescribeParent),
					HideFlags = Pick(where, "hide flags", "hideFlags", inMine, mineBase?.HideFlags ?? 0, mine.HideFlags, inTheirs, theirsBase?.HideFlags ?? 0, theirs.HideFlags, (a, b) => a == b, a => a.ToString(CultureInfo.InvariantCulture)),
					Layer = Pick(where, "layer", "layer", inMine, mineBase?.Layer ?? 0, mine.Layer, inTheirs, theirsBase?.Layer ?? 0, theirs.Layer, (a, b) => a == b, a => a.ToString(CultureInfo.InvariantCulture))
				};

				MergeComponents(where, mineBase?.Components, mine.Components, theirsBase?.Components, theirs.Components, merged.Components);
				return merged;
			}

			private PrefabInstanceRecord MergeInstance(PrefabInstanceRecord mineBase, PrefabInstanceRecord mine, PrefabInstanceRecord theirsBase, PrefabInstanceRecord theirs)
			{
				string name = NameOf(new DocumentEntry { Instance = theirs });
				var where = new Where(theirs.Id, name, null, "i:" + theirs.Id.ToString(CultureInfo.InvariantCulture) + "/");
				bool inMine = mineBase != null, inTheirs = theirsBase != null;
				var merged = new PrefabInstanceRecord
				{
					Id = theirs.Id,
					Prefab = Pick(where, "prefab", "prefab", inMine, mineBase?.Prefab ?? default, mine.Prefab, inTheirs, theirsBase?.Prefab ?? default, theirs.Prefab, GenericValues.SameAsset,
						a => GenericValues.Describe(a, ValueTag.Asset)),
					Parent = Pick(where, "parent", "parent", inMine, mineBase?.Parent ?? 0, mine.Parent, inTheirs, theirsBase?.Parent ?? 0, theirs.Parent, (a, b) => a == b, DescribeParent)
				};

				// Overrides, each by its target and path.
				Dictionary<string, PropertyModification> mineBaseMods = Modifications(mineBase), mineMods = Modifications(mine);
				Dictionary<string, PropertyModification> theirsBaseMods = Modifications(theirsBase), theirsMods = Modifications(theirs);
				var keys = new List<string>();
				var seen = new HashSet<string>(StringComparer.Ordinal);
				foreach (PrefabInstanceRecord record in new[] { theirs, mine, theirsBase, mineBase })
				{
					if (record == null)
					{
						continue;
					}

					foreach (PropertyModification modification in record.Modifications)
					{
						string key = ModificationKey(modification);
						if (seen.Add(key))
						{
							keys.Add(key);
						}
					}
				}

				foreach (string key in keys)
				{
					PropertyModification any = Find(theirsMods, key) ?? Find(mineMods, key) ?? Find(theirsBaseMods, key) ?? Find(mineBaseMods, key);
					PropertyModification value = Pick(where, "override " + any.FullPath + " of " + Hex(any.Target), "m:" + key,
						inMine, Find(mineBaseMods, key), Find(mineMods, key), inTheirs, Find(theirsBaseMods, key), Find(theirsMods, key), ModificationEquals,
						v => v == null ? "not overridden" : v.Removed ? "entry removed" : GenericValues.Describe(v.Value, v.Tag));
					if (value != null)
					{
						merged.Modifications.Add(value);
					}
				}

				MergeIds(where, "removed object", "ro:", mineBase?.RemovedObjects, mine.RemovedObjects, theirsBase?.RemovedObjects, theirs.RemovedObjects, merged.RemovedObjects);
				MergeIds(where, "removed component", "rc:", mineBase?.RemovedComponents, mine.RemovedComponents, theirsBase?.RemovedComponents, theirs.RemovedComponents, merged.RemovedComponents);
				MergeComponents(where, mineBase?.AddedComponents, mine.AddedComponents, theirsBase?.AddedComponents, theirs.AddedComponents, merged.AddedComponents);
				return merged;
			}

			private static PropertyModification Find(Dictionary<string, PropertyModification> map, string key)
			{
				return map.TryGetValue(key, out PropertyModification modification) ? modification : null;
			}

			/// <summary>A set of ids (an instance's removed objects or components), each id in or out.</summary>
			private void MergeIds(Where where, string what, string prefix, List<ulong> mineBase, List<ulong> mine, List<ulong> theirsBase, List<ulong> theirs, List<ulong> into)
			{
				var ids = new List<ulong>();
				var seen = new HashSet<ulong>();
				foreach (List<ulong> list in new[] { theirs, mine, theirsBase, mineBase })
				{
					if (list == null)
					{
						continue;
					}

					foreach (ulong id in list)
					{
						if (seen.Add(id))
						{
							ids.Add(id);
						}
					}
				}

				foreach (ulong id in ids)
				{
					bool inMineBase = mineBase != null && mineBase.Contains(id), inTheirsBase = theirsBase != null && theirsBase.Contains(id);
					if (Pick(where, what + " " + Hex(id), prefix + id.ToString(CultureInfo.InvariantCulture), mineBase != null, inMineBase, mine.Contains(id),
						theirsBase != null, inTheirsBase, theirs.Contains(id), (a, b) => a == b, a => a ? "removed" : "kept"))
					{
						into.Add(id);
					}
				}
			}

			private void MergeComponents(Where owner, List<DocumentComponent> mineBase, List<DocumentComponent> mine, List<DocumentComponent> theirsBase, List<DocumentComponent> theirs, List<DocumentComponent> into)
			{
				Dictionary<ulong, DocumentComponent> mineBaseById = ById(mineBase), mineById = ById(mine), theirsBaseById = ById(theirsBase), theirsById = ById(theirs);
				var ids = new List<ulong>();
				var seen = new HashSet<ulong>();
				foreach (List<DocumentComponent> list in new[] { theirs, mine })
				{
					foreach (DocumentComponent component in list)
					{
						if (seen.Add(component.Id))
						{
							ids.Add(component.Id);
						}
					}
				}

				var kept = new Dictionary<ulong, DocumentComponent>();
				foreach (ulong id in ids)
				{
					mineBaseById.TryGetValue(id, out DocumentComponent cmb);
					mineById.TryGetValue(id, out DocumentComponent cm);
					theirsBaseById.TryGetValue(id, out DocumentComponent ctb);
					theirsById.TryGetValue(id, out DocumentComponent ct);
					Where where = owner.ForComponent(id, ShortType(cm ?? ct));
					if (cm != null && ct != null)
					{
						kept[id] = MergeComponent(where, cmb, cm, ctb, ct);
					}
					else if (cm != null)
					{
						if (ctb == null)
						{
							// Added here.
							kept[id] = cm;
							result.MineKept++;
						}
						else if (ComponentEquals(cmb, cm))
						{
							// Removed on disk, unchanged here.
							result.TheirsTaken++;
						}
						else if (Conflict(where, "removed", "removed", "changed", "removed") == MergeSide.Mine)
						{
							kept[id] = cm;
						}
					}
					else if (cmb == null)
					{
						// Added on disk.
						kept[id] = ct;
						result.TheirsTaken++;
					}
					else if (ComponentEquals(ctb, ct))
					{
						// Removed here, unchanged on disk.
						result.MineKept++;
					}
					else if (Conflict(where, "removed", "removed", "removed", "changed") == MergeSide.Theirs)
					{
						kept[id] = ct;
					}
				}

				var keep = new List<ulong>();
				foreach (ulong id in ids)
				{
					if (kept.ContainsKey(id))
					{
						keep.Add(id);
					}
				}

				foreach (ulong id in MergeOrder(owner, "order of components", "components", keep, mineBase != null ? Ids(mineBase) : null, Ids(mine), theirsBase != null ? Ids(theirsBase) : null, Ids(theirs)))
				{
					into.Add(kept[id]);
				}
			}

			private static Dictionary<ulong, DocumentComponent> ById(List<DocumentComponent> components)
			{
				var map = new Dictionary<ulong, DocumentComponent>();
				if (components != null)
				{
					foreach (DocumentComponent component in components)
					{
						map.TryAdd(component.Id, component);
					}
				}

				return map;
			}

			private DocumentComponent MergeComponent(Where where, DocumentComponent mineBase, DocumentComponent mine, DocumentComponent theirsBase, DocumentComponent theirs)
			{
				bool inMine = mineBase != null, inTheirs = theirsBase != null;
				var merged = new DocumentComponent
				{
					Id = theirs.Id,
					Enabled = Pick(where, "enabled", "enabled", inMine, mineBase?.Enabled ?? true, mine.Enabled, inTheirs, theirsBase?.Enabled ?? true, theirs.Enabled, (a, b) => a == b, a => a ? "enabled" : "disabled"),
					GameObject = Pick(where, "object", "gameObject", inMine, mineBase?.GameObject ?? 0, mine.GameObject, inTheirs, theirsBase?.GameObject ?? 0, theirs.GameObject, (a, b) => a == b, Hex)
				};

				if (mine.Data != null && theirs.Data != null && mine.Data.TypeName == theirs.Data.TypeName)
				{
					merged.Data = MergeNode(where, null, mineBase?.Data, mine.Data, theirsBase?.Data, theirs.Data);
				}
				else
				{
					merged.Data = Pick(where, "class", "class", inMine, mineBase?.Data, mine.Data, inTheirs, theirsBase?.Data, theirs.Data, DataEquals, n => GenericValues.Describe(n, ValueTag.Object));
				}

				return merged;
			}

			/// <summary>
			///     The order of <paramref name="keep" />: the editor's when it reordered them and the file did not (both did,
			///     differently: a conflict), else the file's; keys only one side has go after the key they follow there.
			/// </summary>
			private List<ulong> MergeOrder(Where where, string what, string key, List<ulong> keep, List<ulong> mineBase, List<ulong> mine, List<ulong> theirsBase, List<ulong> theirs)
			{
				bool mineReordered = mineBase != null && RelativeOrderDiffers(mineBase, mine);
				bool theirsReordered = theirsBase != null && RelativeOrderDiffers(theirsBase, theirs);
				bool useMine = false;
				if (mineReordered && RelativeOrderDiffers(mine, theirs))
				{
					if (!theirsReordered)
					{
						useMine = true;
						result.MineKept++;
					}
					else
					{
						useMine = Conflict(where, what, key, "reordered", "reordered") == MergeSide.Mine;
					}
				}

				return useMine ? OrderKeys(keep, mine, theirs) : OrderKeys(keep, theirs, mine);
			}

			private string DescribeParent(ulong id)
			{
				if (id == 0)
				{
					return "root";
				}

				DocumentEntry entry = m.Get(id) ?? t.Get(id);
				return entry?.Object != null ? Quote(entry.Object.Name) : Hex(id);
			}

			// ---------------------------------------------------------------- order of the entries

			/// <summary>
			///     Writes the kept entries parents first: each under the entry its parent belongs to, siblings in the merged
			///     order (<see cref="MergeOrder" /> per parent), depth first.
			/// </summary>
			private void Arrange(SceneDocument document, Dictionary<ulong, DocumentEntry> kept, List<ulong> keptOrder)
			{
				var instanceOf = new Dictionary<ulong, ulong>();
				foreach (ulong key in keptOrder)
				{
					if (kept[key].Instance != null)
					{
						foreach (ulong id in instanceObjects(kept[key].Instance))
						{
							instanceOf.TryAdd(id, key);
						}
					}
				}

				var children = new Dictionary<ulong, List<ulong>>();
				foreach (ulong key in keptOrder)
				{
					ulong parent = ParentOf(kept[key]);
					ulong owner = Index.OwnerOf(parent, kept, instanceOf, 0);
					if (owner == 0 && parent != 0)
					{
						// Under an instance whose prefab is unknown: where the side giving the parent puts it.
						Index source = t.Has(key) && ParentOf(t.Get(key)) == parent ? t : m.Has(key) ? m : t;
						owner = source.Owners.TryGetValue(key, out ulong found) && kept.ContainsKey(found) ? found : 0;
					}

					if (!children.TryGetValue(owner, out List<ulong> list))
					{
						list = new List<ulong>();
						children[owner] = list;
					}

					list.Add(key);
				}

				var visited = new HashSet<ulong>();
				Emit(document, kept, children, 0, visited);
				foreach (ulong key in keptOrder)
				{
					// Left out by a cycle of parents (each side moved one under the other): the loader keeps them at the root.
					if (visited.Add(key))
					{
						document.Entries.Add(kept[key]);
						Emit(document, kept, children, key, visited);
					}
				}
			}

			private void Emit(SceneDocument document, Dictionary<ulong, DocumentEntry> kept, Dictionary<ulong, List<ulong>> children, ulong owner, HashSet<ulong> visited)
			{
				if (!children.TryGetValue(owner, out List<ulong> keys))
				{
					return;
				}

				string name = owner == 0 ? "Scene" : NameOf(kept[owner]);
				string prefix = owner == 0 ? "scene/" : (kept[owner].Instance != null ? "i:" : "o:") + owner.ToString(CultureInfo.InvariantCulture) + "/";
				List<ulong> order = MergeOrder(new Where(owner, name, null, prefix), "order of children", "children", keys,
					mb.Has(owner) || owner == 0 ? mb.ChildrenOf(owner) : null, m.ChildrenOf(owner), tb.Has(owner) || owner == 0 ? tb.ChildrenOf(owner) : null, t.ChildrenOf(owner));
				foreach (ulong key in order)
				{
					if (visited.Add(key))
					{
						document.Entries.Add(kept[key]);
						Emit(document, kept, children, key, visited);
					}
				}
			}
		}

		#endregion
	}
}
