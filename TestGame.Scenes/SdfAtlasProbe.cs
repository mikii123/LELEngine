using System;
using System.Collections.Generic;
using LELEngine;
using LELEngine.Rendering.DistanceField;
using LELEngine.Rendering.Lumen;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Distance field atlas check (sdfatlas=1): spawns boxes of distinct shapes in steps and logs the atlas after each.
	///     A: 30 shapes at once (more than the first 1024 slices hold: the atlas grows). B: those go, and a second later 30
	///     other shapes come (the unused fields make room: no growth). C: 120 shapes at once (more than the deepest atlas
	///     holds: one warning, the rest without fields). D: those go; one box whose shape changes every frame for two
	///     seconds builds no field until its shape holds, then builds one.
	/// </summary>
	public sealed class SdfAtlasProbe : Behaviour
	{
		#region PrivateFields

		private readonly List<GameObject> boxes = new List<GameObject>();
		private GameObject animated;
		private float time;
		private int step;
		private int shapeSeed;

		#endregion

		#region UnityMethods

		public override void Update()
		{
			time += Time.deltaTime;
			if (animated != null && time < 9f)
			{
				// A new shape every frame (y stretch), then it holds from 9 s on.
				animated.transform.scale = new Vector3(0.3f, 0.05f + 0.25f * (0.5f + 0.5f * MathF.Sin(time * 7f)), 0.3f);
			}

			float[] at = { 0.5f, 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 10f };
			if (step >= at.Length || time < at[step])
			{
				return;
			}

			switch (step)
			{
				case 0:
					Spawn(30);
					break;
				case 1:
					Log("A: 30 shapes");
					Clear();
					break;
				case 2:
					Spawn(30);
					break;
				case 3:
					Log("B: 30 other shapes, a second after the first 30 went");
					Spawn(120);
					break;
				case 4:
					Log("C: 120 more shapes");
					Clear();
					break;
				case 5:
					animated = SceneModels.Instantiate(gameObject.scene, SceneModels.Cube, "AnimatedShape");
					animated.transform.position = new Vector3(0f, 1.5f, -5f);
					animated.GetComponent<MeshRenderer>().IsStatic = false;
					break;
				case 6:
					Log("D: one box changing shape every frame for a second");
					break;
				case 7:
					Log("D: the box held its shape for a second");
					break;
			}

			step++;
		}

		#endregion

		#region PrivateMethods

		private void Spawn(int count)
		{
			for (int i = 0; i < count; i++)
			{
				GameObject box = SceneModels.Instantiate(gameObject.scene, SceneModels.Cube, "SdfShape" + shapeSeed);
				// Every box a shape of its own: the y size steps by 0.4% of the x / z size.
				float y = 0.02f + 0.004f * shapeSeed++;
				box.transform.position = new Vector3(-6f + i % 12, 0.2f + 0.3f * (i / 12), -2f);
				box.transform.scale = new Vector3(0.2f, 0.2f * y, 0.2f);
				box.GetComponent<MeshRenderer>().IsStatic = false;
				boxes.Add(box);
			}
		}

		private void Clear()
		{
			foreach (GameObject box in boxes)
			{
				GameObject.Destroy(box);
			}

			boxes.Clear();
		}

		private static void Log(string what)
		{
			LumenScene scene = Game.Mono.Renderer.LumenScene;
			DistanceFieldAtlas atlas = scene.DistanceFields;
			if (atlas == null)
			{
				Console.WriteLine("[SdfAtlasProbe] " + what + ": no atlas (the GI's distance field is off)");
				return;
			}

			Console.WriteLine($"[SdfAtlasProbe] {what}: depth {atlas.Depth} of {atlas.MaxDepth}, {atlas.FieldCount} fields in {atlas.UsedSlices} slices, {scene.Objects.Count} GI objects");
		}

		#endregion
	}
}
