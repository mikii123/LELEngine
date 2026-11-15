namespace LELEngine.Editor
{
	/// <summary>
	///     What the editor makes of an object's <see cref="HideFlags" />: temporary objects (<see cref="HideFlags.DontSave" />,
	///     the object or a parent: never saved) are shown dimmed and tagged; read-only ones (<see cref="HideFlags.NotEditable" />,
	///     the object or a parent) are shown but never changed by the editor (inspector, hierarchy, scene view gizmos,
	///     delete / duplicate, automation).
	/// </summary>
	internal static class TempObjects
	{
		/// <summary>The tag after a temporary object's name in the hierarchy.</summary>
		public const string Tag = "temp";

		/// <summary>Not saved with the scene: the object or one of its parents is <see cref="HideFlags.DontSave" />.</summary>
		public static bool IsTemporary(GameObject go) => HasFlag(go, HideFlags.DontSave);

		/// <summary>Not changed by the editor: the object or one of its parents is <see cref="HideFlags.NotEditable" />.</summary>
		public static bool IsReadOnly(GameObject go) => HasFlag(go, HideFlags.NotEditable);

		/// <summary>The object has children the hierarchy lists (not hidden, not destroyed).</summary>
		public static bool HasVisibleChildren(GameObject go)
		{
			if (go == null)
			{
				return false;
			}

			foreach (Transform child in go.transform.Children)
			{
				GameObject other = child.gameObject;
				if (other != null && !other.dead && (other.hideFlags & HideFlags.HideInHierarchy) == 0)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>One line on what such an object is (hierarchy tooltip, inspector note); null for an ordinary object.</summary>
		public static string Describe(GameObject go)
		{
			bool temporary = IsTemporary(go);
			bool readOnly = IsReadOnly(go);
			if (temporary && readOnly)
			{
				return "Temporary: made and kept by a script, not saved with the scene, not editable.";
			}

			if (temporary)
			{
				return "Temporary: made by a script, not saved with the scene.";
			}

			return readOnly ? "Not editable: a script keeps it up to date." : null;
		}

		/// <summary>Throws for a read-only object (automation's edits).</summary>
		public static void RequireEditable(GameObject go)
		{
			if (IsReadOnly(go))
			{
				throw new System.InvalidOperationException($"'{go.Name}' is not editable (HideFlags.NotEditable): a script keeps it.");
			}
		}

		private static bool HasFlag(GameObject go, HideFlags flag)
		{
			for (Transform t = go?.transform; t != null; t = t.parent)
			{
				if ((t.gameObject.hideFlags & flag) != 0)
				{
					return true;
				}
			}

			return false;
		}
	}
}
