using LELEngine;
using LELEngine.Mathematics;
using LELEngine.Windowing;

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
        float time = Time.deltaTime * 50;
        if(Input.GetKey(Keys.Left))
        {
            angle.Y -= time;
        }
        if(Input.GetKey(Keys.Right))
        {
            angle.Y += time;
        }
        if (Input.GetKey(Keys.Up))
        {
            angle.X += time;
        }
        if (Input.GetKey(Keys.Down))
        {
            angle.X -= time;
        }
        if (Input.GetKey(Keys.Z))
        {
            angle.Z -= time;
        }
        if (Input.GetKey(Keys.X))
        {
            angle.Z += time;
        }
        Quaternion targetRot = QuaternionHelper.Euler(angle.X, angle.Y, angle.Z);
        transform.localRotation = Quaternion.Slerp(transform.localRotation, targetRot, Time.deltaTime * 3);
    }
}
