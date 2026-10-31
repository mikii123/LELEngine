using System;
using LELEngine;

namespace TestGame.Scenes
{
	/// <summary>
	///     Drive cone tests: runs its object's <see cref="DriveCone" /> through a cycle of <see cref="Period" /> seconds: the
	///     throttle up from idle to full (the ignition plays at full) and back, then the engine stopped for the last
	///     <see cref="OffShare" /> of the cycle (and lit again at the next one). A <see cref="Period" /> of 0 leaves the cone
	///     as it was set.
	/// </summary>
	public sealed class DriveConeCycler : Behaviour
	{
		#region PublicFields

		public float Period = 6f;

		/// <summary>Share of the cycle the engine is off.</summary>
		public float OffShare = 0.15f;

		/// <summary>Seconds into the cycle at the start.</summary>
		public float Phase;

		#endregion

		#region PrivateFields

		private float time;

		#endregion

		#region UnityMethods

		public override void Update()
		{
			DriveCone cone = GetComponent<DriveCone>();
			if (cone == null || Period <= 0f)
			{
				return;
			}

			time += Time.deltaTime;
			float share = (time + Phase) % Period / Period;
			float burning = 1f - Math.Clamp(OffShare, 0f, 0.9f);
			cone.Burning = share < burning;
			// Idle for the first fifth, full (the ignition plays), then back down to idle over the last quarter.
			float t = share / burning;
			cone.Throttle = t < 0.2f ? 0f : t < 0.75f ? 1f : Math.Clamp(1f - (t - 0.75f) / 0.25f, 0f, 1f);
		}

		#endregion
	}
}
