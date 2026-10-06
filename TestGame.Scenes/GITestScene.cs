using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Cornell-box style test room for lighting work: open-top room with colored side walls,
	///     a few occluders of different heights, a sphere, and a directional "sun" casting shadows.
	///     Walk around with <see cref="FpsController" />.
	/// </summary>
	public static class GITestScene
	{
		#region PublicFields

		public const float RoomHalfSize = 10f;
		public const float WallHeight = 6f;
		public const float WallThickness = 0.25f;

		#endregion

		#region PublicMethods

		public static void Load(Scene scene)
		{
			BuildRoom(scene);
			BuildProps(scene);
			BuildLight(scene);
			BuildPlayer(scene);

			Lighting.Ambient.Color = new Color4(0.55f, 0.6f, 0.75f, 1f);
			Lighting.Ambient.Strength = 0.25f;
			Lighting.Directional.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			Lighting.Directional.Strength = 1.25f;
			Lighting.Specular.Strength = 0.25f;
			Lighting.Specular.Shine = 48f;

			Lighting.Shadows.Enabled = true;
			Lighting.Shadows.MapSize = 2048;
			Lighting.Shadows.Distance = 30f;
			Lighting.Shadows.DepthRange = 60f;

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

			// Partial ceiling over the back half so there is a dark interior area that GI should later fill.
			Box(scene, "Ceiling", "LitWhite.material", new Vector3(0f, h, s * 0.5f), new Vector3(s + t, t, s * 0.5f));
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
				Box(scene, "Pillar" + i, "LitWhite.material", new Vector3(i * 6f, WallHeight * 0.5f, 0f), new Vector3(0.35f, WallHeight * 0.5f, 0.35f));
			}
		}

		private static void BuildLight(Scene scene)
		{
			GameObject light = scene.CreateGameObject("Sun");
			Vector3 lightDirection = new Vector3(-0.45f, -0.75f, 0.35f).Normalized();
			light.transform.rotation = QuaternionHelper.LookRotation(lightDirection, Vector3.UnitY);
			light.AddComponent<DirectionalLight>();

			// Visible sun disc far along the opposite of the light direction. Not a shadow caster.
			GameObject sunDisc = scene.CreateGameObject("SunDisc");
			sunDisc.transform.position = -lightDirection * 300f;
			sunDisc.transform.scale = Vector3.One * 12f;
			MeshRenderer sunRenderer = sunDisc.AddComponent<MeshRenderer>();
			sunRenderer.SetMaterial("Sun.material");
			sunRenderer.SetMesh("sphere.obj");
			sunRenderer.CastShadows = false;
			sunRenderer.ReceiveShadows = false;
		}

		private static void BuildPlayer(Scene scene)
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

		#endregion
	}
}
