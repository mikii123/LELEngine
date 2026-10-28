using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.Animation;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Animation
{
	/// <summary>
	///     The Animator component's inspector below its fields (open the controller, bind state, live states per layer
	///     with progress and parameters in play mode) and the inspectors of .animator and .anim assets in the Project panel.
	/// </summary>
	internal static class AnimatorComponentInspector
	{
		#region PublicMethods

		public static bool Draw(Animator animator, InspectorContext context)
		{
			EditorApplication editor = context.Editor;
			AnimatorController controller = animator.controller;
			if (controller == null)
			{
				ImGui.TextDisabled("Assign an animator controller (Project panel: right click > Create Animator Controller).");
				return false;
			}

			if (EditorGui.Button("Open in Animator", new NVector2(-1, 0)))
			{
				editor.OpenAnimator(controller);
			}

			var states = new List<AnimatorState>();
			foreach (AnimatorLayer layer in controller.layers) layer.stateMachine.CollectStates(states);
			ImGui.TextDisabled(controller.layers.Count + (controller.layers.Count == 1 ? " layer, " : " layers, ") + states.Count + " states, " + controller.parameters.Count + " parameters");

			AnimatorPlayer player = animator.Player;
			if (player == null || !player.IsBuilt)
			{
				if (editor.IsPlaying)
				{
					ImGui.TextDisabled("Not running yet.");
				}

				return false;
			}

			if (player.Layout.Unresolved.Count > 0)
			{
				ImGui.TextColored(EditorStyle.Warning, player.Layout.Unresolved.Count + " animated properties not found on this hierarchy:");
				foreach (string unresolved in player.Layout.Unresolved)
				{
					ImGui.BulletText(unresolved);
				}
			}

			ImGui.SeparatorText("Layers");
			for (int i = 0; i < player.Layers.Length; i++)
			{
				LayerPlayer layer = player.Layers[i];
				ImGui.PushID(i);
				string current = layer.Current != null ? layer.Current.State.FullName : "(none)";
				ImGui.TextUnformatted(layer.Layer.Name + ": " + current);
				if (layer.Current != null)
				{
					double time = layer.Current.NormalizedTime;
					float fraction = layer.Current.Loop ? (float)(time - Math.Floor(time)) : (float)Math.Clamp(time, 0d, 1d);
					ImGui.ProgressBar(fraction, new NVector2(-1, 6), "");
				}

				if (layer.InTransition)
				{
					ImGui.TextDisabled("  -> " + layer.Next.State.FullName + "  " + (layer.TransitionProgress * 100f).ToString("0") + "%");
					ImGui.ProgressBar(layer.TransitionProgress, new NVector2(-1, 4), "");
				}

				if (i > 0)
				{
					float weight = layer.Weight;
					ImGui.SetNextItemWidth(-1);
					if (EditorGui.SliderFloat("##weight", ref weight, 0f, 1f, "weight %.2f"))
					{
						layer.Weight = weight;
					}
				}

				ImGui.PopID();
			}

			ImGui.SeparatorText("Parameters");
			foreach (ParameterState state in player.Parameters)
			{
				ImGui.PushID(state.Name);
				FieldDrawer.Label(state.Name, null, context);
				if (state.IsTrigger)
				{
					bool set = state is ParameterState<bool> b && b.value;
					if (EditorGui.Button(set ? "Set!" : "Fire"))
					{
						state.Boxed = true;
					}
				}
				else
				{
					object boxed = state.Boxed;
					object undoTarget = context.UndoTarget;
					context.UndoTarget = null;
					bool changed = FieldDrawer.DrawValue("##" + state.Name, state.ValueType, ref boxed, null, context, 1);
					context.UndoTarget = undoTarget;
					if (changed)
					{
						state.Boxed = boxed;
					}
				}

				ImGui.PopID();
			}

			ImGui.TextDisabled($"Last update: {AnimationSystem.LastUpdateMilliseconds:0.00} ms for {AnimationSystem.LastAnimatorCount} animators{(AnimationSystem.LastParallel ? " (parallel)" : "")}");
			return false;
		}

		/// <summary>The .animator asset's inspector: a summary and the Open button.</summary>
		public static void DrawControllerAsset(string assetPath, InspectorContext context)
		{
			if (EditorGui.Button("Open in Animator", new NVector2(-1, 0)))
			{
				context.Editor.OpenAnimator(assetPath);
			}

			try
			{
				AnimatorController controller = AnimatorController.Load(assetPath);
				var states = new List<AnimatorState>();
				foreach (AnimatorLayer layer in controller.layers) layer.stateMachine.CollectStates(states);
				ImGui.Text($"{controller.layers.Count} layers, {states.Count} states, {controller.parameters.Count} parameters, {controller.CollectClips().Count} clips");
				foreach (AnimatorLayer layer in controller.layers)
				{
					var layerStates = new List<AnimatorState>();
					layer.stateMachine.CollectStates(layerStates);
					ImGui.BulletText(layer.name + ": " + layerStates.Count + " states" + (layer.blending == LayerBlendingMode.Additive ? ", additive" : "") + (!layer.mask.IsEmpty ? ", masked" : ""));
				}

				if (controller.parameters.Count > 0)
				{
					ImGui.SeparatorText("Parameters");
					foreach (AnimatorParameter parameter in controller.parameters)
					{
						ImGui.BulletText(parameter.name + "  (" + parameter.TypeLabel + ")  " + (parameter.defaultValue?.ToString() ?? ""));
					}
				}

				List<string> problems = controller.Validate();
				if (problems.Count > 0)
				{
					ImGui.SeparatorText("Problems");
					foreach (string problem in problems)
					{
						ImGui.TextColored(EditorStyle.Warning, problem);
					}
				}
			}
			catch (Exception e)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot load: " + e.Message);
			}
		}

		/// <summary>The .anim asset's inspector: settings (undoable, saved on edit) and the Open button.</summary>
		public static void DrawClipAsset(string assetPath, InspectorContext context)
		{
			if (EditorGui.Button("Open in Animation window", new NVector2(-1, 0)))
			{
				context.Editor.OpenAnimationWindow(assetPath);
			}

			try
			{
				AnimationClip clip = AnimationClip.Load(assetPath);
				ImGui.Text($"{clip.length:0.###} s, {clip.bindings.Count} properties, {clip.events.Count} events, {clip.frameRate:0.#} fps");
				object undoTarget = context.UndoTarget;
				context.UndoTarget = clip;
				object loop = clip.loop;
				if (FieldDrawer.DrawValue("Loop", typeof(bool), ref loop, null, context, 0)) clip.loop = (bool)loop;
				object rate = clip.frameRate;
				if (FieldDrawer.DrawValue("Frame Rate", typeof(float), ref rate, null, context, 0)) clip.frameRate = Math.Max(1f, (float)rate);
				object offset = clip.cycleOffset;
				if (FieldDrawer.DrawValue("Cycle Offset", typeof(float), ref offset, null, context, 0)) clip.cycleOffset = (float)offset;
				object length = clip.lengthOverride;
				if (FieldDrawer.DrawValue("Length Override", typeof(float), ref length, null, context, 0)) clip.lengthOverride = Math.Max(0f, (float)length);
				if (ImGui.IsItemHovered()) ImGui.SetTooltip("0: the last key or event decides the length");
				object rootMotion = clip.rootMotion;
				if (FieldDrawer.DrawValue("Root Motion", typeof(RootMotionChannels), ref rootMotion, null, context, 0)) clip.rootMotion = (RootMotionChannels)rootMotion;
				context.UndoTarget = undoTarget;
				ImGui.SeparatorText("Properties");
				foreach (AnimationBinding binding in clip.bindings)
				{
					ImGui.BulletText(binding.DisplayName + "  (" + binding.kind + ")");
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
