using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;
using OpenTK;

// Do NOT call "base" in any overridden functions
class Rotator : Behaviour
{
    private Vector3 angle;

    public override void Start()
    {
        angle = QuaternionHelper.ToEulerAngles(transform.localRotation);
    }

    public override void Update()
    {
        float time = Time.deltaTimeF * 50;
        if(Input.GetKey(OpenTK.Input.Key.Left))
        {
            angle.Y += time;
        }
        if(Input.GetKey(OpenTK.Input.Key.Right))
        {
            angle.Y -= time;
        }
        if (Input.GetKey(OpenTK.Input.Key.Up))
        {
            angle.X += time;
        }
        if (Input.GetKey(OpenTK.Input.Key.Down))
        {
            angle.X -= time;
        }
        if (Input.GetKey(OpenTK.Input.Key.Z))
        {
            angle.Z += time;
        }
        if (Input.GetKey(OpenTK.Input.Key.X))
        {
            angle.Z -= time;
        }
        Quaternion targetRot = QuaternionHelper.EulerGimbal(angle.X, angle.Y, angle.Z);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, targetRot, Time.deltaTimeF * 3);
    }
}
