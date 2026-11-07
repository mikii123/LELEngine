using System;
using LELEngine;
using LELEngine.Volumetrics;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     The volumetric objects' test scene (TestGame volumetrics=1): the particle scenes' ground, wall and sun under the
	///     procedural sky, and
	///     <list type="bullet">
	///         <item>a storm (cylinder, Perlin-Worley, <see cref="VolumetricObject" />) behind the wall, two flashes flickering inside it;</item>
	///         <item>a fog bank (box, Perlin, <see cref="VolumetricObjectHQ" />) over the ground in front of the player (camera=0,1.6,6 is inside it);</item>
	///         <item>a glowing ball (sphere, Worley, emissive, <see cref="VolumetricObject" />) on the left;</item>
	///         <item>a ridged nebula wisp (box, Ridged, <see cref="VolumetricObject" />, a quarter resolution) high on the right.</item>
	///     </list>
	///     volcube=1 puts a cube into the fog bank (the march ends at it, its shadow falls into the fog); volhq=1 makes the
	///     storm a <see cref="VolumetricObjectHQ" /> (to compare the presets from inside it).
	/// </summary>
	public static class VolumetricTestScene
	{
		#region PublicMethods

		public static void Load(Scene scene, GITestScene.Options options, bool cubeInFog, bool stormHQ)
		{
			options.Sky = true;
			receiveGI = options.VolumeGI;
			VfxTestScene.BuildEnvironment(scene, options);
			Lighting.Shadows.Distance = 60f;

			VolumetricObjectBase storm = stormHQ
				? Add<VolumetricObjectHQ>(scene, "Storm", new Vector3(0f, 14f, 42f))
				: Add<VolumetricObject>(scene, "Storm", new Vector3(0f, 14f, 42f));
			storm.Shape = VolumeShape.Cylinder;
			storm.Size = new Vector3(70f, 16f, 70f);
			storm.Noise = VolumeNoise.PerlinWorley;
			storm.NoiseSize = 9f;
			storm.Coverage = 0.62f;
			storm.Softness = 0.15f;
			storm.Density = 30f;
			storm.ShadowReach = 1.5f;
			storm.VoxelSize = 0.45f;
			storm.Albedo = new Color4(0.55f, 0.6f, 0.75f, 1f);
			storm.EmissionColor = new Color4(0.12f, 0.08f, 0.3f, 1f);
			storm.EmissionStrength = 0.05f;
			storm.Lights.Add(new VolumeLight { Position = new Vector3(-12f, 0f, -14f), Radius = 4f, Color = new Color4(0.8f, 0.72f, 1f, 1f) });
			storm.Lights.Add(new VolumeLight { Position = new Vector3(14f, 2f, -6f), Radius = 5f, Color = new Color4(0.8f, 0.72f, 1f, 1f) });
			storm.gameObject.AddComponent<VolumetricFlasher>();

			VolumetricObjectHQ fog = Add<VolumetricObjectHQ>(scene, "Fog Bank", new Vector3(0f, 2f, 6f));
			fog.Shape = VolumeShape.Box;
			fog.Size = new Vector3(18f, 4f, 18f);
			fog.Noise = VolumeNoise.Perlin;
			fog.NoiseSize = 3.5f;
			fog.Coverage = 0.62f;
			fog.Softness = 0.35f;
			fog.ClumpSize = 0f;
			fog.Density = 1.2f;
			fog.ShadowReach = 1f;
			fog.TopRoughness = 0.5f;
			fog.BottomRoughness = 0f;
			fog.CapSoftness = 0.3f;
			fog.VoxelSize = 0.12f;
			fog.DetailStrength = 0.6f;
			fog.DetailSize = 0.5f;
			fog.DetailWind = new Vector3(0.3f, 0f, 0.1f);
			fog.Albedo = new Color4(0.85f, 0.88f, 0.92f, 1f);
			fog.PhaseG = 0.3f;

			VolumetricObject ball = Add<VolumetricObject>(scene, "Glow Ball", new Vector3(-9f, 4f, 14f));
			ball.Shape = VolumeShape.Sphere;
			ball.Size = new Vector3(7f, 7f, 7f);
			ball.Noise = VolumeNoise.Worley;
			ball.NoiseSize = 2f;
			ball.Coverage = 0.55f;
			ball.Softness = 0.3f;
			ball.ClumpSize = 0f;
			ball.Density = 8f;
			ball.SideSoftness = 0.35f;
			ball.VoxelSize = 0.06f;
			ball.Albedo = new Color4(0.9f, 0.6f, 0.4f, 1f);
			ball.EmissionColor = new Color4(1f, 0.45f, 0.1f, 1f);
			ball.EmissionStrength = 0.6f;
			ball.Lights.Add(new VolumeLight { Position = Vector3.Zero, Radius = 1f, Color = new Color4(1f, 0.6f, 0.2f, 1f), Intensity = 2f });

			VolumetricObject wisp = Add<VolumetricObject>(scene, "Nebula Wisp", new Vector3(12f, 9f, 18f));
			wisp.transform.rotation = Quaternion.FromEulerAngles(0.3f, 0.6f, 0.2f);
			wisp.Shape = VolumeShape.Box;
			wisp.Size = new Vector3(14f, 6f, 10f);
			wisp.Noise = VolumeNoise.Ridged;
			wisp.NoiseSize = 4f;
			wisp.Coverage = 0.45f;
			wisp.Softness = 0.25f;
			wisp.ClumpSize = 1.2f;
			wisp.Density = 40f;
			wisp.SideSoftness = 0.4f;
			wisp.CapSoftness = 0.4f;
			wisp.VoxelSize = 0.1f;
			wisp.Albedo = new Color4(0.4f, 0.7f, 1f, 1f);
			wisp.EmissionColor = new Color4(0.2f, 0.4f, 1f, 1f);
			wisp.EmissionStrength = 0.3f;
			wisp.Resolution = VolumetricResolution.Quarter;

			if (cubeInFog)
			{
				GameObject cube = SceneModels.Instantiate(scene, SceneModels.Cube, "Cube In Fog");
				cube.transform.position = new Vector3(1.5f, 1.2f, 7f);
				cube.transform.localScale = new Vector3(0.8f, 1.2f, 0.8f);
			}

			GITestScene.ApplyHarness(scene, options);
		}

		#endregion

		#region PrivateMethods

		private static T Add<T>(Scene scene, string name, Vector3 position) where T : VolumetricObjectBase
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = position;
			T volume = go.AddComponent<T>();
			volume.ReceiveGI = receiveGI;
			return volume;
		}

		// volgi=0: the volumes do not receive the GI (comparisons).
		private static bool receiveGI = true;

		#endregion
	}

	/// <summary>Flickers the lights inside a volumetric object like lightning: short bright strokes at random moments.</summary>
	public sealed class VolumetricFlasher : Behaviour
	{
		private readonly Random random = new Random(3);
		private float[] next;
		private float[] start;

		public override void Update()
		{
			VolumetricObjectBase volume = GetComponent<VolumetricObjectBase>();
			if (volume == null)
			{
				return;
			}

			int count = volume.Lights.Count;
			if (next == null || next.Length != count)
			{
				next = new float[count];
				start = new float[count];
				for (int i = 0; i < count; i++)
				{
					next[i] = 0.5f + i * 0.7f;
					start[i] = -10f;
				}
			}

			float time = (float)Time.timeD;
			for (int i = 0; i < count; i++)
			{
				if (time >= next[i])
				{
					start[i] = time;
					next[i] = time + 1.2f + (float)random.NextDouble() * 2.5f;
				}

				float age = time - start[i];
				float stroke = age < 0f ? 0f : MathF.Exp(-age / 0.08f) + 0.6f * MathF.Exp(-MathF.Max(age - 0.12f, 0f) / 0.06f) * (age > 0.12f ? 1f : 0f);
				volume.Lights[i].Intensity = stroke * 3f;
			}
		}
	}
}
