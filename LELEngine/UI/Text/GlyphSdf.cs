using System;
using System.Collections.Generic;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Rasterizes a glyph outline into a signed distance field: every pixel stores its distance to the nearest
	///     contour edge (positive inside), encoded in 8 bits around 0.5 with a fixed spread in pixels. The fragment
	///     shader turns the distance into anti-aliased coverage at any scale, so one atlas serves all font sizes.
	/// </summary>
	internal static class GlyphSdf
	{
		#region PrivateFields

		private struct Segment
		{
			public float ax, ay, bx, by;
			public float minX, minY, maxX, maxY;
		}

		[ThreadStatic] private static List<Segment> segmentBuffer;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Renders the contours (font units) scaled by <paramref name="scale" /> into a bitmap of the given size.
		///     <paramref name="originX" />/<paramref name="originY" /> is the glyph-space position of the bitmap's
		///     top-left pixel (y up in glyph space; bitmap rows go down).
		/// </summary>
		public static byte[] Render(List<GlyphContour> contours, float scale, float originX, float originY, int width, int height, float spread)
		{
			List<Segment> segments = segmentBuffer ??= new List<Segment>(256);
			segments.Clear();
			Flatten(contours, scale, segments);

			var pixels = new byte[width * height];
			if (segments.Count == 0)
			{
				return pixels;
			}

			float invRange = 1f / (2f * spread);
			for (int py = 0; py < height; py++)
			{
				// Pixel center in scaled glyph space (y up).
				float sy = originY - (py + 0.5f);
				int row = py * width;
				for (int px = 0; px < width; px++)
				{
					float sx = originX + px + 0.5f;
					float best = spread * spread;
					int winding = 0;
					for (int i = 0; i < segments.Count; i++)
					{
						Segment s = segments[i];
						// Winding: count crossings of the horizontal ray to +x.
						if ((s.ay <= sy) != (s.by <= sy))
						{
							float t = (sy - s.ay) / (s.by - s.ay);
							float ix = s.ax + t * (s.bx - s.ax);
							if (ix > sx)
							{
								winding += s.by > s.ay ? 1 : -1;
							}
						}

						// Quick reject on the segment's bounding box.
						float dxBox = sx < s.minX ? s.minX - sx : sx > s.maxX ? sx - s.maxX : 0f;
						float dyBox = sy < s.minY ? s.minY - sy : sy > s.maxY ? sy - s.maxY : 0f;
						if (dxBox * dxBox + dyBox * dyBox >= best)
						{
							continue;
						}

						float d = DistanceSquared(sx, sy, s);
						if (d < best)
						{
							best = d;
						}
					}

					float distance = (float)Math.Sqrt(best);
					if (winding == 0)
					{
						distance = -distance;
					}

					float value = 0.5f + distance * invRange;
					pixels[row + px] = (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);
				}
			}

			return pixels;
		}

		#endregion

		#region PrivateMethods

		/// <summary>Turns the quadratic contours into line segments in scaled glyph space.</summary>
		private static void Flatten(List<GlyphContour> contours, float scale, List<Segment> segments)
		{
			foreach (GlyphContour contour in contours)
			{
				List<GlyphPoint> points = contour.points;
				int count = points.Count;
				if (count < 2)
				{
					continue;
				}

				// Find a starting on-curve point (insert the midpoint when all are off-curve).
				int startIndex = -1;
				for (int i = 0; i < count; i++)
				{
					if (points[i].onCurve)
					{
						startIndex = i;
						break;
					}
				}

				float startX, startY;
				if (startIndex < 0)
				{
					startX = (points[0].x + points[1].x) * 0.5f * scale;
					startY = (points[0].y + points[1].y) * 0.5f * scale;
				}
				else
				{
					startX = points[startIndex].x * scale;
					startY = points[startIndex].y * scale;
				}

				float currentX = startX, currentY = startY;
				bool haveControl = false;
				float controlX = 0f, controlY = 0f;
				int begin = startIndex < 0 ? 1 : startIndex + 1;
				for (int step = 0; step < count; step++)
				{
					GlyphPoint p = points[(begin + step) % count];
					float px = p.x * scale, py = p.y * scale;
					if (p.onCurve)
					{
						if (haveControl)
						{
							AddQuadratic(segments, currentX, currentY, controlX, controlY, px, py);
							haveControl = false;
						}
						else
						{
							AddSegment(segments, currentX, currentY, px, py);
						}

						currentX = px;
						currentY = py;
					}
					else if (haveControl)
					{
						// Two consecutive off-curve points imply an on-curve midpoint between them.
						float midX = (controlX + px) * 0.5f, midY = (controlY + py) * 0.5f;
						AddQuadratic(segments, currentX, currentY, controlX, controlY, midX, midY);
						currentX = midX;
						currentY = midY;
						controlX = px;
						controlY = py;
					}
					else
					{
						controlX = px;
						controlY = py;
						haveControl = true;
					}
				}

				// Close the contour back to the start.
				if (haveControl)
				{
					AddQuadratic(segments, currentX, currentY, controlX, controlY, startX, startY);
				}
				else
				{
					AddSegment(segments, currentX, currentY, startX, startY);
				}
			}
		}

		private static void AddQuadratic(List<Segment> segments, float x0, float y0, float cx, float cy, float x1, float y1)
		{
			float dx = Math.Abs(x0 - 2f * cx + x1);
			float dy = Math.Abs(y0 - 2f * cy + y1);
			int steps = Math.Clamp((int)Math.Ceiling(Math.Sqrt((dx + dy) * 2f)), 1, 24);
			float px = x0, py = y0;
			for (int i = 1; i <= steps; i++)
			{
				float t = i / (float)steps;
				float u = 1f - t;
				float x = u * u * x0 + 2f * u * t * cx + t * t * x1;
				float y = u * u * y0 + 2f * u * t * cy + t * t * y1;
				AddSegment(segments, px, py, x, y);
				px = x;
				py = y;
			}
		}

		private static void AddSegment(List<Segment> segments, float ax, float ay, float bx, float by)
		{
			if (ax == bx && ay == by)
			{
				return;
			}

			segments.Add(new Segment
			{
				ax = ax,
				ay = ay,
				bx = bx,
				by = by,
				minX = Math.Min(ax, bx),
				minY = Math.Min(ay, by),
				maxX = Math.Max(ax, bx),
				maxY = Math.Max(ay, by)
			});
		}

		private static float DistanceSquared(float px, float py, in Segment s)
		{
			float dx = s.bx - s.ax;
			float dy = s.by - s.ay;
			float lengthSquared = dx * dx + dy * dy;
			float t = lengthSquared > 0f ? ((px - s.ax) * dx + (py - s.ay) * dy) / lengthSquared : 0f;
			t = Math.Clamp(t, 0f, 1f);
			float cx = s.ax + t * dx - px;
			float cy = s.ay + t * dy - py;
			return cx * cx + cy * cy;
		}

		#endregion
	}
}
