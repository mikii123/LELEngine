using System;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Impostors
{
	/// <summary>
	///     The geometry of octahedral impostors, as the shaders have it in Engine/Impostor.glsl (the two must stay
	///     equal: the bake lays the views out with this code, the shader finds them with that one). An impostor is an
	///     object captured from the directions of a grid of frames x frames views: the grid is the octahedral map of
	///     the directions, the whole sphere (+Y at the centre of the map, the horizon on the diamond |x| + |y| = 1, -Y at
	///     its four corners) or the upper half alone (+Y at the centre, the horizon on the map's border). Every view is
	///     orthographic and looks at the centre of the object's bounding sphere from a radius away.
	/// </summary>
	public static class ImpostorMath
	{
		#region PublicFields

		public const int MinFrames = 2;
		public const int MaxFrames = 32;

		#endregion

		#region PrivateFields

		// A little off the Y axis, so never along a view's direction (the poles are views).
		private static readonly Vector3 AxisHelper = new Vector3(0.0131f, 1f, 0.0273f);

		#endregion

		#region PublicMethods

		/// <summary>The unit direction of a point of the whole-sphere map (<paramref name="e" /> in -1..1 on both axes).</summary>
		public static Vector3 OctDecode(Vector2 e)
		{
			var n = new Vector3(e.X, 1f - Math.Abs(e.X) - Math.Abs(e.Y), e.Y);
			float t = Math.Max(-n.Y, 0f);
			n.X += n.X >= 0f ? -t : t;
			n.Z += n.Z >= 0f ? -t : t;
			return n.Normalized();
		}

		/// <summary>The point of the whole-sphere map of a direction.</summary>
		public static Vector2 OctEncode(Vector3 n)
		{
			n /= Math.Abs(n.X) + Math.Abs(n.Y) + Math.Abs(n.Z);
			var e = new Vector2(n.X, n.Z);
			if (n.Y < 0f)
			{
				e = new Vector2((1f - Math.Abs(n.Z)) * (n.X >= 0f ? 1f : -1f), (1f - Math.Abs(n.X)) * (n.Z >= 0f ? 1f : -1f));
			}

			return e;
		}

		/// <summary>The unit direction of a point of the upper-half map.</summary>
		public static Vector3 HemiOctDecode(Vector2 e)
		{
			var t = new Vector2(e.X + e.Y, e.X - e.Y) * 0.5f;
			return new Vector3(t.X, 1f - Math.Abs(t.X) - Math.Abs(t.Y), t.Y).Normalized();
		}

		/// <summary>The point of the upper-half map of a direction; one below the horizon is taken on it.</summary>
		public static Vector2 HemiOctEncode(Vector3 n)
		{
			n.Y = Math.Max(n.Y, 0f);
			float sum = Math.Max(Math.Abs(n.X) + n.Y + Math.Abs(n.Z), 1e-6f);
			var t = new Vector2(n.X, n.Z) / sum;
			return new Vector2(t.X + t.Y, t.X - t.Y);
		}

		/// <summary>The point of the map of a direction (from the sphere's centre towards the eye, object space).</summary>
		public static Vector2 Encode(Vector3 direction, bool hemisphere)
		{
			return hemisphere ? HemiOctEncode(direction) : OctEncode(direction);
		}

		/// <summary>
		///     The direction a view is captured from: from the sphere's centre towards its camera (object space), for the
		///     view at <paramref name="column" />, <paramref name="row" /> of a grid of <paramref name="frames" /> a side.
		/// </summary>
		public static Vector3 FrameDirection(int column, int row, int frames, bool hemisphere)
		{
			float step = 2f / (frames - 1);
			var e = new Vector2(column * step - 1f, row * step - 1f);
			return hemisphere ? HemiOctDecode(e) : OctDecode(e);
		}

		/// <summary>The axes of a view's image for its direction: right along its u, up along its v.</summary>
		public static void FrameAxes(Vector3 direction, out Vector3 right, out Vector3 up)
		{
			right = Vector3.Cross(AxisHelper, direction).Normalized();
			up = Vector3.Cross(direction, right);
		}

		/// <summary>
		///     The view matrix of a view (row vectors, as the engine's): its camera a radius from the centre along
		///     <paramref name="direction" />, looking back at it, its axes those of <see cref="FrameAxes" />.
		/// </summary>
		public static Matrix4 FrameView(Vector3 centre, float radius, Vector3 direction)
		{
			FrameAxes(direction, out Vector3 right, out Vector3 up);
			Vector3 eye = centre + direction * radius;
			return new Matrix4(
				right.X, up.X, direction.X, 0f,
				right.Y, up.Y, direction.Y, 0f,
				right.Z, up.Z, direction.Z, 0f,
				-Vector3.Dot(eye, right), -Vector3.Dot(eye, up), -Vector3.Dot(eye, direction), 1f);
		}

		/// <summary>
		///     The projection of every view: orthographic, the sphere's width across, from the plane in front of the sphere
		///     (depth 0) to the one behind it (depth 1).
		/// </summary>
		public static Matrix4 FrameProjection(float radius)
		{
			return Matrix4.CreateOrthographicOffCenter(-radius, radius, -radius, radius, 0f, 2f * radius);
		}

		/// <summary>
		///     The three views around a point of the map and their weights (they sum to 1; each fades to 0 where its view
		///     leaves the three). Every cell of the grid is cut along its shorter diagonal on the sphere, so the three are
		///     the nearest views.
		/// </summary>
		public static void NearestFrames(Vector2 e, int frames, bool hemisphere, out Vector2i frameA, out Vector2i frameB, out Vector2i frameC, out Vector3 weights)
		{
			float last = frames - 1;
			float gridX = Math.Clamp(e.X * 0.5f + 0.5f, 0f, 1f) * last;
			float gridY = Math.Clamp(e.Y * 0.5f + 0.5f, 0f, 1f) * last;
			int cellX = Math.Min((int)MathF.Floor(gridX), frames - 2);
			int cellY = Math.Min((int)MathF.Floor(gridY), frames - 2);
			float fx = gridX - cellX;
			float fy = gridY - cellY;
			float centreX = (cellX + 0.5f) / last * 2f - 1f;
			float centreY = (cellY + 0.5f) / last * 2f - 1f;
			if ((centreX * centreY > 0f) != hemisphere)
			{
				// Cut from (1, 0) to (0, 1).
				frameA = new Vector2i(cellX + 1, cellY);
				frameB = new Vector2i(cellX, cellY + 1);
				if (fx + fy < 1f)
				{
					frameC = new Vector2i(cellX, cellY);
					weights = new Vector3(fx, fy, 1f - fx - fy);
				}
				else
				{
					frameC = new Vector2i(cellX + 1, cellY + 1);
					weights = new Vector3(1f - fy, 1f - fx, fx + fy - 1f);
				}
			}
			else
			{
				// Cut from (0, 0) to (1, 1).
				frameA = new Vector2i(cellX, cellY);
				frameB = new Vector2i(cellX + 1, cellY + 1);
				if (fx > fy)
				{
					frameC = new Vector2i(cellX + 1, cellY);
					weights = new Vector3(1f - fx, fy, fx - fy);
				}
				else
				{
					frameC = new Vector2i(cellX, cellY + 1);
					weights = new Vector3(1f - fy, fx, fy - fx);
				}
			}
		}

		#endregion
	}
}
