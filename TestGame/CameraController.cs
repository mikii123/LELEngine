using LELEngine;
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

// Do NOT call "base" in any overridden functions
class CameraController : Behaviour
{
    private Vector3 angle;

    public override void Start()
    {
        angle = QuaternionHelper.ToEulerAngles(transform.localRotation);
    }

    public override void FixedUpdate()
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

        transform.rotation = Quaternion.Slerp(transform.rotation, QuaternionHelper.LookRotation(PlayerShipControl.This.transform.forward, PlayerShipControl.This.transform.up), Time.fixedDeltaTime * 10);
        Vector3 targetPos = PlayerShipControl.This.transform.position;
        targetPos -= PlayerShipControl.This.transform.forward * 16;
        targetPos += PlayerShipControl.This.transform.up * 5;
        targetPos += PlayerShipControl.This.transform.right * PlayerShipControl.This.angVelocity.Y * 25;
        transform.position = targetPos;
    }
}
