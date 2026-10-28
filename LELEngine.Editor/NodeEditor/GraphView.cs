using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Graphs
{
	/// <summary>
	///     A node graph canvas for editor windows (the editor's own, and editor scripts through
	///     <see cref="EditorWindow.OnGUI" />): pan and zoom, a grid, nodes with headers, text rows, ports and progress
	///     bars, edges port to port (beziers) or node to node (arrows, as a state machine's transitions), groups and
	///     sticky notes, selection (click, Ctrl / Shift, rubber band), dragging with grid snapping and alignment
	///     guides, port and node drags that request connections, a searchable create popup, context menus, inline
	///     renaming, a minimap, keyboard shortcuts and automatic layouts (<see cref="GraphLayout" />).
	///     <para>
	///         Immediate mode: every frame the host calls <see cref="Begin" />, adds what to draw (<see cref="AddNode" />,
	///         <see cref="AddEdge" />, <see cref="AddGroup" />, <see cref="AddNote" />) from its own data, and
	///         <see cref="End" /> draws and handles the input. Changes come back through the callbacks
	///         (<see cref="NodesMoved" />, <see cref="ConnectRequested" />, <see cref="DeleteRequested" />, ...): the view
	///         never changes the host's data itself, so undo stays the host's.
	///     </para>
	/// </summary>
	public sealed class GraphView
	{
		#region PublicFields

		/// <summary>Pan: graph units added to a position before scaling (the top left of the canvas shows -Offset).</summary>
		public NVector2 Offset;

		public float Zoom = 1f;
		public float MinZoom = 0.2f;
		public float MaxZoom = 2.5f;
		public bool ShowGrid = true;
		public float GridSize = 32f;
		public bool SnapToGrid;
		public float SnapSize = 16f;
		public bool ShowMinimap = true;
		public bool ShowAlignmentGuides = true;

		/// <summary>Edges may be started by dragging a node's handle onto another node (state machine transitions).</summary>
		public bool AllowNodeConnections;

		/// <summary>Edges may be started by dragging ports (data flow graphs).</summary>
		public bool AllowPortConnections = true;

		/// <summary>Right click on empty canvas opens the create popup (else the context menu).</summary>
		public bool CreateOnRightClick = true;

		/// <summary>The built-in items (Delete, Duplicate, Rename, Frame, Align) follow the host's in context menus.</summary>
		public bool ShowDefaultContextItems = true;

		/// <summary>Smallest node width in graph units.</summary>
		public float MinNodeWidth = 150f;

		/// <summary>Font size the nodes use at zoom 1 (0: the editor's).</summary>
		public float BaseFontSize;

		public NVector4 BackgroundColor = new NVector4(0.11f, 0.11f, 0.12f, 1f);
		public NVector4 GridColor = new NVector4(1f, 1f, 1f, 0.05f);

		/// <summary>The selected ids (nodes, edges, groups, notes).</summary>
		public readonly HashSet<ulong> Selection = new HashSet<ulong>();

		/// <summary>The id clicked last (the inspector shows it).</summary>
		public ulong Primary;

		/// <summary>The next End frames every node (the view just opened).</summary>
		public bool PendingFrame;

		// ---- callbacks (set what applies; the rest stays null)

		/// <summary>Nodes were dragged, nudged or laid out: every moved node with its new position.</summary>
		public Action<IReadOnlyList<GraphMove>> NodesMoved;

		/// <summary>A resizable node or note got a new size (Size; Position unchanged).</summary>
		public Action<GraphMove> NodeResized;

		/// <summary>A port or node drag ended on a target (or "connect" from a menu).</summary>
		public Action<GraphConnection> ConnectRequested;

		/// <summary>Delete pressed or chosen: the selected ids that are deletable.</summary>
		public Action<IReadOnlyList<ulong>> DeleteRequested;

		public Action<IReadOnlyList<ulong>> DuplicateRequested;
		public Action<IReadOnlyList<ulong>> CopyRequested;

		/// <summary>Paste at a graph position.</summary>
		public Action<NVector2> PasteRequested;

		public Action<ulong> NodeDoubleClicked;
		public Action<ulong, string> RenameRequested;
		public Action SelectionChanged;

		/// <summary>Draws the items of a context menu (inside an open popup).</summary>
		public Action<GraphContextMenu> ContextMenu;

		/// <summary>
		///     The entries of the create popup for a graph position, with the pending connection when a drag from a port
		///     or node ended on empty canvas (null otherwise): the entry's action may create a node there and connect it.
		/// </summary>
		public Func<NVector2, GraphConnection?, IEnumerable<GraphMenuEntry>> CreateEntries;

		/// <summary>
		///     Called right after the canvas item each frame, so the host can accept drag and drop payloads on the canvas
		///     (DragAndDrop.AcceptValue): the graph position of the mouse is passed.
		/// </summary>
		public Action<NVector2> DropTarget;

		/// <summary>The id under the mouse after the last End (0: none).</summary>
		public ulong HoveredId { get; private set; }

		public bool IsHovered => hovered;
		public bool IsFocused => focused;

		/// <summary>A drag, box select or connection is in progress.</summary>
		public bool IsInteracting => draggingNodes || boxSelecting || dragPort != null || dragFromNode != 0 || connecting != 0 || resizing != 0 || panning;

		/// <summary>The nodes added this frame (after Begin).</summary>
		public IReadOnlyList<GraphNode> Nodes => nodes;

		public IReadOnlyList<GraphEdge> Edges => edges;
		public IReadOnlyList<GraphGroup> Groups => groups;
		public IReadOnlyList<GraphNote> Notes => notes;

		/// <summary>Screen rectangle of the canvas after the last End.</summary>
		public NVector2 CanvasMin => canvasMin;

		public NVector2 CanvasSize => canvasSize;

		#endregion

		#region PrivateFields

		private const float PortRadius = 5f;
		private const float HandleRadius = 6f;
		private const float HeaderPadding = 5f;
		private const float Rounding = 6f;
		private const float GuideSnap = 6f;

		private readonly List<GraphNode> nodes = new List<GraphNode>();
		private readonly List<GraphEdge> edges = new List<GraphEdge>();
		private readonly List<GraphGroup> groups = new List<GraphGroup>();
		private readonly List<GraphNote> notes = new List<GraphNote>();
		private readonly Dictionary<ulong, GraphNode> nodesById = new Dictionary<ulong, GraphNode>();
		private readonly Dictionary<ulong, GraphEdge> edgesById = new Dictionary<ulong, GraphEdge>();
		private readonly Dictionary<ulong, GraphNote> notesById = new Dictionary<ulong, GraphNote>();
		private readonly Dictionary<ulong, GraphGroup> groupsById = new Dictionary<ulong, GraphGroup>();
		private readonly Dictionary<ulong, NVector2> dragStart = new Dictionary<ulong, NVector2>();
		private readonly List<(NVector2 a, NVector2 b)> guides = new List<(NVector2, NVector2)>();
		private readonly List<GraphMenuEntry> createCache = new List<GraphMenuEntry>();

		private NVector2 canvasMin;
		private NVector2 canvasSize;
		private bool hovered;
		private bool focused;
		private float fontSize = 14f;

		// Interaction.
		private bool panning;
		private NVector2 panStart;
		private NVector2 panOffsetStart;
		private NVector2 rightPress;
		private bool draggingNodes;
		private bool nodesMoved;
		private NVector2 dragAccumulated;
		private ulong dragPrimary;
		private bool boxSelecting;
		private NVector2 boxStart;
		private GraphNode dragPortNode;
		private GraphPort dragPort;
		private ulong dragFromNode;
		private ulong connecting;
		private ulong resizing;
		private NVector2 resizeStartSize;
		private ulong renaming;
		private string renameBuffer = "";
		private bool renameOpen;
		private ulong lastClicked;
		private double lastClickTime;
		private NVector2 lastMouse;

		// Popups.
		private bool openCreate;
		private NVector2 createPosition;
		private GraphConnection? createConnection;
		private string createSearch = "";
		private bool openContext;
		private GraphContextMenu contextRequest;
		private NVector2 minimapMin;
		private NVector2 minimapMax;
		private bool minimapDragging;

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- frame

		/// <summary>Starts a frame: drops last frame's nodes, edges, groups and notes.</summary>
		public void Begin()
		{
			nodes.Clear();
			edges.Clear();
			groups.Clear();
			notes.Clear();
			nodesById.Clear();
			edgesById.Clear();
			notesById.Clear();
			groupsById.Clear();
		}

		public GraphNode AddNode(GraphNode node)
		{
			if (node == null)
			{
				return null;
			}

			nodes.Add(node);
			nodesById[node.Id] = node;
			return node;
		}

		public GraphNode AddNode(ulong id, string title, NVector2 position)
		{
			return AddNode(new GraphNode { Id = id, Title = title ?? "", Position = position });
		}

		public GraphEdge AddEdge(GraphEdge edge)
		{
			if (edge == null)
			{
				return null;
			}

			edges.Add(edge);
			edgesById[edge.Id] = edge;
			return edge;
		}

		/// <summary>A node to node edge (arrow style).</summary>
		public GraphEdge AddEdge(ulong id, ulong from, ulong to)
		{
			return AddEdge(new GraphEdge { Id = id, From = from, To = to, Style = GraphEdgeStyle.Arrow });
		}

		/// <summary>A port to port edge (bezier style).</summary>
		public GraphEdge AddEdge(ulong id, ulong from, string fromPort, ulong to, string toPort)
		{
			return AddEdge(new GraphEdge { Id = id, From = from, FromPort = fromPort ?? "", To = to, ToPort = toPort ?? "", Style = GraphEdgeStyle.Bezier });
		}

		public GraphGroup AddGroup(GraphGroup group)
		{
			if (group == null)
			{
				return null;
			}

			groups.Add(group);
			groupsById[group.Id] = group;
			return group;
		}

		public GraphNote AddNote(GraphNote note)
		{
			if (note == null)
			{
				return null;
			}

			notes.Add(note);
			notesById[note.Id] = note;
			return note;
		}

		/// <summary>Draws the graph into the current ImGui window's remaining area and handles the input.</summary>
		public void End()
		{
			canvasMin = ImGui.GetCursorScreenPos();
			canvasSize = ImGui.GetContentRegionAvail();
			if (canvasSize.X < 16f || canvasSize.Y < 16f)
			{
				return;
			}

			fontSize = BaseFontSize > 0f ? BaseFontSize : ImGui.GetFontSize();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(canvasMin, canvasMin + canvasSize, ImGui.GetColorU32(BackgroundColor));
			if (ShowGrid)
			{
				DrawGrid(draw);
			}

			ImGui.SetCursorScreenPos(canvasMin);
			EditorGui.InvisibleButton("##graph_canvas", canvasSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight | ImGuiButtonFlags.MouseButtonMiddle);
			hovered = ImGui.IsItemHovered();
			focused = ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows);
			if (DropTarget != null)
			{
				try
				{
					DropTarget(ToGraph(ImGui.GetMousePos()));
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			draw.PushClipRect(canvasMin, canvasMin + canvasSize, true);
			ImGui.PushFont(ImGui.GetFont(), fontSize * Zoom);
			try
			{
				ComputeLayouts();
				if (PendingFrame)
				{
					PendingFrame = false;
					FrameAll();
					ComputeLayouts();
				}

				ComputeGroups();
				foreach (GraphGroup group in groups)
				{
					DrawGroup(draw, group);
				}

				foreach (GraphNote note in notes)
				{
					DrawNote(draw, note);
				}

				ComputeEdgeLanes();
				foreach (GraphEdge edge in edges)
				{
					DrawEdge(draw, edge);
				}

				foreach (GraphNode node in nodes)
				{
					DrawNode(draw, node);
				}

				DrawDragConnection(draw);
				foreach ((NVector2 a, NVector2 b) in guides)
				{
					draw.AddLine(a, b, ImGui.GetColorU32(new NVector4(1f, 0.6f, 0.2f, 0.8f)), 1f);
				}

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

			if (ShowMinimap)
			{
				DrawMinimap(draw);
			}

			HoveredId = hovered ? HitTest(ImGui.GetMousePos()).Id : 0;
			HandleInput();
			DrawCreatePopup();
			DrawContextPopup();
			DrawRenamePopup();
			if (TestAnchors.Enabled)
			{
				foreach (GraphNode node in nodes)
				{
					TestAnchors.Record("node:" + node.Title, node.ScreenMin, node.ScreenMax);
				}
			}
		}

		// ---------------------------------------------------------------- view

		public NVector2 ToScreen(NVector2 graph)
		{
			return canvasMin + (graph + Offset) * Zoom;
		}

		public NVector2 ToGraph(NVector2 screen)
		{
			return (screen - canvasMin) / Zoom - Offset;
		}

		/// <summary>The graph position under the mouse (the create popup's default position).</summary>
		public NVector2 MouseGraphPosition => ToGraph(ImGui.GetMousePos());

		/// <summary>Centers the view on every node (zoom to fit, between 0.25 and 1.25).</summary>
		public void FrameAll()
		{
			Frame(nodes, true);
		}

		/// <summary>Centers the view on the selected nodes.</summary>
		public void FrameSelection()
		{
			var selected = new List<GraphNode>();
			foreach (GraphNode node in nodes)
			{
				if (Selection.Contains(node.Id))
				{
					selected.Add(node);
				}
			}

			Frame(selected.Count > 0 ? selected : nodes, false);
		}

		/// <summary>Scrolls a node into the centre (zoom unchanged).</summary>
		public void FrameNode(ulong id)
		{
			if (nodesById.TryGetValue(id, out GraphNode node))
			{
				Frame(new List<GraphNode> { node }, false);
			}
		}

		public GraphNode FindNode(ulong id)
		{
			return nodesById.TryGetValue(id, out GraphNode node) ? node : null;
		}

		public GraphEdge FindEdge(ulong id)
		{
			return edgesById.TryGetValue(id, out GraphEdge edge) ? edge : null;
		}

		public bool IsSelected(ulong id)
		{
			return Selection.Contains(id);
		}

		public void SelectOnly(ulong id)
		{
			Selection.Clear();
			if (id != 0)
			{
				Selection.Add(id);
			}

			Primary = id;
			SelectionChanged?.Invoke();
		}

		public void Select(ulong id, bool additive)
		{
			if (!additive)
			{
				Selection.Clear();
			}

			Selection.Add(id);
			Primary = id;
			SelectionChanged?.Invoke();
		}

		public void ClearSelection()
		{
			if (Selection.Count == 0 && Primary == 0)
			{
				return;
			}

			Selection.Clear();
			Primary = 0;
			SelectionChanged?.Invoke();
		}

		/// <summary>Opens the inline rename of a node (F2, double click on the title).</summary>
		public void BeginRename(ulong id)
		{
			if (!nodesById.TryGetValue(id, out GraphNode node) || !node.Renamable)
			{
				return;
			}

			renaming = id;
			renameBuffer = node.Title;
			renameOpen = true;
		}

		/// <summary>Starts a connection from a node: the edge follows the mouse until a node is clicked (Escape cancels).</summary>
		public void StartConnecting(ulong fromNode)
		{
			connecting = fromNode;
			dragFromNode = 0;
		}

		/// <summary>Opens the create popup at a graph position.</summary>
		public void OpenCreatePopup(NVector2 graphPosition, GraphConnection? connection = null)
		{
			createPosition = graphPosition;
			createConnection = connection;
			openCreate = true;
		}

		/// <summary>Opens the context menu of the canvas or an id at the mouse.</summary>
		public void OpenContextMenu(GraphContextKind kind, ulong id)
		{
			contextRequest = new GraphContextMenu { Kind = kind, Id = id, GraphPosition = MouseGraphPosition, Selection = new List<ulong>(Selection) };
			openContext = true;
		}

		public void CancelInteraction()
		{
			panning = false;
			draggingNodes = false;
			boxSelecting = false;
			dragPort = null;
			dragPortNode = null;
			dragFromNode = 0;
			connecting = 0;
			resizing = 0;
			dragStart.Clear();
			guides.Clear();
		}

		/// <summary>Applies a layout's positions through <see cref="NodesMoved" /> (one undoable step for the host).</summary>
		public void ApplyLayout(List<GraphMove> moves)
		{
			if (moves != null && moves.Count > 0)
			{
				NodesMoved?.Invoke(moves);
			}
		}

		/// <summary>A layered layout of the current nodes and edges, applied through <see cref="NodesMoved" />.</summary>
		public void AutoLayoutLayered(bool leftToRight = true)
		{
			NVector2 origin = nodes.Count > 0 ? nodes[0].Position : NVector2.Zero;
			foreach (GraphNode node in nodes)
			{
				origin = NVector2.Min(origin, node.Position);
			}

			ApplyLayout(GraphLayout.Layered(nodes, edges, origin + new NVector2(0f, 200f), 80f, 40f, leftToRight));
			PendingFrame = true;
		}

		/// <summary>A force-directed layout of the current nodes (the unselected ones pinned when <paramref name="selectedOnly" />).</summary>
		public void AutoLayoutForce(bool selectedOnly = false)
		{
			var pinned = new HashSet<ulong>();
			if (selectedOnly && Selection.Count > 0)
			{
				foreach (GraphNode node in nodes)
				{
					if (!Selection.Contains(node.Id))
					{
						pinned.Add(node.Id);
					}
				}
			}

			ApplyLayout(GraphLayout.Force(nodes, edges, pinned));
			PendingFrame = !selectedOnly;
		}

		/// <summary>Aligns the selected nodes (mode: 0 left, 1 centre x, 2 right, 3 top, 4 centre y, 5 bottom).</summary>
		public void AlignSelection(int mode)
		{
			ApplyLayout(GraphLayout.Align(SelectedNodes(), mode));
		}

		public void DistributeSelection(bool horizontal)
		{
			ApplyLayout(GraphLayout.Distribute(SelectedNodes(), horizontal));
		}

		/// <summary>The selected nodes (not edges, groups or notes).</summary>
		public List<GraphNode> SelectedNodes()
		{
			var result = new List<GraphNode>();
			foreach (GraphNode node in nodes)
			{
				if (Selection.Contains(node.Id))
				{
					result.Add(node);
				}
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		// ---------------------------------------------------------------- layout

		private float S(float graphUnits)
		{
			return graphUnits * Zoom;
		}

		private void ComputeLayouts()
		{
			float lineHeight = ImGui.GetTextLineHeight();
			float padding = S(HeaderPadding);
			foreach (GraphNode node in nodes)
			{
				NVector2 min = ToScreen(node.Position);
				float titleWidth = ImGui.CalcTextSize(node.Title).X + (string.IsNullOrEmpty(node.Badge) ? 0f : ImGui.CalcTextSize(node.Badge).X + padding * 2f);
				float width = Math.Max(S(node.Width > 0f ? node.Width : MinNodeWidth), titleWidth + padding * 4f);
				float height;
				int rows = 0;
				float rowsWidth = 0f;
				int inputs = 0;
				int outputs = 0;
				foreach (GraphPort port in node.Ports)
				{
					float textWidth = ImGui.CalcTextSize(port.DisplayLabel).X + (string.IsNullOrEmpty(port.Preview) ? 0f : ImGui.CalcTextSize(port.Preview).X + padding);
					rowsWidth = Math.Max(rowsWidth, textWidth + S(PortRadius) * 4f + padding * 2f);
					if (port.Direction == GraphPortDirection.Input) inputs++;
					else outputs++;
				}

				rows = Math.Max(inputs, outputs);
				foreach (GraphNodeLine line in node.Lines)
				{
					float textWidth = ImGui.CalcTextSize(line.Text).X + (string.IsNullOrEmpty(line.Right) ? 0f : ImGui.CalcTextSize(line.Right).X + padding * 2f);
					rowsWidth = Math.Max(rowsWidth, textWidth + padding * 2f);
				}

				if (!string.IsNullOrEmpty(node.Subtitle))
				{
					rowsWidth = Math.Max(rowsWidth, ImGui.CalcTextSize(node.Subtitle).X + padding * 2f);
				}

				if (node.Shape == GraphNodeShape.Box && node.Width <= 0f)
				{
					width = Math.Max(width, rowsWidth);
				}
				else if (node.Width <= 0f)
				{
					width = Math.Max(titleWidth + padding * 6f, S(MinNodeWidth) * 0.6f);
				}

				float headerHeight = node.Shape == GraphNodeShape.Box ? lineHeight + padding * 2f + (string.IsNullOrEmpty(node.Subtitle) ? 0f : lineHeight) : lineHeight + padding * 3f;
				float bodyHeight = node.Shape == GraphNodeShape.Box ? rows * (lineHeight + padding) + node.Lines.Count * (lineHeight + padding * 0.5f) + S(node.CustomHeight) + (rows + node.Lines.Count > 0 || node.CustomHeight > 0f ? padding : 0f) : 0f;
				if (node.Progress >= 0f)
				{
					bodyHeight += S(6f);
				}

				height = headerHeight + bodyHeight;
				if (node.Height > 0f && node.Resizable)
				{
					height = Math.Max(height, S(node.Height));
				}

				if (node.Shape == GraphNodeShape.Circle || node.Shape == GraphNodeShape.Diamond)
				{
					float side = Math.Max(width, height);
					width = side;
					height = side;
				}

				node.HeaderHeight = headerHeight;
				node.BodyTop = min.Y + headerHeight;
				node.ScreenMin = min;
				node.ScreenMax = min + new NVector2(width, height);
				node.Size = new NVector2(width, height) / Math.Max(Zoom, 1e-3f);

				// Ports down both sides of the body.
				int inputRow = 0;
				int outputRow = 0;
				foreach (GraphPort port in node.Ports)
				{
					bool input = port.Direction == GraphPortDirection.Input;
					int row = input ? inputRow++ : outputRow++;
					float y = node.BodyTop + padding + row * (lineHeight + padding) + lineHeight * 0.5f;
					port.Center = new NVector2(input ? min.X : node.ScreenMax.X, y);
					port.Connected = false;
				}
			}

			foreach (GraphNote note in notes)
			{
				note.ScreenMin = ToScreen(note.Position);
				note.ScreenMax = ToScreen(note.Position + note.Size);
			}

			foreach (GraphEdge edge in edges)
			{
				GraphNode from = FindNode(edge.From);
				GraphNode to = FindNode(edge.To);
				GraphPort fromPort = from?.FindPort(edge.FromPort);
				GraphPort toPort = to?.FindPort(edge.ToPort);
				if (fromPort != null) fromPort.Connected = true;
				if (toPort != null) toPort.Connected = true;
			}
		}

		private void ComputeGroups()
		{
			float padding = S(12f);
			float title = ImGui.GetTextLineHeight() + S(8f);
			foreach (GraphGroup group in groups)
			{
				var min = new NVector2(float.MaxValue);
				var max = new NVector2(float.MinValue);
				foreach (ulong member in group.Members)
				{
					if (nodesById.TryGetValue(member, out GraphNode node))
					{
						min = NVector2.Min(min, node.ScreenMin);
						max = NVector2.Max(max, node.ScreenMax);
					}
					else if (notesById.TryGetValue(member, out GraphNote note))
					{
						min = NVector2.Min(min, note.ScreenMin);
						max = NVector2.Max(max, note.ScreenMax);
					}
				}

				if (min.X > max.X)
				{
					group.ScreenMin = ToScreen(NVector2.Zero);
					group.ScreenMax = group.ScreenMin + new NVector2(S(160f), S(60f));
					continue;
				}

				group.ScreenMin = min - new NVector2(padding, padding + title);
				group.ScreenMax = max + new NVector2(padding, padding);
			}
		}

		/// <summary>Arrow edges between the same two nodes in opposite directions get lanes, so both are visible.</summary>
		private void ComputeEdgeLanes()
		{
			var pairs = new Dictionary<(ulong, ulong), List<GraphEdge>>();
			foreach (GraphEdge edge in edges)
			{
				if (edge.Style != GraphEdgeStyle.Arrow && edge.Style != GraphEdgeStyle.Line)
				{
					continue;
				}

				(ulong, ulong) key = edge.From < edge.To ? (edge.From, edge.To) : (edge.To, edge.From);
				if (!pairs.TryGetValue(key, out List<GraphEdge> list))
				{
					list = new List<GraphEdge>();
					pairs[key] = list;
				}

				list.Add(edge);
			}

			foreach (KeyValuePair<(ulong, ulong), List<GraphEdge>> pair in pairs)
			{
				List<GraphEdge> list = pair.Value;
				bool both = false;
				foreach (GraphEdge edge in list)
				{
					if (edge.From != list[0].From)
					{
						both = true;
					}
				}

				foreach (GraphEdge edge in list)
				{
					edge.LaneCount = both ? 2 : 1;
					edge.LaneIndex = both ? (edge.From == pair.Key.Item1 ? 0 : 1) : 0;
				}
			}
		}

		// ---------------------------------------------------------------- drawing

		private void DrawGrid(ImDrawListPtr draw)
		{
			float step = GridSize * Zoom;
			if (step < 6f)
			{
				return;
			}

			uint color = ImGui.GetColorU32(GridColor);
			uint strong = ImGui.GetColorU32(new NVector4(GridColor.X, GridColor.Y, GridColor.Z, GridColor.W * 2.2f));
			float startX = canvasMin.X + (Offset.X * Zoom % step + step) % step;
			int indexX = (int)Math.Floor(-Offset.X / GridSize);
			for (float x = startX; x < canvasMin.X + canvasSize.X; x += step, indexX++)
			{
				draw.AddLine(new NVector2(x, canvasMin.Y), new NVector2(x, canvasMin.Y + canvasSize.Y), indexX % 5 == 0 ? strong : color);
			}

			float startY = canvasMin.Y + (Offset.Y * Zoom % step + step) % step;
			int indexY = (int)Math.Floor(-Offset.Y / GridSize);
			for (float y = startY; y < canvasMin.Y + canvasSize.Y; y += step, indexY++)
			{
				draw.AddLine(new NVector2(canvasMin.X, y), new NVector2(canvasMin.X + canvasSize.X, y), indexY % 5 == 0 ? strong : color);
			}
		}

		private void DrawGroup(ImDrawListPtr draw, GraphGroup group)
		{
			bool selected = Selection.Contains(group.Id);
			NVector4 color = group.Color;
			draw.AddRectFilled(group.ScreenMin, group.ScreenMax, ImGui.GetColorU32(new NVector4(color.X, color.Y, color.Z, 0.12f)), S(Rounding));
			float title = ImGui.GetTextLineHeight() + S(8f);
			draw.AddRectFilled(group.ScreenMin, new NVector2(group.ScreenMax.X, group.ScreenMin.Y + title), ImGui.GetColorU32(new NVector4(color.X, color.Y, color.Z, 0.35f)), S(Rounding), ImDrawFlags.RoundCornersTop);
			draw.AddRect(group.ScreenMin, group.ScreenMax, ImGui.GetColorU32(selected ? EditorStyle.Selection : new NVector4(color.X, color.Y, color.Z, 0.5f)), S(Rounding), ImDrawFlags.None, selected ? 2f : 1f);
			draw.AddText(group.ScreenMin + new NVector2(S(8f), S(4f)), ImGui.GetColorU32(ImGuiCol.Text), group.Title);
		}

		private void DrawNote(ImDrawListPtr draw, GraphNote note)
		{
			bool selected = Selection.Contains(note.Id);
			NVector4 color = note.Color;
			draw.AddRectFilled(note.ScreenMin, note.ScreenMax, ImGui.GetColorU32(new NVector4(color.X, color.Y, color.Z, 0.85f)), S(4f));
			draw.AddRect(note.ScreenMin, note.ScreenMax, ImGui.GetColorU32(selected ? EditorStyle.Selection : new NVector4(0f, 0f, 0f, 0.5f)), S(4f), ImDrawFlags.None, selected ? 2f : 1f);
			draw.PushClipRect(note.ScreenMin, note.ScreenMax, true);
			DrawWrappedText(draw, note.Text, note.ScreenMin + new NVector2(S(8f), S(6f)), note.ScreenMax.X - note.ScreenMin.X - S(16f), ImGui.GetColorU32(new NVector4(0.1f, 0.1f, 0.1f, 1f)));
			draw.PopClipRect();
			// Resize grip.
			NVector2 corner = note.ScreenMax;
			uint grip = ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.4f));
			draw.AddTriangleFilled(corner - new NVector2(S(10f), 0f), corner, corner - new NVector2(0f, S(10f)), grip);
		}

		private void DrawWrappedText(ImDrawListPtr draw, string text, NVector2 position, float width, uint color)
		{
			if (string.IsNullOrEmpty(text))
			{
				return;
			}

			float lineHeight = ImGui.GetTextLineHeight();
			float y = position.Y;
			foreach (string paragraph in text.Split('\n'))
			{
				string line = "";
				foreach (string word in paragraph.Split(' '))
				{
					string candidate = line.Length == 0 ? word : line + " " + word;
					if (ImGui.CalcTextSize(candidate).X > width && line.Length > 0)
					{
						draw.AddText(new NVector2(position.X, y), color, line);
						y += lineHeight;
						line = word;
					}
					else
					{
						line = candidate;
					}
				}

				draw.AddText(new NVector2(position.X, y), color, line);
				y += lineHeight;
			}
		}

		private void DrawNode(ImDrawListPtr draw, GraphNode node)
		{
			bool selected = Selection.Contains(node.Id);
			bool isHovered = hovered && HoveredId == node.Id && !draggingNodes;
			NVector2 min = node.ScreenMin;
			NVector2 max = node.ScreenMax;
			float padding = S(HeaderPadding);
			float rounding = S(Rounding);
			uint body = ImGui.GetColorU32(node.BodyColor ?? new NVector4(0.2f, 0.2f, 0.22f, 0.97f));
			uint header = ImGui.GetColorU32(node.HeaderColor);
			uint text = ImGui.GetColorU32(node.TitleColor ?? new NVector4(1f, 1f, 1f, 1f));
			uint dim = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.6f));
			NVector2 center = (min + max) * 0.5f;
			switch (node.Shape)
			{
				case GraphNodeShape.Capsule:
				{
					float r = (max.Y - min.Y) * 0.5f;
					draw.AddRectFilled(min, max, header, r);
					break;
				}
				case GraphNodeShape.Circle:
					draw.AddCircleFilled(center, (max.X - min.X) * 0.5f, header, 32);
					break;
				case GraphNodeShape.Diamond:
					draw.AddQuadFilled(new NVector2(center.X, min.Y), new NVector2(max.X, center.Y), new NVector2(center.X, max.Y), new NVector2(min.X, center.Y), header);
					break;
				default:
					draw.AddRectFilled(min, max, body, rounding);
					draw.AddRectFilled(min, new NVector2(max.X, min.Y + node.HeaderHeight), header, rounding, node.HeaderHeight >= max.Y - min.Y - 0.5f ? ImDrawFlags.RoundCornersAll : ImDrawFlags.RoundCornersTop);
					break;
			}

			// Outline: selection, host highlight, hover.
			NVector4? outlineColor = selected ? EditorStyle.Selection : node.Outline;
			float thickness = selected ? 2f : node.Outline != null ? node.OutlineThickness : 1f;
			if (outlineColor == null && isHovered)
			{
				outlineColor = new NVector4(1f, 1f, 1f, 0.35f);
			}

			uint outline = ImGui.GetColorU32(outlineColor ?? new NVector4(0f, 0f, 0f, 0.6f));
			switch (node.Shape)
			{
				case GraphNodeShape.Capsule:
					draw.AddRect(min, max, outline, (max.Y - min.Y) * 0.5f, ImDrawFlags.None, thickness);
					break;
				case GraphNodeShape.Circle:
					draw.AddCircle(center, (max.X - min.X) * 0.5f, outline, 32, thickness);
					break;
				case GraphNodeShape.Diamond:
					draw.AddQuad(new NVector2(center.X, min.Y), new NVector2(max.X, center.Y), new NVector2(center.X, max.Y), new NVector2(min.X, center.Y), outline, thickness);
					break;
				default:
					draw.AddRect(min, max, outline, rounding, ImDrawFlags.None, thickness);
					break;
			}

			if (selected && node.Outline != null)
			{
				// Both: the host's color inside the selection.
				float inset = thickness + 1f;
				draw.AddRect(min + new NVector2(inset), max - new NVector2(inset), ImGui.GetColorU32(node.Outline.Value), Math.Max(rounding - inset, 0f), ImDrawFlags.None, node.OutlineThickness);
			}

			// Title (centered for the non-box shapes).
			NVector2 titleSize = ImGui.CalcTextSize(node.Title);
			if (node.Shape == GraphNodeShape.Box)
			{
				draw.AddText(min + new NVector2(padding * 2f, padding), text, node.Title);
				if (!string.IsNullOrEmpty(node.Subtitle))
				{
					draw.AddText(min + new NVector2(padding * 2f, padding + ImGui.GetTextLineHeight()), dim, node.Subtitle);
				}
			}
			else
			{
				draw.AddText(center - titleSize * 0.5f, text, node.Title);
			}

			if (!string.IsNullOrEmpty(node.Badge))
			{
				NVector2 badgeSize = ImGui.CalcTextSize(node.Badge);
				NVector2 badgeMin = new NVector2(max.X - badgeSize.X - padding * 3f, min.Y + padding * 0.5f);
				draw.AddRectFilled(badgeMin, badgeMin + badgeSize + new NVector2(padding * 2f, padding), ImGui.GetColorU32(node.BadgeColor ?? new NVector4(0f, 0f, 0f, 0.35f)), S(3f));
				draw.AddText(badgeMin + new NVector2(padding, padding * 0.5f), text, node.Badge);
			}

			if (node.Shape != GraphNodeShape.Box)
			{
				DrawHandle(draw, node, isHovered);
				return;
			}

			// Ports and lines.
			float lineHeight = ImGui.GetTextLineHeight();
			int inputRow = 0;
			int outputRow = 0;
			foreach (GraphPort port in node.Ports)
			{
				bool input = port.Direction == GraphPortDirection.Input;
				int row = input ? inputRow++ : outputRow++;
				NVector2 portCenter = port.Center;
				uint portColor = ImGui.GetColorU32(port.Color);
				float radius = S(PortRadius);
				if (port.Connected || (dragPort == port))
				{
					draw.AddCircleFilled(portCenter, radius, portColor, 12);
				}
				else
				{
					draw.AddCircleFilled(portCenter, radius, body, 12);
					draw.AddCircle(portCenter, radius, portColor, 12, 1.5f);
				}

				string label = port.DisplayLabel;
				NVector2 labelSize = ImGui.CalcTextSize(label);
				if (input)
				{
					draw.AddText(new NVector2(portCenter.X + radius * 2f, portCenter.Y - labelSize.Y * 0.5f), text, label);
					if (!string.IsNullOrEmpty(port.Preview) && !port.Connected)
					{
						draw.AddText(new NVector2(portCenter.X + radius * 2f + labelSize.X + padding, portCenter.Y - labelSize.Y * 0.5f), dim, port.Preview);
					}
				}
				else
				{
					draw.AddText(new NVector2(portCenter.X - radius * 2f - labelSize.X, portCenter.Y - labelSize.Y * 0.5f), text, label);
				}
			}

			int rows = Math.Max(inputRow, outputRow);
			float y = node.BodyTop + padding + rows * (lineHeight + padding);
			foreach (GraphNodeLine line in node.Lines)
			{
				uint color = line.Color != null ? ImGui.GetColorU32(line.Color.Value) : line.Dim ? dim : text;
				draw.AddText(new NVector2(min.X + padding * 2f, y), color, line.Text);
				if (!string.IsNullOrEmpty(line.Right))
				{
					NVector2 rightSize = ImGui.CalcTextSize(line.Right);
					draw.AddText(new NVector2(max.X - padding * 2f - rightSize.X, y), dim, line.Right);
				}

				y += lineHeight + padding * 0.5f;
			}

			if (node.CustomDraw != null && node.CustomHeight > 0f)
			{
				var context = new GraphNodeDrawContext { DrawList = draw, Min = new NVector2(min.X + padding, y), Max = new NVector2(max.X - padding, y + S(node.CustomHeight)), Zoom = Zoom, Node = node };
				try
				{
					node.CustomDraw(context);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				y += S(node.CustomHeight);
			}

			if (node.Progress >= 0f)
			{
				float barHeight = S(4f);
				NVector2 barMin = new NVector2(min.X + padding, max.Y - barHeight - padding * 0.5f);
				NVector2 barMax = new NVector2(max.X - padding, max.Y - padding * 0.5f);
				draw.AddRectFilled(barMin, barMax, ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.4f)), S(2f));
				float t = Math.Clamp(node.Progress, 0f, 1f);
				draw.AddRectFilled(barMin, new NVector2(barMin.X + (barMax.X - barMin.X) * t, barMax.Y), ImGui.GetColorU32(node.ProgressColor), S(2f));
			}

			if (node.Resizable)
			{
				uint grip = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.3f));
				draw.AddTriangleFilled(max - new NVector2(S(10f), S(2f)), max - new NVector2(S(2f), S(2f)), max - new NVector2(S(2f), S(10f)), grip);
			}

			DrawHandle(draw, node, isHovered);
		}

		/// <summary>The connection handle of transition-style graphs: a circle at the node's right edge, shown on hover.</summary>
		private void DrawHandle(ImDrawListPtr draw, GraphNode node, bool isHovered)
		{
			if (!AllowNodeConnections || !node.ConnectableFrom)
			{
				return;
			}

			bool active = dragFromNode == node.Id || connecting == node.Id;
			if (!isHovered && !active)
			{
				return;
			}

			NVector2 handle = HandleCenter(node);
			uint color = ImGui.GetColorU32(active ? EditorStyle.Accent : new NVector4(1f, 1f, 1f, 0.85f));
			draw.AddCircleFilled(handle, S(HandleRadius), ImGui.GetColorU32(new NVector4(0.1f, 0.1f, 0.1f, 0.9f)), 16);
			draw.AddCircle(handle, S(HandleRadius), color, 16, 1.5f);
			float a = S(3f);
			draw.AddTriangleFilled(handle + new NVector2(-a * 0.7f, -a), handle + new NVector2(a, 0f), handle + new NVector2(-a * 0.7f, a), color);
		}

		private NVector2 HandleCenter(GraphNode node)
		{
			return new NVector2(node.ScreenMax.X + S(HandleRadius) + 2f, (node.ScreenMin.Y + node.ScreenMax.Y) * 0.5f);
		}

		private void DrawEdge(ImDrawListPtr draw, GraphEdge edge)
		{
			GraphNode from = FindNode(edge.From);
			GraphNode to = FindNode(edge.To);
			if (from == null || to == null)
			{
				return;
			}

			bool selected = Selection.Contains(edge.Id);
			bool isHovered = hovered && HoveredId == edge.Id;
			NVector4 color = selected ? EditorStyle.Selection : edge.Highlight ? EditorStyle.Accent : edge.Color;
			if (isHovered && !selected)
			{
				color = new NVector4(Math.Min(color.X + 0.2f, 1f), Math.Min(color.Y + 0.2f, 1f), Math.Min(color.Z + 0.2f, 1f), 1f);
			}

			uint colorU32 = ImGui.GetColorU32(color);
			float thickness = Math.Max(1f, edge.Thickness * (selected || edge.Highlight ? 1.5f : 1f) * Math.Max(Zoom, 0.6f));
			switch (edge.Style)
			{
				case GraphEdgeStyle.Bezier:
				case GraphEdgeStyle.FlowBezier:
				{
					GraphPort fromPort = from.FindPort(edge.FromPort);
					GraphPort toPort = to.FindPort(edge.ToPort);
					NVector2 a = fromPort?.Center ?? new NVector2(from.ScreenMax.X, (from.ScreenMin.Y + from.ScreenMax.Y) * 0.5f);
					NVector2 b = toPort?.Center ?? new NVector2(to.ScreenMin.X, (to.ScreenMin.Y + to.ScreenMax.Y) * 0.5f);
					edge.ScreenFrom = a;
					edge.ScreenTo = b;
					if (edge.Style == GraphEdgeStyle.Bezier)
					{
						float tangent = Math.Max(S(40f), Math.Abs(b.X - a.X) * 0.4f);
						draw.AddBezierCubic(a, a + new NVector2(tangent, 0f), b - new NVector2(tangent, 0f), b, colorU32, thickness);
						edge.LabelCenter = Bezier(a, a + new NVector2(tangent, 0f), b - new NVector2(tangent, 0f), b, 0.5f);
						if (edge.Progress >= 0f)
						{
							draw.AddCircleFilled(Bezier(a, a + new NVector2(tangent, 0f), b - new NVector2(tangent, 0f), b, Math.Clamp(edge.Progress, 0f, 1f)), S(4f), ImGui.GetColorU32(EditorStyle.Accent), 12);
						}
					}
					else
					{
						float tangent = Math.Max(S(30f), Math.Abs(b.Y - a.Y) * 0.4f);
						draw.AddBezierCubic(a, a + new NVector2(0f, tangent), b - new NVector2(0f, tangent), b, colorU32, thickness);
						edge.LabelCenter = Bezier(a, a + new NVector2(0f, tangent), b - new NVector2(0f, tangent), b, 0.5f);
					}

					break;
				}
				default:
				{
					NVector2 centerA = (from.ScreenMin + from.ScreenMax) * 0.5f;
					NVector2 centerB = (to.ScreenMin + to.ScreenMax) * 0.5f;
					if (from == to)
					{
						DrawSelfLoop(draw, edge, from, colorU32, thickness);
						break;
					}

					NVector2 direction = centerB - centerA;
					float length = direction.Length();
					if (length < 1f)
					{
						break;
					}

					direction /= length;
					var normal = new NVector2(-direction.Y, direction.X);
					float lane = edge.LaneCount > 1 ? (edge.LaneIndex == 0 ? -1f : 1f) * S(9f) : 0f;
					// Lanes are mirrored for the opposite direction so both edges sit on the same side of their own travel.
					if (edge.LaneCount > 1 && edge.From > edge.To)
					{
						lane = -lane;
					}

					NVector2 offset = normal * lane;
					NVector2 a = BorderPoint(from, centerA + offset, direction) + offset * 0f;
					NVector2 b = BorderPoint(to, centerB + offset, -direction);
					a += offset;
					b += offset;
					edge.ScreenFrom = a;
					edge.ScreenTo = b;
					if (edge.Dashed)
					{
						DrawDashed(draw, a, b, colorU32, thickness);
					}
					else
					{
						draw.AddLine(a, b, colorU32, thickness);
					}

					NVector2 middle = (a + b) * 0.5f;
					edge.LabelCenter = middle;
					if (edge.Style == GraphEdgeStyle.Arrow)
					{
						float size = S(7f) + thickness;
						NVector2 tip = middle + direction * size * 0.6f;
						NVector2 back = middle - direction * size * 0.6f;
						draw.AddTriangleFilled(tip, back + normal * size * 0.6f, back - normal * size * 0.6f, colorU32);
						if (edge.LaneCount > 1 || edges.Count > 40)
						{
							// Several transitions along the same way: a second arrow head shows there is more.
						}
					}

					if (edge.Progress >= 0f)
					{
						NVector2 dot = a + (b - a) * Math.Clamp(edge.Progress, 0f, 1f);
						draw.AddCircleFilled(dot, S(5f), ImGui.GetColorU32(EditorStyle.Accent), 12);
						draw.AddCircle(dot, S(5f), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.8f)), 12, 1f);
					}

					break;
				}
			}

			if (!string.IsNullOrEmpty(edge.Label))
			{
				NVector2 size = ImGui.CalcTextSize(edge.Label);
				NVector2 labelPosition = edge.LabelCenter - size * 0.5f + new NVector2(0f, -size.Y - S(4f));
				draw.AddRectFilled(labelPosition - new NVector2(S(3f), S(1f)), labelPosition + size + new NVector2(S(3f), S(1f)), ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.55f)), S(3f));
				draw.AddText(labelPosition, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.9f)), edge.Label);
			}
		}

		private void DrawSelfLoop(ImDrawListPtr draw, GraphEdge edge, GraphNode node, uint color, float thickness)
		{
			// A loop leaving the top right corner and coming back into the right side.
			NVector2 corner = new NVector2(node.ScreenMax.X - S(20f), node.ScreenMin.Y);
			NVector2 back = new NVector2(node.ScreenMax.X, node.ScreenMin.Y + S(20f));
			NVector2 c1 = corner + new NVector2(0f, -S(40f));
			NVector2 c2 = back + new NVector2(S(40f), 0f);
			draw.AddBezierCubic(corner, c1, c2, back, color, thickness);
			edge.ScreenFrom = corner;
			edge.ScreenTo = back;
			NVector2 middle = Bezier(corner, c1, c2, back, 0.5f);
			edge.LabelCenter = middle;
			NVector2 direction = NVector2.Normalize(Bezier(corner, c1, c2, back, 0.55f) - Bezier(corner, c1, c2, back, 0.45f));
			var normal = new NVector2(-direction.Y, direction.X);
			float size = S(7f) + thickness;
			draw.AddTriangleFilled(middle + direction * size * 0.6f, middle - direction * size * 0.6f + normal * size * 0.6f, middle - direction * size * 0.6f - normal * size * 0.6f, color);
		}

		private static void DrawDashed(ImDrawListPtr draw, NVector2 a, NVector2 b, uint color, float thickness)
		{
			NVector2 d = b - a;
			float length = d.Length();
			if (length < 1f)
			{
				return;
			}

			d /= length;
			float dash = 8f;
			for (float t = 0f; t < length; t += dash * 2f)
			{
				float end = Math.Min(t + dash, length);
				draw.AddLine(a + d * t, a + d * end, color, thickness);
			}
		}

		private static NVector2 Bezier(NVector2 p0, NVector2 p1, NVector2 p2, NVector2 p3, float t)
		{
			float u = 1f - t;
			return u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3;
		}

		/// <summary>The point where a ray from the node's centre in <paramref name="direction" /> leaves its rectangle.</summary>
		private static NVector2 BorderPoint(GraphNode node, NVector2 center, NVector2 direction)
		{
			float halfWidth = (node.ScreenMax.X - node.ScreenMin.X) * 0.5f;
			float halfHeight = (node.ScreenMax.Y - node.ScreenMin.Y) * 0.5f;
			float tx = Math.Abs(direction.X) > 1e-5f ? halfWidth / Math.Abs(direction.X) : float.MaxValue;
			float ty = Math.Abs(direction.Y) > 1e-5f ? halfHeight / Math.Abs(direction.Y) : float.MaxValue;
			float t = Math.Min(tx, ty);
			return center + direction * t;
		}

		private void DrawDragConnection(ImDrawListPtr draw)
		{
			NVector2 mouse = ImGui.GetMousePos();
			uint color = ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.8f));
			if (dragPort != null)
			{
				uint portColor = ImGui.GetColorU32(dragPort.Color);
				NVector2 a = dragPort.Center;
				bool input = dragPort.Direction == GraphPortDirection.Input;
				NVector2 from = input ? mouse : a;
				NVector2 to = input ? a : mouse;
				float tangent = Math.Max(S(40f), Math.Abs(to.X - from.X) * 0.4f);
				draw.AddBezierCubic(from, from + new NVector2(tangent, 0f), to - new NVector2(tangent, 0f), to, portColor, 2f);
			}
			else if (dragFromNode != 0 || connecting != 0)
			{
				GraphNode from = FindNode(dragFromNode != 0 ? dragFromNode : connecting);
				if (from != null)
				{
					NVector2 center = (from.ScreenMin + from.ScreenMax) * 0.5f;
					NVector2 direction = mouse - center;
					if (direction.Length() > 1f)
					{
						direction = NVector2.Normalize(direction);
						NVector2 a = BorderPoint(from, center, direction);
						draw.AddLine(a, mouse, color, 2f);
						var normal = new NVector2(-direction.Y, direction.X);
						float size = S(8f);
						draw.AddTriangleFilled(mouse, mouse - direction * size + normal * size * 0.6f, mouse - direction * size - normal * size * 0.6f, color);
					}
				}
			}
		}

		private void DrawMinimap(ImDrawListPtr draw)
		{
			if (nodes.Count == 0)
			{
				return;
			}

			var min = new NVector2(float.MaxValue);
			var max = new NVector2(float.MinValue);
			foreach (GraphNode node in nodes)
			{
				min = NVector2.Min(min, node.Position);
				max = NVector2.Max(max, node.Position + node.Size);
			}

			// The visible area counts too, so the viewport rectangle stays inside the map.
			NVector2 viewMin = ToGraph(canvasMin);
			NVector2 viewMax = ToGraph(canvasMin + canvasSize);
			min = NVector2.Min(min, viewMin);
			max = NVector2.Max(max, viewMax);
			NVector2 extent = NVector2.Max(max - min, new NVector2(1f, 1f));
			var size = new NVector2(170f, 110f);
			float scale = Math.Min(size.X / extent.X, size.Y / extent.Y);
			NVector2 mapSize = extent * scale + new NVector2(8f, 8f);
			minimapMin = canvasMin + canvasSize - mapSize - new NVector2(10f, 10f);
			minimapMax = minimapMin + mapSize;
			draw.AddRectFilled(minimapMin, minimapMax, ImGui.GetColorU32(new NVector4(0f, 0f, 0f, 0.55f)), 4f);
			draw.AddRect(minimapMin, minimapMax, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.25f)), 4f);
			NVector2 origin = minimapMin + new NVector2(4f, 4f);
			foreach (GraphNode node in nodes)
			{
				NVector2 a = origin + (node.Position - min) * scale;
				NVector2 b = origin + (node.Position + node.Size - min) * scale;
				uint color = Selection.Contains(node.Id) ? ImGui.GetColorU32(EditorStyle.Selection) : ImGui.GetColorU32(node.Outline ?? node.HeaderColor);
				draw.AddRectFilled(a, NVector2.Max(b, a + new NVector2(2f, 2f)), color, 1f);
			}

			NVector2 va = origin + (viewMin - min) * scale;
			NVector2 vb = origin + (viewMax - min) * scale;
			draw.AddRect(va, vb, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.7f)), 2f);

			// Click or drag inside the map moves the view.
			NVector2 mouse = ImGui.GetMousePos();
			bool inside = mouse.X >= minimapMin.X && mouse.X <= minimapMax.X && mouse.Y >= minimapMin.Y && mouse.Y <= minimapMax.Y;
			if (hovered && inside && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
			{
				minimapDragging = true;
			}

			if (minimapDragging && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				NVector2 target = min + (mouse - origin) / scale;
				Offset = canvasSize * 0.5f / Zoom - target;
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				minimapDragging = false;
			}
		}

		// ---------------------------------------------------------------- hit testing

		private enum HitKind
		{
			None,
			Node,
			Port,
			Handle,
			Edge,
			Group,
			Note,
			NoteResize,
			NodeResize,
			Minimap
		}

		private struct Hit
		{
			public HitKind Kind;
			public ulong Id;
			public GraphNode Node;
			public GraphPort Port;
			public GraphNote Note;
			public GraphGroup Group;
			public GraphEdge Edge;
		}

		private bool InMinimap(NVector2 p)
		{
			return ShowMinimap && nodes.Count > 0 && p.X >= minimapMin.X && p.X <= minimapMax.X && p.Y >= minimapMin.Y && p.Y <= minimapMax.Y;
		}

		private Hit HitTest(NVector2 p)
		{
			if (InMinimap(p))
			{
				return new Hit { Kind = HitKind.Minimap };
			}

			// Nodes on top (last drawn = last in the list), ports before their nodes.
			for (int i = nodes.Count - 1; i >= 0; i--)
			{
				GraphNode node = nodes[i];
				if (AllowPortConnections)
				{
					foreach (GraphPort port in node.Ports)
					{
						if (NVector2.DistanceSquared(p, port.Center) <= S(PortRadius + 4f) * S(PortRadius + 4f))
						{
							return new Hit { Kind = HitKind.Port, Id = node.Id, Node = node, Port = port };
						}
					}
				}

				if (AllowNodeConnections && node.ConnectableFrom && NVector2.DistanceSquared(p, HandleCenter(node)) <= S(HandleRadius + 3f) * S(HandleRadius + 3f))
				{
					return new Hit { Kind = HitKind.Handle, Id = node.Id, Node = node };
				}

				if (Inside(p, node.ScreenMin, node.ScreenMax))
				{
					if (node.Resizable && p.X >= node.ScreenMax.X - S(12f) && p.Y >= node.ScreenMax.Y - S(12f))
					{
						return new Hit { Kind = HitKind.NodeResize, Id = node.Id, Node = node };
					}

					return new Hit { Kind = HitKind.Node, Id = node.Id, Node = node };
				}
			}

			GraphEdge nearest = null;
			float best = 7f * 7f;
			foreach (GraphEdge edge in edges)
			{
				if (!edge.Selectable)
				{
					continue;
				}

				float distance = EdgeDistanceSquared(edge, p);
				if (distance < best)
				{
					best = distance;
					nearest = edge;
				}
			}

			if (nearest != null)
			{
				return new Hit { Kind = HitKind.Edge, Id = nearest.Id, Edge = nearest };
			}

			for (int i = notes.Count - 1; i >= 0; i--)
			{
				GraphNote note = notes[i];
				if (Inside(p, note.ScreenMin, note.ScreenMax))
				{
					if (p.X >= note.ScreenMax.X - S(12f) && p.Y >= note.ScreenMax.Y - S(12f))
					{
						return new Hit { Kind = HitKind.NoteResize, Id = note.Id, Note = note };
					}

					return new Hit { Kind = HitKind.Note, Id = note.Id, Note = note };
				}
			}

			float title = ImGui.GetTextLineHeight() * Zoom + S(8f);
			foreach (GraphGroup group in groups)
			{
				if (Inside(p, group.ScreenMin, new NVector2(group.ScreenMax.X, group.ScreenMin.Y + title)))
				{
					return new Hit { Kind = HitKind.Group, Id = group.Id, Group = group };
				}
			}

			return new Hit { Kind = HitKind.None };
		}

		private float EdgeDistanceSquared(GraphEdge edge, NVector2 p)
		{
			GraphNode from = FindNode(edge.From);
			GraphNode to = FindNode(edge.To);
			if (from == null || to == null)
			{
				return float.MaxValue;
			}

			if (edge.Style == GraphEdgeStyle.Arrow || edge.Style == GraphEdgeStyle.Line)
			{
				if (from == to)
				{
					return NVector2.DistanceSquared(p, edge.LabelCenter);
				}

				return SegmentDistanceSquared(p, edge.ScreenFrom, edge.ScreenTo);
			}

			// Beziers: sampled.
			NVector2 a = edge.ScreenFrom;
			NVector2 b = edge.ScreenTo;
			NVector2 c1;
			NVector2 c2;
			if (edge.Style == GraphEdgeStyle.Bezier)
			{
				float tangent = Math.Max(S(40f), Math.Abs(b.X - a.X) * 0.4f);
				c1 = a + new NVector2(tangent, 0f);
				c2 = b - new NVector2(tangent, 0f);
			}
			else
			{
				float tangent = Math.Max(S(30f), Math.Abs(b.Y - a.Y) * 0.4f);
				c1 = a + new NVector2(0f, tangent);
				c2 = b - new NVector2(0f, tangent);
			}

			float best = float.MaxValue;
			NVector2 previous = a;
			for (int i = 1; i <= 24; i++)
			{
				NVector2 point = Bezier(a, c1, c2, b, i / 24f);
				best = Math.Min(best, SegmentDistanceSquared(p, previous, point));
				previous = point;
			}

			return best;
		}

		private static float SegmentDistanceSquared(NVector2 p, NVector2 a, NVector2 b)
		{
			NVector2 ab = b - a;
			float lengthSquared = ab.LengthSquared();
			float t = lengthSquared > 1e-6f ? Math.Clamp(NVector2.Dot(p - a, ab) / lengthSquared, 0f, 1f) : 0f;
			return NVector2.DistanceSquared(p, a + ab * t);
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
			guides.Clear();
			bool popupOpen = ImGui.IsPopupOpen("##graph_create") || ImGui.IsPopupOpen("##graph_context") || ImGui.IsPopupOpen("##graph_rename");

			// Zoom around the cursor.
			if (hovered && Math.Abs(io.MouseWheel) > 0f && !popupOpen)
			{
				NVector2 before = ToGraph(mouse);
				Zoom = Math.Clamp(Zoom * (io.MouseWheel > 0f ? 1.1f : 1f / 1.1f), MinZoom, MaxZoom);
				NVector2 after = ToGraph(mouse);
				Offset += after - before;
			}

			// Pan: middle button, Alt + left, or the right button dragged.
			if (hovered && !popupOpen && (ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || ImGui.IsMouseClicked(ImGuiMouseButton.Right) || (io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left))))
			{
				panning = ImGui.IsMouseClicked(ImGuiMouseButton.Middle) || (io.KeyAlt && ImGui.IsMouseClicked(ImGuiMouseButton.Left));
				panStart = mouse;
				panOffsetStart = Offset;
				rightPress = mouse;
			}

			if (panning && (ImGui.IsMouseDown(ImGuiMouseButton.Middle) || ImGui.IsMouseDown(ImGuiMouseButton.Left)))
			{
				Offset = panOffsetStart + (mouse - panStart) / Zoom;
			}
			else if (ImGui.IsMouseDown(ImGuiMouseButton.Right) && NVector2.DistanceSquared(mouse, rightPress) > 36f && !popupOpen && !openCreate && !openContext)
			{
				panning = true;
				Offset = panOffsetStart + (mouse - panStart) / Zoom;
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Middle) || (panning && ImGui.IsMouseReleased(ImGuiMouseButton.Left)))
			{
				panning = false;
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Right))
			{
				bool moved = NVector2.DistanceSquared(mouse, rightPress) > 36f;
				panning = false;
				if (hovered && !moved && !popupOpen)
				{
					Hit hit = HitTest(mouse);
					if (connecting != 0)
					{
						connecting = 0;
					}
					else if (hit.Kind == HitKind.None)
					{
						if (CreateOnRightClick && CreateEntries != null)
						{
							OpenCreatePopup(ToGraph(mouse));
						}
						else
						{
							OpenContextMenu(GraphContextKind.Canvas, 0);
						}
					}
					else
					{
						if (!Selection.Contains(hit.Id) && hit.Kind != HitKind.Minimap)
						{
							Select(hit.Id, false);
						}

						OpenContextMenu(KindOf(hit.Kind), hit.Id);
					}
				}
			}

			// Left button.
			if (hovered && !popupOpen && ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !io.KeyAlt)
			{
				Hit hit = HitTest(mouse);
				bool additive = io.KeyCtrl || io.KeyShift;
				double now = ImGui.GetTime();
				bool doubleClick = hit.Id != 0 && hit.Id == lastClicked && now - lastClickTime < 0.35 && NVector2.DistanceSquared(mouse, lastMouse) < 25f;
				lastClicked = hit.Id;
				lastClickTime = now;
				lastMouse = mouse;
				if (connecting != 0)
				{
					if ((hit.Kind == HitKind.Node || hit.Kind == HitKind.Handle) && hit.Node.ConnectableTo)
					{
						ConnectRequested?.Invoke(new GraphConnection { From = connecting, To = hit.Id, FromPort = "", ToPort = "" });
					}
					else if (hit.Kind == HitKind.None && CreateEntries != null)
					{
						OpenCreatePopup(ToGraph(mouse), new GraphConnection { From = connecting, To = 0 });
					}

					connecting = 0;
					return;
				}

				switch (hit.Kind)
				{
					case HitKind.Minimap:
						break;
					case HitKind.Port:
						dragPort = hit.Port;
						dragPortNode = hit.Node;
						break;
					case HitKind.Handle:
						dragFromNode = hit.Id;
						break;
					case HitKind.NodeResize:
						resizing = hit.Id;
						resizeStartSize = hit.Node.Size;
						break;
					case HitKind.NoteResize:
						resizing = hit.Id;
						resizeStartSize = hit.Note.Size;
						break;
					case HitKind.Node:
					case HitKind.Note:
					case HitKind.Group:
						if (doubleClick && hit.Kind == HitKind.Node)
						{
							if (NodeDoubleClicked != null)
							{
								NodeDoubleClicked(hit.Id);
							}
							else
							{
								BeginRename(hit.Id);
							}

							break;
						}

						if (hit.Kind == HitKind.Node && !hit.Node.Selectable)
						{
							break;
						}

						if (additive)
						{
							if (!Selection.Remove(hit.Id))
							{
								Selection.Add(hit.Id);
								Primary = hit.Id;
							}

							SelectionChanged?.Invoke();
						}
						else if (!Selection.Contains(hit.Id))
						{
							Select(hit.Id, false);
						}
						else
						{
							Primary = hit.Id;
						}

						BeginNodeDrag(hit.Id);
						break;
					case HitKind.Edge:
						if (additive)
						{
							if (!Selection.Remove(hit.Id))
							{
								Selection.Add(hit.Id);
								Primary = hit.Id;
							}

							SelectionChanged?.Invoke();
						}
						else
						{
							Select(hit.Id, false);
						}

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
					dragAccumulated = delta;
					nodesMoved = true;
					ApplyDragPositions(delta);
				}
			}

			if (resizing != 0 && ImGui.IsMouseDown(ImGuiMouseButton.Left))
			{
				NVector2 delta = ImGui.GetMouseDragDelta(ImGuiMouseButton.Left) / Zoom;
				NVector2 size = NVector2.Max(resizeStartSize + delta, new NVector2(60f, 30f));
				if (nodesById.TryGetValue(resizing, out GraphNode node))
				{
					node.Width = size.X;
					node.Height = size.Y;
				}
				else if (notesById.TryGetValue(resizing, out GraphNote note))
				{
					note.Size = size;
				}
			}

			if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
			{
				if (draggingNodes)
				{
					draggingNodes = false;
					if (nodesMoved)
					{
						var moves = new List<GraphMove>();
						foreach (KeyValuePair<ulong, NVector2> entry in dragStart)
						{
							NVector2 position = DraggedPosition(entry.Key, entry.Value, dragAccumulated);
							moves.Add(new GraphMove { Id = entry.Key, Position = position, Size = nodesById.TryGetValue(entry.Key, out GraphNode moved) ? moved.Size : (notesById.TryGetValue(entry.Key, out GraphNote movedNote) ? movedNote.Size : NVector2.Zero) });
						}

						NodesMoved?.Invoke(moves);
					}

					dragStart.Clear();
					nodesMoved = false;
					guides.Clear();
				}

				if (resizing != 0)
				{
					NVector2 size = resizeStartSize;
					if (nodesById.TryGetValue(resizing, out GraphNode node)) size = new NVector2(node.Width, node.Height);
					else if (notesById.TryGetValue(resizing, out GraphNote note)) size = note.Size;
					NodeResized?.Invoke(new GraphMove { Id = resizing, Size = size });
					resizing = 0;
				}

				if (dragPort != null)
				{
					EndPortDrag(mouse);
				}

				if (dragFromNode != 0)
				{
					EndNodeDrag(mouse);
				}

				if (boxSelecting)
				{
					boxSelecting = false;
					NVector2 a = NVector2.Min(boxStart, mouse);
					NVector2 b = NVector2.Max(boxStart, mouse);
					if (NVector2.DistanceSquared(a, b) > 16f)
					{
						foreach (GraphNode node in nodes)
						{
							if (node.Selectable && node.ScreenMin.X >= a.X && node.ScreenMax.X <= b.X && node.ScreenMin.Y >= a.Y && node.ScreenMax.Y <= b.Y)
							{
								Selection.Add(node.Id);
								Primary = node.Id;
							}
						}

						foreach (GraphNote note in notes)
						{
							if (note.ScreenMin.X >= a.X && note.ScreenMax.X <= b.X && note.ScreenMin.Y >= a.Y && note.ScreenMax.Y <= b.Y)
							{
								Selection.Add(note.Id);
								Primary = note.Id;
							}
						}

						SelectionChanged?.Invoke();
					}
				}
			}

			// Tooltips.
			if (hovered && !popupOpen && !IsInteracting)
			{
				Hit hit = HitTest(mouse);
				string tooltip = hit.Kind == HitKind.Node ? hit.Node.Tooltip : hit.Kind == HitKind.Edge ? hit.Edge.Tooltip : null;
				if (!string.IsNullOrEmpty(tooltip))
				{
					ImGui.SetTooltip(tooltip);
				}
			}

			// Keys.
			if (focused && (hovered || IsInteracting) && !io.WantTextInput && !popupOpen)
			{
				if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
				{
					if (connecting != 0 || dragFromNode != 0 || dragPort != null)
					{
						CancelInteraction();
					}
					else
					{
						ClearSelection();
					}
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.Delete, false) || ImGui.IsKeyPressed(ImGuiKey.Backspace, false))
				{
					RequestDelete();
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.D, false))
				{
					DuplicateRequested?.Invoke(new List<ulong>(Selection));
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.C, false))
				{
					CopyRequested?.Invoke(new List<ulong>(Selection));
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.V, false))
				{
					PasteRequested?.Invoke(ToGraph(mouse));
				}
				else if (io.KeyCtrl && ImGui.IsKeyPressed(ImGuiKey.A, false))
				{
					foreach (GraphNode node in nodes)
					{
						if (node.Selectable) Selection.Add(node.Id);
					}

					SelectionChanged?.Invoke();
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.F, false) || ImGui.IsKeyPressed(ImGuiKey.Home, false))
				{
					if (Selection.Count > 0 && !ImGui.IsKeyPressed(ImGuiKey.Home, false)) FrameSelection();
					else FrameAll();
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.F2, false) && Primary != 0)
				{
					BeginRename(Primary);
				}
				else if (ImGui.IsKeyPressed(ImGuiKey.Space, false) && CreateEntries != null)
				{
					OpenCreatePopup(ToGraph(mouse));
				}
				else
				{
					NVector2 nudge = NVector2.Zero;
					float step = io.KeyShift ? 10f : 1f;
					if (ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) nudge.X -= step;
					if (ImGui.IsKeyPressed(ImGuiKey.RightArrow)) nudge.X += step;
					if (ImGui.IsKeyPressed(ImGuiKey.UpArrow)) nudge.Y -= step;
					if (ImGui.IsKeyPressed(ImGuiKey.DownArrow)) nudge.Y += step;
					if (nudge != NVector2.Zero && Selection.Count > 0)
					{
						var moves = new List<GraphMove>();
						foreach (GraphNode node in nodes)
						{
							if (Selection.Contains(node.Id) && node.Movable)
							{
								moves.Add(new GraphMove { Id = node.Id, Position = node.Position + nudge, Size = node.Size });
							}
						}

						foreach (GraphNote note in notes)
						{
							if (Selection.Contains(note.Id))
							{
								moves.Add(new GraphMove { Id = note.Id, Position = note.Position + nudge, Size = note.Size });
							}
						}

						if (moves.Count > 0)
						{
							NodesMoved?.Invoke(moves);
						}
					}
				}
			}
		}

		private static GraphContextKind KindOf(HitKind kind)
		{
			switch (kind)
			{
				case HitKind.Node:
				case HitKind.Port:
				case HitKind.Handle:
				case HitKind.NodeResize:
					return GraphContextKind.Node;
				case HitKind.Edge:
					return GraphContextKind.Edge;
				case HitKind.Group:
					return GraphContextKind.Group;
				case HitKind.Note:
				case HitKind.NoteResize:
					return GraphContextKind.Note;
				default:
					return GraphContextKind.Canvas;
			}
		}

		private void RequestDelete()
		{
			var ids = new List<ulong>();
			foreach (ulong id in Selection)
			{
				if (nodesById.TryGetValue(id, out GraphNode node))
				{
					if (node.Deletable) ids.Add(id);
				}
				else if (edgesById.TryGetValue(id, out GraphEdge edge))
				{
					if (edge.Deletable) ids.Add(id);
				}
				else
				{
					ids.Add(id);
				}
			}

			if (ids.Count > 0)
			{
				DeleteRequested?.Invoke(ids);
			}
		}

		private void BeginNodeDrag(ulong primary)
		{
			draggingNodes = true;
			nodesMoved = false;
			dragAccumulated = NVector2.Zero;
			dragPrimary = primary;
			dragStart.Clear();
			foreach (ulong id in Selection)
			{
				if (nodesById.TryGetValue(id, out GraphNode node))
				{
					if (node.Movable)
					{
						dragStart[id] = node.Position;
					}
				}
				else if (notesById.TryGetValue(id, out GraphNote note))
				{
					dragStart[id] = note.Position;
				}
				else if (groupsById.TryGetValue(id, out GraphGroup group))
				{
					// Dragging a group's title moves its members.
					foreach (ulong member in group.Members)
					{
						if (nodesById.TryGetValue(member, out GraphNode memberNode) && memberNode.Movable) dragStart[member] = memberNode.Position;
						else if (notesById.TryGetValue(member, out GraphNote memberNote)) dragStart[member] = memberNote.Position;
					}
				}
			}
		}

		/// <summary>Moves the dragged items for display (the host's data changes at the drop through NodesMoved).</summary>
		private void ApplyDragPositions(NVector2 delta)
		{
			NVector2 snapDelta = NVector2.Zero;
			if (ShowAlignmentGuides && nodesById.TryGetValue(dragPrimary, out GraphNode primary) && dragStart.TryGetValue(dragPrimary, out NVector2 primaryStart))
			{
				snapDelta = ComputeGuides(primary, primaryStart + delta);
			}

			foreach (KeyValuePair<ulong, NVector2> entry in dragStart)
			{
				NVector2 position = DraggedPosition(entry.Key, entry.Value, delta + snapDelta);
				if (nodesById.TryGetValue(entry.Key, out GraphNode node))
				{
					node.Position = position;
				}
				else if (notesById.TryGetValue(entry.Key, out GraphNote note))
				{
					note.Position = position;
				}
			}

			dragAccumulated = delta + snapDelta;
			ComputeLayouts();
		}

		private NVector2 DraggedPosition(ulong id, NVector2 start, NVector2 delta)
		{
			NVector2 position = start + delta;
			if (SnapToGrid && SnapSize > 0f)
			{
				position = new NVector2((float)Math.Round(position.X / SnapSize) * SnapSize, (float)Math.Round(position.Y / SnapSize) * SnapSize);
			}

			return position;
		}

		/// <summary>Alignment guides of the dragged node against the others; returns the snap correction in graph units.</summary>
		private NVector2 ComputeGuides(GraphNode primary, NVector2 proposed)
		{
			NVector2 size = primary.Size;
			float threshold = GuideSnap / Zoom;
			var correction = NVector2.Zero;
			bool snappedX = false;
			bool snappedY = false;
			float[] mine = { proposed.X, proposed.X + size.X * 0.5f, proposed.X + size.X };
			float[] mineY = { proposed.Y, proposed.Y + size.Y * 0.5f, proposed.Y + size.Y };
			foreach (GraphNode other in nodes)
			{
				if (other == primary || dragStart.ContainsKey(other.Id))
				{
					continue;
				}

				float[] theirs = { other.Position.X, other.Position.X + other.Size.X * 0.5f, other.Position.X + other.Size.X };
				float[] theirsY = { other.Position.Y, other.Position.Y + other.Size.Y * 0.5f, other.Position.Y + other.Size.Y };
				if (!snappedX)
				{
					for (int i = 0; i < 3 && !snappedX; i++)
					{
						for (int j = 0; j < 3; j++)
						{
							if (Math.Abs(mine[i] - theirs[j]) <= threshold)
							{
								correction.X = theirs[j] - mine[i];
								snappedX = true;
								float x = ToScreen(new NVector2(theirs[j], 0f)).X;
								float top = Math.Min(other.ScreenMin.Y, ToScreen(proposed).Y) - S(20f);
								float bottom = Math.Max(other.ScreenMax.Y, ToScreen(proposed + size).Y) + S(20f);
								guides.Add((new NVector2(x, top), new NVector2(x, bottom)));
								break;
							}
						}
					}
				}

				if (!snappedY)
				{
					for (int i = 0; i < 3 && !snappedY; i++)
					{
						for (int j = 0; j < 3; j++)
						{
							if (Math.Abs(mineY[i] - theirsY[j]) <= threshold)
							{
								correction.Y = theirsY[j] - mineY[i];
								snappedY = true;
								float y = ToScreen(new NVector2(0f, theirsY[j])).Y;
								float left = Math.Min(other.ScreenMin.X, ToScreen(proposed).X) - S(20f);
								float right = Math.Max(other.ScreenMax.X, ToScreen(proposed + size).X) + S(20f);
								guides.Add((new NVector2(left, y), new NVector2(right, y)));
								break;
							}
						}
					}
				}
			}

			return correction;
		}

		private void EndPortDrag(NVector2 mouse)
		{
			GraphPort source = dragPort;
			GraphNode sourceNode = dragPortNode;
			dragPort = null;
			dragPortNode = null;
			Hit hit = HitTest(mouse);
			if (hit.Kind == HitKind.Port && hit.Port != source && hit.Port.Direction != source.Direction)
			{
				bool fromInput = source.Direction == GraphPortDirection.Input;
				GraphConnection connection = fromInput
					? new GraphConnection { From = hit.Id, FromPort = hit.Port.Name, To = sourceNode.Id, ToPort = source.Name, FromInput = true }
					: new GraphConnection { From = sourceNode.Id, FromPort = source.Name, To = hit.Id, ToPort = hit.Port.Name };
				ConnectRequested?.Invoke(connection);
			}
			else if (hit.Kind == HitKind.None && CreateEntries != null)
			{
				OpenCreatePopup(ToGraph(mouse), new GraphConnection { From = sourceNode.Id, FromPort = source.Name, FromInput = source.Direction == GraphPortDirection.Input });
			}
		}

		private void EndNodeDrag(NVector2 mouse)
		{
			ulong from = dragFromNode;
			dragFromNode = 0;
			Hit hit = HitTest(mouse);
			if ((hit.Kind == HitKind.Node || hit.Kind == HitKind.Handle || hit.Kind == HitKind.Port) && hit.Node.ConnectableTo)
			{
				ConnectRequested?.Invoke(new GraphConnection { From = from, To = hit.Id, FromPort = "", ToPort = "" });
			}
			else if (hit.Kind == HitKind.None && CreateEntries != null)
			{
				OpenCreatePopup(ToGraph(mouse), new GraphConnection { From = from, To = 0 });
			}
		}

		private void Frame(List<GraphNode> targets, bool fitZoom)
		{
			if (targets == null || targets.Count == 0 || canvasSize.X <= 0f)
			{
				return;
			}

			var min = new NVector2(float.MaxValue);
			var max = new NVector2(float.MinValue);
			foreach (GraphNode node in targets)
			{
				NVector2 size = node.Size.X > 0f ? node.Size : new NVector2(node.Width > 0f ? node.Width : MinNodeWidth, 60f);
				min = NVector2.Min(min, node.Position);
				max = NVector2.Max(max, node.Position + size);
			}

			foreach (GraphNote note in notes)
			{
				if (targets == nodes)
				{
					min = NVector2.Min(min, note.Position);
					max = NVector2.Max(max, note.Position + note.Size);
				}
			}

			NVector2 extent = max - min + new NVector2(120f, 120f);
			if (fitZoom)
			{
				float zoomX = canvasSize.X / Math.Max(extent.X, 1f);
				float zoomY = canvasSize.Y / Math.Max(extent.Y, 1f);
				Zoom = Math.Clamp(Math.Min(zoomX, zoomY), 0.25f, 1.25f);
			}

			NVector2 center = (min + max) * 0.5f;
			Offset = canvasSize * 0.5f / Zoom - center;
		}

		// ---------------------------------------------------------------- popups

		private void DrawCreatePopup()
		{
			if (openCreate)
			{
				ImGui.OpenPopup("##graph_create");
				createSearch = "";
				createCache.Clear();
				if (CreateEntries != null)
				{
					createCache.AddRange(CreateEntries(createPosition, createConnection));
				}

				openCreate = false;
			}

			if (!ImGui.BeginPopup("##graph_create"))
			{
				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			ImGui.SetNextItemWidth(280f);
			bool enter = EditorGui.InputTextWithHint("##graph_search", "Search...", ref createSearch, 128, ImGuiInputTextFlags.EnterReturnsTrue);
			ImGui.Separator();
			GraphMenuEntry chosen = null;
			bool filtered = createSearch.Length > 0;
			if (ImGui.BeginChild("##graph_entries", new NVector2(280f, Math.Min(360f, Math.Max(createCache.Count, 1) * ImGui.GetTextLineHeightWithSpacing() + 8f))))
			{
				string lastCategory = null;
				GraphMenuEntry first = null;
				foreach (GraphMenuEntry entry in createCache)
				{
					string path = entry.Path ?? "";
					int slash = path.LastIndexOf('/');
					string category = slash >= 0 ? path.Substring(0, slash) : "";
					string name = slash >= 0 ? path.Substring(slash + 1) : path;
					if (filtered && path.IndexOf(createSearch, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					if (!filtered && category != lastCategory)
					{
						if (category.Length > 0)
						{
							ImGui.TextDisabled(category);
						}

						lastCategory = category;
					}

					first = first ?? entry;
					ImGui.BeginDisabled(!entry.Enabled);
					if (EditorGui.Selectable((filtered && category.Length > 0 ? category + " / " : "") + name + "##" + path))
					{
						chosen = entry;
					}

					ImGui.EndDisabled();
					if (!string.IsNullOrEmpty(entry.Tooltip) && ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(entry.Tooltip);
					}
				}

				if (enter && chosen == null && first != null && first.Enabled)
				{
					chosen = first;
				}

				if (createCache.Count == 0)
				{
					ImGui.TextDisabled("Nothing to create here.");
				}

				ImGui.EndChild();
			}

			ImGui.EndPopup();
			if (chosen != null)
			{
				ImGui.CloseCurrentPopup();
				try
				{
					chosen.Action?.Invoke(createPosition);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}

				createConnection = null;
			}
		}

		/// <summary>The connection a create entry should complete (while its popup is open): a port or node drag ended on empty canvas.</summary>
		public GraphConnection? PendingConnection => createConnection;

		private void DrawContextPopup()
		{
			if (openContext)
			{
				ImGui.OpenPopup("##graph_context");
				openContext = false;
			}

			if (!ImGui.BeginPopup("##graph_context"))
			{
				return;
			}

			GraphContextMenu request = contextRequest;
			try
			{
				ContextMenu?.Invoke(request);
			}
			catch (Exception e)
			{
				Debug.LogException(e);
			}

			if (ShowDefaultContextItems)
			{
				if (ContextMenu != null)
				{
					ImGui.Separator();
				}

				switch (request.Kind)
				{
					case GraphContextKind.Canvas:
						if (CreateEntries != null && EditorMenu.Item("Create...", "Space")) OpenCreatePopup(request.GraphPosition);
						if (PasteRequested != null && EditorMenu.Item("Paste", "Ctrl+V")) PasteRequested(request.GraphPosition);
						if (EditorMenu.Item("Frame All", "F")) FrameAll();
						if (EditorMenu.Item("Select All", "Ctrl+A"))
						{
							foreach (GraphNode node in nodes) if (node.Selectable) Selection.Add(node.Id);
							SelectionChanged?.Invoke();
						}

						break;
					case GraphContextKind.Node:
					{
						GraphNode node = FindNode(request.Id);
						if (AllowNodeConnections && node != null && node.ConnectableFrom && EditorMenu.Item("Make Transition")) StartConnecting(request.Id);
						if (node != null && node.Renamable && EditorMenu.Item("Rename", "F2")) BeginRename(request.Id);
						if (DuplicateRequested != null && EditorMenu.Item("Duplicate", "Ctrl+D")) DuplicateRequested(new List<ulong>(Selection));
						if (CopyRequested != null && EditorMenu.Item("Copy", "Ctrl+C")) CopyRequested(new List<ulong>(Selection));
						if (DeleteRequested != null && node != null && node.Deletable && EditorMenu.Item("Delete", "Del")) RequestDelete();
						if (Selection.Count > 1 && EditorMenu.Begin("Align"))
						{
							if (EditorMenu.Item("Left")) AlignSelection(0);
							if (EditorMenu.Item("Center (horizontal)")) AlignSelection(1);
							if (EditorMenu.Item("Right")) AlignSelection(2);
							if (EditorMenu.Item("Top")) AlignSelection(3);
							if (EditorMenu.Item("Center (vertical)")) AlignSelection(4);
							if (EditorMenu.Item("Bottom")) AlignSelection(5);
							ImGui.Separator();
							if (EditorMenu.Item("Distribute horizontally")) DistributeSelection(true);
							if (EditorMenu.Item("Distribute vertically")) DistributeSelection(false);
							EditorMenu.End();
						}

						if (EditorMenu.Item("Frame", "F")) FrameSelection();
						break;
					}
					case GraphContextKind.Edge:
					{
						GraphEdge edge = FindEdge(request.Id);
						if (DeleteRequested != null && edge != null && edge.Deletable && EditorMenu.Item("Delete", "Del")) RequestDelete();
						break;
					}
					case GraphContextKind.Group:
					case GraphContextKind.Note:
						if (DeleteRequested != null && EditorMenu.Item("Delete", "Del")) RequestDelete();
						break;
				}
			}

			ImGui.EndPopup();
		}

		private void DrawRenamePopup()
		{
			if (renameOpen)
			{
				GraphNode node = FindNode(renaming);
				if (node != null)
				{
					ImGui.SetNextWindowPos(node.ScreenMin + new NVector2(0f, -2f));
				}

				ImGui.OpenPopup("##graph_rename");
				renameOpen = false;
			}

			if (!ImGui.BeginPopup("##graph_rename"))
			{
				if (renaming != 0 && !renameOpen)
				{
					renaming = 0;
				}

				return;
			}

			if (ImGui.IsWindowAppearing())
			{
				ImGui.SetKeyboardFocusHere();
			}

			GraphNode target = FindNode(renaming);
			ImGui.SetNextItemWidth(Math.Max(140f, target != null ? target.ScreenMax.X - target.ScreenMin.X : 140f));
			bool done = EditorGui.InputText("##graph_rename_text", ref renameBuffer, 128, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
			if (done)
			{
				if (target != null && renameBuffer.Trim().Length > 0 && renameBuffer != target.Title)
				{
					RenameRequested?.Invoke(renaming, renameBuffer.Trim());
				}

				renaming = 0;
				ImGui.CloseCurrentPopup();
			}
			else if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
			{
				renaming = 0;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		#endregion
	}
}
