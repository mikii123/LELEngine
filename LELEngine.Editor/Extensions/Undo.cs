using System;

namespace LELEngine.Editor
{
	/// <summary>Undo for changes made by editor scripts (custom inspectors, editor windows, menu items).</summary>
	public static class Undo
	{
		#region PublicMethods

		/// <summary>
		///     Call before changing fields of <paramref name="component" />: its state is captured once, and the values it
		///     has when the edit ends become one undo step (a whole drag of an ImGui slider is one step: the step ends
		///     when no ImGui item is active any more). The scene is marked dirty and the component's OnValidate runs.
		/// </summary>
		/// <param name="name">Undo menu text (default: "Edit &lt;Type&gt;").</param>
		public static void RecordObject(Behaviour component, string name = null)
		{
			if (component != null)
			{
				EditorExtensions.Current.RecordObject(component, name);
			}
		}

		/// <summary>
		///     Runs a structural change of the scene (create or delete GameObjects, add or remove components, re-parent)
		///     as one undo step.
		/// </summary>
		public static void RecordSceneChange(string name, Action change)
		{
			if (change != null)
			{
				EditorExtensions.Current.RecordSceneChange(name ?? "Scene change", change);
			}
		}

		#endregion
	}

	/// <summary>The editor's selection (what the hierarchy highlights and the inspector shows).</summary>
	public static class Selection
	{
		#region PublicFields

		/// <summary>The selected GameObject of the scene (null when a project file or nothing is selected).</summary>
		public static GameObject activeGameObject
		{
			get => EditorApplication.Instance?.Selected;
			set => EditorExtensions.Current.Select(value);
		}

		/// <summary>The project file shown in the inspector ("Folder/File.ext", relative to Assets), or null.</summary>
		public static string activeAssetPath
		{
			get => EditorApplication.Instance?.SelectedAsset;
			set => EditorExtensions.Current.SelectAsset(value);
		}

		#endregion
	}
}
