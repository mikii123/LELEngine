using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using LELEngine.Serialization;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Audio
{
	/// <summary>
	///     The audio system's inspectors in the editor's main window: the event picker of <see cref="AudioEventReference" />
	///     fields, the emitter's preview buttons, the audio file and audio project asset inspectors, and the Audio tab
	///     of the Project Settings.
	/// </summary>
	internal static class AudioInspectors
	{
		#region PrivateFields

		private static readonly string[] LoadTypeNames = { "Decompress On Load (PCM in memory)", "Compressed In Memory (decoded at load)", "Streaming (from disk)" };
		private static string eventSearch = "";
		private static EventInstance clipPreview;

		#endregion

		#region PublicMethods

		/// <summary>The game's audio project (loaded on demand), or null when the project has none.</summary>
		public static AudioProject CurrentProject()
		{
			string path = AudioProject.FindProjectPath();
			return path != null ? AudioProject.Load(path) : null;
		}

		/// <summary>The widget of an event reference field: a searchable list of the audio project's events; true when it changed.</summary>
		public static bool DrawEventReferenceCombo(ref object value, InspectorContext context)
		{
			var reference = value is AudioEventReference existing ? existing : default;
			AudioProject project = CurrentProject();
			AudioEvent current = project?.FindEvent(reference);
			string shown = current != null ? current.Path : reference.IsNull ? "None (Audio Event)" : (reference.Path ?? reference.Guid) + "  (missing)";
			bool changed = false;
			if (EditorGui.BeginCombo("##event_ref", shown))
			{
				if (project == null)
				{
					ImGui.TextDisabled("No audio project: create one in Window > Audio Studio.");
				}
				else
				{
					ImGui.SetNextItemWidth(-1);
					if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
					EditorGui.InputTextWithHint("##event_search", "Search", ref eventSearch, 128);
					if (EditorGui.Selectable("None", reference.IsNull) && !reference.IsNull)
					{
						value = default(AudioEventReference);
						changed = true;
					}

					var events = new List<AudioEvent>(project.Events);
					events.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
					foreach (AudioEvent audioEvent in events)
					{
						if (!string.IsNullOrWhiteSpace(eventSearch) && audioEvent.Path.IndexOf(eventSearch, StringComparison.OrdinalIgnoreCase) < 0)
						{
							continue;
						}

						if (EditorGui.Selectable(audioEvent.Path + "##ev" + audioEvent.Id, audioEvent == current) && audioEvent != current)
						{
							value = AudioEventReference.FromEvent(audioEvent);
							changed = true;
						}
					}
				}

				EditorGui.EndCombo();
			}

			if (ImGui.IsItemHovered() && current != null)
			{
				ImGui.SetTooltip(current.Path + "\nGUID " + current.Id + "\n" + current.Tracks.Count + " tracks" + (current.Spatializer.Enabled ? ", 3D" : ", 2D") + "\nDouble click: open in the Audio Studio");
			}

			if (EditorGui.IsItemDoubleClicked() && current != null && project != null)
			{
				context.Editor?.OpenAudioStudio(project.AssetPath, current.Id);
			}

			return changed;
		}

		/// <summary>Below the emitter's fields: play / stop and the state of its instance.</summary>
		public static bool DrawEmitter(AudioEventEmitter emitter, InspectorContext context)
		{
			ImGui.Spacing();
			AudioProject project = CurrentProject();
			AudioEvent audioEvent = project?.FindEvent(emitter.Event);
			if (!emitter.Event.IsNull && audioEvent == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "The event is missing from the audio project.");
			}

			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(emitter.Event.IsNull || !AudioSystem.IsInitialized);
			if (EditorGui.Button("Play", new NVector2(width, 0)))
			{
				emitter.Play();
			}

			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip("Plays the event at the object's position (in edit mode too).");
			}

			ImGui.SameLine();
			if (EditorGui.Button("Stop", new NVector2(width, 0)))
			{
				emitter.Stop();
			}

			ImGui.EndDisabled();
			EventInstance instance = emitter.Instance;
			if (instance != null)
			{
				ImGui.TextDisabled(instance.State + " at " + instance.TimelinePosition.ToString("0.00") + " s, " + instance.VoiceCount + " voices");
			}

			if (audioEvent != null && project != null && EditorGui.SmallButton("Open in Audio Studio"))
			{
				context.Editor.OpenAudioStudio(project.AssetPath, audioEvent.Id);
			}

			return false;
		}

		/// <summary>Below the snapshot trigger's fields: a picker of the project's snapshots and start / stop.</summary>
		public static bool DrawSnapshotTrigger(AudioSnapshotTrigger trigger, InspectorContext context)
		{
			ImGui.Spacing();
			AudioProject project = CurrentProject();
			FieldDrawer.Label("Pick Snapshot", null, context);
			AudioSnapshot current = project?.FindSnapshotByName(trigger.Snapshot);
			if (EditorGui.BeginCombo("##pick_snapshot", current != null ? current.Name : string.IsNullOrEmpty(trigger.Snapshot) ? "None" : trigger.Snapshot + "  (missing)"))
			{
				if (project == null || project.Snapshots.Count == 0)
				{
					ImGui.TextDisabled("No snapshots in the audio project (Audio Studio > Snapshots).");
				}
				else
				{
					foreach (AudioSnapshot snapshot in project.Snapshots)
					{
						if (EditorGui.Selectable(snapshot.Name + "##snap" + snapshot.Id, snapshot == current) && snapshot != current)
						{
							context.BeginEdit();
							trigger.Snapshot = snapshot.Name;
							context.EndEdit();
						}
					}
				}

				EditorGui.EndCombo();
			}

			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(string.IsNullOrEmpty(trigger.Snapshot) || !AudioSystem.IsInitialized);
			if (EditorGui.Button("Start", new NVector2(width, 0)))
			{
				trigger.StartSnapshot();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Stop", new NVector2(width, 0)))
			{
				trigger.StopSnapshot();
			}

			ImGui.EndDisabled();
			SnapshotInstance instance = trigger.Instance;
			if (instance != null)
			{
				ImGui.TextDisabled("Active at intensity " + instance.Intensity.ToString("0.00"));
			}

			return false;
		}

		/// <summary>Inspector of an audio file: its format, the import settings (saved to the meta file at once) and a preview.</summary>
		public static void DrawClip(string assetPath, InspectorContext context)
		{
			AudioClip clip = AudioClip.Load(assetPath);
			if (clip == null)
			{
				ImGui.TextDisabled("Cannot load.");
				return;
			}

			context.UndoTarget = null;
			if (clip.Error != null)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot read: " + clip.Error);
			}
			else
			{
				ImGui.TextUnformatted($"{SerializationUtility.NicifyName(clip.Format.ToString())}, {clip.Duration:0.000} s, {(clip.Channels == 1 ? "mono" : "stereo")}, {clip.SampleRate} Hz");
			}

			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			ImGui.BeginDisabled(clip.Error != null || !AudioSystem.IsInitialized);
			if (EditorGui.Button("Play", new NVector2(width, 0)))
			{
				clipPreview?.Stop(AudioStopMode.Immediate);
				clipPreview = AudioSystem.PreviewClip(clip);
			}

			ImGui.SameLine();
			if (EditorGui.Button("Stop", new NVector2(width, 0)))
			{
				clipPreview?.Stop(AudioStopMode.Immediate);
				clipPreview = null;
			}

			ImGui.EndDisabled();
			ImGui.Spacing();
			ImGui.SeparatorText("Import Settings");
			AudioImporter importer = clip.Importer;
			FieldDrawer.Label("Load Type", null, context);
			int loadType = (int)importer.loadType;
			if (ImGui.Combo("##loadtype", ref loadType, LoadTypeNames, LoadTypeNames.Length) && loadType != (int)importer.loadType)
			{
				SaveImporter(clip, i => i.loadType = (AudioLoadType)loadType);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("How banks hold the sound: decoded PCM (fastest load), the encoded file decoded when the bank loads (smaller builds), or streamed from disk while playing (music, long ambiences).");
			}

			FieldDrawer.Label("Force Mono", null, context);
			bool mono = importer.forceMono;
			if (EditorGui.Checkbox("##mono", ref mono) && mono != importer.forceMono)
			{
				SaveImporter(clip, i => i.forceMono = mono);
			}

			ImGui.Spacing();
			AudioProject project = CurrentProject();
			if (project != null)
			{
				int uses = 0;
				foreach (AudioEvent audioEvent in project.Events)
				{
					foreach (AudioClipReference reference in audioEvent.Clips)
					{
						if (string.Equals(reference.Resolve() ?? reference.Path, assetPath, StringComparison.OrdinalIgnoreCase))
						{
							uses++;
						}
					}
				}

				ImGui.TextDisabled("Used by " + uses + " instrument(s) of the audio project.");
			}

			AssetInspectors.DrawFileInfo(AssetDatabase.ToAbsolute(assetPath));
		}

		/// <summary>Inspector of the audio project asset: open it, and a summary.</summary>
		public static void DrawProject(string assetPath, InspectorContext context)
		{
			if (EditorGui.Button("Open in Audio Studio", new NVector2(-1, 0)))
			{
				context.Editor.OpenAudioStudio(assetPath, null);
			}

			AudioProject project = AudioProject.Load(assetPath);
			if (project != null)
			{
				int instruments = 0;
				foreach (AudioEvent audioEvent in project.Events)
				{
					foreach (AudioTrack track in audioEvent.Tracks)
					{
						instruments += track.Instruments.Count;
					}
				}

				ImGui.Spacing();
				ImGui.TextUnformatted($"{project.Events.Count} events, {instruments} instruments, {project.GlobalParameters.Count} global parameters, {project.Buses.Count} buses, {project.Banks.Count} banks");
				string active = AudioProject.FindProjectPath();
				if (!string.Equals(active, assetPath, StringComparison.OrdinalIgnoreCase))
				{
					ImGui.TextColored(EditorStyle.Warning, "Not the game's audio project (Project Settings > Audio chooses " + (active ?? "none") + ").");
				}

				foreach (AudioBank bank in project.Banks)
				{
					ImGui.BulletText(bank.Name + ": " + project.EventsOfBank(bank.Id).Count + " events" + (bank.AutoLoad ? ", loads at start" : ""));
				}
			}

			AssetInspectors.DrawFileInfo(AssetDatabase.ToAbsolute(assetPath));
		}

		/// <summary>The Audio tab of the Project Settings window.</summary>
		public static bool DrawSettings(AudioSettings settings, InspectorContext context)
		{
			ImGui.TextDisabled("The audio project's banks are compiled into builds (Data/Audio/Banks) and loaded at start.");
			ImGui.Spacing();
			bool changed = false;
			FieldDrawer.Label("Audio Project", null, context);
			List<string> projects = AssetDatabase.GetAllAssetPaths(AudioProject.Extension);
			string currentPath = string.IsNullOrEmpty(settings.Project) ? null : AssetDatabase.GetPath(settings.Project);
			string shown = currentPath ?? (projects.Count > 0 ? "Automatic (" + projects[0] + ")" : "None (create one in the Audio Studio)");
			if (EditorGui.BeginCombo("##audioproject", shown))
			{
				if (EditorGui.Selectable("Automatic (the only .lelaudio file)", currentPath == null) && currentPath != null)
				{
					context.BeginEdit();
					settings.Project = "";
					context.EndEdit();
					changed = true;
				}

				foreach (string path in projects)
				{
					if (EditorGui.Selectable(path, string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)) && !string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase))
					{
						context.BeginEdit();
						settings.Project = AssetDatabase.GetGuid(path) ?? "";
						context.EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			changed |= FieldDrawer.DrawFields(settings, context, filter: field => field.Name != nameof(AudioSettings.Project));
			ImGui.Spacing();
			ImGui.TextDisabled("Device: " + AudioSystem.DeviceStatus);
			if (changed)
			{
				AudioSystem.ApplySettings(settings);
			}

			return changed;
		}

		#endregion

		#region PrivateMethods

		private static void SaveImporter(AudioClip clip, Action<AudioImporter> change)
		{
			AssetMeta meta = AssetDatabase.GetMeta(clip.AssetPath);
			var importer = meta.importer as AudioImporter ?? new AudioImporter();
			change(importer);
			importer.version++;
			meta.importer = importer;
			AssetDatabase.SaveMeta(clip.AssetPath, meta);
			clip.Reset();
			AudioSystem.ReloadFromProject();
		}

		#endregion
	}
}
