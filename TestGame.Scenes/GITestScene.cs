using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Cornell-box style test room for lighting work: open-front room with colored side walls,
	///     a ceiling over most of it (dark interior for GI to fill), a few occluders, a sphere,
	///     a directional "sun" casting shadows and dynamic colored emitters orbiting inside.
	///     Walk around with <see cref="FpsController" />; see <see cref="GIDebugControls" /> for toggles.
	/// </summary>
	public static class GITestScene
	{
		#region PublicFields

		public const float RoomHalfSize = 10f;
		public const float WallHeight = 6f;
		public const float WallThickness = 0.25f;

		/// <summary>Startup switches, mainly for comparing renders from the command line.</summary>
		public sealed class Options
		{
			public bool GI = true;

			/// <summary>Lumen-style pipeline (surface cache + screen probes); false = voxel cone tracing.</summary>
			public bool Lumen = true;

			public bool VoxelView;
			public bool SdfView;
			public bool SurfaceCacheView;
			public bool Emitters = true;

			/// <summary>Animate the emitters (orbit, bobbing, pulsing). Off freezes them for stability measurements.</summary>
			public bool Animate = true;

			public bool Stats;

			/// <summary>Scripted camera motion (slow yaw and strafe) for recording temporal stability tests.</summary>
			public bool AutoCamera;

			/// <summary>Dump this many consecutive frames (BMP) to <see cref="DumpDirectory" /> after a warm-up.</summary>
			public int DumpFrames;

			public string DumpDirectory = "framedump";
			public float DumpAfterSeconds = 5f;

			/// <summary>Per-frame probe jitter (off for diagnostics).</summary>
			public bool ProbeJitter = true;

			/// <summary>Half-resolution screen-space cone tracing (GIResolvePass) instead of per-fragment.</summary>
			public bool Resolve = true;

			/// <summary>Mark room and props static so they are voxelized once.</summary>
			public bool StaticCache = true;

			/// <summary>Feed the previous frame's radiance back when lighting voxels (multi-bounce).</summary>
			public bool Bounce = true;

			/// <summary>Cone visibility from the global distance field instead of voxel alpha (experimental, stamps emitters).</summary>
			public bool SdfTrace;

			public int VoxelResolution = 128;
			public int SdfResolution = 128;

			// Lumen quality knobs (defaults mirror GlobalIlluminationSettings).
			public float ProbeAnchorJitter = 0f;
			public bool ProbeDirectionJitter = true;
			public int ProbeSpacing = 16;
			public int RadiosityRays = 4;
			public float RadiosityBlend = 0.9f;
			public float ProbeHistoryWeight = 0.5f;
			public float ProbeTemporalBlend = 0.9f;
			public bool ProbeImportanceSampling = true;
			public bool ProbeSpatialFilter = true;
			public int ProbeFilterRadius = 1;
			public int SurfaceCacheTexelsPerMeter = 6;
			public bool RadianceCache = true;
			public bool RadianceCacheForRadiosity = true;
			public float RadianceCacheNearDistance = 2f;
			public float RadianceCacheHistoryFrames = 20f;
			public int RadianceCacheProbesPerFrame = 160;
			public int RadianceCacheProbeResolution = 16;
			public int RadianceCacheTraceResolution = 32;
		}

		#endregion

		#region PublicMethods

		public static void Load(Scene scene)
		{
			Load(scene, new Options());
		}

		public static void Load(Scene scene, Options options)
		{
			markStatic = options.StaticCache;
			BuildRoom(scene);
			BuildProps(scene);
			BuildLight(scene);
			if (options.Emitters)
			{
				BuildEmitters(scene, options.Animate);
			}
			BuildPlayer(scene, options);

			if (options.VoxelView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.VoxelDebugPass>().Enabled = true;
			}
			if (options.SdfView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.SdfDebugPass>().Enabled = true;
			}
			if (options.SurfaceCacheView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.SurfaceCacheDebugPass>().Enabled = true;
			}

			// With GI on, the flat ambient only has to cover leaks; the bounce does the rest.
			Lighting.Ambient.Color = new Color4(0.55f, 0.6f, 0.75f, 1f);
			Lighting.Ambient.Strength = 0.06f;
			Lighting.Directional.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			Lighting.Directional.Strength = 1.25f;
			Lighting.Specular.Strength = 0.25f;
			Lighting.Specular.Shine = 48f;

			Lighting.Shadows.Enabled = true;
			Lighting.Shadows.MapSize = 2048;
			Lighting.Shadows.Distance = 30f;
			Lighting.Shadows.DepthRange = 60f;

			// Voxel volume tightly around the room: 26 m / 128 = ~0.2 m voxels.
			Lighting.GI.Enabled = options.GI;
			Lighting.GI.Mode = options.Lumen ? GIMode.Lumen : GIMode.VoxelConeTracing;
			Lighting.GI.Resolution = options.VoxelResolution;
			Lighting.GI.GridSize = 26f;
			Lighting.GI.Center = new Vector3(0f, 3.5f, 0f);
			Lighting.GI.FollowCamera = false;
			Lighting.GI.DiffuseStrength = 1.0f;
			Lighting.GI.SpecularStrength = 1.0f;
			Lighting.GI.OcclusionStrength = 1.0f;
			Lighting.GI.ProbeJitter = options.ProbeJitter;
			Lighting.GI.ProbeAnchorJitter = options.ProbeAnchorJitter;
			Lighting.GI.ProbeDirectionJitter = options.ProbeDirectionJitter;
			Lighting.GI.ProbeSpacing = options.ProbeSpacing;
			Lighting.GI.RadiosityRays = options.RadiosityRays;
			Lighting.GI.RadiosityBlend = options.RadiosityBlend;
			Lighting.GI.ProbeHistoryWeight = options.ProbeHistoryWeight;
			Lighting.GI.ProbeTemporalBlend = options.ProbeTemporalBlend;
			Lighting.GI.ProbeImportanceSampling = options.ProbeImportanceSampling;
			Lighting.GI.ProbeSpatialFilter = options.ProbeSpatialFilter;
			Lighting.GI.ProbeFilterRadius = options.ProbeFilterRadius;
			Lighting.GI.RadianceCacheEnabled = options.RadianceCache;
			Lighting.GI.RadianceCacheForRadiosity = options.RadianceCacheForRadiosity;
			Lighting.GI.RadianceCacheNearDistance = options.RadianceCacheNearDistance;
			Lighting.GI.RadianceCacheHistoryFrames = options.RadianceCacheHistoryFrames;
			Lighting.GI.RadianceCacheProbesPerFrame = options.RadianceCacheProbesPerFrame;
			Lighting.GI.RadianceCacheProbeResolution = options.RadianceCacheProbeResolution;
			Lighting.GI.RadianceCacheTraceResolution = options.RadianceCacheTraceResolution;
			Lighting.GI.SurfaceCacheTexelsPerMeter = options.SurfaceCacheTexelsPerMeter;
			Lighting.GI.ScreenSpaceResolve = options.Resolve;
			Lighting.GI.ResolveScale = 0.5f;
			Lighting.GI.DynamicUpdateInterval = 1;
			Lighting.GI.LightingUpdateInterval = 1;
			Lighting.GI.BounceStrength = options.Bounce ? 1f : 0f;
			Lighting.GI.BounceCones = 6;

			// Global SDF over the same grid; sky light enters through the open side of the room.
			Lighting.GI.DistanceFieldEnabled = true;
			Lighting.GI.SdfResolution = options.SdfResolution;
			Lighting.GI.SdfBandVoxels = 8;
			Lighting.GI.SdfMaxSteps = 48;
			Lighting.GI.TraceMode = options.SdfTrace ? GITraceMode.SdfDetail : GITraceMode.VoxelCones;
			Lighting.GI.SdfDetailDistance = 1.5f;
			// The sky the GI sees must be the sky the camera sees: same radiance as the clear color.
			Lighting.GI.SkyRadiance = new Vector3(0.45f, 0.6f, 0.85f);

			Game.Mono.Renderer.Settings.ClearColor = new Color4(0.45f, 0.6f, 0.85f, 1f);
			Game.Mono.Renderer.Settings.Exposure = 1.0f;
		}

		#endregion

		#region PrivateFields

		private static bool markStatic = true;

		#endregion

		#region PrivateMethods

		private static void BuildRoom(Scene scene)
		{
			float s = RoomHalfSize;
			float h = WallHeight;
			float t = WallThickness;

			// cube.obj spans -1..1, so scale is half-size.
			Box(scene, "Floor", "LitWhite.material", new Vector3(0f, -t, 0f), new Vector3(s + t, t, s + t));
			Box(scene, "BackWall", "LitWhite.material", new Vector3(0f, h * 0.5f, s), new Vector3(s + t, h * 0.5f, t));

			// Screen-right is -X when looking down +Z in this engine, so the classic
			// "red left / green right" Cornell layout puts red at +X.
			Box(scene, "LeftWall", "LitRed.material", new Vector3(s, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s));
			Box(scene, "RightWall", "LitGreen.material", new Vector3(-s, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s));

			// Ceiling over the back 70% of the room: a dark interior that only indirect light reaches.
			Box(scene, "Ceiling", "LitWhite.material", new Vector3(0f, h, s * 0.3f), new Vector3(s + t, t, s * 0.7f));
		}

		private static void BuildProps(Scene scene)
		{
			GameObject tall = Box(scene, "TallBox", "LitGray.material", new Vector3(-3.5f, 2f, 4f), new Vector3(1f, 2f, 1f));
			tall.transform.rotation = QuaternionHelper.Euler(0f, 20f, 0f);

			GameObject shortBox = Box(scene, "ShortBox", "LitGray.material", new Vector3(3f, 1f, 1.5f), new Vector3(1f, 1f, 1f));
			shortBox.transform.rotation = QuaternionHelper.Euler(0f, -15f, 0f);

			Box(scene, "Slab", "LitBlue.material", new Vector3(0f, 0.25f, -3f), new Vector3(2.5f, 0.25f, 1f));

			GameObject sphere = scene.CreateGameObject("Sphere");
			sphere.transform.position = new Vector3(0f, 1.75f, 5f);
			sphere.transform.scale = Vector3.One * 1.25f;
			MeshRenderer sphereRenderer = sphere.AddComponent<MeshRenderer>();
			sphereRenderer.SetMaterial("LitYellow.material");
			sphereRenderer.SetMesh("sphere.obj");
			sphereRenderer.IsStatic = markStatic;

			// Pillars under the ceiling edge; long thin shadows are a good test for bias settings.
			for (int i = -1; i <= 1; i++)
			{
				Box(scene, "Pillar" + i, "LitWhite.material", new Vector3(i * 6f, WallHeight * 0.5f, -4f), new Vector3(0.35f, WallHeight * 0.5f, 0.35f));
			}
		}

		private static void BuildLight(Scene scene)
		{
			GameObject light = scene.CreateGameObject("Sun");
			Vector3 lightDirection = new Vector3(-0.45f, -0.75f, 0.35f).Normalized();
			light.transform.rotation = QuaternionHelper.LookRotation(lightDirection, Vector3.UnitY);
			light.AddComponent<DirectionalLight>();

			// Visible sun disc far along the opposite of the light direction. Not a shadow caster, not voxelized.
			GameObject sunDisc = scene.CreateGameObject("SunDisc");
			sunDisc.transform.position = -lightDirection * 300f;
			sunDisc.transform.scale = Vector3.One * 12f;
			MeshRenderer sunRenderer = sunDisc.AddComponent<MeshRenderer>();
			sunRenderer.SetMaterial("Sun.material");
			sunRenderer.SetMesh("sphere.obj");
			sunRenderer.CastShadows = false;
			sunRenderer.ReceiveShadows = false;
			sunRenderer.ContributesToGI = false;
			sunRenderer.ReceiveGI = false;
		}

		private static void BuildEmitters(Scene scene, bool animate)
		{
			// Three colored orbs circling under the ceiling; their bounce light should tint ceiling and floor.
			Vector3 orbitCenter = new Vector3(0f, 0f, 3f);
			Color4[] colors = { new Color4(1f, 0.25f, 0.1f, 1f), new Color4(0.2f, 1f, 0.3f, 1f), new Color4(0.25f, 0.4f, 1f, 1f) };
			EmissiveOrbiter[] emitters = new EmissiveOrbiter[colors.Length + 2];
			for (int i = 0; i < colors.Length; i++)
			{
				EmissiveOrbiter orbiter = Emitter(scene, "Orb" + i, "sphere.obj", Vector3.One * 0.6f);
				orbiter.Center = orbitCenter;
				orbiter.Radius = 6f;
				orbiter.Height = 1.4f;
				orbiter.Bobbing = 0.4f;
				orbiter.AngularSpeed = 0.5f;
				orbiter.PhaseOffset = i * MathHelper.TwoPi / colors.Length;
				orbiter.Color = colors[i];
				// Radiance units: a sunlit white surface is ~1, so a few times that reads as a lamp.
				orbiter.Intensity = 4f;
				orbiter.PulseSpeed = 1.5f + i * 0.4f;
				orbiter.PulseAmount = 0.4f;
				emitters[i] = orbiter;
			}

			// Static ceiling lamp panel, slowly breathing.
			EmissiveOrbiter lamp = Emitter(scene, "CeilingLamp", "cube.obj", new Vector3(2f, 0.08f, 1f));
			lamp.Center = new Vector3(0f, WallHeight - WallThickness - 0.1f, 5f);
			lamp.Radius = 0f;
			lamp.Height = 0f;
			lamp.Bobbing = 0f;
			lamp.Color = new Color4(1f, 0.92f, 0.8f, 1f);
			lamp.Intensity = 2.5f;
			lamp.PulseSpeed = 0.7f;
			lamp.PulseAmount = 0.6f;
			emitters[colors.Length] = lamp;

			// Low wall-washer strip along the back wall.
			EmissiveOrbiter strip = Emitter(scene, "FloorStrip", "cube.obj", new Vector3(6f, 0.06f, 0.15f));
			strip.Center = new Vector3(0f, 0.1f, RoomHalfSize - 0.6f);
			strip.Radius = 0f;
			strip.Height = 0f;
			strip.Bobbing = 0f;
			strip.Color = new Color4(0.3f, 0.9f, 1f, 1f);
			strip.Intensity = 3f;
			strip.PulseSpeed = 1.1f;
			strip.PulseAmount = 0.5f;
			emitters[colors.Length + 1] = strip;

			if (!animate)
			{
				// Frozen at their phase-0 positions and full intensity: a static scene with emissive objects.
				foreach (EmissiveOrbiter emitter in emitters)
				{
					emitter.AngularSpeed = 0f;
					emitter.Bobbing = 0f;
					emitter.PulseAmount = 0f;
				}
			}
		}

		private static void BuildPlayer(Scene scene, Options options)
		{
			GameObject player = scene.CreateGameObject("Player");
			player.transform.position = new Vector3(0f, 1.7f, -8f);
			player.transform.rotation = Quaternion.Identity;

			GameObject cameraObject = scene.CreateGameObject("MainCamera");
			Camera camera = cameraObject.AddComponent<Camera>();
			camera.FoV = 75f;
			camera.NearClip = 0.05f;
			camera.FarClip = 500f;
			cameraObject.transform.SetParent(player.transform);
			cameraObject.transform.localPosition = Vector3.Zero;
			cameraObject.transform.localRotation = Quaternion.Identity;

			FpsController controller = player.AddComponent<FpsController>();
			controller.CameraTransform = cameraObject.transform;
			controller.EyeHeight = 1.7f;
			controller.AutoPilot = options.AutoCamera;

			GIDebugControls controls = player.AddComponent<GIDebugControls>();
			controls.PrintStats = options.Stats;
			controls.DumpFrames = options.DumpFrames;
			controls.DumpDirectory = options.DumpDirectory;
			controls.DumpAfterSeconds = options.DumpAfterSeconds;
		}

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = center;
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.AddComponent<MeshRenderer>();
			renderer.SetMaterial(material);
			renderer.SetMesh("cube.obj");
			renderer.IsStatic = markStatic;
			return go;
		}

		private static EmissiveOrbiter Emitter(Scene scene, string name, string mesh, Vector3 halfSize)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.AddComponent<MeshRenderer>();
			renderer.SetMaterial("Emitter.material");
			renderer.SetMesh(mesh);
			renderer.CastShadows = false;

			return go.AddComponent<EmissiveOrbiter>();
		}

		#endregion
	}
}
