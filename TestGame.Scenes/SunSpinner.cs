using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>Directional light tests: turns its object about the world's vertical axis (suns moving across the scene).</summary>
	public sealed class SunSpinner : Behaviour
	{
		/// <summary>Degrees per second.</summary>
		public float Speed = 20f;

		public override void Update()
		{
			transform.rotation = Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(Speed * Time.deltaTime)) * transform.rotation;
		}
	}
}
