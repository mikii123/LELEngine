using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     The console, in the manner of Unity's Console Pro: a toolbar (Clear and its options, Collapse, Clear on Play,
	///     Error Pause, type toggles with counts, a search with regex / case / stack trace options, the [Tag]s of the
	///     messages, the custom filters as colored toggles with counts), the messages (icon, time, frame, text, source
	///     file, count when collapsed; a filter's color on its matches) and below them the selected message: its text,
	///     when and what about, its call stack (the project's frames bright, the engine's dimmed, libraries' hidden,
	///     logging wrappers skipped) with the code around each frame's line inline. Double-click opens the source in the
	///     script editor; the toggles, columns and filters are preferences (<see cref="ConsolePreferences" />).
	/// </summary>
	internal sealed class ConsolePanel
	{
		#region PublicFields

		/// <summary>The window is shown (its close button and the Window menu toggle it; saved with the editor's windows).</summary>
		public bool Open = true;

		#endregion

		#region PrivateFields

		private const float SplitterThickness = 6f;

		private readonly EditorApplication editor;

		// The log as last read, and the rows it shows (filtered, collapsed). New messages are added to the rows as they
		// come; a change of the filters, a clear or trimmed old messages rebuild them.
		private List<ConsoleEntry> entries = new List<ConsoleEntry>();
		private readonly List<Row> rows = new List<Row>();
		private readonly Dictionary<string, int> collapsedRows = new Dictionary<string, int>(StringComparer.Ordinal);
		private readonly SortedDictionary<string, int> tagCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
		private int[] filterCounts = Array.Empty<int>();
		private Regex[] filterRegexes = Array.Empty<Regex>();
		private int logs, warnings, errors;
		private long lastProcessedId = -1;
		private int seenLogVersion = -1;
		private int seenRemovalVersion = -1;
		private int seenPreferencesVersion = -1;
		private bool rowsDirty = true;

		private string search = "";
		private Regex searchRegex;
		private bool searchInvalid;

		/// <summary>Alt+click on a filter: only the messages it matches (until clicked again).</summary>
		private ConsoleFilter solo;

		private long selectedId = -1;

		/// <summary>Frames of the selected message whose code the user showed or hid (a click), against the default.</summary>
		private readonly HashSet<int> toggledFrames = new HashSet<int>();

		private bool scrollToSelection;
		private ConsoleFilter editedFilter;
		private bool openFilterEditor;

		#endregion

		#region Constructors

		public ConsolePanel(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			if (ImGui.Begin("Console", ref Open, ImGuiWindowFlags.NoNavInputs))
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

		/// <summary>Selects the first message containing <paramref name="text" /> (test runs); false when none does.</summary>
		public bool SelectFirst(string text)
		{
			foreach (ConsoleEntry entry in editor.Log.Snapshot())
			{
				if (entry.Text.Contains(text, StringComparison.Ordinal))
				{
					Select(entry, false);
					scrollToSelection = true;
					return true;
				}
			}

			return false;
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			ConsolePreferences prefs = EditorPreferences.Current.Console;
			ConsoleLog log = editor.Log;
			bool newEntries = false;
			if (log.Version != seenLogVersion)
			{
				seenLogVersion = log.Version;
				entries = log.Snapshot(out int removals);
				newEntries = true;
				if (removals != seenRemovalVersion)
				{
					seenRemovalVersion = removals;
					rowsDirty = true;
				}
			}

			if (EditorPreferences.Version != seenPreferencesVersion)
			{
				seenPreferencesVersion = EditorPreferences.Version;
				CompileFilters(prefs);
				CompileSearch(prefs);
				rowsDirty = true;
			}

			DrawToolbar(prefs, log);
			UpdateRows(prefs);
			HandleKeys();

			float available = ImGui.GetContentRegionAvail().Y;
			float listHeight = MathF.Floor(Math.Clamp((available - SplitterThickness) * prefs.ListFraction, ImGui.GetFrameHeight() * 2f, Math.Max(ImGui.GetFrameHeight() * 2f, available - SplitterThickness - ImGui.GetFrameHeight() * 2f)));
			DrawList(prefs, listHeight, newEntries);
			if (Splitter("##listsplit", false, ImGui.GetContentRegionAvail().X, out float delta) || delta != 0f)
			{
				prefs.ListFraction = Math.Clamp((listHeight + delta) / Math.Max(1f, available - SplitterThickness), 0.1f, 0.9f);
				if (delta == 0f)
				{
					EditorPreferences.MarkChanged();
				}
			}

			DrawDetails(prefs);
		}

		// ---------------------------------------------------------------- toolbar

		private void DrawToolbar(ConsolePreferences prefs, ConsoleLog log)
		{
			if (ImGui.Button("Clear"))
			{
				log.Clear();
			}

			Tooltip("Clear the messages (script compile errors stay until the next build)");
			ImGui.SameLine(0, 1);
			if (ImGui.ArrowButton("##options", ImGuiDir.Down))
			{
				ImGui.OpenPopup("##consoleoptions");
			}

			Tooltip("Console options");
			DrawOptionsMenu(prefs, log);

			if (Toggle("Collapse", ref prefs.Collapse, "Identical messages (text and call stack) as one row with a count"))
			{
				rowsDirty = true;
			}

			Toggle("Clear on Play", ref prefs.ClearOnPlay, "Clear the console when play mode starts");
			Toggle("Error Pause", ref prefs.ErrorPause, "Pause play mode on an error or exception");

			Flow(SplitterThickness * 2f);
			ImGui.TextDisabled("|");
			if (TypeToggle("##logs", LogType.Log, logs, ref prefs.ShowLogs, "Messages")
				| TypeToggle("##warnings", LogType.Warning, warnings, ref prefs.ShowWarnings, "Warnings")
				| TypeToggle("##errors", LogType.Error, errors, ref prefs.ShowErrors, "Errors and exceptions"))
			{
				rowsDirty = true;
			}

			DrawSearch(prefs);
			DrawTags(prefs);
			DrawFilters(prefs);
		}

		private void DrawSearch(ConsolePreferences prefs)
		{
			float optionWidth = ImGui.CalcTextSize("Stack").X + ImGui.GetStyle().FramePadding.X * 2f;
			float width = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.3f, 140f, 320f);
			Flow(width + optionWidth * 3f);
			ImGui.SetNextItemWidth(width);
			if (searchInvalid)
			{
				ImGui.PushStyleColor(ImGuiCol.FrameBg, EditorStyle.Error * new NVector4(1f, 1f, 1f, 0.35f));
			}

			bool changed = ImGui.InputTextWithHint("##search", "Search", ref search, 256);
			if (searchInvalid)
			{
				ImGui.PopStyleColor();
				Tooltip("Not a valid regular expression");
			}

			bool options = false;
			ImGui.SameLine(0, 1);
			options |= SmallToggle(".*", ref prefs.SearchRegex, "Regular expression");
			ImGui.SameLine(0, 1);
			options |= SmallToggle("Aa", ref prefs.SearchMatchCase, "Match case");
			ImGui.SameLine(0, 1);
			options |= SmallToggle("Stack", ref prefs.SearchStackTraces, "Search the call stacks too");
			if (changed || options)
			{
				CompileSearch(prefs);
				rowsDirty = true;
			}
		}

		private void DrawTags(ConsolePreferences prefs)
		{
			if (tagCounts.Count == 0)
			{
				return;
			}

			int hidden = 0;
			foreach (string tag in prefs.HiddenTags)
			{
				hidden += tagCounts.ContainsKey(tag) ? 1 : 0;
			}

			string label = hidden > 0 ? $"Tags ({hidden} hidden)" : "Tags";
			Flow(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f);
			if (hidden > 0)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
			}

			if (ImGui.Button(label + "##tags"))
			{
				ImGui.OpenPopup("##tagsmenu");
			}

			if (hidden > 0)
			{
				ImGui.PopStyleColor();
			}

			Tooltip("Show or hide messages by the [Tag] they start with");
			if (!ImGui.BeginPopup("##tagsmenu"))
			{
				return;
			}

			if (ImGui.MenuItem("Show All", "", false, prefs.HiddenTags.Count > 0))
			{
				prefs.HiddenTags.Clear();
				Changed();
			}

			ImGui.Separator();
			foreach (KeyValuePair<string, int> tag in tagCounts)
			{
				bool shown = !prefs.HiddenTags.Contains(tag.Key);
				if (ImGui.MenuItem($"{tag.Key}  {tag.Value}", "", shown))
				{
					if (shown)
					{
						prefs.HiddenTags.Add(tag.Key);
					}
					else
					{
						prefs.HiddenTags.Remove(tag.Key);
					}

					Changed();
				}
			}

			ImGui.EndPopup();
		}

		private void DrawFilters(ConsolePreferences prefs)
		{
			for (int i = 0; i < prefs.Filters.Count; i++)
			{
				ImGui.PushID(i);
				FilterChip(prefs, prefs.Filters[i], i < filterCounts.Length ? filterCounts[i] : 0);
				ImGui.PopID();
			}

			Flow(ImGui.GetFrameHeight());
			if (ImGui.Button("+##addfilter", new NVector2(ImGui.GetFrameHeight(), 0)))
			{
				AddFilter(prefs, "", search, prefs.SearchRegex);
			}

			Tooltip("New custom filter (from the search text, when there is one)");
			DrawFilterEditor(prefs);
		}

		private void FilterChip(ConsolePreferences prefs, ConsoleFilter filter, int count)
		{
			string name = filter.Name.Length > 0 ? filter.Name : filter.Pattern.Length > 0 ? filter.Pattern : "(empty)";
			string text = name + "  " + count;
			NVector2 padding = ImGui.GetStyle().FramePadding;
			float swatch = ImGui.GetTextLineHeight() * 0.55f;
			var size = new NVector2(padding.X * 2f + swatch + 6f + ImGui.CalcTextSize(text).X, ImGui.GetFrameHeight());
			Flow(size.X);
			NVector2 pos = ImGui.GetCursorScreenPos();
			bool clicked = ImGui.InvisibleButton("##chip", size);
			if (TestAnchors.Enabled)
			{
				TestAnchors.RecordItem("consolefilter:" + name);
			}

			bool hovered = ImGui.IsItemHovered();
			NVector4 color = ColorHex.Parse(filter.Color, new NVector4(0.3f, 0.6f, 1f, 1f));
			bool on = filter.Shown && (solo == null || solo == filter);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			float rounding = ImGui.GetStyle().FrameRounding;
			draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(color * new NVector4(1f, 1f, 1f, on ? (hovered ? 0.5f : 0.32f) : (hovered ? 0.22f : 0.1f))), rounding);
			if (solo == filter)
			{
				draw.AddRect(pos, pos + size, ImGui.ColorConvertFloat4ToU32(color), rounding, ImDrawFlags.None, 1.5f);
			}

			var swatchMin = new NVector2(pos.X + padding.X, pos.Y + (size.Y - swatch) * 0.5f);
			draw.AddRectFilled(swatchMin, swatchMin + new NVector2(swatch, swatch), ImGui.ColorConvertFloat4ToU32(color * new NVector4(1f, 1f, 1f, on ? 1f : 0.35f)), 2f);
			draw.AddText(new NVector2(swatchMin.X + swatch + 6f, pos.Y + padding.Y), ImGui.GetColorU32(on ? ImGuiCol.Text : ImGuiCol.TextDisabled), text);
			if (clicked)
			{
				if (ImGui.GetIO().KeyAlt)
				{
					solo = solo == filter ? null : filter;
					rowsDirty = true;
				}
				else
				{
					filter.Shown = !filter.Shown;
					Changed();
				}
			}

			if (hovered)
			{
				ImGui.SetTooltip($"{name}: {(filter.Regex ? "regex " : "")}\"{filter.Pattern}\"{(filter.InStackTrace ? " (also in call stacks)" : "")}\n" +
					"Click: show / hide its messages. Alt+click: only its messages. Right-click: edit.");
			}

			if (ImGui.BeginPopupContextItem("##chipmenu"))
			{
				if (ImGui.MenuItem("Edit..."))
				{
					editedFilter = filter;
					openFilterEditor = true;
				}

				if (ImGui.MenuItem("Only Its Messages", "Alt+Click", solo == filter))
				{
					solo = solo == filter ? null : filter;
					rowsDirty = true;
				}

				int index = prefs.Filters.IndexOf(filter);
				if (ImGui.MenuItem("Move Left", "", false, index > 0))
				{
					prefs.Filters.RemoveAt(index);
					prefs.Filters.Insert(index - 1, filter);
					Changed();
				}

				if (ImGui.MenuItem("Move Right", "", false, index < prefs.Filters.Count - 1))
				{
					prefs.Filters.RemoveAt(index);
					prefs.Filters.Insert(index + 1, filter);
					Changed();
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Delete"))
				{
					prefs.Filters.Remove(filter);
					solo = solo == filter ? null : solo;
					Changed();
				}

				ImGui.EndPopup();
			}
		}

		private void AddFilter(ConsolePreferences prefs, string name, string pattern, bool regex)
		{
			// Colors go round a palette that reads on dark and light backgrounds.
			string[] palette = { "#4FA3FFFF", "#57D68DFF", "#FFB454FF", "#D97BFFFF", "#FF6E6EFF", "#4FD6D6FF", "#E0E05AFF", "#FF8FC8FF" };
			var filter = new ConsoleFilter { Name = name, Pattern = pattern ?? "", Regex = regex, Color = palette[prefs.Filters.Count % palette.Length] };
			prefs.Filters.Add(filter);
			editedFilter = filter;
			openFilterEditor = true;
			Changed();
		}

		private void DrawFilterEditor(ConsolePreferences prefs)
		{
			if (openFilterEditor)
			{
				openFilterEditor = false;
				ImGui.OpenPopup("##filtereditor");
			}

			if (!ImGui.BeginPopup("##filtereditor"))
			{
				return;
			}

			ConsoleFilter filter = editedFilter;
			if (filter == null || !prefs.Filters.Contains(filter))
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextDisabled("Custom filter");
			bool changed = false;
			ImGui.SetNextItemWidth(260f);
			changed |= ImGui.InputTextWithHint("Name", "(the pattern)", ref filter.Name, 64);
			ImGui.SetNextItemWidth(260f);
			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			changed |= ImGui.InputTextWithHint("Pattern", "text the message contains", ref filter.Pattern, 256);
			changed |= ImGui.Checkbox("Regex", ref filter.Regex);
			ImGui.SameLine();
			changed |= ImGui.Checkbox("Match case", ref filter.MatchCase);
			ImGui.SameLine();
			changed |= ImGui.Checkbox("In call stacks", ref filter.InStackTrace);
			NVector4 color = ColorHex.Parse(filter.Color, new NVector4(0.3f, 0.6f, 1f, 1f));
			if (ImGui.ColorEdit4("Color", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
			{
				filter.Color = ColorHex.ToHex(color);
				changed = true;
			}

			changed |= ImGui.Checkbox("Shown", ref filter.Shown);
			int index = prefs.Filters.IndexOf(filter);
			if (filter.Regex && index >= 0 && index < filterRegexes.Length && filterRegexes[index] == null && filter.Pattern.Length > 0)
			{
				ImGui.TextColored(EditorStyle.Error, "Not a valid regular expression");
			}

			ImGui.Separator();
			if (ImGui.Button("Delete"))
			{
				prefs.Filters.Remove(filter);
				solo = solo == filter ? null : solo;
				changed = true;
				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (ImGui.Button("Done") || ImGui.IsKeyPressed(ImGuiKey.Enter))
			{
				ImGui.CloseCurrentPopup();
			}

			if (changed)
			{
				Changed();
			}

			ImGui.EndPopup();
		}

		private void DrawOptionsMenu(ConsolePreferences prefs, ConsoleLog log)
		{
			if (!ImGui.BeginPopup("##consoleoptions"))
			{
				return;
			}

			bool changed = false;
			changed |= MenuToggle("Clear on Recompile", ref prefs.ClearOnRecompile);
			ImGui.Separator();
			changed |= MenuToggle("Timestamps", ref prefs.ShowTimestamps);
			changed |= MenuToggle("Frame Numbers", ref prefs.ShowFrames);
			changed |= MenuToggle("Source Column", ref prefs.ShowSourceColumn);
			if (ImGui.BeginMenu("Lines per Message"))
			{
				for (int lines = 1; lines <= 3; lines++)
				{
					if (ImGui.MenuItem(lines.ToString(), "", prefs.LinesPerEntry == lines))
					{
						prefs.LinesPerEntry = lines;
						changed = true;
					}
				}

				ImGui.EndMenu();
			}

			ImGui.Separator();
			changed |= MenuToggle("Code in Call Stacks", ref prefs.ShowSourceCode);
			changed |= MenuToggle("Library Frames", ref prefs.ShowLibraryFrames);
			if (ImGui.BeginMenu("Stack Traces"))
			{
				changed |= StackTraceMenu("Log", ref prefs.LogStackTrace);
				changed |= StackTraceMenu("Warning", ref prefs.WarningStackTrace);
				changed |= StackTraceMenu("Error", ref prefs.ErrorStackTrace);
				ImGui.EndMenu();
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Copy Shown Messages"))
			{
				ImGui.SetClipboardText(ShownText());
			}

			if (ImGui.MenuItem("Save Shown Messages", "", false, editor.Project != null))
			{
				SaveShown();
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Preferences..."))
			{
				editor.ShowPreferences(PreferencesWindow.Section.Console);
			}

			if (changed)
			{
				Changed();
			}

			ImGui.EndPopup();
		}

		private static bool StackTraceMenu(string label, ref StackTraceLogType value)
		{
			bool changed = false;
			if (ImGui.BeginMenu(label))
			{
				foreach (StackTraceLogType type in Enum.GetValues<StackTraceLogType>())
				{
					if (ImGui.MenuItem(LELEngine.Serialization.SerializationUtility.NicifyName(type.ToString()), "", value == type))
					{
						value = type;
						changed = true;
					}
				}

				ImGui.EndMenu();
			}

			return changed;
		}

		// ---------------------------------------------------------------- rows

		/// <summary>Adds the new messages to the rows, or rebuilds them (filters changed, messages cleared or trimmed).</summary>
		private void UpdateRows(ConsolePreferences prefs)
		{
			if (rowsDirty)
			{
				rows.Clear();
				collapsedRows.Clear();
				tagCounts.Clear();
				filterCounts = new int[prefs.Filters.Count];
				logs = warnings = errors = 0;
				lastProcessedId = -1;
				rowsDirty = false;
			}

			// Messages only come after the last one (anything else bumps the removal version: a rebuild).
			int first = entries.Count;
			while (first > 0 && entries[first - 1].Id > lastProcessedId)
			{
				first--;
			}

			for (int i = first; i < entries.Count; i++)
			{
				Process(prefs, entries[i]);
			}

			if (entries.Count > 0)
			{
				lastProcessedId = Math.Max(lastProcessedId, entries[entries.Count - 1].Id);
			}
		}

		private void Process(ConsolePreferences prefs, ConsoleEntry entry)
		{
			switch (entry.Type)
			{
				case LogType.Warning:
					warnings++;
					break;
				case LogType.Error:
				case LogType.Exception:
					errors++;
					break;
				default:
					logs++;
					break;
			}

			string tag = entry.Tag;
			if (tag != null)
			{
				tagCounts[tag] = tagCounts.TryGetValue(tag, out int tagged) ? tagged + 1 : 1;
			}

			int color = -1;
			bool hiddenByFilter = false;
			bool soloMatch = false;
			for (int f = 0; f < prefs.Filters.Count; f++)
			{
				ConsoleFilter filter = prefs.Filters[f];
				if (!Matches(filter, f < filterRegexes.Length ? filterRegexes[f] : null, entry))
				{
					continue;
				}

				filterCounts[f]++;
				soloMatch |= filter == solo;
				if (!filter.Shown)
				{
					hiddenByFilter = true;
				}
				else if (color < 0)
				{
					color = f;
				}
			}

			bool typeShown = entry.Type == LogType.Warning ? prefs.ShowWarnings : entry.IsError ? prefs.ShowErrors : prefs.ShowLogs;
			bool shown = typeShown && (solo != null ? soloMatch : !hiddenByFilter) && (tag == null || !prefs.HiddenTags.Contains(tag)) && SearchMatches(prefs, entry);
			if (!shown)
			{
				return;
			}

			if (prefs.Collapse)
			{
				if (collapsedRows.TryGetValue(entry.CollapseKey, out int at))
				{
					Row row = rows[at];
					row.Count++;
					rows[at] = row;
					return;
				}

				collapsedRows[entry.CollapseKey] = rows.Count;
			}

			rows.Add(new Row { Entry = entry, Count = 1, Filter = color });
		}

		private static bool Matches(ConsoleFilter filter, Regex regex, ConsoleEntry entry)
		{
			if (filter.Pattern.Length == 0)
			{
				return false;
			}

			if (filter.Regex)
			{
				return regex != null && (regex.IsMatch(entry.Text) || (filter.InStackTrace && entry.StackTrace != null && regex.IsMatch(entry.StackTrace)));
			}

			StringComparison comparison = filter.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
			return entry.Text.Contains(filter.Pattern, comparison) || (filter.InStackTrace && entry.StackTrace != null && entry.StackTrace.Contains(filter.Pattern, comparison));
		}

		private bool SearchMatches(ConsolePreferences prefs, ConsoleEntry entry)
		{
			if (search.Length == 0 || searchInvalid)
			{
				return true;
			}

			if (searchRegex != null)
			{
				return searchRegex.IsMatch(entry.Text) || (prefs.SearchStackTraces && entry.StackTrace != null && searchRegex.IsMatch(entry.StackTrace));
			}

			StringComparison comparison = prefs.SearchMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
			return entry.Text.Contains(search, comparison) || (prefs.SearchStackTraces && entry.StackTrace != null && entry.StackTrace.Contains(search, comparison));
		}

		private void CompileFilters(ConsolePreferences prefs)
		{
			filterRegexes = new Regex[prefs.Filters.Count];
			for (int i = 0; i < prefs.Filters.Count; i++)
			{
				ConsoleFilter filter = prefs.Filters[i];
				filterRegexes[i] = filter.Regex ? TryRegex(filter.Pattern, filter.MatchCase) : null;
			}

			if (solo != null && !prefs.Filters.Contains(solo))
			{
				solo = null;
			}
		}

		private void CompileSearch(ConsolePreferences prefs)
		{
			searchRegex = prefs.SearchRegex && search.Length > 0 ? TryRegex(search, prefs.SearchMatchCase) : null;
			searchInvalid = prefs.SearchRegex && search.Length > 0 && searchRegex == null;
		}

		private static Regex TryRegex(string pattern, bool matchCase)
		{
			try
			{
				return pattern.Length > 0 ? new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)) : null;
			}
			catch (ArgumentException)
			{
				return null;
			}
		}

		// ---------------------------------------------------------------- list

		private void DrawList(ConsolePreferences prefs, float height, bool newEntries)
		{
			// A narrow console (docked in a side column) keeps the width for the messages: the extra columns go first.
			float width = ImGui.GetContentRegionAvail().X;
			var shown = new Columns
			{
				Time = prefs.ShowTimestamps && width >= 380f,
				Frame = prefs.ShowFrames && width >= 460f,
				Source = prefs.ShowSourceColumn && width >= 560f,
				Count = prefs.Collapse
			};

			int columns = 2 + (shown.Time ? 1 : 0) + (shown.Frame ? 1 : 0) + (shown.Source ? 1 : 0) + (shown.Count ? 1 : 0);
			string id = "##messages" + (shown.Time ? "t" : "") + (shown.Frame ? "f" : "") + (shown.Source ? "s" : "") + (shown.Count ? "c" : "");
			ImGuiTableFlags flags = ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersOuter;
			if (!ImGui.BeginTable(id, columns, flags, new NVector2(0, height)))
			{
				return;
			}

			float lineHeight = ImGui.GetTextLineHeight();
			float padding = ImGui.GetStyle().CellPadding.Y;
			float rowHeight = lineHeight * prefs.LinesPerEntry + padding * 2f;
			ImGui.TableSetupColumn("##type", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, lineHeight + 4f);
			if (shown.Time)
			{
				ImGui.TableSetupColumn("Time", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("00:00:00.000").X);
			}

			if (shown.Frame)
			{
				ImGui.TableSetupColumn("Frame", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("0000000").X);
			}

			ImGui.TableSetupColumn("Message", ImGuiTableColumnFlags.WidthStretch);
			if (shown.Source)
			{
				ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, ImGui.CalcTextSize("SomeScriptName.cs:1234").X);
			}

			if (shown.Count)
			{
				ImGui.TableSetupColumn("##count", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoResize, ImGui.CalcTextSize("99999").X + 8f);
			}

			bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 2f;
			if (scrollToSelection)
			{
				scrollToSelection = false;
				int index = RowIndex(selectedId);
				if (index >= 0)
				{
					float top = index * rowHeight;
					float view = ImGui.GetWindowHeight();
					if (top < ImGui.GetScrollY())
					{
						ImGui.SetScrollY(top);
					}
					else if (top + rowHeight > ImGui.GetScrollY() + view)
					{
						ImGui.SetScrollY(top + rowHeight - view);
					}

					atBottom = false;
				}
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			ImGuiListClipperPtr clipper = ImGui.ImGuiListClipper();
			clipper.Begin(rows.Count, rowHeight);
			while (clipper.Step())
			{
				for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
				{
					DrawRow(prefs, shown, rows[i], i, rowHeight, draw);
				}
			}

			clipper.End();
			clipper.Destroy();
			if (newEntries && atBottom)
			{
				ImGui.SetScrollHereY(1f);
			}

			if (rows.Count == 0)
			{
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(1 + (shown.Time ? 1 : 0) + (shown.Frame ? 1 : 0));
				ImGui.TextDisabled(entries.Count == 0 ? "No messages" : $"No message shown of {entries.Count} (types, tags, filters or the search hide them)");
			}

			ImGui.EndTable();
		}

		private void DrawRow(ConsolePreferences prefs, Columns shown, Row row, int index, float rowHeight, ImDrawListPtr draw)
		{
			ConsoleEntry entry = row.Entry;
			ImGui.TableNextRow(ImGuiTableRowFlags.None, rowHeight);
			NVector4 filterColor = row.Filter >= 0 && row.Filter < prefs.Filters.Count ? ColorHex.Parse(prefs.Filters[row.Filter].Color, default) : default;
			if (row.Filter >= 0)
			{
				ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.ColorConvertFloat4ToU32(filterColor * new NVector4(1f, 1f, 1f, 0.14f)));
			}

			ImGui.TableNextColumn();
			ImGui.PushID((int)entry.Id);
			NVector2 cell = ImGui.GetCursorScreenPos();
			float padding = ImGui.GetStyle().CellPadding.Y;
			if (ImGui.Selectable("##row", entry.Id == selectedId, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowOverlap | ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, rowHeight - padding * 2f)))
			{
				Select(entry, true);
				if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					OpenSource(entry);
				}
			}

			if (TestAnchors.Enabled)
			{
				TestAnchors.Record("console:" + index, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
			}

			if (ImGui.BeginPopupContextItem("##rowmenu"))
			{
				if (selectedId != entry.Id)
				{
					Select(entry, false);
				}

				DrawRowMenu(entry);
				ImGui.EndPopup();
			}

			if (row.Filter >= 0)
			{
				float x = cell.X - ImGui.GetStyle().CellPadding.X;
				draw.AddRectFilled(new NVector2(x, cell.Y - padding), new NVector2(x + 3f, cell.Y - padding + rowHeight), ImGui.ColorConvertFloat4ToU32(filterColor));
			}

			float lineHeight = ImGui.GetTextLineHeight();
			DrawLogIcon(draw, new NVector2(cell.X + lineHeight * 0.5f + 1f, cell.Y + lineHeight * 0.5f), lineHeight * 0.36f, entry.Type, 1f);

			if (shown.Time)
			{
				ImGui.TableNextColumn();
				ImGui.TextDisabled(entry.Time.ToString("HH:mm:ss.fff"));
			}

			if (shown.Frame)
			{
				ImGui.TableNextColumn();
				if (entry.Frame >= 0)
				{
					ImGui.TextDisabled(entry.Frame.ToString());
				}
			}

			ImGui.TableNextColumn();
			bool colored = entry.Type != LogType.Log;
			if (colored)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, entry.Type == LogType.Warning ? EditorStyle.Warning : EditorStyle.Error);
			}

			ImGui.TextUnformatted(prefs.LinesPerEntry == 1 ? entry.FirstLine : FirstLines(entry.Text, prefs.LinesPerEntry));
			if (colored)
			{
				ImGui.PopStyleColor();
			}

			if (shown.Source)
			{
				ImGui.TableNextColumn();
				IReadOnlyList<ConsoleFrame> frames = entry.Frames;
				int source = ConsoleStackTrace.SourceFrame(frames);
				if (source >= 0)
				{
					ImGui.TextColored(EditorStyle.Dim, Path.GetFileName(frames[source].File) + ":" + frames[source].Line);
				}
			}

			if (shown.Count)
			{
				ImGui.TableNextColumn();
				if (row.Count > 1)
				{
					string count = row.Count > 99999 ? "99999+" : row.Count.ToString();
					NVector2 size = ImGui.CalcTextSize(count) + new NVector2(8f, 0f);
					NVector2 pos = ImGui.GetCursorScreenPos();
					draw.AddRectFilled(pos, pos + size, ImGui.GetColorU32(ImGuiCol.Button), size.Y * 0.5f);
					draw.AddText(pos + new NVector2(4f, 0f), ImGui.GetColorU32(ImGuiCol.Text), count);
					ImGui.Dummy(size);
				}
			}

			ImGui.PopID();
		}

		private void DrawRowMenu(ConsoleEntry entry)
		{
			if (ImGui.MenuItem("Copy", "Ctrl+C"))
			{
				ImGui.SetClipboardText(entry.ToFullText());
			}

			if (ImGui.MenuItem("Copy Message Only"))
			{
				ImGui.SetClipboardText(entry.Text);
			}

			ImGui.Separator();
			IReadOnlyList<ConsoleFrame> frames = entry.Frames;
			int source = ConsoleStackTrace.SourceFrame(frames);
			if (ImGui.MenuItem(source >= 0 ? "Open " + Path.GetFileName(frames[source].File) + ":" + frames[source].Line : "Open Source", "Enter", false, source >= 0))
			{
				OpenSource(entry);
			}

			object context = entry.GetContext();
			if (ImGui.MenuItem("Select " + (context != null ? ContextName(context) : "Context Object"), "", false, context != null))
			{
				editor.SelectObject(context);
			}

			ConsolePreferences prefs = EditorPreferences.Current.Console;
			string tag = entry.Tag;
			if (tag != null)
			{
				ImGui.Separator();
				if (ImGui.MenuItem("Hide " + tag + " Messages"))
				{
					prefs.HiddenTags.Add(tag);
					Changed();
				}

				if (ImGui.MenuItem("New Filter for " + tag))
				{
					AddFilter(prefs, tag.Trim('[', ']'), tag, false);
				}
			}

			if (ImGui.MenuItem("New Filter from This Message"))
			{
				string line = entry.FirstLine;
				AddFilter(prefs, "", line.Length > 60 ? line.Substring(0, 60) : line, false);
			}

			ImGui.Separator();
			if (ImGui.MenuItem("Clear Console"))
			{
				editor.Log.Clear();
			}
		}

		// ---------------------------------------------------------------- details

		private void DrawDetails(ConsolePreferences prefs)
		{
			ConsoleEntry entry = SelectedEntry();
			if (entry == null)
			{
				ImGui.TextDisabled("Select a message to see its call stack.");
				return;
			}

			if (ImGui.BeginChild("##details", NVector2.Zero, ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar))
			{
				DrawStack(prefs, entry, entry.Frames);
			}

			ImGui.EndChild();
		}

		/// <summary>
		///     The message, when and what about, then its call stack with the code around each frame's line inline (the
		///     project's frames and the one the message opens show it; a click shows or hides it on the others).
		/// </summary>
		private void DrawStack(ConsolePreferences prefs, ConsoleEntry entry, IReadOnlyList<ConsoleFrame> frames)
		{
			// The message, whole.
			bool colored = entry.Type != LogType.Log;
			if (colored)
			{
				ImGui.PushStyleColor(ImGuiCol.Text, entry.Type == LogType.Warning ? EditorStyle.Warning : EditorStyle.Error);
			}

			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted(entry.Text);
			ImGui.PopTextWrapPos();
			if (colored)
			{
				ImGui.PopStyleColor();
			}

			if (ImGui.BeginPopupContextItem("##messagemenu"))
			{
				if (ImGui.MenuItem("Copy"))
				{
					ImGui.SetClipboardText(entry.ToFullText());
				}

				if (ImGui.MenuItem("Copy Message Only"))
				{
					ImGui.SetClipboardText(entry.Text);
				}

				ImGui.EndPopup();
			}

			// When, on which frame, about what.
			string meta = entry.Time.ToString("HH:mm:ss.fff") + (entry.Frame >= 0 ? "  ·  frame " + entry.Frame : "") + "  ·  " + entry.Type;
			ImGui.TextDisabled(meta);
			object context = entry.GetContext();
			if (context != null)
			{
				ImGui.SameLine();
				ImGui.TextDisabled("·");
				ImGui.SameLine();
				if (ImGui.TextLink(ContextName(context) + "##context"))
				{
					editor.SelectObject(context);
				}

				Tooltip("Select the object the message is about");
			}

			ImGui.Separator();
			if (frames.Count == 0)
			{
				ImGui.TextDisabled("No call stack (Preferences > Console > Stack traces)");
				return;
			}

			int hiddenLibrary = 0;
			int source = ConsoleStackTrace.SourceFrame(frames);
			float lineHeight = ImGui.GetTextLineHeight();
			float arrow = lineHeight;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			for (int i = 0; i < frames.Count; i++)
			{
				ConsoleFrame frame = frames[i];
				if (frame.Kind == ConsoleFrameKind.Library && !prefs.ShowLibraryFrames)
				{
					hiddenLibrary++;
					continue;
				}

				if (frame.Kind == ConsoleFrameKind.Note)
				{
					ImGui.TextColored(EditorStyle.Dim, frame.Method);
					continue;
				}

				bool wrapper = ConsoleStackTrace.IsHidden(frame);
				bool bright = frame.Kind == ConsoleFrameKind.Project && !wrapper;
				bool expandable = prefs.ShowSourceCode && frame.HasSource;
				bool expanded = expandable && ((bright || i == source) != toggledFrames.Contains(i));
				ImGui.PushID(i);
				NVector2 pos = ImGui.GetCursorScreenPos();
				string method = ConsoleStackTrace.ShortMethod(frame.Method);
				string location = frame.DisplayFile != null ? frame.DisplayFile + ":" + frame.Line : "";
				float width = arrow + ImGui.CalcTextSize(method).X + 12f + ImGui.CalcTextSize(location).X;
				if (ImGui.Selectable("##frame", false, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new NVector2(Math.Max(width, ImGui.GetContentRegionAvail().X), lineHeight)))
				{
					// A click shows or hides the code; a double-click opens it (and leaves the code as it was).
					if (expandable && !toggledFrames.Remove(i))
					{
						toggledFrames.Add(i);
					}

					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && frame.HasSource)
					{
						ExternalScriptEditor.Open(frame.File, frame.Line);
					}
				}

				if (TestAnchors.Enabled)
				{
					TestAnchors.Record("consoleframe:" + i, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip(frame.Method + (frame.File != null ? "\n" + frame.File + ":" + frame.Line : "") +
						(wrapper ? "\nA logging wrapper (Preferences > Console > Hidden frames)" : "") + (frame.HasSource ? "\nDouble-click: open in the script editor" : ""));
				}

				if (ImGui.BeginPopupContextItem("##framemenu"))
				{
					if (ImGui.MenuItem("Open in Script Editor", "", false, frame.HasSource))
					{
						ExternalScriptEditor.Open(frame.File, frame.Line);
					}

					if (ImGui.MenuItem("Copy Frame"))
					{
						ImGui.SetClipboardText(frame.Method + (frame.File != null ? " in " + frame.File + ":line " + frame.Line : ""));
					}

					string type = TypeOf(frame.Method);
					if (ImGui.MenuItem(wrapper ? "Show Frames of " + type : "Hide Frames of " + type + " (logging wrapper)", "", false, type.Length > 0))
					{
						if (wrapper)
						{
							prefs.HiddenFrames.RemoveAll(prefix => frame.Method.StartsWith(prefix.Trim(), StringComparison.Ordinal));
						}
						else
						{
							prefs.HiddenFrames.Add(type + ".");
						}

						Changed();
					}

					ImGui.EndPopup();
				}

				if (expandable)
				{
					DrawArrow(draw, new NVector2(pos.X + arrow * 0.5f, pos.Y + lineHeight * 0.5f), arrow * 0.22f, expanded, ImGui.GetColorU32(ImGuiCol.TextDisabled));
				}

				var textPos = new NVector2(pos.X + arrow, pos.Y);
				draw.AddText(textPos, ImGui.GetColorU32(bright ? ImGuiCol.Text : ImGuiCol.TextDisabled), method);
				if (location.Length > 0)
				{
					NVector4 linkColor = frame.HasSource ? EditorStyle.Get(EditorColor.Link) : EditorStyle.Dim;
					draw.AddText(textPos + new NVector2(ImGui.CalcTextSize(method).X + 12f, 0f), ImGui.ColorConvertFloat4ToU32(bright ? linkColor : linkColor * new NVector4(1f, 1f, 1f, 0.6f)), location);
				}

				if (expanded)
				{
					SourceSnippet.Draw(frame.File, frame.Line, prefs.SourceContextLines, arrow);
				}

				ImGui.PopID();
			}

			if (hiddenLibrary > 0)
			{
				ImGui.TextDisabled($"{hiddenLibrary} frame{(hiddenLibrary == 1 ? "" : "s")} of .NET and libraries hidden");
				ImGui.SameLine();
				if (ImGui.TextLink("Show##libraryframes"))
				{
					prefs.ShowLibraryFrames = true;
					Changed();
				}
			}
		}

		// ---------------------------------------------------------------- selection

		private void Select(ConsoleEntry entry, bool ping)
		{
			if (selectedId != entry.Id)
			{
				toggledFrames.Clear();
			}

			selectedId = entry.Id;
			object context = entry.GetContext();
			if (ping && context != null)
			{
				editor.Ping(context);
			}
		}

		private ConsoleEntry SelectedEntry()
		{
			if (selectedId < 0)
			{
				return null;
			}

			int index = RowIndex(selectedId);
			if (index >= 0)
			{
				return rows[index].Entry;
			}

			// Hidden by a filter since: still shown below while it is in the log.
			foreach (ConsoleEntry entry in entries)
			{
				if (entry.Id == selectedId)
				{
					return entry;
				}
			}

			return null;
		}

		private int RowIndex(long id)
		{
			for (int i = 0; i < rows.Count; i++)
			{
				if (rows[i].Entry.Id == id)
				{
					return i;
				}
			}

			return -1;
		}

		private void HandleKeys()
		{
			if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) || ImGui.GetIO().WantTextInput || rows.Count == 0)
			{
				return;
			}

			int index = RowIndex(selectedId);
			int moved = index;
			if (ImGui.IsKeyPressed(ImGuiKey.DownArrow))
			{
				moved = Math.Min(rows.Count - 1, index + 1);
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.UpArrow))
			{
				moved = index < 0 ? rows.Count - 1 : Math.Max(0, index - 1);
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.Home))
			{
				moved = 0;
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.End))
			{
				moved = rows.Count - 1;
			}

			if (moved != index && moved >= 0)
			{
				Select(rows[moved].Entry, true);
				scrollToSelection = true;
			}

			ConsoleEntry selected = index >= 0 ? rows[index].Entry : null;
			if (selected != null && ImGui.GetIO().KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C))
			{
				ImGui.SetClipboardText(selected.ToFullText());
			}

			if (selected != null && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter)))
			{
				OpenSource(selected);
			}
		}

		private static void OpenSource(ConsoleEntry entry)
		{
			IReadOnlyList<ConsoleFrame> frames = entry.Frames;
			int source = ConsoleStackTrace.SourceFrame(frames);
			if (source >= 0)
			{
				ExternalScriptEditor.Open(frames[source].File, frames[source].Line);
			}
		}

		// ---------------------------------------------------------------- export

		private string ShownText()
		{
			var text = new StringBuilder();
			foreach (Row row in rows)
			{
				ConsoleEntry entry = row.Entry;
				text.Append(entry.Time.ToString("HH:mm:ss.fff")).Append(" [").Append(entry.Type).Append("] ");
				if (row.Count > 1)
				{
					text.Append('(').Append(row.Count).Append("x) ");
				}

				text.AppendLine(entry.ToFullText());
			}

			return text.ToString();
		}

		/// <summary>The shown messages to Logs/Console-date.txt of the project.</summary>
		private void SaveShown()
		{
			try
			{
				string folder = Path.Combine(editor.Project.RootPath, "Logs");
				Directory.CreateDirectory(folder);
				string file = Path.Combine(folder, "Console-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
				File.WriteAllText(file, ShownText());
				Debug.Log("[Console] Saved " + rows.Count + " messages to " + file);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[Console] Cannot save the messages: " + e.Message);
			}
		}

		// ---------------------------------------------------------------- widgets

		/// <summary>A preference changed: saved, rows rebuilt.</summary>
		private void Changed()
		{
			EditorPreferences.MarkChanged();
			rowsDirty = true;
		}

		/// <summary>The next item on this line when <paramref name="width" /> fits, else on the next.</summary>
		private static void Flow(float width)
		{
			ImGui.SameLine();
			if (ImGui.GetContentRegionAvail().X < width)
			{
				ImGui.NewLine();
			}
		}

		private bool Toggle(string label, ref bool value, string tooltip)
		{
			Flow(ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f);
			bool on = value;
			if (on)
			{
				ImGui.PushStyleColor(ImGuiCol.Button, EditorStyle.Accent);
				ImGui.PushStyleColor(ImGuiCol.ButtonHovered, EditorStyle.Accent * new NVector4(1.15f, 1.15f, 1.15f, 1f));
			}

			bool clicked = ImGui.Button(label);
			if (on)
			{
				ImGui.PopStyleColor(2);
			}

			Tooltip(tooltip);
			if (clicked)
			{
				value = !value;
				Changed();
			}

			return clicked;
		}

		private bool SmallToggle(string label, ref bool value, string tooltip)
		{
			bool on = value;
			ImGui.PushStyleColor(ImGuiCol.Button, on ? EditorStyle.Accent : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
			ImGui.PushStyleColor(ImGuiCol.Text, on ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
			bool clicked = ImGui.Button(label);
			ImGui.PopStyleColor(2);
			Tooltip(tooltip);
			if (clicked)
			{
				value = !value;
				EditorPreferences.MarkChanged();
			}

			return clicked;
		}

		private bool TypeToggle(string id, LogType type, int count, ref bool value, string tooltip)
		{
			string text = count > 9999 ? "9999+" : count.ToString();
			NVector2 padding = ImGui.GetStyle().FramePadding;
			float height = ImGui.GetFrameHeight();
			float icon = ImGui.GetTextLineHeight() * 0.72f;
			var size = new NVector2(padding.X * 2f + icon + 5f + ImGui.CalcTextSize(text).X, height);
			Flow(size.X);
			NVector2 pos = ImGui.GetCursorScreenPos();
			bool clicked = ImGui.InvisibleButton(id, size);
			bool on = value;
			ImGuiCol background = ImGui.IsItemActive() ? ImGuiCol.ButtonActive : ImGui.IsItemHovered() ? ImGuiCol.ButtonHovered : ImGuiCol.Button;
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector4 color = ImGui.GetStyle().Colors[(int)background];
			draw.AddRectFilled(pos, pos + size, ImGui.ColorConvertFloat4ToU32(on ? color : color * new NVector4(1f, 1f, 1f, 0.35f)), ImGui.GetStyle().FrameRounding);
			DrawLogIcon(draw, new NVector2(pos.X + padding.X + icon * 0.5f, pos.Y + height * 0.5f), icon * 0.5f, type, on ? 1f : 0.35f);
			draw.AddText(new NVector2(pos.X + padding.X + icon + 5f, pos.Y + padding.Y), ImGui.GetColorU32(on ? ImGuiCol.Text : ImGuiCol.TextDisabled), text);
			Tooltip(tooltip + (on ? " (shown)" : " (hidden)"));
			if (clicked)
			{
				value = !value;
				EditorPreferences.MarkChanged();
			}

			return clicked;
		}

		private static bool MenuToggle(string label, ref bool value)
		{
			if (!ImGui.MenuItem(label, "", value))
			{
				return false;
			}

			value = !value;
			return true;
		}

		/// <summary>A horizontal (or <paramref name="vertical" />) bar dragged to resize; true when the drag ends, <paramref name="delta" /> while it moves.</summary>
		private static bool Splitter(string id, bool vertical, float length, out float delta)
		{
			NVector2 size = vertical ? new NVector2(SplitterThickness, length) : new NVector2(length, SplitterThickness);
			NVector2 pos = ImGui.GetCursorScreenPos();
			ImGui.InvisibleButton(id, size);
			bool active = ImGui.IsItemActive();
			bool hovered = ImGui.IsItemHovered();
			if (active || hovered)
			{
				ImGui.SetMouseCursor(vertical ? ImGuiMouseCursor.ResizeEw : ImGuiMouseCursor.ResizeNs);
			}

			uint color = ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : hovered ? ImGuiCol.SeparatorHovered : ImGuiCol.Separator);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			if (vertical)
			{
				float x = pos.X + size.X * 0.5f;
				draw.AddLine(new NVector2(x, pos.Y), new NVector2(x, pos.Y + size.Y), color, active || hovered ? 2f : 1f);
			}
			else
			{
				float y = pos.Y + size.Y * 0.5f;
				draw.AddLine(new NVector2(pos.X, y), new NVector2(pos.X + size.X, y), color, active || hovered ? 2f : 1f);
			}

			NVector2 mouse = ImGui.GetIO().MouseDelta;
			delta = active ? (vertical ? mouse.X : mouse.Y) : 0f;
			return ImGui.IsItemDeactivated();
		}

		/// <summary>A small triangle pointing right (closed) or down (open).</summary>
		private static void DrawArrow(ImDrawListPtr draw, NVector2 c, float r, bool open, uint color)
		{
			if (open)
			{
				draw.AddTriangleFilled(new NVector2(c.X - r, c.Y - r * 0.6f), new NVector2(c.X + r, c.Y - r * 0.6f), new NVector2(c.X, c.Y + r * 0.8f), color);
			}
			else
			{
				draw.AddTriangleFilled(new NVector2(c.X - r * 0.6f, c.Y - r), new NVector2(c.X + r * 0.8f, c.Y), new NVector2(c.X - r * 0.6f, c.Y + r), color);
			}
		}

		/// <summary>The type's icon: a gray (i), a yellow /!\, a red (x) for errors and (!) for exceptions.</summary>
		private static void DrawLogIcon(ImDrawListPtr draw, NVector2 c, float r, LogType type, float alpha)
		{
			uint glyph = ImGui.ColorConvertFloat4ToU32(new NVector4(0.1f, 0.1f, 0.11f, alpha));
			float w = Math.Max(1f, r * 0.17f);
			switch (type)
			{
				case LogType.Warning:
				{
					draw.AddTriangleFilled(new NVector2(c.X, c.Y - r * 1.05f), new NVector2(c.X + r * 1.1f, c.Y + r * 0.9f), new NVector2(c.X - r * 1.1f, c.Y + r * 0.9f), EditorStyle.GetU32(EditorColor.Warning, alpha));
					draw.AddRectFilled(new NVector2(c.X - w, c.Y - r * 0.4f), new NVector2(c.X + w, c.Y + r * 0.3f), glyph);
					draw.AddCircleFilled(new NVector2(c.X, c.Y + r * 0.58f), w * 1.15f, glyph);
					break;
				}
				case LogType.Error:
				{
					draw.AddCircleFilled(c, r, EditorStyle.GetU32(EditorColor.Error, alpha));
					float d = r * 0.42f;
					draw.AddLine(new NVector2(c.X - d, c.Y - d), new NVector2(c.X + d, c.Y + d), glyph, w * 2f);
					draw.AddLine(new NVector2(c.X + d, c.Y - d), new NVector2(c.X - d, c.Y + d), glyph, w * 2f);
					break;
				}
				case LogType.Exception:
				{
					draw.AddCircleFilled(c, r, EditorStyle.GetU32(EditorColor.Error, alpha));
					draw.AddRectFilled(new NVector2(c.X - w, c.Y - r * 0.62f), new NVector2(c.X + w, c.Y + r * 0.2f), glyph);
					draw.AddCircleFilled(new NVector2(c.X, c.Y + r * 0.5f), w * 1.15f, glyph);
					break;
				}
				default:
				{
					draw.AddCircleFilled(c, r, EditorStyle.GetU32(EditorColor.Info, alpha));
					draw.AddCircleFilled(new NVector2(c.X, c.Y - r * 0.5f), w * 1.15f, glyph);
					draw.AddRectFilled(new NVector2(c.X - w, c.Y - r * 0.18f), new NVector2(c.X + w, c.Y + r * 0.62f), glyph);
					break;
				}
			}
		}

		private static void Tooltip(string text)
		{
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
			{
				ImGui.SetTooltip(text);
			}
		}

		private static string FirstLines(string text, int count)
		{
			int end = -1;
			for (int i = 0; i < count; i++)
			{
				int next = text.IndexOf('\n', end + 1);
				if (next < 0)
				{
					return text;
				}

				end = next;
			}

			return text.Substring(0, end).TrimEnd('\r');
		}

		/// <summary>Namespace.Type of a frame's method (logging wrappers are hidden by it).</summary>
		private static string TypeOf(string method)
		{
			int paren = method.IndexOf('(');
			string name = paren >= 0 ? method.Substring(0, paren) : method;
			int dot = name.LastIndexOf('.');
			return dot > 0 ? name.Substring(0, dot) : "";
		}

		private static string ContextName(object context)
		{
			return context switch
			{
				GameObject go => go.Name,
				Behaviour component => (component.gameObject?.Name ?? "?") + " (" + component.GetType().Name + ")",
				IAsset asset when asset.AssetPath != null => asset.AssetPath,
				_ => context.ToString()
			};
		}

		#endregion

		#region NestedTypes

		private struct Row
		{
			/// <summary>The message shown (the first of identical ones when collapsed).</summary>
			public ConsoleEntry Entry;

			public int Count;

			/// <summary>The first shown filter matching it (its color), or -1.</summary>
			public int Filter;
		}

		/// <summary>The list's optional columns this frame (the preferences', those that fit).</summary>
		private struct Columns
		{
			public bool Time;
			public bool Frame;
			public bool Source;
			public bool Count;
		}

		#endregion
	}
}
