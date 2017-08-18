using LELEngine;
using OpenTK;

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

        if(Input.GetKey(OpenTK.Input.Key.Left))
        {
            angle.Y -= time;
        }
        if(Input.GetKey(OpenTK.Input.Key.Right))
        {
            angle.Y += time;
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
            angle.Z -= time;
        }
        if (Input.GetKey(OpenTK.Input.Key.X))
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
