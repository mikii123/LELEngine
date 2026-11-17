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
			// night=1: no sun, no sky: only the clouds' own light is left (emission, their lights, the lamps), so what they
			// send into the GI shows alone.
			options.Sky = !options.Night;
			receiveGI = options.VolumeGI;
			contributeGI = options.VolumeContributeGI;
			temporal = options.VolumeTemporal;
			VfxTestScene.BuildEnvironment(scene, options);
			if (options.Night)
			{
				DirectionalLight sun = scene.FindObjectOfType<DirectionalLight>();
				if (sun != null)
				{
					sun.Strength = 0f;
				}

				Lighting.Environment.AmbientStrength = 0f;
				Lighting.GI.SkyRadiance = new Vector3(0.004f, 0.005f, 0.008f);
				Lighting.Environment.BackgroundColor = new Color4(0.004f, 0.005f, 0.008f, 1f);
			}

			if (options.LinearExposure > 0f)
			{
				Lighting.Environment.Tonemap = false;
				Lighting.Environment.Exposure = options.LinearExposure;
			}

			Lighting.Shadows.Distance = 60f;
			// volmedia=0: the project's switch (the GI sees no clouds); volmediares / volmediareach: the media volume.
			Lighting.GI.VolumesInGI = (VolumeGIMode)Math.Clamp(options.VolumeMedia, 0, 2);
			Lighting.GI.VolumeMediaResolution = options.VolumeMediaResolution;
			Lighting.GI.VolumeMediaReach = options.VolumeMediaReach;
			Lighting.GI.VolumeMediaSides = options.VolumeMediaSides;

			if (options.VolumeTest > 0)
			{
				BuildLightTest(scene, options);
				GITestScene.ApplyHarness(scene, options);
				return;
			}

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
			// volbig=1: as many voxels as a large game's storm (11 M, transport 1.4 M), for costs that scale with the grid.
			storm.VoxelSize = options.VolumeBig ? 0.19f : 0.45f;
			storm.Albedo = new Color4(0.55f, 0.6f, 0.75f, 1f);
			storm.EmissionColor = new Color4(0.12f, 0.08f, 0.3f, 1f);
			storm.EmissionStrength = 0.05f;
			storm.Lights.Add(new VolumeLight { Position = new Vector3(-12f, 0f, -14f), Radius = 4f, Color = new Color4(0.8f, 0.72f, 1f, 1f) });
			storm.Lights.Add(new VolumeLight { Position = new Vector3(14f, 2f, -6f), Radius = 5f, Color = new Color4(0.8f, 0.72f, 1f, 1f) });
			// volflash=0: the storm's own lights stay dark; 2: lit all the time (steady costs).
			if (options.VolumeFlash == 1)
			{
				storm.gameObject.AddComponent<VolumetricFlasher>();
			}
			else if (options.VolumeFlash == 2)
			{
				foreach (VolumeLight own in storm.Lights)
				{
					own.Intensity = 3f;
				}
			}

			// vollamps=N: steady lamps (scene Lights) on rings inside the storm, each a colour of its own.
			for (int i = 0; i < options.VolumeLamps; i++)
			{
				float ring = 8f + 10f * (i % 3);
				float angle = i * 2.399963f;
				GameObject lamp = scene.CreateGameObject("Storm Lamp " + (i + 1));
				lamp.transform.position = new Vector3(MathF.Cos(angle) * ring, 14f + 2f * MathF.Sin(i * 1.7f), 42f + MathF.Sin(angle) * ring);
				Light light = lamp.AddComponent<Light>();
				float hue = i * 0.61803f % 1f;
				light.Color = new Color4(0.55f + 0.45f * MathF.Cos(6.2832f * hue), 0.55f + 0.45f * MathF.Cos(6.2832f * (hue + 0.33f)), 0.55f + 0.45f * MathF.Cos(6.2832f * (hue + 0.67f)), 1f);
				light.Intensity = 60f;
				light.Range = options.VolumeLampRange;
				light.SourceRadius = 0.5f;
				// vollamplive=1: the lamps take live channels (8 a cloud: the rest are left out) instead of the baked texture.
				light.VolumeMode = options.VolumeLampsLive ? LightVolumeMode.Live : LightVolumeMode.Baked;
				if (options.VolumeLampPulse)
				{
					lamp.AddComponent<VolumetricLampPulse>().Phase = i * 0.9f;
				}
			}

			// volstormonly=1: of the clouds only the storm lights the GI (what one thick cloud does, without the others' light).
			contributeGI = contributeGI && !options.VolumeStormOnly;
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

			// vollight=1: a lamp (a scene Light) flying back and forth through the storm, its range entering and leaving it.
			if (options.VolumeMovingLight > 0)
			{
				VolumetricLightMover.Mode = options.VolumeMovingLight;
				GameObject lamp = scene.CreateGameObject("Moving Lamp");
				lamp.transform.position = new Vector3(-80f, 14f, 42f);
				Light light = lamp.AddComponent<Light>();
				light.Color = new Color4(1f, 0.55f, 0.25f, 1f);
				light.Intensity = 120f;
				light.Range = 14f;
				light.SourceRadius = 0.5f;
				lamp.AddComponent<VolumetricLightMover>();
			}

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

		/// <summary>
		///     voltest=N: a cloud whose light on a floor is known, in place of the scene's clouds, over a grey plate
		///     (Materials/LitGray, on the ground): black inside (it scatters nothing), opaque, giving off 1 everywhere, so
		///     it looks 1 from every side (use night=1: nothing else lights the plate, and linear=x: the frame's pixels are
		///     the light times x).
		///     <list type="bullet">
		///         <item>1: a ceiling (240 x 6 x 240 at y 14, far past the GI's volume). The plate gets the plate's colour x 1 everywhere.</item>
		///         <item>2: a ball (6 across, 10 above the plate's middle). Below it the plate gets its colour x (3 / 10)^2 = 0.09.</item>
		///         <item>3: the ball at 20 (inside the GI's volume still): its colour x 0.0225.</item>
		///     </list>
		/// </summary>
		private static void BuildLightTest(Scene scene, GITestScene.Options options)
		{
			GameObject plate = SceneModels.Instantiate(scene, SceneModels.Cube, "Grey Plate");
			plate.transform.position = new Vector3(0f, 0.03f, 0f);
			plate.transform.localScale = new Vector3(11f, 0.03f, 11f);
			MeshRenderer renderer = plate.GetComponent<MeshRenderer>();
			renderer.SetMaterials(SceneModels.Material("LitGray.material"));
			renderer.IsStatic = true;

			if (options.VolumeTest == 4)
			{
				// The ball of 2 as a mesh that gives off 1: what the GI itself makes of such a light (the clouds' reference).
				GameObject ball = SceneModels.Instantiate(scene, SceneModels.Sphere, "Mesh Ball");
				ball.transform.position = new Vector3(0f, 10f, 0f);
				ball.transform.scale = new Vector3(3f, 3f, 3f);
				var glowing = new LELEngine.Shaders.Material(SceneModels.Material("LitGray.material").UsingShader);
				glowing.SetColor("Color", new Color4(0f, 0f, 0f, 1f));
				glowing.SetColor("Emissive", new Color4(1f, 1f, 1f, 1f));
				MeshRenderer ballRenderer = ball.GetComponent<MeshRenderer>();
				ballRenderer.SetMaterials(glowing);
				ballRenderer.CastShadows = false;
				return;
			}

			bool ceiling = options.VolumeTest == 1;
			VolumetricObject cloud = Add<VolumetricObject>(scene, "Light Test", new Vector3(0f, ceiling ? 14f : options.VolumeTest == 2 ? 10f : 20f, 0f));
			cloud.Shape = ceiling ? VolumeShape.Box : VolumeShape.Sphere;
			cloud.Size = ceiling ? new Vector3(240f, 6f, 240f) : new Vector3(6f, 6f, 6f);
			cloud.Noise = VolumeNoise.Perlin;
			cloud.NoiseSize = 6f;
			cloud.Coverage = 1f;
			cloud.Softness = 0.02f;
			cloud.ClumpSize = 0f;
			cloud.SideSoftness = 0f;
			cloud.CapSoftness = 0f;
			cloud.TopRoughness = 0f;
			cloud.BottomRoughness = 0f;
			cloud.SideRoughness = 0f;
			cloud.Density = 60f;
			cloud.DetailStrength = 0f;
			cloud.DetailShade = 0f;
			cloud.VoxelSize = ceiling ? 1f : 0.1f;
			cloud.Albedo = new Color4(0f, 0f, 0f, 1f);
			cloud.EmissionColor = new Color4(1f, 1f, 1f, 1f);
			cloud.EmissionStrength = 1f;
			cloud.SkyStrength = 0f;
			cloud.AmbientStrength = 0f;
			cloud.ReceiveGI = false;
			cloud.SceneLights = false;
		}

		private static T Add<T>(Scene scene, string name, Vector3 position) where T : VolumetricObjectBase
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = position;
			T volume = go.AddComponent<T>();
			volume.ReceiveGI = receiveGI;
			volume.ContributeGI = contributeGI;
			volume.TemporalFilter = temporal;
			return volume;
		}

		// volgi=0: the volumes do not receive the GI; volgiout=0: they do not light it; voltemporal=0: no temporal
		// filter (comparisons).
		private static bool receiveGI = true;
		private static bool contributeGI = true;
		private static bool temporal = true;

		#endregion
	}

	/// <summary>
	///     Moves its object back and forth along x through the storm (x -80 .. 80 over 8 s), every frame (vollight=1); with
	///     vollight=2 the same until 6 s, then still at x = 10; with vollight=3 at x = 10 from the start (the transport worked
	///     out in place along a path against one made at once).
	/// </summary>
	public sealed class VolumetricLightMover : Behaviour
	{
		public static int Mode = 1;

		public override void Update()
		{
			float time = (float)Time.timeD;
			float x = -80f * MathF.Cos(time * MathF.PI * 2f / 8f);
			if (Mode == 3 || Mode == 2 && time >= 6f)
			{
				x = 10f;
			}

			transform.position = new Vector3(x, 14f, 42f);
		}
	}

	/// <summary>A lamp whose brightness changes every frame (vollamppulse=1): its light in a cloud has to follow.</summary>
	public sealed class VolumetricLampPulse : Behaviour
	{
		public float Phase;

		public override void Update()
		{
			Light light = GetComponent<Light>();
			if (light != null)
			{
				light.Intensity = 60f * (0.6f + 0.4f * MathF.Sin((float)Time.timeD * 3f + Phase));
			}
		}
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
