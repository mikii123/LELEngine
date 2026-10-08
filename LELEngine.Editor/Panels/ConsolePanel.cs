using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Engine log, script compile errors and exceptions.</summary>
	internal sealed class ConsolePanel
	{
		#region PrivateFields

		private readonly EditorApplication editor;
		private bool showLog = true;
		private bool showWarnings = true;
		private bool showErrors = true;
		private string filter = "";
		private int seenVersion = -1;
		private List<ConsoleEntry> entries = new List<ConsoleEntry>();

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
			if (ImGui.Begin("Console"))
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
			ConsoleLog log = editor.Log;
			bool newEntries = log.Version != seenVersion;
			if (newEntries)
			{
				seenVersion = log.Version;
				entries = log.Snapshot();
			}

			int logs = 0, warnings = 0, errors = 0;
			foreach (ConsoleEntry entry in entries)
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
			}

			if (ImGui.Button("Clear"))
			{
				log.Clear();
			}

			ImGui.SameLine();
			ImGui.Checkbox("Log " + logs, ref showLog);
			ImGui.SameLine();
			ImGui.Checkbox("Warnings " + warnings, ref showWarnings);
			ImGui.SameLine();
			ImGui.Checkbox("Errors " + errors, ref showErrors);
			ImGui.SameLine();
			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##filter", "Filter", ref filter, 128);

			if (ImGui.BeginChild("##entries", new NVector2(0, 0), ImGuiChildFlags.Borders, ImGuiWindowFlags.HorizontalScrollbar))
			{
				bool atBottom = ImGui.GetScrollY() >= ImGui.GetScrollMaxY() - 4f;
				for (int i = 0; i < entries.Count; i++)
				{
					ConsoleEntry entry = entries[i];
					bool error = entry.Type == LogType.Error || entry.Type == LogType.Exception;
					if (error && !showErrors || entry.Type == LogType.Warning && !showWarnings || entry.Type == LogType.Log && !showLog)
					{
						continue;
					}

					if (filter.Length > 0 && entry.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					if (error)
					{
						ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Error);
					}
					else if (entry.Type == LogType.Warning)
					{
						ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Warning);
					}

					ImGui.PushID(i);
					ImGui.TextUnformatted(entry.Text);
					ImGui.PopID();
					if (error || entry.Type == LogType.Warning)
					{
						ImGui.PopStyleColor();
					}
				}

				if (newEntries && atBottom)
				{
					ImGui.SetScrollHereY(1f);
				}
			}

			ImGui.EndChild();
		}

		#endregion
	}
}
