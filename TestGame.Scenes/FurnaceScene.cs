using System;
using System.Globalization;
using LELEngine;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Furnace test of the GI (furnace=1): a closed box whose walls all have albedo a and emit radiance Le, without sun,
	///     ambient or sky. With every bounce a wall shows Le / (1 - a); a GI that adds n bounces to the emission (the last
	///     one gathered by the pixel itself) shows Le (1 - a^(n+1)) / (1 - a). The expected values are printed at start;
	///     the walls in view are logged as regions of the HDR scene color ("[Regions] frame v0 v1 v2").
	/// </summary>
	public static class FurnaceScene
	{
		#region PublicFields

		/// <summary>Half the interior's width and depth.</summary>
		public const float HalfSize = 4f;

		public const float Height = 6f;

		/// <summary>Default half thickness of the walls (0.5 m walls, as in the test room).</summary>
		public const float HalfThickness = 0.25f;

		public static readonly Vector3 CameraPosition = new Vector3(0f, 3f, -3f);
		public static readonly Vector3 CameraLookAt = new Vector3(0f, 3f, HalfSize);

		/// <summary>The far wall's centre, a point near its top corner and the floor in front of it.</summary>
		public const string RegionPoints = "0,3,4;3.4,5.4,4;0,0,2";

		#endregion

		#region PublicMethods

		/// <param name="halfThickness">Half the walls' thickness (thick walls show how much light cones lose through thin ones).</param>
		public static void Load(Scene scene, GITestScene.Options options, float albedo, float emission, float halfThickness = HalfThickness)
		{
			var material = new Material(InternalStorage.GetOrCreateShader("Engine/Lit.shader")) { name = "Furnace" };
			material.SetColor("Color", new Color4(albedo, albedo, albedo, 1f));
			material.SetColor("Emissive", new Color4(1f, 1f, 1f, emission));

			float s = HalfSize;
			float t = halfThickness;
			float h = Height;
			Wall(scene, "Floor", material, new Vector3(0f, -t, 0f), new Vector3(s + 2f * t, t, s + 2f * t));
			Wall(scene, "Ceiling", material, new Vector3(0f, h + t, 0f), new Vector3(s + 2f * t, t, s + 2f * t));
			Wall(scene, "WallPosX", material, new Vector3(s + t, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s + 2f * t));
			Wall(scene, "WallNegX", material, new Vector3(-s - t, h * 0.5f, 0f), new Vector3(t, h * 0.5f, s + 2f * t));
			Wall(scene, "WallPosZ", material, new Vector3(0f, h * 0.5f, s + t), new Vector3(s + 2f * t, h * 0.5f, t));
			Wall(scene, "WallNegZ", material, new Vector3(0f, h * 0.5f, -s - t), new Vector3(s + 2f * t, h * 0.5f, t));

			// A sun of strength 0: without a directional light the renderer keeps its default one.
			GameObject light = scene.CreateGameObject("Sun");
			light.transform.rotation = QuaternionHelper.LookRotation(-Vector3.UnitY, Vector3.UnitZ);
			DirectionalLight sun = light.AddComponent<DirectionalLight>();
			sun.Strength = 0f;

			GITestScene.BuildPlayer(scene);
			GITestScene.ApplySettings(options);
			Lighting.Environment.AmbientStrength = 0f;
			Lighting.Environment.SpecularStrength = 0f;
			Lighting.Environment.BackgroundColor = new Color4(0f, 0f, 0f, 1f);
			Lighting.Environment.Exposure = 1f;
			Lighting.GI.SkyRadiance = Vector3.Zero;

			options.CameraPosition ??= CameraPosition;
			options.CameraLookAt ??= CameraLookAt;
			GITestScene.ApplyHarness(scene, options);

			PrintExpected(albedo, emission);
		}

		#endregion

		#region PrivateMethods

		private static void Wall(Scene scene, string name, Material material, Vector3 center, Vector3 halfSize)
		{
			GameObject go = SceneModels.Instantiate(scene, SceneModels.Cube, name);
			go.transform.position = center;
			go.transform.scale = halfSize;
			MeshRenderer renderer = go.GetComponent<MeshRenderer>();
			renderer.SetMaterials(material);
			renderer.IsStatic = true;
		}

		private static void PrintExpected(float albedo, float emission)
		{
			var line = new System.Text.StringBuilder();
			line.AppendFormat(CultureInfo.InvariantCulture, "[Furnace] albedo {0:0.###} emission {1:0.###}: all bounces {2:0.0000}", albedo, emission, emission / Math.Max(1e-4f, 1f - albedo));
			// n bounces in the GI volume plus the pixel's own gather: emission * (1 + a + ... + a^(n+1)).
			float sum = emission;
			float term = emission;
			for (int bounces = 0; bounces <= 4; bounces++)
			{
				term *= albedo;
				sum += term;
				line.AppendFormat(CultureInfo.InvariantCulture, " | {0} volume bounces {1:0.0000}", bounces, sum);
			}

			Console.WriteLine(line.ToString());
		}

		#endregion
	}
}
