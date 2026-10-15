using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation.Builder
{
	/// <summary>Grid directions used by the voxel pipeline: 0 -x, 1 +z, 2 +x, 3 -z.</summary>
	internal static class Dir
	{
		public static readonly int[] X = { -1, 0, 1, 0 };
		public static readonly int[] Z = { 0, 1, 0, -1 };
	}

	/// <summary>
	///     Column-sparse voxel grid: per cell a linked list of solid spans (min / max height in cells) with the
	///     area of the triangle that produced the top of the span. Triangles rasterize into it; filters then decide
	///     which span tops an agent can stand on.
	/// </summary>
	internal sealed class Heightfield
	{
		#region PublicFields

		public const int MaxHeight = 0xffff;
		public const int NullSpan = -1;

		public readonly int Width;
		public readonly int Height;
		public readonly Vector3 BoundsMin;
		public readonly Vector3 BoundsMax;
		public readonly float CellSize;
		public readonly float CellHeight;

		/// <summary>First span index per cell, or <see cref="NullSpan" />.</summary>
		public readonly int[] Cells;

		public int SpanCount => spanMin.Count;

		#endregion

		#region PrivateFields

		// Span pool as parallel lists (indices never move; freed spans go to a free list).
		private readonly List<int> spanMin = new List<int>();
		private readonly List<int> spanMax = new List<int>();
		private readonly List<byte> spanArea = new List<byte>();
		private readonly List<int> spanNext = new List<int>();
		private int freeList = NullSpan;

		#endregion

		#region Constructors

		public Heightfield(int width, int height, Vector3 boundsMin, Vector3 boundsMax, float cellSize, float cellHeight)
		{
			Width = width;
			Height = height;
			BoundsMin = boundsMin;
			BoundsMax = boundsMax;
			CellSize = cellSize;
			CellHeight = cellHeight;
			Cells = new int[width * height];
			Array.Fill(Cells, NullSpan);
		}

		#endregion

		#region PublicMethods

		public int SpanMin(int span) => spanMin[span];
		public int SpanMax(int span) => spanMax[span];
		public byte SpanAreaOf(int span) => spanArea[span];
		public int SpanNext(int span) => spanNext[span];

		public void SetSpanArea(int span, byte area)
		{
			spanArea[span] = area;
		}

		/// <summary>Adds a span to a column, merging it with spans it overlaps (Recast's addSpan).</summary>
		public void AddSpan(int x, int z, int min, int max, byte area, int flagMergeThreshold)
		{
			int column = x + z * Width;
			int span = AllocSpan(min, max, area);
			int previous = NullSpan;
			int current = Cells[column];

			while (current != NullSpan)
			{
				if (spanMin[current] > spanMax[span])
				{
					// Current span is further than the new span: insert before it.
					break;
				}

				if (spanMax[current] < spanMin[span])
				{
					// Current span is completely before the new span: keep going.
					previous = current;
					current = spanNext[current];
					continue;
				}

				// Merge: the spans overlap.
				if (spanMin[current] < spanMin[span])
				{
					spanMin[span] = spanMin[current];
				}

				if (spanMax[current] > spanMax[span])
				{
					spanMax[span] = spanMax[current];
				}

				// Merge flags when the tops are close.
				if (Math.Abs(spanMax[span] - spanMax[current]) <= flagMergeThreshold)
				{
					spanArea[span] = Math.Max(spanArea[span], spanArea[current]);
				}

				// Remove the current span.
				int next = spanNext[current];
				FreeSpan(current);
				if (previous != NullSpan)
				{
					spanNext[previous] = next;
				}
				else
				{
					Cells[column] = next;
				}

				current = next;
			}

			// Insert the new span.
			if (previous != NullSpan)
			{
				spanNext[span] = spanNext[previous];
				spanNext[previous] = span;
			}
			else
			{
				spanNext[span] = Cells[column];
				Cells[column] = span;
			}
		}

		#endregion

		#region PrivateMethods

		private int AllocSpan(int min, int max, byte area)
		{
			if (freeList != NullSpan)
			{
				int span = freeList;
				freeList = spanNext[span];
				spanMin[span] = min;
				spanMax[span] = max;
				spanArea[span] = area;
				spanNext[span] = NullSpan;
				return span;
			}

			spanMin.Add(min);
			spanMax.Add(max);
			spanArea.Add(area);
			spanNext.Add(NullSpan);
			return spanMin.Count - 1;
		}

		private void FreeSpan(int span)
		{
			spanNext[span] = freeList;
			freeList = span;
		}

		#endregion
	}

	/// <summary>Rasterizes triangles into a <see cref="Heightfield" /> (Recast's rasterizeTri).</summary>
	internal static class Rasterizer
	{
		#region PublicMethods

		/// <summary>
		///     Rasterizes one triangle. <paramref name="area" /> is the area of its top; non-walkable triangles still
		///     become solid spans (they block the space below ceilings and walls).
		/// </summary>
		public static void RasterizeTriangle(Heightfield hf, Vector3 v0, Vector3 v1, Vector3 v2, byte area, int flagMergeThreshold, float[] scratch)
		{
			Vector3 bmin = hf.BoundsMin;
			Vector3 bmax = hf.BoundsMax;
			float cs = hf.CellSize;
			float ics = 1f / cs;
			float ich = 1f / hf.CellHeight;
			int w = hf.Width;
			int h = hf.Height;
			float by = bmax.Y - bmin.Y;

			Vector3 tmin = Vector3.ComponentMin(Vector3.ComponentMin(v0, v1), v2);
			Vector3 tmax = Vector3.ComponentMax(Vector3.ComponentMax(v0, v1), v2);
			if (tmin.X > bmax.X || tmax.X < bmin.X || tmin.Y > bmax.Y || tmax.Y < bmin.Y || tmin.Z > bmax.Z || tmax.Z < bmin.Z)
			{
				return;
			}

			int z0 = (int)((tmin.Z - bmin.Z) * ics);
			int z1 = (int)((tmax.Z - bmin.Z) * ics);
			z0 = Math.Clamp(z0, -1, h - 1);
			z1 = Math.Clamp(z1, 0, h - 1);

			// Polygon buffers: 7 vertices of 3 floats each; scratch holds 4 of them (in, inrow, p1, p2).
			int inPoly = 0, inRow = 21, p1 = 42, p2 = 63;
			scratch[inPoly] = v0.X; scratch[inPoly + 1] = v0.Y; scratch[inPoly + 2] = v0.Z;
			scratch[inPoly + 3] = v1.X; scratch[inPoly + 4] = v1.Y; scratch[inPoly + 5] = v1.Z;
			scratch[inPoly + 6] = v2.X; scratch[inPoly + 7] = v2.Y; scratch[inPoly + 8] = v2.Z;
			int nvRow, nvIn = 3;

			for (int z = z0; z <= z1; z++)
			{
				float cz = bmin.Z + z * cs;
				DividePoly(scratch, inPoly, nvIn, inRow, out nvRow, p1, out nvIn, cz + cs, 2);
				(inPoly, p1) = (p1, inPoly);
				if (nvRow < 3 || z < 0)
				{
					continue;
				}

				float minX = scratch[inRow];
				float maxX = scratch[inRow];
				for (int i = 1; i < nvRow; i++)
				{
					float x = scratch[inRow + i * 3];
					minX = Math.Min(minX, x);
					maxX = Math.Max(maxX, x);
				}

				int x0 = (int)((minX - bmin.X) * ics);
				int x1 = (int)((maxX - bmin.X) * ics);
				if (x1 < 0 || x0 >= w)
				{
					continue;
				}

				x0 = Math.Clamp(x0, -1, w - 1);
				x1 = Math.Clamp(x1, 0, w - 1);

				int nv, nv2 = nvRow;
				for (int x = x0; x <= x1; x++)
				{
					float cx = bmin.X + x * cs;
					DividePoly(scratch, inRow, nv2, p1, out nv, p2, out nv2, cx + cs, 0);
					(inRow, p2) = (p2, inRow);
					if (nv < 3 || x < 0)
					{
						continue;
					}

					float smin = scratch[p1 + 1];
					float smax = smin;
					for (int i = 1; i < nv; i++)
					{
						float y = scratch[p1 + i * 3 + 1];
						smin = Math.Min(smin, y);
						smax = Math.Max(smax, y);
					}

					smin -= bmin.Y;
					smax -= bmin.Y;
					if (smax < 0f || smin > by)
					{
						continue;
					}

					if (smin < 0f) smin = 0f;
					if (smax > by) smax = by;

					int ismin = Math.Clamp((int)Math.Floor(smin * ich), 0, Heightfield.MaxHeight);
					int ismax = Math.Clamp((int)Math.Ceiling(smax * ich), ismin + 1, Heightfield.MaxHeight);
					hf.AddSpan(x, z, ismin, ismax, area, flagMergeThreshold);
				}
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>Splits a polygon by the plane axis = value into the part below (out1) and above (out2).</summary>
		private static void DividePoly(float[] buf, int inOffset, int nin, int out1, out int nout1, int out2, out int nout2, float value, int axis)
		{
			Span<float> d = stackalloc float[12];
			for (int i = 0; i < nin; i++)
			{
				d[i] = value - buf[inOffset + i * 3 + axis];
			}

			int m = 0, n = 0;
			for (int i = 0, j = nin - 1; i < nin; j = i, i++)
			{
				bool ina = d[j] >= 0f;
				bool inb = d[i] >= 0f;
				int vi = inOffset + i * 3;
				int vj = inOffset + j * 3;
				if (ina != inb)
				{
					float s = d[j] / (d[j] - d[i]);
					float px = buf[vj] + (buf[vi] - buf[vj]) * s;
					float py = buf[vj + 1] + (buf[vi + 1] - buf[vj + 1]) * s;
					float pz = buf[vj + 2] + (buf[vi + 2] - buf[vj + 2]) * s;
					buf[out1 + m * 3] = px; buf[out1 + m * 3 + 1] = py; buf[out1 + m * 3 + 2] = pz;
					buf[out2 + n * 3] = px; buf[out2 + n * 3 + 1] = py; buf[out2 + n * 3 + 2] = pz;
					m++;
					n++;
					if (d[i] > 0f)
					{
						buf[out1 + m * 3] = buf[vi]; buf[out1 + m * 3 + 1] = buf[vi + 1]; buf[out1 + m * 3 + 2] = buf[vi + 2];
						m++;
					}
					else if (d[i] < 0f)
					{
						buf[out2 + n * 3] = buf[vi]; buf[out2 + n * 3 + 1] = buf[vi + 1]; buf[out2 + n * 3 + 2] = buf[vi + 2];
						n++;
					}
				}
				else
				{
					if (d[i] >= 0f)
					{
						buf[out1 + m * 3] = buf[vi]; buf[out1 + m * 3 + 1] = buf[vi + 1]; buf[out1 + m * 3 + 2] = buf[vi + 2];
						m++;
						if (d[i] != 0f)
						{
							continue;
						}
					}

					buf[out2 + n * 3] = buf[vi]; buf[out2 + n * 3 + 1] = buf[vi + 1]; buf[out2 + n * 3 + 2] = buf[vi + 2];
					n++;
				}
			}

			nout1 = m;
			nout2 = n;
		}

		#endregion
	}

	/// <summary>Span filters that decide where an agent can stand (Recast's filter functions).</summary>
	internal static class HeightfieldFilters
	{
		#region PublicMethods

		/// <summary>A non-walkable span right on top of a walkable one, within the climb height, becomes walkable (stairs, curbs).</summary>
		public static void FilterLowHangingWalkableObstacles(Heightfield hf, int walkableClimb)
		{
			for (int column = 0; column < hf.Cells.Length; column++)
			{
				int previous = Heightfield.NullSpan;
				bool previousWalkable = false;
				byte previousArea = NavMeshArea.Null;
				for (int span = hf.Cells[column]; span != Heightfield.NullSpan; span = hf.SpanNext(span))
				{
					bool walkable = hf.SpanAreaOf(span) != NavMeshArea.Null;
					if (!walkable && previousWalkable && previous != Heightfield.NullSpan && Math.Abs(hf.SpanMax(span) - hf.SpanMax(previous)) <= walkableClimb)
					{
						hf.SetSpanArea(span, previousArea);
					}

					previousWalkable = walkable;
					previousArea = hf.SpanAreaOf(span);
					previous = span;
				}
			}
		}

		/// <summary>Spans at ledges (a neighbor drops more than the climb height) become non-walkable.</summary>
		public static void FilterLedgeSpans(Heightfield hf, int walkableHeight, int walkableClimb)
		{
			int w = hf.Width;
			int h = hf.Height;
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					for (int span = hf.Cells[x + z * w]; span != Heightfield.NullSpan; span = hf.SpanNext(span))
					{
						if (hf.SpanAreaOf(span) == NavMeshArea.Null)
						{
							continue;
						}

						int bot = hf.SpanMax(span);
						int next = hf.SpanNext(span);
						int top = next != Heightfield.NullSpan ? hf.SpanMin(next) : Heightfield.MaxHeight;

						int minh = Heightfield.MaxHeight;
						int asmin = bot;
						int asmax = bot;
						for (int dir = 0; dir < 4; dir++)
						{
							int dx = x + Dir.X[dir];
							int dz = z + Dir.Z[dir];
							if (dx < 0 || dz < 0 || dx >= w || dz >= h)
							{
								minh = Math.Min(minh, -walkableClimb - bot);
								continue;
							}

							// From the minus infinity to the first span.
							int ns = hf.Cells[dx + dz * w];
							int nbot = -walkableClimb;
							int ntop = ns != Heightfield.NullSpan ? hf.SpanMin(ns) : Heightfield.MaxHeight;
							if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
							{
								minh = Math.Min(minh, nbot - bot);
							}

							for (; ns != Heightfield.NullSpan; ns = hf.SpanNext(ns))
							{
								nbot = hf.SpanMax(ns);
								int nnext = hf.SpanNext(ns);
								ntop = nnext != Heightfield.NullSpan ? hf.SpanMin(nnext) : Heightfield.MaxHeight;
								if (Math.Min(top, ntop) - Math.Max(bot, nbot) > walkableHeight)
								{
									minh = Math.Min(minh, nbot - bot);
									if (Math.Abs(nbot - bot) <= walkableClimb)
									{
										asmin = Math.Min(asmin, nbot);
										asmax = Math.Max(asmax, nbot);
									}
								}
							}
						}

						// The current span is close to a ledge if the drop to any neighbor is more than walkableClimb,
						// or the walkable neighbors differ too much in height (steep).
						if (minh < -walkableClimb || asmax - asmin > walkableClimb)
						{
							hf.SetSpanArea(span, NavMeshArea.Null);
						}
					}
				}
			}
		}

		/// <summary>Spans without enough clearance for the agent become non-walkable.</summary>
		public static void FilterWalkableLowHeightSpans(Heightfield hf, int walkableHeight)
		{
			for (int column = 0; column < hf.Cells.Length; column++)
			{
				for (int span = hf.Cells[column]; span != Heightfield.NullSpan; span = hf.SpanNext(span))
				{
					int next = hf.SpanNext(span);
					int bot = hf.SpanMax(span);
					int top = next != Heightfield.NullSpan ? hf.SpanMin(next) : Heightfield.MaxHeight;
					if (top - bot <= walkableHeight)
					{
						hf.SetSpanArea(span, NavMeshArea.Null);
					}
				}
			}
		}

		#endregion
	}

	/// <summary>
	///     The walkable surface of a heightfield as a compact structure: per cell the open spans (floor height,
	///     clearance) with their 4-neighbor connections (Recast's rcCompactHeightfield).
	/// </summary>
	internal sealed class CompactHeightfield
	{
		#region PublicFields

		public const byte NotConnected = 255;

		public readonly int Width;
		public readonly int Height;
		public readonly int SpanCount;
		public readonly int WalkableHeight;
		public readonly int WalkableClimb;
		public readonly Vector3 BoundsMin;
		public readonly Vector3 BoundsMax;
		public readonly float CellSize;
		public readonly float CellHeight;

		/// <summary>Per cell: first span index and span count.</summary>
		public readonly int[] CellIndex;

		public readonly int[] CellCount;

		/// <summary>Per span: floor height (cells), clearance (cells), area, connections (span offset in the neighbor cell per direction).</summary>
		public readonly int[] Y;

		public readonly int[] H;
		public readonly byte[] Areas;
		public readonly byte[] Con;

		#endregion

		#region Constructors

		public CompactHeightfield(Heightfield hf, int walkableHeight, int walkableClimb)
		{
			Width = hf.Width;
			Height = hf.Height;
			WalkableHeight = walkableHeight;
			WalkableClimb = walkableClimb;
			BoundsMin = hf.BoundsMin;
			BoundsMax = hf.BoundsMax;
			BoundsMax.Y += walkableHeight * hf.CellHeight;
			CellSize = hf.CellSize;
			CellHeight = hf.CellHeight;

			int count = 0;
			for (int column = 0; column < hf.Cells.Length; column++)
			{
				for (int span = hf.Cells[column]; span != Heightfield.NullSpan; span = hf.SpanNext(span))
				{
					if (hf.SpanAreaOf(span) != NavMeshArea.Null)
					{
						count++;
					}
				}
			}

			SpanCount = count;
			CellIndex = new int[Width * Height];
			CellCount = new int[Width * Height];
			Y = new int[count];
			H = new int[count];
			Areas = new byte[count];
			Con = new byte[count * 4];

			int index = 0;
			for (int column = 0; column < hf.Cells.Length; column++)
			{
				CellIndex[column] = index;
				for (int span = hf.Cells[column]; span != Heightfield.NullSpan; span = hf.SpanNext(span))
				{
					if (hf.SpanAreaOf(span) == NavMeshArea.Null)
					{
						continue;
					}

					int bot = hf.SpanMax(span);
					int next = hf.SpanNext(span);
					int top = next != Heightfield.NullSpan ? hf.SpanMin(next) : Heightfield.MaxHeight;
					Y[index] = bot;
					H[index] = Math.Min(top - bot, 0xffff);
					Areas[index] = hf.SpanAreaOf(span);
					index++;
					CellCount[column]++;
				}
			}

			BuildConnections();
		}

		#endregion

		#region PublicMethods

		public byte GetCon(int span, int dir)
		{
			return Con[span * 4 + dir];
		}

		/// <summary>Span index of the neighbor of <paramref name="span" /> (cell x, z) in direction dir, or -1.</summary>
		public int Neighbor(int span, int x, int z, int dir)
		{
			byte con = Con[span * 4 + dir];
			if (con == NotConnected)
			{
				return -1;
			}

			int nx = x + Dir.X[dir];
			int nz = z + Dir.Z[dir];
			return CellIndex[nx + nz * Width] + con;
		}

		/// <summary>Shrinks the walkable area by the agent radius (Recast's rcErodeWalkableArea).</summary>
		public void ErodeWalkableArea(int radius)
		{
			int w = Width;
			int h = Height;
			var dist = new byte[SpanCount];
			Array.Fill(dist, (byte)255);

			// Boundary cells: null area, or fewer than 4 walkable neighbors.
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					int cell = x + z * w;
					for (int i = CellIndex[cell], end = i + CellCount[cell]; i < end; i++)
					{
						if (Areas[i] == NavMeshArea.Null)
						{
							dist[i] = 0;
							continue;
						}

						int nc = 0;
						for (int dir = 0; dir < 4; dir++)
						{
							int ai = Neighbor(i, x, z, dir);
							if (ai >= 0 && Areas[ai] != NavMeshArea.Null)
							{
								nc++;
							}
						}

						if (nc != 4)
						{
							dist[i] = 0;
						}
					}
				}
			}

			// Pass 1: -x, (-x,-z), -z, (+x,-z).
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					int cell = x + z * w;
					for (int i = CellIndex[cell], end = i + CellCount[cell]; i < end; i++)
					{
						int ai = Neighbor(i, x, z, 0);
						if (ai >= 0)
						{
							Relax(dist, i, ai, 2);
							int aai = Neighbor(ai, x + Dir.X[0], z + Dir.Z[0], 3);
							if (aai >= 0) Relax(dist, i, aai, 3);
						}

						ai = Neighbor(i, x, z, 3);
						if (ai >= 0)
						{
							Relax(dist, i, ai, 2);
							int aai = Neighbor(ai, x + Dir.X[3], z + Dir.Z[3], 2);
							if (aai >= 0) Relax(dist, i, aai, 3);
						}
					}
				}
			}

			// Pass 2: +x, (+x,+z), +z, (-x,+z).
			for (int z = h - 1; z >= 0; z--)
			{
				for (int x = w - 1; x >= 0; x--)
				{
					int cell = x + z * w;
					for (int i = CellIndex[cell], end = i + CellCount[cell]; i < end; i++)
					{
						int ai = Neighbor(i, x, z, 2);
						if (ai >= 0)
						{
							Relax(dist, i, ai, 2);
							int aai = Neighbor(ai, x + Dir.X[2], z + Dir.Z[2], 1);
							if (aai >= 0) Relax(dist, i, aai, 3);
						}

						ai = Neighbor(i, x, z, 1);
						if (ai >= 0)
						{
							Relax(dist, i, ai, 2);
							int aai = Neighbor(ai, x + Dir.X[1], z + Dir.Z[1], 0);
							if (aai >= 0) Relax(dist, i, aai, 3);
						}
					}
				}
			}

			int threshold = radius * 2;
			for (int i = 0; i < SpanCount; i++)
			{
				if (dist[i] < threshold)
				{
					Areas[i] = NavMeshArea.Null;
				}
			}
		}

		/// <summary>Sets the area of walkable spans inside a convex xz polygon and height range (Recast's rcMarkConvexPolyArea).</summary>
		public void MarkConvexPolyArea(Vector2[] polygon, float minY, float maxY, byte area)
		{
			if (polygon == null || polygon.Length < 3)
			{
				return;
			}

			Vector2 pmin = polygon[0];
			Vector2 pmax = polygon[0];
			foreach (Vector2 p in polygon)
			{
				pmin = Vector2.ComponentMin(pmin, p);
				pmax = Vector2.ComponentMax(pmax, p);
			}

			int minx = (int)((pmin.X - BoundsMin.X) / CellSize);
			int miny = (int)((minY - BoundsMin.Y) / CellHeight);
			int minz = (int)((pmin.Y - BoundsMin.Z) / CellSize);
			int maxx = (int)((pmax.X - BoundsMin.X) / CellSize);
			int maxy = (int)((maxY - BoundsMin.Y) / CellHeight);
			int maxz = (int)((pmax.Y - BoundsMin.Z) / CellSize);
			if (maxx < 0 || minx >= Width || maxz < 0 || minz >= Height)
			{
				return;
			}

			minx = Math.Max(0, minx);
			maxx = Math.Min(Width - 1, maxx);
			minz = Math.Max(0, minz);
			maxz = Math.Min(Height - 1, maxz);

			for (int z = minz; z <= maxz; z++)
			{
				for (int x = minx; x <= maxx; x++)
				{
					int cell = x + z * Width;
					for (int i = CellIndex[cell], end = i + CellCount[cell]; i < end; i++)
					{
						if (Areas[i] == NavMeshArea.Null || Y[i] < miny || Y[i] > maxy)
						{
							continue;
						}

						float px = BoundsMin.X + (x + 0.5f) * CellSize;
						float pz = BoundsMin.Z + (z + 0.5f) * CellSize;
						if (PointInPolygon(polygon, px, pz))
						{
							Areas[i] = area;
						}
					}
				}
			}
		}

		#endregion

		#region PrivateMethods

		private static void Relax(byte[] dist, int i, int ai, int add)
		{
			int nd = Math.Min(dist[ai] + add, 255);
			if (nd < dist[i])
			{
				dist[i] = (byte)nd;
			}
		}

		private static bool PointInPolygon(Vector2[] polygon, float x, float z)
		{
			bool inside = false;
			for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
			{
				Vector2 vi = polygon[i];
				Vector2 vj = polygon[j];
				if ((vi.Y > z) != (vj.Y > z) && x < (vj.X - vi.X) * (z - vi.Y) / (vj.Y - vi.Y) + vi.X)
				{
					inside = !inside;
				}
			}

			return inside;
		}

		private void BuildConnections()
		{
			int w = Width;
			int h = Height;
			Array.Fill(Con, NotConnected);
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					int cell = x + z * w;
					for (int i = CellIndex[cell], end = i + CellCount[cell]; i < end; i++)
					{
						for (int dir = 0; dir < 4; dir++)
						{
							int nx = x + Dir.X[dir];
							int nz = z + Dir.Z[dir];
							if (nx < 0 || nz < 0 || nx >= w || nz >= h)
							{
								continue;
							}

							int ncell = nx + nz * w;
							int nstart = CellIndex[ncell];
							for (int k = nstart, nend = nstart + CellCount[ncell]; k < nend; k++)
							{
								int bot = Math.Max(Y[i], Y[k]);
								int top = Math.Min(Y[i] + H[i], Y[k] + H[k]);
								if (top - bot >= WalkableHeight && Math.Abs(Y[k] - Y[i]) <= WalkableClimb)
								{
									int offset = k - nstart;
									if (offset >= 0 && offset < NotConnected)
									{
										Con[i * 4 + dir] = (byte)offset;
									}

									break;
								}
							}
						}
					}
				}
			}
		}

		#endregion
	}
}
