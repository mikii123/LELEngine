using System;
using System.Collections.Generic;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor.Graphs
{
	/// <summary>Outline of a node.</summary>
	public enum GraphNodeShape
	{
		/// <summary>A rounded rectangle with a header bar (the usual node).</summary>
		Box,

		/// <summary>A pill (Unity's Entry / Exit / Any State nodes).</summary>
		Capsule,

		/// <summary>A diamond (decision nodes).</summary>
		Diamond,

		/// <summary>A circle (small junction nodes).</summary>
		Circle
	}

	/// <summary>How an edge is drawn.</summary>
	public enum GraphEdgeStyle
	{
		/// <summary>Port to port, horizontal tangents (data flow graphs).</summary>
		Bezier,

		/// <summary>Port to port, vertical tangents (top to bottom flow).</summary>
		FlowBezier,

		/// <summary>Node to node, a straight line between the borders with an arrow head half way (state machines).</summary>
		Arrow,

		/// <summary>Node to node, a straight line without an arrow.</summary>
		Line
	}

	public enum GraphPortDirection
	{
		Input,
		Output
	}

	/// <summary>What a context menu was opened on.</summary>
	public enum GraphContextKind
	{
		Canvas,
		Node,
		Edge,
		Group,
		Note
	}

	/// <summary>A connection slot on a node's side; edges of <see cref="GraphEdgeStyle.Bezier" /> / <see cref="GraphEdgeStyle.FlowBezier" /> attach to ports.</summary>
	public sealed class GraphPort
	{
		/// <summary>Unique within the node (edges name it).</summary>
		public string Name = "";

		/// <summary>Text next to the port (default: <see cref="Name" />).</summary>
		public string Label;

		public GraphPortDirection Direction;
		public NVector4 Color = new NVector4(0.8f, 0.8f, 0.8f, 1f);

		/// <summary>Several edges may attach (outputs usually; inputs usually one).</summary>
		public bool AllowMultiple = true;

		/// <summary>Dimmed text after the label (an unconnected input's value).</summary>
		public string Preview;

		/// <summary>Anything the host wants back (a type, a slot definition) in connect requests.</summary>
		public object Tag;

		/// <summary>Set by the view: the port's centre on screen after the last layout.</summary>
		public NVector2 Center { get; internal set; }

		/// <summary>Set by the view: an edge attaches to it.</summary>
		public bool Connected { get; internal set; }

		public string DisplayLabel => Label ?? Name;
	}

	/// <summary>A text row inside a node's body.</summary>
	public sealed class GraphNodeLine
	{
		public string Text = "";
		public NVector4? Color;
		public bool Dim;

		/// <summary>A second text at the right edge (a value).</summary>
		public string Right;

		public GraphNodeLine()
		{ }

		public GraphNodeLine(string text, bool dim = false)
		{
			Text = text;
			Dim = dim;
		}
	}

	/// <summary>What the view draws for a node this frame; the host adds one per node between Begin and End.</summary>
	public sealed class GraphNode
	{
		/// <summary>Unique across nodes, edges, groups and notes of the view (selection and callbacks use it).</summary>
		public ulong Id;

		public string Title = "";

		/// <summary>Smaller text under the title (a clip name, a type).</summary>
		public string Subtitle;

		/// <summary>Top left corner in graph units (the host's data; the view reports moves through <see cref="GraphView.NodesMoved" />).</summary>
		public NVector2 Position;

		/// <summary>Width in graph units (0: as wide as the content needs, at least <see cref="GraphView.MinNodeWidth" />).</summary>
		public float Width;

		/// <summary>Height in graph units for resizable nodes (notes); 0: from the content.</summary>
		public float Height;

		public GraphNodeShape Shape = GraphNodeShape.Box;
		public NVector4 HeaderColor = new NVector4(0.32f, 0.34f, 0.4f, 1f);
		public NVector4? BodyColor;

		/// <summary>Text color of the title (default: the theme's).</summary>
		public NVector4? TitleColor;

		public readonly List<GraphPort> Ports = new List<GraphPort>();
		public readonly List<GraphNodeLine> Lines = new List<GraphNodeLine>();

		/// <summary>A bar at the bottom of the node filled to this fraction (play mode progress); below 0: none.</summary>
		public float Progress = -1f;

		public NVector4 ProgressColor = new NVector4(0.95f, 0.65f, 0.2f, 1f);

		/// <summary>A thick outline in this color (the active state, the default state, errors); null: by selection only.</summary>
		public NVector4? Outline;

		public float OutlineThickness = 2f;

		/// <summary>Small text in the header's right corner ("3", "default").</summary>
		public string Badge;

		public NVector4? BadgeColor;

		public string Tooltip;
		public bool Selectable = true;
		public bool Movable = true;
		public bool Deletable = true;
		public bool Renamable = true;

		/// <summary>Dragging the bottom right corner changes <see cref="Width" /> / <see cref="Height" /> (<see cref="GraphView.NodeResized" />).</summary>
		public bool Resizable;

		/// <summary>A node a transition-style edge may start from by dragging its handle (<see cref="GraphView.AllowNodeConnections" />).</summary>
		public bool ConnectableFrom = true;

		public bool ConnectableTo = true;

		/// <summary>Draws over the body after the lines (draw list, screen rectangle, zoom).</summary>
		public Action<GraphNodeDrawContext> CustomDraw;

		/// <summary>Extra height in graph units reserved for <see cref="CustomDraw" />.</summary>
		public float CustomHeight;

		public object Tag;

		/// <summary>Screen rectangle after the last layout (End).</summary>
		public NVector2 ScreenMin { get; internal set; }

		public NVector2 ScreenMax { get; internal set; }

		/// <summary>Size in graph units after the last layout.</summary>
		public NVector2 Size { get; internal set; }

		internal float HeaderHeight;
		internal float BodyTop;

		public GraphPort FindPort(string name)
		{
			foreach (GraphPort port in Ports)
			{
				if (port.Name == name)
				{
					return port;
				}
			}

			return null;
		}

		public GraphPort AddPort(string name, GraphPortDirection direction, NVector4? color = null, string label = null)
		{
			var port = new GraphPort { Name = name, Direction = direction, Label = label };
			if (color != null)
			{
				port.Color = color.Value;
			}

			Ports.Add(port);
			return port;
		}

		public GraphNodeLine AddLine(string text, bool dim = false)
		{
			var line = new GraphNodeLine(text, dim);
			Lines.Add(line);
			return line;
		}
	}

	/// <summary>Passed to <see cref="GraphNode.CustomDraw" />.</summary>
	public struct GraphNodeDrawContext
	{
		public Hexa.NET.ImGui.ImDrawListPtr DrawList;

		/// <summary>The area reserved by <see cref="GraphNode.CustomHeight" /> (screen pixels).</summary>
		public NVector2 Min;

		public NVector2 Max;
		public float Zoom;
		public GraphNode Node;
	}

	/// <summary>A connection drawn this frame.</summary>
	public sealed class GraphEdge
	{
		public ulong Id;
		public ulong From;

		/// <summary>Port names for port styles; empty for node to node styles.</summary>
		public string FromPort = "";

		public ulong To;
		public string ToPort = "";
		public NVector4 Color = new NVector4(0.85f, 0.85f, 0.9f, 0.9f);
		public float Thickness = 2f;

		/// <summary>Text drawn by the arrow head (a transition count, a condition summary).</summary>
		public string Label;

		public GraphEdgeStyle Style = GraphEdgeStyle.Bezier;

		/// <summary>A dot travelling along the edge at this fraction (a running transition); below 0: none.</summary>
		public float Progress = -1f;

		public bool Highlight;
		public bool Dashed;
		public bool Selectable = true;
		public bool Deletable = true;
		public string Tooltip;
		public object Tag;

		/// <summary>Edges in both directions between the same nodes are offset sideways so both are visible.</summary>
		internal int LaneIndex;

		internal int LaneCount = 1;
		internal NVector2 ScreenFrom;
		internal NVector2 ScreenTo;
		internal NVector2 LabelCenter;
	}

	/// <summary>A frame drawn around member nodes with a title.</summary>
	public sealed class GraphGroup
	{
		public ulong Id;
		public string Title = "Group";
		public NVector4 Color = new NVector4(0.4f, 0.5f, 0.7f, 1f);
		public readonly List<ulong> Members = new List<ulong>();
		public object Tag;
		public NVector2 ScreenMin { get; internal set; }
		public NVector2 ScreenMax { get; internal set; }
	}

	/// <summary>A sticky note: free text in a colored box, resizable.</summary>
	public sealed class GraphNote
	{
		public ulong Id;
		public string Text = "";
		public NVector2 Position;
		public NVector2 Size = new NVector2(200f, 80f);
		public NVector4 Color = new NVector4(0.95f, 0.85f, 0.4f, 1f);
		public object Tag;
		public NVector2 ScreenMin { get; internal set; }
		public NVector2 ScreenMax { get; internal set; }
	}

	/// <summary>An entry of the create (search) popup: "Category/Name", run with the graph position it was opened at.</summary>
	public sealed class GraphMenuEntry
	{
		public string Path = "";
		public string Tooltip;
		public Action<NVector2> Action;
		public bool Enabled = true;

		public GraphMenuEntry()
		{ }

		public GraphMenuEntry(string path, Action<NVector2> action, string tooltip = null)
		{
			Path = path;
			Action = action;
			Tooltip = tooltip;
		}
	}

	/// <summary>A node's new position after a drag (or a nudge, or an auto layout).</summary>
	public struct GraphMove
	{
		public ulong Id;
		public NVector2 Position;

		/// <summary>Resizes: the new size (graph units).</summary>
		public NVector2 Size;
	}

	/// <summary>A connection the user asked for (a port or node drag ended on a target, or a "connect" action).</summary>
	public struct GraphConnection
	{
		public ulong From;
		public string FromPort;
		public ulong To;
		public string ToPort;

		/// <summary>The drag started on an input port (or a node's incoming side): the host may read it as To -> From.</summary>
		public bool FromInput;

		public bool IsNodeToNode => string.IsNullOrEmpty(FromPort) && string.IsNullOrEmpty(ToPort);
	}

	/// <summary>A context menu request: the host draws menu items (EditorGUILayout / ImGui.MenuItem) inside.</summary>
	public struct GraphContextMenu
	{
		public GraphContextKind Kind;

		/// <summary>The node, edge, group or note under the mouse (0 for the canvas).</summary>
		public ulong Id;

		/// <summary>Where the menu was opened, in graph units.</summary>
		public NVector2 GraphPosition;

		/// <summary>The selected ids at that moment (the clicked node included).</summary>
		public IReadOnlyList<ulong> Selection;
	}
}
