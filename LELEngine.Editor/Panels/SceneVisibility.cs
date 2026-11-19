using System;
using System.Collections.Generic;

namespace LELEngine.Editor
{
	/// <summary>
	///     Objects hidden in the scene view (Unity's scene visibility, the eye in the hierarchy): the scene view draws
	///     nothing of them (meshes, trails, effects, clouds, their shadows and GI; <see cref="Rendering.Renderer.HiddenObjects" />)
	///     and its clicks pick through them; the game view, play mode's game and builds draw them as always. It is the
	///     user's view of the scene, not the scene's data: kept per project and scene (or prefab) by object id in the
	///     user's hierarchy.json (<see cref="HierarchyExpansion" />), never in the scene file, no undo, the scene stays clean.
	/// </summary>
	internal sealed class SceneVisibility
	{
		#region PrivateFields

		private readonly EditorApplication editor;

		// The scene (or prefab stage) the ids belong to, and the key they are saved under.
		private string root;
		private string path;
		private string key;
		private HashSet<ulong> hidden = new HashSet<ulong>();

		// The scene shown while it had no file (its hidden objects go with it when it is saved); weak, as scenes unload.
		private WeakReference<Scene> unsavedSceneRef;
		private Scene unsavedScene
		{
			get => unsavedSceneRef != null && unsavedSceneRef.TryGetTarget(out Scene scene) ? scene : null;
			set => unsavedSceneRef = value == null ? null : new WeakReference<Scene>(value);
		}

		#endregion

		#region Constructors

		public SceneVisibility(EditorApplication editor)
		{
			this.editor = editor;
			Hides = go => hidden.Count != 0 && go.scene == editor.Scene && hidden.Contains(go.Id);
		}

		#endregion

		#region PublicFields

		/// <summary>The object is hidden (the ids of the scene last synchronized).</summary>
		private Func<GameObject, bool> Hides { get; }

		/// <summary>The scene view renderer's filter (<see cref="Rendering.Renderer.HiddenObjects" />): null while nothing is hidden.</summary>
		public Func<GameObject, bool> Filter
		{
			get
			{
				Sync();
				return hidden.Count == 0 ? null : Hides;
			}
		}

		/// <summary>Some object of the shown scene is hidden.</summary>
		public bool AnyHidden
		{
			get
			{
				Sync();
				return hidden.Count != 0;
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>The scene view hides the object itself.</summary>
		public bool IsHidden(GameObject go)
		{
			Sync();
			return go != null && Hides(go);
		}

		/// <summary>Some object under <paramref name="go" /> (not the object itself) is hidden.</summary>
		public bool HasHiddenChildren(GameObject go)
		{
			Sync();
			if (hidden.Count == 0 || go == null)
			{
				return false;
			}

			foreach (Transform child in go.transform.Children)
			{
				if (Hides(child.gameObject) || HasHiddenChildren(child.gameObject))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Hides or shows the object, with everything under it unless <paramref name="alone" /> (Unity: Alt + click).</summary>
		public void SetHidden(GameObject go, bool hide, bool alone)
		{
			Sync();
			if (go == null || go.dead || go.scene != editor.Scene)
			{
				return;
			}

			Set(go, hide, alone);
			Changed();
		}

		/// <summary>Shows every object of the shown scene.</summary>
		public void ShowAll()
		{
			Sync();
			if (hidden.Count != 0)
			{
				hidden.Clear();
				Changed();
			}
		}

		#endregion

		#region PrivateMethods

		private void Set(GameObject go, bool hide, bool alone)
		{
			if (hide)
			{
				hidden.Add(go.Id);
			}
			else
			{
				hidden.Remove(go.Id);
			}

			if (alone)
			{
				return;
			}

			foreach (Transform child in go.transform.Children)
			{
				Set(child.gameObject, hide, false);
			}
		}

		private void Changed()
		{
			HierarchyExpansion.SaveHidden(key, hidden);
			editor.NotifySceneChanged();
		}

		/// <summary>Follows the shown scene or prefab: its hidden objects as saved (an unsaved scene starts with none).</summary>
		private void Sync()
		{
			Scene scene = editor.Scene;
			string projectRoot = editor.Project?.RootPath;
			string assetPath = editor.Stage?.AssetPath ?? scene?.Path;
			// Scenes without a file are told apart by the scene itself (a new scene after another new one starts clean).
			if (projectRoot == root && assetPath == path && (assetPath != null || scene == unsavedScene))
			{
				return;
			}

			bool firstSave = key == null && path == null && root == projectRoot && hidden.Count != 0 && assetPath != null && editor.Stage == null && scene == unsavedScene;
			unsavedScene = assetPath == null ? scene : null;
			root = projectRoot;
			path = assetPath;
			key = HierarchyExpansion.Key(projectRoot, assetPath);
			if (firstSave)
			{
				// A new scene saved for the first time keeps what was hidden in it.
				HierarchyExpansion.SaveHidden(key, hidden);
				return;
			}

			hidden = HierarchyExpansion.LoadHidden(key);
		}

		#endregion
	}
}
