using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.Navigation.Builder
{
	/// <summary>
	///     One walkable layer of a tile: a 2D grid (the tile without its border) with, per cell, the floor height
	///     relative to <see cref="HMin" />, the area and the connections (low nibble: neighbors in this layer,
	///     high nibble: "portals" leading out of the layer, into the tile border or another layer). The polygons of
	///     a tile are built from its layers, after obstacles mark their cells unwalkable (Recast's
	///     rcHeightfieldLayer / dtTileCacheLayer).
	/// </summary>
	internal sealed class HeightfieldLayer
	{
		#region PublicFields

		public const ushort EmptyHeight = 0xffff;

		public int Width;
		public int Height;
		public int MinX;
		public int MaxX;
		public int MinY;
		public int MaxY;
		public int HMin;
		public int HMax;
		public Vector3 BoundsMin;
		public Vector3 BoundsMax;
		public float CellSize;
		public float CellHeight;
		public ushort[] Heights;
		public byte[] Areas;
		public byte[] Cons;

		#endregion

		#region PublicMethods

		public HeightfieldLayer Clone()
		{
			var copy = (HeightfieldLayer)MemberwiseClone();
			copy.Heights = (ushort[])Heights.Clone();
			copy.Areas = (byte[])Areas.Clone();
			copy.Cons = (byte[])Cons.Clone();
			return copy;
		}

		/// <summary>Packs the layer into its serialized form.</summary>
		public NavMeshLayerData ToData(int x, int y, int layer)
		{
			int cells = Width * Height;
			var raw = new byte[cells * 4];
			for (int i = 0; i < cells; i++)
			{
				raw[i] = (byte)(Heights[i] & 0xff);
				raw[cells + i] = (byte)(Heights[i] >> 8);
			}

			Buffer.BlockCopy(Areas, 0, raw, cells * 2, cells);
			Buffer.BlockCopy(Cons, 0, raw, cells * 3, cells);
			return new NavMeshLayerData
			{
				X = x, Y = y, Layer = layer, Width = Width, Height = Height, MinX = MinX, MaxX = MaxX, MinY = MinY, MaxY = MaxY,
				HMin = HMin, HMax = HMax, BoundsMin = BoundsMin, BoundsMax = BoundsMax, Compressed = RunLength.Encode(raw)
			};
		}

		public static HeightfieldLayer FromData(NavMeshLayerData data, float cellSize, float cellHeight)
		{
			int cells = data.Width * data.Height;
			byte[] raw = RunLength.Decode(data.Compressed, cells * 4);
			var layer = new HeightfieldLayer
			{
				Width = data.Width, Height = data.Height, MinX = data.MinX, MaxX = data.MaxX, MinY = data.MinY, MaxY = data.MaxY,
				HMin = data.HMin, HMax = data.HMax, BoundsMin = data.BoundsMin, BoundsMax = data.BoundsMax, CellSize = cellSize, CellHeight = cellHeight,
				Heights = new ushort[cells], Areas = new byte[cells], Cons = new byte[cells]
			};
			for (int i = 0; i < cells; i++)
			{
				layer.Heights[i] = (ushort)(raw[i] | (raw[cells + i] << 8));
			}

			Buffer.BlockCopy(raw, cells * 2, layer.Areas, 0, cells);
			Buffer.BlockCopy(raw, cells * 3, layer.Cons, 0, cells);
			return layer;
		}

		// ---------------------------------------------------------------- obstacle marking

		/// <summary>Cells inside a vertical cylinder (local space) get the area (0 carves them out).</summary>
		public void MarkCylinderArea(Vector3 position, float radius, float height, byte area)
		{
			float ics = 1f / CellSize;
			float ich = 1f / CellHeight;
			float px = (position.X - BoundsMin.X) * ics;
			float pz = (position.Z - BoundsMin.Z) * ics;
			float r = radius * ics + 0.5f;
			float r2 = r * r;
			int minx = (int)Math.Floor(px - r);
			int maxx = (int)Math.Floor(px + r);
			int minz = (int)Math.Floor(pz - r);
			int maxz = (int)Math.Floor(pz + r);
			int miny = (int)Math.Floor((position.Y - BoundsMin.Y) * ich);
			int maxy = (int)Math.Floor((position.Y + height - BoundsMin.Y) * ich);
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
					float dx = x + 0.5f - px;
					float dz = z + 0.5f - pz;
					if (dx * dx + dz * dz > r2)
					{
						continue;
					}

					int index = x + z * Width;
					int y = Heights[index];
					if (y == EmptyHeight || y < miny || y > maxy)
					{
						continue;
					}

					Areas[index] = area;
				}
			}
		}

		/// <summary>
		///     Cells inside a box rotated about the vertical axis (local space): <paramref name="axisX" /> and
		///     <paramref name="axisZ" /> are the box's horizontal axes (unit length), the extents half sizes.
		/// </summary>
		public void MarkOrientedBoxArea(Vector3 center, Vector2 axisX, Vector2 axisZ, Vector3 halfExtents, byte area)
		{
			float ics = 1f / CellSize;
			float ich = 1f / CellHeight;
			float cx = (center.X - BoundsMin.X) * ics;
			float cz = (center.Z - BoundsMin.Z) * ics;
			float ex = halfExtents.X * ics + 0.5f;
			float ez = halfExtents.Z * ics + 0.5f;
			float reach = (float)Math.Sqrt(ex * ex + ez * ez);
			int minx = (int)Math.Floor(cx - reach);
			int maxx = (int)Math.Floor(cx + reach);
			int minz = (int)Math.Floor(cz - reach);
			int maxz = (int)Math.Floor(cz + reach);
			int miny = (int)Math.Floor((center.Y - halfExtents.Y - BoundsMin.Y) * ich);
			int maxy = (int)Math.Floor((center.Y + halfExtents.Y - BoundsMin.Y) * ich);
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
					float dx = x + 0.5f - cx;
					float dz = z + 0.5f - cz;
					float lx = dx * axisX.X + dz * axisX.Y;
					float lz = dx * axisZ.X + dz * axisZ.Y;
					if (Math.Abs(lx) > ex || Math.Abs(lz) > ez)
					{
						continue;
					}

					int index = x + z * Width;
					int y = Heights[index];
					if (y == EmptyHeight || y < miny || y > maxy)
					{
						continue;
					}

					Areas[index] = area;
				}
			}
		}

		#endregion
	}

	/// <summary>Byte run-length coding: (value, run) pairs, runs up to 255.</summary>
	internal static class RunLength
	{
		public static byte[] Encode(byte[] data)
		{
			var output = new List<byte>(data.Length / 4 + 16);
			int i = 0;
			while (i < data.Length)
			{
				byte value = data[i];
				int run = 1;
				while (i + run < data.Length && data[i + run] == value && run < 255)
				{
					run++;
				}

				output.Add(value);
				output.Add((byte)run);
				i += run;
			}

			return output.ToArray();
		}

		public static byte[] Decode(byte[] compressed, int length)
		{
			var output = new byte[length];
			int o = 0;
			for (int i = 0; i + 1 < compressed.Length && o < length; i += 2)
			{
				byte value = compressed[i];
				int run = Math.Min(compressed[i + 1], length - o);
				if (value != 0)
				{
					output.AsSpan(o, run).Fill(value);
				}

				o += run;
			}

			return output;
		}
	}

	/// <summary>Splits a tile's compact heightfield into non-overlapping 2D layers (Recast's rcBuildHeightfieldLayers).</summary>
	internal static class LayerBuilder
	{
		#region PublicFields

		private const ushort NullRegion = 0xffff;
		private const int MaxLayerHeightRange = 60000;

		#endregion

		#region PublicMethods

		public static List<HeightfieldLayer> Build(CompactHeightfield chf, int borderSize, int walkableHeight)
		{
			int w = chf.Width;
			int h = chf.Height;
			var srcReg = new ushort[chf.SpanCount];
			Array.Fill(srcReg, NullRegion);

			var sweepNei = new ushort[w];
			var sweepNs = new int[w];
			var sweepId = new ushort[w];
			var prevCount = new List<int>();
			int regionCount = 0;

			// Partition the walkable area into monotone regions.
			for (int z = borderSize; z < h - borderSize; z++)
			{
				prevCount.Clear();
				for (int i = 0; i < regionCount; i++)
				{
					prevCount.Add(0);
				}

				int sweeps = 0;
				for (int x = borderSize; x < w - borderSize; x++)
				{
					int cell = x + z * w;
					for (int i = chf.CellIndex[cell], end = i + chf.CellCount[cell]; i < end; i++)
					{
						if (chf.Areas[i] == NavMeshArea.Null)
						{
							continue;
						}

						ushort sid = NullRegion;
						int ai = chf.Neighbor(i, x, z, 0);
						if (ai >= 0 && chf.Areas[ai] != NavMeshArea.Null && srcReg[ai] != NullRegion)
						{
							sid = srcReg[ai];
						}

						if (sid == NullRegion)
						{
							sid = (ushort)sweeps++;
							sweepNei[sid] = NullRegion;
							sweepNs[sid] = 0;
						}

						ai = chf.Neighbor(i, x, z, 3);
						if (ai >= 0)
						{
							ushort nr = srcReg[ai];
							if (nr != NullRegion)
							{
								if (sweepNs[sid] == 0)
								{
									sweepNei[sid] = nr;
								}

								if (sweepNei[sid] == nr)
								{
									sweepNs[sid]++;
									prevCount[nr]++;
								}
								else
								{
									sweepNei[sid] = NullRegion;
								}
							}
						}

						srcReg[i] = sid;
					}
				}

				// Unique ids: a sweep joins its previous-row neighbor when that is its only connection.
				for (int i = 0; i < sweeps; i++)
				{
					if (sweepNei[i] != NullRegion && prevCount[sweepNei[i]] == sweepNs[i])
					{
						sweepId[i] = sweepNei[i];
					}
					else
					{
						sweepId[i] = (ushort)regionCount++;
					}
				}

				for (int x = borderSize; x < w - borderSize; x++)
				{
					int cell = x + z * w;
					for (int i = chf.CellIndex[cell], end = i + chf.CellCount[cell]; i < end; i++)
					{
						if (srcReg[i] != NullRegion)
						{
							srcReg[i] = sweepId[srcReg[i]];
						}
					}
				}
			}

			var regions = new LayerRegion[regionCount];
			for (int i = 0; i < regionCount; i++)
			{
				regions[i] = new LayerRegion { YMin = int.MaxValue, YMax = int.MinValue, LayerId = NullRegion };
			}

			// Region neighbors and overlaps (regions stacked in one column).
			var columnRegions = new List<ushort>();
			for (int z = 0; z < h; z++)
			{
				for (int x = 0; x < w; x++)
				{
					int cell = x + z * w;
					columnRegions.Clear();
					for (int i = chf.CellIndex[cell], end = i + chf.CellCount[cell]; i < end; i++)
					{
						ushort ri = srcReg[i];
						if (ri == NullRegion)
						{
							continue;
						}

						LayerRegion region = regions[ri];
						region.YMin = Math.Min(region.YMin, chf.Y[i]);
						region.YMax = Math.Max(region.YMax, chf.Y[i]);
						columnRegions.Add(ri);
						for (int dir = 0; dir < 4; dir++)
						{
							int ai = chf.Neighbor(i, x, z, dir);
							if (ai >= 0)
							{
								ushort rai = srcReg[ai];
								if (rai != NullRegion && rai != ri)
								{
									AddUnique(region.Neighbors, rai);
								}
							}
						}
					}

					for (int i = 0; i < columnRegions.Count - 1; i++)
					{
						for (int j = i + 1; j < columnRegions.Count; j++)
						{
							if (columnRegions[i] != columnRegions[j])
							{
								AddUnique(regions[columnRegions[i]].Layers, columnRegions[j]);
								AddUnique(regions[columnRegions[j]].Layers, columnRegions[i]);
							}
						}
					}
				}
			}

			// 2D layers from regions: flood neighbors that do not overlap the root.
			ushort layerId = 0;
			var stack = new List<ushort>();
			for (int i = 0; i < regionCount; i++)
			{
				LayerRegion root = regions[i];
				if (root.LayerId != NullRegion)
				{
					continue;
				}

				root.LayerId = layerId;
				root.Base = true;
				stack.Clear();
				stack.Add((ushort)i);
				while (stack.Count > 0)
				{
					LayerRegion region = regions[stack[0]];
					stack.RemoveAt(0);
					foreach (ushort nei in region.Neighbors)
					{
						LayerRegion regn = regions[nei];
						if (regn.LayerId != NullRegion || root.Layers.Contains(nei))
						{
							continue;
						}

						int ymin = Math.Min(root.YMin, regn.YMin);
						int ymax = Math.Max(root.YMax, regn.YMax);
						if (ymax - ymin >= MaxLayerHeightRange)
						{
							continue;
						}

						stack.Add(nei);
						regn.LayerId = layerId;
						foreach (ushort layer in regn.Layers)
						{
							AddUnique(root.Layers, layer);
						}

						root.YMin = ymin;
						root.YMax = ymax;
					}
				}

				layerId++;
			}

			// Merge non-overlapping layers that are close in height.
			int mergeHeight = walkableHeight * 4;
			for (int i = 0; i < regionCount; i++)
			{
				LayerRegion ri = regions[i];
				if (!ri.Base)
				{
					continue;
				}

				ushort newId = ri.LayerId;
				while (true)
				{
					ushort oldId = NullRegion;
					for (int j = 0; j < regionCount; j++)
					{
						if (i == j)
						{
							continue;
						}

						LayerRegion rj = regions[j];
						if (!rj.Base || !OverlapRange(ri.YMin, ri.YMax + mergeHeight, rj.YMin, rj.YMax + mergeHeight))
						{
							continue;
						}

						int ymin = Math.Min(ri.YMin, rj.YMin);
						int ymax = Math.Max(ri.YMax, rj.YMax);
						if (ymax - ymin >= MaxLayerHeightRange)
						{
							continue;
						}

						bool overlap = false;
						for (int k = 0; k < regionCount; k++)
						{
							if (regions[k].LayerId == rj.LayerId && ri.Layers.Contains((ushort)k))
							{
								overlap = true;
								break;
							}
						}

						if (overlap)
						{
							continue;
						}

						oldId = rj.LayerId;
						break;
					}

					if (oldId == NullRegion)
					{
						break;
					}

					for (int j = 0; j < regionCount; j++)
					{
						LayerRegion rj = regions[j];
						if (rj.LayerId == oldId)
						{
							rj.Base = false;
							rj.LayerId = newId;
							foreach (ushort layer in rj.Layers)
							{
								AddUnique(ri.Layers, layer);
							}

							ri.YMin = Math.Min(ri.YMin, rj.YMin);
							ri.YMax = Math.Max(ri.YMax, rj.YMax);
						}
					}
				}
			}

			// Compact layer ids.
			var remap = new Dictionary<ushort, ushort>();
			for (int i = 0; i < regionCount; i++)
			{
				if (!remap.ContainsKey(regions[i].LayerId))
				{
					remap[regions[i].LayerId] = (ushort)remap.Count;
				}
			}

			for (int i = 0; i < regionCount; i++)
			{
				regions[i].LayerId = remap[regions[i].LayerId];
			}

			int layerCount = remap.Count;
			var result = new List<HeightfieldLayer>(layerCount);
			if (layerCount == 0)
			{
				return result;
			}

			int lw = w - borderSize * 2;
			int lh = h - borderSize * 2;
			Vector3 bmin = chf.BoundsMin;
			Vector3 bmax = chf.BoundsMax;
			bmin.X += borderSize * chf.CellSize;
			bmin.Z += borderSize * chf.CellSize;
			bmax.X -= borderSize * chf.CellSize;
			bmax.Z -= borderSize * chf.CellSize;

			for (int current = 0; current < layerCount; current++)
			{
				int hmin = 0;
				int hmax = 0;
				for (int j = 0; j < regionCount; j++)
				{
					if (regions[j].Base && regions[j].LayerId == current)
					{
						hmin = regions[j].YMin;
						hmax = regions[j].YMax;
					}
				}

				var layer = new HeightfieldLayer
				{
					Width = lw, Height = lh, CellSize = chf.CellSize, CellHeight = chf.CellHeight, HMin = hmin, HMax = hmax,
					BoundsMin = new Vector3(bmin.X, chf.BoundsMin.Y + hmin * chf.CellHeight, bmin.Z),
					BoundsMax = new Vector3(bmax.X, chf.BoundsMin.Y + hmax * chf.CellHeight, bmax.Z),
					Heights = new ushort[lw * lh], Areas = new byte[lw * lh], Cons = new byte[lw * lh],
					MinX = lw, MaxX = 0, MinY = lh, MaxY = 0
				};
				Array.Fill(layer.Heights, HeightfieldLayer.EmptyHeight);

				for (int y = 0; y < lh; y++)
				{
					for (int x = 0; x < lw; x++)
					{
						int cx = borderSize + x;
						int cz = borderSize + y;
						int cell = cx + cz * w;
						for (int j = chf.CellIndex[cell], end = j + chf.CellCount[cell]; j < end; j++)
						{
							if (srcReg[j] == NullRegion)
							{
								continue;
							}

							int lid = regions[srcReg[j]].LayerId;
							if (lid != current)
							{
								continue;
							}

							layer.MinX = Math.Min(layer.MinX, x);
							layer.MaxX = Math.Max(layer.MaxX, x);
							layer.MinY = Math.Min(layer.MinY, y);
							layer.MaxY = Math.Max(layer.MaxY, y);

							int index = x + y * lw;
							layer.Heights[index] = (ushort)Math.Clamp(chf.Y[j] - hmin, 0, 0xfffe);
							layer.Areas[index] = chf.Areas[j];

							byte portal = 0;
							byte con = 0;
							for (int dir = 0; dir < 4; dir++)
							{
								int ai = chf.Neighbor(j, cx, cz, dir);
								if (ai < 0)
								{
									continue;
								}

								int alid = srcReg[ai] != NullRegion ? regions[srcReg[ai]].LayerId : NullRegion;
								if (chf.Areas[ai] != NavMeshArea.Null && lid != alid)
								{
									portal |= (byte)(1 << dir);
									if (chf.Y[ai] > hmin)
									{
										layer.Heights[index] = (ushort)Math.Max(layer.Heights[index], Math.Clamp(chf.Y[ai] - hmin, 0, 0xfffe));
									}
								}

								if (chf.Areas[ai] != NavMeshArea.Null && lid == alid)
								{
									int nx = cx + Dir.X[dir] - borderSize;
									int ny = cz + Dir.Z[dir] - borderSize;
									if (nx >= 0 && ny >= 0 && nx < lw && ny < lh)
									{
										con |= (byte)(1 << dir);
									}
								}
							}

							layer.Cons[index] = (byte)((portal << 4) | con);
						}
					}
				}

				if (layer.MinX > layer.MaxX)
				{
					layer.MinX = layer.MaxX = 0;
				}

				if (layer.MinY > layer.MaxY)
				{
					layer.MinY = layer.MaxY = 0;
				}

				result.Add(layer);
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private sealed class LayerRegion
		{
			public readonly List<ushort> Layers = new List<ushort>();
			public readonly List<ushort> Neighbors = new List<ushort>();
			public int YMin;
			public int YMax;
			public ushort LayerId;
			public bool Base;
		}

		private static void AddUnique(List<ushort> list, ushort value)
		{
			if (!list.Contains(value))
			{
				list.Add(value);
			}
		}

		private static bool OverlapRange(int amin, int amax, int bmin, int bmax)
		{
			return !(amin > bmax || amax < bmin);
		}

		#endregion
	}
}
