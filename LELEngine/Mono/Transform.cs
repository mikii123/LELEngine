using LELEngine;
using LELEngine.Shaders;
using OpenTK;

// Do NOT call "base" in any overridden functions
class Transform : Behaviour
{
    #region Positions
    private Vector3 _position;
    public Vector3 position
    {
        get
        {
            if (parent != null)
                return parent.position + 
                    (parent.right * localPosition.X) +
                    (parent.up * localPosition.Y) +
                    (parent.forward * localPosition.Z);
            else
                return _position;
        }
        set
        {
            _position = value;
            if(parent != null)
            {
                localPosition = value - parent.position;
            }
        }
    }
    private Vector3 _localPosition = Vector3.Zero;
    public Vector3 localPosition
    {
        get
        {
            if (parent != null)
                return _localPosition;
            else
                return position;
        }
        set
        {
            _localPosition = value;
            if (parent == null)
                position = value;
        }
    }
    #endregion

    #region Rotations
    private Quaternion _rotation = Quaternion.Identity;
    public Quaternion rotation
    {
        get
        {
            if (parent != null)
                return parent.rotation * localRotation;
            else
                return _rotation;
        }
        set
        {
            _rotation = value;
            if(parent != null)
            {
                localRotation = value * parent.rotation.Inverted();
            }
        }
    }
    private Quaternion _localRotation = Quaternion.Identity;
    public Quaternion localRotation
    {
        get
        {
            if (parent != null)
                return _localRotation;
            else
                return rotation;
        }
        set
        {
            _localRotation = value;
            if (parent == null)
                rotation = value;
        }
    }
    #endregion

    #region Scale
    private Vector3 _scale = Vector3.One;
    public Vector3 scale
    {
        get
        {
            if (parent != null)
                return parent.scale * _scale;
            else
                return _scale;
        }
        set
        {
            _scale = value;
        }
    }
    #endregion

    public Transform parent;

    private LELEngine.Shaders.Uniforms.Matrix4 modelMatrix = new LELEngine.Shaders.Uniforms.Matrix4("modelMatrix");

    public Vector3 forward
    {
        get
        {
            return (rotation * Vector3.UnitZ).Normalized();
        }
    }
    public Vector3 up
    {
        get
        {
            return (rotation * Vector3.UnitY).Normalized();
        }
    }
    public Vector3 right
    {
        get
        {
            return (rotation * Vector3.UnitX).Normalized();
        }
    }

    public void SetModelMatrix(ShaderProgram program)
    {
        modelMatrix.Set(program);
    }

    public override void LateUpdate()
    {
        modelMatrix.Matrix = Matrix4.CreateScale(scale) * Matrix4.CreateFromQuaternion(rotation) * Matrix4.CreateTranslation(position);
    }

    public void SetParent(Transform _transform)
    {
        parent = _transform;
    }

    public void LookAt(Vector3 pos)
    {
        Vector3 dir = (pos - position).Normalized() * 100;
        rotation = QuaternionHelper.LookRotation(dir, Vector3.UnitY);
    }
}
