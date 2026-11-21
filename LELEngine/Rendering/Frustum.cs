using System;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A view's frustum as six world-space planes (inside: dot(normal, point) + d >= 0), from the view's row-vector
	///     view-projection matrix: a camera's, a shadow map tile's. The box tests are the ones Engine/InstanceCull.shader
	///     runs for the GPU-driven instances (the box's reach along each plane's normal), so what the CPU leaves out and
	///     what the GPU leaves out agree. Conservative: a box near a corner of the frustum may pass while no pixel of it
	///     shows; nothing visible is ever left out. A matrix or a box with a NaN in it passes.
	/// </summary>
	public readonly struct Frustum
	{
		#region PublicFields

		public readonly Vector4 Left;
		public readonly Vector4 Right;
		public readonly Vector4 Bottom;
		public readonly Vector4 Top;
		public readonly Vector4 Near;
		public readonly Vector4 Far;

		#endregion

		#region Constructors

		public Frustum(Matrix4 viewProjection)
		{
			Vector4 c0 = viewProjection.Column0;
			Vector4 c1 = viewProjection.Column1;
			Vector4 c2 = viewProjection.Column2;
			Vector4 c3 = viewProjection.Column3;
			Left = Normalize(c3 + c0);
			Right = Normalize(c3 - c0);
			Bottom = Normalize(c3 + c1);
			Top = Normalize(c3 - c1);
			Near = Normalize(c3 + c2);
			Far = Normalize(c3 - c2);
		}

		#endregion

		#region PublicMethods

		/// <summary>A world-space box (its corners) reaches into the frustum.</summary>
		public bool Intersects(Vector3 min, Vector3 max)
		{
			return IntersectsBox((min + max) * 0.5f, (max - min) * 0.5f);
		}

		/// <summary>A box of an object's space (a mesh's bounds) under the object's world matrix reaches into the frustum.</summary>
		public bool Intersects(in Matrix4 localToWorld, Vector3 localMin, Vector3 localMax)
		{
			WorldBox(localToWorld, localMin, localMax, out Vector3 center, out Vector3 extent);
			return IntersectsBox(center, extent);
		}

		/// <summary>A world-space box (its centre and half size) reaches into the frustum.</summary>
		public bool IntersectsBox(Vector3 center, Vector3 extent)
		{
			return !(Outside(Left, center, extent) || Outside(Right, center, extent) || Outside(Bottom, center, extent) || Outside(Top, center, extent) || Outside(Near, center, extent)
				|| Outside(Far, center, extent));
		}

		/// <summary>
		///     The world-space box around a box of an object's space: its centre and half size (the object's axes are the
		///     matrix's rows; a rotated box gets the axis-aligned box around it).
		/// </summary>
		public static void WorldBox(in Matrix4 localToWorld, Vector3 localMin, Vector3 localMax, out Vector3 center, out Vector3 extent)
		{
			Vector3 localCenter = (localMin + localMax) * 0.5f;
			Vector3 localExtent = (localMax - localMin) * 0.5f;
			center = new Vector3(
				localCenter.X * localToWorld.M11 + localCenter.Y * localToWorld.M21 + localCenter.Z * localToWorld.M31 + localToWorld.M41,
				localCenter.X * localToWorld.M12 + localCenter.Y * localToWorld.M22 + localCenter.Z * localToWorld.M32 + localToWorld.M42,
				localCenter.X * localToWorld.M13 + localCenter.Y * localToWorld.M23 + localCenter.Z * localToWorld.M33 + localToWorld.M43);
			extent = new Vector3(
				MathF.Abs(localToWorld.M11) * localExtent.X + MathF.Abs(localToWorld.M21) * localExtent.Y + MathF.Abs(localToWorld.M31) * localExtent.Z,
				MathF.Abs(localToWorld.M12) * localExtent.X + MathF.Abs(localToWorld.M22) * localExtent.Y + MathF.Abs(localToWorld.M32) * localExtent.Z,
				MathF.Abs(localToWorld.M13) * localExtent.X + MathF.Abs(localToWorld.M23) * localExtent.Y + MathF.Abs(localToWorld.M33) * localExtent.Z);
		}

		#endregion

		#region PrivateMethods

		private static Vector4 Normalize(Vector4 plane)
		{
			float length = plane.Xyz.Length;
			return length > 0f ? plane / length : plane;
		}

		// The whole box is behind the plane (a NaN compares false: not outside).
		private static bool Outside(Vector4 plane, Vector3 center, Vector3 extent)
		{
			return plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W + MathF.Abs(plane.X) * extent.X + MathF.Abs(plane.Y) * extent.Y + MathF.Abs(plane.Z) * extent.Z < 0f;
		}

		#endregion
	}
}
