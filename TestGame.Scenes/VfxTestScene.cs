using LELEngine;
using LELEngine.VFX;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Showcase of the GPU particle system (vfx=1): a ground under the sun with four effects built in code through
	///     <see cref="VfxGraphBuilder" />: the default soft puffs, fireworks whose rockets spawn sparks through a GPU
	///     event, a rain of lit cubes (mesh output) bouncing in a box, and ribbons (strip output) swirling in turbulence.
	/// </summary>
	public static class VfxTestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, GITestScene.Options options)
		{
			BuildEnvironment(scene, options);

			// LEL_VFX_ONLY=puffs|fireworks|cubes|ribbons shows one effect; LEL_VFX_SOFT=0 and LEL_VFX_SORT=0 switch features off (bisecting).
			string only = System.Environment.GetEnvironmentVariable("LEL_VFX_ONLY") ?? "";
			bool soft = System.Environment.GetEnvironmentVariable("LEL_VFX_SOFT") != "0";
			bool sort = System.Environment.GetEnvironmentVariable("LEL_VFX_SORT") != "0";
			if (only.Length == 0 || only == "puffs")
			{
				VfxGraph puffs = VfxGraphBuilder.CreateDefault("Puffs");
				foreach (VfxNodeData node in puffs.Nodes)
				{
					if (node.Type == VfxContextNodes.OutputQuad)
					{
						node.SetSetting("SoftParticles", soft ? "1" : "0");
						node.SetSetting("Sort", sort ? "Auto" : "Off");
					}
				}

				Effect(scene, "Puffs", puffs, new Vector3(-2.5f, 0.4f, -2f));
			}

			if (only.Length == 0 || only == "fireworks") Effect(scene, "Fireworks", CreateFireworks(), new Vector3(0f, 0.2f, 4f));
			if (only.Length == 0 || only == "cubes") Effect(scene, "Cube Rain", CreateCubeRain(), new Vector3(2.5f, 3f, -1f));
			if (only.Length == 0 || only == "ribbons") Effect(scene, "Ribbons", CreateRibbons(), new Vector3(0f, 2.2f, -2f));
			GITestScene.ApplyHarness(scene, options);
		}

		/// <summary>The ground, the wall, the sun, the player camera and the lighting settings of the particle scenes.</summary>
		public static void BuildEnvironment(Scene scene, GITestScene.Options options)
		{
			BuildGround(scene);
			BuildLight(scene);
			GITestScene.BuildPlayer(scene);
			GITestScene.ApplySettings(options);
			Lighting.GI.GridSize = 32f;
			Lighting.GI.Center = new Vector3(0f, 4f, 0f);
			Lighting.Shadows.Distance = 24f;
			if (options.Sky)
			{
				scene.Settings.Environment.Skybox = GITestScene.CreateSkybox(options, SkyTestScene.SunDirection);
			}
		}

		#endregion

		#region PrivateMethods

		private static VisualEffect Effect(Scene scene, string name, VfxGraph graph, Vector3 position)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = position;
			VisualEffect effect = go.AddComponent<VisualEffect>();
			effect.visualEffectAsset = graph;
			return effect;
		}

		private static void BuildGround(Scene scene)
		{
			GameObject ground = scene.CreateGameObject("Ground");
			ground.transform.position = new Vector3(0f, -0.25f, 0f);
			ground.transform.localScale = new Vector3(12f, 0.25f, 12f);
			MeshRenderer renderer = ground.AddComponent<MeshRenderer>();
			renderer.SetMesh("cube.obj");
			renderer.SetMaterial("Floor.material");
			renderer.IsStatic = true;

			GameObject wall = scene.CreateGameObject("Wall");
			wall.transform.position = new Vector3(0f, 2f, 11f);
			wall.transform.localScale = new Vector3(12f, 2f, 0.25f);
			MeshRenderer wallRenderer = wall.AddComponent<MeshRenderer>();
			wallRenderer.SetMesh("cube.obj");
			wallRenderer.SetMaterial("LitGray.material");
			wallRenderer.IsStatic = true;
		}

		private static void BuildLight(Scene scene)
		{
			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(-SkyTestScene.SunDirection, Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.95f, 0.85f, 1f);
			sun.Strength = 1.2f;
			scene.Settings.Environment.AmbientColor = new Color4(0.5f, 0.6f, 0.8f, 1f);
			scene.Settings.Environment.AmbientStrength = 0.12f;
			scene.Settings.Environment.BackgroundColor = new Color4(0.18f, 0.3f, 0.55f, 1f);
		}

		/// <summary>Rockets rise from the ground and, when they die, spawn a burst of sparks (a GPU event) drawn as lines.</summary>
		private static VfxGraph CreateFireworks()
		{
			var b = new VfxGraphBuilder("Fireworks");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData burst = b.Block(spawn, "Block.PeriodicBurst");
			b.Inputs(burst, ("Count", VfxValue.FromFloat(3f)), ("Period", VfxValue.FromFloat(0.7f)));

			VfxNodeData rockets = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Rockets");
			b.Settings(rockets, ("Capacity", "64"), ("Space", "World"));
			b.Inputs(rockets, ("Bounds Center", VfxValue.FromVector3(0f, 4f, 0f)), ("Bounds Size", VfxValue.FromVector3(8f, 10f, 8f)));
			VfxNodeData lifetime = b.Block(rockets, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(0.9f)), ("Max", VfxValue.FromFloat(1.4f)));
			VfxNodeData position = b.Block(rockets, "Block.PositionShape");
			b.Settings(position, ("Shape", "Circle"));
			b.Inputs(position, ("Radius", VfxValue.FromFloat(1f)));
			VfxNodeData velocity = b.Block(rockets, "Block.VelocityCone");
			b.Inputs(velocity, ("Angle", VfxValue.FromFloat(12f)), ("Min Speed", VfxValue.FromFloat(7f)), ("Max Speed", VfxValue.FromFloat(9f)));
			VfxNodeData size = b.Block(rockets, "Block.Set.size");
			b.Inputs(size, ("Value", VfxValue.FromFloat(0.12f)));
			VfxNodeData color = b.Block(rockets, "Block.Set.color");
			b.Inputs(color, ("Value", VfxValue.FromVector3(1f, 0.8f, 0.4f)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 520f);
			b.Block(update, "Block.Gravity");
			VfxNodeData trigger = b.Block(update, "Block.TriggerOnDie");
			b.Settings(trigger, ("Event", "Explode"));
			b.Inputs(trigger, ("Count", VfxValue.FromUint(120)));

			VfxNodeData rocketOutput = b.Context(VfxContextNodes.OutputQuad, 0f, 820f);
			b.Settings(rocketOutput, ("Blend", "Additive"), ("Orient", "AlongVelocity"));
			b.Inputs(rocketOutput, ("Velocity Scale", VfxValue.FromFloat(0.05f)));

			// Sparks: a child system fed by the Explode event.
			VfxNodeData sparks = b.Context(VfxContextNodes.Initialize, 420f, 520f, "Sparks");
			b.Settings(sparks, ("Capacity", "8192"), ("Space", "World"));
			b.Inputs(sparks, ("Bounds Center", VfxValue.FromVector3(0f, 5f, 0f)), ("Bounds Size", VfxValue.FromVector3(12f, 12f, 12f)));
			VfxNodeData inheritPosition = b.Block(sparks, "Block.InheritSourceAttribute");
			b.Settings(inheritPosition, ("Attribute", "position"));
			VfxNodeData inheritColor = b.Block(sparks, "Block.InheritSourceAttribute");
			b.Settings(inheritColor, ("Attribute", "color"));
			VfxNodeData sparkLifetime = b.Block(sparks, "Block.Set.lifetime");
			b.Settings(sparkLifetime, ("Random", "Uniform"));
			b.Inputs(sparkLifetime, ("Min", VfxValue.FromFloat(0.6f)), ("Max", VfxValue.FromFloat(1.5f)));
			VfxNodeData sparkVelocity = b.Block(sparks, "Block.VelocityRandomDirection");
			b.Inputs(sparkVelocity, ("Min Speed", VfxValue.FromFloat(2f)), ("Max Speed", VfxValue.FromFloat(5f)));
			VfxNodeData sparkSize = b.Block(sparks, "Block.Set.size");
			b.Inputs(sparkSize, ("Value", VfxValue.FromFloat(0.05f)));

			VfxNodeData sparkUpdate = b.Context(VfxContextNodes.Update, 420f, 900f);
			b.Block(sparkUpdate, "Block.Gravity");
			VfxNodeData drag = b.Block(sparkUpdate, "Block.Drag");
			b.Inputs(drag, ("Drag Coefficient", VfxValue.FromFloat(1.5f)));
			VfxNodeData fade = b.Block(sparkUpdate, "Block.AlphaOverLife");
			b.Settings(fade, ("Composition", "Set"));
			VfxNodeData sparkOutput = b.Context(VfxContextNodes.OutputLine, 420f, 1200f);
			b.Settings(sparkOutput, ("Blend", "Additive"));
			b.Inputs(sparkOutput, ("Line Length", VfxValue.FromFloat(0.04f)));

			b.Flow(spawn, rockets);
			b.Flow(rockets, update);
			b.Flow(update, rocketOutput);
			// Port 1 of the update context: the first GPU event (Explode).
			b.Flow(update, sparks, 1, 0);
			b.Flow(sparks, sparkUpdate);
			b.Flow(sparkUpdate, sparkOutput);
			return b.Graph;
		}

		/// <summary>Small lit cubes fall inside an inverted box and bounce off its walls (mesh output, collisions).</summary>
		private static VfxGraph CreateCubeRain()
		{
			var b = new VfxGraphBuilder("CubeRain");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData rate = b.Block(spawn, "Block.ConstantSpawnRate");
			b.Inputs(rate, ("Rate", VfxValue.FromFloat(40f)));

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Cubes");
			b.Settings(initialize, ("Capacity", "400"));
			b.Inputs(initialize, ("Bounds Center", VfxValue.FromVector3(0f, -2f, 0f)), ("Bounds Size", VfxValue.FromVector3(4f, 6f, 4f)));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(6f)), ("Max", VfxValue.FromFloat(9f)));
			VfxNodeData position = b.Block(initialize, "Block.PositionShape");
			b.Settings(position, ("Shape", "Box"));
			b.Inputs(position, ("Size", VfxValue.FromVector3(2f, 0.2f, 2f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Settings(size, ("Random", "Uniform"));
			b.Inputs(size, ("Min", VfxValue.FromFloat(0.06f)), ("Max", VfxValue.FromFloat(0.14f)));
			VfxNodeData angularVelocity = b.Block(initialize, "Block.Set.angularVelocity");
			b.Settings(angularVelocity, ("Random", "PerComponent"));
			b.Inputs(angularVelocity, ("Min", VfxValue.FromVector3(-180f, -180f, -180f)), ("Max", VfxValue.FromVector3(180f, 180f, 180f)));
			VfxNodeData color = b.Block(initialize, "Block.ColorFromGradientRandom");
			var palette = new VfxGradient();
			palette.ColorKeys.Add(new VfxGradientKey(0f, new Color4(0.9f, 0.2f, 0.2f, 1f)));
			palette.ColorKeys.Add(new VfxGradientKey(0.5f, new Color4(0.2f, 0.9f, 0.3f, 1f)));
			palette.ColorKeys.Add(new VfxGradientKey(1f, new Color4(0.2f, 0.4f, 1f, 1f)));
			palette.AlphaKeys.Add(new VfxGradientKey(0f, Color4.White));
			palette.AlphaKeys.Add(new VfxGradientKey(1f, Color4.White));
			b.Inputs(color, ("Gradient", VfxValue.FromGradient(palette)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 620f);
			b.Block(update, "Block.Gravity");
			VfxNodeData box = b.Block(update, "Block.CollideBox");
			b.Settings(box, ("Mode", "Inverted"));
			b.Inputs(box, ("Center", VfxValue.FromVector3(0f, -2f, 0f)), ("Size", VfxValue.FromVector3(3.5f, 4f, 3.5f)), ("Bounce", VfxValue.FromFloat(0.6f)), ("Friction", VfxValue.FromFloat(0.1f)));

			VfxNodeData output = b.Context(VfxContextNodes.OutputMesh, 0f, 900f);
			b.Settings(output, ("Blend", "Opaque"), ("Lighting", "Lit"), ("CastShadows", "1"));
			b.Inputs(output, ("Mesh", VfxValue.FromMesh(InternalStorage.GetOrCreateMesh("cube.obj"))));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		/// <summary>Particles swirling in curl noise, drawn as ribbons through their recent positions.</summary>
		private static VfxGraph CreateRibbons()
		{
			var b = new VfxGraphBuilder("Ribbons");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData rate = b.Block(spawn, "Block.ConstantSpawnRate");
			b.Inputs(rate, ("Rate", VfxValue.FromFloat(12f)));

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Ribbons");
			b.Settings(initialize, ("Capacity", "128"), ("BoundsMode", "Automatic"));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(3f)), ("Max", VfxValue.FromFloat(5f)));
			VfxNodeData position = b.Block(initialize, "Block.PositionShape");
			b.Settings(position, ("Shape", "Sphere"), ("Mode", "Surface"));
			b.Inputs(position, ("Radius", VfxValue.FromFloat(0.4f)));
			VfxNodeData velocity = b.Block(initialize, "Block.VelocityRandomDirection");
			b.Inputs(velocity, ("Min Speed", VfxValue.FromFloat(0.5f)), ("Max Speed", VfxValue.FromFloat(1f)), ("Direction Blend", VfxValue.FromFloat(0.8f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Inputs(size, ("Value", VfxValue.FromFloat(0.35f)));
			VfxNodeData color = b.Block(initialize, "Block.ColorFromGradientRandom");
			var palette = new VfxGradient();
			palette.ColorKeys.Add(new VfxGradientKey(0f, new Color4(0.3f, 0.8f, 1f, 1f)));
			palette.ColorKeys.Add(new VfxGradientKey(1f, new Color4(1f, 0.5f, 0.9f, 1f)));
			palette.AlphaKeys.Add(new VfxGradientKey(0f, Color4.White));
			palette.AlphaKeys.Add(new VfxGradientKey(1f, Color4.White));
			b.Inputs(color, ("Gradient", VfxValue.FromGradient(palette)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 620f);
			VfxNodeData turbulence = b.Block(update, "Block.Turbulence");
			b.Inputs(turbulence, ("Intensity", VfxValue.FromFloat(3f)), ("Frequency", VfxValue.FromFloat(0.6f)), ("Drag", VfxValue.FromFloat(3f)));
			VfxNodeData attractor = b.Block(update, "Block.Attractor");
			b.Inputs(attractor, ("Strength", VfxValue.FromFloat(1.5f)));
			VfxNodeData fade = b.Block(update, "Block.AlphaOverLife");
			b.Settings(fade, ("Composition", "Set"));

			VfxNodeData output = b.Context(VfxContextNodes.OutputStrip, 0f, 900f);
			b.Settings(output, ("Blend", "Additive"), ("StripLength", "24"), ("StripSpacing", "0.06"));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		#endregion
	}
}
