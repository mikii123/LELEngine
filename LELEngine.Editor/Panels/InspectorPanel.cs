using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace LELEngine.Editor
{
	/// <summary>Components of the selected GameObject: serialized fields, enable / remove, Add Component.</summary>
	internal sealed class InspectorPanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private string nameBuffer = "";
		private GameObject nameOwner;
		private string componentFilter = "";
		private List<Type> componentTypes;
		private int componentTypesVersion = -1;

		// Euler angles last shown per transform: re-deriving them from the quaternion every frame flips between
		// equivalent angle sets while dragging.
		private readonly Dictionary<Transform, (Quaternion rotation, Vector3 euler)> eulerHints = new Dictionary<Transform, (Quaternion, Vector3)>();

		#endregion

		#region Constructors

		public InspectorPanel(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			if (ImGui.Begin("Inspector"))
			{
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
					context.Release();
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			GameObject go = editor.Selected;
			if (go == null || go.scene != editor.Scene)
			{
				ImGui.TextDisabled("Nothing selected");
				return;
			}

			context.Scene = editor.Scene;
			context.LabelWidth = Math.Max(110f, ImGui.GetContentRegionAvail().X * 0.38f);
			DrawObjectHeader(go);
			ImGui.Separator();

			foreach (Behaviour component in new List<Behaviour>(go.Components))
			{
				ImGui.PushID(component.Id.ToString());
				DrawComponent(go, component);
				ImGui.PopID();
			}

			ImGui.Spacing();
			DrawAddComponent(go);
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

			ImGui.SameLine();
			ImGui.SetNextItemWidth(-1);
			ImGui.InputText("##name", ref nameBuffer, 256);
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
		}

		private void DrawComponent(GameObject go, Behaviour component)
		{
			context.UndoTarget = component;
			Type type = component.GetType();
			string title = component is MissingComponent missing
				? "Missing script: " + missing.TypeName
				: SerializationUtility.NicifyName(type.Name);

			ImGui.SetNextItemAllowOverlap();
			bool open = ImGui.CollapsingHeader(title + "##header", ImGuiTreeNodeFlags.DefaultOpen);
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
			else
			{
				changed = FieldDrawer.DrawFields(component, context);
			}

			if (changed)
			{
				editor.NotifyChanged(go);
				editor.Scene.Validate(component);
			}

			ImGui.Unindent(4);
			ImGui.Spacing();
		}

		private bool DrawTransform(Transform transform)
		{
			bool changedAny = false;

			Vector3 position = transform.localPosition;
			NVector3 p = new NVector3(position.X, position.Y, position.Z);
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

			Vector3 scale = transform.localScale;
			NVector3 s = new NVector3(scale.X, scale.Y, scale.Z);
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
			if (ImGui.Button("Add Component", new NVector2(-1, 0)))
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
					editor.RecordStructural("Add " + type.Name, () => go.AddComponent(selected));
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
			componentTypes = null;
			eulerHints.Clear();
			nameOwner = null;
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
