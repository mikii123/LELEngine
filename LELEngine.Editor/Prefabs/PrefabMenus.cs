using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Serialization;

namespace LELEngine.Editor
{
	/// <summary>
	///     Apply / revert actions on prefab instances as undoable editor operations, and the menu items offering them
	///     (hierarchy, inspector, overrides list). Applying writes prefab files (<see cref="EditorApplication.RecordPrefabFiles" />:
	///     the scene reloads with every instance updated); reverting changes the scene only. The actions run after the
	///     frame's panels are drawn (<see cref="EditorApplication.Defer" />).
	/// </summary>
	internal static class PrefabMenus
	{
		#region PublicMethods

		/// <summary>The menu text of applying to a prefab of the chain: an override in a prefab nested further out, or the prefab itself.</summary>
		public static string ApplyLabel(PrefabApplyTarget target)
		{
			return target.Defines ? "Apply to Prefab '" + target.Name + "'" : "Apply as Override in Prefab '" + target.Name + "'";
		}

		/// <summary>One menu item per prefab the change can go to; returns the chosen one (null: none).</summary>
		public static PrefabApplyTarget ApplyItems(List<PrefabApplyTarget> targets, bool enabled = true)
		{
			PrefabApplyTarget chosen = null;
			foreach (PrefabApplyTarget target in targets)
			{
				if (ImGui.MenuItem(ApplyLabel(target) + "##" + target.PrefabPath, "", false, enabled))
				{
					chosen = target;
				}
			}

			return chosen;
		}

		/// <summary>Applies property overrides (of one object or component) to a prefab of its chain.</summary>
		public static void ApplyProperties(EditorApplication editor, PrefabInstance instance, List<PropertyModification> modifications, PrefabApplyTarget target)
		{
			if (modifications.Count > 0)
			{
				Files(editor, "Apply " + Describe(modifications) + " to " + target.Name, target.PrefabPath, () => PrefabUtility.ApplyProperties(instance, modifications, target.PrefabPath));
			}
		}

		public static void RevertProperties(EditorApplication editor, PrefabInstance instance, List<PropertyModification> modifications)
		{
			if (modifications.Count == 0)
			{
				return;
			}

			Structural(editor, "Revert " + Describe(modifications), () =>
			{
				foreach (PropertyModification modification in modifications)
				{
					PrefabUtility.RevertProperty(instance, modification);
				}
			});
		}

		public static void ApplyAll(EditorApplication editor, PrefabInstance instance)
		{
			Files(editor, "Apply all overrides to " + instance.Prefab?.name, instance.PrefabPath, () => PrefabUtility.ApplyPrefabInstance(instance));
		}

		public static void RevertAll(EditorApplication editor, PrefabInstance instance)
		{
			Structural(editor, "Revert all overrides of " + instance.Root?.Name, () => PrefabUtility.RevertPrefabInstance(instance));
		}

		public static void ApplyAddedComponent(EditorApplication editor, Behaviour component, PrefabApplyTarget target)
		{
			Files(editor, "Apply added " + component.GetType().Name + " to " + target.Name, target.PrefabPath, () => PrefabUtility.ApplyAddedComponent(component, target));
		}

		public static void RevertAddedComponent(EditorApplication editor, Behaviour component)
		{
			Structural(editor, "Remove added " + component.GetType().Name, () => PrefabUtility.RevertAddedComponent(component));
		}

		public static void ApplyRemovedComponent(EditorApplication editor, PrefabInstance instance, ulong sourceId, PrefabApplyTarget target)
		{
			Files(editor, "Apply removal of " + PrefabUtility.DescribeSource(instance, sourceId) + " to " + target.Name, target.PrefabPath, () => PrefabUtility.ApplyRemovedComponent(instance, sourceId, target));
		}

		public static void RevertRemovedComponent(EditorApplication editor, PrefabInstance instance, ulong sourceId)
		{
			Structural(editor, "Restore " + PrefabUtility.DescribeSource(instance, sourceId), () => PrefabUtility.RevertRemovedComponent(instance, sourceId));
		}

		public static void ApplyAddedGameObject(EditorApplication editor, GameObject go, PrefabApplyTarget target)
		{
			Files(editor, "Apply added " + go.Name + " to " + target.Name, target.PrefabPath, () => PrefabUtility.ApplyAddedGameObject(go, target));
		}

