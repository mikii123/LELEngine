using System;
using LELEngine;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
class FrameRateCounter : Behaviour
{
	public override void Update()
    {
        Console.Title = "LELEngine - FPS: " + Time.frameRate.ToString("0") + "  Render: " + Time.renderDeltaTime.ToString() + "ms";
    }
}
