using System;
using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Trail tests: moves its object on a Lissajous path around <see cref="Center" /> in the XY plane
	///     (x = Size.x sin(FrequencyX t + Phase), y = Size.y sin(FrequencyY t)), optionally spinning about the X axis
	///     (TransformZ ribbons twist) and switching its trail's emitting off and on every <see cref="EmitToggle" /> seconds.
	/// </summary>
	public sealed class TrailMover : Behaviour
	{
		#region PublicFields

		public Vector3 Center;
		public Vector2 Size = new Vector2(1f, 0.5f);

		/// <summary>Radians per second.</summary>
		public float FrequencyX = 1f;

		public float FrequencyY = 2f;
		public float Phase;

		/// <summary>Degrees per second about the X axis (0: no spin).</summary>
		public float SpinSpeed;

		/// <summary>Seconds between switching the trail's emitting (0: always on).</summary>
		public float EmitToggle;

		#endregion

		#region PrivateFields

		private float time;

		#endregion

		#region UnityMethods

		public override void Update()
		{
			time += Time.deltaTime;
			transform.position = Center + new Vector3(Size.X * MathF.Sin(FrequencyX * time + Phase), Size.Y * MathF.Sin(FrequencyY * time), 0f);
			if (SpinSpeed != 0f)
			{
				transform.rotation = Quaternion.FromAxisAngle(Vector3.UnitX, time * SpinSpeed * MathF.PI / 180f);
			}

			if (EmitToggle > 0f && GetComponent<TrailRenderer>() is TrailRenderer trail)
			{
				trail.emitting = ((int)(time / EmitToggle) & 1) == 0;
			}
		}

		#endregion
	}
}
