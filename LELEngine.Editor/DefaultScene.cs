using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>Content of a new scene and of the GameObject menu's primitives (uses the project's starter assets).</summary>
	internal static class DefaultScene
	{
		#region PublicFields

		public const string DefaultMaterial = "Default.material";
		public const string GroundMaterial = "Ground.material";

		#endregion

		#region PublicMethods

		public static Scene Create(ProjectInfo project)
		{
			var scene = new Scene("Untitled");

			// Look: soft sky, a little ambient for the leaks, GI over a 32 m volume around the origin. Colors are
			// linear (the output is gamma encoded): an open scene sees the whole sky, so it is darker than the GI
			// test room's.
			EnvironmentSettings environment = scene.Settings.Environment;
			environment.AmbientColor = new Color4(0.55f, 0.6f, 0.75f, 1f);
			environment.AmbientStrength = 0.06f;
			environment.SpecularStrength = 0.25f;
			environment.SpecularShine = 48f;
			environment.BackgroundColor = new Color4(0.16f, 0.3f, 0.6f, 1f);
			scene.Settings.GI.SkyRadiance = new Vector3(0.12f, 0.2f, 0.38f);
			scene.Settings.GI.GridSize = 32f;
			scene.Settings.GI.Center = new Vector3(0f, 6f, 0f);
			scene.Settings.Shadows.Distance = 30f;
			scene.Settings.Shadows.DepthRange = 60f;

			GameObject camera = scene.CreateGameObject("Main Camera");
			camera.transform.position = new Vector3(3.5f, 2.2f, -6f);
			camera.transform.rotation = QuaternionHelper.LookRotation((new Vector3(0f, 0.4f, 0f) - camera.transform.position).Normalized(), Vector3.UnitY);
			Camera cameraComponent = camera.AddComponent<Camera>();
			cameraComponent.FoV = 60f;
			cameraComponent.NearClip = 0.05f;
			cameraComponent.FarClip = 500f;

			GameObject light = scene.CreateGameObject("Directional Light");
			// From behind the camera's left: one visible face lit, one in shade, the shadow in view.
			light.transform.rotation = QuaternionHelper.LookRotation(new Vector3(0.45f, -0.7f, 0.55f).Normalized(), Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			sun.Strength = 1.1f;

			GameObject ground = scene.CreateGameObject("Ground");
			ground.transform.position = new Vector3(0f, -0.1f, 0f);
			ground.transform.localScale = new Vector3(10f, 0.1f, 10f);
			// A darker ground than the default material: objects stand out against it.
			MeshRenderer groundRenderer = AddMesh(ground, "Cube.obj");
			groundRenderer.IsStatic = true;
			string groundMaterial = AssetDatabase.Resolve(GroundMaterial, "Materials");
			if (groundMaterial != null)
			{
				groundRenderer.SetMaterial(groundMaterial);
			}

			GameObject cube = scene.CreateGameObject("Cube");
			cube.transform.position = new Vector3(0f, 0.5f, 0f);
			cube.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
			AddMesh(cube, "Cube.obj");

			return scene;
		}

		/// <summary>Writes a new default scene into the project and lists it in the build (needs a GL context).</summary>
		public static void CreateAsset(ProjectInfo project, string assetPath)
		{
			Scene scene = Create(project);
			scene.Name = System.IO.Path.GetFileNameWithoutExtension(assetPath);
			scene.Path = assetPath;
			string file = AssetDatabase.ToAbsolute(assetPath);
			System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file));
			Serialization.SceneSerializer.Save(scene, file);
			AssetDatabase.EnsureMeta(assetPath);
			if (!project.BuildScenes.Contains(assetPath))
			{
				project.BuildScenes.Add(assetPath);
			}

			project.Save();
			scene.Unload();
		}

		/// <summary>Adds a MeshRenderer with the given project mesh (file name) and the default material.</summary>
		public static MeshRenderer AddMesh(GameObject go, string meshFile)
		{
			MeshRenderer renderer = go.AddComponent<MeshRenderer>();
			string mesh = AssetDatabase.Resolve(meshFile, "Meshes");
			if (mesh != null)
			{
				renderer.SetMesh(mesh);
			}
			else
			{
				Debug.LogWarning($"[Editor] '{meshFile}' is not in the project (starter assets were removed?)");
			}

			string material = AssetDatabase.Resolve(DefaultMaterial, "Materials");
			if (material != null)
			{
				renderer.SetMaterial(material);
			}

			return renderer;
		}

		#endregion
	}
}
