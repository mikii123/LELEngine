using System.IO;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Content of a new scene and of the GameObject menu's primitives: instances of the project's starter models
	///     (<see cref="Model.Instantiate" />, with the import's materials: the default material for the starter meshes).
	/// </summary>
	internal static class DefaultScene
	{
		#region PublicFields

		public const string CubeModel = "Meshes/Cube.obj";
		public const string SphereModel = "Meshes/Sphere.obj";
		public const string QuadModel = "Meshes/Quad.obj";
		public const string GroundMaterial = "Materials/Ground.material";

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

			GameObject ground = Instantiate(scene, CubeModel, "Ground");
			ground.transform.position = new Vector3(0f, -0.1f, 0f);
			ground.transform.localScale = new Vector3(10f, 0.1f, 10f);
			MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
			if (groundRenderer != null)
			{
				groundRenderer.IsStatic = true;
				// A darker ground than the default material: objects stand out against it.
				if (File.Exists(AssetDatabase.ToAbsolute(GroundMaterial)))
				{
					groundRenderer.SetMaterials(InternalStorage.GetOrCreateMaterial(GroundMaterial));
				}
			}

			GameObject cube = Instantiate(scene, CubeModel, "Cube");
			cube.transform.position = new Vector3(0f, 0.5f, 0f);
			cube.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);

			return scene;
		}

		/// <summary>Writes a new default scene into the project and lists it in the build (needs a GL context).</summary>
		public static void CreateAsset(ProjectInfo project, string assetPath)
		{
			Scene scene = Create(project);
			scene.Name = Path.GetFileNameWithoutExtension(assetPath);
			scene.Path = assetPath;
			string file = AssetDatabase.ToAbsolute(assetPath);
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			Serialization.SceneSerializer.Save(scene, file);
			AssetDatabase.EnsureMeta(assetPath);
			if (!project.BuildScenes.Contains(assetPath))
			{
				project.BuildScenes.Add(assetPath);
			}

			project.Save();
			scene.Unload();
		}

		/// <summary>
		///     An instance of a project model named <paramref name="name" /> under <paramref name="parent" />; an empty
		///     GameObject when the project no longer has the model (starter assets removed).
		/// </summary>
		public static GameObject Instantiate(Scene scene, string modelPath, string name, Transform parent = null)
		{
			GameObject go = null;
			if (File.Exists(AssetDatabase.ToAbsolute(modelPath)))
			{
				go = ModelAssets.Load(modelPath).Instantiate(scene, parent);
			}
			else
			{
				Debug.LogWarning($"[Editor] '{modelPath}' is not in the project (starter assets were removed?)");
			}

			if (go == null)
			{
				go = scene.CreateGameObject(name);
				if (parent != null)
				{
					go.transform.SetParent(parent, false);
				}
			}

			go.Name = name;
			return go;
		}

		#endregion
	}
}
