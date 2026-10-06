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
			public bool VoxelView;
			public bool Emitters = true;
			public bool Stats;
		}

		#endregion

		#region PublicMethods

		public static void Load(Scene scene)
		{
			Load(scene, new Options());
		}

		public static void Load(Scene scene, Options options)
		{
			BuildRoom(scene);
			BuildProps(scene);
			BuildLight(scene);
			if (options.Emitters)
			{
				BuildEmitters(scene);
			}
			BuildPlayer(scene, options);

			if (options.VoxelView)
			{
				Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.VoxelDebugPass>().Enabled = true;
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
			Lighting.GI.Resolution = 128;
			Lighting.GI.GridSize = 26f;
			Lighting.GI.Center = new Vector3(0f, 3.5f, 0f);
			Lighting.GI.FollowCamera = false;
			Lighting.GI.DiffuseStrength = 1.0f;
			Lighting.GI.SpecularStrength = 1.0f;
			Lighting.GI.OcclusionStrength = 1.0f;

			Game.Mono.Renderer.Settings.ClearColor = new Color4(0.45f, 0.6f, 0.85f, 1f);
			Game.Mono.Renderer.Settings.Exposure = 1.0f;
		}

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

		private static void BuildEmitters(Scene scene)
		{
			// Three colored orbs circling under the ceiling; their bounce light should tint ceiling and floor.
			Vector3 orbitCenter = new Vector3(0f, 0f, 3f);
			Color4[] colors = { new Color4(1f, 0.25f, 0.1f, 1f), new Color4(0.2f, 1f, 0.3f, 1f), new Color4(0.25f, 0.4f, 1f, 1f) };
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

			GIDebugControls controls = player.AddComponent<GIDebugControls>();
			controls.PrintStats = options.Stats;
		}

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = center;
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.AddComponent<MeshRenderer>();
			renderer.SetMaterial(material);
			renderer.SetMesh("cube.obj");
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
