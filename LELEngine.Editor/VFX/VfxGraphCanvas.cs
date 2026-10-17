using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using LELEngine.VFX;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.VFX
{
	/// <summary>
	///     The node canvas of the VFX Graph window: contexts with their blocks, operators, data and flow edges, drawn
	///     with the window draw list and edited with the mouse (select, move, connect, pan, zoom, create nodes from
	///     a searchable menu, add blocks to contexts, reorder blocks). Every change goes through the document's
	///     edit records, so it is undoable.
	/// </summary>
	internal sealed class VfxGraphCanvas
	{
		#region PublicFields

		public VfxGraphDocument Document;
		public readonly HashSet<ulong> Selected = new HashSet<ulong>();

		/// <summary>The node shown in the inspector (the last selected one).</summary>
		public ulong Primary;

		public float Zoom = 1f;
		public NVector2 Offset;

		/// <summary>Node ids with compile errors (highlighted).</summary>
		public HashSet<ulong> ErrorNodes = new HashSet<ulong>();

		/// <summary>Frame every node on the next draw (once the layout and the canvas size are known).</summary>
		public bool PendingFrame;

		public const string PropertyDragType = "LEL_VFX_PROPERTY";
		public static string DraggedProperty;

		#endregion

		#region PrivateFields

		private enum HitKind
		{
			None,
			Node,
			Block,
			BlockEnable,
			InputPort,
			OutputPort,
			FlowInput,
			FlowOutput,
			AddBlock
		}

		private struct Hit
		{
			public HitKind Kind;
			public VfxNodeData Node;
			public string Slot;
			public int Port;
		}

		private sealed class PortLayout
		{
			public NVector2 Center;
			public VfxValueType Type;
			public string Slot;
			public VfxNodeData Node;
			public bool Input;
		}

		private sealed class NodeLayout
		{
			public VfxNodeData Node;
			public VfxNodeDefinition Definition;
			public NVector2 Min;
			public NVector2 Max;
			public readonly List<PortLayout> Ports = new List<PortLayout>();
			public readonly List<NVector2> FlowInputs = new List<NVector2>();
			public readonly List<NVector2> FlowOutputs = new List<NVector2>();
			public string[] FlowOutputNames = Array.Empty<string>();
			public readonly List<BlockLayout> Blocks = new List<BlockLayout>();
			public NVector2 AddBlockMin;
			public NVector2 AddBlockMax;
			public float HeaderHeight;
		}

		private sealed class BlockLayout
		{
			public VfxNodeData Node;
			public NVector2 Min;
			public NVector2 Max;
			public NVector2 EnableMin;
			public NVector2 EnableMax;
		}

		private const float OperatorWidth = 170f;
		private const float ContextWidth = 330f;
		private const float HeaderHeight = 26f;
		private const float RowHeight = 20f;
		private const float BlockHeaderHeight = 22f;
		private const float BlockRowHeight = 18f;
		private const float PortRadius = 5f;
		private const float FlowPortWidth = 22f;
		private const float FlowPortHeight = 8f;
		private const float BaseFontSize = 15f;

		private readonly Dictionary<ulong, NodeLayout> layouts = new Dictionary<ulong, NodeLayout>();
		private readonly List<NodeLayout> ordered = new List<NodeLayout>();
		private NVector2 canvasMin;
		private NVector2 canvasSize;
		private bool hovered;
		private bool focused;

		// Interaction state.
		private bool draggingNodes;
		private bool nodesMoved;
		private readonly Dictionary<ulong, Vector2> dragStart = new Dictionary<ulong, Vector2>();
		private bool boxSelecting;
		private NVector2 boxStart;
		private bool panning;
		private NVector2 panStart;
		private NVector2 panOffsetStart;
		private PortLayout dragPort;
		private bool dragFlow;
		private VfxNodeData dragFlowNode;
		private int dragFlowPort;
		private bool dragFlowIsOutput;
		private VfxNodeData draggingBlock;
		private NVector2 rightPress;

		// Create menu.
		private bool openCreatePopup;
		private NVector2 createPosition;
		private string createSearch = "";
		private PortLayout pendingPort;
		private VfxNodeData pendingFlowNode;
		private int pendingFlowPort;
		private bool pendingFlowIsOutput;
		private bool openBlockPopup;
		private VfxNodeData blockPopupContext;
		private string blockSearch = "";
		private VfxNodeData contextMenuNode;
		private bool openNodeContextMenu;

		#endregion

		#region PublicMethods

		/// <summary>Draws the canvas in the current window's remaining area.</summary>
		public void Draw()
		{
			if (Document == null)
			{
				ImGui.TextDisabled("No graph open.");
				return;
			}

			canvasMin = ImGui.GetCursorScreenPos();
			canvasSize = ImGui.GetContentRegionAvail();
			if (canvasSize.X < 16f || canvasSize.Y < 16f)
			{
				return;
			}

			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(canvasMin, canvasMin + canvasSize, ImGui.GetColorU32(new NVector4(0.11f, 0.11f, 0.12f, 1f)));
			DrawGrid(draw);

			ImGui.SetCursorScreenPos(canvasMin);
			ImGui.InvisibleButton("##vfx_canvas", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
			hovered = ImGui.IsItemHovered();
			focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			AcceptPropertyDrop();

			draw.PushClipRect(canvasMin, canvasMin + canvasSize, true);
			ImGui.PushFont(ImGui.GetFont(), BaseFontSize * Zoom);
			try
			{
				ComputeLayouts();
				if (PendingFrame)
				{
					PendingFrame = false;
					FrameAll();
					ComputeLayouts();
				}

				DrawEdges(draw);
				foreach (NodeLayout layout in ordered)
				{
					DrawNode(draw, layout);
				}

				DrawDragConnection(draw);
				if (boxSelecting)
				{
					NVector2 a = NVector2.Min(boxStart, ImGui.GetMousePos());
					NVector2 b = NVector2.Max(boxStart, ImGui.GetMousePos());
					draw.AddRectFilled(a, b, ImGui.GetColorU32(new NVector4(0.3f, 0.5f, 0.9f, 0.15f)));
					draw.AddRect(a, b, ImGui.GetColorU32(new NVector4(0.3f, 0.5f, 0.9f, 0.8f)));
				}
			}
			finally
			{
				ImGui.PopFont();
				draw.PopClipRect();
			}

			HandleInput();
			DrawCreatePopup();
			DrawBlockPopup();
			DrawNodeContextMenu();
		}

		/// <summary>Centers the view on every node.</summary>
		public void FrameAll()
		{
			if (Document == null || Document.Graph.Nodes.Count == 0)
			{
				Offset = NVector2.Zero;
				Zoom = 1f;
				return;
			}

			var min = new NVector2(float.MaxValue);
			var max = new NVector2(float.MinValue);
			foreach (VfxNodeData node in Document.Graph.Nodes)
			{
				if (node.Parent != 0)
				{
					continue;
				}

				NodeLayout layout;
				NVector2 size = layouts.TryGetValue(node.Id, out layout) ? (layout.Max - layout.Min) / Math.Max(Zoom, 0.01f) : new NVector2(ContextWidth, 200f);
				min = NVector2.Min(min, new NVector2(node.Position.X, node.Position.Y));
				max = NVector2.Max(max, new NVector2(node.Position.X, node.Position.Y) + size);
			}

			if (min.X > max.X)
			{
				return;
			}

			NVector2 extent = max - min + new NVector2(80f, 80f);
			float zoomX = canvasSize.X / Math.Max(extent.X, 1f);
			float zoomY = canvasSize.Y / Math.Max(extent.Y, 1f);
			Zoom = Math.Clamp(Math.Min(zoomX, zoomY), 0.25f, 1.5f);
			NVector2 center = (min + max) * 0.5f;
			Offset = canvasSize * 0.5f / Zoom - center;
		}

		/// <summary>
		///     Arranges the contexts: every chain (spawn, initialize, update, outputs) in a column with the measured
		///     heights, GPU-event children in the next column, operators left of the contexts that read them.
		/// </summary>
		public void AutoLayout()
		{
			if (Document == null || ordered.Count == 0)
			{
				return;
			}

			VfxGraph graph = Document.Graph;
			var heights = new Dictionary<ulong, float>();
			foreach (NodeLayout layout in ordered)
			{
				heights[layout.Node.Id] = (layout.Max.Y - layout.Min.Y) / Math.Max(Zoom, 0.01f);
			}

			Document.EditLayout("Auto layout", () =>
			{
				var placed = new HashSet<ulong>();
				float columnX = 0f;
				foreach (VfxNodeData root in graph.GetContexts())
				{
					// Roots: contexts nothing flows into (events, spawns without events, orphan initializes).
					if (placed.Contains(root.Id) || HasFlowInput(graph, root))
					{
						continue;
					}

					float bottom = PlaceChain(graph, root, columnX, 0f, placed, heights, 0);
					columnX += ContextWidth + 120f;
					_ = bottom;
				}

				// Any context still unplaced (cycles, loose ones).
				foreach (VfxNodeData context in graph.GetContexts())
				{
					if (placed.Add(context.Id))
					{
						context.Position = new Vector2(columnX, 0f);
						columnX += ContextWidth + 120f;
					}
				}

				// Operators: left of the first node that reads them, stacked by their depth.
				var operatorRows = new Dictionary<ulong, float>();
				foreach (VfxNodeData node in graph.Nodes)
				{
					VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
					if (definition == null || definition.Kind != VfxNodeKind.Operator)
					{
						continue;
					}

					int depth = OperatorDepth(graph, node, 0);
					ulong consumer = 0;
					foreach (VfxEdge edge in graph.Edges)
					{
						if (edge.From == node.Id)
						{
							consumer = edge.To;
							break;
						}
					}

					VfxNodeData target = graph.FindNode(consumer);
					if (target != null && target.Parent != 0)
					{
						target = graph.FindNode(target.Parent);
					}

					float x = (target?.Position.X ?? 0f) - (OperatorWidth + 60f) * (depth + 1);
					float y = target?.Position.Y ?? 0f;
					ulong key = (ulong)(depth * 7919) ^ (ulong)(target?.Id ?? 0);
					float row;
					operatorRows.TryGetValue(key, out row);
					node.Position = new Vector2(x, y + row);
					float height = heights.TryGetValue(node.Id, out float h) ? h : 100f;
					operatorRows[key] = row + height + 30f;
				}
			});

			PendingFrame = true;
		}

		private static bool HasFlowInput(VfxGraph graph, VfxNodeData node)
		{
			foreach (VfxFlowEdge edge in graph.Flow)
			{
				if (edge.To == node.Id)
				{
					return true;
				}
			}

			return false;
		}

		private static float PlaceChain(VfxGraph graph, VfxNodeData node, float x, float y, HashSet<ulong> placed, Dictionary<ulong, float> heights, int depth)
		{
			if (!placed.Add(node.Id) || depth > 32)
			{
				return y;
			}

			node.Position = new Vector2(x, y);
			float height = heights.TryGetValue(node.Id, out float h) ? h : 200f;
			float bottom = y + height + 50f;
			float childX = x;
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			string[] ports = definition != null ? VfxContextNodes.GetFlowOutputNames(graph, node) : Array.Empty<string>();
			for (int port = 0; port < ports.Length; port++)
			{
				float childY = bottom;
				foreach (VfxFlowEdge edge in graph.GetFlowOutputs(node.Id, port))
				{
					VfxNodeData child = graph.FindNode(edge.To);
					if (child == null || placed.Contains(child.Id))
					{
						continue;
					}

					// Port 0 continues the column; event ports start a column to the right.
					float cx = port == 0 ? childX : x + (ContextWidth + 120f) * port;
					float end = PlaceChain(graph, child, cx, port == 0 ? childY : y, placed, heights, depth + 1);
					if (port == 0)
					{
						childY = end;
						childX += 0f;
					}
				}

				if (port == 0)
				{
					bottom = Math.Max(bottom, childY);
				}
			}

			return bottom;
		}

		private static int OperatorDepth(VfxGraph graph, VfxNodeData node, int depth)
		{
			if (depth > 16)
			{
				return depth;
			}

			int result = 0;
			foreach (VfxEdge edge in graph.Edges)
			{
				if (edge.From != node.Id)
				{
					continue;
				}

				VfxNodeData target = graph.FindNode(edge.To);
				VfxNodeDefinition definition = target != null ? VfxNodeLibrary.Find(target.Type) : null;
				if (definition != null && definition.Kind == VfxNodeKind.Operator)
				{
					result = Math.Max(result, OperatorDepth(graph, target, depth + 1) + 1);
				}
			}

			return result;
		}

		/// <summary>Selects one node (the inspector shows it).</summary>
		public void Select(ulong id, bool additive = false)
		{
			if (!additive)
			{
				Selected.Clear();
			}

			if (id != 0)
			{
				Selected.Add(id);
				Primary = id;
			}
			else if (!additive)
			{
				Primary = 0;
			}
		}

		public void ClearSelection()
		{
			Selected.Clear();
			Primary = 0;
		}

		/// <summary>Deletes the selected nodes (blocks and contexts with their blocks).</summary>
		public void DeleteSelected()
		{
			if (Document == null || Selected.Count == 0)
			{
				return;
			}

			Document.Edit("Delete nodes", () =>
			{
				foreach (ulong id in new List<ulong>(Selected))
				{
					Document.Graph.RemoveNode(Document.Graph.FindNode(id));
				}
			});
			ClearSelection();
		}

		/// <summary>Duplicates the selected nodes (operators and contexts with their blocks; blocks inside their context).</summary>
		public void DuplicateSelected()
		{
			if (Document == null || Selected.Count == 0)
			{
				return;
			}

			var created = new List<ulong>();
			Document.Edit("Duplicate nodes", () =>
			{
				VfxGraph graph = Document.Graph;
				var mapping = new Dictionary<ulong, ulong>();
				foreach (ulong id in new List<ulong>(Selected))
				{
					VfxNodeData node = graph.FindNode(id);
					if (node == null)
					{
						continue;
					}

					VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
					if (definition == null)
					{
						continue;
					}

					if (definition.Kind == VfxNodeKind.Block)
					{
						VfxNodeData context = graph.FindNode(node.Parent);
						if (context == null)
						{
							continue;
						}

						VfxNodeData copy = CloneNode(graph, node);
						copy.Parent = context.Id;
						int index = context.Blocks.IndexOf(node.Id);
						context.Blocks.Insert(Math.Min(context.Blocks.Count, index + 1), copy.Id);
						mapping[node.Id] = copy.Id;
						created.Add(copy.Id);
						continue;
					}

					VfxNodeData clone = CloneNode(graph, node);
					clone.Position += new Vector2(40f, 40f);
					mapping[node.Id] = clone.Id;
					created.Add(clone.Id);
					if (definition.Kind == VfxNodeKind.Context)
					{
						clone.Blocks.Clear();
						foreach (VfxNodeData block in graph.GetBlocks(node))
						{
							VfxNodeData blockCopy = CloneNode(graph, block);
							blockCopy.Parent = clone.Id;
							clone.Blocks.Add(blockCopy.Id);
							mapping[block.Id] = blockCopy.Id;
						}
					}
				}

				// Edges among the copied nodes.
				foreach (VfxEdge edge in new List<VfxEdge>(graph.Edges))
				{
					ulong from;
					ulong to;
					if (mapping.TryGetValue(edge.From, out from) && mapping.TryGetValue(edge.To, out to))
					{
						graph.Edges.Add(new VfxEdge(from, edge.FromSlot, to, edge.ToSlot));
					}
					else if (mapping.TryGetValue(edge.To, out to))
					{
						// Inputs from nodes outside the copy keep their source.
						graph.Edges.Add(new VfxEdge(edge.From, edge.FromSlot, to, edge.ToSlot));
					}
				}
			});

			ClearSelection();
			foreach (ulong id in created)
			{
				Selected.Add(id);
				Primary = id;
			}
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- coordinates

		private NVector2 ToScreen(Vector2 graph)
		{
			return canvasMin + (new NVector2(graph.X, graph.Y) + Offset) * Zoom;
		}

		private NVector2 ToScreen(NVector2 graph)
		{
			return canvasMin + (graph + Offset) * Zoom;
		}

		private Vector2 ToGraph(NVector2 screen)
		{
			NVector2 g = (screen - canvasMin) / Zoom - Offset;
			return new Vector2(g.X, g.Y);
		}

		private float S(float graphUnits)
		{
			return graphUnits * Zoom;
		}

		// ---------------------------------------------------------------- layout

		private void ComputeLayouts()
		{
			layouts.Clear();
			ordered.Clear();
			VfxGraph graph = Document.Graph;
			foreach (VfxNodeData node in graph.Nodes)
			{
				if (node.Parent != 0)
				{
					continue;
				}

				VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
				if (definition == null)
				{
					continue;
				}

				NodeLayout layout = definition.Kind == VfxNodeKind.Context ? LayoutContext(graph, node, definition) : LayoutOperator(node, definition);
				layouts[node.Id] = layout;
				ordered.Add(layout);
			}

			// Selected nodes on top.
			ordered.Sort((a, b) => Selected.Contains(a.Node.Id).CompareTo(Selected.Contains(b.Node.Id)));
		}

		private NodeLayout LayoutOperator(VfxNodeData node, VfxNodeDefinition definition)
		{
			var layout = new NodeLayout { Node = node, Definition = definition };
			List<VfxSlotDefinition> inputs = definition.GetInputs(node);
			List<VfxSlotDefinition> outputs = definition.GetOutputs(node);
			float width = OperatorWidth;
			float titleWidth = ImGui.CalcTextSize(definition.GetTitle(node)).X / Zoom + 24f;
			width = Math.Max(width, titleWidth);
			foreach (VfxSlotDefinition slot in inputs)
			{
				string preview = IsConnected(node, slot.Name) ? "" : "  " + VfxValueEditors.Preview(node.GetInput(slot.Name));
				width = Math.Max(width, ImGui.CalcTextSize(slot.DisplayName + preview).X / Zoom + 30f);
			}

			foreach (VfxSlotDefinition slot in outputs)
			{
				width = Math.Max(width, ImGui.CalcTextSize(slot.DisplayName).X / Zoom + 30f);
			}

			width = Math.Min(width, 320f);
			int rows = Math.Max(inputs.Count, outputs.Count);
			float height = HeaderHeight + rows * RowHeight + 8f;
			NVector2 min = ToScreen(node.Position);
			layout.Min = min;
			layout.Max = min + new NVector2(S(width), S(height));
			layout.HeaderHeight = S(HeaderHeight);
			for (int i = 0; i < inputs.Count; i++)
			{
				float y = HeaderHeight + 4f + i * RowHeight + RowHeight * 0.5f;
				layout.Ports.Add(new PortLayout { Node = node, Slot = inputs[i].Name, Type = inputs[i].Type, Input = true, Center = min + new NVector2(0f, S(y)) });
			}

			for (int i = 0; i < outputs.Count; i++)
			{
				float y = HeaderHeight + 4f + i * RowHeight + RowHeight * 0.5f;
				layout.Ports.Add(new PortLayout { Node = node, Slot = outputs[i].Name, Type = outputs[i].Type, Input = false, Center = min + new NVector2(S(width), S(y)) });
			}

			return layout;
		}

		private NodeLayout LayoutContext(VfxGraph graph, VfxNodeData node, VfxNodeDefinition definition)
		{
			var layout = new NodeLayout { Node = node, Definition = definition };
			List<VfxSlotDefinition> inputs = definition.GetInputs(node);
			NVector2 min = ToScreen(node.Position);
			layout.Min = min;
			layout.HeaderHeight = S(HeaderHeight);
			float y = HeaderHeight + 4f;
			for (int i = 0; i < inputs.Count; i++)
			{
				layout.Ports.Add(new PortLayout { Node = node, Slot = inputs[i].Name, Type = inputs[i].Type, Input = true, Center = min + new NVector2(0f, S(y + RowHeight * 0.5f)) });
				y += RowHeight;
			}

			y += 4f;
			foreach (VfxNodeData block in graph.GetBlocks(node))
			{
				VfxNodeDefinition blockDefinition = VfxNodeLibrary.Find(block.Type);
				if (blockDefinition == null)
				{
					continue;
				}

				var blockLayout = new BlockLayout { Node = block, Min = min + new NVector2(S(6f), S(y)) };
				float blockHeight = BlockHeaderHeight;
				List<VfxSlotDefinition> blockInputs = blockDefinition.GetInputs(block);
				float rowY = y + BlockHeaderHeight;
				foreach (VfxSlotDefinition slot in blockInputs)
				{
					layout.Ports.Add(new PortLayout { Node = block, Slot = slot.Name, Type = slot.Type, Input = true, Center = min + new NVector2(0f, S(rowY + BlockRowHeight * 0.5f)) });
					rowY += BlockRowHeight;
					blockHeight += BlockRowHeight;
				}

				blockHeight += 4f;
				blockLayout.Max = min + new NVector2(S(ContextWidth - 6f), S(y + blockHeight));
				blockLayout.EnableMin = min + new NVector2(S(10f), S(y + 4f));
				blockLayout.EnableMax = min + new NVector2(S(24f), S(y + 18f));
				layout.Blocks.Add(blockLayout);
				y += blockHeight + 3f;
			}

			// The "+" row to add a block.
			layout.AddBlockMin = min + new NVector2(S(6f), S(y));
			layout.AddBlockMax = min + new NVector2(S(ContextWidth - 6f), S(y + RowHeight));
			y += RowHeight + 8f;
			layout.Max = min + new NVector2(S(ContextWidth), S(y));

			// Flow ports along the top and bottom edges.
			for (int i = 0; i < definition.FlowInputs.Length; i++)
			{
				layout.FlowInputs.Add(min + new NVector2(S(24f + i * 40f), 0f));
			}

			layout.FlowOutputNames = VfxContextNodes.GetFlowOutputNames(graph, node);
			for (int i = 0; i < layout.FlowOutputNames.Length; i++)
			{
				layout.FlowOutputs.Add(new NVector2(min.X + S(24f + i * 40f), layout.Max.Y));
			}

			return layout;
		}

		private bool IsConnected(VfxNodeData node, string slot)
		{
			return Document.Graph.FindInputEdge(node.Id, slot) != null;
		}

		// ---------------------------------------------------------------- drawing

		private void DrawGrid(ImDrawListPtr draw)
		{
			float step = 32f * Zoom;
			if (step < 6f)
			{
				return;
			}

			uint color = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.04f));
			float startX = canvasMin.X + (Offset.X * Zoom % step + step) % step;
			for (float x = startX; x < canvasMin.X + canvasSize.X; x += step)
			{
				draw.AddLine(new NVector2(x, canvasMin.Y), new NVector2(x, canvasMin.Y + canvasSize.Y), color);
			}

			float startY = canvasMin.Y + (Offset.Y * Zoom % step + step) % step;
			for (float y = startY; y < canvasMin.Y + canvasSize.Y; y += step)
			{
				draw.AddLine(new NVector2(canvasMin.X, y), new NVector2(canvasMin.X + canvasSize.X, y), color);
			}
		}

		private static NVector4 ContextColor(VfxContextKind kind)
		{
			switch (kind)
			{
				case VfxContextKind.Event: return new NVector4(0.55f, 0.35f, 0.75f, 1f);
				case VfxContextKind.Spawn: return new NVector4(0.2f, 0.55f, 0.6f, 1f);
				case VfxContextKind.Initialize: return new NVector4(0.3f, 0.6f, 0.3f, 1f);
				case VfxContextKind.Update: return new NVector4(0.75f, 0.5f, 0.2f, 1f);
				case VfxContextKind.Output: return new NVector4(0.3f, 0.45f, 0.75f, 1f);
				default: return new NVector4(0.4f, 0.4f, 0.45f, 1f);
			}
		}

		public static NVector4 TypeColor(VfxValueType type)
		{
			switch (type)
			{
				case VfxValueType.Float: return new NVector4(0.55f, 0.8f, 0.55f, 1f);
				case VfxValueType.Int:
				case VfxValueType.Uint:
					return new NVector4(0.5f, 0.7f, 0.9f, 1f);
				case VfxValueType.Bool: return new NVector4(0.85f, 0.5f, 0.5f, 1f);
				case VfxValueType.Vector2: return new NVector4(0.9f, 0.85f, 0.4f, 1f);
				case VfxValueType.Vector3: return new NVector4(0.95f, 0.75f, 0.3f, 1f);
				case VfxValueType.Vector4: return new NVector4(0.95f, 0.6f, 0.3f, 1f);
				case VfxValueType.Color: return new NVector4(0.9f, 0.4f, 0.8f, 1f);
				case VfxValueType.Gradient: return new NVector4(0.8f, 0.45f, 0.95f, 1f);
				case VfxValueType.Curve: return new NVector4(0.4f, 0.85f, 0.9f, 1f);
				case VfxValueType.Texture2D:
				case VfxValueType.Texture3D:
					return new NVector4(0.6f, 0.6f, 0.95f, 1f);
				case VfxValueType.Mesh: return new NVector4(0.95f, 0.95f, 0.7f, 1f);
				case VfxValueType.Matrix: return new NVector4(0.7f, 0.7f, 0.7f, 1f);
				default: return new NVector4(0.7f, 0.7f, 0.7f, 1f);
			}
		}

		private void DrawNode(ImDrawListPtr draw, NodeLayout layout)
		{
			VfxNodeData node = layout.Node;
			VfxNodeDefinition definition = layout.Definition;
			bool selected = Selected.Contains(node.Id);
			bool isContext = definition.Kind == VfxNodeKind.Context;
			float rounding = S(5f);
			uint body = ImGui.GetColorU32(new NVector4(0.2f, 0.2f, 0.22f, 0.97f));
			NVector4 headerColor = isContext ? ContextColor(definition.ContextKind) : node.Type == VfxNodeLibrary.PropertyNodeType ? new NVector4(0.6f, 0.55f, 0.25f, 1f) : new NVector4(0.32f, 0.34f, 0.4f, 1f);
			draw.AddRectFilled(layout.Min, layout.Max, body, rounding);
			draw.AddRectFilled(layout.Min, new NVector2(layout.Max.X, layout.Min.Y + layout.HeaderHeight), ImGui.GetColorU32(headerColor), rounding, ImDrawFlags.RoundCornersTop);
			uint outline = ErrorNodes.Contains(node.Id) ? ImGui.GetColorU32(EditorStyle.Error) : selected ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.6f));
			draw.AddRect(layout.Min, layout.Max, outline, rounding, ImDrawFlags.None, selected || ErrorNodes.Contains(node.Id) ? 2f : 1f);

			string title = definition.GetTitle(node);
			draw.AddText(layout.Min + new NVector2(S(10f), S(5f)), ImGui.GetColorU32(ImGuiCol.Text), title);
			if (isContext && definition.ContextKind == VfxContextKind.Initialize)
			{
				string capacity = "cap " + node.GetInt("Capacity", 1024);
				NVector2 size = ImGui.CalcTextSize(capacity);
				draw.AddText(new NVector2(layout.Max.X - size.X - S(8f), layout.Min.Y + S(5f)), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.7f)), capacity);
			}

			// Slot rows.
			List<VfxSlotDefinition> inputs = definition.GetInputs(node);
			List<VfxSlotDefinition> outputs = definition.GetOutputs(node);
			uint text = ImGui.GetColorU32(ImGuiCol.Text);
			uint dim = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.55f));
			foreach (PortLayout port in layout.Ports)
			{
				if (port.Node != node)
				{
					continue;
				}

				DrawPort(draw, port);
				VfxSlotDefinition slot = port.Input ? FindSlot(inputs, port.Slot) : FindSlot(outputs, port.Slot);
				string label = slot?.DisplayName ?? port.Slot;
				NVector2 textSize = ImGui.CalcTextSize(label);
				if (port.Input)
				{
					draw.AddText(port.Center + new NVector2(S(10f), -textSize.Y * 0.5f), text, label);
					if (!IsConnected(node, port.Slot) && !isContext)
					{
						string preview = VfxValueEditors.Preview(node.GetInput(port.Slot));
						draw.AddText(port.Center + new NVector2(S(14f) + textSize.X, -textSize.Y * 0.5f), dim, preview);
					}
					else if (!IsConnected(node, port.Slot))
					{
						string preview = VfxValueEditors.Preview(node.GetInput(port.Slot));
						draw.AddText(port.Center + new NVector2(S(14f) + textSize.X, -textSize.Y * 0.5f), dim, preview);
					}
				}
				else
				{
					draw.AddText(port.Center - new NVector2(S(10f) + textSize.X, textSize.Y * 0.5f), text, label);
				}
			}

			if (!isContext)
			{
				return;
			}

			// Blocks.
			foreach (BlockLayout block in layout.Blocks)
			{
				VfxNodeDefinition blockDefinition = VfxNodeLibrary.Find(block.Node.Type);
				bool blockSelected = Selected.Contains(block.Node.Id);
				uint blockBody = ImGui.GetColorU32(block.Node.Enabled ? new NVector4(0.26f, 0.27f, 0.3f, 1f) : new NVector4(0.22f, 0.22f, 0.23f, 1f));
				draw.AddRectFilled(block.Min, block.Max, blockBody, S(3f));
				uint blockOutline = ErrorNodes.Contains(block.Node.Id) ? ImGui.GetColorU32(EditorStyle.Error) : blockSelected ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.35f));
				draw.AddRect(block.Min, block.Max, blockOutline, S(3f), ImDrawFlags.None, blockSelected ? 2f : 1f);

				// Enable box.
				draw.AddRect(block.EnableMin, block.EnableMax, dim, S(2f));
				if (block.Node.Enabled)
				{
					draw.AddRectFilled(block.EnableMin + new NVector2(S(3f)), block.EnableMax - new NVector2(S(3f)), ImGui.GetColorU32(new NVector4(0.4f, 0.65f, 1f, 1f)));
				}

				string blockTitle = blockDefinition?.GetTitle(block.Node) ?? block.Node.Type;
				draw.AddText(block.Min + new NVector2(S(24f), S(3f)), block.Node.Enabled ? text : dim, blockTitle);
				List<VfxSlotDefinition> blockInputs = blockDefinition?.GetInputs(block.Node) ?? new List<VfxSlotDefinition>();
				foreach (PortLayout port in layout.Ports)
				{
					if (port.Node != block.Node)
					{
						continue;
					}

					DrawPort(draw, port);
					VfxSlotDefinition slot = FindSlot(blockInputs, port.Slot);
					string label = slot?.DisplayName ?? port.Slot;
					NVector2 textSize = ImGui.CalcTextSize(label);
					draw.AddText(new NVector2(block.Min.X + S(20f), port.Center.Y - textSize.Y * 0.5f), text, label);
					if (!IsConnected(block.Node, port.Slot))
					{
						string preview = VfxValueEditors.Preview(block.Node.GetInput(port.Slot));
						NVector2 previewSize = ImGui.CalcTextSize(preview);
						draw.AddText(new NVector2(block.Max.X - S(8f) - previewSize.X, port.Center.Y - textSize.Y * 0.5f), dim, preview);
					}
				}
			}

			// Add block row.
			draw.AddRectFilled(layout.AddBlockMin, layout.AddBlockMax, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.05f)), S(3f));
			string add = "+ Add Block";
			NVector2 addSize = ImGui.CalcTextSize(add);
			draw.AddText(new NVector2((layout.AddBlockMin.X + layout.AddBlockMax.X - addSize.X) * 0.5f, layout.AddBlockMin.Y + S(2f)), dim, add);

			// Flow ports.
			for (int i = 0; i < layout.FlowInputs.Count; i++)
			{
				DrawFlowPort(draw, layout.FlowInputs[i], true, definition.FlowInputs[i], headerColor);
			}

			for (int i = 0; i < layout.FlowOutputs.Count; i++)
			{
				DrawFlowPort(draw, layout.FlowOutputs[i], false, layout.FlowOutputNames[i], headerColor);
			}
		}

		private void DrawPort(ImDrawListPtr draw, PortLayout port)
		{
			uint color = ImGui.GetColorU32(TypeColor(port.Type));
			bool connected = port.Input ? IsConnected(port.Node, port.Slot) : HasOutputEdge(port.Node, port.Slot);
			if (connected)
			{
				draw.AddCircleFilled(port.Center, S(PortRadius), color);
			}
			else
			{
				draw.AddCircleFilled(port.Center, S(PortRadius), ImGui.GetColorU32(new NVector4(0.15f, 0.15f, 0.16f, 1f)));
				draw.AddCircle(port.Center, S(PortRadius), color, 0, 1.5f);
			}
		}

		private void DrawFlowPort(ImDrawListPtr draw, NVector2 center, bool input, string name, NVector4 color)
		{
			var min = new NVector2(center.X - S(FlowPortWidth) * 0.5f, center.Y - S(FlowPortHeight) * 0.5f);
			var max = new NVector2(center.X + S(FlowPortWidth) * 0.5f, center.Y + S(FlowPortHeight) * 0.5f);
			draw.AddRectFilled(min, max, ImGui.GetColorU32(color), S(2f));
			draw.AddRect(min, max, ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.7f)), S(2f));
			if (!input)
			{
				NVector2 size = ImGui.CalcTextSize(name);
				draw.AddText(new NVector2(center.X - size.X * 0.5f, max.Y + S(2f)), ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.6f)), name);
			}
		}

		private bool HasOutputEdge(VfxNodeData node, string slot)
		{
			foreach (VfxEdge edge in Document.Graph.Edges)
			{
				if (edge.From == node.Id && edge.FromSlot == slot)
				{
					return true;
				}
			}

			return false;
		}

		private static VfxSlotDefinition FindSlot(List<VfxSlotDefinition> slots, string name)
		{
			foreach (VfxSlotDefinition slot in slots)
			{
				if (slot.Name == name)
				{
					return slot;
				}
			}

			return null;
		}

		private PortLayout FindPort(ulong nodeId, string slot, bool input)
		{
			foreach (NodeLayout layout in ordered)
			{
				foreach (PortLayout port in layout.Ports)
				{
					if (port.Node.Id == nodeId && port.Slot == slot && port.Input == input)
					{
						return port;
					}
				}
			}

			return null;
		}

		private void DrawEdges(ImDrawListPtr draw)
		{
			VfxGraph graph = Document.Graph;
			foreach (VfxEdge edge in graph.Edges)
			{
				PortLayout from = FindPort(edge.From, edge.FromSlot, false);
				PortLayout to = FindPort(edge.To, edge.ToSlot, true);
				if (from == null || to == null)
				{
					continue;
				}

				DrawBezier(draw, from.Center, to.Center, ImGui.GetColorU32(TypeColor(from.Type)), 2f);
			}

			foreach (VfxFlowEdge edge in graph.Flow)
			{
				NodeLayout from;
				NodeLayout to;
				if (!layouts.TryGetValue(edge.From, out from) || !layouts.TryGetValue(edge.To, out to))
				{
					continue;
				}

				if (edge.FromPort >= from.FlowOutputs.Count || edge.ToPort >= to.FlowInputs.Count)
				{
					continue;
				}

				DrawFlowBezier(draw, from.FlowOutputs[edge.FromPort], to.FlowInputs[edge.ToPort], ImGui.GetColorU32(new NVector4(0.85f, 0.85f, 0.9f, 0.9f)), 3f);
			}
		}

		private void DrawBezier(ImDrawListPtr draw, NVector2 from, NVector2 to, uint color, float thickness)
		{
			float tangent = Math.Max(S(40f), Math.Abs(to.X - from.X) * 0.4f);
			draw.AddBezierCubic(from, from + new NVector2(tangent, 0f), to - new NVector2(tangent, 0f), to, color, thickness);
		}

		private void DrawFlowBezier(ImDrawListPtr draw, NVector2 from, NVector2 to, uint color, float thickness)
		{
			float tangent = Math.Max(S(30f), Math.Abs(to.Y - from.Y) * 0.4f);
			draw.AddBezierCubic(from, from + new NVector2(0f, tangent), to - new NVector2(0f, tangent), to, color, thickness);
		}

		private void DrawDragConnection(ImDrawListPtr draw)
		{
			NVector2 mouse = ImGui.GetMousePos();
			if (dragPort != null)
			{
				uint color = ImGui.GetColorU32(TypeColor(dragPort.Type));
				if (dragPort.Input)
				{
					DrawBezier(draw, mouse, dragPort.Center, color, 2f);
				}
				else
				{
					DrawBezier(draw, dragPort.Center, mouse, color, 2f);
				}
			}
			else if (dragFlow)
			{
				NodeLayout layout;
				if (layouts.TryGetValue(dragFlowNode.Id, out layout))
				{
					NVector2 from = dragFlowIsOutput ? layout.FlowOutputs[dragFlowPort] : layout.FlowInputs[dragFlowPort];
					if (dragFlowIsOutput)
					{
						DrawFlowBezier(draw, from, mouse, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.8f)), 3f);
					}
					else
					{
						DrawFlowBezier(draw, mouse, from, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.8f)), 3f);
					}
				}
			}
		}

		// ---------------------------------------------------------------- hit testing

		private Hit HitTest(NVector2 p)
		{
			for (int i = ordered.Count - 1; i >= 0; i--)
			{
				NodeLayout layout = ordered[i];
				foreach (PortLayout port in layout.Ports)
				{
					if (NVector2.DistanceSquared(p, port.Center) <= S(PortRadius + 4f) * S(PortRadius + 4f))
					{
						return new Hit { Kind = port.Input ? HitKind.InputPort : HitKind.OutputPort, Node = port.Node, Slot = port.Slot };
					}
				}

				for (int f = 0; f < layout.FlowInputs.Count; f++)
				{
					if (InFlowPort(p, layout.FlowInputs[f]))
					{
						return new Hit { Kind = HitKind.FlowInput, Node = layout.Node, Port = f };
					}
				}

				for (int f = 0; f < layout.FlowOutputs.Count; f++)
				{
					if (InFlowPort(p, layout.FlowOutputs[f]))
					{
						return new Hit { Kind = HitKind.FlowOutput, Node = layout.Node, Port = f };
					}
				}

				if (!Inside(p, layout.Min, layout.Max))
				{
					continue;
				}

				foreach (BlockLayout block in layout.Blocks)
				{
					if (Inside(p, block.EnableMin, block.EnableMax))
					{
						return new Hit { Kind = HitKind.BlockEnable, Node = block.Node };
					}

					if (Inside(p, block.Min, block.Max))
					{
						return new Hit { Kind = HitKind.Block, Node = block.Node };
					}
				}

				if (layout.Definition.Kind == VfxNodeKind.Context && Inside(p, layout.AddBlockMin, layout.AddBlockMax))
				{
					return new Hit { Kind = HitKind.AddBlock, Node = layout.Node };
				}

				return new Hit { Kind = HitKind.Node, Node = layout.Node };
			}

			return new Hit { Kind = HitKind.None };
		}

		private bool InFlowPort(NVector2 p, NVector2 center)
		{
			return Math.Abs(p.X - center.X) <= S(FlowPortWidth) * 0.5f + 3f && Math.Abs(p.Y - center.Y) <= S(FlowPortHeight) * 0.5f + 5f;
		}

		private static bool Inside(NVector2 p, NVector2 min, NVector2 max)
		{
			return p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y;
		}

		// ---------------------------------------------------------------- input

		private void HandleInput()
		{
			ImGuiIOPtr io = ImGui.GetIO();
			NVector2 mouse = ImGui.GetMousePos();
			VfxGraph graph = Document.Graph;

			// Zoom around the cursor.
			if (hovered && Math.Abs(io.MouseWheel) > 0f)
			{
				Vector2 before = ToGraph(mouse);
				Zoom = Math.Clamp(Zoom * (io.MouseWheel > 0f ? 1.1f : 1f / 1.1f), 0.2f, 2.5f);
				Vector2 after = ToGraph(mouse);
				Offset += new NVector2(after.X - before.X, after.Y - before.Y);
			}

			// Pan: middle button, or the right button dragged.
			if (hovered && (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
			{
				panning = ImGui.IsMouseClicked(ImGuiMouseButton.Middle);
				panStart = mouse;
				panOffsetStart = Offset;
				rightPress = mouse;
			}

			if (ImGui.IsMouseDown(ImGuiMouseButton.Middle) && panning)
			{
				Offset = panOffsetStart + (mouse - panStart) / Zoom;
			}
			else if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && NVector2.DistanceSquared(mouse, rightPress) > 36f && !openCreatePopup)
			{
				panning = true;
				Offset = panOffsetStart + (mouse - panStart) / Zoom;
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle))
			{
				panning = false;
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				bool moved = NVector2.DistanceSquared(mouse, rightPress) > 36f;
				panning = false;
				if (hovered && !moved)
				{
					Hit hit = HitTest(mouse);
					if (hit.Kind == HitKind.None)
					{
						OpenCreateMenu(mouse, null);
					}
					else if (hit.Kind == HitKind.AddBlock)
					{
						OpenBlockMenu(hit.Node);
					}
					else if (hit.Node != null)
					{
						if (!Selected.Contains(hit.Node.Id))
						{
							Select(hit.Node.Id);
						}

						contextMenuNode = hit.Node;
						openNodeContextMenu = true;
					}
				}
			}

			// Left button.
			if (hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				Hit hit = HitTest(mouse);
				bool additive = io.KeyCtrl || io.KeyShift;
				switch (hit.Kind)
				{
					case HitKind.InputPort:
					case HitKind.OutputPort:
						dragPort = FindPort(hit.Node.Id, hit.Slot, hit.Kind == HitKind.InputPort);
						break;
					case HitKind.FlowInput:
					case HitKind.FlowOutput:
						dragFlow = true;
						dragFlowNode = hit.Node;
						dragFlowPort = hit.Port;
						dragFlowIsOutput = hit.Kind == HitKind.FlowOutput;
						break;
					case HitKind.BlockEnable:
						Document.Edit((hit.Node.Enabled ? "Disable " : "Enable ") + "block", () => hit.Node.Enabled = !hit.Node.Enabled);
						break;
					case HitKind.AddBlock:
						OpenBlockMenu(hit.Node);
						break;
					case HitKind.Block:
						if (additive)
						{
							if (!Selected.Remove(hit.Node.Id))
							{
								Selected.Add(hit.Node.Id);
								Primary = hit.Node.Id;
							}
						}
						else if (!Selected.Contains(hit.Node.Id))
						{
							Select(hit.Node.Id);
						}
						else
						{
							Primary = hit.Node.Id;
						}

						draggingBlock = hit.Node;
						break;
					case HitKind.Node:
						if (additive)
						{
							if (!Selected.Remove(hit.Node.Id))
							{
								Selected.Add(hit.Node.Id);
								Primary = hit.Node.Id;
							}
						}
						else if (!Selected.Contains(hit.Node.Id))
						{
							Select(hit.Node.Id);
						}
						else
						{
							Primary = hit.Node.Id;
						}

						BeginNodeDrag();
						break;
					default:
						if (!additive)
						{
							ClearSelection();
						}

						boxSelecting = true;
						boxStart = mouse;
						break;
				}
			}

			if (draggingNodes && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				NVector2 delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left) / Zoom;
				if (delta.LengthSquared() > 0f)
				{
					foreach (KeyValuePair<ulong, Vector2> entry in dragStart)
					{
						VfxNodeData node = graph.FindNode(entry.Key);
						if (node != null)
						{
							node.Position = entry.Value + new Vector2(delta.X, delta.Y);
						}
					}

					nodesMoved = true;
				}
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				if (draggingNodes)
				{
					draggingNodes = false;
					if (nodesMoved)
					{
						// The positions already changed: record the move as from the start positions.
						var moved = new Dictionary<ulong, Vector2>();
						foreach (KeyValuePair<ulong, Vector2> entry in dragStart)
						{
							VfxNodeData node = graph.FindNode(entry.Key);
							if (node != null)
							{
								moved[entry.Key] = node.Position;
								node.Position = entry.Value;
							}
						}

						Document.EditLayout("Move nodes", () =>
						{
							foreach (KeyValuePair<ulong, Vector2> entry in moved)
							{
								VfxNodeData node = graph.FindNode(entry.Key);
								if (node != null)
								{
									node.Position = entry.Value;
								}
							}
						});
					}

					dragStart.Clear();
					nodesMoved = false;
				}

				if (dragPort != null)
				{
					EndPortDrag(mouse);
				}

				if (dragFlow)
				{
					EndFlowDrag(mouse);
				}

				if (draggingBlock != null)
				{
					EndBlockDrag(mouse);
				}

				if (boxSelecting)
				{
					boxSelecting = false;
					NVector2 a = NVector2.Min(boxStart, mouse);
					NVector2 b = NVector2.Max(boxStart, mouse);
					if (NVector2.DistanceSquared(a, b) > 16f)
					{
						foreach (NodeLayout layout in ordered)
						{
							if (layout.Min.X >= a.X && layout.Max.X <= b.X && layout.Min.Y >= a.Y && layout.Max.Y <= b.Y)
							{
								Selected.Add(layout.Node.Id);
								Primary = layout.Node.Id;
							}
						}
					}
				}
			}

			// Keys.
			if (focused && hovered && !io.WantTextInput)
			{
				if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
				{
					DeleteSelected();
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
				{
					DuplicateSelected();
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.F, false))
				{
					FrameAll();
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false))
				{
					foreach (NodeLayout layout in ordered)
					{
						Selected.Add(layout.Node.Id);
					}
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.Space, false))
				{
					OpenCreateMenu(mouse, null);
				}
			}
		}

		private void BeginNodeDrag()
		{
			draggingNodes = true;
			nodesMoved = false;
			dragStart.Clear();
			foreach (ulong id in Selected)
			{
				VfxNodeData node = Document.Graph.FindNode(id);
				if (node != null && node.Parent == 0)
				{
					dragStart[id] = node.Position;
				}
			}
		}

		private void EndPortDrag(NVector2 mouse)
		{
			PortLayout source = dragPort;
			dragPort = null;
			Hit hit = HitTest(mouse);
			VfxGraph graph = Document.Graph;
			if (hit.Kind == HitKind.InputPort || hit.Kind == HitKind.OutputPort)
			{
				PortLayout target = FindPort(hit.Node.Id, hit.Slot, hit.Kind == HitKind.InputPort);
				if (target != null && target.Input != source.Input && target.Node != source.Node)
				{
					PortLayout output = source.Input ? target : source;
					PortLayout input = source.Input ? source : target;
					TryConnect(output, input);
				}

				return;
			}

			if (hit.Kind == HitKind.None)
			{
				if (source.Input && IsConnected(source.Node, source.Slot))
				{
					Document.Edit("Disconnect", () => graph.Disconnect(source.Node, source.Slot));
				}
				else
				{
					// Released on the canvas: offer nodes to connect to.
					OpenCreateMenu(mouse, source);
				}
			}
		}

		/// <summary>Connects an output to an input when the types allow it (widening a typed operator's slots when needed).</summary>
		private void TryConnect(PortLayout output, PortLayout input)
		{
			VfxGraph graph = Document.Graph;
			VfxNodeData target = input.Node;
			VfxNodeDefinition targetDefinition = VfxNodeLibrary.Find(target.Type);
			if (!VfxValueTypes.CanConvert(output.Type, input.Type))
			{
				// A wider numeric value into a typed operator: switch the operator's type.
				VfxSettingDefinition typeSetting = targetDefinition?.FindSetting("Type");
				if (typeSetting != null && typeSetting.Options != null && Array.IndexOf(typeSetting.Options, output.Type.ToString()) >= 0 && VfxValueTypes.IsNumeric(output.Type) && VfxValueTypes.IsNumeric(input.Type))
				{
					Document.Edit("Connect", () =>
					{
						target.SetSetting("Type", output.Type.ToString());
						new VfxGraphBuilder(graph).RebuildSlots(target);
						graph.Connect(output.Node, output.Slot, target, input.Slot);
					});
				}

				return;
			}

			bool widen = false;
			VfxSettingDefinition type = targetDefinition?.FindSetting("Type");
			if (type != null && type.Options != null && VfxValueTypes.IsNumeric(output.Type) && VfxValueTypes.IsNumeric(input.Type) && output.Type != VfxValueType.Matrix
				&& VfxValueTypes.Components(output.Type) > VfxValueTypes.Components(input.Type) && Array.IndexOf(type.Options, output.Type.ToString()) >= 0)
			{
				widen = true;
			}

			Document.Edit("Connect", () =>
			{
				if (widen)
				{
					target.SetSetting("Type", output.Type.ToString());
					new VfxGraphBuilder(graph).RebuildSlots(target);
				}

				graph.Connect(output.Node, output.Slot, target, input.Slot);
			});
		}

		private void EndFlowDrag(NVector2 mouse)
		{
			dragFlow = false;
			Hit hit = HitTest(mouse);
			VfxGraph graph = Document.Graph;
			if ((hit.Kind == HitKind.FlowInput && dragFlowIsOutput) || (hit.Kind == HitKind.FlowOutput && !dragFlowIsOutput))
			{
				VfxNodeData from = dragFlowIsOutput ? dragFlowNode : hit.Node;
				int fromPort = dragFlowIsOutput ? dragFlowPort : hit.Port;
				VfxNodeData to = dragFlowIsOutput ? hit.Node : dragFlowNode;
				int toPort = dragFlowIsOutput ? hit.Port : dragFlowPort;
				if (from != to && FlowAllowed(from, fromPort, to, toPort))
				{
					Document.Edit("Connect flow", () => graph.ConnectFlow(from, fromPort, to, toPort));
				}

				return;
			}

			if (hit.Kind == HitKind.None)
			{
				// Dragging an input port away removes its connections; an output port offers a new context.
				if (!dragFlowIsOutput)
				{
					List<VfxFlowEdge> edges = graph.GetFlowInputs(dragFlowNode.Id, dragFlowPort);
					if (edges.Count > 0)
					{
						Document.Edit("Disconnect flow", () =>
						{
							foreach (VfxFlowEdge edge in edges)
							{
								graph.DisconnectFlow(edge);
							}
						});
						return;
					}
				}

				pendingFlowNode = dragFlowNode;
				pendingFlowPort = dragFlowPort;
				pendingFlowIsOutput = dragFlowIsOutput;
				OpenCreateMenu(mouse, null, true);
			}
		}

		/// <summary>Which contexts may feed which: events to spawn ports, spawn to initialize, initialize to update / output, update to output and (its event ports) to initialize.</summary>
		public static bool FlowAllowed(VfxNodeData from, int fromPort, VfxNodeData to, int toPort)
		{
			VfxNodeDefinition a = VfxNodeLibrary.Find(from.Type);
			VfxNodeDefinition b = VfxNodeLibrary.Find(to.Type);
			if (a == null || b == null)
			{
				return false;
			}

			switch (a.ContextKind)
			{
				case VfxContextKind.Event: return b.ContextKind == VfxContextKind.Spawn;
				case VfxContextKind.Spawn: return b.ContextKind == VfxContextKind.Initialize;
				case VfxContextKind.Initialize: return b.ContextKind == VfxContextKind.Update || b.ContextKind == VfxContextKind.Output;
				case VfxContextKind.Update: return fromPort == 0 ? b.ContextKind == VfxContextKind.Output : b.ContextKind == VfxContextKind.Initialize;
				default: return false;
			}
		}

		private void EndBlockDrag(NVector2 mouse)
		{
			VfxNodeData block = draggingBlock;
			draggingBlock = null;
			if (NVector2.DistanceSquared(ImGui.GetMouseDragDelta(ImGuiMouseButton.Left), NVector2.Zero) < 25f)
			{
				return;
			}

			// Reorder within the context the mouse is over (the same context only).
			VfxGraph graph = Document.Graph;
			VfxNodeData context = graph.FindNode(block.Parent);
			NodeLayout layout;
			if (context == null || !layouts.TryGetValue(context.Id, out layout))
			{
				return;
			}

			int target = layout.Blocks.Count;
			for (int i = 0; i < layout.Blocks.Count; i++)
			{
				BlockLayout other = layout.Blocks[i];
				if (mouse.Y < (other.Min.Y + other.Max.Y) * 0.5f)
				{
					target = i;
					break;
				}
			}

			int current = context.Blocks.IndexOf(block.Id);
			if (current < 0 || target == current || target == current + 1)
			{
				return;
			}

			Document.Edit("Reorder blocks", () =>
			{
				context.Blocks.RemoveAt(current);
				if (target > current)
				{
					target--;
				}

				context.Blocks.Insert(Math.Clamp(target, 0, context.Blocks.Count), block.Id);
			});
		}

		private void AcceptPropertyDrop()
		{
			if (!ImGui.BeginDragDropTarget())
			{
				return;
			}

			if (!ImGui.AcceptDragDropPayload(PropertyDragType).IsNull && DraggedProperty != null)
			{
				string name = DraggedProperty;
				Vector2 position = ToGraph(ImGui.GetMousePos());
				Document.Edit("Add property node", () => new VfxGraphBuilder(Document.Graph).PropertyNode(name, position.X, position.Y));
			}

			ImGui.EndDragDropTarget();
		}

		// ---------------------------------------------------------------- menus

		private void OpenCreateMenu(NVector2 mouse, PortLayout fromPort, bool flow = false)
		{
			openCreatePopup = true;
			createPosition = mouse;
			createSearch = "";
			pendingPort = fromPort;
			if (!flow)
			{
				pendingFlowNode = null;
			}
		}

		private void OpenBlockMenu(VfxNodeData context)
		{
			openBlockPopup = true;
			blockPopupContext = context;
			blockSearch = "";
		}

		private void DrawCreatePopup()
		{
			if (openCreatePopup)
			{
				ImGui.OpenPopup("##vfx_create");
				openCreatePopup = false;
				ImGui.SetNextWindowPos(createPosition);
			}

			ImGui.SetNextWindowSize(new NVector2(300f, 400f), ImGuiCond.Appearing);
			if (!ImGui.BeginPopup("##vfx_create"))
			{
				pendingPort = null;
				pendingFlowNode = null;
				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##search", "Search nodes", ref createSearch, 128);
			bool filtered = createSearch.Length > 0;
			if (ImGui.BeginChild("##create_list", new NVector2(0f, 0f)))
			{
				if (pendingFlowNode != null)
				{
					DrawCreateList(VfxNodeLibrary.OfKind(VfxNodeKind.Context), filtered, "Contexts");
				}
				else if (pendingPort != null)
				{
					DrawCreateList(VfxNodeLibrary.OfKind(VfxNodeKind.Operator), filtered, "Operators");
				}
				else
				{
					DrawCreateList(VfxNodeLibrary.OfKind(VfxNodeKind.Context), filtered, "Contexts");
					DrawCreateList(VfxNodeLibrary.OfKind(VfxNodeKind.Operator), filtered, "Operators");
					if (Document.Graph.Properties.Count > 0 && (!filtered || "property".Contains(createSearch.ToLowerInvariant())))
					{
						if (ImGui.TreeNodeEx("Properties", filtered ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None))
						{
							foreach (VfxProperty property in Document.Graph.Properties)
							{
								if (ImGui.Selectable(property.Name + " (" + property.Value.Type + ")"))
								{
									CreateProperty(property.Name);
									ImGui.CloseCurrentPopup();
								}
							}

							ImGui.TreePop();
						}
					}
				}
			}

			ImGui.EndChild();
			ImGui.EndPopup();
		}

		private void DrawCreateList(List<VfxNodeDefinition> definitions, bool filtered, string header)
		{
			if (filtered)
			{
				string search = createSearch.ToLowerInvariant();
				foreach (VfxNodeDefinition definition in definitions)
				{
					if (!Matches(definition, search))
					{
						continue;
					}

					if (ImGui.Selectable(definition.Name + "  (" + definition.Category + ")"))
					{
						CreateNode(definition);
						ImGui.CloseCurrentPopup();
					}

					Tooltip(definition.Description);
				}

				return;
			}

			if (!ImGui.TreeNodeEx(header, ImGuiTreeNodeFlags.DefaultOpen))
			{
				return;
			}

			string current = null;
			bool open = false;
			foreach (VfxNodeDefinition definition in definitions)
			{
				string category = definition.Category ?? "";
				if (category != current)
				{
					if (current != null && open)
					{
						ImGui.TreePop();
					}

					current = category;
					open = ImGui.TreeNodeEx(category.Length == 0 ? "Other" : category);
				}

				if (!open)
				{
					continue;
				}

				if (ImGui.Selectable(definition.Name))
				{
					CreateNode(definition);
					ImGui.CloseCurrentPopup();
				}

				Tooltip(definition.Description);
			}

			if (current != null && open)
			{
				ImGui.TreePop();
			}

			ImGui.TreePop();
		}

		private static bool Matches(VfxNodeDefinition definition, string search)
		{
			return definition.Name.ToLowerInvariant().Contains(search) || (definition.Category ?? "").ToLowerInvariant().Contains(search) || (definition.Keywords ?? "").ToLowerInvariant().Contains(search);
		}

		private static void Tooltip(string text)
		{
			if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(text);
			}
		}

		private void CreateNode(VfxNodeDefinition definition)
		{
			Vector2 position = ToGraph(createPosition);
			PortLayout port = pendingPort;
			VfxNodeData flowNode = pendingFlowNode;
			int flowPort = pendingFlowPort;
			bool flowIsOutput = pendingFlowIsOutput;
			pendingPort = null;
			pendingFlowNode = null;
			VfxNodeData created = null;
			Document.Edit("Create " + definition.Name, () =>
			{
				VfxGraph graph = Document.Graph;
				created = graph.AddNode(definition.Type, position);
				if (port != null)
				{
					// Connect the dragged port to the first compatible slot.
					if (port.Input)
					{
						foreach (VfxSlotDefinition output in definition.GetOutputs(created))
						{
							if (VfxValueTypes.CanConvert(output.Type, port.Type))
							{
								graph.Connect(created, output.Name, port.Node, port.Slot);
								break;
							}
						}
					}
					else
					{
						foreach (VfxSlotDefinition input in definition.GetInputs(created))
						{
							if (VfxValueTypes.CanConvert(port.Type, input.Type))
							{
								graph.Connect(port.Node, port.Slot, created, input.Name);
								break;
							}
						}
					}
				}

				if (flowNode != null && definition.Kind == VfxNodeKind.Context)
				{
					if (flowIsOutput && definition.FlowInputs.Length > 0 && FlowAllowed(flowNode, flowPort, created, 0))
					{
						graph.ConnectFlow(flowNode, flowPort, created, 0);
					}
					else if (!flowIsOutput && definition.FlowOutputs.Length > 0 && FlowAllowed(created, 0, flowNode, flowPort))
					{
						graph.ConnectFlow(created, 0, flowNode, flowPort);
					}
				}
			});

			if (created != null)
			{
				Select(created.Id);
			}
		}

		private void CreateProperty(string propertyName)
		{
			Vector2 position = ToGraph(createPosition);
			PortLayout port = pendingPort;
			pendingPort = null;
			VfxNodeData created = null;
			Document.Edit("Create property node", () =>
			{
				created = new VfxGraphBuilder(Document.Graph).PropertyNode(propertyName, position.X, position.Y);
				if (port != null && !port.Input)
				{
					return;
				}

				if (port != null)
				{
					VfxProperty property = Document.Graph.FindProperty(propertyName);
					if (property != null && VfxValueTypes.CanConvert(property.Value.Type, port.Type))
					{
						Document.Graph.Connect(created, "Value", port.Node, port.Slot);
					}
				}
			});
			if (created != null)
			{
				Select(created.Id);
			}
		}

		private void DrawBlockPopup()
		{
			if (openBlockPopup)
			{
				ImGui.OpenPopup("##vfx_add_block");
				openBlockPopup = false;
			}

			ImGui.SetNextWindowSize(new NVector2(300f, 360f), ImGuiCond.Appearing);
			if (!ImGui.BeginPopup("##vfx_add_block"))
			{
				return;
			}

			VfxNodeData context = blockPopupContext != null ? Document.Graph.FindNode(blockPopupContext.Id) : null;
			VfxNodeDefinition contextDefinition = context != null ? VfxNodeLibrary.Find(context.Type) : null;
			if (context == null || contextDefinition == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##block_search", "Search blocks", ref blockSearch, 128);
			string search = blockSearch.ToLowerInvariant();
			if (ImGui.BeginChild("##block_list", new NVector2(0f, 0f)))
			{
				string current = null;
				bool open = false;
				foreach (VfxNodeDefinition definition in VfxNodeLibrary.BlocksFor(contextDefinition.ContextKind))
				{
					if (search.Length > 0)
					{
						if (!Matches(definition, search))
						{
							continue;
						}

						if (ImGui.Selectable(definition.Name + "  (" + definition.Category + ")"))
						{
							AddBlock(context, definition);
							ImGui.CloseCurrentPopup();
						}

						Tooltip(definition.Description);
						continue;
					}

					string category = definition.Category ?? "";
					if (category != current)
					{
						if (current != null && open)
						{
							ImGui.TreePop();
						}

						current = category;
						open = ImGui.TreeNodeEx(category.Length == 0 ? "Other" : category, ImGuiTreeNodeFlags.DefaultOpen);
					}

					if (!open)
					{
						continue;
					}

					if (ImGui.Selectable(definition.Name))
					{
						AddBlock(context, definition);
						ImGui.CloseCurrentPopup();
					}

					Tooltip(definition.Description);
				}

				if (current != null && open)
				{
					ImGui.TreePop();
				}
			}

			ImGui.EndChild();
			ImGui.EndPopup();
		}

		private void AddBlock(VfxNodeData context, VfxNodeDefinition definition)
		{
			VfxNodeData created = null;
			Document.Edit("Add " + definition.Name, () => created = Document.Graph.AddBlock(context, definition.Type));
			if (created != null)
			{
				Select(created.Id);
			}
		}

		private void DrawNodeContextMenu()
		{
			if (openNodeContextMenu)
			{
				ImGui.OpenPopup("##vfx_node_menu");
				openNodeContextMenu = false;
			}

			if (!ImGui.BeginPopup("##vfx_node_menu"))
			{
				return;
			}

			VfxGraph graph = Document.Graph;
			VfxNodeData node = contextMenuNode != null ? graph.FindNode(contextMenuNode.Id) : null;
			VfxNodeDefinition definition = node != null ? VfxNodeLibrary.Find(node.Type) : null;
			if (node == null || definition == null)
			{
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.TextDisabled(definition.GetTitle(node));
			ImGui.Separator();
			if (definition.Kind == VfxNodeKind.Context && ImGui.MenuItem("Add Block..."))
			{
				OpenBlockMenu(node);
			}

			if (definition.Kind == VfxNodeKind.Block)
			{
				VfxNodeData context = graph.FindNode(node.Parent);
				int index = context?.Blocks.IndexOf(node.Id) ?? -1;
				if (ImGui.MenuItem(node.Enabled ? "Disable" : "Enable"))
				{
					Document.Edit("Toggle block", () => node.Enabled = !node.Enabled);
				}

				if (ImGui.MenuItem("Move Up", "", false, index > 0))
				{
					Document.Edit("Move block up", () =>
					{
						context.Blocks.RemoveAt(index);
						context.Blocks.Insert(index - 1, node.Id);
					});
				}

				if (ImGui.MenuItem("Move Down", "", false, context != null && index >= 0 && index < context.Blocks.Count - 1))
				{
					Document.Edit("Move block down", () =>
					{
						context.Blocks.RemoveAt(index);
						context.Blocks.Insert(index + 1, node.Id);
					});
				}
			}

			if (ImGui.MenuItem("Duplicate", "Ctrl+D"))
			{
				Select(node.Id);
				DuplicateSelected();
			}

			if (ImGui.MenuItem("Delete", "Del"))
			{
				if (!Selected.Contains(node.Id))
				{
					Select(node.Id);
				}

				DeleteSelected();
			}

			ImGui.EndPopup();
		}

		private static VfxNodeData CloneNode(VfxGraph graph, VfxNodeData source)
		{
			var copy = new VfxNodeData
			{
				Id = VfxGraph.NewId(),
				Type = source.Type,
				Position = source.Position,
				Collapsed = source.Collapsed,
				Enabled = source.Enabled,
				Parent = 0,
				Title = source.Title
			};
			foreach (VfxSlotValue input in source.Inputs)
			{
				copy.Inputs.Add(new VfxSlotValue(input.Name, input.Value?.Clone()));
			}

			foreach (VfxSetting setting in source.Settings)
			{
				copy.Settings.Add(new VfxSetting(setting.Name, setting.Value));
			}

			graph.Nodes.Add(copy);
			return copy;
		}

		#endregion
	}
}
