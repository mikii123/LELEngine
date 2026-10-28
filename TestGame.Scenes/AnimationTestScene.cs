using System;
using LELEngine;
using LELEngine.Animation;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Showcase of the animation system (anim=1): a grid of cubes, each with an <see cref="Animator" /> playing a
	///     controller built in code (clips of position, rotation and scale; a 1D blend tree on Speed; a Jump trigger
	///     from Any State with an event; an additive wobble layer). The cubes start at different cycle offsets, so the
	///     animators evaluate in parallel on worker threads (24 of them). <c>animcount=N</c> sizes the grid.
	/// </summary>
	public static class AnimationTestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, GITestScene.Options options, int count)
		{
			VfxTestScene.BuildEnvironment(scene, options);
			AnimatorController controller = BuildController();
			int side = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
			for (int i = 0; i < count; i++)
			{
				int x = i % side;
				int z = i / side;
				GameObject cube = SceneModels.Instantiate(scene, SceneModels.Cube, "Dancer " + i);
				cube.transform.position = new Vector3((x - (side - 1) * 0.5f) * 1.6f, 0.5f, 2f + (z - (side - 1) * 0.5f) * 1.6f);
				cube.transform.localScale = new Vector3(0.6f, 0.6f, 0.6f);
				cube.GetComponent<MeshRenderer>().SetMaterials(SceneModels.Material(i % 3 == 0 ? "LitRed.material" : i % 3 == 1 ? "LitGreen.material" : "LitBlue.material"));
				Animator animator = cube.AddComponent<Animator>();
				animator.controller = controller;
				AnimDemoDriver driver = cube.AddComponent<AnimDemoDriver>();
				driver.phase = i * 0.37f;
			}

			GITestScene.ApplyHarness(scene, options);
		}

		/// <summary>The controller every cube plays: Idle (breathing scale), Move (1D blend: sway / spin), Jump (an arc with an event), plus an additive wobble layer.</summary>
		public static AnimatorController BuildController()
		{
			var idle = new AnimationClip { name = "Idle", loop = true };
			AnimationBinding scale = idle.EnsureBinding("", typeof(Transform), AnimationBinder.LocalScale, AnimationValueKind.Vector3);
			for (int c = 0; c < 3; c++)
			{
				scale.channels[c].AddKey(0f, 0.6f);
				scale.channels[c].AddKey(0.8f, c == 1 ? 0.75f : 0.55f);
				scale.channels[c].AddKey(1.6f, 0.6f);
			}

			var sway = new AnimationClip { name = "Sway", loop = true };
			AnimationBinding swayRotation = sway.EnsureBinding("", typeof(Transform), AnimationBinder.LocalEulerAngles, AnimationValueKind.Euler);
			swayRotation.channels[2].AddKey(0f, -20f);
			swayRotation.channels[2].AddKey(0.5f, 20f);
			swayRotation.channels[2].AddKey(1f, -20f);
			swayRotation.channels[0].AddKey(0f, 0f);
			swayRotation.channels[1].AddKey(0f, 0f);

			var spin = new AnimationClip { name = "Spin", loop = true };
			AnimationBinding spinRotation = spin.EnsureBinding("", typeof(Transform), AnimationBinder.LocalEulerAngles, AnimationValueKind.Euler);
			spinRotation.channels[1].AddKey(new Keyframe(0f, 0f) { tangentMode = TangentMode.Linear });
			spinRotation.channels[1].AddKey(new Keyframe(1f, 360f) { tangentMode = TangentMode.Linear });
			spinRotation.channels[1].RecalculateTangents();
			spinRotation.channels[0].AddKey(0f, 0f);
			spinRotation.channels[2].AddKey(0f, 0f);
			AnimationBinding spinScale = spin.EnsureBinding("", typeof(Transform), AnimationBinder.LocalScale, AnimationValueKind.Vector3);
			for (int c = 0; c < 3; c++)
			{
				spinScale.channels[c].AddKey(0f, 0.5f);
			}

			var jump = new AnimationClip { name = "Jump" };
			AnimationBinding jumpPosition = jump.EnsureBinding("", typeof(Transform), AnimationBinder.LocalPosition, AnimationValueKind.Vector3);
			jumpPosition.channels[1].AddKey(0f, 0.5f);
			jumpPosition.channels[1].AddKey(0.3f, 1.8f);
			jumpPosition.channels[1].AddKey(0.6f, 0.5f);
			jump.AddEvent(0.6f, "OnLand");
			jump.rootMotion = RootMotionChannels.None;

			var wobble = new AnimationClip { name = "Wobble", loop = true };
			AnimationBinding wobbleRotation = wobble.EnsureBinding("", typeof(Transform), AnimationBinder.LocalEulerAngles, AnimationValueKind.Euler);
			wobbleRotation.channels[0].AddKey(0f, 0f);
			wobbleRotation.channels[0].AddKey(0.35f, 12f);
			wobbleRotation.channels[0].AddKey(0.7f, 0f);
			wobbleRotation.channels[1].AddKey(0f, 0f);
			wobbleRotation.channels[2].AddKey(0f, 0f);
			foreach (AnimationClip clip in new[] { idle, sway, spin, jump, wobble })
			{
				clip.MarkChanged();
			}

			AnimatorController controller = AnimatorController.CreateDefault("Dancer");
			AnimatorParameter speed = controller.AddFloat("Speed");
			AnimatorParameter jumpTrigger = controller.AddTrigger("Jump");
			AnimatorParameter offset = controller.AddFloat("Offset");
			AnimatorParameter wobbleWeight = controller.AddFloat("Wobble", 1f);

			AnimatorStateMachine root = controller.layers[0].stateMachine;
			AnimatorState idleState = root.AddState("Idle", idle, new Vector2(0f, 0f));
			idleState.cycleOffsetParameter = offset.id;
			var tree = new BlendTree { name = "Locomotion", parameter = speed.id, automateThresholds = false };
			tree.AddClip(sway, 0f);
			tree.AddClip(spin, 1f);
			AnimatorState move = root.AddState("Move", new Vector2(300f, 0f));
			move.motion = tree;
			move.cycleOffsetParameter = offset.id;
			AnimatorState jumpState = root.AddState("Jump", jump, new Vector2(150f, 200f));
			jumpState.writeDefaults = false;

			AnimatorTransition toMove = idleState.AddTransition(move);
			toMove.hasExitTime = false;
			toMove.duration = 0.3f;
			toMove.conditions.Add(new ParameterCondition { parameter = speed.id, mode = ConditionMode.Greater, value = new FloatValue { value = 0.05f } });
			AnimatorTransition toIdle = move.AddTransition(idleState);
			toIdle.hasExitTime = false;
			toIdle.duration = 0.3f;
			toIdle.conditions.Add(new ParameterCondition { parameter = speed.id, mode = ConditionMode.LessOrEqual, value = new FloatValue { value = 0.05f } });
			AnimatorTransition anyJump = root.AddAnyStateTransition(jumpState);
			anyJump.hasExitTime = false;
			anyJump.duration = 0.05f;
			anyJump.conditions.Add(new ParameterCondition { parameter = jumpTrigger.id, mode = ConditionMode.Trigger });
			AnimatorTransition jumpDone = jumpState.AddTransition(idleState);
			jumpDone.hasExitTime = true;
			jumpDone.exitTime = 1f;
			jumpDone.duration = 0.1f;

			AnimatorLayer additive = controller.AddLayer("Wobble");
			additive.blending = LayerBlendingMode.Additive;
			additive.weight = 1f;
			AnimatorState wobbleState = additive.stateMachine.AddState("Wobble", wobble, new Vector2(0f, 0f));
			wobbleState.speedParameter = wobbleWeight.id;
			return controller;
		}

		#endregion
	}

	/// <summary>Drives a dancer: Speed from a sine (phase shifted per cube), a Jump every few seconds, a flash on the landing event.</summary>
	public class AnimDemoDriver : Behaviour
	{
		public float phase;
		private float time;
		private float nextJump = 2f;
		private Animator animator;

		public override void Start()
		{
			animator = GetComponent<Animator>();
			animator.SetFloat("Offset", phase % 1f);
			nextJump = 2f + phase * 2f;
		}

		public override void Update()
		{
			time += Time.deltaTime;
			animator.SetFloat("Speed", Math.Max(0f, (float)Math.Sin(time * 0.6f + phase)));
			animator.SetFloat("Wobble", 0.5f + 0.5f * (float)Math.Sin(time * 0.3f + phase));
			if (time >= nextJump)
			{
				animator.SetTrigger("Jump");
				nextJump = time + 4f + phase % 2f;
			}
		}

		public void OnLand()
		{
			// A landing: the next idle cycle starts from the squash (the clip's cycle offset parameter).
			animator.SetFloat("Offset", 0.5f);
		}
	}
}
