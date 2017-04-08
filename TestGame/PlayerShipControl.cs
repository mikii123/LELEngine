using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LELEngine;
using OpenTK;

class PlayerShipControl : Behaviour
{
    public static PlayerShipControl This;
    private float Speed = 100;
    private float AngSpeed = 300;
    public Vector3 angVelocity = Vector3.Zero;
    public Vector3 velocity = Vector3.Zero;
    public float X = 0;
    public float Y = 0;

    public override void Awake()
    {
        This = this;
    }

    public override void FixedUpdate()
    {
        Vector3 _velocity = transform.forward * Speed;
        velocity = Vector3.Lerp(velocity, _velocity, Time.fixedDeltaTime * 5);
        
        X = -(0.5f - Input.relativeMousePosition.Y);
        Y = (0.5f - Input.relativeMousePosition.X);

        Vector3 _angVelocity = new Vector3(X, Y, 0);
        angVelocity = Vector3.Lerp(angVelocity, _angVelocity, Time.fixedDeltaTime * 5);

        UpdateVelocity();
        UpdateAngVelocity();

        //Vector3 rot = QuaternionHelper.ToEulerAngles(transform.rotation);
        //transform.rotation = QuaternionHelper.Euler(rot.X, rot.Y, 80 * -angVelocity.Y);
    }

    void UpdateVelocity()
    {
        Vector3 targetPos = transform.position;
        targetPos += Vector3.UnitX * velocity.X * Time.fixedDeltaTime;
        targetPos += Vector3.UnitY * velocity.Y * Time.fixedDeltaTime;
        targetPos += Vector3.UnitZ * velocity.Z * Time.fixedDeltaTime;
        transform.position = targetPos;
    }

    void UpdateAngVelocity()
    {
        Quaternion rotOffset = QuaternionHelper.Euler(angVelocity * AngSpeed * Time.fixedDeltaTime);
        transform.rotation *= rotOffset;
    }
}
