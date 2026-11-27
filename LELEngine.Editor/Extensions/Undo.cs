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
		///     Call before changing fields of a ScriptableObject asset (Unity's Undo.RecordObject on an asset): its state is
		///     captured once, and the values it has when the edit ends become one undo step, as the inspector's edits of an
		///     asset are; its file is written then (assets are saved as their edits end). Call it again before each change
		///     (cheap while the same asset's edit goes on).
		/// </summary>
		/// <param name="name">Undo menu text (default: "Edit &lt;asset name&gt;").</param>
		public static void RecordObject(ScriptableObject asset, string name = null)
		{
			if (asset != null)
			{
				EditorExtensions.Current.RecordAsset(asset, name);
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

		/// <summary>
		///     The selected GameObject of the scene (null when a project file or nothing is selected); with several
		///     selected, the active one (what the inspector shows). Setting it selects that object alone.
		/// </summary>
		public static GameObject activeGameObject
		{
			get => EditorApplication.Instance?.Selected;
			set => EditorExtensions.Current.Select(value);
		}

		/// <summary>
		///     Every selected GameObject (Unity's Selection.gameObjects; empty when none is), <see cref="activeGameObject" />
		///     among them. Setting it selects these objects and no others; the last one becomes the active one.
		/// </summary>
		public static GameObject[] gameObjects
		{
			get
			{
				EditorApplication editor = EditorApplication.Instance;
				if (editor == null)
				{
					return System.Array.Empty<GameObject>();
				}

				var objects = new GameObject[editor.SelectedObjects.Count];
				for (int i = 0; i < objects.Length; i++)
				{
					objects[i] = editor.SelectedObjects[i];
				}

				return objects;
			}
			set => EditorApplication.Instance?.SelectObjects(value);
		}

		/// <summary>
		///     The selected GameObjects none of whose parents is selected, in the hierarchy's order: what a tool acting on the
		///     selection usually wants (a child selected with its parent is already part of it).
		/// </summary>
		public static GameObject[] topLevelGameObjects => EditorApplication.Instance?.SelectedRoots().ToArray() ?? System.Array.Empty<GameObject>();

		/// <summary>The project file shown in the inspector ("Folder/File.ext", relative to Assets), or null.</summary>
		public static string activeAssetPath
		{
			get => EditorApplication.Instance?.SelectedAsset;
			set => EditorExtensions.Current.SelectAsset(value);
		}

		/// <summary>
		///     Every selected project file ("Folder/File.ext"; empty when GameObjects or nothing are selected),
		///     <see cref="activeAssetPath" /> among them. Setting it selects these files; the last one becomes the active one.
		/// </summary>
		public static string[] assetPaths
		{
			get
			{
				EditorApplication editor = EditorApplication.Instance;
				if (editor == null)
				{
					return System.Array.Empty<string>();
				}

				var paths = new string[editor.SelectedAssets.Count];
				for (int i = 0; i < paths.Length; i++)
				{
					paths[i] = editor.SelectedAssets[i];
				}

				return paths;
			}
			set => EditorApplication.Instance?.SelectAssets(value);
		}

		#endregion
	}
}
