using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>
	///     Builds graphs in code (test scenes, templates): contexts, blocks, operators and their connections with
	///     the positions the editor shows them at. <see cref="CreateDefault" /> is the graph a new .vfx asset starts with.
	/// </summary>
	public sealed class VfxGraphBuilder
	{
		#region PublicFields

		public VfxGraph Graph { get; }

		#endregion

		#region Constructors

		public VfxGraphBuilder(string name = "VisualEffect")
		{
			Graph = new VfxGraph { name = name };
		}

		public VfxGraphBuilder(VfxGraph graph)
		{
			Graph = graph;
		}

		#endregion

		#region PublicMethods

		/// <summary>Adds a node of any kind at a position.</summary>
		public VfxNodeData Node(string type, float x, float y)
		{
			return Graph.AddNode(type, new Vector2(x, y));
		}

		/// <summary>Adds a context node.</summary>
		public VfxNodeData Context(string type, float x, float y, string title = null)
		{
			VfxNodeData node = Graph.AddNode(type, new Vector2(x, y));
			node.Title = title;
			return node;
		}

		/// <summary>Adds a block at the end of a context.</summary>
		public VfxNodeData Block(VfxNodeData context, string type)
		{
			return Graph.AddBlock(context, type);
		}

		/// <summary>Sets settings on a node (name, value pairs); the slots rebuild for settings that affect them.</summary>
		public VfxGraphBuilder Settings(VfxNodeData node, params (string name, string value)[] settings)
		{
			foreach ((string name, string value) in settings)
			{
				node.SetSetting(name, value);
			}

			RebuildSlots(node);
			return this;
		}

		/// <summary>Sets inline input values on a node.</summary>
		public VfxGraphBuilder Inputs(VfxNodeData node, params (string name, VfxValue value)[] inputs)
		{
			foreach ((string name, VfxValue value) in inputs)
			{
				node.SetInput(name, value);
			}

			return this;
		}

		public VfxEdge Connect(VfxNodeData from, string fromSlot, VfxNodeData to, string toSlot)
		{
			return Graph.Connect(from, fromSlot, to, toSlot);
		}

		public VfxFlowEdge Flow(VfxNodeData from, VfxNodeData to, int fromPort = 0, int toPort = 0)
		{
			return Graph.ConnectFlow(from, fromPort, to, toPort);
		}

		public VfxProperty Property(string name, VfxValue value, bool exposed = true)
		{
			return Graph.AddProperty(name, value, exposed);
		}

		/// <summary>A Property node reading a blackboard property.</summary>
		public VfxNodeData PropertyNode(string propertyName, float x, float y)
		{
			VfxNodeData node = Graph.AddNode(VfxNodeLibrary.PropertyNodeType, new Vector2(x, y));
			node.SetSetting("Property", propertyName);
			VfxProperty property = Graph.FindProperty(propertyName);
			node.SetSetting("PropertyType", (property?.Value.Type ?? VfxValueType.Float).ToString());
			return node;
		}

		/// <summary>Re-creates the inline values of a node after its settings changed the slots (keeps existing values).</summary>
		public void RebuildSlots(VfxNodeData node)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			if (definition == null)
			{
				return;
			}

			List<VfxSlotDefinition> slots = definition.GetInputs(node);
			var kept = new List<VfxSlotValue>();
			foreach (VfxSlotDefinition slot in slots)
			{
				VfxSlotValue existing = node.FindInput(slot.Name);
				if (existing != null && existing.Value != null && (existing.Value.Type == slot.Type || VfxValueTypes.CanConvert(existing.Value.Type, slot.Type)))
				{
					existing.Value = existing.Value.Type == slot.Type ? existing.Value : existing.Value.ConvertTo(slot.Type);
					kept.Add(existing);
				}
				else
				{
					kept.Add(new VfxSlotValue(slot.Name, slot.CreateDefault()));
				}
			}

			node.Inputs = kept;
		}

		/// <summary>
		///     The default graph of a new asset: a steady spawn of soft puffs from a small sphere that drift upwards,
		///     grow, fade out and sway in turbulence; one exposed color and rate.
		/// </summary>
		public static VfxGraph CreateDefault(string name = "New VFX")
		{
			var b = new VfxGraphBuilder(name);
			b.Property("Rate", VfxValue.FromFloat(24f));
			b.Property("Color", VfxValue.FromColor(1f, 0.85f, 0.6f, 1f));

			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData rate = b.Block(spawn, "Block.ConstantSpawnRate");
			VfxNodeData rateProperty = b.PropertyNode("Rate", -260f, 40f);
			b.Connect(rateProperty, "Value", rate, "Rate");

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f);
			b.Settings(initialize, ("Capacity", "2000"), ("BoundsMode", "Manual"));
			b.Inputs(initialize, ("Bounds Center", VfxValue.FromVector3(0f, 2f, 0f)), ("Bounds Size", VfxValue.FromVector3(6f, 6f, 6f)));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(1.5f)), ("Max", VfxValue.FromFloat(3f)));
			VfxNodeData position = b.Block(initialize, "Block.PositionShape");
			b.Settings(position, ("Shape", "Sphere"), ("Mode", "Volume"));
			b.Inputs(position, ("Radius", VfxValue.FromFloat(0.3f)));
			VfxNodeData velocity = b.Block(initialize, "Block.VelocityDirection");
			b.Inputs(velocity, ("Direction", VfxValue.FromVector3(0f, 1f, 0f)), ("Min Speed", VfxValue.FromFloat(0.6f)), ("Max Speed", VfxValue.FromFloat(1.2f)), ("Direction Blend", VfxValue.FromFloat(0.5f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Settings(size, ("Random", "Uniform"));
			b.Inputs(size, ("Min", VfxValue.FromFloat(0.25f)), ("Max", VfxValue.FromFloat(0.5f)));
			VfxNodeData color = b.Block(initialize, "Block.Set.color");
			VfxNodeData colorProperty = b.PropertyNode("Color", -260f, 420f);
			b.Connect(colorProperty, "Value", color, "Value");
			VfxNodeData angle = b.Block(initialize, "Block.Set.angularVelocity");
			b.Settings(angle, ("Random", "Uniform"));
			b.Inputs(angle, ("Min", VfxValue.FromVector3(0f, 0f, -40f)), ("Max", VfxValue.FromVector3(0f, 0f, 40f)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 760f);
			VfxNodeData turbulence = b.Block(update, "Block.Turbulence");
			b.Inputs(turbulence, ("Intensity", VfxValue.FromFloat(1.5f)), ("Frequency", VfxValue.FromFloat(0.8f)), ("Drag", VfxValue.FromFloat(2f)));
			VfxNodeData gravity = b.Block(update, "Block.Gravity");
			b.Inputs(gravity, ("Force", VfxValue.FromVector3(0f, 0.4f, 0f)));

			VfxNodeData output = b.Context(VfxContextNodes.OutputQuad, 0f, 1030f);
			b.Settings(output, ("Blend", "Alpha"), ("SoftParticles", "1"));
			VfxNodeData sizeOverLife = b.Block(output, "Block.SizeOverLife");
			b.Inputs(sizeOverLife, ("Size", VfxValue.FromCurve(VfxCurve.Linear(0.5f, 1.5f))));
			VfxNodeData colorOverLife = b.Block(output, "Block.ColorOverLife");
			var gradient = new VfxGradient();
			gradient.ColorKeys.Add(new VfxGradientKey(0f, Color4.White));
			gradient.ColorKeys.Add(new VfxGradientKey(1f, Color4.White));
			gradient.AlphaKeys.Add(new VfxGradientKey(0f, new Color4(1f, 1f, 1f, 0f)));
			gradient.AlphaKeys.Add(new VfxGradientKey(0.15f, new Color4(1f, 1f, 1f, 0.6f)));
			gradient.AlphaKeys.Add(new VfxGradientKey(1f, new Color4(1f, 1f, 1f, 0f)));
			b.Inputs(colorOverLife, ("Gradient", VfxValue.FromGradient(gradient)));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		/// <summary>An empty graph with the four contexts chained and nothing inside them.</summary>
		public static VfxGraph CreateEmpty(string name = "New VFX")
		{
			var b = new VfxGraphBuilder(name);
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f);
			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 420f);
			VfxNodeData output = b.Context(VfxContextNodes.OutputQuad, 0f, 640f);
			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		#endregion
	}
}
