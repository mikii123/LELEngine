using System;
using System.Collections.Generic;
using System.IO;
using LELEngine;
using LELEngine.Rendering.Impostors;
using LELEngine.Shaders;
using LELEngine.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Impostors against the meshes they stand for (impostors=1): an open yard under the sun, and pairs of the same
	///     object, the mesh on the left and its impostor (baked at start) on the right, floating over the ground so both
	///     shadows show: a ship (specular workflow, specular and emission maps), an axe (scene workflow, normal map), a
	///     car (plain colour), and a group of several renderers with a glowing part. impostorfield=N adds N x N ships
	///     with LOD groups (the mesh near, the impostor far) behind them.
	/// </summary>
	public static class ImpostorTestScene
	{
		#region PublicFields

		public sealed class Options
		{
			/// <summary>impostorframes=N: views along a side of the grid.</summary>
			public int Frames = 12;

			/// <summary>impostorsize=N: texels of a view.</summary>
			public int FrameSize = 128;

			/// <summary>impostorhemi=1: views from above the horizon only.</summary>
			public bool Hemisphere;

			/// <summary>impostorsamples=N: capture samples along a texel's side.</summary>
			public int Supersampling = 4;

			/// <summary>impostoryaw=degrees, impostorpitch=degrees: every pair turned so (the same for the mesh and its impostor).</summary>
			public float Yaw;

			public float Pitch;

			/// <summary>impostorspin=degrees a second about Y.</summary>
			public float Spin;

			/// <summary>impostorspinframe=degrees a frame about Y: the same turn at the same frame in every run.</summary>
			public float SpinPerFrame;

			/// <summary>impostorscale=x,y,z: every pair scaled so (a stretched object keeps its impostor in place).</summary>
			public Vector3 Scale = Vector3.One;

			/// <summary>impostorshow=both|mesh|impostor: which of each pair is in the scene.</summary>
			public string Show = "both";

			/// <summary>
			///     impostorswap=frame: the meshes alone until that frame, then the impostors alone in their places (frames dumped
			///     before and after it compare a mesh with its impostor pixel by pixel in one run).
			/// </summary>
			public int SwapFrame;

			/// <summary>impostorfield=N: N x N ships with LOD groups behind the pairs.</summary>
			public int Field;

			/// <summary>impostorlod=height: the share of the view's height below which the field's ships are impostors.</summary>
			public float FieldLodHeight = 0.12f;

			/// <summary>impostordump=folder: the baked atlases as BMP files (colour and alpha apart).</summary>
			public string DumpDirectory;

			/// <summary>impostorparallax=0..1, impostorcutoff=0..1, impostorbias=radii: the materials' values.</summary>
			public float Parallax = 1f;

			public float Cutoff = 0.5f;
			public float ShadowBias = -1f;

			/// <summary>impostorsteps=N: parallax steps (0: the bake's).</summary>
			public int ParallaxSteps;

			/// <summary>impostoragree=x: the materials' View Agreement (negative: the shader's default).</summary>
			public float Agreement = -1f;

			/// <summary>impostorheight=y: how high the pairs float (low enough and they cut into the ground: the depth an impostor writes).</summary>
			public float Height = 3f;

			/// <summary>impostorprepass=0: no geometry prepass (the impostors write their depth while they shade).</summary>
			public bool Prepass = true;

			/// <summary>impostorlights=1: a point light and a spot light among the pairs.</summary>
			public bool Lights;

			/// <summary>impostorrepeat=N: every prop is baked N times more and the bakes' atlases are compared (a bake repeats exactly).</summary>
			public int Repeat;

			/// <summary>
			///     impostormotion=frame: at that frame the geometry prepass's motion vectors are read back and summed up per
			///     quarter of the view's width (the four props with the start view, alone in their places: impostorswap or
			///     impostorshow), so a run of the meshes and one of the impostors compare (<see cref="ImpostorMotionProbe" />).
			/// </summary>
			public int MotionFrame;
		}

		/// <summary>Towards the sun: behind the start view, to its left and high, so the sides the camera sees are lit and the shadows fall behind the objects.</summary>
		public static readonly Vector3 SunDirection = new Vector3(-0.45f, 0.75f, -0.5f).Normalized();

		#endregion

		#region PublicMethods

		public static void Load(Scene scene, GITestScene.Options options, Options impostors)
		{
			BuildGround(scene);
			BuildLight(scene);
			GITestScene.BuildPlayer(scene);
			GITestScene.ApplySettings(options);

			Lighting.GI.GridSize = 60f;
			Lighting.GI.Center = new Vector3(0f, 8f, 10f);
			Lighting.Shadows.Distance = 80f;
			Lighting.Shadows.DepthRange = 160f;
			if (options.Sky)
			{
				scene.Settings.Environment.Skybox = GITestScene.CreateSkybox(options, SunDirection);
			}

			var settings = new ImpostorSettings { Frames = impostors.Frames, FrameSize = impostors.FrameSize, Hemisphere = impostors.Hemisphere, Supersampling = impostors.Supersampling };
			Quaternion turn = QuaternionHelper.Euler(impostors.Pitch, impostors.Yaw, 0f);
			bool showMesh = !string.Equals(impostors.Show, "impostor", StringComparison.OrdinalIgnoreCase);
			bool showImpostor = !string.Equals(impostors.Show, "mesh", StringComparison.OrdinalIgnoreCase) && impostors.SwapFrame <= 0;
			ImpostorSwap swap = impostors.SwapFrame > 0 ? scene.CreateGameObject("ImpostorSwap").AddComponent<ImpostorSwap>() : null;
			if (swap != null)
			{
				showMesh = true;
				swap.Frame = impostors.SwapFrame;
			}

			// Side by side when both show; alone, each stands at the pair's centre, so a frame of the meshes and one of the
			// impostors can be compared pixel by pixel.
			float apart = showMesh && showImpostor ? 2.6f : 0f;
			var props = new List<Func<Scene, GameObject>> { BuildShip, BuildAxe, BuildCar, BuildGroup };
			Impostor shipImpostor = null;
			for (int i = 0; i < props.Count; i++)
			{
				float centre = (i - (props.Count - 1) * 0.5f) * 11f;
				GameObject mesh = props[i](scene);
				mesh.transform.position = new Vector3(centre - apart, impostors.Height, 14f);
				mesh.transform.rotation = turn;
				mesh.transform.scale = mesh.transform.scale * impostors.Scale;

				Impostor impostor = Impostor.Bake(mesh, settings);
				ApplyMaterialOptions(impostor, impostors);
				Report(impostor, mesh.Name);
				if (impostors.Repeat > 1)
				{
					Repeat(mesh, settings, impostors.Repeat);
				}
				if (i == 0)
				{
					shipImpostor = impostor;
				}

				if (!string.IsNullOrEmpty(impostors.DumpDirectory))
				{
					Dump(impostor, impostors.DumpDirectory, mesh.Name);
				}

				if (swap != null)
				{
					// The mesh and its impostor as the two levels of one LOD group, the first forced until the swap: as
					// in a game, the GI keeps seeing the mesh whichever level draws.
					LODGroup group = mesh.AddComponent<LODGroup>();
					group.SetLODs(Array.Empty<LOD>());
					impostor.AddToLODGroup(group, 0f, 0.5f);
					group.ForceLOD(0);
					swap.Groups.Add(group);
					Spin(mesh, impostors);
					continue;
				}

				// The impostor on an object with the mesh's transform, beside it.
				GameObject holder = scene.CreateGameObject(mesh.Name + " Impostor");
				holder.transform.position = new Vector3(centre + apart, impostors.Height, 14f);
				holder.transform.rotation = turn;
				holder.transform.scale = mesh.transform.scale;
				impostor.Instantiate(scene, holder.transform);

				Spin(mesh, impostors);
				Spin(holder, impostors);
				mesh.SetActive(showMesh);
				holder.SetActive(showImpostor);
			}

			if (impostors.Field > 0)
			{
				BuildField(scene, shipImpostor, impostors);
			}

			if (impostors.Lights)
			{
				BuildLights(scene, impostors.Height);
			}

			if (impostors.MotionFrame > 0)
			{
				scene.CreateGameObject("ImpostorMotionProbe").AddComponent<ImpostorMotionProbe>().Frame = impostors.MotionFrame;
			}

			GITestScene.ApplyHarness(scene, options);
			Game.Mono.Renderer.Settings.DepthPrepass = impostors.Prepass;
		}

		#endregion

		#region PrivateMethods

		private static void BuildGround(Scene scene)
		{
			GameObject ground = SceneModels.Instantiate(scene, SceneModels.Cube, "Ground");
			ground.transform.position = new Vector3(0f, -0.25f, 60f);
			ground.transform.scale = new Vector3(120f, 0.25f, 120f);
			MeshRenderer renderer = ground.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material("LitGray.material"));
			ground.isStatic = true;
		}

		private static void BuildLight(Scene scene)
		{
			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(-SunDirection, Vector3.UnitY);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Color = new Color4(1f, 0.96f, 0.9f, 1f);
			sun.Strength = 1.1f;
		}

		// A magenta point light between the first two pairs and a green spot from above on the last two: the local
		// lights reach an impostor's surface as they reach the mesh's.
		private static void BuildLights(Scene scene, float height)
		{
			GameObject point = scene.CreateGameObject("PointLight");
			point.transform.position = new Vector3(-11f, height + 0.5f, 10.5f);
			Light pointLight = point.AddComponent<Light>();
			pointLight.Type = LightType.Point;
			pointLight.Color = new Color4(1f, 0.2f, 0.9f, 1f);
			pointLight.Intensity = 30f;
			pointLight.Range = 14f;

			GameObject spot = scene.CreateGameObject("SpotLight");
			spot.transform.position = new Vector3(11f, height + 7f, 11f);
			spot.transform.rotation = QuaternionHelper.LookRotation(new Vector3(0f, -7f, 3f), Vector3.UnitZ);
			Light spotLight = spot.AddComponent<Light>();
			spotLight.Type = LightType.Spot;
			spotLight.SpotAngle = 70f;
			spotLight.Color = new Color4(0.3f, 1f, 0.4f, 1f);
			spotLight.Intensity = 60f;
			spotLight.Range = 20f;
		}

		// Bakes the object again and again and says how the atlases differ from the first bake's (impostorrepeat=N): a
		// bake of the same object should give the same texels every time.
		private static void Repeat(GameObject mesh, ImpostorSettings settings, int count)
		{
			ImpostorBake first = ImpostorBaker.Bake(mesh, settings);
			int differing = 0;
			for (int i = 1; i < count; i++)
			{
				ImpostorBake again = ImpostorBaker.Bake(mesh, settings);
				string report = Differences("shape", first.Shape, again.Shape, first.AtlasSize) + Differences("base", first.Base, again.Base, first.AtlasSize)
				                + Differences("specular", first.Specular, again.Specular, first.AtlasSize) + Differences("emission", first.Emission, again.Emission, first.AtlasSize);
				if (report.Length > 0)
				{
					differing++;
					Console.WriteLine($"[Impostor] {mesh.Name} bake {i + 1} differs from the first:{report}");
				}
			}

			Console.WriteLine($"[Impostor] {mesh.Name}: {differing} of {count - 1} repeated bakes differ from the first");
		}

		private static string Differences(string name, byte[] a, byte[] b, int size)
		{
			if (a == null || b == null)
			{
				return a == b ? "" : " " + name + " there in one bake only;";
			}

			int texels = 0;
			string where = "";
			for (int i = 0; i < a.Length; i += 4)
			{
				if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3])
				{
					continue;
				}

				if (texels++ < 3)
				{
					where += $" ({i / 4 % size},{i / 4 / size}: {a[i]} {a[i + 1]} {a[i + 2]} {a[i + 3]} -> {b[i]} {b[i + 1]} {b[i + 2]} {b[i + 3]})";
				}
			}

			return texels == 0 ? "" : $" {name} {texels} texels{where};";
		}

		// The object turns about Y when the options say so (a second or a frame at a time).
		private static void Spin(GameObject target, Options impostors)
		{
			if (impostors.Spin == 0f && impostors.SpinPerFrame == 0f)
			{
				return;
			}

			ImpostorSpinner spinner = target.AddComponent<ImpostorSpinner>();
			spinner.DegreesPerSecond = impostors.Spin;
			spinner.DegreesPerFrame = impostors.SpinPerFrame;
		}

		// A model as one object, scaled so its longest side is `size` and centred on its own origin.
		private static GameObject Prop(Scene scene, string name, string modelPath, float size, Material material)
		{
			Model model = ModelAssets.Load(modelPath);
			GameObject root = scene.CreateGameObject(name);
			GameObject body = model.Instantiate(scene, root.transform);
			model.GetBounds(out Vector3 min, out Vector3 max);
			Vector3 extent = max - min;
			float scale = size / Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
			body.transform.localScale = new Vector3(scale);
			body.transform.localPosition = -(min + max) * 0.5f * scale;
			foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>())
			{
				renderer.SetMaterials(material);
			}

			return root;
		}

		private static GameObject BuildShip(Scene scene)
		{
			// The specular workflow with a specular map, a procedural normal map and the ship's lights as emission.
			var material = new Material(InternalStorage.GetOrCreateShader("Engine/LitTextured.shader")) { name = "Ship" };
			material.SetFloat("Workflow", 2f);
			material.SetTexture("DiffuseMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-diffuse.jpg"));
			material.SetColor("SpecularColor", Color4.White);
			material.SetTexture("SpecularMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-spec.jpg"));
			material.SetTexture("NormalMap", BumpNormalMap(256, 24, 0.25f));
			material.SetFloat("Roughness", 0.35f);
			material.SetColor("Emissive", new Color4(1f, 1f, 1f, 6f));
			material.SetTexture("EmissionMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-lights.jpg"));
			return Prop(scene, "Ship", "Meshes/ship.obj", 4.6f, material);
		}

		private static GameObject BuildAxe(Scene scene)
		{
			// The scene's own highlight, with a normal map.
			var material = new Material(InternalStorage.GetOrCreateShader("Engine/LitTextured.shader")) { name = "Axe" };
			material.SetTexture("DiffuseMap", AssetDatabase.Load<Texture2D>("Textures/Axe_diff.png"));
			material.SetTexture("NormalMap", AssetDatabase.Load<Texture2D>("Textures/Axe_normal.png"));
			material.SetFloat("Roughness", 0.5f);
			return Prop(scene, "Axe", "Meshes/Axe.obj", 4.6f, material);
		}

		private static GameObject BuildCar(Scene scene)
		{
			var material = new Material(InternalStorage.GetOrCreateShader("Engine/Lit.shader")) { name = "Car" };
			material.SetColor("Color", new Color4(0.5f, 0.5f, 1f, 1f));
			material.SetFloat("Roughness", 0.4f);
			return Prop(scene, "Car", "Meshes/car.obj", 4.6f, material);
		}

		private static GameObject BuildGroup(Scene scene)
		{
			// Several renderers under one object, each with its own transform; one glows, one is metal.
			GameObject root = scene.CreateGameObject("Group");
			Part(scene, root, "Block", SceneModels.Cube, new Vector3(0f, -0.6f, 0f), new Vector3(1.6f, 0.5f, 1.1f), QuaternionHelper.Euler(0f, 20f, 0f), SceneModels.Material("LitRed.material"));
			Part(scene, root, "Pillar", SceneModels.Cube, new Vector3(-0.9f, 0.6f, 0.3f), new Vector3(0.25f, 1.3f, 0.25f), QuaternionHelper.Euler(0f, 0f, 12f), SceneModels.Material("LitWhite.material"));

			var glow = new Material(InternalStorage.GetOrCreateShader("Engine/Lit.shader")) { name = "Glow" };
			glow.SetColor("Color", new Color4(1f, 0.8f, 0.2f, 1f));
			glow.SetColor("Emissive", new Color4(1f, 0.7f, 0.15f, 3f));
			Part(scene, root, "Lamp", SceneModels.Sphere, new Vector3(0.7f, 0.55f, -0.2f), new Vector3(0.65f), Quaternion.Identity, glow);

			var gold = new Material(InternalStorage.GetOrCreateShader("Engine/LitTextured.shader")) { name = "Gold" };
			gold.SetFloat("Workflow", 1f);
			gold.SetColor("Color", new Color4(1f, 0.77f, 0.34f, 1f));
			gold.SetFloat("Metallic", 1f);
			gold.SetFloat("Roughness", 0.25f);
			Part(scene, root, "Ball", SceneModels.Sphere, new Vector3(-0.2f, 0.9f, -0.7f), new Vector3(0.5f), Quaternion.Identity, gold);
			return root;
		}

		private static void Part(Scene scene, GameObject root, string name, string model, Vector3 position, Vector3 scale, Quaternion rotation, Material material)
		{
			GameObject part = SceneModels.Instantiate(scene, model, name);
			part.transform.SetParent(root.transform, false);
			part.transform.localPosition = position;
			part.transform.localRotation = rotation;
			part.transform.localScale = scale;
			part.GetComponent<MeshRenderer>().SetMaterials(material);
		}

		// N x N ships with LOD groups behind the pairs: the mesh while a ship is tall on screen, its impostor beyond. Every
		// ship turned its own way, so the impostors are seen from every side.
		private static void BuildField(Scene scene, Impostor impostor, Options options)
		{
			var random = new Random(7);
			Model model = ModelAssets.Load("Meshes/ship.obj");
			model.GetBounds(out Vector3 min, out Vector3 max);
			Vector3 extent = max - min;
			float scale = 4.6f / Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
			Material material = null;
			float spacing = 9f;
			for (int z = 0; z < options.Field; z++)
			{
				for (int x = 0; x < options.Field; x++)
				{
					GameObject root = scene.CreateGameObject("FieldShip");
					root.transform.position = new Vector3((x - (options.Field - 1) * 0.5f) * spacing, 2.5f + (float)random.NextDouble() * 6f, 30f + z * spacing);
					root.transform.rotation = QuaternionHelper.Euler((float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 360f);
					GameObject body = model.Instantiate(scene, root.transform);
					body.transform.localScale = new Vector3(scale);
					body.transform.localPosition = -(min + max) * 0.5f * scale;
					var renderers = root.GetComponentsInChildren<MeshRenderer>();
					foreach (MeshRenderer renderer in renderers)
					{
						material ??= FieldMaterial();
						renderer.SetMaterials(material);
					}

					LODGroup group = root.AddComponent<LODGroup>();
					group.SetLODs(new[] { new LOD(options.FieldLodHeight, renderers.ToArray()) });
					group.RecalculateBounds();
					impostor.AddToLODGroup(group, 0.002f);
				}
			}

			Console.WriteLine($"[Impostor] Field of {options.Field * options.Field} ships with LOD groups (mesh above {options.FieldLodHeight:0.###} of the view's height)");
		}

		private static Material fieldMaterial;

		// The field's ships share the first ship's material (the impostor was baked from it).
		private static Material FieldMaterial()
		{
			if (fieldMaterial != null)
			{
				return fieldMaterial;
			}

			fieldMaterial = new Material(InternalStorage.GetOrCreateShader("Engine/LitTextured.shader")) { name = "Ship" };
			fieldMaterial.SetFloat("Workflow", 2f);
			fieldMaterial.SetTexture("DiffuseMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-diffuse.jpg"));
			fieldMaterial.SetColor("SpecularColor", Color4.White);
			fieldMaterial.SetTexture("SpecularMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-spec.jpg"));
			fieldMaterial.SetTexture("NormalMap", BumpNormalMap(256, 24, 0.25f));
			fieldMaterial.SetFloat("Roughness", 0.35f);
			fieldMaterial.SetColor("Emissive", new Color4(1f, 1f, 1f, 6f));
			fieldMaterial.SetTexture("EmissionMap", AssetDatabase.Load<Texture2D>("Textures/SciFi_Fighter-MK6-lights.jpg"));
			return fieldMaterial;
		}

		private static void ApplyMaterialOptions(Impostor impostor, Options options)
		{
			impostor.Material.SetFloat("Parallax", options.Parallax);
			impostor.Material.SetFloat("Cutoff", options.Cutoff);
			if (options.ParallaxSteps > 0)
			{
				impostor.Material.SetFloat("ParallaxSteps", options.ParallaxSteps);
			}

			if (options.Agreement >= 0f)
			{
				impostor.Material.SetFloat("Agreement", options.Agreement);
			}

			if (options.ShadowBias >= 0f)
			{
				impostor.Material.SetFloat("ShadowBias", options.ShadowBias);
			}
		}

		private static void Report(Impostor impostor, string name)
		{
			int atlas = impostor.Frames * impostor.FrameSize;
			int atlases = 2 + (impostor.SpecularMap != null ? 1 : 0) + (impostor.EmissionMap != null ? 1 : 0);
			Console.WriteLine($"[Impostor] {name}: {impostor.SourceTriangles} triangles -> {impostor.Frames} x {impostor.Frames} views of {impostor.FrameSize}, {atlases} atlases of {atlas} x {atlas}"
				+ $" (specular {(impostor.SpecularMap != null ? "yes" : "no")}{(impostor.Material.GetFloat("SceneHighlights") > 0f ? " with scene highlights" : "")}, emission {(impostor.EmissionMap != null ? "yes" : "no")}), radius {impostor.Radius:0.###}");
		}

		private static Texture2D bumps;

		// A tangent-space normal map of round bumps in a grid.
		private static Texture2D BumpNormalMap(int size, int cells, float tilt)
		{
			if (bumps != null)
			{
				return bumps;
			}

			var pixels = new byte[size * size * 4];
			for (int y = 0; y < size; y++)
			{
				for (int x = 0; x < size; x++)
				{
					float u = (x + 0.5f) / size * cells;
					float v = (y + 0.5f) / size * cells;
					float dx = (u - MathF.Floor(u)) * 2f - 1f;
					float dy = (v - MathF.Floor(v)) * 2f - 1f;
					float r = MathF.Sqrt(dx * dx + dy * dy);
					float slope = r < 0.8f ? MathF.Sin(r / 0.8f * MathF.PI) * tilt : 0f;
					float nx = r > 1e-4f ? dx / r * slope : 0f;
					float ny = r > 1e-4f ? dy / r * slope : 0f;
					int index = (y * size + x) * 4;
					pixels[index] = (byte)Math.Clamp((nx * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
					pixels[index + 1] = (byte)Math.Clamp((ny * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
					pixels[index + 2] = (byte)Math.Clamp((MathF.Sqrt(Math.Max(0f, 1f - nx * nx - ny * ny)) * 0.5f + 0.5f) * 255f + 0.5f, 0f, 255f);
					pixels[index + 3] = 255;
				}
			}

			bumps = new Texture2D(size, size, pixels, "bumps", true, false);
			return bumps;
		}

		// ---------------------------------------------------------------- atlas dumps

		// The impostor's atlases as 24-bit BMP files: colour and alpha apart ("<name>_shape_rgb.bmp", "_shape_a.bmp", ...).
		private static void Dump(Impostor impostor, string folder, string name)
		{
			Directory.CreateDirectory(folder);
			DumpTexture(impostor.ShapeMap, Path.Combine(folder, name + "_shape"));
			DumpTexture(impostor.BaseMap, Path.Combine(folder, name + "_base"));
			if (impostor.SpecularMap != null)
			{
				DumpTexture(impostor.SpecularMap, Path.Combine(folder, name + "_specular"));
			}

			if (impostor.EmissionMap != null)
			{
				DumpTexture(impostor.EmissionMap, Path.Combine(folder, name + "_emission"));
			}
		}

		private static void DumpTexture(Texture2D texture, string path)
		{
			byte[] texels = texture.GetRawPixels();
			int size = texture.width;
			var colour = new byte[size * size * 3];
			var alpha = new byte[size * size * 3];
			for (int i = 0, o = 0; i < size * size * 4; i += 4, o += 3)
			{
				// BMP stores blue first; its rows run bottom up, as the atlas's.
				colour[o] = texels[i + 2];
				colour[o + 1] = texels[i + 1];
				colour[o + 2] = texels[i];
				alpha[o] = alpha[o + 1] = alpha[o + 2] = texels[i + 3];
			}

			WriteBmp(path + "_rgb.bmp", size, colour);
			WriteBmp(path + "_a.bmp", size, alpha);
		}

		private static void WriteBmp(string file, int size, byte[] bgr)
		{
			int stride = (size * 3 + 3) & ~3;
			using (var writer = new BinaryWriter(File.Create(file)))
			{
				writer.Write((byte)'B');
				writer.Write((byte)'M');
				writer.Write(54 + stride * size);
				writer.Write(0);
				writer.Write(54);
				writer.Write(40);
				writer.Write(size);
				writer.Write(size);
				writer.Write((short)1);
				writer.Write((short)24);
				writer.Write(0);
				writer.Write(stride * size);
				writer.Write(2835);
				writer.Write(2835);
				writer.Write(0);
				writer.Write(0);
				var padding = new byte[stride - size * 3];
				for (int y = 0; y < size; y++)
				{
					writer.Write(bgr, y * size * 3, size * 3);
					writer.Write(padding);
				}
			}
		}

		#endregion
	}

	/// <summary>Swaps the meshes for their impostors at a frame (impostorswap=frame): the LOD groups' forced level.</summary>
	public sealed class ImpostorSwap : Behaviour
	{
		public int Frame;
		public readonly List<LODGroup> Groups = new List<LODGroup>();
		private int frame;

		public override void Update()
		{
			if (++frame != Frame)
			{
				return;
			}

			foreach (LODGroup group in Groups)
			{
				group.ForceLOD(1);
			}

			Console.WriteLine("[Impostor] Swapped the meshes for their impostors at frame " + frame);
		}
	}

	/// <summary>
	///     Reads the geometry prepass's motion vectors back at a frame (impostormotion=frame) and prints, per quarter of
	///     the view's width, how many pixels moved and their mean motion (world units a frame, in thousandths): what the
	///     prepass wrote for whatever stands there. With the start view the four props are one to a quarter, so a run of
	///     the turning meshes and one of the turning impostors give lines to compare.
	/// </summary>
	public sealed class ImpostorMotionProbe : Behaviour
	{
		public int Frame;
		private int frame;

		public override void Update()
		{
			var prepass = Game.Mono.Renderer.GetPass<LELEngine.Rendering.Passes.GeometryPrepass>();
			if (prepass != null)
			{
				// Read after the frame: its memory is not given to the frame's later textures.
				prepass.KeepMotion = true;
			}

			if (++frame != Frame)
			{
				return;
			}

			LELEngine.Rendering.RenderTexture motion = prepass?.Motion;
			if (motion == null)
			{
				Console.WriteLine("[Impostor] motion: the geometry prepass has no motion target");
				return;
			}

			int width = Game.Mono.Renderer.RenderWidth;
			int height = Game.Mono.Renderer.RenderHeight;
			float[] texels = LELEngine.Graphics.TexelConversion.ReadFloats(motion.Gpu, 0, 0, 0, 0, width, height);
			for (int quarter = 0; quarter < 4; quarter++)
			{
				int moved = 0;
				Vector3 sum = Vector3.Zero;
				double length = 0.0;
				for (int y = 0; y < height; y++)
				{
					for (int x = quarter * width / 4; x < (quarter + 1) * width / 4; x++)
					{
						int i = (y * width + x) * 4;
						if (texels[i + 3] < 0.5f)
						{
							continue;
						}

						var vector = new Vector3(texels[i], texels[i + 1], texels[i + 2]);
						moved++;
						sum += vector;
						length += vector.Length;
					}
				}

				Vector3 mean = moved > 0 ? sum / moved * 1000f : Vector3.Zero;
				Console.WriteLine(FormattableString.Invariant(
					$"[Impostor] motion quarter {quarter}: {moved} moved pixels, mean ({mean.X:0.00}, {mean.Y:0.00}, {mean.Z:0.00}) / 1000, mean length {(moved > 0 ? length / moved * 1000.0 : 0.0):0.00} / 1000"));
			}
		}
	}

	/// <summary>
	///     Turns its object about the world's Y axis: impostorspin=degrees a second, or impostorspinframe=degrees a frame
	///     (the same turn at the same frame in every run: two runs compare frame for frame).
	/// </summary>
	public sealed class ImpostorSpinner : Behaviour
	{
		public float DegreesPerSecond = 20f;
		public float DegreesPerFrame;

		public override void Update()
		{
			float turn = DegreesPerFrame != 0f ? DegreesPerFrame : DegreesPerSecond * Time.deltaTime;
			transform.rotation = QuaternionHelper.Euler(0f, turn, 0f) * transform.rotation;
		}
	}
}
