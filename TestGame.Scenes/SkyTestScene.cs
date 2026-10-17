using LELEngine;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Open-air yard for the skybox and the sky light of the GI: a ground slab under the open sky, a
	///     free-standing wall, a roofed shelter the sky cannot reach directly, colored blocks, a pillar, a sphere
	///     and a canopy. The skybox fills every pixel without geometry and every GI ray that leaves the geometry
	///     reads it (sky=0 keeps the flat sky color instead, skybox=path uses a project asset). The directional
	///     light shines from the sun disc of the procedural sky.
	/// </summary>
	public static class SkyTestScene
	{
		#region PublicFields

		public const float GroundHalfSize = 20f;

		/// <summary>
		///     Towards the sun: ahead of the start view, a little to the left, about 24 degrees up. The sun disc is in
		///     view and the faces the camera sees are lit by the sky alone.
		/// </summary>
		public static readonly Vector3 SunDirection = new Vector3(0.35f, 0.4f, 0.85f).Normalized();

		#endregion

		#region PublicMethods

		/// <summary>Builds the yard, its sun and player, the GI settings of the room on a larger volume, and the skybox.</summary>
		public static void Load(Scene scene, GITestScene.Options options)
		{
			isStatic = options.StaticCache;
			BuildGround(scene);
			BuildProps(scene);
			BuildLight(scene, options.Sky);
			GITestScene.BuildPlayer(scene);
			GITestScene.ApplySettings(options);

			// A larger GI volume than the room's: 40 m / 128 = ~0.3 m voxels over the whole yard.
			Lighting.GI.GridSize = 40f;
			Lighting.GI.Center = new Vector3(0f, 6f, 0f);
			Lighting.Shadows.Distance = 40f;

			if (options.Sky)
			{
				scene.Settings.Environment.Skybox = GITestScene.CreateSkybox(options, SunDirection);
			}

			GITestScene.ApplyHarness(scene, options);
		}

		#endregion

		#region PrivateFields

		private static bool isStatic = true;

		#endregion

		#region PrivateMethods

		private static void BuildGround(Scene scene)
		{
			// cube.obj spans -1..1, so the scale is the half size; the top of the slab is at y = 0. Gray, so the sky's
			// tint shows and the sunlit ground stays below white (there is no tonemapping).
			Box(scene, "Ground", "LitGray.material", new Vector3(0f, -0.25f, 0f), new Vector3(GroundHalfSize, 0.25f, GroundHalfSize));
		}

		private static void BuildProps(Scene scene)
		{
			// A free-standing wall across the back: the camera side is sunlit, the back only sees the sky.
			Box(scene, "Wall", "LitWhite.material", new Vector3(0f, 2f, 9f), new Vector3(6f, 2f, 0.25f));

			// A shelter open towards the camera (screen-left is +X): under its roof only bounced sky light arrives.
			Box(scene, "ShelterLeft", "LitWhite.material", new Vector3(8.5f, 1.75f, 4f), new Vector3(0.25f, 1.75f, 2.5f));
			Box(scene, "ShelterRight", "LitWhite.material", new Vector3(4.5f, 1.75f, 4f), new Vector3(0.25f, 1.75f, 2.5f));
			Box(scene, "ShelterBack", "LitWhite.material", new Vector3(6.5f, 1.75f, 6.25f), new Vector3(2.25f, 1.75f, 0.25f));
			Box(scene, "ShelterRoof", "LitWhite.material", new Vector3(6.5f, 3.5f, 4f), new Vector3(2.25f, 0.25f, 2.5f));
			Box(scene, "ShelterBox", "LitGray.material", new Vector3(6.5f, 0.5f, 4.5f), new Vector3(0.5f, 0.5f, 0.5f));

			// Colored blocks on the other side: their bounce tints the ground, the sky tints their shaded faces.
			GameObject red = Box(scene, "RedBlock", "LitRed.material", new Vector3(-5f, 1f, 3f), new Vector3(1f, 1f, 1f));
			red.transform.rotation = QuaternionHelper.Euler(0f, 25f, 0f);
			Box(scene, "GreenBlock", "LitGreen.material", new Vector3(-7.5f, 0.75f, 0f), new Vector3(0.75f, 0.75f, 0.75f));
			Box(scene, "BlueSlab", "LitBlue.material", new Vector3(-3f, 0.2f, -1f), new Vector3(2f, 0.2f, 1f));

			// A tall pillar for a long shadow and a sphere for the sky's gradient on a curved surface.
			Box(scene, "Pillar", "LitGray.material", new Vector3(2f, 2.5f, 1f), new Vector3(0.4f, 2.5f, 0.4f));
			GameObject sphere = SceneModels.Instantiate(scene, SceneModels.Sphere, "Sphere");
			sphere.transform.position = new Vector3(0f, 1.25f, 4f);
			sphere.transform.scale = Vector3.One * 1.25f;
			MeshRenderer sphereRenderer = sphere.GetComponent<MeshRenderer>();
			sphereRenderer.SetMaterials(SceneModels.Material("LitYellow.material"));
			sphereRenderer.IsStatic = isStatic;

			// A thin canopy on two posts near the start: the ground under it sees less of the sky.
			Box(scene, "Canopy", "LitWhite.material", new Vector3(0f, 2.6f, -3f), new Vector3(2f, 0.08f, 1.5f));
			for (int i = -1; i <= 1; i += 2)
			{
				Box(scene, "CanopyPost" + i, "LitWhite.material", new Vector3(i * 1.8f, 1.3f, -3f), new Vector3(0.1f, 1.3f, 0.1f));
			}
		}

		private static void BuildLight(Scene scene, bool skybox)
		{
			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(-SunDirection, Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			// Low enough that sunlit white faces plus the sky do not clip.
			sun.Strength = 1.1f;

			if (skybox)
			{
				// The sky draws its own sun disc.
				return;
			}

			// Visible sun disc far along the sun direction. Not a shadow caster, not voxelized.
			GameObject sunDisc = SceneModels.Instantiate(scene, SceneModels.Sphere, "SunDisc");
			sunDisc.transform.position = SunDirection * 300f;
			sunDisc.transform.scale = Vector3.One * 12f;
			MeshRenderer sunRenderer = sunDisc.GetComponent<MeshRenderer>();
			sunRenderer.SetMaterials(SceneModels.Material("Sun.material"));
			sunRenderer.CastShadows = false;
			sunRenderer.ReceiveShadows = false;
			sunRenderer.ContributesToGI = false;
			sunRenderer.ReceiveGI = false;
		}

		private static GameObject Box(Scene scene, string name, string material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = center;
			go.transform.scale = halfSize;

			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material(material));
			renderer.IsStatic = isStatic;
			return go;
		}

		#endregion
	}
}
