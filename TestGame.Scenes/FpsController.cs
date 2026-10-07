using System;
using LELEngine;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace TestGame.Scenes
{
	/// <summary>
	///     First person walk controller. Yaw rotates this transform, pitch rotates the camera child.
	///     WASD move, Shift sprint, Esc toggles cursor lock, F toggles fly mode (Space / Ctrl for up / down).
	/// </summary>
	public sealed class FpsController : Behaviour
	{
		#region PublicFields

		public float MoveSpeed = 4f;
		public float SprintMultiplier = 2.5f;

		/// <summary>Degrees of rotation per pixel of mouse movement.</summary>
		public float MouseSensitivity = 0.12f;

		public float EyeHeight = 1.7f;
		public float MinPitch = -89f;
		public float MaxPitch = 89f;
		public bool Fly;

		/// <summary>Transform that receives pitch. Defaults to the main camera.</summary>
		public Transform CameraTransform;

		/// <summary>Scripted motion for temporal stability tests: slow yaw sweep and sideways drift, no input.</summary>
		public bool AutoPilot;

		/// <summary>Mouse look (off keeps the camera still for measurements even when the mouse moves).</summary>
		public bool MouseLook = true;

		public float AutoPilotYawAmplitude = 25f;
		public float AutoPilotStrafeAmplitude = 1.5f;
		public float AutoPilotPeriod = 8f;

		#endregion

		#region PrivateFields

		private float yaw;
		private float pitch;
		private float baseYaw;
		private Vector3 basePosition;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			if (CameraTransform == null && Camera.main != null)
			{
				CameraTransform = Camera.main.transform;
			}

			// Derive the initial yaw from the current facing so the scene can orient the player.
			Vector3 forward = transform.forward;
			yaw = (float)(Math.Atan2(forward.X, forward.Z) * QuaternionHelper.Rad2Deg);
			pitch = 0f;

			ApplyRotation();
			baseYaw = yaw;
			basePosition = transform.position;
			Input.SetCursorLocked(!AutoPilot && MouseLook);
		}

		public override void Update()
		{
			if (AutoPilot)
			{
				float phase = Time.time * MathHelper.TwoPi / AutoPilotPeriod;
				yaw = baseYaw + AutoPilotYawAmplitude * (float)Math.Sin(phase);
				pitch = 4f * (float)Math.Sin(phase * 0.5f);
				ApplyRotation();
				Vector3 right = Vector3.Cross(Vector3.UnitZ, Vector3.UnitY).Normalized();
				transform.position = basePosition + right * (AutoPilotStrafeAmplitude * (float)Math.Sin(phase * 0.7f));
				return;
			}

			if (Input.GetKeyDown(Keys.Escape))
			{
				Input.SetCursorLocked(!Input.cursorLocked);
			}

			if (Input.GetKeyDown(Keys.F))
			{
				Fly = !Fly;
			}

			if (Input.cursorLocked && MouseLook)
			{
				Look(Input.mouseDelta);
			}

			Move(Time.deltaTime);
		}

		#endregion

		#region PrivateMethods

		private void Look(Vector2 delta)
		{
			// Screen-right is -X in this engine's view convention, hence the inverted yaw.
			yaw -= delta.X * MouseSensitivity;
			pitch += delta.Y * MouseSensitivity;
			pitch = Math.Clamp(pitch, MinPitch, MaxPitch);

			ApplyRotation();
		}

		private void ApplyRotation()
		{
			transform.rotation = QuaternionHelper.Euler(0f, yaw, 0f);
			if (CameraTransform != null)
			{
				CameraTransform.localRotation = QuaternionHelper.Euler(pitch, 0f, 0f);
			}
		}

		private void Move(float deltaTime)
		{
			Vector3 forward = transform.forward;
			if (!Fly)
			{
				forward.Y = 0f;
			}
			if (forward.LengthSquared < 1e-6f)
			{
				forward = Vector3.UnitZ;
			}
			forward.Normalize();

			// Right-handed: screen-right is forward x up.
			Vector3 right = Vector3.Cross(forward, Vector3.UnitY).Normalized();

			Vector3 move = Vector3.Zero;
			if (Input.GetKey(Keys.W)) move += forward;
			if (Input.GetKey(Keys.S)) move -= forward;
			if (Input.GetKey(Keys.D)) move += right;
			if (Input.GetKey(Keys.A)) move -= right;

			if (Fly)
			{
				if (Input.GetKey(Keys.Space)) move += Vector3.UnitY;
				if (Input.GetKey(Keys.LeftControl)) move -= Vector3.UnitY;
			}

			if (move.LengthSquared > 1e-6f)
			{
				move.Normalize();
				float speed = MoveSpeed * (Input.GetKey(Keys.LeftShift) ? SprintMultiplier : 1f);
				transform.position += move * speed * deltaTime;
			}

			if (!Fly)
			{
				Vector3 position = transform.position;
				position.Y = EyeHeight;
				transform.position = position;
			}
		}

		#endregion
	}
}
