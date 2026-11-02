using System;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using LELEngine.Audio;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Animation
{
	/// <summary>The Timeline component's inspector below its fields (open the window, which object each channel animates, the live state in play mode) and the inspector of .timeline assets in the Project panel.</summary>
	internal static class TimelineComponentInspector
	{
		#region PublicMethods

		public static bool Draw(Timeline timeline, InspectorContext context)
		{
			EditorApplication editor = context.Editor;
			TimelineAsset asset = timeline.asset;
			if (asset == null)
			{
				ImGui.TextDisabled("Assign a timeline asset (Project panel: right click > Create Timeline), or open the Timeline window.");
				if (EditorGui.Button("Open Timeline Window", new NVector2(-1, 0)))
				{
					editor.OpenTimeline(timeline);
				}

				return false;
			}

			if (EditorGui.Button("Open in Timeline", new NVector2(-1, 0)))
			{
				editor.OpenTimeline(timeline);
			}

			ImGui.TextDisabled($"{asset.channels.Count} channels, {asset.markers.Count} markers, {asset.parameters.Count} parameters, {asset.Duration:0.##} s");
			if (asset.channels.Count > 0)
			{
				ImGui.SeparatorText("Channels");
				foreach (TimelineChannel channel in asset.channels)
				{
					ImGui.PushID(channel.id);
					GameObject target = timeline.ResolveTarget(channel);
					FieldDrawer.Label(channel.name, null, context);
					object value = target;
					if (ObjectField.Draw(typeof(GameObject), ref value, context))
					{
						context.BeginEdit();
						timeline.SetBinding(channel, value as GameObject);
						context.EndEdit();
					}

					ImGui.PopID();
				}
			}

			TimelinePlayer player = timeline.Player;
			if (!editor.IsPlaying || player == null)
			{
				return false;
			}

			ImGui.SeparatorText("Playback");
			ImGui.TextUnformatted(player.State + (player.Paused ? " (paused)" : "") + "  " + player.Time.ToString("0.000") + " s");
			if (player.State == TimelinePlaybackState.Sustaining && EditorGui.Button("Key Off", new NVector2(-1, 0)))
			{
				player.KeyOff();
			}

			foreach (AudioParameter parameter in asset.parameters)
			{
				float current = player.GetParameter(parameter.Id);
				ImGui.SetNextItemWidth(-1);
				if (EditorGui.SliderFloat("##" + parameter.Id, ref current, Math.Min(parameter.Min, parameter.Max), Math.Max(parameter.Min, parameter.Max), parameter.Name + " %.2f"))
				{
					player.SetParameter(parameter.Id, current, true);
				}
			}

			return false;
		}

		/// <summary>The .timeline asset's inspector: a summary and the Open button.</summary>
		public static void DrawAsset(string assetPath, InspectorContext context)
		{
			if (EditorGui.Button("Open in Timeline", new NVector2(-1, 0)))
			{
				context.Editor.OpenTimeline(assetPath);
			}

			try
			{
				TimelineAsset asset = TimelineAsset.Load(assetPath);
				ImGui.Text($"{asset.Duration:0.###} s, {asset.frameRate:0.#} fps, {asset.channels.Count} channels, {asset.CollectClips().Count} clips");
				foreach (TimelineChannel channel in asset.channels)
				{
					ImGui.BulletText(channel.name + ": " + channel.clips.Count + (channel.clips.Count == 1 ? " clip" : " clips"));
				}

				if (asset.parameters.Count > 0)
				{
					ImGui.SeparatorText("Parameters");
					foreach (AudioParameter parameter in asset.parameters)
					{
						ImGui.BulletText($"{parameter.Name}  {parameter.Min:0.##} .. {parameter.Max:0.##}");
					}
				}

				if (asset.markers.Count > 0)
				{
					ImGui.SeparatorText("Logic");
					foreach (AudioMarker marker in asset.markers)
					{
						ImGui.BulletText(marker.Kind + (string.IsNullOrEmpty(marker.Name) ? "" : ": " + marker.Name) + " at " + marker.Position.ToString("0.###") + " s");
					}
				}
			}
			catch (Exception e)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot load: " + e.Message);
			}
		}

		#endregion
	}
}
