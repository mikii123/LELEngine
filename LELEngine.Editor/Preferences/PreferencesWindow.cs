using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Edit > Preferences...: the user's settings for every project (<see cref="EditorPreferences" />), a section
	///     each: Theme (preset, text sizes, every color of the editor, its source code and ImGui, each resettable to the
	///     preset's), Console (toolbar, list, stack traces, hidden wrapper frames, source view, custom filters, hidden
	///     tags) and External Tools (the script editor). Changes apply at once and are saved a moment later.
	/// </summary>
	internal sealed class PreferencesWindow
	{
		#region PublicFields

		public enum Section
		{
			Theme,
			Console,
			ExternalTools
		}

		public bool Open;

		#endregion

		#region PrivateFields

		private static readonly string[] SectionNames = { "Theme", "Console", "External Tools" };
		private static readonly string[] StackTraceNames = { "None", "Script Only", "Full" };

		private Section section = Section.Theme;
		private bool focus;
		private string colorFilter = "";
		private float labelWidth;

		#endregion

		#region PublicMethods

		/// <summary>Opens the window in front, on <paramref name="show" /> (null: the section it was on).</summary>
		public void Show(Section? show)
		{
			Open = true;
			focus = true;
			if (show != null)
			{
				section = show.Value;
			}
		}

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(820, 620), ImGuiCond.FirstUseEver);
			if (focus)
			{
				ImGui.SetNextWindowFocus();
				focus = false;
			}

			if (ImGui.Begin("Preferences", ref Open))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			float listWidth = Math.Max(150f, ImGui.CalcTextSize("External Tools").X + 40f);
			if (ImGui.BeginChild("##sections", new NVector2(listWidth, 0), ImGuiChildFlags.Borders))
			{
				for (int i = 0; i < SectionNames.Length; i++)
				{
					if (ImGui.Selectable(SectionNames[i], (int)section == i))
					{
						section = (Section)i;
					}

					if (TestAnchors.Enabled)
					{
						TestAnchors.RecordItem("preferences:" + SectionNames[i]);
					}
				}

				// Where they are kept, at the bottom.
				float height = ImGui.GetTextLineHeightWithSpacing() * 2f + ImGui.GetStyle().WindowPadding.Y;
				ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - height));
				ImGui.TextDisabled("For every project");
				ImGui.TextDisabled(EditorPreferences.File != null ? System.IO.Path.GetFileName(EditorPreferences.File) : "(not saved)");
				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(EditorPreferences.File ?? "An automated run: nothing is saved");
				}
			}

			ImGui.EndChild();
			ImGui.SameLine();
			if (ImGui.BeginChild("##section", NVector2.Zero))
			{
				labelWidth = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.38f, 160f, 260f);
				switch (section)
				{
					case Section.Theme:
						DrawTheme(EditorPreferences.Current.Theme);
						break;
					case Section.Console:
						DrawConsole(EditorPreferences.Current.Console);
						break;
					default:
						DrawExternalTools(EditorPreferences.Current.ExternalTools);
						break;
				}
			}

			ImGui.EndChild();
		}

		// ---------------------------------------------------------------- theme

		private void DrawTheme(ThemePreferences theme)
		{
			Label("Preset", "The colors the theme starts from (the colors below change them)");
			if (ImGui.BeginCombo("##preset", theme.Preset))
			{
				foreach (string preset in EditorStyle.Presets)
				{
					if (ImGui.Selectable(preset, preset == theme.Preset))
					{
						theme.Preset = preset;
						EditorPreferences.MarkChanged(true);
					}
				}

				ImGui.EndCombo();
			}

			Label("Text size", "Size of the editor's text, in pixels");
			if (ImGui.SliderFloat("##fontsize", ref theme.FontSize, 12f, 24f, "%.0f px"))
			{
				theme.FontSize = MathF.Round(theme.FontSize);
				EditorPreferences.MarkChanged(true);
			}

			Label("Code text size", "Size of source code (the console's source view)");
			if (ImGui.SliderFloat("##codefontsize", ref theme.CodeFontSize, 10f, 24f, "%.0f px"))
			{
				theme.CodeFontSize = MathF.Round(theme.CodeFontSize);
				EditorPreferences.MarkChanged(true);
			}

			ImGui.SeparatorText("Colors");
			ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Reset All").X - ImGui.GetStyle().FramePadding.X * 2f - ImGui.GetStyle().ItemSpacing.X));
			ImGui.InputTextWithHint("##colorfilter", "Filter colors", ref colorFilter, 64);
			ImGui.SameLine();
			ImGui.BeginDisabled(theme.Colors.Count == 0);
			if (ImGui.Button("Reset All"))
			{
				theme.Colors.Clear();
				EditorPreferences.MarkChanged(true);
			}

			ImGui.EndDisabled();
			if (theme.Colors.Count > 0)
			{
				ImGui.TextDisabled($"{theme.Colors.Count} color{(theme.Colors.Count == 1 ? "" : "s")} changed from {theme.Preset}");
			}

			var editorNames = new List<string>();
			var codeNames = new List<string>();
			foreach (EditorColor color in Enum.GetValues<EditorColor>())
			{
				(color.ToString().StartsWith("Code", StringComparison.Ordinal) ? codeNames : editorNames).Add(color.ToString());
			}

			var imguiNames = new List<string>();
			foreach (ImGuiCol color in Enum.GetValues<ImGuiCol>())
			{
				if (color >= 0 && color < ImGuiCol.Count)
				{
					imguiNames.Add(color.ToString());
				}
			}

			ColorGroup(theme, "Editor", editorNames, true);
			ColorGroup(theme, "Source Code", codeNames, true);
			ColorGroup(theme, "Interface", imguiNames, colorFilter.Length > 0);
		}

		private void ColorGroup(ThemePreferences theme, string title, List<string> names, bool open)
		{
			names = names.FindAll(name => colorFilter.Length == 0 || name.Contains(colorFilter, StringComparison.OrdinalIgnoreCase) || SerializationUtility.NicifyName(name).Contains(colorFilter, StringComparison.OrdinalIgnoreCase));
			if (names.Count == 0)
			{
				return;
			}

			ImGui.SetNextItemOpen(open, colorFilter.Length > 0 ? ImGuiCond.Always : ImGuiCond.Once);
			if (!ImGui.CollapsingHeader(title + "##" + title))
			{
				return;
			}

			if (!ImGui.BeginTable("##colors" + title, 3, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
			{
				return;
			}

			ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, labelWidth);
			ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Reset", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("Reset").X + ImGui.GetStyle().FramePadding.X * 2f);
			foreach (string name in names)
			{
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				bool changed = theme.Colors.ContainsKey(name);
				ImGui.AlignTextToFramePadding();
				if (changed)
				{
					ImGui.TextUnformatted(SerializationUtility.NicifyName(name) + " *");
				}
				else
				{
					ImGui.TextUnformatted(SerializationUtility.NicifyName(name));
				}

				ImGui.TableNextColumn();
				ImGui.PushID(name);
				NVector4 value = EditorStyle.Current(name);
				ImGui.SetNextItemWidth(-1f);
				if (ImGui.ColorEdit4("##value", ref value, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.DisplayHex))
				{
					theme.Colors[name] = ColorHex.ToHex(value);
					EditorPreferences.MarkChanged(true);
				}

				ImGui.TableNextColumn();
				if (changed && ImGui.SmallButton("Reset"))
				{
					theme.Colors.Remove(name);
					EditorPreferences.MarkChanged(true);
				}

				if (changed && ImGui.IsItemHovered())
				{
					NVector4 preset = EditorStyle.PresetValue(theme.Preset, name);
					ImGui.SetTooltip("Back to " + theme.Preset + "'s " + ColorHex.ToHex(preset));
				}

				ImGui.PopID();
			}

			ImGui.EndTable();
		}

		// ---------------------------------------------------------------- console

		private void DrawConsole(ConsolePreferences console)
		{
			bool changed = false;
			ImGui.TextDisabled("Clear on Play, Error Pause, Collapse and Clear on Recompile are in the console's toolbar.");
			ImGui.SeparatorText("List");
			changed |= Check("Timestamps", ref console.ShowTimestamps, "The time of each message");
			changed |= Check("Frame numbers", ref console.ShowFrames, "The frame (Time.frameCount) of messages logged in play mode");
			changed |= Check("Source column", ref console.ShowSourceColumn, "The file and line a message came from");
			Label("Lines per message", "Lines of a message's text each row shows");
			changed |= ImGui.SliderInt("##lines", ref console.LinesPerEntry, 1, 3);
			Label("Messages kept", "Older messages go beyond this many");
			if (ImGui.InputInt("##max", ref console.MaxEntries, 1000, 10000))
			{
				console.MaxEntries = Math.Clamp(console.MaxEntries, 100, 100000);
				changed = true;
			}

			ImGui.SeparatorText("Stack traces");
			ImGui.TextDisabled("How much of the call stack messages record (exceptions always have theirs).");
			changed |= StackTraceCombo("Log", ref console.LogStackTrace);
			changed |= StackTraceCombo("Warning", ref console.WarningStackTrace);
			changed |= StackTraceCombo("Error", ref console.ErrorStackTrace);
			changed |= Check("Library frames", ref console.ShowLibraryFrames, "List frames of .NET and third-party libraries too (exceptions record them)");
			Label("Hidden frames", "Logging wrappers: frames whose method starts with one of these (Namespace.Type. or Namespace.Type.Method) are dimmed and never the frame a message opens");
			changed |= StringList("hiddenframes", console.HiddenFrames, "MyGame.Log.");

			ImGui.SeparatorText("Code in call stacks");
			changed |= Check("Code under frames", ref console.ShowSourceCode, "The code around a frame's line under it (the project's frames and the one the message opens; a click shows or hides it on the others)");
			Label("Lines around", "Lines above and below the frame's line (1: three lines)");
			changed |= ImGui.SliderInt("##context", ref console.SourceContextLines, 0, 10);

			ImGui.SeparatorText("Custom filters");
			ImGui.TextDisabled("Toggles in the console's toolbar: off hides the messages a filter matches; matches get its color.");
			changed |= DrawFilters(console.Filters);

			if (console.HiddenTags.Count > 0)
			{
				ImGui.SeparatorText("Hidden tags");
				for (int i = 0; i < console.HiddenTags.Count; i++)
				{
					ImGui.PushID("tag" + i);
					if (ImGui.SmallButton("Show"))
					{
						console.HiddenTags.RemoveAt(i);
						changed = true;
						ImGui.PopID();
						break;
					}

					ImGui.SameLine();
					ImGui.TextUnformatted(console.HiddenTags[i]);
					ImGui.PopID();
				}
			}

			if (changed)
			{
				EditorPreferences.MarkChanged();
			}
		}

		private bool DrawFilters(List<ConsoleFilter> filters)
		{
			bool changed = false;
			if (filters.Count > 0 && ImGui.BeginTable("##filters", 8, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("Shown");
				ImGui.TableSetupColumn("Color");
				ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.4f);
				ImGui.TableSetupColumn("Pattern", ImGuiTableColumnFlags.WidthStretch, 0.6f);
				ImGui.TableSetupColumn("Regex");
				ImGui.TableSetupColumn("Case");
				ImGui.TableSetupColumn("Stacks");
				ImGui.TableSetupColumn("");
				ImGui.TableHeadersRow();
				for (int i = 0; i < filters.Count; i++)
				{
					ConsoleFilter filter = filters[i];
					ImGui.PushID(i);
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					changed |= ImGui.Checkbox("##shown", ref filter.Shown);
					ImGui.TableNextColumn();
					NVector4 color = ColorHex.Parse(filter.Color, new NVector4(0.3f, 0.6f, 1f, 1f));
					if (ImGui.ColorEdit4("##color", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
					{
						filter.Color = ColorHex.ToHex(color);
						changed = true;
					}

					ImGui.TableNextColumn();
					ImGui.SetNextItemWidth(-1f);
					changed |= ImGui.InputTextWithHint("##name", "(the pattern)", ref filter.Name, 64);
					ImGui.TableNextColumn();
					ImGui.SetNextItemWidth(-1f);
					changed |= ImGui.InputText("##pattern", ref filter.Pattern, 256);
					ImGui.TableNextColumn();
					changed |= ImGui.Checkbox("##regex", ref filter.Regex);
					ImGui.TableNextColumn();
					changed |= ImGui.Checkbox("##case", ref filter.MatchCase);
					ImGui.TableNextColumn();
					changed |= ImGui.Checkbox("##stack", ref filter.InStackTrace);
					ImGui.TableNextColumn();
					if (ImGui.SmallButton("x"))
					{
						filters.RemoveAt(i);
						changed = true;
						ImGui.PopID();
						break;
					}

					ImGui.PopID();
				}

				ImGui.EndTable();
			}

			if (ImGui.Button("Add Filter"))
			{
				filters.Add(new ConsoleFilter { Name = "Filter " + (filters.Count + 1) });
				changed = true;
			}

			return changed;
		}

		private bool StackTraceCombo(string type, ref StackTraceLogType value)
		{
			Label(type, type + " messages: None records no call stack; Script Only leaves out .NET and libraries; Full keeps every frame");
			int index = (int)value;
			if (!ImGui.Combo("##stack" + type, ref index, StackTraceNames, StackTraceNames.Length))
			{
				return false;
			}

			value = (StackTraceLogType)index;
			return true;
		}

		// ---------------------------------------------------------------- external tools

		private void DrawExternalTools(ExternalToolsPreferences tools)
		{
			bool changed = false;
			ImGui.SeparatorText("Script editor");
			Label("Script editor", "Opens scripts at a line: the console's stack frames, script assets");
			if (ImGui.BeginCombo("##scripteditor", EditorName(tools.ScriptEditor)))
			{
				foreach (ScriptEditorKind kind in Enum.GetValues<ScriptEditorKind>())
				{
					if (ImGui.Selectable(EditorName(kind) + (ExternalScriptEditor.IsInstalled(kind) ? "" : "  (not found)"), kind == tools.ScriptEditor))
					{
						tools.ScriptEditor = kind;
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled(ExternalScriptEditor.Describe(tools.ScriptEditor));
			ImGui.PopTextWrapPos();
			if (tools.ScriptEditor == ScriptEditorKind.Custom)
			{
				Label("Path", "The editor's executable");
				changed |= ImGui.InputTextWithHint("##path", @"C:\Tools\Editor\editor.exe", ref tools.CustomPath, 512);
				Label("Arguments", "$(File), $(Line), $(Column), $(ProjectPath), $(SolutionPath) are replaced");
				changed |= ImGui.InputText("##arguments", ref tools.CustomArguments, 512);
				ImGui.TextDisabled("$(File)  $(Line)  $(Column)  $(ProjectPath)  $(SolutionPath)");
			}

			if (changed)
			{
				EditorPreferences.MarkChanged();
			}
		}

		private static string EditorName(ScriptEditorKind kind)
		{
			return kind switch
			{
				ScriptEditorKind.VisualStudioCode => "Visual Studio Code",
				ScriptEditorKind.VisualStudio => "Visual Studio",
				ScriptEditorKind.SystemDefault => "System Default",
				_ => kind.ToString()
			};
		}

		// ---------------------------------------------------------------- widgets

		/// <summary>A label on the left (tooltip on hover); the next item fills the rest of the line.</summary>
		private void Label(string label, string tooltip)
		{
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(label);
			if (tooltip != null && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(tooltip);
			}

			ImGui.SameLine(labelWidth);
			ImGui.SetNextItemWidth(-1f);
		}

		private bool Check(string label, ref bool value, string tooltip)
		{
			Label(label, tooltip);
			return ImGui.Checkbox("##" + label, ref value);
		}

		/// <summary>Editable strings with a remove button each and an add button.</summary>
		private bool StringList(string id, List<string> values, string hint)
		{
			bool changed = false;
			ImGui.BeginGroup();
			ImGui.PushID(id);
			for (int i = 0; i < values.Count; i++)
			{
				ImGui.PushID(i);
				string value = values[i];
				ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - ImGui.GetFrameHeight() - ImGui.GetStyle().ItemSpacing.X));
				if (ImGui.InputTextWithHint("##value", hint, ref value, 256))
				{
					values[i] = value;
					changed = true;
				}

				ImGui.SameLine();
				if (ImGui.Button("x", new NVector2(ImGui.GetFrameHeight(), 0)))
				{
					values.RemoveAt(i);
					changed = true;
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
			}

			if (ImGui.Button("Add"))
			{
				values.Add("");
				changed = true;
			}

			ImGui.PopID();
			ImGui.EndGroup();
			return changed;
		}

		#endregion
	}
}
