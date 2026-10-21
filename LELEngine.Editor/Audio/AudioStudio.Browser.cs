using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Audio
{
	/// <summary>The browser panels: Events (folder tree), Banks, Parameters (global) and Assets (the project's audio files).</summary>
	internal sealed partial class AudioStudio
	{
		#region PrivateFields

		private string eventsSearch = "";
		private string assetsSearch = "";
		private readonly HashSet<string> collapsedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private List<string> audioAssets;
		private int audioAssetsVersion = -1;
		private string draggedEventId;
		private static string draggedClipPath;
		private EventInstance assetPreview;

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- events

		private void DrawEventsPanel()
		{
			if (ImGui.Begin("Events", ref showEvents))
			{
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
					ImGui.TextDisabled("File > New Audio Project, or open a .lelaudio file.");
					if (ImGui.Button("New Audio Project...")) openNewProjectPopup = true;
				}
				else
				{
					ImGui.SetNextItemWidth(-62);
					ImGui.InputTextWithHint("##search", "Search events", ref eventsSearch, 128);
					ImGui.SameLine();
					if (ImGui.Button("+ Event")) RequestNewEvent(SelectedEvent?.Folder);
					ImGui.Separator();
					if (ImGui.BeginChild("##events_tree", NVector2.Zero))
					{
						DrawEventTree();
						// Empty space: deselect, context menu for the root.
						NVector2 rest = ImGui.GetContentRegionAvail();
						ImGui.InvisibleButton("##events_rest", new NVector2(Math.Max(1f, rest.X), Math.Max(24f, rest.Y)));
						if (ImGui.BeginPopupContextItem("##events_root_menu"))
						{
							if (ImGui.MenuItem("New Event...")) RequestNewEvent(null);
							if (ImGui.MenuItem("New Folder...")) openNewFolderPopup = true;
							ImGui.EndPopup();
						}

						EventDropTarget("");
					}

					ImGui.EndChild();
				}
			}

			ImGui.End();
		}

		private void DrawEventTree()
		{
			bool searching = !string.IsNullOrWhiteSpace(eventsSearch);
			if (searching)
			{
				foreach (AudioEvent audioEvent in Project.Events)
				{
					if (audioEvent.Path.IndexOf(eventsSearch, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						DrawEventLeaf(audioEvent, true);
					}
				}

				return;
			}

			DrawFolder("", 0);
		}

		/// <summary>Draws the subfolders and events directly inside a folder path ("" for the root).</summary>
		private void DrawFolder(string folder, int depth)
		{
			if (depth > 32)
			{
				return;
			}

			var subfolders = new List<string>();
			foreach (string candidate in Project.AllFolders())
			{
				string parent = candidate.Contains('/') ? candidate.Substring(0, candidate.LastIndexOf('/')) : "";
				if (parent == folder && !subfolders.Contains(candidate))
				{
					subfolders.Add(candidate);
				}
			}

			foreach (string subfolder in subfolders)
			{
				string name = subfolder.Substring(subfolder.LastIndexOf('/') + 1);
				ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.OpenOnArrow | ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.OpenOnDoubleClick;
				ImGui.SetNextItemOpen(!collapsedFolders.Contains(subfolder), ImGuiCond.Always);
				bool open = ImGui.TreeNodeEx(name + "##folder_" + subfolder, flags);
				if (ImGui.IsItemToggledOpen())
				{
					if (open) collapsedFolders.Remove(subfolder);
					else collapsedFolders.Add(subfolder);
				}

				if (ImGui.BeginPopupContextItem("##folder_menu_" + subfolder))
				{
					if (ImGui.MenuItem("New Event Here...")) RequestNewEvent(subfolder);
					if (ImGui.MenuItem("New Subfolder..."))
					{
						newFolderPath = subfolder + "/Folder";
						openNewFolderPopup = true;
					}

					ImGui.Separator();
					if (ImGui.MenuItem("Delete Folder", "", false, Project.Events.TrueForAll(e => !e.Folder.StartsWith(subfolder, StringComparison.OrdinalIgnoreCase))))
					{
						string target = subfolder;
						Edit("Delete folder", () => Project.Folders.RemoveAll(f => f == target || f.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase)));
					}

					ImGui.EndPopup();
				}

				EventDropTarget(subfolder);
				if (open)
				{
					DrawFolder(subfolder, depth + 1);
					ImGui.TreePop();
				}
			}

			var events = new List<AudioEvent>();
			foreach (AudioEvent audioEvent in Project.Events)
			{
				if (string.Equals(audioEvent.Folder, folder, StringComparison.OrdinalIgnoreCase))
				{
					events.Add(audioEvent);
				}
			}

			events.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
			foreach (AudioEvent audioEvent in events)
			{
				DrawEventLeaf(audioEvent, false);
			}
		}

		private void DrawEventLeaf(AudioEvent audioEvent, bool fullPath)
		{
			bool selected = selectedEventId == audioEvent.Id;
			ImGuiTreeNodeFlags flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
			if (selected)
			{
				flags |= ImGuiTreeNodeFlags.Selected;
			}

			string label = fullPath ? audioEvent.Path : audioEvent.Name;
			AudioBank bank = Project.FindBank(audioEvent.BankId);
			if (bank != null && bank.Id != AudioProject.MasterBankId)
			{
				label += "   [" + bank.Name + "]";
			}

			ImGui.TreeNodeEx(label + "##event_" + audioEvent.Id, flags);
			if (ImGui.IsItemClicked() || (ImGui.IsItemClicked(ImGuiMouseButton.Right) && !selected))
			{
				SelectEvent(audioEvent.Id);
			}

			// The game plays it (live preview): a cyan play mark with the instance count.
			int playing = LiveCount(audioEvent.Id);
			if (playing > 0)
			{
				NVector2 itemMax = ImGui.GetItemRectMax();
				NVector2 itemMin = ImGui.GetItemRectMin();
				ImDrawListPtr draw = ImGui.GetWindowDrawList();
				string count = playing.ToString();
				float countWidth = ImGui.CalcTextSize(count).X;
				float middle = (itemMin.Y + itemMax.Y) * 0.5f;
				float x = itemMax.X - countWidth - 18f;
				draw.AddTriangleFilled(new NVector2(x, middle - 5f), new NVector2(x, middle + 5f), new NVector2(x + 8f, middle), StudioUi.LivePlayhead);
				draw.AddText(new NVector2(x + 11f, middle - ImGui.GetTextLineHeight() * 0.5f), StudioUi.LivePlayhead, count);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(audioEvent.Path + "\n" + audioEvent.Tracks.Count + " tracks, bank " + (bank?.Name ?? "?") + "\nDrag onto a folder to move it.");
			}

			if (ImGui.BeginDragDropSource())
			{
				draggedEventId = audioEvent.Id;
				unsafe
				{
					ImGui.SetDragDropPayload(EventPayload, null, 0);
				}

				ImGui.TextUnformatted(audioEvent.Path);
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginPopupContextItem("##event_menu_" + audioEvent.Id))
			{
				if (ImGui.MenuItem("Rename...")) RequestRename(Selection.Of(SelectionKind.Event, audioEvent.Id), audioEvent.Path);
				if (ImGui.MenuItem("Duplicate")) DuplicateEvent(audioEvent);
				if (ImGui.BeginMenu("Move to Bank"))
				{
					foreach (AudioBank other in Project.Banks)
					{
						if (ImGui.MenuItem(other.Name, "", other.Id == audioEvent.BankId))
						{
							string id = other.Id;
							Edit("Move event to bank", () => audioEvent.BankId = id);
						}
					}

					ImGui.EndMenu();
				}

				if (ImGui.MenuItem("Copy Path")) ImGui.SetClipboardText(audioEvent.Path);
				if (ImGui.MenuItem("Copy GUID")) ImGui.SetClipboardText(audioEvent.Id);
				ImGui.Separator();
				if (ImGui.MenuItem("Delete")) pendingDeleteEvent = audioEvent.Id;
				ImGui.EndPopup();
			}
		}

		/// <summary>A folder (or the root) takes dragged events.</summary>
		private void EventDropTarget(string folder)
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			if (!ImGui.AcceptDragDropPayload(EventPayload).IsNull && draggedEventId != null)
			{
				AudioEvent moved = Project.FindEvent(draggedEventId);
				draggedEventId = null;
				if (moved != null)
				{
					string path = string.IsNullOrEmpty(folder) ? moved.Name : folder + "/" + moved.Name;
					if (Project.FindEventByPath(path) == null)
					{
						Edit("Move event", () => moved.Path = path);
					}
					else
					{
						SetStatus("An event with that name exists in the folder already");
					}
				}
			}

			ImGui.EndDragDropTarget();
		}

		// ---------------------------------------------------------------- banks

		private void DrawBanksPanel()
		{
			if (ImGui.Begin("Banks", ref showBanks))
			{
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
				}
				else
				{
					if (ImGui.Button("+ Bank"))
					{
						var bank = new AudioBank { Id = AudioProject.NewId(), Name = UniqueBankName("New Bank"), AutoLoad = false };
						Edit("Add bank", () => Project.Banks.Add(bank));
						selection = Selection.Of(SelectionKind.Bank, bank.Id);
					}

					ImGui.SameLine();
					if (ImGui.Button("Build Banks"))
					{
						BuildBanks(null);
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip("Writes every bank into Library/AudioBanks (what a game build ships under Data/Audio/Banks) and logs their sizes.");
					}

					ImGui.Separator();
					foreach (AudioBank bank in new List<AudioBank>(Project.Banks))
					{
						bool isMaster = bank.Id == AudioProject.MasterBankId;
						int count = Project.EventsOfBank(bank.Id).Count;
						bool selected = selection.Is(SelectionKind.Bank, bank.Id);
						string label = bank.Name + "   (" + count + (count == 1 ? " event" : " events") + (bank.AutoLoad ? ", loads at start" : "") + ")";
						if (ImGui.Selectable(label + "##bank_" + bank.Id, selected))
						{
							selection = Selection.Of(SelectionKind.Bank, bank.Id);
						}

						if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && !isMaster)
						{
							RequestRename(Selection.Of(SelectionKind.Bank, bank.Id), bank.Name);
						}

						if (ImGui.BeginPopupContextItem("##bank_menu_" + bank.Id))
						{
							if (ImGui.MenuItem("Rename...", "", false, !isMaster)) RequestRename(Selection.Of(SelectionKind.Bank, bank.Id), bank.Name);
							bool autoLoad = bank.AutoLoad;
							if (ImGui.MenuItem("Load At Start", "", autoLoad, !isMaster)) Edit("Toggle bank auto load", () => bank.AutoLoad = !autoLoad);
							ImGui.Separator();
							if (ImGui.MenuItem("Delete", "", false, !isMaster))
							{
								Edit("Delete bank", () =>
								{
									foreach (AudioEvent audioEvent in Project.Events)
									{
										if (audioEvent.BankId == bank.Id) audioEvent.BankId = AudioProject.MasterBankId;
									}

									Project.Banks.Remove(bank);
								});
							}

							ImGui.EndPopup();
						}
					}

					ImGui.Spacing();
					ImGui.TextDisabled("Drag-free: assign events to banks from the event's context menu or the Deck.");
				}
			}

			ImGui.End();
		}

		private string UniqueBankName(string baseName)
		{
			string name = baseName;
			for (int i = 2; Project.FindBankByName(name) != null; i++)
			{
				name = baseName + " " + i;
			}

			return name;
		}

		// ---------------------------------------------------------------- global parameters

		private void DrawParametersPanel()
		{
			if (ImGui.Begin("Parameters", ref showParameters))
			{
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
				}
				else
				{
					ImGui.TextDisabled("Global parameters: one value for the whole game (AudioSystem.SetGlobalParameter).");
					if (ImGui.Button("+ Global Parameter"))
					{
						var parameter = new AudioParameter { Id = AudioProject.NewId(), Name = UniqueParameterName(null, "Parameter") };
						Edit("Add global parameter", () => Project.GlobalParameters.Add(parameter));
						selection = Selection.Of(SelectionKind.GlobalParameter, parameter.Id);
					}

					ImGui.Separator();
					foreach (AudioParameter parameter in new List<AudioParameter>(Project.GlobalParameters))
					{
						bool selected = selection.Is(SelectionKind.GlobalParameter, parameter.Id);
						string label = parameter.Name + "   " + parameter.Min.ToString("0.##") + " .. " + parameter.Max.ToString("0.##") + (parameter.BuiltIn != BuiltInParameter.None ? "  (" + parameter.BuiltIn + ")" : "");
						if (ImGui.Selectable(label + "##gparam_" + parameter.Id, selected))
						{
							selection = Selection.Of(SelectionKind.GlobalParameter, parameter.Id);
						}

						if (ImGui.BeginPopupContextItem("##gparam_menu_" + parameter.Id))
						{
							if (ImGui.MenuItem("Rename...")) RequestRename(Selection.Of(SelectionKind.GlobalParameter, parameter.Id), parameter.Name);
							if (ImGui.MenuItem("Delete")) Edit("Delete global parameter", () => Project.GlobalParameters.Remove(parameter));
							ImGui.EndPopup();
						}
					}

					if (Project.GlobalParameters.Count == 0)
					{
						ImGui.TextDisabled("None yet.");
					}
				}
			}

			ImGui.End();
		}

		private string UniqueParameterName(AudioEvent context, string baseName)
		{
			string name = baseName;
			for (int i = 2; Project.ParametersOf(context).Exists(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
			{
				name = baseName + " " + i;
			}

			return name;
		}

		// ---------------------------------------------------------------- snapshots

		private void DrawSnapshotsPanel()
		{
			if (ImGui.Begin("Snapshots", ref showSnapshots))
			{
				if (Project == null)
				{
					ImGui.TextDisabled("No audio project open.");
				}
				else
				{
					ImGui.TextDisabled("Mixer snapshots: bus volumes and effect values the game switches on (AudioSystem.StartSnapshot).");
					if (ImGui.Button("+ Snapshot"))
					{
						var snapshot = new AudioSnapshot { Id = AudioProject.NewId(), Name = UniqueSnapshotName("Snapshot") };
						Edit("Add snapshot", () => Project.Snapshots.Add(snapshot));
						selection = Selection.Of(SelectionKind.Snapshot, snapshot.Id);
					}

					ImGui.SameLine();
					ImGui.TextDisabled("first in the list wins");
					ImGui.Separator();
					for (int i = 0; i < Project.Snapshots.Count; i++)
					{
						AudioSnapshot snapshot = Project.Snapshots[i];
						bool selected = selection.Is(SelectionKind.Snapshot, snapshot.Id);
						bool active = previewSnapshot != null && previewSnapshot.IsActive && previewSnapshot.Definition.Id == snapshot.Id;
						string label = snapshot.Name + "   (" + (snapshot.Mode == SnapshotMode.Blending ? "blending" : "overriding") + ", " + snapshot.Buses.Count + (snapshot.Buses.Count == 1 ? " bus" : " buses") + (active ? ", active" : "") + ")";
						if (ImGui.Selectable(label + "##snapshot_" + snapshot.Id, selected, ImGuiSelectableFlags.AllowDoubleClick))
						{
							selection = Selection.Of(SelectionKind.Snapshot, snapshot.Id);
							if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
							{
								RequestRename(Selection.Of(SelectionKind.Snapshot, snapshot.Id), snapshot.Name);
							}
						}

						if (ImGui.BeginPopupContextItem("##snapshot_menu_" + snapshot.Id))
						{
							if (ImGui.MenuItem("Rename...")) RequestRename(Selection.Of(SelectionKind.Snapshot, snapshot.Id), snapshot.Name);
							if (ImGui.MenuItem("Move Up (higher priority)", "", false, i > 0))
							{
								int index = i;
								Edit("Reorder snapshots", () =>
								{
									Project.Snapshots.RemoveAt(index);
									Project.Snapshots.Insert(index - 1, snapshot);
								});
							}

							if (ImGui.MenuItem("Move Down (lower priority)", "", false, i < Project.Snapshots.Count - 1))
							{
								int index = i;
								Edit("Reorder snapshots", () =>
								{
									Project.Snapshots.RemoveAt(index);
									Project.Snapshots.Insert(index + 1, snapshot);
								});
							}

							ImGui.Separator();
							if (ImGui.MenuItem(active ? "Stop" : "Start"))
							{
								if (active) StopPreviewSnapshot();
								else StartPreviewSnapshot(snapshot);
							}

							ImGui.Separator();
							if (ImGui.MenuItem("Delete"))
							{
								if (active) StopPreviewSnapshot();
								Edit("Delete snapshot", () => Project.Snapshots.Remove(snapshot));
								if (selected) selection = default;
							}

							ImGui.EndPopup();
						}
					}

					if (Project.Snapshots.Count == 0)
					{
						ImGui.TextDisabled("None yet. A snapshot scopes bus volumes and effect values; select one and tick buses in the Mixer.");
					}
				}
			}

			ImGui.End();
		}

		private string UniqueSnapshotName(string baseName)
		{
			string name = baseName;
			for (int i = 2; Project.FindSnapshotByName(name) != null; i++)
			{
				name = baseName + " " + i;
			}

			return name;
		}

		// ---------------------------------------------------------------- assets

		private List<string> AudioAssets()
		{
			if (audioAssets == null || audioAssetsVersion != editor.AssetsVersion)
			{
				audioAssets = AssetDatabase.GetAllAssetPaths(AudioClip.Extensions);
				audioAssetsVersion = editor.AssetsVersion;
			}

			return audioAssets;
		}

		private void DrawAssetsPanel()
		{
			if (ImGui.Begin("Assets", ref showAssets))
			{
				ImGui.SetNextItemWidth(-1);
				ImGui.InputTextWithHint("##assetsearch", "Search audio files (wav, ogg, mp3)", ref assetsSearch, 128);
				ImGui.Separator();
				if (ImGui.BeginChild("##assets_list", NVector2.Zero))
				{
					List<string> assets = AudioAssets();
					if (assets.Count == 0)
					{
						ImGui.TextDisabled("No audio files under Assets. Copy .wav, .ogg or .mp3 files into the project.");
					}

					foreach (string path in assets)
					{
						if (!string.IsNullOrWhiteSpace(assetsSearch) && path.IndexOf(assetsSearch, StringComparison.OrdinalIgnoreCase) < 0)
						{
							continue;
						}

						DrawAssetRow(path);
					}
				}

				ImGui.EndChild();
			}

			ImGui.End();
		}

		private void DrawAssetRow(string path)
		{
			AudioClip clip = AudioClip.Load(path);
			bool selected = selection.Is(SelectionKind.Asset, path);
			string info = clip == null || clip.Error != null ? "unreadable" : FormatTime(clip.Duration) + "  " + (clip.Channels == 1 ? "mono" : "stereo") + " " + clip.SampleRate / 1000f + " kHz";
			if (ImGui.Selectable(Path.GetFileName(path) + "##asset_" + path, selected, ImGuiSelectableFlags.AllowDoubleClick))
			{
				selection = Selection.Of(SelectionKind.Asset, path);
				if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					PreviewAsset(clip);
				}
			}

			float right = ImGui.GetItemRectMax().X - ImGui.CalcTextSize(info).X - 8f;
			ImGui.GetWindowDrawList().AddText(new NVector2(right, ImGui.GetItemRectMin().Y + 2f), ImGui.GetColorU32(ImGuiCol.TextDisabled), info);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(path + "\n" + info + (clip != null ? "\nLoad: " + SerializationUtilityName(clip.Importer.loadType.ToString()) : "") + "\nDouble click: play. Drag onto a track to add an instrument, onto a playlist to add an entry.");
			}

			if (ImGui.BeginDragDropSource())
			{
				draggedClipPath = path;
				unsafe
				{
					ImGui.SetDragDropPayload(ClipPayload, null, 0);
				}

				ImGui.TextUnformatted(Path.GetFileName(path));
				ImGui.EndDragDropSource();
			}

			if (ImGui.BeginPopupContextItem("##asset_menu_" + path))
			{
				if (ImGui.MenuItem("Play")) PreviewAsset(clip);
				if (ImGui.MenuItem("Stop", "", false, assetPreview != null && assetPreview.IsValid)) StopAssetPreview();
				ImGui.Separator();
				if (clip != null && ImGui.BeginMenu("Load Type"))
				{
					foreach (AudioLoadType loadType in Enum.GetValues<AudioLoadType>())
					{
						if (ImGui.MenuItem(SerializationUtilityName(loadType.ToString()), "", clip.Importer.loadType == loadType))
						{
							SetImporter(clip, importer => importer.loadType = loadType);
						}
					}

					ImGui.EndMenu();
				}

				if (clip != null && ImGui.MenuItem("Force Mono", "", clip.Importer.forceMono))
				{
					SetImporter(clip, importer => importer.forceMono = !importer.forceMono);
				}

				ImGui.Separator();
				if (ImGui.MenuItem("Show in Project Panel")) editor.Ping(path);
				ImGui.EndPopup();
			}
		}

		private static string SerializationUtilityName(string name)
		{
			return LELEngine.Serialization.SerializationUtility.NicifyName(name);
		}

		/// <summary>Changes a clip's import settings (meta file) and reloads what plays it.</summary>
		private void SetImporter(AudioClip clip, Action<AudioImporter> change)
		{
			AssetMeta meta = AssetDatabase.GetMeta(clip.AssetPath);
			var importer = meta.importer as AudioImporter ?? new AudioImporter();
			change(importer);
			importer.version++;
			meta.importer = importer;
			AssetDatabase.SaveMeta(clip.AssetPath, meta);
			clip.Reset();
			liveUpdatePending = true;
			SetStatus("Import settings of " + Path.GetFileName(clip.AssetPath) + " saved");
		}

		private void PreviewAsset(AudioClip clip)
		{
			if (clip == null)
			{
				return;
			}

			StopAssetPreview();
			assetPreview = AudioSystem.PreviewClip(clip);
			if (assetPreview == null)
			{
				SetStatus("Cannot play " + clip.AssetPath + (clip.Error != null ? ": " + clip.Error : ""));
			}
		}

		private void StopAssetPreview()
		{
			if (assetPreview != null)
			{
				assetPreview.Stop(AudioStopMode.Immediate);
				assetPreview = null;
			}
		}

		#endregion
	}
}
