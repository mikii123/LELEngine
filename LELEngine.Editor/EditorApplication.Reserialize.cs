using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Assets > Force Reserialize All Assets: the question, the run over frames, what the editor updates after it.</summary>
	internal sealed partial class EditorApplication
	{
		#region PrivateFields

		private AssetReserializer reserializer;
		private bool openReserializePopup;

		#endregion

		#region PublicMethods

		/// <summary>Force Reserialize All Assets is running (<see cref="AssetReserializer" />).</summary>
		public bool IsReserializing => reserializer != null;

		/// <summary>The last run's summary line (null before one ended).</summary>
		public string LastReserializeReport { get; private set; }

		/// <summary>
		///     The action: starts Force Reserialize All Assets, asking nothing (the menu item asks,
		///     <see cref="AskForceReserializeAllAssets" />; automation calls this). Not in play mode or prefab mode, whose open
		///     files the run would read under them, nor while a run goes on.
		/// </summary>
		/// <exception cref="InvalidOperationException">It cannot start now (the reason).</exception>
		public void ForceReserializeAllAssets()
		{
			if (Project == null || reserializer != null || IsPlaying || Stage != null)
			{
				throw new InvalidOperationException("Force Reserialize All Assets cannot start: "
					+ (Project == null ? "no project is open" : reserializer != null ? "it runs already" : IsPlaying ? "leave play mode first" : "leave prefab mode first"));
			}

			StartReserialize();
		}

		/// <summary>Assets > Force Reserialize All Assets: asks first in the editor's window (without one it starts at once).</summary>
		public void AskForceReserializeAllAssets()
		{
			if (Project == null || reserializer != null)
			{
				return;
			}

			if (IsPlaying || Stage != null)
			{
				Debug.LogWarning("[Editor] Force Reserialize All Assets: leave " + (IsPlaying ? "play mode" : "prefab mode") + " first");
				return;
			}

			if (HasUI)
			{
				openReserializePopup = true;
				return;
			}

			StartReserialize();
		}

		#endregion

		#region PrivateMethods

		private void StartReserialize()
		{
			Edits.Commit();
			var unsavedAssets = new List<string>();
			VFX.VfxGraphDocument graph = vfxGraphWindow.Document;
			if (graph != null && graph.Dirty && graph.AssetPath != null)
			{
				unsavedAssets.Add(graph.AssetPath);
			}

			if (audioStudio != null && audioStudio.IsOpen && audioStudio.Studio.Dirty && audioStudio.Studio.AssetPath != null)
			{
				unsavedAssets.Add(audioStudio.Studio.AssetPath);
			}

			reserializer = new AssetReserializer(Project, unsavedAssets);
			Debug.Log($"[Editor] Force Reserialize All Assets: {reserializer.Total} files");
		}

		/// <summary>A frame's share of the run; at its end the editor takes the files it wrote as its own.</summary>
		private void UpdateReserialize()
		{
			if (reserializer == null || !reserializer.Step())
			{
				return;
			}

			AssetReserializer done = reserializer;
			reserializer = null;
			LastReserializeReport = done.Report();

			// The open scene's and the settings' files changed under the editor, with the same contents: the editor's own
			// writes, so neither reloads (the other assets reload in place through the watcher, as after a checkout).
			string scenePath = Scene?.Path;
			if (!string.IsNullOrEmpty(scenePath))
			{
				string file = AssetDatabase.ToAbsolute(scenePath);
				if (File.Exists(file) && FileStamp.Of(file) != sceneStamp && !sceneChangedOnDisk)
				{
					sceneStamp = FileStamp.Of(file);
					sceneFileBase = File.ReadAllBytes(file);
				}
			}

			if (File.Exists(Project.ProjectSettingsFile))
			{
				projectSettingsStamp = FileStamp.Of(Project.ProjectSettingsFile);
			}

			NotifyAssetsChanged();
		}

		private void DrawReserializeModal()
		{
			if (openReserializePopup)
			{
				ImGui.OpenPopup("Force Reserialize All Assets");
				openReserializePopup = false;
			}

			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (!ImGui.BeginPopupModal("Force Reserialize All Assets", ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			ImGui.TextUnformatted("Every scene, prefab, material, ScriptableObject, meta file and the other asset documents\n"
				+ "are read from their files and written again as the editor writes them now.\n"
				+ "Only files that come out different are written (review them in version control).\n"
				+ "Unsaved changes in the open scene and in the windows stay where they are.");
			ImGui.Spacing();
			if (EditorGui.Button("Reserialize", new NVector2(110, 0)))
			{
				ImGui.CloseCurrentPopup();
				if (!IsPlaying && Stage == null)
				{
					StartReserialize();
				}
			}

			ImGui.SameLine();
			if (EditorGui.Button("Cancel", new NVector2(110, 0)))
			{
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
