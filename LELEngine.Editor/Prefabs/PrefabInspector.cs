using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector's prefab parts, as in Unity: the prefab bar of an instance (Open, Select, the Overrides list
	///     with Revert / Apply per change and for all), overridden values in bold with a blue bar in the margin and
	///     their Apply / Revert menu (right click), components added to an instance with a "+", components it removed
	///     as greyed headers. Applying offers every prefab the change can go to: the instance's prefab, and the nested
	///     prefabs (or variant bases) the object comes from.
	/// </summary>
	internal static class PrefabInspector
	{
		#region PrivateFields

		private static PrefabInstance cachedInstance;
		private static int cachedVersion = -1;
		private static PrefabInstanceOverrides cachedOverrides;

		#endregion

		#region PublicMethods

		/// <summary>The overrides of an instance, worked out once per change of the scene.</summary>
		public static PrefabInstanceOverrides OverridesOf(PrefabInstance instance, int sceneVersion)
		{
			if (instance == null || instance.IsMissing)
			{
				return null;
			}

			if (instance != cachedInstance || sceneVersion != cachedVersion || cachedOverrides == null)
			{
				cachedInstance = instance;
				cachedVersion = sceneVersion;
				cachedOverrides = PrefabUtility.GetOverrides(instance);
			}

			return cachedOverrides;
		}

		/// <summary>Drops the cached overrides (the scene or the scripts go away).</summary>
		public static void Forget()
		{
			cachedInstance = null;
			cachedOverrides = null;
		}

		/// <summary>The override bar in the inspector's left margin, from <paramref name="top" /> to <paramref name="bottom" />.</summary>
		public static void Bar(float top, float bottom)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 windowMin = ImGui.GetWindowPos();
			NVector2 windowMax = windowMin + ImGui.GetWindowSize();
			draw.PushClipRect(windowMin, windowMax, false);
			float x = windowMin.X + 2f;
			draw.AddRectFilled(new NVector2(x, top), new NVector2(x + 3f, bottom), ImGui.GetColorU32(EditorStyle.Prefab));
			draw.PopClipRect();
		}

		/// <summary>On an overridden field's label (the last item): the bar, and its Apply / Revert menu on right click.</summary>
		public static void MarkOverride(InspectorContext context)
		{
			Bar(ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().Y);
			if (ImGui.IsItemHovered() && !DragAndDrop.IsDragging)
			{
				ImGui.SetTooltip("Prefab override (right click: apply or revert)");
			}

			if (context.Overrides != null && ImGui.BeginPopupContextItem("##prefab_override"))
			{
				DrawPropertyMenu(context, context.OverrideTarget, context.Path);
				ImGui.EndPopup();
			}
		}

		/// <summary>The Apply / Revert items of the overrides of a property (and of everything inside it) of one object or component.</summary>
		public static void DrawPropertyMenu(InspectorContext context, ulong target, string path)
		{
			PrefabInstanceOverrides overrides = context.Overrides;
			List<PropertyModification> modifications = Matching(overrides, target, path);
			if (modifications.Count == 0)
			{
				ImGui.TextDisabled("No override");
				return;
			}

			PrefabInstance instance = overrides.Instance;
			bool isComponent = instance.Source != null && instance.Source.FindObject(target) == null;
			ImGui.TextDisabled(modifications.Count == 1 ? Describe(modifications[0]) : modifications.Count + " overrides");
			ImGui.Separator();
			PrefabApplyTarget chosen = PrefabMenus.ApplyItems(PrefabUtility.GetApplyTargets(instance, target, isComponent));
			if (chosen != null)
			{
				PrefabMenus.ApplyProperties(context.Editor, instance, modifications, chosen);
			}

			if (ImGui.MenuItem("Revert"))
			{
				PrefabMenus.RevertProperties(context.Editor, instance, modifications);
			}
		}

		/// <summary>
		///     The prefab bar under the GameObject's header: what the object is part of, Open and Select of its prefab, and
		///     on an instance root the Overrides list.
		/// </summary>
		public static void DrawPrefabBar(EditorApplication editor, GameObject go, InspectorContext context)
		{
			PrefabInstance instance = go.prefabInstance;
			if (instance == null)
			{
				if (PrefabUtility.IsAddedGameObject(go))
				{
					GameObject host = go.transform.parent.gameObject;
					DrawBadgeText(IconKind.AddedBadge, EditorStyle.Added, "Added to the prefab instance of '" + host.prefabInstance.Prefab?.name + "'");
					if (ImGui.BeginPopupContextItem("##added_object"))
					{
						PrefabMenus.DrawObjectMenu(editor, go);
						ImGui.EndPopup();
					}

					ImGui.Separator();
				}

				return;
			}

			float height = ImGui.GetFrameHeight();
			float icon = ImGui.GetTextLineHeight();
			if (instance.IsMissing)
			{
				DrawIconText(IconKind.Prefab, EditorStyle.PrefabMissing, "Missing prefab: " + instance.PrefabPath);
				ImGui.SameLine();
				if (PrefabUtility.IsPrefabInstanceRoot(go) && ImGui.SmallButton("Unpack"))
				{
					editor.Defer(() => editor.UnpackPrefab(go, PrefabUnpackMode.Completely));
				}

				ImGui.Separator();
				return;
			}

			bool isRoot = PrefabUtility.IsPrefabInstanceRoot(go);
			Prefab nested = PrefabUtility.GetNestedPrefab(go);
			Prefab shown = isRoot ? instance.Prefab : nested;
			if (shown == null)
			{
				// An object inside an instance: a way to its root.
				ImGui.AlignTextToFramePadding();
				ImGui.TextDisabled("Part of the prefab instance");
				ImGui.SameLine();
				ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Prefab);
				if (ImGui.SmallButton((instance.Root?.Name ?? "?") + "##root"))
				{
					editor.SelectObject(instance.Root);
				}

				ImGui.PopStyleColor();
				ImGui.Separator();
				return;
			}

			string label = shown.name + (shown.BasePrefab != null ? "  (variant of " + shown.BasePrefab.name + ")" : "") + (!isRoot ? "  (nested)" : "");
			float lineRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
			DrawIconText(shown.IsVariant ? IconKind.PrefabVariant : IconKind.Prefab, EditorStyle.Prefab, label);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(shown.AssetPath);
			}

			// Buttons at the end of the line: Open, Select (and Overrides on the instance's root).
			PrefabInstanceOverrides overrides = isRoot ? context.Overrides : null;
			string overridesLabel = overrides == null ? null : overrides.IsEmpty ? "No Overrides" : "Overrides";
			float spacing = ImGui.GetStyle().ItemSpacing.X;
			float width = ImGui.CalcTextSize("Open").X + ImGui.CalcTextSize("Select").X + ImGui.GetStyle().FramePadding.X * 4f + spacing
				+ (overridesLabel != null ? ImGui.CalcTextSize(overridesLabel).X + ImGui.GetStyle().FramePadding.X * 2f + spacing + icon : 0f);
			ImGui.SameLine();
			ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), lineRight - width));
			ImGui.BeginDisabled(editor.IsPlaying);
			if (ImGui.SmallButton("Open"))
			{
				string path = shown.AssetPath;
				editor.Defer(() => editor.OpenPrefab(path));
			}

			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip("Edit the prefab '" + shown.name + "' (prefab mode)");
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Select"))
			{
				editor.SelectAsset(shown.AssetPath);
				editor.Ping(shown.AssetPath);
			}

			if (overridesLabel != null)
			{
				ImGui.SameLine();
				if (!overrides.IsEmpty)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Prefab);
				}

				if (ImGui.SmallButton(overridesLabel + "  ##overrides"))
				{
					ImGui.OpenPopup("##prefab_overrides");
				}

				TestAnchors.RecordItem("button:PrefabOverrides");

				NVector2 max = ImGui.GetItemRectMax();
				EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(max.X - icon * 0.95f, max.Y - (ImGui.GetItemRectSize().Y + icon * 0.7f) * 0.5f), icon * 0.7f, IconKind.Dropdown, ImGui.GetColorU32(ImGuiCol.Text));
				if (!overrides.IsEmpty)
				{
					ImGui.PopStyleColor();
				}

				// Dropped down from the button's right edge (as Unity's Overrides list).
				ImGui.SetNextWindowPos(new NVector2(max.X, max.Y + 2f), ImGuiCond.Appearing, new NVector2(1f, 0f));
				ImGui.SetNextWindowSizeConstraints(new NVector2(380f, 0f), new NVector2(700f, 600f));
				if (ImGui.BeginPopup("##prefab_overrides"))
				{
					DrawOverridesList(editor, instance, overrides, context);
					ImGui.EndPopup();
				}
			}

			ImGui.Separator();
		}

		/// <summary>Spaces before an added component's title: room for its "+".</summary>
		public static string AddedTitlePadding()
		{
			return new string(' ', (int)Math.Ceiling((ImGui.GetTextLineHeight() * 0.75f + 4f) / Math.Max(1f, ImGui.CalcTextSize(" ").X)));
		}

		/// <summary>The header decorations of a component: "+" for one added to the instance, bold with the bar when it has overrides.</summary>
		public static void DecorateComponentHeader(EditorApplication editor, Behaviour component, InspectorContext context)
		{
			if (PrefabUtility.IsAddedComponent(component))
			{
				// The "+" sits after the arrow, in the room the title leaves for it (see AddedTitlePadding).
				NVector2 min = ImGui.GetItemRectMin();
				float size = ImGui.GetTextLineHeight() * 0.75f;
				EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(min.X + ImGui.GetTreeNodeToLabelSpacing(), min.Y + (ImGui.GetItemRectSize().Y - size) * 0.5f), size, IconKind.AddedBadge, ImGui.GetColorU32(EditorStyle.Added));
				Bar(min.Y, ImGui.GetItemRectMax().Y);
			}
			else if (context.Overrides != null && component.prefabInstance != null && HasOverrides(context.Overrides, component.prefabSourceId))
			{
				Bar(ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().Y);
			}
		}

		/// <summary>Items of a component header's context menu: apply / revert of the component's overrides, or of the component added.</summary>
		public static void DrawComponentMenu(EditorApplication editor, Behaviour component, InspectorContext context)
		{
			GameObject go = component.gameObject;
			if (PrefabUtility.IsAddedComponent(component))
			{
				ImGui.Separator();
				ImGui.TextDisabled("Added to the prefab instance");
				PrefabApplyTarget chosen = PrefabMenus.ApplyItems(PrefabUtility.GetApplyTargets(go.prefabInstance, go.prefabSourceId, false));
				if (chosen != null)
				{
					PrefabMenus.ApplyAddedComponent(editor, component, chosen);
				}

				if (ImGui.MenuItem("Revert (Remove Added Component)"))
				{
					PrefabMenus.RevertAddedComponent(editor, component);
				}

				return;
			}

			if (context.Overrides == null || component.prefabInstance == null || !HasOverrides(context.Overrides, component.prefabSourceId))
			{
				return;
			}

			var modifications = new List<PropertyModification>(context.Overrides.Of(component.prefabSourceId));
			ImGui.Separator();
			ImGui.TextDisabled(modifications.Count + (modifications.Count == 1 ? " override" : " overrides"));
			PrefabApplyTarget target = PrefabMenus.ApplyItems(PrefabUtility.GetApplyTargets(component.prefabInstance, component.prefabSourceId, true));
			if (target != null)
			{
				PrefabMenus.ApplyProperties(editor, component.prefabInstance, modifications, target);
			}

			if (ImGui.MenuItem("Revert Component Overrides"))
			{
				PrefabMenus.RevertProperties(editor, component.prefabInstance, modifications);
			}
		}

		/// <summary>The components of the prefab the instance removed from <paramref name="go" />: greyed headers with Revert / Apply.</summary>
		public static void DrawRemovedComponents(EditorApplication editor, GameObject go, InspectorContext context)
		{
			PrefabInstanceOverrides overrides = context.Overrides;
			PrefabInstance instance = go.prefabInstance;
			if (overrides == null || instance?.Source == null || overrides.RemovedComponents.Count == 0)
			{
				return;
			}

			foreach (ulong removed in overrides.RemovedComponents)
			{
				PrefabComponent stored = instance.Source.FindComponent(removed);
				if (stored?.Owner == null || stored.Owner.Id != go.prefabSourceId)
				{
					continue;
				}

				ImGui.PushID("removed" + removed);
				string name = GenericValues.Describe(stored.Data, ValueTag.Object);
				NVector2 start = ImGui.GetCursorScreenPos();
				ImGui.PushStyleColor(ImGuiCol.Header, new NVector4(0.2f, 0.2f, 0.21f, 1f));
				ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Dim);
				ImGui.CollapsingHeader("      " + SerializationUtility.NicifyName(name) + "  (Removed)##removed", ImGuiTreeNodeFlags.Leaf);
				ImGui.PopStyleColor(2);
				float size = ImGui.GetTextLineHeight() * 0.7f;
				EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(start.X + 6f, start.Y + (ImGui.GetItemRectSize().Y - size) * 0.5f), size, IconKind.RemovedBadge, ImGui.GetColorU32(EditorStyle.PrefabMissing));
				Bar(ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().Y);
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("The prefab has this component; this instance removed it (right click: revert or apply)");
				}

				if (ImGui.BeginPopupContextItem("##removed_menu"))
				{
					PrefabApplyTarget chosen = PrefabMenus.ApplyItems(PrefabUtility.GetApplyTargets(instance, removed, true));
					if (chosen != null)
					{
						PrefabMenus.ApplyRemovedComponent(editor, instance, removed, chosen);
					}

					if (ImGui.MenuItem("Revert (Restore Component)"))
					{
						PrefabMenus.RevertRemovedComponent(editor, instance, removed);
					}

					ImGui.EndPopup();
				}

				ImGui.PopID();
			}
		}

		/// <summary>The bar and menu of a GameObject header property (name, active, layer) when the instance overrides it; the last item is its widget.</summary>
		public static void MarkHeaderProperty(InspectorContext context, GameObject go, string property)
		{
			if (context.Overrides == null || go.prefabInstance == null || !context.Overrides.IsOverridden(go.prefabSourceId, property))
			{
				return;
			}

			Bar(ImGui.GetItemRectMin().Y, ImGui.GetItemRectMax().Y);
			if (ImGui.BeginPopupContextItem("##header_override_" + property))
			{
				DrawPropertyMenu(context, go.prefabSourceId, property);
				ImGui.EndPopup();
			}
		}

		#endregion

		#region PrivateMethods

		private static bool HasOverrides(PrefabInstanceOverrides overrides, ulong target)
		{
			foreach (PropertyModification modification in overrides.Of(target))
			{
				return true;
			}

			return false;
		}

		/// <summary>The overrides of a target at a path, inside it, or replacing a value it is inside of.</summary>
		private static List<PropertyModification> Matching(PrefabInstanceOverrides overrides, ulong target, string path)
		{
			var result = new List<PropertyModification>();
			foreach (PropertyModification modification in overrides.Of(target))
			{
				string full = modification.FullPath;
				if (PropertyPath.IsWithin(full, path) || PropertyPath.IsWithin(path, full) || full == PropertyPath.SizeOf(path))
				{
					result.Add(modification);
				}
			}

			return result;
		}

		/// <summary>The Overrides list of an instance (Unity's Overrides dropdown): every change with Revert / Apply, and Revert All / Apply All.</summary>
		private static void DrawOverridesList(EditorApplication editor, PrefabInstance instance, PrefabInstanceOverrides overrides, InspectorContext context)
		{
			ImGui.TextUnformatted("Overrides of '" + instance.Root?.Name + "' to the prefab '" + instance.Prefab?.name + "'");
			ImGui.Separator();
			if (overrides.IsEmpty)
			{
				ImGui.TextDisabled("The instance is the same as its prefab.");
				return;
			}

			ResolvedPrefab source = instance.Source;
			int row = 0;
			// Changed values, per object and component in the prefab's order.
			foreach (PrefabObject obj in source.Objects)
			{
				DrawTargetOverrides(editor, instance, overrides, obj.Id, obj.Name, false, ref row);
				foreach (PrefabComponent component in obj.Components)
				{
					DrawTargetOverrides(editor, instance, overrides, component.Id, obj.Name + " (" + GenericValues.Describe(component.Data, ValueTag.Object) + ")", true, ref row);
				}
			}

			foreach (Behaviour added in overrides.AddedComponents)
			{
				ImGui.PushID(row++);
				DrawEntry(IconKind.AddedBadge, EditorStyle.Added, added.gameObject.Name + " (" + added.GetType().Name + ")", "added component",
					PrefabUtility.GetApplyTargets(instance, added.gameObject.prefabSourceId, false),
					target => PrefabMenus.ApplyAddedComponent(editor, added, target), () => PrefabMenus.RevertAddedComponent(editor, added));
				ImGui.PopID();
			}

			foreach (ulong removed in overrides.RemovedComponents)
			{
				ImGui.PushID(row++);
				DrawEntry(IconKind.RemovedBadge, EditorStyle.PrefabMissing, PrefabUtility.DescribeSource(instance, removed), "removed component",
					PrefabUtility.GetApplyTargets(instance, removed, true),
					target => PrefabMenus.ApplyRemovedComponent(editor, instance, removed, target), () => PrefabMenus.RevertRemovedComponent(editor, instance, removed));
				ImGui.PopID();
			}

			foreach (GameObject added in overrides.AddedObjects)
			{
				GameObject host = added.transform.parent.gameObject;
				ImGui.PushID(row++);
				DrawEntry(IconKind.AddedBadge, EditorStyle.Added, added.Name, "added GameObject",
					PrefabUtility.GetApplyTargets(instance, host.prefabSourceId, false),
					target => PrefabMenus.ApplyAddedGameObject(editor, added, target), () => PrefabMenus.RevertAddedGameObject(editor, added));
				ImGui.PopID();
			}

			foreach (ulong removed in overrides.RemovedObjects)
			{
				ImGui.PushID(row++);
				DrawEntry(IconKind.RemovedBadge, EditorStyle.PrefabMissing, PrefabUtility.DescribeSource(instance, removed), "removed GameObject",
					PrefabUtility.GetApplyTargets(instance, removed, false),
					target => PrefabMenus.ApplyRemovedGameObject(editor, instance, removed, target), () => PrefabMenus.RevertRemovedGameObject(editor, instance, removed));
				ImGui.PopID();
			}

			ImGui.Separator();
			if (ImGui.Button("Revert All"))
			{
				PrefabMenus.RevertAll(editor, instance);
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Apply All"))
			{
				PrefabMenus.ApplyAll(editor, instance);
				ImGui.CloseCurrentPopup();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Every override goes into '" + instance.Prefab?.name + "' (the instance's position, rotation and name stay its own)");
			}
		}

		/// <summary>The changed values of one object or component: a line for it (Revert / Apply all of them) and one per value.</summary>
		private static void DrawTargetOverrides(EditorApplication editor, PrefabInstance instance, PrefabInstanceOverrides overrides, ulong target, string name, bool isComponent, ref int row)
		{
			var modifications = new List<PropertyModification>(overrides.Of(target));
			if (modifications.Count == 0)
			{
				return;
			}

			List<PrefabApplyTarget> targets = PrefabUtility.GetApplyTargets(instance, target, isComponent);
			ImGui.PushID(row++);
			DrawEntry(isComponent ? IconKind.Component : IconKind.GameObject, EditorStyle.Prefab, name, modifications.Count == 1 ? "1 override" : modifications.Count + " overrides",
				targets, chosen => PrefabMenus.ApplyProperties(editor, instance, modifications, chosen), () => PrefabMenus.RevertProperties(editor, instance, modifications));
			ImGui.Indent(18f);
			for (int i = 0; i < modifications.Count; i++)
			{
				PropertyModification modification = modifications[i];
				var single = new List<PropertyModification> { modification };
				ImGui.PushID(i);
				DrawEntry(IconKind.None, EditorStyle.Prefab, Describe(modification), null, targets,
					chosen => PrefabMenus.ApplyProperties(editor, instance, single, chosen), () => PrefabMenus.RevertProperties(editor, instance, single));
				ImGui.PopID();
			}

			ImGui.Unindent(18f);
			ImGui.PopID();
		}

		/// <summary>One line of the list: icon, text, a detail, and its Revert and Apply buttons at the end.</summary>
		private static void DrawEntry(IconKind icon, NVector4 color, string text, string detail, List<PrefabApplyTarget> targets, Action<PrefabApplyTarget> apply, Action revert)
		{
			float size = ImGui.GetTextLineHeight();
			NVector2 start = ImGui.GetCursorScreenPos();
			float lineRight = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
			if (icon != IconKind.None)
			{
				EditorIcons.Draw(ImGui.GetWindowDrawList(), start + new NVector2(0f, 2f), size * 0.85f, icon, ImGui.GetColorU32(color));
				ImGui.Dummy(new NVector2(size, size));
				ImGui.SameLine();
			}

			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(text);
			if (detail != null)
			{
				ImGui.SameLine();
				ImGui.TextDisabled(detail);
			}

			float buttons = ImGui.CalcTextSize("Revert").X + ImGui.CalcTextSize(targets.Count > 1 ? "Apply..." : "Apply").X + ImGui.GetStyle().FramePadding.X * 4f + ImGui.GetStyle().ItemSpacing.X;
			ImGui.SameLine();
			ImGui.SameLine(Math.Max(ImGui.GetCursorPosX() + 8f, lineRight - buttons));
			if (ImGui.SmallButton("Revert"))
			{
				revert();
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (targets.Count <= 1)
			{
				ImGui.BeginDisabled(targets.Count == 0);
				if (ImGui.SmallButton("Apply") && targets.Count == 1)
				{
					apply(targets[0]);
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndDisabled();
				if (targets.Count == 1 && ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(PrefabMenus.ApplyLabel(targets[0]));
				}

				return;
			}

			if (ImGui.SmallButton("Apply..."))
			{
				ImGui.OpenPopup("##apply_targets");
			}

			if (ImGui.BeginPopup("##apply_targets"))
			{
				PrefabApplyTarget chosen = PrefabMenus.ApplyItems(targets);
				ImGui.EndPopup();
				if (chosen != null)
				{
					apply(chosen);
					ImGui.CloseCurrentPopup();
				}
			}
		}

		private static void DrawIconText(IconKind icon, NVector4 color, string text)
		{
			float size = ImGui.GetTextLineHeight();
			NVector2 start = ImGui.GetCursorScreenPos();
			ImGui.AlignTextToFramePadding();
			EditorIcons.Draw(ImGui.GetWindowDrawList(), start + new NVector2(0f, (ImGui.GetFrameHeight() - size) * 0.5f), size, icon, ImGui.GetColorU32(color));
			ImGui.Dummy(new NVector2(size, ImGui.GetFrameHeight()));
			ImGui.SameLine();
			ImGui.AlignTextToFramePadding();
			ImGui.TextColored(color, text);
		}

		private static void DrawBadgeText(IconKind icon, NVector4 color, string text)
		{
			float size = ImGui.GetTextLineHeight() * 0.8f;
			NVector2 start = ImGui.GetCursorScreenPos();
			EditorIcons.Draw(ImGui.GetWindowDrawList(), start + new NVector2(0f, (ImGui.GetFrameHeight() - size) * 0.5f), size, icon, ImGui.GetColorU32(color));
			ImGui.Dummy(new NVector2(size, ImGui.GetFrameHeight()));
			ImGui.SameLine();
			ImGui.AlignTextToFramePadding();
			ImGui.TextDisabled(text);
		}

		/// <summary>An override as a line: the property's name (an element, an entry, the length) and the value.</summary>
		private static string Describe(PropertyModification modification)
		{
			string path = modification.Path;
			string name = path switch
			{
				PropertyModification.NameProperty => "Name",
				PropertyModification.ActiveProperty => "Active",
				PropertyModification.LayerProperty => "Layer",
				PropertyModification.EnabledProperty => "Enabled",
				_ => NicePath(path)
			};

			if (modification.HasKey)
			{
				name += " [" + ValueText(modification.Key, modification.KeyTag) + "]";
				return modification.Removed ? name + "  (removed)" : name + " = " + ValueText(modification.Value, modification.Tag);
			}

			return name + " = " + ValueText(modification.Value, modification.Tag);
		}

		/// <summary>A value of an override as text; a reference by the name of the scene object it points at.</summary>
		private static string ValueText(object value, ValueTag tag)
		{
			Scene scene = EditorApplication.Instance?.Scene;
			if (value is ulong id && (tag == ValueTag.SceneRef || tag == ValueTag.Object) && id != 0 && scene != null)
			{
				GameObject go = scene.FindObjectById(id);
				if (go != null)
				{
					return go.Name;
				}

				Behaviour component = scene.FindComponentById(id);
				return component != null ? component.gameObject?.Name + " (" + component.GetType().Name + ")" : "missing object";
			}

			return GenericValues.Describe(value, tag);
		}

		/// <summary>"weapon.damage" -> "Weapon > Damage", "drops[2]" -> "Drops [2]", "drops.#size" -> "Drops > Size".</summary>
		private static string NicePath(string path)
		{
			var parts = new List<string>();
			foreach (string part in path.Split('.'))
			{
				if (part == PropertyPath.Size)
				{
					parts.Add("Size");
					continue;
				}

				int bracket = part.IndexOf('[');
				string field = bracket < 0 ? part : part.Substring(0, bracket);
				parts.Add((field.Length > 0 ? SerializationUtility.NicifyName(field) : "") + (bracket >= 0 ? " " + part.Substring(bracket) : ""));
			}

			return string.Join(" > ", parts);
		}

		#endregion
	}
}
