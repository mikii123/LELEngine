using LELEngine;
using LELEngine.Shaders;
using OpenTK;
using Matrix4 = LELEngine.Shaders.Uniforms.Matrix4;

// Do NOT call "base" in any overridden functions
public sealed class Transform : Behaviour
{
	#region PublicFields

	public Vector3 forward => (rotation * Vector3.UnitZ).Normalized();

	public Vector3 up => (rotation * Vector3.UnitY).Normalized();

	public Vector3 right => (rotation * Vector3.UnitX).Normalized();

	public Transform parent;

	#endregion

	#region PrivateFields

	private Matrix4 modelMatrix = new Matrix4("modelMatrix");

	#endregion

	#region UnityMethods

	public override void LateUpdate()
	{
		modelMatrix.Matrix = OpenTK.Matrix4.CreateScale(scale) * OpenTK.Matrix4.CreateFromQuaternion(rotation) * OpenTK.Matrix4.CreateTranslation(position);
	}

	#endregion

	#region PublicMethods

	public void SetModelMatrix(ShaderProgram program)
	{
		modelMatrix.Set(program);
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

	public void LookAt(Vector3 pos, Vector3 up)
	{
		Vector3 dir = (pos - position).Normalized() * 100;
		rotation = QuaternionHelper.LookRotation(dir, up);
	}

	#endregion

	#region Positions

	private Vector3 _position;

	public Vector3 position
	{
		get
		{
			if (parent != null)
			{
				return parent.position +
					parent.right * localPosition.X +
					parent.up * localPosition.Y +
					parent.forward * localPosition.Z;
			}

			return _position;
		}
		set
		{
			_position = value;
			if (parent != null)
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
			{
				return _localPosition;
			}

			return position;
		}
		set
		{
			_localPosition = value;
			if (parent == null)
			{
				position = value;
			}
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
			{
				return parent.rotation * localRotation;
			}

			return _rotation;
		}
		set
		{
			_rotation = value;
			if (parent != null)
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
			{
				return _localRotation;
			}

			return rotation;
		}
		set
		{
			_localRotation = value;
			if (parent == null)
			{
				rotation = value;
			}
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
			{
				return parent.scale * _scale;
			}

			return _scale;
		}
		set => _scale = value;
	}

	#endregion
}
