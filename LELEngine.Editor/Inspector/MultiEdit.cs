using System;
using System.Collections.Generic;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Where the values of objects edited together differ (Unity's mixed values): the serialized data of the one the
	///     inspector draws against each other's, as property paths (<see cref="PropertyPath" />, the paths the inspector's
	///     drawers keep in <see cref="InspectorContext.Path" />). A field whose path differs shows "—".
	/// </summary>
	internal sealed class MixedValues
	{
		#region PrivateFields

		private readonly List<string> differing = new List<string>();
		private SerializedNode primary;
		private readonly List<SerializedNode> others = new List<SerializedNode>();

		#endregion

		#region PublicFields

		/// <summary>No value differs.</summary>
		public bool None => differing.Count == 0;

		#endregion

		#region PublicMethods

		/// <summary>The values of <paramref name="primary" /> against each of <paramref name="others" /> (objects of one class).</summary>
		public static MixedValues Compare(object primary, IReadOnlyList<object> others, SerializationContext context)
		{
			var mixed = new MixedValues { primary = MultiEdit.NodeOf(primary, context) };
			if (mixed.primary == null)
			{
				return mixed;
			}

			var seen = new HashSet<string>(StringComparer.Ordinal);
			var modifications = new List<PropertyModification>();
			foreach (object other in others)
			{
				SerializedNode node = MultiEdit.NodeOf(other, context);
				mixed.others.Add(node);
				if (node == null)
				{
					continue;
				}

				// Both ways: a list longer on either side has elements the other one lacks.
				modifications.Clear();
				PrefabOverrides.DiffNode(mixed.primary, node, "", 0, modifications, null);
				PrefabOverrides.DiffNode(node, mixed.primary, "", 0, modifications, null);
				foreach (PropertyModification modification in modifications)
				{
					string path = modification.FullPath;
					if (seen.Add(path))
					{
						mixed.differing.Add(path);
					}
				}
			}

			return mixed;
		}

		/// <summary>
		///     Mixed values worked out by the caller (objects with no serialized fields of their own, as materials):
		///     <paramref name="paths" /> differ; the nodes give a vector's components (<see cref="MixedComponents" />).
		/// </summary>
		public static MixedValues FromPaths(IEnumerable<string> paths, SerializedNode primary, List<SerializedNode> others)
		{
			var mixed = new MixedValues { primary = primary };
			mixed.others.AddRange(others);
			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (string path in paths)
			{
				if (seen.Add(path))
				{
					mixed.differing.Add(path);
				}
			}

			return mixed;
		}

		/// <summary>
		///     The value at <paramref name="path" /> differs between the objects (it, or a value holding it, is not the same
		///     on all); with <paramref name="within" /> also when something inside it differs (a list, a nested object).
		/// </summary>
		public bool IsMixed(string path, bool within)
		{
			if (differing.Count == 0 || path == null)
			{
				return false;
			}

			foreach (string different in differing)
			{
				if (PropertyPath.IsWithin(path, different) || within && PropertyPath.IsWithin(different, path))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     Which components (x, y, z, w; r, g, b, a) of a vector or color at <paramref name="path" /> differ between the
		///     objects; null when none does. A path that cannot be read marks them all.
		/// </summary>
		public bool[] MixedComponents(string path, int count)
		{
			if (!IsMixed(path, false))
			{
				return null;
			}

			var mixed = new bool[count];
			if (!GenericValues.TryGet(primary, path, out object value, out _, out _) || !MultiEdit.TryComponents(value, out double[] mine))
			{
				Array.Fill(mixed, true);
				return mixed;
			}

			bool any = false;
			foreach (SerializedNode other in others)
			{
				if (other == null || !GenericValues.TryGet(other, path, out object otherValue, out _, out _) || !MultiEdit.TryComponents(otherValue, out double[] theirs) || theirs.Length != mine.Length)
				{
					Array.Fill(mixed, true);
					return mixed;
				}

				for (int i = 0; i < count && i < mine.Length; i++)
				{
					if (!mine[i].Equals(theirs[i]))
					{
						mixed[i] = true;
						any = true;
					}
				}
			}

			return any ? mixed : null;
		}

		#endregion
	}

	/// <summary>
	///     Several objects of one class edited as one (Unity's multi-object editing): the inspector draws one of them, and
	///     what an edit changed in it (the property paths whose values are new, <see cref="PrefabOverrides.DiffNode" />)
	///     is written into the others, only those values: a vector's or color's components one by one, so dragging X of
	///     objects at different places moves them along X alone. Every object keeps the rest of its values.
	/// </summary>
	internal static class MultiEdit
	{
		#region PublicMethods

		/// <summary>The serialized data of a component or any serializable object (a ScriptableObject, a settings object).</summary>
		public static SerializedNode NodeOf(object target, SerializationContext context)
		{
			if (target == null)
			{
				return null;
			}

			if (target is Behaviour component)
			{
				return PrefabOverrides.NodeOf(component, context);
			}

			if (target is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnBeforeSerialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			return DocumentValues.ToNode(target, context);
		}

		/// <summary>
		///     Writes into each of <paramref name="others" /> the values <paramref name="primary" /> has now that differ from
		///     <paramref name="before" /> (its data before the edit). Returns the objects changed; <paramref name="changed" />
		///     runs for each.
		/// </summary>
		public static int Propagate(object primary, SerializedNode before, IReadOnlyList<object> others, Func<SerializationContext> context, Action<object> changed = null, List<string> touched = null)
		{
			if (primary == null || before == null || others == null || others.Count == 0)
			{
				touched?.Clear();
				return 0;
			}

			// The reading context (every object of the scene by id) only when something changed.
			SerializedNode after = NodeOf(primary, new SerializationContext { Scene = (primary as Behaviour)?.gameObject?.scene });
			var modifications = new List<PropertyModification>();
			PrefabOverrides.DiffNode(before, after, "", 0, modifications, null);

			// Fields set while they differed: their value goes to all, also when the drawn object had it already (a
			// vector's components only as edited: those are in the diff).
			if (touched != null)
			{
				foreach (string path in touched)
				{
					if (path.IndexOf('{') >= 0 || modifications.Exists(m => !m.HasKey && PropertyPath.IsWithin(path, m.Path))
						|| !GenericValues.TryGet(after, path, out object value, out ValueTag tag, out ValueTag elementTag) || IsPerComponent(tag))
					{
						continue;
					}

					modifications.Add(new PropertyModification { Path = path, Tag = tag, ElementTag = elementTag, Value = value });
				}

				touched.Clear();
			}

			return modifications.Count == 0 ? 0 : Apply(modifications, before, others, context(), changed);
		}

		/// <summary>
		///     Writes the modifications (paths and new values of the edited object, whose data before the edit was
		///     <paramref name="before" />) into each of <paramref name="others" />.
		/// </summary>
		public static int Apply(List<PropertyModification> modifications, SerializedNode before, IReadOnlyList<object> others, SerializationContext context, Action<object> changed = null)
		{
			int count = 0;
			foreach (object other in others)
			{
				if (other == null || other is EngineObject engineObject && engineObject.IsDestroyed)
				{
					continue;
				}

				SerializedNode node = NodeOf(other, context);
				if (node == null)
				{
					continue;
				}

				bool any = false;
				foreach (PropertyModification modification in modifications)
				{
					any |= ApplyOne(node, modification, before);
				}

				if (!any)
				{
					continue;
				}

				ReadInto(other, node, context);
				changed?.Invoke(other);
				count++;
			}

			return count;
		}

		/// <summary>Reads serialized data into a live component or object (its references through <paramref name="context" />).</summary>
		public static void ReadInto(object target, SerializedNode node, SerializationContext context)
		{
			if (target is Behaviour component)
			{
				PrefabUtility.ReadInto(component, node, context);
				return;
			}

			var writer = new BinaryDocumentWriter(null) { Kind = BinaryDocument.KindObject };
			int typeId = writer.GenericTypeId(node);
			int lengthPosition = writer.Body.ReserveInt32();
			writer.WriteGenericFields(node);
			writer.Body.PatchInt32(lengthPosition, writer.Body.Length - lengthPosition - 4);
			var reader = new BinaryDocumentReader(writer.ToArray(), context);
			context.Location = target.GetType().Name;
			reader.ReadFieldBlock(target, typeId);
			if (target is ISerializationCallbackReceiver receiver)
			{
				try
				{
					receiver.OnAfterDeserialize();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}
		}

		/// <summary>The components of a vector, color or quaternion value (generic data); false for anything else.</summary>
		public static bool TryComponents(object value, out double[] components)
		{
			switch (value)
			{
				case Vector2 v: components = new double[] { v.X, v.Y }; return true;
				case Vector3 v: components = new double[] { v.X, v.Y, v.Z }; return true;
				case Vector4 v: components = new double[] { v.X, v.Y, v.Z, v.W }; return true;
				case Color4 c: components = new double[] { c.R, c.G, c.B, c.A }; return true;
				case Quaternion q: components = new double[] { q.X, q.Y, q.Z, q.W }; return true;
				case Vector2i v: components = new double[] { v.X, v.Y }; return true;
				case Vector3i v: components = new double[] { v.X, v.Y, v.Z }; return true;
				case Vector4i v: components = new double[] { v.X, v.Y, v.Z, v.W }; return true;
				default: components = null; return false;
			}
		}

		#endregion

		#region PrivateMethods

		// One modification into another object's data: a vector or color only in the components the edit changed (a
		// quaternion whole: its components are no axes).
		private static bool ApplyOne(SerializedNode node, PropertyModification modification, SerializedNode before)
		{
			if (!modification.HasKey && IsPerComponent(modification.Tag)
				&& GenericValues.TryGet(before, modification.Path, out object old, out ValueTag oldTag, out _) && oldTag == modification.Tag
				&& GenericValues.TryGet(node, modification.Path, out object theirs, out ValueTag theirTag, out ValueTag elementTag) && theirTag == modification.Tag
				&& TryComponents(old, out double[] oldComponents) && TryComponents(modification.Value, out double[] newComponents) && TryComponents(theirs, out double[] otherComponents)
				&& oldComponents.Length == newComponents.Length && otherComponents.Length == newComponents.Length)
			{
				for (int i = 0; i < newComponents.Length; i++)
				{
					if (!oldComponents[i].Equals(newComponents[i]))
					{
						otherComponents[i] = newComponents[i];
					}
				}

				return GenericValues.TrySet(node, modification.Path, FromComponents(modification.Value, otherComponents), modification.Tag, elementTag);
			}

			return GenericValues.Apply(node, modification);
		}

		private static bool IsPerComponent(ValueTag tag)
		{
			return tag == ValueTag.Vector2 || tag == ValueTag.Vector3 || tag == ValueTag.Vector4 || tag == ValueTag.Color4
				|| tag == ValueTag.Vector2i || tag == ValueTag.Vector3i || tag == ValueTag.Vector4i;
		}

		private static object FromComponents(object like, double[] c)
		{
			switch (like)
			{
				case Vector2 _: return new Vector2((float)c[0], (float)c[1]);
				case Vector3 _: return new Vector3((float)c[0], (float)c[1], (float)c[2]);
				case Vector4 _: return new Vector4((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
				case Color4 _: return new Color4((float)c[0], (float)c[1], (float)c[2], (float)c[3]);
				case Vector2i _: return new Vector2i((int)c[0], (int)c[1]);
				case Vector3i _: return new Vector3i((int)c[0], (int)c[1], (int)c[2]);
				case Vector4i _: return new Vector4i((int)c[0], (int)c[1], (int)c[2], (int)c[3]);
				default: return like;
			}
		}

		#endregion
	}
}
