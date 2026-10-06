using System;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Dynamic light emitter for GI tests: moves on a circle (or stays put when Radius is 0)
	///     and pulses its emissive intensity. Works on any renderer using a material with an "Emissive" vec4.
	/// </summary>
	public sealed class EmissiveOrbiter : Behaviour
	{
		#region PublicFields

		public Vector3 Center;
		public float Radius = 5f;
		public float Height = 1f;

		/// <summary>Radians per second.</summary>
		public float AngularSpeed = 0.6f;

		/// <summary>Vertical bobbing amplitude in world units.</summary>
		public float Bobbing = 0.3f;

		public float PhaseOffset;
		public float PulseSpeed = 2f;

		/// <summary>Pulse depth in [0,1]. 0 keeps the intensity constant.</summary>
		public float PulseAmount = 0.35f;

		public Color4 Color = Color4.White;
		public float Intensity = 10f;

		#endregion

		#region PrivateFields

		private Material material;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			MeshRenderer renderer = GetComponent<MeshRenderer>();
			if (renderer?.Material == null)
			{
				return;
			}

			// Shared materials come from InternalStorage; clone so this emitter has its own parameters.
			material = renderer.Material.Clone();
			renderer.SetMaterial(material);

			// Emitters are light sources: no self-shadowing, no indirect pickup on their own surface.
			renderer.ReceiveShadows = false;
			renderer.ReceiveGI = false;

			// Dim tinted albedo so the body reads as the light color even where emission is low.
			material.SetVector4("Color", new Vector4(Color.R * 0.3f, Color.G * 0.3f, Color.B * 0.3f, 1f));
			Apply(0f);
		}

		public override void Update()
		{
			Apply(Time.time);
		}

		#endregion

		#region PrivateMethods

		private void Apply(float time)
		{
			float angle = time * AngularSpeed + PhaseOffset;
			Vector3 position = Center + new Vector3(
				(float)Math.Cos(angle) * Radius,
				Height + (float)Math.Sin(angle * 1.7f) * Bobbing,
				(float)Math.Sin(angle) * Radius);
			transform.position = position;

			float pulse = 1f - PulseAmount * 0.5f * (1f + (float)Math.Sin(time * PulseSpeed + PhaseOffset));
			material?.SetVector4("Emissive", new Vector4(Color.R, Color.G, Color.B, Intensity * pulse));
		}

		#endregion
	}
}
