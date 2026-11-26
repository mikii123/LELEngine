using System;
using LELEngine.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     An axis-aligned box (Unity's Bounds): its centre and half size (<see cref="extents" />). A renderer's in world
	///     space: <see cref="MeshRenderer.bounds" />.
	/// </summary>
	[Serializable]
	public struct Bounds : IEquatable<Bounds>
	{
		[SerializeField] private Vector3 m_Center;
		[SerializeField] private Vector3 m_Extents;

		/// <summary>A box around <paramref name="center" />, <paramref name="size" /> across.</summary>
		public Bounds(Vector3 center, Vector3 size)
		{
			m_Center = center;
			m_Extents = size * 0.5f;
		}

		public Vector3 center
		{
			get => m_Center;
			set => m_Center = value;
		}

		/// <summary>Half the size.</summary>
		public Vector3 extents
		{
			get => m_Extents;
			set => m_Extents = value;
		}

		public Vector3 size
		{
			get => m_Extents * 2f;
			set => m_Extents = value * 0.5f;
		}

		public Vector3 min
		{
			get => m_Center - m_Extents;
			set => SetMinMax(value, max);
		}

		public Vector3 max
		{
			get => m_Center + m_Extents;
			set => SetMinMax(min, value);
		}

		public void SetMinMax(Vector3 min, Vector3 max)
		{
			m_Extents = (max - min) * 0.5f;
			m_Center = min + m_Extents;
		}

		/// <summary>Grows the box to take in <paramref name="point" />.</summary>
		public void Encapsulate(Vector3 point)
		{
			SetMinMax(Vector3.ComponentMin(min, point), Vector3.ComponentMax(max, point));
		}

		/// <summary>Grows the box to take in <paramref name="bounds" />.</summary>
		public void Encapsulate(Bounds bounds)
		{
			SetMinMax(Vector3.ComponentMin(min, bounds.min), Vector3.ComponentMax(max, bounds.max));
		}

		/// <summary>Grows the size by <paramref name="amount" /> along every axis.</summary>
		public void Expand(float amount)
		{
			m_Extents += new Vector3(amount * 0.5f);
		}

		/// <summary>The point is inside or on the box.</summary>
		public bool Contains(Vector3 point)
		{
			return MathF.Abs(point.X - m_Center.X) <= m_Extents.X && MathF.Abs(point.Y - m_Center.Y) <= m_Extents.Y && MathF.Abs(point.Z - m_Center.Z) <= m_Extents.Z;
		}

		/// <summary>The boxes overlap or touch.</summary>
		public bool Intersects(Bounds other)
		{
			return MathF.Abs(m_Center.X - other.m_Center.X) <= m_Extents.X + other.m_Extents.X &&
			       MathF.Abs(m_Center.Y - other.m_Center.Y) <= m_Extents.Y + other.m_Extents.Y &&
			       MathF.Abs(m_Center.Z - other.m_Center.Z) <= m_Extents.Z + other.m_Extents.Z;
		}

		public bool Equals(Bounds other) => m_Center == other.m_Center && m_Extents == other.m_Extents;

		public override bool Equals(object obj) => obj is Bounds other && Equals(other);

		public override int GetHashCode() => HashCode.Combine(m_Center, m_Extents);

		public static bool operator ==(Bounds a, Bounds b) => a.Equals(b);

		public static bool operator !=(Bounds a, Bounds b) => !a.Equals(b);

		public override string ToString() => $"Center: {m_Center}, Extents: {m_Extents}";
	}
}
