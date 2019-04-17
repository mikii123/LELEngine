using System;
using LELCS.Model;
using LELEngine;

namespace TestGame
{
	public class FrameRateCounterSystem : ECSSystem<FrameRateCounterComponent>
	{
		#region PublicFields

		public override Type[] Subtractive => null;

		#endregion

		#region ProtectedMethods

		protected override void Execute(ECSEntity ecsEntity, ref FrameRateCounterComponent component1)
		{
			Console.Title = $@"LELEngine - FPS: {Time.frameRate:0}  Render: {Time.renderDeltaTime}ms";
		}

		#endregion
	}
}
