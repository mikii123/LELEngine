using System;
using System.Collections.Generic;
using System.Text.Json;

namespace LELEngine.Serialization
{
	/// <summary>
	///     Reading what was written while "static" was a field of <see cref="MeshRenderer" /> (IsStatic, until 2026-11-20):
	///     the flag is the GameObject's now (<see cref="GameObject.isStatic" />), so an object whose renderer stored it true
	///     is static, and an instance's override of the renderer's field is an override of its object's flag. Nothing
	///     writes the old field: a file saved again holds the object's "static" only.
	/// </summary>
	internal static class LegacyStatic
	{
		#region PublicFields

		/// <summary>The renderer's field of old.</summary>
		public const string Field = "IsStatic";

		#endregion

		#region PublicMethods

		/// <summary>A stored type name is the mesh renderer's (<see cref="TypeRegistry.GetName" />: "MeshRenderer, LELEngine").</summary>
		public static bool IsRenderer(string typeName)
		{
			return typeName != null && typeName.StartsWith("MeshRenderer,", StringComparison.Ordinal);
		}

		/// <summary>A component's JSON object is a mesh renderer that stored the flag true.</summary>
		public static bool Read(JsonElement component, string typeName)
		{
			return IsRenderer(typeName) && component.TryGetProperty(Field, out JsonElement value) && value.ValueKind == JsonValueKind.True;
		}

		/// <summary>Takes the old field out of a mesh renderer's generic data; true when it was stored true.</summary>
		public static bool Take(SerializedNode renderer)
		{
			if (renderer == null || !IsRenderer(renderer.TypeName))
			{
				return false;
			}

			for (int i = 0; i < renderer.Properties.Count; i++)
			{
				SerializedProperty property = renderer.Properties[i];
				if (property.Name == Field)
				{
					renderer.Properties.RemoveAt(i);
					return property.Value is bool flag && flag;
				}
			}

			return false;
		}

		/// <summary>
		///     A read instance record: a mesh renderer added to one of the instance's objects that stored the flag true makes
		///     that object static (an override of its flag, unless the record has one).
		/// </summary>
		public static void Normalize(PrefabInstanceRecord record)
		{
			foreach (DocumentComponent added in record.AddedComponents)
			{
				if (!Take(added.Data))
				{
					continue;
				}

				ulong owner = added.GameObject;
				if (!record.Modifications.Exists(m => m.Target == owner && m.Path == PropertyModification.StaticProperty))
				{
					record.Modifications.Add(new PropertyModification { Target = owner, Path = PropertyModification.StaticProperty, Tag = ValueTag.Bool, Value = true });
				}
			}
		}

		/// <summary>
		///     An instance's overrides by target (<see cref="PrefabResolver.GroupByTarget" />): an override of a mesh renderer's
		///     old field becomes one of its object's flag (unless the object has one already) and leaves the renderer's list.
		/// </summary>
		public static void MoveModifications(ResolvedPrefab source, Dictionary<ulong, List<PropertyModification>> modifications)
		{
			if (source == null || modifications == null || modifications.Count == 0)
			{
				return;
			}

			List<(ulong Owner, PropertyModification Change)> moved = null;
			foreach (PrefabObject obj in source.Objects)
			{
				foreach (PrefabComponent component in obj.Components)
				{
					if (!IsRenderer(component.TypeName) || !modifications.TryGetValue(component.Id, out List<PropertyModification> changes))
					{
						continue;
					}

					for (int i = changes.Count - 1; i >= 0; i--)
					{
						if (changes[i].Path == Field && !changes[i].HasKey)
						{
							(moved ??= new List<(ulong, PropertyModification)>()).Add((obj.Id, changes[i]));
							changes.RemoveAt(i);
						}
					}
				}
			}

			if (moved == null)
			{
				return;
			}

			foreach ((ulong owner, PropertyModification change) in moved)
			{
				if (!modifications.TryGetValue(owner, out List<PropertyModification> header))
				{
					header = new List<PropertyModification>();
					modifications[owner] = header;
				}

				if (!header.Exists(m => m.Path == PropertyModification.StaticProperty))
				{
					header.Add(new PropertyModification { Target = owner, Path = PropertyModification.StaticProperty, Tag = ValueTag.Bool, Value = change.Value is bool flag && flag });
				}
			}
		}

		#endregion
	}
}
