using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below the fields of the camera shake's parts. A <see cref="CameraShakeProfile" />: its presets, and a
	///     viewfinder that plays the profile over a drawn scene (far lines, near posts: turning and moving both show), so
	///     a profile is tuned by eye in edit mode, with nothing of the scene touched. A <see cref="CameraShake" /> and a
	///     <see cref="CameraShakeSource" />: while the game plays, what is playing and buttons to try them; in the scene
	///     view a selected source's reach.
	/// </summary>
	internal static class CameraShakeInspector
	{
		#region PrivateFields

		private const float ToRadians = MathF.PI / 180f;
		private const float ToDegrees = 180f / MathF.PI;
		private const int Segments = 48;

		private static readonly string[] presetNames = Enum.GetNames(typeof(CameraShakePreset));

		// The viewfinder's own clock and settings (one for every profile shown: it is a look, not data).
		private static double previewTime;
		private static double lastFrameTime = -1.0;
		private static bool previewPlaying = true;
		private static float previewFieldOfView = 50f;
		private static float impactAge = -1f;
		private static CameraShakeProfile impactProfile;
		private static int presetIndex = (int)CameraShakePreset.Handheld;

		// A profile tried on the running game's camera for a few seconds.
		private static CameraShakeInstance trial;
		private static double trialEnds;

		private static CameraShakeProfile measured;
		private static double measuredAt = -10.0;
		private static float measuredTurn;
		private static float measuredPeak;
		private static float measuredMove;

		#endregion

		#region PublicMethods

		/// <summary>The profile's presets and its viewfinder; true when a preset was loaded into the profile.</summary>
		public static bool DrawProfile(CameraShakeProfile profile, InspectorContext context)
		{
			bool changed = false;
			ImGui.PushID("camerashakeprofile");
			DrawPreview(profile, context);
			ImGui.SeparatorText("Preset");
			float buttonWidth = ImGui.CalcTextSize("Load").X + ImGui.GetStyle().FramePadding.X * 2f;
			ImGui.SetNextItemWidth(MathF.Max(60f, ImGui.GetContentRegionAvail().X - buttonWidth - ImGui.GetStyle().ItemSpacing.X));
			presetIndex = Math.Clamp(presetIndex, 0, presetNames.Length - 1);
			if (EditorGui.BeginCombo("##preset", Spaced(presetNames[presetIndex])))
			{
				for (int i = 0; i < presetNames.Length; i++)
				{
					if (EditorGui.Selectable(Spaced(presetNames[i]) + "##" + i, i == presetIndex))
					{
						presetIndex = i;
					}
				}

				EditorGui.EndCombo();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Load"))
			{
				context.BeginEdit();
				profile.Load((CameraShakePreset)presetIndex);
				context.EndEdit();
				changed = true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Sets every value of this profile to the preset's (undoable).");
			}

			ImGui.PopID();
			return changed;
		}

		/// <summary>While the game plays: how hard the camera shakes, what plays on it, and buttons to try it.</summary>
		public static bool DrawComponent(CameraShake shake, InspectorContext context)
		{
			ImGui.Spacing();
			if (!context.Editor.IsPlaying)
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextDisabled("Shakes this object's transform while the game plays. What steers the camera should move a parent of this object.");
				ImGui.PopTextWrapPos();
				return false;
			}

			CameraShakePose pose = shake.Pose;
			ImGui.ProgressBar(shake.Intensity, new NVector2(-1f, 0f), $"shaking {shake.Intensity * 100f:0} %");
			ImGui.TextDisabled($"Turned {pose.Rotation.Length:0.00} deg, moved {pose.Position.Length * 100f:0.0} cm" + (pose.FieldOfView != 0f ? $", field of view {pose.FieldOfView:+0.0;-0.0}" : ""));
			int world = shake.Listen ? CameraShake.World.Count : 0;
			ImGui.TextDisabled($"{shake.Playing.Count} playing on it" + (shake.Listen ? $", {world} in the world" : ", not listening to the world"));
			foreach (CameraShakeInstance instance in shake.Playing)
			{
				// A format string to ImGui: the percent sign doubled.
				ImGui.BulletText($"{NameOf(instance.Profile).Replace("%", "%%")}  {(instance.IsOneShot ? "impact" : instance.IsStopping ? "dying away" : "constant")}  {instance.Level * 100f:0} %%  {instance.Age:0.0} s");
			}

			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
			if (EditorGui.Button("Impact", new NVector2(width, 0f)))
			{
				shake.Impact();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("One impact on this camera (the Impact preset).");
			}

			ImGui.SameLine();
			if (EditorGui.Button("Stop All", new NVector2(width, 0f)))
			{
				shake.StopAll();
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Stops what scripts started on this camera; the Profiles list plays on.");
			}

			return false;
		}

		/// <summary>While the game plays: buttons to start and end the source.</summary>
		public static bool DrawSource(CameraShakeSource source, InspectorContext context)
		{
			if (!context.Editor.IsPlaying)
			{
				return false;
			}

			ImGui.Spacing();
			CameraShakeInstance instance = source.Instance;
			ImGui.TextDisabled(source.IsPlaying ? $"Playing: {instance.Level * 100f:0} %%, {instance.Age:0.0} s" + (instance.IsStopping ? " (dying away)" : "") : "Not playing");
			float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X * 2f) / 3f;
			if (EditorGui.Button("Play", new NVector2(width, 0f)))
			{
				source.Play();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Impact", new NVector2(width, 0f)))
			{
				source.Impact();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Stop", new NVector2(width, 0f)))
			{
				source.Stop();
			}

			return false;
		}

		/// <summary>Scene view gizmo: the reach of each expanded source on the selected object (where it is felt in full, where it ends).</summary>
		public static void DrawGizmos(EditorApplication editor, GameObject selected)
		{
			if (selected == null)
			{
				return;
			}

			foreach (CameraShakeSource source in selected.GetComponents<CameraShakeSource>())
			{
				if (source.IsDestroyed || !source.enabled || source.Everywhere || !editor.IsExpandedInInspector(source))
				{
					continue;
				}

				Vector3 position = source.transform.position;
				Sphere(position, MathF.Max(source.Radius, 0f), new Color4(1f, 0.75f, 0.35f, 1f));
				if (source.InnerRadius > 0f && source.InnerRadius < source.Radius)
				{
					Sphere(position, source.InnerRadius, new Color4(1f, 0.9f, 0.7f, 1f));
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void DrawPreview(CameraShakeProfile profile, InspectorContext context)
		{
			ImGui.SeparatorText("Preview");

			// The clock moves once per editor frame, however many profiles are drawn.
			double now = ImGui.GetTime();
			if (now != lastFrameTime)
			{
				float delta = lastFrameTime < 0.0 ? 0f : (float)Math.Min(now - lastFrameTime, 0.1);
				lastFrameTime = now;
				if (previewPlaying)
				{
					previewTime += delta;
					if (impactAge >= 0f)
					{
						impactAge += delta;
					}
				}
			}

			// An impact tried in the viewfinder: the profile once, dying away as it would in the game.
			CameraShakePose pose;
			if (impactAge >= 0f && ReferenceEquals(impactProfile, profile))
			{
				float length = MathF.Max(profile.HasMovement ? profile.Duration : 0f, profile.KickLength);
				if (impactAge >= length + 0.4f)
				{
					impactAge = -1f;
					impactProfile = null;
				}

				float envelope = profile.Duration > 1e-4f && impactAge < profile.Duration ? MathF.Pow(1f - impactAge / profile.Duration, MathF.Max(profile.Decay, 0.01f)) : 0f;
				if (profile.Attack > 1e-4f)
				{
					float coming = Math.Clamp(impactAge / profile.Attack, 0f, 1f);
					envelope *= coming * coming * (3f - 2f * coming);
				}

				pose = profile.Evaluate(previewTime, 7) * envelope + profile.EvaluateKick(MathF.Max(impactAge, 0f));
			}
			else
			{
				pose = profile.Evaluate(previewTime, 7);
			}

			DrawViewfinder(pose, previewFieldOfView);

			if (EditorGui.Button(previewPlaying ? "Pause" : "Play"))
			{
				previewPlaying = !previewPlaying;
			}

			ImGui.SameLine();
			if (EditorGui.Button("Impact"))
			{
				impactAge = 0f;
				impactProfile = profile;
				previewPlaying = true;
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Plays the profile once in the viewfinder, as CameraShake.Impact does: its kick, and its movement dying away over its Duration.");
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(MathF.Max(60f, ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize("Lens").X - ImGui.GetStyle().ItemInnerSpacing.X));
			EditorGui.SliderFloat("Lens", ref previewFieldOfView, 5f, 90f, "%.0f deg");
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The viewfinder's vertical field of view: the same shake is larger in the picture of a longer lens.");
			}

			Measure(profile, now);
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled($"Turns the view by about {measuredTurn:0.00} deg (at most {measuredPeak:0.0}), moves the camera by about {measuredMove * 100f:0.0} cm.");
			ImGui.PopTextWrapPos();
			if (context.Editor.IsPlaying)
			{
				CameraShake main = CameraShake.Main;
				ImGui.BeginDisabled(main == null);
				float width = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) * 0.5f;
				if (EditorGui.Button("Impact In Game", new NVector2(width, 0f)) && main != null)
				{
					main.Impact(1f, profile);
				}

				ImGui.SameLine();
				if (EditorGui.Button("Play In Game 5 s", new NVector2(width, 0f)) && main != null)
				{
					trial?.Stop();
					trial = main.Play(profile);
					trialEnds = now + 5.0;
				}

				ImGui.EndDisabled();
				if (main == null)
				{
					ImGui.TextDisabled("No enabled Camera Shake in the running game.");
				}

				if (trial != null && now >= trialEnds)
				{
					trial.Stop();
					trial = null;
				}
			}
			else
			{
				trial = null;
			}
		}

		// A drawn scene through a camera with the pose: far lines every 5 degrees and a horizon (they show the turning),
		// posts two metres away (they show the moving against the far lines), and the frame's own cross, which stays.
		private static void DrawViewfinder(CameraShakePose pose, float fieldOfView)
		{
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			NVector2 min = ImGui.GetCursorScreenPos();
			float width = MathF.Max(120f, ImGui.GetContentRegionAvail().X);
			float height = MathF.Min(width * 9f / 16f, ImGui.GetTextLineHeight() * 12f);
			var max = new NVector2(min.X + width, min.Y + height);
			var centre = new NVector2(min.X + width * 0.5f, min.Y + height * 0.5f);
			float perDegree = height / MathF.Max(fieldOfView + pose.FieldOfView, 1f);

			// The picture turns against the camera's roll.
			float roll = -pose.Rotation.Z * ToRadians;
			float cos = MathF.Cos(roll);
			float sin = MathF.Sin(roll);

			NVector2 Far(float right, float up)
			{
				float x = (right - pose.Rotation.Y) * perDegree;
				float y = -(up - pose.Rotation.X) * perDegree;
				return new NVector2(centre.X + x * cos - y * sin, centre.Y + x * sin + y * cos);
			}

			NVector2 Near(float x, float y, float z)
			{
				float depth = MathF.Max(z - pose.Position.Z, 0.05f);
				return Far(MathF.Atan2(x - pose.Position.X, depth) * ToDegrees, MathF.Atan2(y - pose.Position.Y, depth) * ToDegrees);
			}

			uint sky = ImGui.GetColorU32(new NVector4(0.13f, 0.16f, 0.21f, 1f));
			uint ground = ImGui.GetColorU32(new NVector4(0.16f, 0.15f, 0.13f, 1f));
			uint line = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.11f));
			uint horizon = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.4f));
			uint post = ImGui.GetColorU32(new NVector4(0.95f, 0.7f, 0.35f, 0.9f));
			uint frame = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.55f));

			draw.AddRectFilled(min, max, sky);
			draw.PushClipRect(min, max, true);
			draw.AddQuadFilled(Far(-200f, 0f), Far(200f, 0f), Far(200f, -200f), Far(-200f, -200f), ground);
			for (int i = -18; i <= 18; i++)
			{
				draw.AddLine(Far(i * 5f, -90f), Far(i * 5f, 90f), line);
				draw.AddLine(Far(-90f, i * 5f), Far(90f, i * 5f), i == 0 ? horizon : line, i == 0 ? 1.5f : 1f);
			}

			// Posts standing on a floor 1.4 m below the camera, 2 m and 3.5 m ahead.
			for (int i = -2; i <= 2; i++)
			{
				float depth = i % 2 == 0 ? 2f : 3.5f;
				draw.AddLine(Near(i * 0.55f, -1.4f, depth), Near(i * 0.55f, 0.25f, depth), post, MathF.Max(1.5f, 12f / depth * perDegree / 6f));
			}

			draw.PopClipRect();

			// The frame itself: it does not move, the scene does.
			float arm = height * 0.06f;
			draw.AddLine(new NVector2(centre.X - arm, centre.Y), new NVector2(centre.X + arm, centre.Y), frame);
			draw.AddLine(new NVector2(centre.X, centre.Y - arm), new NVector2(centre.X, centre.Y + arm), frame);
			draw.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border));
			ImGui.Dummy(new NVector2(width, height));
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("The profile at full strength, seen through a camera: the far lines stand 5 degrees apart, the posts 2 and 3.5 m away.");
			}
		}

		// How far the profile turns and moves a camera, over half a minute of it (worked out now and then, not every frame).
		private static void Measure(CameraShakeProfile profile, double now)
		{
			if (ReferenceEquals(measured, profile) && now - measuredAt < 0.5)
			{
				return;
			}

			measured = profile;
			measuredAt = now;
			double turn = 0.0;
			double move = 0.0;
			float peak = 0f;
			const int samples = 360;
			for (int i = 0; i < samples; i++)
			{
				CameraShakePose pose = profile.Evaluate(i / 12.0, 7);
				float turned = pose.Rotation.Length;
				turn += turned * turned;
				move += pose.Position.LengthSquared;
				peak = MathF.Max(peak, turned);
			}

			measuredTurn = (float)Math.Sqrt(turn / samples);
			measuredMove = (float)Math.Sqrt(move / samples);
			measuredPeak = peak;
		}

		private static string NameOf(CameraShakeProfile profile)
		{
			return profile == null ? "(no profile)" : string.IsNullOrEmpty(profile.name) ? "Profile" : profile.name;
		}

		// "HandheldTelephoto" -> "Handheld Telephoto".
		private static string Spaced(string name)
		{
			var text = new System.Text.StringBuilder(name.Length + 4);
			for (int i = 0; i < name.Length; i++)
			{
				if (i > 0 && char.IsUpper(name[i]))
				{
					text.Append(' ');
				}

				text.Append(name[i]);
			}

			return text.ToString();
		}

		private static void Sphere(Vector3 center, float radius, Color4 color)
		{
			Circle(center, Vector3.UnitX * radius, Vector3.UnitY * radius, color);
			Circle(center, Vector3.UnitY * radius, Vector3.UnitZ * radius, color);
			Circle(center, Vector3.UnitX * radius, Vector3.UnitZ * radius, color);
		}

		private static void Circle(Vector3 center, Vector3 axisA, Vector3 axisB, Color4 color)
		{
			Vector3 previous = center + axisA;
			for (int i = 1; i <= Segments; i++)
			{
				float angle = i * MathF.PI * 2f / Segments;
				Vector3 point = center + axisA * MathF.Cos(angle) + axisB * MathF.Sin(angle);
				DebugDraw.Line(previous, point, color);
				previous = point;
			}
		}

		#endregion
	}
}