		public static void RevertAddedGameObject(EditorApplication editor, GameObject go)
		{
			Structural(editor, "Remove added " + go.Name, () => PrefabUtility.RevertAddedGameObject(go));
		}

		public static void ApplyRemovedGameObject(EditorApplication editor, PrefabInstance instance, ulong sourceId, PrefabApplyTarget target)
		{
			Files(editor, "Apply removal of " + PrefabUtility.DescribeSource(instance, sourceId) + " to " + target.Name, target.PrefabPath, () => PrefabUtility.ApplyRemovedGameObject(instance, sourceId, target));
		}

		public static void RevertRemovedGameObject(EditorApplication editor, PrefabInstance instance, ulong sourceId)
		{
			Structural(editor, "Restore " + PrefabUtility.DescribeSource(instance, sourceId), () => PrefabUtility.RevertRemovedGameObject(instance, sourceId));
		}

		/// <summary>
		///     The prefab menu of a hierarchy row or the inspector's header: open / select, unpack, apply and revert of the
		///     instance's overrides, an added object's apply and revert.
		/// </summary>
		public static void DrawObjectMenu(EditorApplication editor, GameObject go)
		{
			PrefabInstance instance = go.prefabInstance;
			bool isRoot = PrefabUtility.IsPrefabInstanceRoot(go);
			if (instance != null || PrefabUtility.IsAddedGameObject(go))
			{
				ImGui.Separator();
			}

			if (instance != null)
			{
				Prefab nested = PrefabUtility.GetNestedPrefab(go);
				Prefab nearest = nested ?? (isRoot ? instance.Prefab : PrefabUtility.GetNearestPrefab(go));
				if (ImGui.MenuItem("Open Prefab" + (nearest != null ? " '" + nearest.name + "'" : ""), "", false, nearest != null && !editor.IsPlaying))
				{
					string path = nearest.AssetPath;
					editor.Defer(() => editor.OpenPrefab(path));
				}

				if (ImGui.MenuItem("Select Prefab Asset", "", false, nearest != null))
				{
					editor.SelectAsset(nearest.AssetPath);
					editor.Ping(nearest.AssetPath);
				}

				if (!isRoot && ImGui.MenuItem("Select Prefab Instance Root", "", false, instance.Root != null))
				{
					editor.SelectObject(instance.Root);
				}

				if (isRoot)
				{
					ImGui.Separator();
					if (ImGui.MenuItem("Unpack Prefab")) editor.Defer(() => editor.UnpackPrefab(go, PrefabUnpackMode.OutermostRoot));
					if (ImGui.MenuItem("Unpack Prefab Completely")) editor.Defer(() => editor.UnpackPrefab(go, PrefabUnpackMode.Completely));
					if (!instance.IsMissing)
					{
						PrefabInstanceOverrides overrides = PrefabUtility.GetOverrides(instance);
						ImGui.Separator();
						if (ImGui.MenuItem("Apply All Overrides to '" + instance.Prefab?.name + "'", "", false, !overrides.IsEmpty)) ApplyAll(editor, instance);
						if (ImGui.MenuItem("Revert All Overrides", "", false, !overrides.IsEmpty)) RevertAll(editor, instance);
					}
				}
			}

			if (PrefabUtility.IsAddedGameObject(go))
			{
				GameObject host = go.transform.parent.gameObject;
				List<PrefabApplyTarget> targets = PrefabUtility.GetApplyTargets(host.prefabInstance, host.prefabSourceId, false);
				ImGui.TextDisabled("Added to the instance of '" + host.prefabInstance.Prefab?.name + "'");
				PrefabApplyTarget chosen = ApplyItems(targets);
				if (chosen != null)
				{
					ApplyAddedGameObject(editor, go, chosen);
				}

				if (ImGui.MenuItem("Revert (Remove Added GameObject)"))
				{
					RevertAddedGameObject(editor, go);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void Files(EditorApplication editor, string name, string prefabPath, Action operation)
		{
			editor.Defer(() => editor.RecordPrefabFiles(name, new[] { prefabPath }, operation));
		}

		private static void Structural(EditorApplication editor, string name, Action operation)
		{
			editor.Defer(() => editor.RecordStructural(name, operation));
		}

		private static string Describe(List<PropertyModification> modifications)
		{
			return modifications.Count == 1 ? SerializationUtility.NicifyName(PropertyPath.RootField(modifications[0].Path)) : modifications.Count + " overrides";
		}

		#endregion
	}
}
