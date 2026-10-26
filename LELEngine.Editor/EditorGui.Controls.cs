using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using LELEngine.Editor.Automation;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's interactive controls: ImGui's widgets, same signatures, that are also UI elements for automation.
	///     Each records itself (<see cref="TestAnchors.RecordElement" />: "button:Add Component", "checkbox:Always Refresh",
	///     "item:Cube", "header:Transform", "tree:Folder", "tab:Build", "combo:Shader", "input:search") with its ImGui id, and
	///     reacts to an automation request (<see cref="UIAutomation" />) as if clicked. Editor code draws its controls through
	///     these, not through ImGui directly, so every one of them can be invoked without the mouse.
	/// </summary>
	internal static partial class EditorGui
	{
		#region PublicMethods

		public static bool Button(string label)
		{
			return Button(label, NVector2.Zero);
		}

		public static bool Button(string label, NVector2 size)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.Button(label, size);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("button", label);
			return pressed;
		}

		public static bool SmallButton(string label)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.SmallButton(label);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("button", label);
			return pressed;
		}

		public static bool InvisibleButton(string id, NVector2 size)
		{
			return InvisibleButton(id, size, ImGuiButtonFlags.None);
		}

		public static bool InvisibleButton(string id, NVector2 size, ImGuiButtonFlags flags)
		{
			bool activated = UIAutomation.BeginActivation(id);
			bool pressed = ImGui.InvisibleButton(id, size, flags);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("button", id);
			return pressed;
		}

		public static bool ArrowButton(string id, ImGuiDir direction)
		{
			bool activated = UIAutomation.BeginActivation(id);
			bool pressed = ImGui.ArrowButton(id, direction);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("button", id);
			return pressed;
		}

		public static bool Checkbox(string label, ref bool value)
		{
			string request = UIAutomation.ValueRequested(ImGui.GetID(label));
			bool activated = UIAutomation.BeginActivation(label);
			bool changed = ImGui.Checkbox(label, ref value);
			UIAutomation.EndActivation(activated);
			if (request != null)
			{
				if (ValueText.TryParse(request, out bool set))
				{
					bool differs = set != value;
					value = set;
					UIAutomation.ValueApplied(differs);
					changed |= differs;
				}
				else
				{
					UIAutomation.ValueApplied(false, "not true or false");
				}
			}

			TestAnchors.RecordElement("checkbox", label, 0, TestAnchors.Enabled ? ValueText.Format(value) : null);
			return changed;
		}

		public static bool RadioButton(string label, bool active)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.RadioButton(label, active);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("radio", label);
			return pressed;
		}

		public static bool RadioButton(string label, ref int value, int buttonValue)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.RadioButton(label, ref value, buttonValue);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("radio", label);
			return pressed;
		}

		public static bool Selectable(string label)
		{
			return Selectable(label, false, ImGuiSelectableFlags.None, NVector2.Zero);
		}

		public static bool Selectable(string label, bool selected)
		{
			return Selectable(label, selected, ImGuiSelectableFlags.None, NVector2.Zero);
		}

		public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags)
		{
			return Selectable(label, selected, flags, NVector2.Zero);
		}

		public static bool Selectable(string label, bool selected, NVector2 size)
		{
			return Selectable(label, selected, ImGuiSelectableFlags.None, size);
		}

		public static bool Selectable(string label, ImGuiSelectableFlags flags)
		{
			return Selectable(label, false, flags, NVector2.Zero);
		}

		public static bool Selectable(string label, ImGuiSelectableFlags flags, NVector2 size)
		{
			return Selectable(label, false, flags, size);
		}

		public static bool Selectable(string label, NVector2 size)
		{
			return Selectable(label, false, ImGuiSelectableFlags.None, size);
		}

		public static bool Selectable(string label, bool selected, ImGuiSelectableFlags flags, NVector2 size)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.Selectable(label, selected, flags, size);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("item", label);
			return pressed;
		}

		public static bool Selectable(string label, ref bool selected)
		{
			return Selectable(label, ref selected, ImGuiSelectableFlags.None, NVector2.Zero);
		}

		public static bool Selectable(string label, ref bool selected, ImGuiSelectableFlags flags)
		{
			return Selectable(label, ref selected, flags, NVector2.Zero);
		}

		public static bool Selectable(string label, ref bool selected, NVector2 size)
		{
			return Selectable(label, ref selected, ImGuiSelectableFlags.None, size);
		}

		public static bool Selectable(string label, ref bool selected, ImGuiSelectableFlags flags, NVector2 size)
		{
			bool activated = UIAutomation.BeginActivation(label);
			bool pressed = ImGui.Selectable(label, ref selected, flags, size);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("item", label);
			return pressed;
		}

		public static bool CollapsingHeader(string label)
		{
			return CollapsingHeader(label, ImGuiTreeNodeFlags.None);
		}

		public static bool CollapsingHeader(string label, ImGuiTreeNodeFlags flags)
		{
			bool activated = UIAutomation.BeginActivation(label, Toggles(flags));
			bool open = ImGui.CollapsingHeader(label, flags);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("header", label);
			return open;
		}

		public static bool CollapsingHeader(string label, ref bool visible)
		{
			return CollapsingHeader(label, ref visible, ImGuiTreeNodeFlags.None);
		}

		public static bool CollapsingHeader(string label, ref bool visible, ImGuiTreeNodeFlags flags)
		{
			bool activated = UIAutomation.BeginActivation(label, Toggles(flags));
			bool open = ImGui.CollapsingHeader(label, ref visible, flags);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("header", label);
			return open;
		}

		public static bool TreeNodeEx(string label)
		{
			return TreeNodeEx(label, ImGuiTreeNodeFlags.None);
		}

		public static bool TreeNodeEx(string label, ImGuiTreeNodeFlags flags)
		{
			bool activated = UIAutomation.BeginActivation(label, Toggles(flags));
			bool open = ImGui.TreeNodeEx(label, flags);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("tree", label);
			return open;
		}

		/// <summary>A tree node with its own id (<paramref name="id" />) and shown text (<paramref name="text" />: no printf formatting).</summary>
		public static bool TreeNodeEx(string id, ImGuiTreeNodeFlags flags, string text)
		{
			bool activated = UIAutomation.BeginActivation(id, Toggles(flags));
			bool open = ImGui.TreeNodeEx(id, flags, text);
			UIAutomation.EndActivation(activated);
			TestAnchors.RecordElement("tree", string.IsNullOrEmpty(text) ? id : text + "##" + id);
			return open;
		}

		public static bool BeginTabItem(string label)
		{
			return BeginTabItem(label, ImGuiTabItemFlags.None);
		}

		public static bool BeginTabItem(string label, ImGuiTabItemFlags flags)
		{
			// A tab asked for by automation is selected (ImGui's way of choosing a tab from code).
			string name = TestAnchors.PeekName("tab", label);
			bool open = ImGui.BeginTabItem(label, flags | (UIAutomation.TabRequested(name) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None));
			TestAnchors.RecordElement("tab", label);
			return open;
		}

		public static bool BeginTabItem(string label, ref bool open)
		{
			return BeginTabItem(label, ref open, ImGuiTabItemFlags.None);
		}

		public static bool BeginTabItem(string label, ref bool open, ImGuiTabItemFlags flags)
		{
			string name = TestAnchors.PeekName("tab", label);
			bool shown = ImGui.BeginTabItem(label, ref open, flags | (UIAutomation.TabRequested(name) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None));
			TestAnchors.RecordElement("tab", label);
			return shown;
		}

		public static bool BeginCombo(string label, string preview)
		{
			return BeginCombo(label, preview, ImGuiComboFlags.None);
		}

		/// <summary>
		///     ImGui.BeginCombo; while open, its items are part of it (<see cref="TestAnchors.PushScope" />: "item:Mover/Mode/Fast"):
		///     close it with <see cref="EndCombo" />, not ImGui.EndCombo.
		/// </summary>
		public static bool BeginCombo(string label, string preview, ImGuiComboFlags flags)
		{
			// Recorded before the call: an open combo's popup is the current window (and its last item) after it. Its frame
			// is where the cursor is, the item's width wide (plus the label's, when shown).
			uint id = ImGui.GetID(label);
			if (TestAnchors.Enabled)
			{
				NVector2 min = ImGui.GetCursorScreenPos();
				string shown = TestAnchors.VisibleText(label);
				float labelWidth = label.StartsWith("##", StringComparison.Ordinal) || shown.Length == 0 ? 0f : ImGui.GetStyle().ItemInnerSpacing.X + ImGui.CalcTextSize(shown).X;
				TestAnchors.RecordElement("combo", label, id, preview, min, min + new NVector2(ImGui.CalcItemWidth() + labelWidth, ImGui.GetFrameHeight()));
			}

			bool activated = UIAutomation.BeginActivation(label);
			bool open = ImGui.BeginCombo(label, preview, flags);
			UIAutomation.EndActivation(activated);
			if (open)
			{
				TestAnchors.PushScope(TestAnchors.LastElementText ?? label);
			}

			return open;
		}

		public static void EndCombo()
		{
			TestAnchors.PopScope();
			ImGui.EndCombo();
		}

		public static bool InputText(string label, ref string text, nuint maxLength)
		{
			return InputText(label, ref text, maxLength, ImGuiInputTextFlags.None);
		}

		public static bool InputText(string label, ref string text, nuint maxLength, ImGuiInputTextFlags flags)
		{
			string request = UIAutomation.ValueRequested(ImGui.GetID(label));
			bool activated = UIAutomation.BeginActivation(label);
			bool changed = ImGui.InputText(label, ref text, maxLength, flags);
			UIAutomation.EndActivation(activated);
			return FinishText(label, request, ref text, maxLength, changed);
		}

		public static unsafe bool InputText(string label, ref string text, nuint maxLength, ImGuiInputTextFlags flags, ImGuiInputTextCallback callback, void* userData = null)
		{
			string request = UIAutomation.ValueRequested(ImGui.GetID(label));
			bool activated = UIAutomation.BeginActivation(label);
			bool changed = ImGui.InputText(label, ref text, maxLength, flags, callback, userData);
			UIAutomation.EndActivation(activated);
			return FinishText(label, request, ref text, maxLength, changed);
		}

		public static bool InputTextWithHint(string label, string hint, ref string text, nuint maxLength)
		{
			return InputTextWithHint(label, hint, ref text, maxLength, ImGuiInputTextFlags.None);
		}

		public static bool InputTextWithHint(string label, string hint, ref string text, nuint maxLength, ImGuiInputTextFlags flags)
		{
			string request = UIAutomation.ValueRequested(ImGui.GetID(label));
			bool activated = UIAutomation.BeginActivation(label);
			bool changed = ImGui.InputTextWithHint(label, hint, ref text, maxLength, flags);
			UIAutomation.EndActivation(activated);
			return FinishText(label, request, ref text, maxLength, changed);
		}

		public static bool InputTextMultiline(string label, ref string text, nuint maxLength, NVector2 size)
		{
			return InputTextMultiline(label, ref text, maxLength, size, ImGuiInputTextFlags.None);
		}

		public static bool InputTextMultiline(string label, ref string text, nuint maxLength, NVector2 size, ImGuiInputTextFlags flags)
		{
			string request = UIAutomation.ValueRequested(ImGui.GetID(label));
			bool activated = UIAutomation.BeginActivation(label);
			bool changed = ImGui.InputTextMultiline(label, ref text, maxLength, size, flags);
			UIAutomation.EndActivation(activated);
			return FinishText(label, request, ref text, maxLength, changed);
		}

		// ---------------------------------------------------------------- number, vector and color fields
		// Elements "input:Label" ("color:Label"), named like the inspector's label column; automation sets their value from
		// text (UIAutomation.Action.SetValue, ValueText). Vectors and colors are groups: their element is the label's id.

		public static bool DragFloat(string label, ref float v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, string format = "%.3f", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragFloat(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		public static bool DragFloat2(string label, ref NVector2 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, string format = "%.3f", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragFloat2(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref Unsafe.As<NVector2, float>(ref v), 2), changed);
		}

		public static bool DragFloat3(string label, ref NVector3 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, string format = "%.3f", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragFloat3(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref Unsafe.As<NVector3, float>(ref v), 3), changed);
		}

		public static bool DragFloat4(string label, ref NVector4 v, float vSpeed = 1f, float vMin = 0f, float vMax = 0f, string format = "%.3f", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragFloat4(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref Unsafe.As<NVector4, float>(ref v), 4), changed);
		}

		public static bool DragInt(string label, ref int v, float vSpeed = 1f, int vMin = 0, int vMax = 0, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragInt(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		/// <summary>ImGui.DragInt2: <paramref name="v" /> is the first of two ints (an array's element 0).</summary>
		public static bool DragInt2(string label, ref int v, float vSpeed = 1f, int vMin = 0, int vMax = 0, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragInt2(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 2), changed);
		}

		public static bool DragInt3(string label, ref int v, float vSpeed = 1f, int vMin = 0, int vMax = 0, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragInt3(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 3), changed);
		}

		public static bool DragInt4(string label, ref int v, float vSpeed = 1f, int vMin = 0, int vMax = 0, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.DragInt4(label, ref v, vSpeed, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 4), changed);
		}

		public static bool SliderFloat(string label, ref float v, float vMin, float vMax, string format = "%.3f", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.SliderFloat(label, ref v, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		public static bool SliderInt(string label, ref int v, int vMin, int vMax, string format = "%d", ImGuiSliderFlags flags = ImGuiSliderFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.SliderInt(label, ref v, vMin, vMax, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		public static bool InputInt(string label, ref int v, int step = 1, int stepFast = 100, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.InputInt(label, ref v, step, stepFast, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		public static bool InputFloat(string label, ref float v, float step = 0f, float stepFast = 0f, string format = "%.3f", ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.InputFloat(label, ref v, step, stepFast, format, flags);
			return FinishNumbers("input", label, id, request, MemoryMarshal.CreateSpan(ref v, 1), changed);
		}

		public static bool ColorEdit3(string label, ref NVector3 color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.ColorEdit3(label, ref color, flags);
			return FinishNumbers("color", label, id, request, MemoryMarshal.CreateSpan(ref Unsafe.As<NVector3, float>(ref color), 3), changed, true);
		}

		public static bool ColorEdit4(string label, ref NVector4 color, ImGuiColorEditFlags flags = ImGuiColorEditFlags.None)
		{
			string request = ValueRequest(label, out uint id);
			bool changed = ImGui.ColorEdit4(label, ref color, flags);
			return FinishNumbers("color", label, id, request, MemoryMarshal.CreateSpan(ref Unsafe.As<NVector4, float>(ref color), 4), changed, true);
		}

		/// <summary>ImGui.IsItemClicked, also true when automation clicks the last item.</summary>
		public static bool IsItemClicked()
		{
			return IsItemClicked(ImGuiMouseButton.Left);
		}

		public static bool IsItemClicked(ImGuiMouseButton button)
		{
			return ImGui.IsItemClicked(button) || UIAutomation.ItemClicked(ImGui.GetItemID(), (int)button);
		}

		/// <summary>Automation clicked the last item in this frame (code that acts on a click's release ends the click at once then).</summary>
		public static bool IsItemInvoked()
		{
			return UIAutomation.WasClicked(ImGui.GetItemID());
		}

		/// <summary>The last item was double-clicked (hovered and a double click), or automation double-clicks it.</summary>
		public static bool IsItemDoubleClicked()
		{
			return (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)) || UIAutomation.ItemDoubleClicked(ImGui.GetItemID());
		}

		/// <summary>ImGui.IsMouseDoubleClicked inside a click of the last item (also true when automation double-clicks it).</summary>
		public static bool IsMouseDoubleClicked(ImGuiMouseButton button)
		{
			return ImGui.IsMouseDoubleClicked(button) || (button == ImGuiMouseButton.Left && UIAutomation.ItemDoubleClicked(ImGui.GetItemID()));
		}

		/// <summary>ImGui.BeginPopupContextItem; automation opens the menu without a right click (<see cref="UIAutomation" />).</summary>
		public static bool BeginPopupContextItem(string id)
		{
			return BeginPopupContextItem(id, ImGuiPopupFlags.MouseButtonRight);
		}

		public static bool BeginPopupContextItem(string id, ImGuiPopupFlags flags)
		{
			if (UIAutomation.ContextRequested(ImGui.GetItemID()))
			{
				// The id BeginPopupContextItem opens: its own string's, else the last item's.
				if (id != null)
				{
					ImGui.OpenPopup(id, flags & ~ImGuiPopupFlags.MouseButtonMask);
				}
				else
				{
					ImGuiP.OpenPopupEx(ImGui.GetItemID(), flags & ~ImGuiPopupFlags.MouseButtonMask);
				}
			}

			return ImGui.BeginPopupContextItem(id, flags);
		}

		#endregion

		#region PrivateMethods

		/// <summary>The id of a value widget about to be drawn, and the value automation sets for it (null: none).</summary>
		private static string ValueRequest(string label, out uint id)
		{
			id = ImGui.GetID(label);
			return UIAutomation.ValueRequested(id);
		}

		/// <summary>After a text field: the text automation sets (at most maxLength - 1 characters), then the element.</summary>
		private static bool FinishText(string label, string request, ref string text, nuint maxLength, bool changed)
		{
			if (request != null)
			{
				string set = maxLength > 0 && (ulong)request.Length >= maxLength ? request.Substring(0, (int)maxLength - 1) : request;
				bool differs = set != text;
				text = set;
				UIAutomation.ValueApplied(differs);
				changed |= differs;
			}

			TestAnchors.RecordElement("input", label, 0, TestAnchors.Enabled ? ValueText.FormatText(text) : null);
			return changed;
		}

		/// <summary>After a number, vector or color widget: the value automation sets (parsed into the components), then the element.</summary>
		private static bool FinishNumbers(string kind, string label, uint id, string request, Span<float> values, bool changed, bool color = false)
		{
			if (request != null)
			{
				Span<float> parsed = stackalloc float[values.Length];
				if (color ? ValueText.TryParseColor(request, parsed) : ValueText.TryParse(request, parsed))
				{
					bool differs = !parsed.SequenceEqual(values);
					parsed.CopyTo(values);
					UIAutomation.ValueApplied(differs);
					changed |= differs;
				}
				else
				{
					UIAutomation.ValueApplied(false, color ? "not a color (#RRGGBB, #RRGGBBAA or r, g, b[, a])" : values.Length == 1 ? "not a number" : $"not {values.Length} numbers");
				}
			}

			TestAnchors.RecordElement(kind, label, id, TestAnchors.Enabled ? ValueText.Format(values) : null);
			return changed;
		}

		private static bool FinishNumbers(string kind, string label, uint id, string request, Span<int> values, bool changed)
		{
			if (request != null)
			{
				Span<int> parsed = stackalloc int[values.Length];
				if (ValueText.TryParse(request, parsed))
				{
					bool differs = !parsed.SequenceEqual(values);
					parsed.CopyTo(values);
					UIAutomation.ValueApplied(differs);
					changed |= differs;
				}
				else
				{
					UIAutomation.ValueApplied(false, values.Length == 1 ? "not a whole number" : $"not {values.Length} whole numbers");
				}
			}

			TestAnchors.RecordElement(kind, label, id, TestAnchors.Enabled ? ValueText.Format(values) : null);
			return changed;
		}

		/// <summary>A tree node or header that folds on a click of its label (not only on its arrow or a double click): activating it folds it too.</summary>
		private static bool Toggles(ImGuiTreeNodeFlags flags)
		{
			return (flags & (ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.OpenOnDoubleClick)) == 0;
		}

		#endregion
	}
}
