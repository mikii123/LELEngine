using System;

namespace LELEngine.Editor
{
	/// <summary>
	///     Makes an <see cref="Editor" /> the inspector of a component type (Unity's [CustomEditor]). Editor classes live
	///     in an Editor folder of the project (Assets/**/Editor/*.cs); they are found every time the scripts load.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
	public sealed class CustomEditorAttribute : Attribute
	{
		#region Constructors

		/// <param name="inspectedType">The component type the editor draws.</param>
		/// <param name="editorForChildClasses">Also draw components derived from it (unless they have an editor of their own).</param>
		public CustomEditorAttribute(Type inspectedType, bool editorForChildClasses = false)
		{
			InspectedType = inspectedType;
			EditorForChildClasses = editorForChildClasses;
		}

		#endregion

		#region PublicFields

		public Type InspectedType { get; }
		public bool EditorForChildClasses { get; }

		#endregion
	}

	/// <summary>
	///     Custom inspector of a component type, drawn with Dear ImGui (Hexa.NET.ImGui) inside the component's section of
	///     the Inspector panel; the header (name, enabled toggle, context menu) stays the editor's. Mark the class with
	///     <see cref="CustomEditorAttribute" /> and put it in an Editor folder.
	///     <para>
	///         One instance per inspected component lives while the component's GameObject stays selected:
	///         <see cref="OnEnable" /> runs when it is created, <see cref="OnDisable" /> when the selection changes, the
	///         component goes away or the scripts reload. Fields of the editor are its own UI state (foldouts, tabs).
	///     </para>
	///     <para>
	///         Values edited through <see cref="EditorGUILayout" /> (or <see cref="DrawDefaultInspector" />) get undo and
	///         mark the scene dirty by themselves. Before writing a value from a raw ImGui widget, call
	///         <see cref="Undo.RecordObject" /> with the target.
	///     </para>
	/// </summary>
	public abstract class Editor
	{
		#region PublicFields

		/// <summary>The inspected component.</summary>
		public Behaviour target { get; internal set; }

		#endregion

		#region InternalFields

		/// <summary>Message of the last exception (logged once per message, shown in the inspector).</summary>
		internal string LastError;

		#endregion

		#region PublicMethods

		/// <summary>Draws the component's inspector (default: every serialized field, as without a custom editor).</summary>
		public virtual void OnInspectorGUI()
		{
			DrawDefaultInspector();
		}

		/// <summary>
		///     Called while the scene view renders and the component's GameObject is selected: draw gizmos with
		///     <see cref="Rendering.DebugDraw" />. The view renders on demand (after changes), not every frame.
		/// </summary>
		public virtual void OnSceneGUI()
		{
		}

		/// <summary>Draws every serialized field of the target the way the inspector does without a custom editor.</summary>
		/// <returns>True when a value changed.</returns>
		public bool DrawDefaultInspector()
		{
			return EditorGUILayout.DefaultFields(target);
		}

		#endregion

		#region ProtectedMethods

		protected virtual void OnEnable()
		{
		}

		protected virtual void OnDisable()
		{
		}

		/// <summary>The target's serialized field <paramref name="fieldName" /> with the inspector's own widget.</summary>
		protected bool PropertyField(string fieldName, string label = null)
		{
			return EditorGUILayout.PropertyField(target, fieldName, label);
		}

		#endregion

		#region InternalMethods

		internal void InvokeEnable()
		{
			OnEnable();
		}

		internal void InvokeDisable()
		{
			OnDisable();
		}

		#endregion
	}
}
