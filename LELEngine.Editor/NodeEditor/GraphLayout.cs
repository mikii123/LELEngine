using System;
using System.Collections.Generic;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Graphs
{
	/// <summary>
	///     Automatic placement of a graph's nodes: a layered layout (sources left, successors to the right, as a
	///     Sugiyama-style layering with barycentre ordering) for flow graphs, and a radial / force layout for state
	///     machines, where edges go both ways. Returns new positions; the host applies and records them.
	/// </summary>
	public static class GraphLayout
	{
		#region PublicMethods

		/// <summary>
		///     Places nodes in columns by their longest path from a source (cycles broken at back edges); nodes of a
		///     column are ordered by the average position of their predecessors. Sizes come from the last frame's layout.
		/// </summary>
		public static List<GraphMove> Layered(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, NVector2 origin, float columnGap = 80f, float rowGap = 40f, bool leftToRight = true)
		{
			var result = new List<GraphMove>();
			if (nodes.Count == 0)
			{
				return result;
			}

			var index = new Dictionary<ulong, int>();
			for (int i = 0; i < nodes.Count; i++)
			{
				index[nodes[i].Id] = i;
			}

			int n = nodes.Count;
			var successors = new List<int>[n];
			var predecessors = new List<int>[n];
			for (int i = 0; i < n; i++)
			{
				successors[i] = new List<int>();
				predecessors[i] = new List<int>();
			}

			foreach (GraphEdge edge in edges)
			{
				if (index.TryGetValue(edge.From, out int a) && index.TryGetValue(edge.To, out int b) && a != b)
				{
					successors[a].Add(b);
					predecessors[b].Add(a);
				}
			}

			// Longest path layering over the forward edges (a DFS marks the back edges, which are left out).
			var layer = new int[n];
			var state = new int[n];
			var forward = new List<int>[n];
			for (int i = 0; i < n; i++)
			{
				forward[i] = new List<int>();
			}

			for (int i = 0; i < n; i++)
			{
				if (state[i] == 0)
				{
					Visit(i, successors, state, forward);
				}
			}

			AssignLayers(n, forward, layer);

			int maxLayer = 0;
			foreach (int l in layer)
			{
				maxLayer = Math.Max(maxLayer, l);
			}

			var columns = new List<List<int>>();
			for (int l = 0; l <= maxLayer; l++)
			{
				columns.Add(new List<int>());
			}

			for (int i = 0; i < n; i++)
			{
				columns[layer[i]].Add(i);
			}

			// Barycentre ordering, a few sweeps.
			var order = new float[n];
			for (int sweep = 0; sweep < 4; sweep++)
			{
				for (int l = 0; l <= maxLayer; l++)
				{
					List<int> column = columns[l];
					for (int k = 0; k < column.Count; k++)
					{
						int node = column[k];
						float sum = 0f;
						int count = 0;
						foreach (int p in predecessors[node])
						{
							if (layer[p] < l)
							{
								sum += order[p];
								count++;
							}
						}

						order[node] = count > 0 ? sum / count : k;
					}

					column.Sort((a, b) => order[a].CompareTo(order[b]));
					for (int k = 0; k < column.Count; k++)
					{
						order[column[k]] = k;
					}
				}
			}

			// Positions: columns left to right (or top to bottom), nodes centred vertically per column.
			float x = origin.X;
			for (int l = 0; l <= maxLayer; l++)
			{
				List<int> column = columns[l];
				float columnWidth = 0f;
				float totalHeight = 0f;
				foreach (int node in column)
				{
					NVector2 size = SizeOf(nodes[node]);
					columnWidth = Math.Max(columnWidth, leftToRight ? size.X : size.Y);
					totalHeight += (leftToRight ? size.Y : size.X) + rowGap;
				}

				float y = origin.Y - totalHeight * 0.5f;
				foreach (int node in column)
				{
					NVector2 size = SizeOf(nodes[node]);
					result.Add(new GraphMove { Id = nodes[node].Id, Position = leftToRight ? new NVector2(x, y) : new NVector2(y, x), Size = size });
					y += (leftToRight ? size.Y : size.X) + rowGap;
				}

				x += columnWidth + columnGap;
			}

			return result;
		}

		/// <summary>
		///     A force-directed layout for graphs with edges in both directions (state machines): nodes repel, edges
		///     pull, starting from the current positions, so a tidy graph stays tidy. Pinned ids keep their place.
		/// </summary>
		public static List<GraphMove> Force(IReadOnlyList<GraphNode> nodes, IReadOnlyList<GraphEdge> edges, ISet<ulong> pinned, int iterations = 200, float idealLength = 220f)
		{
			var result = new List<GraphMove>();
			int n = nodes.Count;
			if (n == 0)
			{
				return result;
			}

			var index = new Dictionary<ulong, int>();
			var positions = new NVector2[n];
			var sizes = new NVector2[n];
			for (int i = 0; i < n; i++)
			{
				index[nodes[i].Id] = i;
				sizes[i] = SizeOf(nodes[i]);
				positions[i] = nodes[i].Position + sizes[i] * 0.5f;
			}

			var links = new List<(int a, int b)>();
			foreach (GraphEdge edge in edges)
			{
				if (index.TryGetValue(edge.From, out int a) && index.TryGetValue(edge.To, out int b) && a != b)
				{
					links.Add((a, b));
				}
			}

			var random = new Random(1234);
			var forces = new NVector2[n];
			float temperature = idealLength * 0.5f;
			for (int step = 0; step < iterations; step++)
			{
				for (int i = 0; i < n; i++)
				{
					forces[i] = NVector2.Zero;
				}

				for (int i = 0; i < n; i++)
				{
					for (int j = i + 1; j < n; j++)
					{
						NVector2 d = positions[j] - positions[i];
						float distance = d.Length();
						if (distance < 1f)
						{
							d = new NVector2((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f);
							distance = Math.Max(d.Length(), 0.01f);
						}

						// Repulsion shaped by the nodes' extents, so wide nodes keep their distance.
						float minimum = (sizes[i].X + sizes[j].X) * 0.5f + 40f;
						float repulsion = idealLength * idealLength / distance;
						if (distance < minimum)
						{
							repulsion *= 2f;
						}

						NVector2 push = d / distance * repulsion;
						forces[i] -= push;
						forces[j] += push;
					}
				}

				foreach ((int a, int b) in links)
				{
					NVector2 d = positions[b] - positions[a];
					float distance = Math.Max(d.Length(), 0.01f);
					float pull = distance * distance / idealLength;
					NVector2 force = d / distance * pull;
					forces[a] += force;
					forces[b] -= force;
				}

				for (int i = 0; i < n; i++)
				{
					if (pinned != null && pinned.Contains(nodes[i].Id))
					{
						continue;
					}

					float length = forces[i].Length();
					if (length > 1e-4f)
					{
						positions[i] += forces[i] / length * Math.Min(length, temperature);
					}
				}

				temperature = Math.Max(temperature * 0.96f, 1f);
			}

			for (int i = 0; i < n; i++)
			{
				NVector2 topLeft = positions[i] - sizes[i] * 0.5f;
				result.Add(new GraphMove { Id = nodes[i].Id, Position = new NVector2((float)Math.Round(topLeft.X), (float)Math.Round(topLeft.Y)), Size = sizes[i] });
			}

			return result;
		}

		/// <summary>Aligns the nodes' left edges, right edges, tops, bottoms or centres (<paramref name="mode" />: 0 left, 1 center x, 2 right, 3 top, 4 center y, 5 bottom).</summary>
		public static List<GraphMove> Align(IReadOnlyList<GraphNode> nodes, int mode)
		{
			var result = new List<GraphMove>();
			if (nodes.Count < 2)
			{
				return result;
			}

			float reference = 0f;
			for (int i = 0; i < nodes.Count; i++)
			{
				NVector2 size = SizeOf(nodes[i]);
				float value = Value(nodes[i].Position, size, mode);
				reference = i == 0 ? value : mode == 0 || mode == 3 ? Math.Min(reference, value) : mode == 2 || mode == 5 ? Math.Max(reference, value) : reference + value;
			}

			if (mode == 1 || mode == 4)
			{
				reference /= nodes.Count;
			}

			foreach (GraphNode node in nodes)
			{
				NVector2 size = SizeOf(node);
				NVector2 position = node.Position;
				switch (mode)
				{
					case 0: position.X = reference; break;
					case 1: position.X = reference - size.X * 0.5f; break;
					case 2: position.X = reference - size.X; break;
					case 3: position.Y = reference; break;
					case 4: position.Y = reference - size.Y * 0.5f; break;
					case 5: position.Y = reference - size.Y; break;
				}

				result.Add(new GraphMove { Id = node.Id, Position = position, Size = size });
			}

			return result;
		}

		/// <summary>Spreads the nodes evenly between the first and the last along x (<paramref name="horizontal" />) or y.</summary>
		public static List<GraphMove> Distribute(IReadOnlyList<GraphNode> nodes, bool horizontal)
		{
			var result = new List<GraphMove>();
			if (nodes.Count < 3)
			{
				return result;
			}

			var sorted = new List<GraphNode>(nodes);
			sorted.Sort((a, b) => horizontal ? a.Position.X.CompareTo(b.Position.X) : a.Position.Y.CompareTo(b.Position.Y));
			float first = horizontal ? sorted[0].Position.X : sorted[0].Position.Y;
			GraphNode last = sorted[sorted.Count - 1];
			float end = horizontal ? last.Position.X : last.Position.Y;
			float step = (end - first) / (sorted.Count - 1);
			for (int i = 0; i < sorted.Count; i++)
			{
				NVector2 position = sorted[i].Position;
				if (horizontal) position.X = first + step * i;
				else position.Y = first + step * i;
				result.Add(new GraphMove { Id = sorted[i].Id, Position = position, Size = SizeOf(sorted[i]) });
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private static NVector2 SizeOf(GraphNode node)
		{
			NVector2 size = node.Size;
			if (size.X <= 0f || size.Y <= 0f)
			{
				size = new NVector2(node.Width > 0f ? node.Width : 180f, node.Height > 0f ? node.Height : 48f);
			}

			return size;
		}

		private static float Value(NVector2 position, NVector2 size, int mode)
		{
			switch (mode)
			{
				case 0: return position.X;
				case 1: return position.X + size.X * 0.5f;
				case 2: return position.X + size.X;
				case 3: return position.Y;
				case 4: return position.Y + size.Y * 0.5f;
				default: return position.Y + size.Y;
			}
		}

		private static void Visit(int node, List<int>[] successors, int[] state, List<int>[] forward)
		{
			state[node] = 1;
			foreach (int next in successors[node])
			{
				if (state[next] == 1)
				{
					// A back edge: ignored for the layering.
					continue;
				}

				forward[node].Add(next);
				if (state[next] == 0)
				{
					Visit(next, successors, state, forward);
				}
			}

			state[node] = 2;
		}

		#endregion

		#region InternalMethods

		/// <summary>Longest path from any source, over the DFS forest's forward edges (the layer of each node).</summary>
		internal static void AssignLayers(int n, List<int>[] successors, int[] layer)
		{
			// Kahn-style relaxation over a DAG built by the DFS: a simple iterative relaxation suffices for our sizes.
			for (int iteration = 0; iteration < n + 1; iteration++)
			{
				bool changed = false;
				for (int i = 0; i < n; i++)
				{
					foreach (int next in successors[i])
					{
						if (layer[next] < layer[i] + 1 && layer[i] + 1 < n)
						{
							layer[next] = layer[i] + 1;
							changed = true;
						}
					}
				}

				if (!changed)
				{
					break;
				}
			}
		}

		#endregion
	}
}
