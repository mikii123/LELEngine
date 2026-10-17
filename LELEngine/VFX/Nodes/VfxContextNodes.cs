using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.VFX
{
	/// <summary>The context node definitions: events, spawn, initialize, update and the output kinds.</summary>
	public static class VfxContextNodes
	{
		#region PublicFields

		public const string Event = "Context.Event";
		public const string Spawn = "Context.Spawn";
		public const string Initialize = "Context.Initialize";
		public const string Update = "Context.Update";
		public const string OutputQuad = "Context.OutputQuad";
		public const string OutputMesh = "Context.OutputMesh";
		public const string OutputPoint = "Context.OutputPoint";
		public const string OutputLine = "Context.OutputLine";
		public const string OutputStrip = "Context.OutputStrip";

		public static readonly string[] BlendOptions = { "Alpha", "Additive", "Premultiplied", "Opaque", "AlphaClip" };
		public static readonly string[] LightingOptions = { "Unlit", "Lit" };
		public static readonly string[] OrientOptions = { "FaceCameraPlane", "FaceCamera", "FixedAxis", "AlongVelocity", "AxisAlignedBillboard" };
		public static readonly string[] UvOptions = { "Simple", "Flipbook", "FlipbookBlend" };
		public static readonly string[] PrimitiveOptions = { "Quad", "Triangle", "Octagon" };
		public static readonly string[] SortOptions = { "Auto", "Off", "ByDistance" };

		#endregion

		#region PublicMethods

		public static void Register()
		{
			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = Event,
				Name = "Event",
				Category = "Context",
				Description = "An event raised by the component (OnPlay, OnStop) or by scripts (SendEvent); connects to the Start or Stop port of a spawn context.",
				Kind = VfxNodeKind.Context,
				ContextKind = VfxContextKind.Event,
				Settings = { VfxSettingDefinition.Text("Event", VfxCompiler.OnPlayEvent, "Name of the event (OnPlay, OnStop or a custom name sent by VisualEffect.SendEvent).") },
				FlowOutputs = new[] { "Out" },
				TitleFactory = node => "Event: " + node.GetSetting("Event", VfxCompiler.OnPlayEvent)
			});

			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = Spawn,
				Name = "Spawn",
				Category = "Context",
				Description = "Decides how many particles are created every frame (on the CPU). Its blocks add rates and bursts; a loop can repeat with delays.",
				Kind = VfxNodeKind.Context,
				ContextKind = VfxContextKind.Spawn,
				Settings = { VfxSettingDefinition.Enum("LoopMode", "Infinite", "Infinite", "Constant") },
				InputsFactory = node =>
				{
					var inputs = new List<VfxSlotDefinition>();
					if (node.GetSetting("LoopMode", "Infinite") == "Constant")
					{
						inputs.Add(new VfxSlotDefinition("Loop Duration", VfxValueType.Float, VfxValue.FromFloat(1f), "Seconds one loop lasts."));
						inputs.Add(new VfxSlotDefinition("Loop Count", VfxValueType.Int, VfxValue.FromInt(1), "Loops before the spawner stops (0 = forever)."));
						inputs.Add(new VfxSlotDefinition("Delay Before Loop", VfxValueType.Float, VfxValue.FromFloat(0f), "Seconds of silence before every loop."));
						inputs.Add(new VfxSlotDefinition("Delay After Loop", VfxValueType.Float, VfxValue.FromFloat(0f), "Seconds of silence after every loop."));
					}

					return inputs;
				},
				FlowInputs = new[] { "Start", "Stop" },
				FlowOutputs = new[] { "Out" }
			});

			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = Initialize,
				Name = "Initialize Particle",
				Category = "Context",
				Description = "Runs once for every spawned particle: its blocks set the starting attributes. Defines a particle system and its capacity.",
				Kind = VfxNodeKind.Context,
				ContextKind = VfxContextKind.Initialize,
				Settings =
				{
					VfxSettingDefinition.Int("Capacity", 1024, "Most particles alive at once (the GPU buffers are sized for it)."),
					VfxSettingDefinition.Enum("Space", "Local", "Local", "World").WithTooltip("Local: particles follow the component's transform. World: particles stay where they were emitted."),
					VfxSettingDefinition.Enum("BoundsMode", "Manual", "Manual", "Automatic").WithTooltip("Manual: the bounds below (culling). Automatic: measured on the GPU every frame.")
				},
				InputsFactory = node =>
				{
					var inputs = new List<VfxSlotDefinition>();
					if (node.GetSetting("BoundsMode", "Manual") != "Automatic")
					{
						inputs.Add(new VfxSlotDefinition("Bounds Center", VfxValueType.Vector3, VfxValue.FromVector3(Vector3.Zero), "Centre of the bounds in the system's space."));
						inputs.Add(new VfxSlotDefinition("Bounds Size", VfxValueType.Vector3, VfxValue.FromVector3(10f, 10f, 10f), "Size of the bounds in the system's space."));
					}

					return inputs;
				},
				FlowInputs = new[] { "In" },
				FlowOutputs = new[] { "Out" },
				TitleFactory = node => "Initialize Particle"
			});

			VfxNodeLibrary.Register(new VfxNodeDefinition
			{
				Type = Update,
				Name = "Update Particle",
				Category = "Context",
				Description = "Runs every frame for every alive particle: ages it, applies its blocks (forces, collisions), integrates its velocity and kills it at the end of its lifetime.",
				Kind = VfxNodeKind.Context,
				ContextKind = VfxContextKind.Update,
				Settings =
				{
					VfxSettingDefinition.Bool("AgeParticles", true, "Advance the age attribute by the frame time."),
					VfxSettingDefinition.Bool("ReapParticles", true, "Kill particles whose age reaches their lifetime."),
					VfxSettingDefinition.Bool("IntegratePosition", true, "Move particles by their velocity."),
					VfxSettingDefinition.Bool("IntegrateAngle", true, "Turn particles by their angular velocity."),
					VfxSettingDefinition.Bool("TrackOldPosition", false, "Keep the previous frame's position in the oldPosition attribute.")
				},
				FlowInputs = new[] { "In" },
				FlowOutputs = new[] { "Out" }
			});

			VfxNodeLibrary.Register(Output(OutputQuad, "Output Particle Quad", VfxOutputKind.Quad, "Draws every particle as a textured quad: a billboard, a fixed plane or stretched along its velocity.", true, true, node =>
			{
				var inputs = new List<VfxSlotDefinition> { new VfxSlotDefinition("Main Texture", VfxValueType.Texture2D, null, "Texture multiplied with the particle color (white when none).") };
				if (node.GetSetting("UvMode", "Simple") != "Simple")
				{
					inputs.Add(new VfxSlotDefinition("Flipbook Size", VfxValueType.Vector2, VfxValue.FromVector2(new Vector2(4f, 4f)), "Columns and rows of the texture sheet (texIndex picks the frame)."));
				}

				if (node.GetBool("SoftParticles", false))
				{
					inputs.Add(new VfxSlotDefinition("Soft Particle Distance", VfxValueType.Float, VfxValue.FromFloat(0.5f), "Distance over which the quad fades where it meets the scene."));
				}

				if (node.GetSetting("Primitive", "Quad") == "Octagon")
				{
					inputs.Add(new VfxSlotDefinition("Crop Factor", VfxValueType.Float, VfxValue.FromFloat(0.5f), "How much of the corners the octagon cuts off (0: a square, 1: a regular octagon)."));
				}

				switch (node.GetSetting("Orient", "FaceCameraPlane"))
				{
					case "FixedAxis":
						inputs.Add(new VfxSlotDefinition("Up Axis", VfxValueType.Vector3, VfxValue.FromVector3(0f, 1f, 0f)));
						inputs.Add(new VfxSlotDefinition("Right Axis", VfxValueType.Vector3, VfxValue.FromVector3(1f, 0f, 0f)));
						break;
					case "AlongVelocity":
						inputs.Add(new VfxSlotDefinition("Velocity Scale", VfxValueType.Float, VfxValue.FromFloat(0.1f), "Extra length per unit of speed (stretched billboards)."));
						break;
					case "AxisAlignedBillboard":
						inputs.Add(new VfxSlotDefinition("Axis", VfxValueType.Vector3, VfxValue.FromVector3(0f, 1f, 0f), "The quad turns around this axis to face the camera."));
						break;
				}

				return inputs;
			}));

			VfxNodeLibrary.Register(Output(OutputMesh, "Output Particle Mesh", VfxOutputKind.Mesh, "Draws every particle as an instance of a mesh (scaled by size and scale, turned by angle).", true, true, node =>
			{
				var inputs = new List<VfxSlotDefinition>
				{
					new VfxSlotDefinition("Mesh", VfxValueType.Mesh, null, "The mesh asset drawn per particle."),
					new VfxSlotDefinition("Main Texture", VfxValueType.Texture2D, null, "Texture multiplied with the particle color (white when none).")
				};
				if (node.GetBool("SoftParticles", false))
				{
					inputs.Add(new VfxSlotDefinition("Soft Particle Distance", VfxValueType.Float, VfxValue.FromFloat(0.5f)));
				}

				return inputs;
			}, "Opaque", "Lit", true));

			VfxNodeLibrary.Register(Output(OutputPoint, "Output Particle Point", VfxOutputKind.Point, "Draws every particle as a round point (size in world units at the particle's depth): the cheapest output.", false, false, node => new List<VfxSlotDefinition>(), "Additive"));

			VfxNodeDefinition line = Output(OutputLine, "Output Particle Line", VfxOutputKind.Line, "Draws a line from every particle along its velocity or to its target position.", false, false, node =>
			{
				var inputs = new List<VfxSlotDefinition>();
				if (node.GetSetting("LineMode", "Velocity") == "Velocity")
				{
					inputs.Add(new VfxSlotDefinition("Line Length", VfxValueType.Float, VfxValue.FromFloat(0.1f), "Seconds of velocity the line shows."));
				}

				return inputs;
			}, "Additive");
			line.Settings.Insert(0, VfxSettingDefinition.Enum("LineMode", "Velocity", "Velocity", "Target").WithTooltip("Velocity: from the position along the velocity. Target: to the targetPosition attribute."));
			VfxNodeLibrary.Register(line);

			VfxNodeDefinition strip = Output(OutputStrip, "Output Particle Strip", VfxOutputKind.Strip, "Draws a ribbon through the recent positions of every particle (a trail) facing the camera.", true, true, node =>
			{
				var inputs = new List<VfxSlotDefinition> { new VfxSlotDefinition("Main Texture", VfxValueType.Texture2D, null, "Texture along the strip (u across, v along).") };
				if (node.GetBool("SoftParticles", false))
				{
					inputs.Add(new VfxSlotDefinition("Soft Particle Distance", VfxValueType.Float, VfxValue.FromFloat(0.5f)));
				}

				inputs.Add(new VfxSlotDefinition("Width Over Strip", VfxValueType.Float, VfxValue.FromFloat(1f), "Multiplier of the strip width."));
				return inputs;
			});
			strip.Settings.Insert(0, VfxSettingDefinition.Int("StripLength", 16, "Recorded positions per particle; the ribbon has one segment less."));
			strip.Settings.Insert(1, VfxSettingDefinition.Float("StripSpacing", 0.05f, "Distance the particle travels between two recorded positions (0: one per frame); the tip always follows the particle."));
			strip.Settings.Insert(2, VfxSettingDefinition.Bool("StripTile", false, "Repeat the texture per segment instead of stretching it along the strip."));
			VfxNodeLibrary.Register(strip);
		}

		/// <summary>Names of a context's flow output ports: the definition's, plus one per GPU event raised by an update context's trigger blocks.</summary>
		public static string[] GetFlowOutputNames(VfxGraph graph, VfxNodeData node)
		{
			VfxNodeDefinition definition = VfxNodeLibrary.Find(node.Type);
			if (definition == null)
			{
				return new string[0];
			}

			if (definition.ContextKind != VfxContextKind.Update)
			{
				return definition.FlowOutputs;
			}

			var names = new List<string>(definition.FlowOutputs);
			foreach (VfxNodeData block in graph.GetBlocks(node))
			{
				VfxNodeDefinition blockDefinition = VfxNodeLibrary.Find(block.Type);
				if (blockDefinition != null && blockDefinition.Keywords.Contains("trigger"))
				{
					string eventName = block.GetSetting("Event", "OnDie");
					if (!names.Contains(eventName))
					{
						names.Add(eventName);
					}
				}
			}

			return names.ToArray();
		}

		#endregion

		#region PrivateMethods

		private static VfxNodeDefinition Output(string type, string name, VfxOutputKind kind, string description, bool textured, bool lit, System.Func<VfxNodeData, List<VfxSlotDefinition>> inputs, string defaultBlend = "Alpha", string defaultLighting = "Unlit", bool cullBack = false)
		{
			var definition = new VfxNodeDefinition
			{
				Type = type,
				Name = name,
				Category = "Context/Output",
				Description = description,
				Kind = VfxNodeKind.Context,
				ContextKind = VfxContextKind.Output,
				OutputKind = kind,
				InputsFactory = inputs,
				FlowInputs = new[] { "In" }
			};

			definition.Settings.Add(VfxSettingDefinition.Enum("Blend", defaultBlend, BlendOptions).WithTooltip("Alpha / Additive / Premultiplied blend in the transparent pass; Opaque and AlphaClip write depth before the sky."));
			if (lit)
			{
				definition.Settings.Add(VfxSettingDefinition.Enum("Lighting", defaultLighting, LightingOptions).WithTooltip("Lit: the sun with shadows, ambient and the GI volume shade the particle."));
				definition.Settings.Add(VfxSettingDefinition.Float("Roughness", 0.8f, "Roughness of lit particles (their indirect reflections)."));
				definition.Settings.Add(VfxSettingDefinition.Bool("ReceiveShadows", true));
				definition.Settings.Add(VfxSettingDefinition.Bool("ReceiveGI", true));
				definition.Settings.Add(VfxSettingDefinition.Bool("TwoSidedLighting", true, "Light back faces as if they faced the camera."));
			}

			if (kind == VfxOutputKind.Quad)
			{
				definition.Settings.Add(VfxSettingDefinition.Enum("Primitive", "Quad", PrimitiveOptions).WithTooltip("Quad: two triangles. Triangle: one triangle around the texture (fewer vertices, more fill). Octagon: the corners cut off (less fill where round textures are transparent)."));
				definition.Settings.Add(VfxSettingDefinition.Enum("Orient", "FaceCameraPlane", OrientOptions));
				definition.Settings.Add(VfxSettingDefinition.Float("NormalBending", 0f, "Lit quads: bend the normal towards the quad's edges (0 flat, 1 spherical)."));
			}

			if (textured)
			{
				definition.Settings.Add(VfxSettingDefinition.Enum("UvMode", "Simple", UvOptions).WithTooltip("Flipbook: the texture is a sheet of frames picked by texIndex; Blend interpolates between frames."));
				definition.Settings.Add(VfxSettingDefinition.Bool("SoftParticles", false, "Fade where the particle intersects the scene (reads the depth buffer).", true));
			}

			definition.Settings.Add(VfxSettingDefinition.Enum("Sort", "Auto", SortOptions).WithTooltip("Draw back to front (sorted on the GPU per view). Auto sorts alpha-blended outputs only."));
			definition.Settings.Add(VfxSettingDefinition.Bool("CastShadows", false, "Draw into the shadow map (alpha tested)."));
			definition.Settings.Add(VfxSettingDefinition.Bool("ZWrite", false, "Write depth (opaque outputs always do)."));
			definition.Settings.Add(VfxSettingDefinition.Bool("CullBack", cullBack, "Cull back faces."));
			definition.Settings.Add(VfxSettingDefinition.Float("AlphaThreshold", 0.5f, "AlphaClip: fragments below this alpha are discarded."));
			definition.Settings.Add(VfxSettingDefinition.Int("Queue", 3000, "Render queue among the transparent objects (higher draws later)."));
			return definition;
		}

		#endregion
	}
}
