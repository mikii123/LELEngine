using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
class FrameRateCounter : Behaviour
{
    private float FrameRate = 0;
    public override void Update()
    {
        FrameRate = 1/Time.deltaTimeF;
        Console.Title = "LELEngine - FPS " + FrameRate.ToString("0");
    }
}
