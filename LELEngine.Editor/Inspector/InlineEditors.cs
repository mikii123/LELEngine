using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     What an [InlineEditor] reference unfolds to (Odin's InlineEditor): a ScriptableObject's fields (the asset
	///     changes), a scene component's fields or a GameObject's components (scene edits with their undo and prefab
	///     override marks), or a prefab's root components. A prefab is edited on a live copy of its contents
	///     (<see cref="PrefabPreview" />), written to the file when an edit ends as one undo step, so every instance follows.
	/// </summary>
	internal static class InlineEditors
	{
		#region PrivateFields

		private static readonly Dictionary<string, PrefabPreview> previews = new Dictionary<string, PrefabPreview>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		/// <summary>The value has something to unfold.</summary>
		public static bool CanInline(object value)
		{
			return value switch
			{
				ScriptableObject asset => !asset.IsDestroyed,
				Behaviour component => !component.IsDestroyed && !(component is MissingComponent),
				GameObject go => !go.IsDestroyed,
				Prefab _ => true,
				_ => false
			};
		}

		/// <summary>Tooltip of the foldout arrow.</summary>
		public static string Hint(object value)
		{
			return value switch
			{
				ScriptableObject _ => "Unfold to edit the asset here (the asset changes for everything using it)",
				Prefab _ => "Unfold to edit the prefab's root here (saved to the prefab: every instance follows)",
				_ => "Unfold to edit the object here"
			};
		}

		/// <summary>Draws the unfolded target; true when it changed (the target, not the field holding the reference).</summary>
		public static bool Draw(object value, InspectorContext context, int depth)
		{
			switch (value)
			{
				case ScriptableObject asset:
					return FieldDrawer.DrawScriptableObject(asset, context, depth);
				case Behaviour component:
					return DrawComponent(component, context, depth, null);
				case GameObject go:
					return DrawComponents(go, context, depth, null);
				case Prefab prefab:
					PrefabPreview preview = Preview(prefab);
					if (preview == null)
					{
						ImGui.TextColored(EditorStyle.Error, "The prefab cannot be read: " + prefab.AssetPath);
						return false;
					}

					return DrawComponents(preview.Root, context, depth, preview);
				default:
					return false;
			}
		}

		/// <summary>Drops the prefab copies (scripts reload, project closes): they hold instances of the game's classes.</summary>
		public static void Clear()
		{
			foreach (PrefabPreview preview in previews.Values)
			{
				preview.Scene.Unload();
			}

			previews.Clear();
		}

		#endregion

		#region PrivateMethods

		/// <summary>The object's components (not its transform), each under its own foldout.</summary>
		private static bool DrawComponents(GameObject go, InspectorContext context, int depth, PrefabPreview preview)
		{
			bool changed = false;
			bool any = false;
			foreach (Behaviour component in new List<Behaviour>(go.Components))
			{
				if (component is Transform || component is MissingComponent || component.IsDestroyed)
				{
					continue;
				}

				any = true;
				ImGui.PushID(component.Id.ToString());
				if (EditorGui.TreeNodeEx(SerializationUtility.NicifyName(component.GetType().Name) + "##inline", ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanAvailWidth))
				{
					changed |= DrawComponent(component, context, depth + 1, preview);
					ImGui.TreePop();
				}

				ImGui.PopID();
			}

			if (!any)
			{
				ImGui.TextDisabled("(no components besides the transform)");
			}

			return changed;
		}

		/// <summary>A component's fields: a scene component's edits are the scene's; a prefab copy's go to the prefab file.</summary>
		private static bool DrawComponent(Behaviour component, InspectorContext context, int depth, PrefabPreview preview)
		{
			EditorApplication editor = context.Editor;
			object previousTarget = context.UndoTarget;
			PrefabInstanceOverrides previousOverrides = context.Overrides;
			ulong previousOverrideTarget = context.OverrideTarget;
			string previousPath = context.Path;
			Scene previousScene = context.Scene;
			context.Path = "";
			if (preview != null)
			{
				context.UndoTarget = preview;
				context.Overrides = null;
				context.OverrideTarget = 0;
				// References picked inside the prefab are the prefab's objects.
				context.Scene = preview.Scene;
			}
			else
			{
				context.UndoTarget = component;
				PrefabInstance instance = component.prefabInstance;
				context.Overrides = instance != null && editor != null ? PrefabInspector.OverridesOf(instance, editor.SceneVersion) : null;
				context.OverrideTarget = instance != null ? component.prefabSourceId : 0;
			}

			bool changed;
			try
			{
				changed = FieldDrawer.DrawFields(component, context, depth);
				if (preview == null)
				{
					changed |= CustomInspectors.Draw(component, context);
				}
			}
			finally
			{
				context.UndoTarget = previousTarget;
				context.Overrides = previousOverrides;
				context.OverrideTarget = previousOverrideTarget;
				context.Path = previousPath;
				context.Scene = previousScene;
			}

			if (changed)
			{
				if (preview != null)
				{
					preview.Scene.Validate(component);
				}
				else if (editor != null)
				{
					editor.NotifyChanged(component.gameObject);
					component.gameObject?.scene?.Validate(component);
				}
			}

			return changed;
		}

		/// <summary>The live copy of a prefab's contents, loaded again when its file changed (undo, prefab mode, another edit).</summary>
		private static PrefabPreview Preview(Prefab prefab)
		{
			string absolute = AssetDatabase.ToAbsolute(prefab.AssetPath);
			if (!File.Exists(absolute))
			{
				return null;
			}

			DateTime stamp = File.GetLastWriteTimeUtc(absolute);
			if (previews.TryGetValue(prefab.AssetPath, out PrefabPreview preview) && preview.Stamp == stamp)
			{
				return preview;
			}

			preview?.Scene.Unload();
			previews.Remove(prefab.AssetPath);
			try
			{
				Scene scene = SceneSerializer.Load(absolute);
				List<GameObject> roots = scene.GetRootGameObjects();
				if (roots.Count == 0)
				{
					scene.Unload();
					return null;
				}

				preview = new PrefabPreview { AssetPath = prefab.AssetPath, Scene = scene, Root = roots[0], Stamp = stamp };
				previews[prefab.AssetPath] = preview;
				return preview;
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot show the prefab " + prefab.AssetPath + ": " + e.Message);
				return null;
			}
		}

		#endregion
	}

	/// <summary>
	///     A prefab's contents loaded as a scene of their own for an inline editor; the undo target of its edits
	///     (<see cref="EditTracker" />): when an edit ends the copy is written to the prefab file (<see cref="Save" />).
	/// </summary>
	internal sealed class PrefabPreview
	{
		public string AssetPath;
		public Scene Scene;
		public GameObject Root;
		public DateTime Stamp;

		public string Name => Path.GetFileNameWithoutExtension(AssetPath);

		/// <summary>The copy's state (to tell whether an edit changed anything).</summary>
		public byte[] Capture()
		{
			return SceneSerializer.SavePrefabToBytes(Root, DocumentFormat.Binary);
		}

		/// <summary>Writes the copy to the prefab file (the copy stays: it is what the file now holds).</summary>
		public void Save()
		{
			PrefabUtility.SavePrefabScene(Scene, AssetPath);
			Stamp = File.GetLastWriteTimeUtc(AssetDatabase.ToAbsolute(AssetPath));
		}
	}
}
