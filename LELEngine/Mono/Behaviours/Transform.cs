using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;
using Matrix4 = LELEngine.Shaders.Uniforms.Matrix4;

/// <summary>
///     Position, rotation and scale of a GameObject, relative to its parent. The local values are stored and
///     serialized; world values are derived through the parent chain (row-vector convention: v * M, with
///     local matrix = scale * rotation * translation and world = local * parent world).
/// </summary>
[ExecuteAlways]
public sealed class Transform : Behaviour
{
	#region PublicFields

	public Vector3 forward => (rotation * Vector3.UnitZ).Normalized();

	public Vector3 up => (rotation * Vector3.UnitY).Normalized();

	public Vector3 right => (rotation * Vector3.UnitX).Normalized();

	/// <summary>Local-to-parent matrix.</summary>
	public OpenTK.Mathematics.Matrix4 LocalMatrix =>
		OpenTK.Mathematics.Matrix4.CreateScale(m_LocalScale) * OpenTK.Mathematics.Matrix4.CreateFromQuaternion(m_LocalRotation) * OpenTK.Mathematics.Matrix4.CreateTranslation(m_LocalPosition);

	/// <summary>Local-to-world matrix.</summary>
	public OpenTK.Mathematics.Matrix4 LocalToWorld => parentTransform == null ? LocalMatrix : LocalMatrix * parentTransform.LocalToWorld;

	public OpenTK.Mathematics.Matrix4 WorldToLocal => LocalToWorld.Inverted();

	public Transform parent
	{
		get => parentTransform;
		set => SetParent(value, true);
	}

	public IReadOnlyList<Transform> Children => children;

	public int childCount => children.Count;

	public Transform root => parentTransform == null ? this : parentTransform.root;

	#endregion

	#region PrivateFields

	[SerializeField] private Vector3 m_LocalPosition = Vector3.Zero;
	[SerializeField] private Quaternion m_LocalRotation = Quaternion.Identity;
	[SerializeField] private Vector3 m_LocalScale = Vector3.One;

	private Transform parentTransform;
	private readonly List<Transform> children = new List<Transform>();
	private readonly Matrix4 modelMatrix = new Matrix4("modelMatrix");

	#endregion

	#region PublicMethods

	/// <summary>Uploads the world matrix as "modelMatrix" (computed at draw time, so edit mode needs no update).</summary>
	public void SetModelMatrix(ShaderProgram program)
	{
		modelMatrix.Matrix = LocalToWorld;
		modelMatrix.Set(program);
	}

	/// <summary>Re-parents the transform, keeping its world position, rotation and (approximately) scale.</summary>
	public void SetParent(Transform newParent)
	{
		SetParent(newParent, true);
	}

	public void SetParent(Transform newParent, bool worldPositionStays)
	{
		if (newParent == parentTransform)
		{
			return;
		}

		if (newParent != null && (newParent == this || newParent.IsChildOf(this)))
		{
			Debug.LogError($"Cannot parent '{gameObject?.Name}' to its own descendant '{newParent.gameObject?.Name}'.");
			return;
		}

		Vector3 worldPosition = position;
		Quaternion worldRotation = rotation;
		Vector3 worldScale = lossyScale;

		parentTransform?.children.Remove(this);
		parentTransform = newParent;
		newParent?.children.Add(this);

		if (worldPositionStays)
		{
			position = worldPosition;
			rotation = worldRotation;
			if (newParent == null)
			{
				m_LocalScale = worldScale;
			}
			else
			{
				Vector3 parentScale = newParent.lossyScale;
				m_LocalScale = new Vector3(
					SafeDivide(worldScale.X, parentScale.X),
					SafeDivide(worldScale.Y, parentScale.Y),
					SafeDivide(worldScale.Z, parentScale.Z));
			}
		}

		Scene scene = gameObject?.scene;
		if (scene != null && gameObject != null && !gameObject.destroyed)
		{
			scene.OnHierarchyActiveChanged(gameObject);
		}
	}

	public bool IsChildOf(Transform other)
	{
		for (Transform t = parentTransform; t != null; t = t.parentTransform)
		{
			if (t == other)
			{
				return true;
			}
		}

		return false;
	}

	public Transform GetChild(int index)
	{
		return children[index];
	}

	public int GetSiblingIndex()
	{
		return parentTransform != null ? parentTransform.children.IndexOf(this) : -1;
	}

	/// <summary>Moves this transform within its parent's children (root order is the scene's object order).</summary>
	public void SetSiblingIndex(int index)
	{
		if (parentTransform == null)
		{
			return;
		}

		List<Transform> siblings = parentTransform.children;
		siblings.Remove(this);
		siblings.Insert(Math.Clamp(index, 0, siblings.Count), this);
	}

	public Vector3 TransformPoint(Vector3 point)
	{
		return Vector3.TransformPosition(point, LocalToWorld);
	}

	public Vector3 InverseTransformPoint(Vector3 point)
	{
		return Vector3.TransformPosition(point, WorldToLocal);
	}

	public Vector3 TransformDirection(Vector3 direction)
	{
		return rotation * direction;
	}

	public Vector3 InverseTransformDirection(Vector3 direction)
	{
		return rotation.Inverted() * direction;
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

	public Vector3 localPosition
	{
		get => m_LocalPosition;
		set => m_LocalPosition = value;
	}

	public Vector3 position
	{
		get => parentTransform == null ? m_LocalPosition : parentTransform.TransformPoint(m_LocalPosition);
		set => m_LocalPosition = parentTransform == null ? value : parentTransform.InverseTransformPoint(value);
	}

	#endregion

	#region Rotations

	public Quaternion localRotation
	{
		get => m_LocalRotation;
		set => m_LocalRotation = value;
	}

	public Quaternion rotation
	{
		get => parentTransform == null ? m_LocalRotation : parentTransform.rotation * m_LocalRotation;
		set => m_LocalRotation = parentTransform == null ? value : parentTransform.rotation.Inverted() * value;
	}

	#endregion

	#region Scale

	public Vector3 localScale
	{
		get => m_LocalScale;
		set => m_LocalScale = value;
	}

	/// <summary>Approximate world scale (product of the local scales along the parent chain).</summary>
	public Vector3 lossyScale => parentTransform == null ? m_LocalScale : parentTransform.lossyScale * m_LocalScale;

	/// <summary>Getter: world scale (<see cref="lossyScale" />); setter: local scale.</summary>
	public Vector3 scale
	{
		get => lossyScale;
		set => m_LocalScale = value;
	}

	#endregion

	#region PrivateMethods

	private static float SafeDivide(float value, float divisor)
	{
		return Math.Abs(divisor) > 1e-8f ? value / divisor : value;
	}

	#endregion
}
