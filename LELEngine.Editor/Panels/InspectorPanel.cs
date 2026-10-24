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
	internal sealed class InspectorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;

		// The renderers' material editors below the components (their own undo target and field paths).
		private readonly InspectorContext materialContext;
		private string nameBuffer = "";
		private GameObject nameOwner;
		private string componentFilter = "";
		private List<Type> componentTypes;
		private int componentTypesVersion = -1;

		// Euler angles last shown per transform: re-deriving them from the quaternion every frame flips between
		// equivalent angle sets while dragging.
		private readonly Dictionary<Transform, (Quaternion rotation, Vector3 euler)> eulerHints = new Dictionary<Transform, (Quaternion, Vector3)>();

		// The lock (as in Unity): the inspector keeps showing this object (by id: it survives scene reloads) or asset
		// while other things are selected, e.g. to drag several selected assets on to one of its lists.
		private bool locked;
		private ulong lockedObjectId;
		private string lockedAsset;

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

		/// <summary>Test runs: the scroll position to set on the next draw (screenshots of long inspectors).</summary>
		public static float? TestScroll;

		/// <summary>Test runs: locks the inspector on what it shows at the next draw.</summary>
		public static bool TestLock;

		/// <summary>The GameObject the inspector shows (the selection, or the locked object), null when none.</summary>
		public GameObject Shown { get; private set; }

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

		/// <summary>An asset file or folder moved: a lock on an asset follows it.</summary>
		public void AssetMoved(string fromPath, string toPath)
		{
			lockedAsset = AssetDatabase.MovedPath(lockedAsset, fromPath, toPath) ?? lockedAsset;
		}

		/// <summary>The inspector keeps showing what it shows now (or follows the selection again).</summary>
		public void Lock(bool value)
		{
			locked = value && (editor.Selected != null || editor.SelectedAsset != null || Shown != null);
			if (!locked)
			{
				lockedObjectId = 0;
				lockedAsset = null;
				return;
			}

			GameObject target = Shown ?? editor.Selected;
			lockedObjectId = target?.Id ?? 0;
			lockedAsset = target == null ? editor.SelectedAsset : null;
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			GameObject go = editor.Selected;
			string asset = editor.SelectedAsset;
			if (locked)
			{
				// The locked object of the current scene (found again after reloads), else the locked asset; gone: unlocked.
				GameObject lockedObject = lockedObjectId != 0 ? editor.Scene?.FindObjectById(lockedObjectId) : null;
				if (lockedObject != null)
				{
					go = lockedObject;
					asset = null;
				}
				else if (lockedAsset != null && System.IO.File.Exists(AssetDatabase.ToAbsolute(lockedAsset)))
				{
					go = null;
					asset = lockedAsset;
				}
				else
				{
					Lock(false);
				}
			}

			if (go == null || go.scene != editor.Scene)
			{
				if (asset != null)
				{
					context.Scene = editor.Scene;
					context.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
					ImGui.PushID(asset);
					AssetInspectors.Draw(asset, context);
					ImGui.PopID();
					return;
				}

				ImGui.TextDisabled("Nothing selected");
				return;
			}

			Shown = go;

			context.Scene = editor.Scene;
			context.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
			// A prefab instance's object: its overrides are marked (bold, the bar) and can be applied or reverted.
			context.Overrides = PrefabInspector.OverridesOf(go.prefabInstance, editor.SceneVersion);
			DrawObjectHeader(go);
			ImGui.Separator();
			PrefabInspector.DrawPrefabBar(editor, go, context);

			foreach (Behaviour component in new List<Behaviour>(go.Components))
			{
				ImGui.PushID(component.Id.ToString());
				DrawComponent(go, component);
				ImGui.PopID();
			}

			PrefabInspector.DrawRemovedComponents(editor, go, context);
			DrawAddComponent(go);
			DrawRendererMaterials(go);
		}

		/// <summary>
		///     The materials of the object's renderers below its components, each with its full editor (as in Unity): an
		///     imported model's materials are edited where they are used. Edits change the material asset, so every
		///     renderer using it follows. Engine materials and materials made at runtime are shown, not edited.
		/// </summary>
		private void DrawRendererMaterials(GameObject go)
		{
			var materials = new List<Material>();
			foreach (Behaviour component in go.Components)
			{
				IReadOnlyList<Material> slots = component switch
				{
					MeshRenderer renderer => renderer.Materials,
					TrailRenderer trail => trail.Materials,
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
						materials.Add(material);
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
				MaterialSection.Draw(materials[i], materialContext, "material:" + (materials[i].name ?? i.ToString()), true);
				ImGui.PopID();
				ImGui.Dummy(new NVector2(0f, 4f));
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
			if (ImGui.Checkbox("##active", ref active))
			{
				editor.Undo.Push(new GameObjectStateRecord { Name = (active ? "Activate " : "Deactivate ") + go.Name, ObjectId = go.Id, NameBefore = go.Name, NameAfter = go.Name, ActiveBefore = go.activeSelf, ActiveAfter = active });
				go.SetActive(active);
				editor.NotifyChanged(go);
			}

			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.ActiveProperty);
			ImGui.SameLine();
			// Room for the lock button at the end of the line.
			ImGui.SetNextItemWidth(-(ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.X));
			ImGui.InputText("##name", ref nameBuffer, 256);
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
			if (ImGui.BeginCombo("##layer", LayerMask.DisplayName(go.layer)))
			{
				for (int layer = 0; layer < 32; layer++)
				{
					string name = LayerMask.LayerToName(layer);
					if (name.Length == 0 && layer != go.layer && layer < 8)
					{
						continue;
					}

					if (ImGui.Selectable(LayerMask.DisplayName(layer), layer == go.layer) && layer != go.layer)
					{
						int previous = go.layer;
						editor.RecordStructural("Change layer of " + go.Name, () => go.layer = layer);
						editor.NotifyChanged(go);
					}
				}

				ImGui.Separator();
				if (ImGui.Selectable("Add Layer..."))
				{
					editor.ShowProjectSettings(ProjectSettingsWindow.Tab.Layers);
				}

				ImGui.EndCombo();
			}

			PrefabInspector.MarkHeaderProperty(context, go, LELEngine.Serialization.PropertyModification.LayerProperty);
		}

		private void DrawComponent(GameObject go, Behaviour component)
		{
			context.UndoTarget = component;
			// Overrides of this component (its id in the prefab); none for components added to the instance.
			context.OverrideTarget = component.prefabInstance != null ? component.prefabSourceId : 0;
			context.Path = "";
			Type type = component.GetType();
			string title = component is MissingComponent missing
				? "Missing script: " + missing.TypeName
				: SerializationUtility.NicifyName(type.Name);

			bool added = PrefabUtility.IsAddedComponent(component);
			bool overridden = added || HasOverrides(component);
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
			bool open = ImGui.CollapsingHeader((added ? PrefabInspector.AddedTitlePadding() : "") + iconPadding + title + "##header", ImGuiTreeNodeFlags.DefaultOpen);
			EditorFonts.Pop(bold);
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
			PrefabInspector.DecorateComponentHeader(editor, component, context);

			// The header drags this very component on to object fields (of its type, its GameObject's or an interface it implements).
			if (!(component is MissingComponent) && ImGui.BeginDragDropSource())
			{
				DragAndDrop.SetPayload(DragAndDrop.ReferencePayload, DragItem.FromComponent(component));
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginPopupContextItem("##component_context"))
			{
				if (!(component is Transform) && ImGui.MenuItem("Remove Component"))
				{
					editor.RecordStructural("Remove " + type.Name, () => GameObject.DestroyImmediate(component));
					ImGui.EndPopup();
					return;
				}

				if (!(component is Transform) && !(component is MissingComponent) && ImGui.MenuItem("Reset"))
				{
					editor.Edits.Begin(component);
					ResetFields(component);
					editor.Edits.RequestCommit();
					editor.NotifyChanged(go);
				}

				// The project's script of the component (engine components have none).
				string script = component is MissingComponent ? null : ScriptFiles.Find(type);
				if (script != null)
				{
					ImGui.Separator();
					if (ImGui.MenuItem("Edit Script"))
					{
						ScriptFiles.Open(type);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Open " + script + " in the external script editor");
					}
				}

				PrefabInspector.DrawComponentMenu(editor, component, context);
				ImGui.EndPopup();
			}

			if (!(component is Transform) && !(component is MissingComponent))
			{
				ImGui.SameLine(ImGui.GetWindowWidth() - 34f);
				bool enabled = component.enabled;
				if (ImGui.Checkbox("##enabled", ref enabled))
				{
					editor.Edits.Begin(component);
					component.enabled = enabled;
					editor.Edits.RequestCommit();
					editor.NotifyChanged(go);
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Enabled");
				}
			}

			if (!open)
			{
				return;
			}

			if (!kind.HasValue)
			{
				DrawComponentBody(go, component);
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
				DrawComponentBody(go, component);
			}
			finally
			{
				draw.ChannelsSetCurrent(0);
				NVector4 tint = kind.Value;
				draw.AddRectFilled(bodyMin, new NVector2(right, ImGui.GetCursorScreenPos().Y - ImGui.GetStyle().ItemSpacing.Y), ImGui.GetColorU32(new NVector4(tint.X, tint.Y, tint.Z, 0.045f)));
				draw.ChannelsMerge();
			}
		}

		private void DrawComponentBody(GameObject go, Behaviour component)
		{
			ImGui.Indent(4);
			bool changed;
			if (component is Transform transform)
			{
				changed = DrawTransform(transform);
			}
			else if (component is MissingComponent missingComponent)
			{
				ImGui.TextColored(EditorStyle.Warning, "The script class was not found (renamed, deleted, or the scripts do not compile).");
				ImGui.TextDisabled("Its data is kept and saved with the scene.");
				changed = false;
			}
			else if (editor.Extensions.GetEditor(component) is Editor custom)
			{
				// [CustomEditor] of the project's editor scripts: it draws the whole body.
				changed = editor.Extensions.DrawInspector(custom, context);
			}
			else
			{
				changed = FieldDrawer.DrawFields(component, context);
				changed |= CustomInspectors.Draw(component, context);
			}

			if (changed)
			{
				editor.NotifyChanged(go);
				editor.Scene.Validate(component);
			}

			ImGui.Unindent(4);
			ImGui.Spacing();
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

		private bool DrawTransform(Transform transform)
		{
			bool changedAny = false;

			Vector3 position = transform.localPosition;
			NVector3 p = new NVector3(position.X, position.Y, position.Z);
			// The serialized fields' paths: prefab overrides of the transform are marked like other fields.
			context.Path = "m_LocalPosition";
			FieldDrawer.Label("Position", null, context);
			bool changed = ImGui.DragFloat3("##position", ref p, 0.05f);
			context.Track(changed);
			if (changed)
			{
				transform.localPosition = new Vector3(p.X, p.Y, p.Z);
				changedAny = true;
			}

			Quaternion rotation = transform.localRotation;
			Vector3 euler;
			(Quaternion rotation, Vector3 euler) hint;
			if (eulerHints.TryGetValue(transform, out hint) && hint.rotation == rotation)
			{
				euler = hint.euler;
			}
			else
			{
				euler = FieldDrawer.ToEuler(rotation);
			}

			NVector3 r = new NVector3(euler.X, euler.Y, euler.Z);
			context.Path = "m_LocalRotation";
			FieldDrawer.Label("Rotation", null, context);
			changed = ImGui.DragFloat3("##rotation", ref r, 0.5f);
			context.Track(changed);
			if (changed)
			{
				euler = new Vector3(r.X, r.Y, r.Z);
				transform.localRotation = QuaternionHelper.Euler(euler.X, euler.Y, euler.Z);
				changedAny = true;
			}

			eulerHints[transform] = (transform.localRotation, euler);
			context.Path = "";

			Vector3 scale = transform.localScale;
			NVector3 s = new NVector3(scale.X, scale.Y, scale.Z);
			context.Path = "m_LocalScale";
			FieldDrawer.Label("Scale", null, context);
			changed = ImGui.DragFloat3("##scale", ref s, 0.02f);
			context.Track(changed);
			if (changed)
			{
				transform.localScale = new Vector3(s.X, s.Y, s.Z);
				changedAny = true;
			}

			if (eulerHints.Count > 4096)
			{
				eulerHints.Clear();
			}

			return changedAny;
		}

		private void DrawAddComponent(GameObject go)
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
			bool clicked = ImGui.Button("Add Component", new NVector2(width, ImGui.GetFrameHeight() + 8f));
			ImGui.Dummy(new NVector2(0f, 6f));
			if (clicked)
			{
				componentFilter = "";
				ImGui.OpenPopup("##add_component");
			}

			if (!ImGui.BeginPopup("##add_component"))
			{
				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(280);
			ImGui.InputTextWithHint("##filter", "Search", ref componentFilter, 128);
			foreach (Type type in GetComponentTypes())
			{
				string name = type.FullName ?? type.Name;
				if (componentFilter.Length > 0 && name.IndexOf(componentFilter, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				if (ImGui.Selectable(name))
				{
					Type selected = type;
					editor.RecordStructural("Add " + type.Name, () => go.AddComponent(selected)?.Reset());
					ImGui.CloseCurrentPopup();
				}
			}

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
					if (type.IsPublic && !type.IsAbstract && typeof(Behaviour).IsAssignableFrom(type) && type != typeof(Transform)
						&& type != typeof(MissingComponent) && type.GetConstructor(Type.EmptyTypes) != null)
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
			eulerHints.Clear();
			nameOwner = null;
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
