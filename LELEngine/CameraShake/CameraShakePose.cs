using System;
using OpenTK.Mathematics;

/// <summary>
///     What a shake adds to a camera at one moment, in the camera's own picture: where it is moved to and how its view
///     is turned. Poses add up (<see cref="CameraShake" /> sums the shakes playing on it).
/// </summary>
public struct CameraShakePose
{
	#region PublicFields

	/// <summary>Metres the camera is moved: x to the right of the picture, y up, z forward (along the view).</summary>
	public Vector3 Position;

	/// <summary>Degrees the view is turned: x tilt (up), y pan (to the right), z roll (clockwise, the horizon falling to the right).</summary>
	public Vector3 Rotation;

	/// <summary>Degrees added to the camera's vertical field of view.</summary>
	public float FieldOfView;

	/// <summary>
	///     <see cref="Position" /> along a transform's own axes: the engine's cameras look along +Z with +Y up, which
	///     puts the right of the picture at -X.
	/// </summary>
	public Vector3 LocalPosition => new Vector3(-Position.X, Position.Y, Position.Z);

	/// <summary>
	///     <see cref="Rotation" /> as a rotation in a transform's own axes (multiply the object's rotation by it): the
	///     roll first, about the view, then the tilt, then the pan.
	/// </summary>
	public Quaternion LocalRotation
	{
		get
		{
			if (Rotation == Vector3.Zero)
			{
				return Quaternion.Identity;
			}

			const float toRadians = MathF.PI / 180f;
			return Quaternion.FromAxisAngle(Vector3.UnitY, -Rotation.Y * toRadians)
				* Quaternion.FromAxisAngle(Vector3.UnitX, -Rotation.X * toRadians)
				* Quaternion.FromAxisAngle(Vector3.UnitZ, Rotation.Z * toRadians);
		}
	}

	/// <summary>Nothing is added.</summary>
	public bool IsZero => Position == Vector3.Zero && Rotation == Vector3.Zero && FieldOfView == 0f;

	#endregion

	#region PublicMethods

	public static CameraShakePose operator +(CameraShakePose a, CameraShakePose b)
	{
		return new CameraShakePose { Position = a.Position + b.Position, Rotation = a.Rotation + b.Rotation, FieldOfView = a.FieldOfView + b.FieldOfView };
	}

	public static CameraShakePose operator *(CameraShakePose pose, float scale)
	{
		return new CameraShakePose { Position = pose.Position * scale, Rotation = pose.Rotation * scale, FieldOfView = pose.FieldOfView * scale };
	}

	public override string ToString()
	{
		return $"turned {Rotation.X:0.###}, {Rotation.Y:0.###}, {Rotation.Z:0.###} deg; moved {Position.X:0.####}, {Position.Y:0.####}, {Position.Z:0.####} m; field of view {FieldOfView:+0.###;-0.###;0}";
	}

	#endregion
}
