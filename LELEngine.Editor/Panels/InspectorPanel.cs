using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using LELEngine.Shaders;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Components of the selected GameObject: serialized fields, enable / remove, Add Component.</summary>
	internal sealed class InspectorPanel : IEditorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;

		// The renderers' material editors below the components (their own undo target and field paths).
		private readonly InspectorContext materialContext;
		private string nameBuffer = "";
		private GameObject nameOwner;

		// The shown object is read-only (HideFlags.NotEditable): values disabled, no component menu, no Add Component.
		private bool readOnly;
		private string componentFilter = "";
		private List<Type> componentTypes;
		private LevelList componentLevels;
		private int componentTypesVersion = -1;

		// Euler angles last shown per transform: re-deriving them from the quaternion every frame flips between
		// equivalent angle sets while dragging.
		private readonly Dictionary<Transform, (Quaternion rotation, Vector3 euler)> eulerHints = new Dictionary<Transform, (Quaternion, Vector3)>();

		// The lock (as in Unity): the inspector keeps showing these objects (by id: they survive scene reloads; the first
		// is the active one) or assets while other things are selected, e.g. to drag several selected assets on to one of
		// its lists.
		private bool locked;
		private readonly List<ulong> lockedObjectIds = new List<ulong>();
		private readonly List<string> lockedAssets = new List<string>();

		// Several objects edited together: where their components' values differ, by the shown component's id, worked
		// out again when the scene changed or a moment passed (play mode changes values without telling), and for the
		// header (name, active, layer, static).
		private readonly Dictionary<ulong, (MixedValues values, int version, double time)> mixedCache = new Dictionary<ulong, (MixedValues, int, double)>();
		private string mixedSelection = "";

		// The objects the inspector shows (several: edited together), the active one first.
		private readonly List<GameObject> shownObjects = new List<GameObject>();

		// Components whose header is folded, by id (they survive scene reloads): the scene view asks before drawing their gizmos.
		private readonly HashSet<ulong> collapsed = new HashSet<ulong>();

		#endregion

		#region Constructors

		public InspectorPanel(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
			materialContext = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		string IEditorPanel.PanelName => "Inspector";
		string IEditorPanel.ImGuiName => "Inspector";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(520, 900);

		/// <summary>Test runs: the scroll position to set on the next draw (screenshots of long inspectors).</summary>
		public static float? TestScroll;

		/// <summary>Test runs: locks the inspector on what it shows at the next draw.</summary>
		public static bool TestLock;

		/// <summary>The GameObject the inspector shows (the selection, or the locked object), null when none.</summary>
		public GameObject Shown { get; private set; }

		/// <summary>The component's header is unfolded, or was never drawn (headers start open).</summary>
		public bool IsExpanded(Behaviour component)
		{
			return !collapsed.Contains(component.Id);
		}

		public void Draw()
		{
			if (!Open)
			{
				Shown = null;
				return;
			}

			Shown = null;
			if (ImGui.Begin("Inspector", ref Open))
			{
				if (TestScroll.HasValue)
				{
					ImGui.SetScrollY(TestScroll.Value);
					TestScroll = null;
				}

				if (TestLock)
				{
					TestLock = false;
					Lock(true);
				}

				NVector2 topRight = new NVector2(ImGui.GetCursorScreenPos().X + ImGui.GetContentRegionAvail().X, ImGui.GetCursorScreenPos().Y);
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
				finally
				{
					// Both contexts: the material sections' keeps the scene too, and a reload must not find it.
					context.Release();
					materialContext.Release();
				}

				// The lock at the top right, over the first line.
				float size = ImGui.GetFrameHeight();
				ImGui.SetCursorScreenPos(topRight - new NVector2(size, 0f));
				// The click changes the lock: the pop follows the push, not the new state (an unbalanced pop is an ImGui assert).
				bool wasLocked = locked;
				if (wasLocked)
				{
					ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
				}

				bool clicked = EditorIcons.Button("inspector_lock", wasLocked ? IconKind.Lock : IconKind.Unlock, wasLocked ? "Locked: shows this object whatever is selected (click to unlock)" : "Lock the inspector on this object (select other things, e.g. assets to drag on to its fields)", size);
				TestAnchors.RecordItem("button:InspectorLock");
				if (clicked)
				{
					Lock(!wasLocked);
				}

				if (wasLocked)
				{
					ImGui.PopStyleColor();
				}
			}

			ImGui.End();
		}

		/// <summary>An asset file or folder moved: a lock on assets follows them.</summary>
		public void AssetMoved(string fromPath, string toPath)
		{
			for (int i = 0; i < lockedAssets.Count; i++)
			{
				lockedAssets[i] = AssetDatabase.MovedPath(lockedAssets[i], fromPath, toPath) ?? lockedAssets[i];
			}
		}

		/// <summary>The inspector keeps showing what it shows now, one object or asset or several (or follows the selection again).</summary>
		public void Lock(bool value)
		{
			locked = value && (editor.Selected != null || editor.SelectedAsset != null || Shown != null);
			lockedObjectIds.Clear();
			lockedAssets.Clear();
			if (!locked)
			{
				return;
			}

			if (Shown != null && shownObjects.Count > 0)
			{
				foreach (GameObject shown in shownObjects)
				{
					lockedObjectIds.Add(shown.Id);
				}

				return;
			}

			GameObject target = editor.Selected;
			if (target != null)
			{
				lockedObjectIds.Add(target.Id);
				foreach (GameObject other in editor.SelectedObjects)
				{
					if (other != target)
					{
						lockedObjectIds.Add(other.Id);
					}
				}

				return;
			}

			// The active asset first.
			lockedAssets.Add(editor.SelectedAsset);
			foreach (string asset in editor.SelectedAssets)
			{
				if (!string.Equals(asset, editor.SelectedAsset, StringComparison.OrdinalIgnoreCase))
				{
					lockedAssets.Add(asset);
				}
			}
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			// While the project loads nothing is selected (and a lock's asset is not read yet).
			if (editor.IsLoadingProject)
			{
				ImGui.TextDisabled("Nothing selected");
				return;
			}

			// What is shown, the active one first: the selected objects, or the selected project files.
			var objects = new List<GameObject>();
			var assets = new List<string>();
			if (locked)
			{
				// The locked objects of the current scene (found again after reloads), else the locked assets; gone: unlocked.
				foreach (ulong id in lockedObjectIds)
				{
					GameObject lockedObject = editor.Scene?.FindObjectById(id);
					if (lockedObject != null && !lockedObject.dead)
					{
						objects.Add(lockedObject);
					}
				}

				if (objects.Count == 0)
				{
					assets.AddRange(lockedAssets.FindAll(path => path != null && System.IO.File.Exists(AssetDatabase.ToAbsolute(path))));
					if (assets.Count == 0)
					{
						Lock(false);
					}
				}
			}

			if (!locked)
			{
				GameObject active = editor.Selected;
				if (active != null)
				{
					objects.Add(active);
					foreach (GameObject other in editor.SelectedObjects)
					{
						if (other != active && other.scene == active.scene)
						{
							objects.Add(other);
						}
					}
				}
				else if (editor.SelectedAsset != null)
				{
					assets.Add(editor.SelectedAsset);
					foreach (string other in editor.SelectedAssets)
					{
						if (!string.Equals(other, editor.SelectedAsset, StringComparison.OrdinalIgnoreCase))
						{
							assets.Add(other);
						}
					}
				}
			}

			GameObject go = objects.Count > 0 ? objects[0] : null;
			if (go == null || go.scene != editor.Scene)
			{
				shownObjects.Clear();
				if (assets.Count > 0)
				{
					context.Scene = editor.Scene;
					context.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
					ImGui.PushID(assets[0]);
					AssetInspectors.Draw(assets, context);
					ImGui.PopID();
					return;
				}

				ImGui.TextDisabled("Nothing selected");
				return;
			}

			Shown = go;
			shownObjects.Clear();
			shownObjects.AddRange(objects);

			context.Scene = editor.Scene;
			context.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
			if (objects.Count > 1)
			{
				// Several objects: edited together (Unity's multi-object editing).
				DrawMultiple(objects);
				return;
			}

			// A prefab instance's object: its overrides are marked (bold, the bar) and can be applied or reverted.
			context.Overrides = PrefabInspector.OverridesOf(go.prefabInstance, editor.SceneVersion);
			// A read-only object (HideFlags.NotEditable) is shown as it is: its values disabled, its headers still fold.
			readOnly = TempObjects.IsReadOnly(go);
			ImGui.BeginDisabled(readOnly);
			DrawObjectHeader(go);
			ImGui.EndDisabled();

			string temporary = TempObjects.Describe(go);
			if (temporary != null)
			{
				ImGui.TextDisabled(temporary);
			}

			ImGui.Separator();
			PrefabInspector.DrawPrefabBar(editor, go, context);

			foreach (Behaviour component in new List<Behaviour>(go.Components))
			{
				ImGui.PushID(component.Id.ToString());
				// Its automation elements carry the title the header shows: "header:Mover", "input:Mover/Speed".
				TestAnchors.PushScope(ComponentTitle(component));
				DrawComponent(go, component);
				TestAnchors.PopScope();
				ImGui.PopID();
			}

			PrefabInspector.DrawRemovedComponents(editor, go, context);
			if (!readOnly)
			{
				DrawAddComponent(go);
			}

			ImGui.BeginDisabled(readOnly);
			DrawRendererMaterials(go);
			ImGui.EndDisabled();
		}

		/// <summary>
		///     The materials of the object's renderers below its components, each with its full editor (as in Unity): an
		///     imported model's materials are edited where they are used. Edits change the material asset, so every
		///     renderer using it follows. Engine materials and materials made at runtime are shown, not edited.
		/// </summary>
		private void DrawRendererMaterials(GameObject go)
		{
			DrawRendererMaterials(new List<GameObject> { go });
		}

		/// <summary>The materials of several objects' renderers, each once (at most <see cref="MaxMaterials" />).</summary>
		private void DrawRendererMaterials(List<GameObject> objects)
		{
			var materials = new List<Material>();
			int more = 0;
			foreach (Behaviour component in Components(objects))
			{
				IReadOnlyList<Material> slots = component switch
				{
					MeshRenderer renderer => renderer.Materials,
					TrailRenderer trail => trail.Materials,
					LELEngine.Rendering.ICustomRenderer custom => custom.Material != null ? new[] { custom.Material } : null,
					_ => null
				};

				if (slots == null)
				{
					continue;
				}

				foreach (Material material in slots)
				{
					if (material != null && !materials.Contains(material))
					{
						if (materials.Count < MaxMaterials)
						{
							materials.Add(material);
						}
						else
						{
							more++;
						}
					}
				}
			}

			if (materials.Count == 0)
			{
				return;
			}

			// The materials are assets, not parts of the object: a gap and a heavy rule set them apart from the components.
			ImGui.Dummy(new NVector2(0f, 10f));
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 ruleStart = ImGui.GetCursorScreenPos();
			float right = ruleStart.X + ImGui.GetContentRegionAvail().X;
			draw.AddRectFilled(ruleStart, new NVector2(right, ruleStart.Y + 2f), ImGui.GetColorU32(ImGuiCol.Separator));
			ImGui.Dummy(new NVector2(0f, 6f));

			materialContext.Scene = editor.Scene;
			materialContext.LabelWidth = context.LabelWidth;
			for (int i = 0; i < materials.Count; i++)
			{
				ImGui.PushID("material" + i);
				TestAnchors.PushScope(materials[i].name ?? i.ToString());
				MaterialSection.Draw(materials[i], materialContext, "material:" + (materials[i].name ?? i.ToString()), true);
				TestAnchors.PopScope();
				ImGui.PopID();
				ImGui.Dummy(new NVector2(0f, 4f));
			}

			if (more > 0)
			{
				ImGui.TextDisabled(more + " more material" + (more == 1 ? "" : "s") + " (select fewer objects to see them)");
			}
		}

		// Several selected objects can use many materials: a full editor each is drawn for the first ones only.
		private const int MaxMaterials = 16;

		private static IEnumerable<Behaviour> Components(List<GameObject> objects)
		{
			foreach (GameObject go in objects)
			{
				foreach (Behaviour component in go.Components)
				{
					yield return component;
				}
			}
		}

		private void DrawObjectHeader(GameObject go)
		{
			if (nameOwner != go)
			{
				nameOwner = go;
				nameBuffer = go.Name;
			}

			bool active = go.activeSelf;
			if (EditorGui.Checkbox("##active", ref active))
			{
				editor.Undo.Push(new GameObjectStateRecord { Name = (active ? "Activate " : "Deactivate ") + go.Name, ObjectId = go.Id, NameBefore = go.Name, NameAfter = go.Name, ActiveBefore = go.activeSelf, ActiveAfter = active });
				go.SetActive(active);
				editor.NotifyChanged(go);
			}

			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.ActiveProperty);
			ImGui.SameLine();
			// Room for the lock button at the end of the line.
			ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
			EditorGui.InputText("##name", ref nameBuffer, 256);
			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.NameProperty);
			if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(nameBuffer) && nameBuffer != go.Name)
			{
				editor.Undo.Push(new GameObjectStateRecord { Name = "Rename " + go.Name, ObjectId = go.Id, NameBefore = go.Name, NameAfter = nameBuffer, ActiveBefore = go.activeSelf, ActiveAfter = go.activeSelf });
				go.Name = nameBuffer;
				editor.MarkDirty();
			}
			else if (!ImGui.IsItemActive())
			{
				nameBuffer = go.Name;
			}

			// Layer (collision filtering, queries, the cameras' culling masks).
			ImGui.AlignTextToFramePadding();
			ImGui.TextDisabled("Layer");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(160);
			if (EditorGui.BeginCombo("##layer", LayerMask.DisplayName(go.layer)))
			{
				for (int layer = 0; layer < 32; layer++)
				{
					string name = LayerMask.LayerToName(layer);
					if (name.Length == 0 && layer != go.layer && layer < 8)
					{
						continue;
					}

					if (EditorGui.Selectable(LayerMask.DisplayName(layer), layer == go.layer) && layer != go.layer)
					{
						// An object with children asks whether they change too (EditorApplication.RequestLayer).
						editor.RequestLayer(go, layer);
					}
				}

				ImGui.Separator();
				if (EditorGui.Selectable("Add Layer..."))
				{
					editor.ShowProjectSettings(ProjectSettingsWindow.Tab.Layers);
				}

				EditorGui.EndCombo();
			}

			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.LayerProperty);

			// Static (GameObject.isStatic): the GI caches the object, static batching (a built game, play mode) may merge its
			// mesh into a batch. An object with children asks whether they change too (EditorApplication.RequestStatic).
			ImGui.SameLine(0, 16);
			bool isStatic = go.isStatic;
			if (EditorGui.Checkbox("Static", ref isStatic))
			{
				editor.RequestStatic(go, isStatic);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The object does not move or change while the game runs: the GI works its renderer out once\n" +
					"instead of every frame, and static batching (Project Settings > Graphics; in built games and in\n" +
					"play mode) may merge its mesh with other static ones into one draw.");
			}

			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.StaticProperty);
		}

		/// <summary>
		///     A component's header and body. With <paramref name="others" /> (the same component of the other objects edited
		///     together, as many as them) the header's toggle and menu act on all of them, and the body's edits are written
		///     into theirs too.
		/// </summary>
		private void DrawComponent(GameObject go, Behaviour component, IReadOnlyList<Behaviour> others = null)
		{
			context.UndoTarget = component;
			context.UndoGroup = others;
			context.Mixed = others != null ? MixedOf(component, others) : null;
			// Overrides of this component (its id in the prefab); none for components added to the instance, nor for
			// several objects edited together (their overrides are each object's own).
			context.OverrideTarget = others == null && component.prefabInstance != null ? component.prefabSourceId : 0;
			context.Path = "";
			Type type = component.GetType();
			string title = ComponentTitle(component);

			bool added = others == null && PrefabUtility.IsAddedComponent(component);
			bool overridden = others == null && (added || HasOverrides(component));
			// [InspectorColor] components (the engine's by kind, scripts as they choose): a bar on the left and a light tint
			// of the header, quieter than a material's framed section.
			NVector4? kind = ComponentColor(component);
			if (kind.HasValue)
			{
				ImGui.PushStyleColor(ImGuiCol.Header, EditorStyle.Tinted(ImGuiCol.Header, kind.Value, 0.1f));
				ImGui.PushStyleColor(ImGuiCol.HeaderHovered, EditorStyle.Tinted(ImGuiCol.HeaderHovered, kind.Value, 0.14f));
			}

			// The transform's and the project's scripts' own icons before the title (as in object fields referencing them).
			IconKind headerIcon = component is Transform ? IconKind.Transform : !(component is MissingComponent) && EditorIcons.IsScript(component) ? IconKind.ScriptComponent : IconKind.None;
			float iconSize = ImGui.GetTextLineHeight();
			string iconPadding = headerIcon != IconKind.None ? new string(' ', (int)Math.Ceiling((iconSize + 4f) / Math.Max(1f, ImGui.CalcTextSize(" ").X))) : "";
			ImGui.SetNextItemAllowOverlap();
			bool bold = overridden && EditorFonts.PushBold();
			bool open = EditorGui.CollapsingHeader((added ? PrefabInspector.AddedTitlePadding() : "") + iconPadding + title + "##header", ImGuiTreeNodeFlags.DefaultOpen);
			EditorFonts.Pop(bold);
			// A capture draws in a fresh ImGui context, where every header starts open.
			if (!Automation.WindowCapture.Active)
			{
				if (open)
				{
					collapsed.Remove(component.Id);
				}
				else
				{
					collapsed.Add(component.Id);
				}
			}

			NVector2 headerMin = ImGui.GetItemRectMin();
			NVector2 headerMax = ImGui.GetItemRectMax();
			if (headerIcon != IconKind.None)
			{
				// After the "+" of a component added to a prefab instance.
				float x = headerMin.X + ImGui.GetTreeNodeToLabelSpacing() + (added ? ImGui.CalcTextSize(PrefabInspector.AddedTitlePadding()).X : 0f);
				EditorIcons.Draw(ImGui.GetWindowDrawList(), new NVector2(x, headerMin.Y + (headerMax.Y - headerMin.Y - iconSize) * 0.5f), iconSize, headerIcon, EditorIcons.ReferenceColor(headerIcon));
			}
			if (kind.HasValue)
			{
				ImGui.PopStyleColor(2);
				ImGui.GetWindowDrawList().AddRectFilled(headerMin, new NVector2(headerMin.X + 3f, headerMax.Y), ImGui.GetColorU32(kind.Value));
			}

			TestAnchors.RecordItem("component:" + type.Name);
			if (others == null)
			{
				PrefabInspector.DecorateComponentHeader(editor, component, context);
			}

			// The header drags this very component on to object fields (of its type, its GameObject's or an interface it implements).
			if (!(component is MissingComponent) && ImGui.BeginDragDropSource())
			{
				DragAndDrop.SetPayload(DragAndDrop.ReferencePayload, DragItem.FromComponent(component));
				ImGui.EndDragDropSource();
			}

			// The header's actions take the component of every object edited together.
			var all = new List<Behaviour> { component };
			if (others != null)
			{
				all.AddRange(others);
			}

			if (!readOnly && EditorGui.BeginPopupContextItem("##component_context"))
			{
				if (!(component is Transform) && EditorMenu.Item("Remove Component"))
				{
					editor.RecordStructural("Remove " + type.Name + (all.Count > 1 ? " from " + all.Count + " objects" : ""), () =>
					{
						foreach (Behaviour removed in all)
						{
							GameObject.DestroyImmediate(removed);
						}
					});
					ImGui.EndPopup();
					return;
				}

				if (!(component is Transform) && !(component is MissingComponent) && EditorMenu.Item("Reset"))
				{
					editor.Edits.Begin(component, others);
					foreach (Behaviour reset in all)
					{
						ResetFields(reset);
						editor.NotifyChanged(reset.gameObject);
					}

					editor.Edits.RequestCommit();
				}

				// The transform's parts (local values, as Unity's Reset): one undo step each.
				if (component is Transform transformComponent)
				{
					if (EditorMenu.Item("Reset Position"))
					{
						ResetTransform(all, true, false, false);
					}

					if (EditorMenu.Item("Reset Rotation"))
					{
						ResetTransform(all, false, true, false);
					}

					if (EditorMenu.Item("Reset Scale"))
					{
						ResetTransform(all, false, false, true);
					}

					ImGui.Separator();
					if (EditorMenu.Item("Reset All"))
					{
						ResetTransform(all, true, true, true);
					}
				}

				// The project's script of the component (engine components have none).
				string script = component is MissingComponent ? null : ScriptFiles.Find(type);
				if (script != null)
				{
					ImGui.Separator();
					if (EditorMenu.Item("Edit Script"))
					{
						ScriptFiles.Open(type);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Open " + script + " in the external script editor");
					}
				}

				if (others == null)
				{
					PrefabInspector.DrawComponentMenu(editor, component, context);
				}

				ImGui.EndPopup();
			}

			if (!(component is Transform) && !(component is MissingComponent))
			{
				ImGui.SameLine(ImGui.GetWindowWidth() - 34f);
				// Several objects: a dash when some are enabled and some not; a click enables them all.
				bool mixedEnabled = all.Exists(c => c.enabled != component.enabled);
				bool enabled = !mixedEnabled && component.enabled;
				ImGui.BeginDisabled(readOnly);
				ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixedEnabled);
				bool toggled = EditorGui.Checkbox("##enabled", ref enabled);
				ImGui.PopItemFlag();
				ImGui.EndDisabled();
				if (toggled)
				{
					editor.Edits.Begin(component, others);
					foreach (Behaviour toggledComponent in all)
					{
						toggledComponent.enabled = enabled;
						editor.NotifyChanged(toggledComponent.gameObject);
					}

					editor.Edits.RequestCommit();
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(mixedEnabled ? "Enabled on some of the selected objects" : "Enabled");
				}
			}

			if (!open)
			{
				return;
			}

			if (!kind.HasValue)
			{
				DrawComponentBody(go, component, others);
				return;
			}

			// A faint tint of the kind's color behind the body (a material's panel is stronger and framed). Drawn first,
			// shown behind: the body's size is known once it is drawn.
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			var bodyMin = new NVector2(headerMin.X, headerMax.Y);
			float right = headerMax.X;
			draw.ChannelsSplit(2);
			draw.ChannelsSetCurrent(1);
			try
			{
				DrawComponentBody(go, component, others);
			}
			finally
			{
				draw.ChannelsSetCurrent(0);
				NVector4 tint = kind.Value;
				draw.AddRectFilled(bodyMin, new NVector2(right, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y), ImGui.GetColorU32(new NVector4(tint.X, tint.Y, tint.Z, 0.045f)));
				draw.ChannelsMerge();
			}
		}

		private void DrawComponentBody(GameObject go, Behaviour component, IReadOnlyList<Behaviour> others)
		{
			ImGui.BeginDisabled(readOnly);
			try
			{
				DrawComponentFields(go, component, others);
			}
			finally
			{
				ImGui.EndDisabled();
			}
		}

		private void DrawComponentFields(GameObject go, Behaviour component, IReadOnlyList<Behaviour> others)
		{
			ImGui.Indent(4);
			// Several objects edited together: what this draw changes in the shown component is written into the others'
			// (its data before the draw against after it).
			SerializedNode before = others != null && !(component is Transform) && !(component is MissingComponent)
				? MultiEdit.NodeOf(component, new SerializationContext { Scene = editor.Scene })
				: null;
			bool changed;
			if (component is Transform transform)
			{
				changed = DrawTransform(transform, others);
			}
			else if (component is MissingComponent missingComponent)
			{
				ImGui.TextColored(EditorStyle.Warning, "The script class was not found (renamed, deleted, or the scripts do not compile).");
				ImGui.TextDisabled("Its data is kept and saved with the scene.");
				changed = false;
			}
			else if (editor.Extensions.GetEditor(component) is Editor custom && (others == null || EditorExtensions.EditsMultipleObjects(custom)))
			{
				// [CustomEditor] of the project's editor scripts: it draws the whole body (with several objects only one
				// marked [CanEditMultipleObjects]; its targets are all of them).
				custom.targets = others == null ? new[] { component } : Targets(component, others);
				changed = editor.Extensions.DrawInspector(custom, context);
			}
			else
			{
				if (others != null && editor.Extensions.GetEditor(component) is Editor single)
				{
					ImGui.TextDisabled(single.GetType().Name + " edits one object: its fields are shown");
				}

				changed = FieldDrawer.DrawFields(component, context);
				changed |= CustomInspectors.Draw(component, context);
			}

			if (changed)
			{
				editor.NotifyChanged(go);
				editor.Scene.Validate(component);
			}

			if (before != null)
			{
				MultiEdit.Propagate(component, before, others, () => EditorSerialization.ContextFor(editor.Scene), other =>
				{
					var otherComponent = (Behaviour)other;
					editor.NotifyChanged(otherComponent.gameObject);
				}, context.Touched);
			}

			ImGui.Unindent(4);
			ImGui.Spacing();
		}

		private static Behaviour[] Targets(Behaviour component, IReadOnlyList<Behaviour> others)
		{
			var targets = new Behaviour[others.Count + 1];
			targets[0] = component;
			for (int i = 0; i < others.Count; i++)
			{
				targets[i + 1] = others[i];
			}

			return targets;
		}

		/// <summary>Where the values of the component and the others' differ (worked out again when the scene changed or half a second passed).</summary>
		private MixedValues MixedOf(Behaviour component, IReadOnlyList<Behaviour> others)
		{
			double now = ImGui.GetTime();
			if (mixedCache.TryGetValue(component.Id, out (MixedValues values, int version, double time) cached)
				&& cached.version == editor.SceneVersion && now - cached.time < 0.5)
			{
				return cached.values;
			}

			MixedValues values = MixedValues.Compare(component, others, new SerializationContext { Scene = editor.Scene });
			mixedCache[component.Id] = (values, editor.SceneVersion, now);
			return values;
		}

		/// <summary>The component's header title: its class name spaced out ("Mesh Renderer"), or the missing script's.</summary>
		private static string ComponentTitle(Behaviour component)
		{
			return component is MissingComponent missing ? "Missing script: " + missing.TypeName : SerializationUtility.NicifyName(component.GetType().Name);
		}

		/// <summary>The component's [InspectorColor]: its kind's theme color or its fixed color; null without one.</summary>
		private static NVector4? ComponentColor(Behaviour component)
		{
			var attribute = component.GetType().GetCustomAttribute<InspectorColorAttribute>(true);
			if (attribute == null)
			{
				return null;
			}

			if (!attribute.Kind.HasValue)
			{
				return new NVector4(attribute.R, attribute.G, attribute.B, 1f);
			}

			return EditorStyle.Get(attribute.Kind.Value switch
			{
				InspectorColorKind.Physics => EditorColor.ComponentPhysics,
				InspectorColorKind.Rendering => EditorColor.ComponentRendering,
				InspectorColorKind.Audio => EditorColor.ComponentAudio,
				InspectorColorKind.Camera => EditorColor.ComponentCamera,
				InspectorColorKind.Lighting => EditorColor.ComponentLighting,
				InspectorColorKind.Navigation => EditorColor.ComponentNavigation,
				InspectorColorKind.Effects => EditorColor.ComponentEffects,
				_ => EditorColor.ComponentUI
			});
		}

		/// <summary>Sets the transforms' local position to zero, rotation to none and / or scale to one, as one undo step.</summary>
		private void ResetTransform(List<Behaviour> transforms, bool position, bool rotation, bool scale)
		{
			editor.Edits.Begin(transforms[0], transforms.Count > 1 ? transforms.GetRange(1, transforms.Count - 1) : null);
			foreach (Behaviour component in transforms)
			{
				var transform = (Transform)component;
				if (position)
				{
					transform.localPosition = Vector3.Zero;
				}

				if (rotation)
				{
					transform.localRotation = Quaternion.Identity;
					eulerHints.Remove(transform);
				}

				if (scale)
				{
					transform.localScale = Vector3.One;
				}

				editor.NotifyChanged(transform.gameObject);
			}

			editor.Edits.RequestCommit();
		}

		/// <summary>The Euler angles shown for a transform: the ones last shown while its rotation is unchanged (no flipping between equivalent sets).</summary>
		private Vector3 EulerOf(Transform transform)
		{
			Quaternion rotation = transform.localRotation;
			return eulerHints.TryGetValue(transform, out (Quaternion rotation, Vector3 euler) hint) && hint.rotation == rotation ? hint.euler : FieldDrawer.ToEuler(rotation);
		}

		/// <summary>
		///     Position, rotation (Euler angles) and scale. With <paramref name="others" /> (the other selected objects'
		///     transforms) an axis whose value differs between them shows "—", and an edit of one axis sets that axis alone on
		///     all of them: each keeps its other axes.
		/// </summary>
		private bool DrawTransform(Transform transform, IReadOnlyList<Behaviour> others = null)
		{
			var all = new List<Transform> { transform };
			if (others != null)
			{
				foreach (Behaviour other in others)
				{
					all.Add((Transform)other);
				}
			}

			// The serialized fields' paths: prefab overrides of the transform are marked like other fields.
			bool changedAny = DrawTransformRow(all, "Position", "##position", "m_LocalPosition", 0.05f, t => t.localPosition, (t, v) => t.localPosition = v);
			changedAny |= DrawTransformRow(all, "Rotation", "##rotation", "m_LocalRotation", 0.5f, EulerOf, (t, euler) =>
			{
				t.localRotation = QuaternionHelper.Euler(euler.X, euler.Y, euler.Z);
				eulerHints[t] = (t.localRotation, euler);
			});
			foreach (Transform shown in all)
			{
				eulerHints[shown] = (shown.localRotation, EulerOf(shown));
			}

			changedAny |= DrawTransformRow(all, "Scale", "##scale", "m_LocalScale", 0.02f, t => t.localScale, (t, v) => t.localScale = v);
			context.Path = "";
			if (eulerHints.Count > 4096)
			{
				eulerHints.Clear();
			}

			if (changedAny)
			{
				for (int i = 1; i < all.Count; i++)
				{
					editor.NotifyChanged(all[i].gameObject);
				}
			}

			return changedAny;
		}

		/// <summary>One vector row of the transform for every shown transform: the axes that differ show "—", an edited axis is set on all.</summary>
		private bool DrawTransformRow(List<Transform> all, string label, string id, string path, float speed, Func<Transform, Vector3> get, Action<Transform, Vector3> set)
		{
			Vector3 value = get(all[0]);
			bool[] mixed = null;
			for (int i = 1; i < all.Count; i++)
			{
				Vector3 other = get(all[i]);
				for (int axis = 0; axis < 3; axis++)
				{
					if (!other[axis].Equals(value[axis]))
					{
						mixed ??= new bool[3];
						mixed[axis] = true;
					}
				}
			}

			Span<float> values = stackalloc float[] { value.X, value.Y, value.Z };
			context.Path = path;
			FieldDrawer.Label(label, null, context);
			bool changed = EditorGui.DragFloats(id, values, mixed, speed);
			context.Track(changed);
			if (!changed)
			{
				return false;
			}

			for (int axis = 0; axis < 3; axis++)
			{
				if (values[axis].Equals(value[axis]))
				{
					continue;
				}

				// The edited axis on every transform; each keeps its other axes.
				foreach (Transform transform in all)
				{
					Vector3 current = get(transform);
					current[axis] = values[axis];
					set(transform, current);
				}
			}

			return true;
		}

		/// <summary>
		///     Several objects edited together (Unity's multi-object editing): the header's name, active flag, layer and
		///     static flag of all of them, then the components every one of them has (of a class several times on an object:
		///     the first with the first, and so on), drawn from the active object with "—" where the values differ; an edit
		///     goes to all of them as one undo step. Add Component adds to each; the materials of all their renderers follow.
		/// </summary>
		private void DrawMultiple(List<GameObject> objects)
		{
			GameObject go = objects[0];
			string selectionKey = string.Join(",", objects.ConvertAll(o => o.Id.ToString()));
			if (selectionKey != mixedSelection)
			{
				mixedSelection = selectionKey;
				mixedCache.Clear();
			}

			context.Overrides = null;
			context.OverrideTarget = 0;
			readOnly = objects.Exists(TempObjects.IsReadOnly);
			ImGui.BeginDisabled(readOnly);
			DrawMultipleHeader(objects);
			ImGui.EndDisabled();
			ImGui.TextDisabled(objects.Count + " objects selected, edited together" + (readOnly ? " (some are read-only: nothing is changed)" : ""));
			ImGui.Separator();

			// The components every object has: the k-th of a class on the active object with the k-th of that class on each other.
			bool someLeftOut = false;
			var shownCounts = new Dictionary<Type, int>();
			foreach (Behaviour component in new List<Behaviour>(go.Components))
			{
				Type type = component.GetType();
				shownCounts.TryGetValue(type, out int occurrence);
				shownCounts[type] = occurrence + 1;
				if (component is MissingComponent)
				{
					someLeftOut = true;
					continue;
				}

				var others = new List<Behaviour>(objects.Count - 1);
				for (int i = 1; i < objects.Count; i++)
				{
					Behaviour match = NthOfType(objects[i], type, occurrence);
					if (match == null)
					{
						break;
					}

					others.Add(match);
				}

				if (others.Count != objects.Count - 1)
				{
					someLeftOut = true;
					continue;
				}

				ImGui.PushID(component.Id.ToString());
				TestAnchors.PushScope(ComponentTitle(component));
				DrawComponent(go, component, others);
				TestAnchors.PopScope();
				ImGui.PopID();
			}

			context.UndoGroup = null;
			context.Mixed = null;

			// Components only some of the objects have (or more of them than the active object has) are not shown.
			for (int i = 1; i < objects.Count && !someLeftOut; i++)
			{
				var counts = new Dictionary<Type, int>();
				foreach (Behaviour component in objects[i].Components)
				{
					Type type = component.GetType();
					counts.TryGetValue(type, out int count);
					counts[type] = count + 1;
					shownCounts.TryGetValue(type, out int shown);
					if (count + 1 > shown)
					{
						someLeftOut = true;
						break;
					}
				}
			}

			if (someLeftOut)
			{
				ImGui.Spacing();
				ImGui.TextDisabled("Components that are only on some of the selected objects are not shown.");
			}

			if (!readOnly)
			{
				DrawAddComponent(objects);
			}

			ImGui.BeginDisabled(readOnly);
			DrawRendererMaterials(objects);
			ImGui.EndDisabled();
		}

		/// <summary>The <paramref name="occurrence" />-th component of exactly this class on the object (0: the first), or null.</summary>
		private static Behaviour NthOfType(GameObject go, Type type, int occurrence)
		{
			foreach (Behaviour component in go.Components)
			{
				if (component.GetType() == type && occurrence-- == 0)
				{
					return component;
				}
			}

			return null;
		}

		/// <summary>The header of several objects: active flag, name, layer and static flag, "—" where they differ.</summary>
		private void DrawMultipleHeader(List<GameObject> objects)
		{
			GameObject go = objects[0];
			bool activeMixed = objects.Exists(o => o.activeSelf != go.activeSelf);
			bool active = !activeMixed && go.activeSelf;
			ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, activeMixed);
			bool toggled = EditorGui.Checkbox("##active", ref active);
			ImGui.PopItemFlag();
			if (toggled)
			{
				var step = new GroupRecord { Name = (active ? "Activate " : "Deactivate ") + objects.Count + " objects" };
				foreach (GameObject target in objects)
				{
					if (target.activeSelf == active)
					{
						continue;
					}

					step.Records.Add(new GameObjectStateRecord { Name = step.Name, ObjectId = target.Id, NameBefore = target.Name, NameAfter = target.Name, ActiveBefore = target.activeSelf, ActiveAfter = active });
					target.SetActive(active);
					editor.NotifyChanged(target);
				}

				if (step.Records.Count > 0)
				{
					editor.Undo.Push(step);
				}
			}

			// The name: the shared one, or "—" when they differ; what is typed renames them all.
			bool namesMixed = objects.Exists(o => o.Name != go.Name);
			if (nameOwner != go)
			{
				nameOwner = go;
				nameBuffer = namesMixed ? "" : go.Name;
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
			if (namesMixed)
			{
				EditorGui.InputTextWithHint("##name", EditorGui.MixedValue, ref nameBuffer, 256);
			}
			else
			{
				EditorGui.InputText("##name", ref nameBuffer, 256);
			}

			if (ImGui.IsItemDeactivatedAfterEdit() && !string.IsNullOrWhiteSpace(nameBuffer))
			{
				var step = new GroupRecord { Name = "Rename " + objects.Count + " objects to " + nameBuffer };
				foreach (GameObject target in objects)
				{
					if (target.Name == nameBuffer)
					{
						continue;
					}

					step.Records.Add(new GameObjectStateRecord { Name = step.Name, ObjectId = target.Id, NameBefore = target.Name, NameAfter = nameBuffer, ActiveBefore = target.activeSelf, ActiveAfter = target.activeSelf });
					target.Name = nameBuffer;
				}

				if (step.Records.Count > 0)
				{
					editor.Undo.Push(step);
					editor.MarkDirty();
				}
			}
			else if (!ImGui.IsItemActive())
			{
				nameBuffer = namesMixed ? "" : go.Name;
			}

			// Layer: "—" when they differ; a layer picked goes to all (objects with children ask about them once).
			bool layerMixed = objects.Exists(o => o.layer != go.layer);
			ImGui.AlignTextToFramePadding();
			ImGui.TextDisabled("Layer");
			ImGui.SameLine();
			ImGui.SetNextItemWidth(160);
			if (EditorGui.BeginCombo("##layer", layerMixed ? EditorGui.MixedValue : LayerMask.DisplayName(go.layer)))
			{
				for (int layer = 0; layer < 32; layer++)
				{
					string name = LayerMask.LayerToName(layer);
					if (name.Length == 0 && layer != go.layer && layer < 8)
					{
						continue;
					}

					if (EditorGui.Selectable(LayerMask.DisplayName(layer), !layerMixed && layer == go.layer) && (layerMixed || layer != go.layer))
					{
						editor.RequestLayer(objects, layer);
					}
				}

				ImGui.Separator();
				if (EditorGui.Selectable("Add Layer..."))
				{
					editor.ShowProjectSettings(ProjectSettingsWindow.Tab.Layers);
				}

				EditorGui.EndCombo();
			}

			ImGui.SameLine(0, 16);
			bool staticMixed = objects.Exists(o => o.isStatic != go.isStatic);
			bool isStatic = !staticMixed && go.isStatic;
			ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, staticMixed);
			bool staticToggled = EditorGui.Checkbox("Static", ref isStatic);
			ImGui.PopItemFlag();
			if (staticToggled)
			{
				editor.RequestStatic(objects, isStatic);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(staticMixed ? "Static on some of the selected objects (click: all of them)" : "The objects do not move or change while the game runs (see the flag of one object)");
			}
		}

		private void DrawAddComponent(GameObject go)
		{
			DrawAddComponent(new List<GameObject> { go });
		}

		/// <summary>Add Component: the class chosen is added to each of the objects (one undo step).</summary>
		private void DrawAddComponent(List<GameObject> objects)
		{
			// Its own strip between the components and the materials (as in Unity): a rule above, room around, a
			// centered button taller than the fields.
			ImGui.Dummy(new NVector2(0f, 6f));
			NVector2 ruleStart = ImGui.GetCursorScreenPos();
			float avail = ImGui.GetContentRegionAvail().X;
			ImGui.GetWindowDrawList().AddLine(ruleStart, ruleStart + new NVector2(avail, 0f), ImGui.GetColorU32(ImGuiCol.Separator));
			ImGui.Dummy(new NVector2(0f, 10f));
			float width = Math.Min(avail, Math.Max(200f, avail * 0.6f));
			ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - width) * 0.5f);
			bool clicked = EditorGui.Button("Add Component", new NVector2(width, ImGui.GetFrameHeight() + 8f));
			NVector2 below = new NVector2((ImGui.GetItemRectMin().X + ImGui.GetItemRectMax().X) * 0.5f, ImGui.GetItemRectMax().Y);
			ImGui.Dummy(new NVector2(0f, 6f));
			if (clicked)
			{
				componentFilter = "";
				ImGui.OpenPopup("##add_component");
				// Under the button (not where the mouse is: a click from automation has it anywhere).
				ImGui.SetNextWindowPos(below, ImGuiCond.Always, new NVector2(0.5f, 0f));
			}

			if (!ImGui.BeginPopup("##add_component"))
			{
				return;
			}

			// The components by namespace (levels), all matches of a search flat.
			LevelList.SearchField("##filter", ref componentFilter, 280);
			List<Type> types = GetComponentTypes();
			componentLevels ??= LevelList.OfTypes(types);
			if (LevelList.BeginArea("##components", 280f))
			{
				int chosen = componentLevels.Draw(componentFilter);
				if (chosen >= 0)
				{
					Type selected = types[chosen];
					editor.RecordStructural("Add " + selected.Name + (objects.Count > 1 ? " to " + objects.Count + " objects" : ""), () =>
					{
						foreach (GameObject target in objects)
						{
							target.AddComponent(selected)?.Reset();
						}
					});
					ImGui.CloseCurrentPopup();
				}
			}

			ImGui.EndChild();
			ImGui.EndPopup();
		}

		private List<Type> GetComponentTypes()
		{
			int version = editor.Scripts?.Version ?? 0;
			if (componentTypes != null && componentTypesVersion == version)
			{
				return componentTypes;
			}

			componentTypesVersion = version;
			componentLevels = null;
			componentTypes = new List<Type>();
			foreach (Assembly assembly in TypeRegistry.Assemblies)
			{
				Type[] types;
				try
				{
					types = assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).ToArray();
				}

				foreach (Type type in types)
				{
					// Not the components nothing adds by hand: a static batch is the batcher's (a build, play mode).
					if (type.IsPublic && !type.IsAbstract && typeof(Behaviour).IsAssignableFrom(type) && type != typeof(Transform)
						&& type != typeof(MissingComponent) && type != typeof(StaticBatch) && type.GetConstructor(Type.EmptyTypes) != null)
					{
						componentTypes.Add(type);
					}
				}
			}

			componentTypes.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase));
			return componentTypes;
		}

		/// <summary>Drops all cached types (before the script assembly is unloaded).</summary>
		public void ClearTypeCaches()
		{
			PrefabInspector.Forget();
			componentTypes = null;
			componentLevels = null;
			eulerHints.Clear();
			nameOwner = null;
			mixedCache.Clear();
			mixedSelection = "";
		}

		/// <summary>The prefab instance overrides values of this component.</summary>
		private bool HasOverrides(Behaviour component)
		{
			if (context.Overrides == null || component.prefabInstance == null)
			{
				return false;
			}

			foreach (LELEngine.Serialization.PropertyModification modification in context.Overrides.Of(component.prefabSourceId))
			{
				return true;
			}

			return false;
		}

		private static void ResetFields(Behaviour component)
		{
			object defaults = Activator.CreateInstance(component.GetType());
			foreach (SerializedField field in SerializationUtility.GetFields(component.GetType()))
			{
				field.SetValue(component, field.GetValue(defaults));
			}
		}

		#endregion
	}
}
