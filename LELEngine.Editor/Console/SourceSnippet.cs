using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     A few lines of a source file around a line, drawn inline under a console stack frame: line numbers, C#
	///     highlighting, the frame's line marked, the common indentation left out. A preview: lines are cut at the
	///     console's edge (no wrapping, no scrolling); a click opens the clicked line in the script editor.
	/// </summary>
	internal static class SourceSnippet
	{
		#region PrivateFields

		private const float Padding = 4f;
		private static readonly List<CSharpLexer.Token> tokens = new List<CSharpLexer.Token>();

		#endregion

		#region PublicMethods

		/// <summary>Lines <paramref name="line" /> ± <paramref name="context" /> of <paramref name="file" />, <paramref name="indent" /> pixels in.</summary>
		public static void Draw(string file, int line, int context, float indent)
		{
			SourceFile source = SourceFiles.Get(file);
			if (source.Error != null)
			{
				ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
				ImGui.TextDisabled(source.Error);
				return;
			}

			int first = Math.Max(1, line - context);
			int last = Math.Min(source.Lines.Length, line + context);
			if (first > last)
			{
				return;
			}

			// The snippet starts at the left: the indentation its lines share goes.
			int dedent = int.MaxValue;
			for (int n = first; n <= last; n++)
			{
				string text = source.Lines[n - 1];
				int lead = 0;
				while (lead < text.Length && text[lead] == ' ')
				{
					lead++;
				}

				if (lead < text.Length)
				{
					dedent = Math.Min(dedent, lead);
				}
			}

			dedent = dedent == int.MaxValue ? 0 : dedent;
			float width = Math.Max(40f, ImGui.GetContentRegionAvail().X - indent);
			bool mono = EditorFonts.PushMono(EditorPreferences.Current.Theme.CodeFontSize);
			float lineHeight = MathF.Ceiling(ImGui.GetTextLineHeight() * 1.15f);
			float textOffset = (lineHeight - ImGui.GetTextLineHeight()) * 0.5f;
			float charWidth = ImGui.CalcTextSize("0").X;
			float gutter = charWidth * (last.ToString().Length + 2);
			float height = (last - first + 1) * lineHeight + Padding * 2f;

			// A preview, cut at the console's edge (no wrapping, no scrolling); a click opens the file at the clicked line.
			ImGui.SetCursorPosX(ImGui.GetCursorPosX() + indent);
			NVector2 start = ImGui.GetCursorScreenPos();
			NVector2 end = start + new NVector2(width, height);
			ImGui.InvisibleButton("##snippet", new NVector2(width, height));
			bool hovered = ImGui.IsItemHovered();
			bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(start, end, EditorStyle.GetU32(EditorColor.CodeBackground), 3f);
			draw.PushClipRect(start, end, true);
			uint numberColor = EditorStyle.GetU32(EditorColor.CodeLineNumber);
			uint currentNumberColor = EditorStyle.GetU32(EditorColor.CodeText);
			for (int n = first; n <= last; n++)
			{
				float y = start.Y + Padding + (n - first) * lineHeight;
				bool current = n == line;
				if (current)
				{
					draw.AddRectFilled(new NVector2(start.X, y), new NVector2(end.X, y + lineHeight), EditorStyle.GetU32(EditorColor.CodeHighlight));
				}

				string number = n.ToString();
				draw.AddText(new NVector2(start.X + Padding + gutter - charWidth - ImGui.CalcTextSize(number).X, y + textOffset), current ? currentNumberColor : numberColor, number);
				DrawCode(draw, source.Lines[n - 1], source.LineStates[n - 1], new NVector2(start.X + Padding + gutter - dedent * charWidth, y + textOffset));
			}

			draw.PopClipRect();
			if (hovered)
			{
				draw.AddRect(start, end, ImGui.GetColorU32(ImGuiCol.HeaderHovered), 3f);
				ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
				ImGui.SetTooltip(file + ":" + line + "\nClick: open in the script editor");
			}

			if (clicked)
			{
				int at = first + (int)((ImGui.GetMousePos().Y - start.Y - Padding) / lineHeight);
				ExternalScriptEditor.Open(file, Math.Clamp(at, first, last));
			}

			EditorFonts.Pop(mono);
			ImGui.Dummy(new NVector2(0f, 2f));
		}

		#endregion

		#region PrivateMethods

		/// <summary>The line's highlighted spans with plain text between them.</summary>
		private static void DrawCode(ImDrawListPtr draw, string line, CSharpLexer.State state, NVector2 pos)
		{
			if (line.Length == 0)
			{
				return;
			}

			tokens.Clear();
			CSharpLexer.Tokenize(line, state, tokens);
			float x = pos.X;
			int at = 0;
			foreach (CSharpLexer.Token token in tokens)
			{
				if (token.Start > at)
				{
					x = Segment(draw, line, at, token.Start - at, CSharpLexer.TokenKind.Text, x, pos.Y);
				}

				x = Segment(draw, line, token.Start, token.Length, token.Kind, x, pos.Y);
				at = token.Start + token.Length;
			}

			if (at < line.Length)
			{
				Segment(draw, line, at, line.Length - at, CSharpLexer.TokenKind.Text, x, pos.Y);
			}
		}

		private static float Segment(ImDrawListPtr draw, string line, int start, int length, CSharpLexer.TokenKind kind, float x, float y)
		{
			string text = line.Substring(start, length);
			draw.AddText(new NVector2(x, y), EditorStyle.GetU32(ColorOf(kind)), text);
			return x + ImGui.CalcTextSize(text).X;
		}

		private static EditorColor ColorOf(CSharpLexer.TokenKind kind)
		{
			return kind switch
			{
				CSharpLexer.TokenKind.Keyword => EditorColor.CodeKeyword,
				CSharpLexer.TokenKind.Control => EditorColor.CodeControl,
				CSharpLexer.TokenKind.Type => EditorColor.CodeType,
				CSharpLexer.TokenKind.Method => EditorColor.CodeMethod,
				CSharpLexer.TokenKind.String => EditorColor.CodeString,
				CSharpLexer.TokenKind.Number => EditorColor.CodeNumber,
				CSharpLexer.TokenKind.Comment => EditorColor.CodeComment,
				CSharpLexer.TokenKind.Preprocessor => EditorColor.CodePreprocessor,
				_ => EditorColor.CodeText
			};
		}

		#endregion
	}
}
