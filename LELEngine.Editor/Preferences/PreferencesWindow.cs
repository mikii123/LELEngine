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
	///     tags), External Tools (the script editor), Play Sessions (recording play mode, how long sessions are kept, the
	///     play session server they are sent to; the open project's sessions listed) and Cache Server (the library cache server: address, access key, which way results
	///     go, a connection test, this session's transfers). Changes apply at once and are saved a moment later.
	/// </summary>
	internal sealed class PreferencesWindow : IEditorPanel
	{
		#region PublicFields

		public enum Section
		{
			Theme,
			Console,
			ExternalTools,
			PlaySessions,
			CacheServer
		}

		public bool Open;

		string IEditorPanel.PanelName => "Preferences";
		string IEditorPanel.ImGuiName => "Preferences";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(820, 620);
		void IEditorPanel.Focus() => EditorApplication.Instance.ShowPreferences();

		#endregion

		#region PrivateFields

		private static readonly string[] SectionNames = { "Theme", "Console", "External Tools", "Play Sessions", "Cache Server" };
		private static readonly string[] StackTraceNames = { "None", "Script Only", "Full" };

		private Section section = Section.Theme;
		private bool focus;
		private string colorFilter = "";
		private float labelWidth;
		private bool showAccessKey;
		private bool showSessionKey;
		private int listedSent = -1;

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
					if (EditorGui.Selectable(SectionNames[i], (int)section == i))
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
					EditorGui.SetTooltip(EditorPreferences.File ?? "An automated run: nothing is saved");
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
					case Section.PlaySessions:
						DrawPlaySessions(EditorPreferences.Current.PlaySessions);
						break;
					case Section.CacheServer:
						DrawCacheServer(EditorPreferences.Current.CacheServer);
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
			if (EditorGui.BeginCombo("##preset", theme.Preset))
			{
				foreach (string preset in EditorStyle.Presets)
				{
					if (EditorGui.Selectable(preset, preset == theme.Preset))
					{
						theme.Preset = preset;
						EditorPreferences.MarkChanged(true);
					}
				}

				EditorGui.EndCombo();
			}

			Label("Text size", "Size of the editor's text, in pixels");
			if (EditorGui.SliderFloat("##fontsize", ref theme.FontSize, 12f, 24f, "%.0f px"))
			{
				theme.FontSize = MathF.Round(theme.FontSize);
				EditorPreferences.MarkChanged(true);
			}

			Label("Code text size", "Size of source code (the console's source view)");
			if (EditorGui.SliderFloat("##codefontsize", ref theme.CodeFontSize, 10f, 24f, "%.0f px"))
			{
				theme.CodeFontSize = MathF.Round(theme.CodeFontSize);
				EditorPreferences.MarkChanged(true);
			}

			ImGui.SeparatorText("Colors");
			ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Reset All").X - ImGui.GetStyle().FramePadding.X * 2f - ImGui.GetStyle().ItemSpacing.X));
			EditorGui.InputTextWithHint("##colorfilter", "Filter colors", ref colorFilter, 64);
			ImGui.SameLine();
			ImGui.BeginDisabled(theme.Colors.Count == 0);
			if (EditorGui.Button("Reset All"))
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
			if (!EditorGui.CollapsingHeader(title + "##" + title))
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
				if (EditorGui.ColorEdit4("##value", ref value, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreviewHalf | ImGuiColorEditFlags.DisplayHex))
				{
					theme.Colors[name] = ColorHex.ToHex(value);
					EditorPreferences.MarkChanged(true);
				}

				ImGui.TableNextColumn();
				if (changed && EditorGui.SmallButton("Reset"))
				{
					theme.Colors.Remove(name);
					EditorPreferences.MarkChanged(true);
				}

				if (changed && ImGui.IsItemHovered())
				{
					NVector4 preset = EditorStyle.PresetValue(theme.Preset, name);
					EditorGui.SetTooltip("Back to " + theme.Preset + "'s " + ColorHex.ToHex(preset));
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
			ImGui.SeparatorText("Messages");
			changed |= Check("Editor messages", ref console.ShowEditorMessages, "Plain messages of the editor and the engine outside the game (imports, rendering setup, ...). Their warnings and errors always show; the game's messages are filtered in the console");
			ImGui.TextDisabled("The game's messages: the project's scripts, and anything logged while the game runs.");
			ImGui.SeparatorText("List");
			changed |= Check("Timestamps", ref console.ShowTimestamps, "The time of each message");
			changed |= Check("Frame numbers", ref console.ShowFrames, "The frame (Time.frameCount) of messages logged in play mode");
			changed |= Check("Source column", ref console.ShowSourceColumn, "The file and line a message came from");
			Label("Lines per message", "Lines of a message's text each row shows");
			changed |= EditorGui.SliderInt("##lines", ref console.LinesPerEntry, 1, 3);
			Label("Messages kept", "Older messages go beyond this many");
			if (EditorGui.InputInt("##max", ref console.MaxEntries, 1000, 10000))
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
			changed |= EditorGui.SliderInt("##context", ref console.SourceContextLines, 0, 10);

			ImGui.SeparatorText("Custom filters");
			ImGui.TextDisabled("Toggles in the console's toolbar: off hides the messages a filter matches; matches get its color.");
			changed |= DrawFilters(console.Filters);

			if (console.HiddenTags.Count > 0)
			{
				ImGui.SeparatorText("Hidden tags");
				for (int i = 0; i < console.HiddenTags.Count; i++)
				{
					ImGui.PushID("tag" + i);
					if (EditorGui.SmallButton("Show"))
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
					changed |= EditorGui.Checkbox("##shown", ref filter.Shown);
					ImGui.TableNextColumn();
					NVector4 color = ColorHex.Parse(filter.Color, new NVector4(0.3f, 0.6f, 1f, 1f));
					if (EditorGui.ColorEdit4("##color", ref color, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
					{
						filter.Color = ColorHex.ToHex(color);
						changed = true;
					}

					ImGui.TableNextColumn();
					ImGui.SetNextItemWidth(-1f);
					changed |= EditorGui.InputTextWithHint("##name", "(the pattern)", ref filter.Name, 64);
					ImGui.TableNextColumn();
					ImGui.SetNextItemWidth(-1f);
					changed |= EditorGui.InputText("##pattern", ref filter.Pattern, 256);
					ImGui.TableNextColumn();
					changed |= EditorGui.Checkbox("##regex", ref filter.Regex);
					ImGui.TableNextColumn();
					changed |= EditorGui.Checkbox("##case", ref filter.MatchCase);
					ImGui.TableNextColumn();
					changed |= EditorGui.Checkbox("##stack", ref filter.InStackTrace);
					ImGui.TableNextColumn();
					if (EditorGui.SmallButton("x"))
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

			if (EditorGui.Button("Add Filter"))
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
			if (EditorGui.BeginCombo("##scripteditor", EditorName(tools.ScriptEditor)))
			{
				foreach (ScriptEditorKind kind in Enum.GetValues<ScriptEditorKind>())
				{
					if (EditorGui.Selectable(EditorName(kind) + (ExternalScriptEditor.IsInstalled(kind) ? "" : "  (not found)"), kind == tools.ScriptEditor))
					{
						tools.ScriptEditor = kind;
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled(ExternalScriptEditor.Describe(tools.ScriptEditor));
			ImGui.PopTextWrapPos();
			if (tools.ScriptEditor == ScriptEditorKind.Custom)
			{
				Label("Path", "The editor's executable");
				changed |= EditorGui.InputTextWithHint("##path", @"C:\Tools\Editor\editor.exe", ref tools.CustomPath, 512);
				Label("Arguments", "$(File), $(Line), $(Column), $(ProjectPath), $(SolutionPath) are replaced");
				changed |= EditorGui.InputText("##arguments", ref tools.CustomArguments, 512);
				ImGui.TextDisabled("$(File)  $(Line)  $(Column)  $(ProjectPath)  $(SolutionPath)");
			}

			if (changed)
			{
				EditorPreferences.MarkChanged();
			}
		}

		// ---------------------------------------------------------------- play sessions

		private void DrawPlaySessions(PlaySessionPreferences sessions)
		{
			bool changed = false;
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled("Play mode's sessions are recorded into the project's Library/PlaySessions by a thread of their own, a folder each: "
				+ "frames.csv (a row per frame: what the Game view's debug overlay shows and more), events.jsonl (the console's messages with their "
				+ "frame's row, the input, scenes, pauses, hitches, GPU passes) and session.json (where it ran, how it ended, a summary).");
			ImGui.PopTextWrapPos();

			ImGui.SeparatorText("Recording");
			changed |= Check("Record play sessions", ref sessions.Record, "Every play mode session is recorded (from the next one on)");
			ImGui.BeginDisabled(!sessions.Record);
			changed |= Check("Hitch reports", ref sessions.HitchReports, "The hitch profiler runs while a session records: each frame it finds much longer than the ones before "
				+ "gets its report in the session (where the frame's time went, what was loaded or compiled for the first time in it, JIT, GC). Costs a little per frame");
			ImGui.EndDisabled();
			Label("Keep for", "Sessions older than this are deleted when a project opens and after each session (a change applies then, not at once)");
			if (EditorGui.SliderInt("##keepdays", ref sessions.KeepDays, PlaySessionPreferences.MinDays, 90, sessions.KeepDays == 1 ? "%d day" : "%d days", ImGuiSliderFlags.AlwaysClamp))
			{
				changed = true;
			}

			Label("Size limit (MB)", "The oldest sessions also go while all of a project's take more than this (MB; 0: no limit)");
			if (EditorGui.InputInt("##maxtotal", ref sessions.MaxTotalMB, 256, 1024))
			{
				sessions.MaxTotalMB = sessions.MaxTotalMB <= 0 ? 0 : Math.Clamp(sessions.MaxTotalMB, PlaySessionPreferences.MinTotalMB, PlaySessionPreferences.MaxTotalMBLimit);
				changed = true;
			}

			ImGui.SeparatorText("Server");
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled("A play session server (a portal project's play sessions endpoint) keeps the sessions of every editor of the team, their "
				+ "console messages in the project's debug tracing. An AI agent driving an editor reads them with the automation's 'sessions --remote' "
				+ "and 'session fetch <id>' (the address and key below, Upload on or not).");
			ImGui.PopTextWrapPos();
			changed |= Check("Upload sessions", ref sessions.Upload, "Every session is sent to the server when it ends (in the background), and the ones not sent yet when a project opens");
			Label("Address", "The play sessions endpoint of a portal project: https://host/api/{project}/play-sessions");
			changed |= EditorGui.InputTextWithHint("##sessionserver", "https://portal.example.com/api/project/play-sessions", ref sessions.ServerUrl, 512);
			Label("Access key", "Sent with every request (X-Api-Key): a key the server accepts. Kept in the preferences file as it is typed");
			float showWidth = ImGui.CalcTextSize("Hide").X + ImGui.GetStyle().FramePadding.X * 2f;
			ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - showWidth - ImGui.GetStyle().ItemSpacing.X));
			changed |= EditorGui.InputText("##sessionkey", ref sessions.AccessKey, 256, showSessionKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
			ImGui.SameLine();
			if (EditorGui.Button(showSessionKey ? "Hide##sessionkey" : "Show##sessionkey", new NVector2(showWidth, 0)))
			{
				showSessionKey = !showSessionKey;
			}

			// The cache server of the same portal project: its address with the last segment swapped, the same key.
			CacheServerPreferences cache = EditorPreferences.Current.CacheServer;
			string cacheUrl = cache.Url?.Trim().TrimEnd('/') ?? "";
			if (cacheUrl.EndsWith("/library-cache", StringComparison.OrdinalIgnoreCase))
			{
				ImGui.SetCursorPosX(ImGui.GetCursorPosX() + labelWidth);
				if (EditorGui.Button("Use the cache server's project and key"))
				{
					sessions.ServerUrl = cacheUrl.Substring(0, cacheUrl.Length - "library-cache".Length) + "play-sessions";
					sessions.AccessKey = cache.AccessKey ?? "";
					changed = true;
				}
			}

			if (changed)
			{
				EditorPreferences.MarkChanged();
			}

			PlaySessionServer server = PlaySessionServer.Instance;
			if (server.Configured)
			{
				ImGui.TextDisabled($"Sent this session: {server.Sent}; waiting: {server.Waiting}" + (server.Failures > 0 ? $"; failures: {server.Failures}" : ""));
				string serverError = server.LastError;
				if (serverError != null)
				{
					ImGui.PushTextWrapPos(0f);
					ImGui.TextColored(EditorStyle.Warning, serverError);
					ImGui.PopTextWrapPos();
				}
			}

			if (EditorPreferences.File == null)
			{
				ImGui.TextDisabled("An automated run without preferencesfile=: nothing is recorded.");
			}

			ImGui.SeparatorText("This project's sessions");
			PlaySessionRecorder recorder = EditorApplication.Instance?.Sessions;
			if (recorder?.Root == null)
			{
				ImGui.TextDisabled("No project is open.");
				return;
			}

			// A session sent to the server since (its folder got the marker): the list's Sent column follows.
			if (recorder.ListingVersion != recorder.Version || listedSent != server.Sent)
			{
				listedSent = server.Sent;
				recorder.RequestListing();
			}

			List<PlaySessionListing> listing = recorder.Listing;
			ImGui.AlignTextToFramePadding();
			ImGui.TextUnformatted(recorder.Root);
			if (EditorGui.Button("Open Folder"))
			{
				System.IO.Directory.CreateDirectory(recorder.Root);
				ShowInExplorer(recorder.Root);
			}

			ImGui.SameLine();
			if (EditorGui.Button("Refresh"))
			{
				recorder.RequestListing();
			}

			ImGui.SameLine();
			ImGui.BeginDisabled(listing == null || listing.Count == 0);
			if (EditorGui.Button("Delete All..."))
			{
				ImGui.OpenPopup("Delete the recorded play sessions?");
			}

			ImGui.EndDisabled();
			if (ImGui.BeginPopupModal("Delete the recorded play sessions?", ImGuiWindowFlags.AlwaysAutoResize))
			{
				ImGui.TextUnformatted($"Every recorded session of this project ({listing?.Count ?? 0}) is deleted from disk; this cannot be undone.");
				ImGui.TextDisabled("A session recording now is kept.");
				if (EditorGui.Button("Delete"))
				{
					recorder.DeleteAll();
					ImGui.CloseCurrentPopup();
				}

				ImGui.SameLine();
				if (EditorGui.Button("Cancel"))
				{
					ImGui.CloseCurrentPopup();
				}

				ImGui.EndPopup();
			}

			if (listing == null)
			{
				ImGui.TextDisabled("Reading...");
				return;
			}

			long bytes = 0;
			foreach (PlaySessionListing session in listing)
			{
				bytes += session.Bytes;
			}

			ImGui.TextDisabled($"{listing.Count} session{(listing.Count == 1 ? "" : "s")}, {bytes / (1024.0 * 1024.0):0.0} MB");
			if (listing.Count == 0 || !ImGui.BeginTable("##sessions", 10, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY, new NVector2(0f, Math.Max(160f, ImGui.GetContentRegionAvail().Y))))
			{
				return;
			}

			ImGui.TableSetupScrollFreeze(0, 1);
			ImGui.TableSetupColumn("Started");
			ImGui.TableSetupColumn("Scene", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Length");
			ImGui.TableSetupColumn("Frame ms");
			ImGui.TableSetupColumn("Worst");
			ImGui.TableSetupColumn("Hitches");
			ImGui.TableSetupColumn("Errors");
			ImGui.TableSetupColumn("Ended", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Sent");
			ImGui.TableSetupColumn("");
			ImGui.TableHeadersRow();
			for (int i = 0; i < listing.Count; i++)
			{
				PlaySessionListing session = listing[i];
				ImGui.PushID(i);
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.AlignTextToFramePadding();
				ImGui.TextUnformatted(session.Started.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(System.IO.Path.GetFileNameWithoutExtension(session.Scene ?? ""));
				if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(session.Scene))
				{
					EditorGui.SetTooltip(session.Scene);
				}

				ImGui.TableNextColumn();
				ImGui.TextUnformatted(session.Recording ? "recording" : Duration(session.Seconds));
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(session.AverageFrameMs > 0 ? session.AverageFrameMs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : "-");
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(session.WorstFrameMs > 0 ? session.WorstFrameMs.ToString("0", System.Globalization.CultureInfo.InvariantCulture) : "-");
				ImGui.TableNextColumn();
				ImGui.TextUnformatted(session.Hitches.ToString(System.Globalization.CultureInfo.InvariantCulture));
				ImGui.TableNextColumn();
				int errors = session.Errors + session.Exceptions;
				if (errors > 0)
				{
					ImGui.TextColored(EditorStyle.Error, errors.ToString(System.Globalization.CultureInfo.InvariantCulture));
				}
				else if (session.Warnings > 0)
				{
					ImGui.TextColored(EditorStyle.Warning, session.Warnings + " w");
				}
				else
				{
					ImGui.TextDisabled("0");
				}

				if (ImGui.IsItemHovered())
				{
					EditorGui.SetTooltip($"{session.Errors} errors, {session.Exceptions} exceptions, {session.Warnings} warnings");
				}

				ImGui.TableNextColumn();
				string ended = session.Recording ? "" : session.EndReason ?? "";
				if (session.Interrupted)
				{
					ImGui.TextColored(EditorStyle.Error, "interrupted");
				}
				else
				{
					ImGui.TextUnformatted(ended);
				}

				if (ImGui.IsItemHovered() && ended.Length > 0)
				{
					EditorGui.SetTooltip(ended);
				}

				ImGui.TableNextColumn();
				if (session.SentTo != null)
				{
					ImGui.TextUnformatted("yes");
					if (ImGui.IsItemHovered())
					{
						EditorGui.SetTooltip(session.SentTo + "\nid " + session.Id);
					}
				}
				else
				{
					ImGui.TextDisabled("-");
				}

				ImGui.TableNextColumn();
				if (EditorGui.SmallButton("Show"))
				{
					ShowInExplorer(System.IO.Path.Combine(session.Folder, PlaySessionWriter.HeaderFile));
				}

				ImGui.PopID();
			}

			ImGui.EndTable();
		}

		// ---------------------------------------------------------------- cache server

		private void DrawCacheServer(CacheServerPreferences server)
		{
			bool changed = false;
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled("A library cache server shares the Library's import results between the editors of a team: model imports, images baked "
				+ "as builds ship them and the meshes' distance fields. An import missing from the Library takes the server's result instead of "
				+ "making it (a fresh checkout's full import, a high quality bake), and what this editor makes is sent to it in the background. "
				+ "Results are kept by the content of the files they come from, their import settings and the importer's version, never by path "
				+ "or file time.");
			ImGui.PopTextWrapPos();

			ImGui.SeparatorText("Server");
			changed |= Check("Use a cache server", ref server.Enabled, "Imports ask the server and send it what they make; off: the Library alone");
			ImGui.BeginDisabled(!server.Enabled);
			Label("Address", "The library cache endpoint of a portal project: https://host/api/{project}/library-cache");
			changed |= EditorGui.InputTextWithHint("##url", "https://portal.example.com/api/project/library-cache", ref server.Url, 512);
			Label("Access key", "Sent with every request (X-Api-Key): a key the server accepts. Kept in the preferences file as it is typed");
			float showWidth = ImGui.CalcTextSize("Hide").X + ImGui.GetStyle().FramePadding.X * 2f;
			ImGui.SetNextItemWidth(Math.Max(80f, ImGui.GetContentRegionAvail().X - showWidth - ImGui.GetStyle().ItemSpacing.X));
			changed |= EditorGui.InputText("##accesskey", ref server.AccessKey, 256, showAccessKey ? ImGuiInputTextFlags.None : ImGuiInputTextFlags.Password);
			ImGui.SameLine();
			if (EditorGui.Button(showAccessKey ? "Hide" : "Show", new NVector2(showWidth, 0)))
			{
				showAccessKey = !showAccessKey;
			}

			changed |= Check("Download", ref server.Download, "An import missing from the Library takes the server's result before making it here");
			changed |= Check("Upload", ref server.Upload, "Results made here are sent to the server in the background (what it has already is not sent)");
			if (changed)
			{
				EditorPreferences.MarkChanged();
			}

			Label("Connection", "Asks the server for its status with this address and key");
			if (EditorGui.Button("Test Connection"))
			{
				LibraryCacheServer.Instance.TestConnection();
			}

			string test = LibraryCacheServer.Instance.ConnectionTest;
			if (test != null)
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(test == "..." ? "Asking the server..." : test);
				ImGui.PopTextWrapPos();
			}

			ImGui.EndDisabled();

			ImGui.SeparatorText("This session");
			LibraryCacheServer cache = LibraryCacheServer.Instance;
			switch (cache.State)
			{
				case LibraryCacheServer.ServerState.Off:
					ImGui.TextDisabled(EditorPreferences.File == null ? "Off: an automated run without preferencesfile= uses no server." : "Off.");
					break;
				case LibraryCacheServer.ServerState.Ready:
					ImGui.TextUnformatted(server.Download || server.Upload ? "In use." : "Set up, but neither downloads nor uploads.");
					break;
				case LibraryCacheServer.ServerState.Unreachable:
					ImGui.TextColored(EditorStyle.Warning, $"Unreachable: imports make their results here; asked again {LibraryCacheServer.RetryAfterSeconds} s after the last try.");
					break;
				case LibraryCacheServer.ServerState.Refused:
					ImGui.TextColored(EditorStyle.Error, "The server refused the access key: not asked again until the preferences change (or Test Connection).");
					break;
			}

			ImGui.TextUnformatted($"Downloaded: {cache.Fetched} result{(cache.Fetched == 1 ? "" : "s")}, {LibraryCacheServer.Megabytes(cache.FetchedBytes)}; not on the server: {cache.Misses}");
			ImGui.TextUnformatted($"Uploaded: {cache.Sent} result{(cache.Sent == 1 ? "" : "s")}, {LibraryCacheServer.Megabytes(cache.SentBytes)}; the server had them already: {cache.AlreadyStored}; waiting: {cache.Waiting}");
			if (cache.Failures > 0)
			{
				ImGui.TextColored(EditorStyle.Warning, $"Failures: {cache.Failures}");
			}

			string error = cache.LastError;
			if (error != null)
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextColored(EditorStyle.Warning, error);
				ImGui.PopTextWrapPos();
			}
		}

		private static string Duration(double seconds)
		{
			var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
			return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}";
		}

		private static void ShowInExplorer(string path)
		{
			try
			{
				string arguments = System.IO.File.Exists(path) ? "/select,\"" + path + "\"" : "\"" + path + "\"";
				System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot open Explorer: " + e.Message);
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
				EditorGui.SetTooltip(tooltip);
			}

			ImGui.SameLine(labelWidth);
			ImGui.SetNextItemWidth(-1f);
		}

		private bool Check(string label, ref bool value, string tooltip)
		{
			Label(label, tooltip);
			return EditorGui.Checkbox("##" + label, ref value);
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
				if (EditorGui.InputTextWithHint("##value", hint, ref value, 256))
				{
					values[i] = value;
					changed = true;
				}

				ImGui.SameLine();
				if (EditorGui.Button("x", new NVector2(ImGui.GetFrameHeight(), 0)))
				{
					values.RemoveAt(i);
					changed = true;
					ImGui.PopID();
					break;
				}

				ImGui.PopID();
			}

			if (EditorGui.Button("Add"))
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
