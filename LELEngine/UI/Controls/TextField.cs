using System;
using System.Collections.Generic;
using System.Text;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	/// <summary>
	///     An editable text box: single or multi-line, selection with the mouse and keyboard, clipboard, password
	///     masking, placeholder, delayed commit (<see cref="isDelayed" />: the value changes on Enter / focus loss).
	/// </summary>
	[UxmlElement]
	public class TextField : BaseField<string>
	{
		#region PublicFields

		public new static readonly string ussClassName = "unity-text-field";
		public new static readonly string labelUssClassName = ussClassName + "__label";
		public new static readonly string inputUssClassName = ussClassName + "__input";
		public static readonly string multilineInputUssClassName = inputUssClassName + "--multiline";
		public static readonly string baseTextFieldUssClassName = "unity-base-text-field";
		public static readonly string baseInputUssClassName = baseTextFieldUssClassName + "__input";
		public static readonly string placeholderUssClassName = baseTextFieldUssClassName + "__placeholder";
		public static readonly string textInputUssName = "unity-text-input";

		[UxmlAttribute]
		public bool multiline
		{
			get => m_Multiline;
			set
			{
				m_Multiline = value;
				textInput.EnableInClassList(multilineInputUssClassName, value);
				textInput.textElement.style.whiteSpace = value ? WhiteSpace.Normal : WhiteSpace.NoWrap;
			}
		}

		/// <summary>Maximum number of characters, -1 for no limit.</summary>
		[UxmlAttribute("max-length")]
		public int maxLength { get; set; } = -1;

		[UxmlAttribute("password")]
		public bool isPasswordField
		{
			get => m_Password;
			set
			{
				m_Password = value;
				textInput.RefreshText();
			}
		}

		[UxmlAttribute("mask-character")]
		public char maskChar { get; set; } = '*';

		[UxmlAttribute("readonly")]
		public bool isReadOnly { get; set; }

		/// <summary>The value changes when editing ends (Enter, focus loss), not on every keystroke.</summary>
		[UxmlAttribute("is-delayed")]
		public bool isDelayed { get; set; }

		[UxmlAttribute("select-all-on-focus")]
		public bool selectAllOnFocus { get; set; } = true;

		[UxmlAttribute("select-all-on-mouse-up")]
		public bool selectAllOnMouseUp { get; set; }

		/// <summary>Text shown while the field is empty.</summary>
		[UxmlAttribute("placeholder-text")]
		public string placeholderText
		{
			get => placeholder.text;
			set
			{
				placeholder.text = value ?? "";
				textInput.UpdatePlaceholder();
			}
		}

		[UxmlAttribute("hide-placeholder-on-focus")]
		public bool hidePlaceholderOnFocus { get; set; }

		/// <summary>The text as being edited (equals value unless the field is delayed).</summary>
		public string text
		{
			get => textInput.editedText;
			set => textInput.SetEditedText(value ?? "", true);
		}

		public int cursorIndex
		{
			get => textInput.cursorIndex;
			set => textInput.SetCursor(value, value);
		}

		public int selectIndex
		{
			get => textInput.selectIndex;
			set => textInput.SetCursor(textInput.cursorIndex, value);
		}

		/// <summary>The selected text.</summary>
		public string selectedText => textInput.GetSelectedText();

		public Color4 selectionColor { get; set; } = new Color4(0.24f, 0.48f, 0.84f, 0.5f);
		public Color4 cursorColor { get; set; } = new Color4(0.9f, 0.9f, 0.9f, 1f);

		#endregion

		#region PrivateFields

		private readonly TextInput textInput;
		private readonly Label placeholder;
		private bool m_Multiline;
		private bool m_Password;

		#endregion

		#region Constructors

		public TextField()
			: this(null)
		{ }

		public TextField(string label)
			: this(label, -1, false, false, '*')
		{ }

		public TextField(int maxLength, bool multiline, bool isPasswordField, char maskChar)
			: this(null, maxLength, multiline, isPasswordField, maskChar)
		{ }

		public TextField(string label, int maxLength, bool multiline, bool isPasswordField, char maskChar)
			: base(label, new TextInput())
		{
			AddToClassList(ussClassName);
			AddToClassList(baseTextFieldUssClassName);
			labelElement.AddToClassList(labelUssClassName);
			textInput = (TextInput)visualInput;
			textInput.name = textInputUssName;
			textInput.AddToClassList(inputUssClassName);
			textInput.AddToClassList(baseInputUssClassName);
			textInput.field = this;
			placeholder = new Label { pickingMode = PickingMode.Ignore };
			placeholder.AddToClassList(placeholderUssClassName);
			placeholder.style.position = Position.Absolute;
			placeholder.style.display = DisplayStyle.None;
			textInput.Add(placeholder);
			this.maxLength = maxLength;
			this.multiline = multiline;
			this.maskChar = maskChar;
			this.isPasswordField = isPasswordField;
			rawValue = "";
			focusable = true;
			delegatesFocus = true;
			this.AddManipulator(new Clickable(() => textInput.Focus()));
		}

		#endregion

		#region PublicMethods

		public override void SetValueWithoutNotify(string newValue)
		{
			newValue ??= "";
			base.SetValueWithoutNotify(newValue);
			textInput.SetEditedText(newValue, false);
		}

		public void SelectAll()
		{
			textInput.SelectAll();
		}

		public void SelectNone()
		{
			textInput.SetCursor(textInput.cursorIndex, textInput.cursorIndex);
		}

		public void SelectRange(int cursorIndex, int selectionIndex)
		{
			textInput.SetCursor(cursorIndex, selectionIndex);
		}

		#endregion

		#region ProtectedMethods

		protected override Focusable GetFocusDelegate()
		{
			return textInput;
		}

		internal Label placeholderLabel => placeholder;

		/// <summary>Called by the input when editing produced new text (every keystroke, or the commit when delayed).</summary>
		internal void OnTextEdited(string newText, bool commit)
		{
			if (isDelayed && !commit)
			{
				return;
			}

			value = newText;
		}

		#endregion

		#region NestedTypes

		/// <summary>The editable area: owns the caret, selection, keyboard and pointer handling.</summary>
		internal sealed class TextInput : VisualElement
		{
			public TextField field;
			public readonly TextElement textElement;
			public string editedText { get; private set; } = "";
			public int cursorIndex { get; private set; }
			public int selectIndex { get; private set; }

			private bool dragging;
			private bool caretVisible = true;
			private long lastBlinkToggle;
			private long lastClickTime;
			private int clickCount;
			private float scrollX;
			private bool revertOnEscape;
			private string valueBeforeEdit;

			public TextInput()
			{
				focusable = true;
				tabIndex = 0;
				textElement = new TextElement { name = "unity-text-element", pickingMode = PickingMode.Ignore };
				textElement.AddToClassList("unity-text-element--inner-input-field-component");
				Add(textElement);
				generateVisualContent = OnGenerateVisualContent;
				RegisterCallback<KeyDownEvent>(OnKeyDown);
				RegisterCallback<PointerDownEvent>(OnPointerDown);
				RegisterCallback<PointerMoveEvent>(OnPointerMove);
				RegisterCallback<PointerUpEvent>(OnPointerUp);
				RegisterCallback<FocusInEvent>(OnFocusIn);
				RegisterCallback<FocusOutEvent>(OnFocusOut);
				RegisterCallback<GeometryChangedEvent>(evt => EnsureCaretVisible());
				textElement.RegisterCallback<GeometryChangedEvent>(evt => EnsureCaretVisible());
				schedule.Execute(Blink).Every(500);
			}

			public bool hasFocus => focusController?.focusedElement == this;
			public bool hasSelection => cursorIndex != selectIndex;

			// ---------------------------------------------------------------- text

			public void SetEditedText(string text, bool notifyField)
			{
				editedText = text ?? "";
				cursorIndex = Math.Clamp(cursorIndex, 0, editedText.Length);
				selectIndex = Math.Clamp(selectIndex, 0, editedText.Length);
				RefreshText();
				if (notifyField)
				{
					field?.OnTextEdited(editedText, false);
				}
			}

			public void RefreshText()
			{
				string shown = editedText;
				if (field != null && field.isPasswordField)
				{
					shown = new string(field.maskChar, editedText.Length);
				}

				textElement.text = shown;
				UpdatePlaceholder();
				MarkDirtyRepaint();
			}

			public void UpdatePlaceholder()
			{
				if (field == null)
				{
					return;
				}

				bool show = editedText.Length == 0 && field.placeholderLabel.text.Length > 0 && !(field.hidePlaceholderOnFocus && hasFocus);
				field.placeholderLabel.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
			}

			public void SetCursor(int cursor, int select)
			{
				cursorIndex = Math.Clamp(cursor, 0, editedText.Length);
				selectIndex = Math.Clamp(select, 0, editedText.Length);
				caretVisible = true;
				lastBlinkToggle = elementPanel?.timeMs ?? 0;
				EnsureCaretVisible();
				MarkDirtyRepaint();
			}

			public void SelectAll()
			{
				SetCursor(editedText.Length, 0);
			}

			public string GetSelectedText()
			{
				int start = Math.Min(cursorIndex, selectIndex);
				int end = Math.Max(cursorIndex, selectIndex);
				return editedText.Substring(start, end - start);
			}

			private void Commit()
			{
				field?.OnTextEdited(editedText, true);
				valueBeforeEdit = editedText;
			}

			private void Replace(string replacement)
			{
				if (field != null && field.isReadOnly)
				{
					return;
				}

				int start = Math.Min(cursorIndex, selectIndex);
				int end = Math.Max(cursorIndex, selectIndex);
				string result = editedText.Substring(0, start) + replacement + editedText.Substring(end);
				if (field != null && field.maxLength >= 0 && result.Length > field.maxLength)
				{
					int allowed = Math.Max(0, field.maxLength - (editedText.Length - (end - start)));
					replacement = replacement.Substring(0, Math.Min(replacement.Length, allowed));
					result = editedText.Substring(0, start) + replacement + editedText.Substring(end);
				}

				editedText = result;
				cursorIndex = selectIndex = start + replacement.Length;
				RefreshText();
				caretVisible = true;
				EnsureCaretVisible();
				field?.OnTextEdited(editedText, false);
			}

			// ---------------------------------------------------------------- keyboard

			private void OnKeyDown(KeyDownEvent evt)
			{
				if (field == null)
				{
					return;
				}

				bool ctrl = evt.ctrlKey || evt.commandKey;
				bool shift = evt.shiftKey;
				if (evt.character != '\0' && evt.keyCode == Keys.Unknown)
				{
					if (!ctrl && !char.IsControl(evt.character))
					{
						Replace(evt.character.ToString());
						evt.StopPropagation();
					}

					return;
				}

				switch (evt.keyCode)
				{
					case Keys.Backspace:
						if (hasSelection) Replace("");
						else if (cursorIndex > 0)
						{
							int start = ctrl ? WordStart(cursorIndex) : cursorIndex - 1;
							selectIndex = start;
							Replace("");
						}

						break;
					case Keys.Delete:
						if (hasSelection) Replace("");
						else if (cursorIndex < editedText.Length)
						{
							int end = ctrl ? WordEnd(cursorIndex) : cursorIndex + 1;
							selectIndex = end;
							Replace("");
						}

						break;
					case Keys.Left:
					{
						int target = ctrl ? WordStart(cursorIndex) : (hasSelection && !shift ? Math.Min(cursorIndex, selectIndex) : cursorIndex - 1);
						Move(target, shift);
						break;
					}

					case Keys.Right:
					{
						int target = ctrl ? WordEnd(cursorIndex) : (hasSelection && !shift ? Math.Max(cursorIndex, selectIndex) : cursorIndex + 1);
						Move(target, shift);
						break;
					}

					case Keys.Up:
						Move(field.multiline ? LineMove(-1) : 0, shift);
						break;
					case Keys.Down:
						Move(field.multiline ? LineMove(1) : editedText.Length, shift);
						break;
					case Keys.Home:
						Move(ctrl || !field.multiline ? 0 : LineStart(cursorIndex), shift);
						break;
					case Keys.End:
						Move(ctrl || !field.multiline ? editedText.Length : LineEnd(cursorIndex), shift);
						break;
					case Keys.Enter:
					case Keys.KeyPadEnter:
						if (field.multiline && !ctrl)
						{
							Replace("\n");
						}
						else
						{
							Commit();
						}

						break;
					case Keys.Escape:
						if (field.isDelayed && valueBeforeEdit != null)
						{
							SetEditedText(valueBeforeEdit, false);
						}

						Blur();
						break;
					case Keys.A when ctrl:
						SelectAll();
						break;
					case Keys.C when ctrl:
						if (hasSelection && !field.isPasswordField) UIRuntime.SetClipboard(GetSelectedText());
						break;
					case Keys.X when ctrl:
						if (hasSelection && !field.isPasswordField)
						{
							UIRuntime.SetClipboard(GetSelectedText());
							Replace("");
						}

						break;
					case Keys.V when ctrl:
					{
						string clipboard = UIRuntime.GetClipboard();
						if (!string.IsNullOrEmpty(clipboard))
						{
							if (!field.multiline) clipboard = clipboard.Replace("\r", "").Replace("\n", " ");
							Replace(clipboard);
						}

						break;
					}

					case Keys.Tab:
						return;
					default:
						return;
				}

				evt.StopPropagation();
			}

			private void Move(int target, bool extend)
			{
				target = Math.Clamp(target, 0, editedText.Length);
				SetCursor(target, extend ? selectIndex : target);
			}

			private int WordStart(int index)
			{
				int i = Math.Clamp(index, 0, editedText.Length);
				while (i > 0 && char.IsWhiteSpace(editedText[i - 1])) i--;
				while (i > 0 && !char.IsWhiteSpace(editedText[i - 1])) i--;
				return i;
			}

			private int WordEnd(int index)
			{
				int i = Math.Clamp(index, 0, editedText.Length);
				while (i < editedText.Length && !char.IsWhiteSpace(editedText[i])) i++;
				while (i < editedText.Length && char.IsWhiteSpace(editedText[i])) i++;
				return i;
			}

			private int LineStart(int index)
			{
				int i = Math.Clamp(index, 0, editedText.Length);
				while (i > 0 && editedText[i - 1] != '\n') i--;
				return i;
			}

			private int LineEnd(int index)
			{
				int i = Math.Clamp(index, 0, editedText.Length);
				while (i < editedText.Length && editedText[i] != '\n') i++;
				return i;
			}

			private int LineMove(int direction)
			{
				TextLayoutResult layout = textElement.GetRenderLayout(true);
				if (layout.charPositions == null || layout.lines.Count == 0)
				{
					return cursorIndex;
				}

				TextCharPosition current = layout.charPositions[Math.Clamp(cursorIndex, 0, layout.charPositions.Count - 1)];
				int line = Math.Clamp(current.line + direction, 0, layout.lines.Count - 1);
				if (line == current.line)
				{
					return direction < 0 ? 0 : editedText.Length;
				}

				TextLineInfo info = layout.lines[line];
				return IndexAt(layout, new Vector2(current.x, info.y + info.height * 0.5f));
			}

			// ---------------------------------------------------------------- pointer

			private void OnPointerDown(PointerDownEvent evt)
			{
				if (evt.button != 0 || field == null || !enabledInHierarchy)
				{
					return;
				}

				Focus();
				long now = elementPanel?.timeMs ?? 0;
				clickCount = now - lastClickTime < 400 ? clickCount + 1 : 1;
				lastClickTime = now;

				int index = IndexAtLocal(evt.localPosition);
				if (clickCount == 2)
				{
					SetCursor(WordEndNoSpace(index), WordStart(index));
				}
				else if (clickCount >= 3)
				{
					SetCursor(LineEnd(index), LineStart(index));
				}
				else
				{
					SetCursor(index, evt.shiftKey ? selectIndex : index);
					dragging = true;
					CapturePointer(evt.pointerId);
				}

				evt.StopPropagation();
			}

			private int WordEndNoSpace(int index)
			{
				int i = Math.Clamp(index, 0, editedText.Length);
				while (i < editedText.Length && !char.IsWhiteSpace(editedText[i])) i++;
				return i;
			}

			private void OnPointerMove(PointerMoveEvent evt)
			{
				if (!dragging)
				{
					return;
				}

				int index = IndexAtLocal(evt.localPosition);
				SetCursor(index, selectIndex);
				evt.StopPropagation();
			}

			private void OnPointerUp(PointerUpEvent evt)
			{
				if (dragging)
				{
					dragging = false;
					ReleasePointer(evt.pointerId);
					if (field != null && field.selectAllOnMouseUp && !hasSelection && clickCount == 1)
					{
						SelectAll();
					}

					evt.StopPropagation();
				}
			}

			private void OnFocusIn(FocusInEvent evt)
			{
				valueBeforeEdit = editedText;
				caretVisible = true;
				lastBlinkToggle = elementPanel?.timeMs ?? 0;
				if (field != null && field.selectAllOnFocus && evt.direction != FocusChangeDirection.Unspecified)
				{
					SelectAll();
				}

				UpdatePlaceholder();
				MarkDirtyRepaint();
			}

			private void OnFocusOut(FocusOutEvent evt)
			{
				dragging = false;
				if (field != null && field.isDelayed && editedText != (field.rawValue ?? ""))
				{
					Commit();
				}

				UpdatePlaceholder();
				MarkDirtyRepaint();
			}

			private void Blink()
			{
				if (!hasFocus)
				{
					return;
				}

				caretVisible = !caretVisible;
				MarkDirtyRepaint();
			}

			// ---------------------------------------------------------------- geometry

			private int IndexAtLocal(Vector2 localPosition)
			{
				TextLayoutResult layout = textElement.GetRenderLayout(true);
				Vector2 inText = localPosition - textElement.layout.position - textElement.contentRect.position;
				return IndexAt(layout, inText);
			}

			private int IndexAt(TextLayoutResult layout, Vector2 position)
			{
				if (layout.charPositions == null || layout.lines.Count == 0)
				{
					return editedText.Length;
				}

				int line = layout.lines.Count - 1;
				for (int i = 0; i < layout.lines.Count; i++)
				{
					if (position.Y < layout.lines[i].y + layout.lines[i].height)
					{
						line = i;
						break;
					}
				}

				int best = -1;
				float bestDistance = float.MaxValue;
				for (int i = 0; i < layout.charPositions.Count; i++)
				{
					TextCharPosition p = layout.charPositions[i];
					if (p.line != line)
					{
						continue;
					}

					float distance = Math.Abs(p.x - position.X);
					if (distance < bestDistance)
					{
						bestDistance = distance;
						best = i;
					}

					float endDistance = Math.Abs(p.x + p.advance - position.X);
					if (p.advance > 0f && endDistance < bestDistance && i + 1 <= editedText.Length)
					{
						// Nearer to the end of this character: the caret goes after it unless that is on the next line.
						int next = i + 1;
						bool nextOnSameLine = next < layout.charPositions.Count && (layout.charPositions[next].line == line || next == editedText.Length);
						if (nextOnSameLine && (next >= editedText.Length || editedText[i] != '\n'))
						{
							bestDistance = endDistance;
							best = next;
						}
					}
				}

				return best < 0 ? editedText.Length : Math.Clamp(best, 0, editedText.Length);
			}

			private void EnsureCaretVisible()
			{
				if (field == null || field.multiline)
				{
					if (scrollX != 0f)
					{
						scrollX = 0f;
						textElement.style.translate = new Translate(0f, 0f);
					}

					return;
				}

				TextLayoutResult layout = textElement.GetRenderLayout(true);
				if (layout.charPositions == null || cursorIndex >= layout.charPositions.Count)
				{
					return;
				}

				float caretX = layout.charPositions[cursorIndex].x;
				float viewWidth = contentRect.width;
				float textWidth = layout.size.X;
				float newScroll = scrollX;
				if (textWidth <= viewWidth)
				{
					newScroll = 0f;
				}
				else
				{
					if (caretX - newScroll > viewWidth - 2f) newScroll = caretX - viewWidth + 2f;
					if (caretX - newScroll < 0f) newScroll = caretX;
					newScroll = Math.Clamp(newScroll, 0f, Math.Max(0f, textWidth - viewWidth + 2f));
				}

				if (Math.Abs(newScroll - scrollX) > 0.5f)
				{
					scrollX = newScroll;
					textElement.style.translate = new Translate(-(float)Math.Round(scrollX), 0f);
				}
			}

			private void OnGenerateVisualContent(MeshGenerationContext context)
			{
				if (field == null || !hasFocus)
				{
					return;
				}

				TextLayoutResult layout = textElement.GetRenderLayout(true);
				if (layout.charPositions == null || layout.charPositions.Count == 0 || layout.lines.Count == 0)
				{
					return;
				}

				Vector2 origin = textElement.layout.position + textElement.contentRect.position - new Vector2(scrollX, 0f);
				if (hasSelection)
				{
					int start = Math.Min(cursorIndex, selectIndex);
					int end = Math.Max(cursorIndex, selectIndex);
					for (int line = 0; line < layout.lines.Count; line++)
					{
						TextLineInfo info = layout.lines[line];
						float x0 = float.MaxValue, x1 = float.MinValue;
						for (int i = Math.Max(start, info.startChar); i < Math.Min(end, info.endChar) && i < layout.charPositions.Count; i++)
						{
							TextCharPosition p = layout.charPositions[i];
							if (p.line != line) continue;
							x0 = Math.Min(x0, p.x);
							x1 = Math.Max(x1, p.x + Math.Max(p.advance, editedText[i] == '\n' ? 4f : 0f));
						}

						if (x1 > x0)
						{
							context.DrawRectangle(new Rect(origin.X + x0, origin.Y + info.y, x1 - x0, info.height), field.selectionColor);
						}
					}
				}

				if (caretVisible && !field.isReadOnly)
				{
					TextCharPosition caret = layout.charPositions[Math.Clamp(cursorIndex, 0, layout.charPositions.Count - 1)];
					int line = Math.Clamp(caret.line, 0, layout.lines.Count - 1);
					TextLineInfo info = layout.lines[line];
					context.DrawRectangle(new Rect((float)Math.Round(origin.X + caret.x), origin.Y + info.y + 1f, 1f, Math.Max(2f, info.height - 2f)), field.cursorColor);
				}
			}
		}

		#endregion
	}
}
