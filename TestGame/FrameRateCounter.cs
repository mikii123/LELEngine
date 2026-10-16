using System;
using LELEngine;

namespace TestGame
{
	/// <summary>Shows the frame rate and render time in the console window's title.</summary>
	public sealed class FrameRateCounter : Behaviour
	{
		#region PublicMethods

		public override void Update()
		{
			Console.Title = $@"LELEngine - FPS: {Time.frameRate:0}  Render: {Time.renderDeltaTime}ms";
		}

		#endregion
	}
}
