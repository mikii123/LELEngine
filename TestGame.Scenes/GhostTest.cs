using System;
using System.Collections.Generic;
using System.Globalization;
using LELEngine;
using LELEngine.Rendering;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Scripted events and readbacks for measuring how fast the GI follows a change (ghosting), frame exact. Frame N
	///     is the N-th rendered frame: an event "at frame N" is first visible in frame N. Combine with capturefps=60 so
	///     motion per frame does not depend on the frame rate, and with animate=0 for a still scene around the event.
	///     - <see cref="EmitterStepFrame" />: the orb Orb0 jumps half its orbit (to the other side of the room).
	///     - <see cref="EmitterOffFrame" />: Orb0 goes dark.
	///     - <see cref="MoverFrame" />: a dynamic box crosses the room in front of the props at <see cref="MoverSpeed" />.
	///     - <see cref="RegionPoints" />: world points whose screen squares are logged every frame ("[Regions] frame v...").
	///     - <see cref="NoiseFrames" />: per-pixel temporal statistics of a window of frames ("[Noise] ...").
	/// </summary>
	public sealed class GhostTest : Behaviour
	{
		#region PublicFields

		public int EmitterStepFrame;
		public int EmitterOffFrame;
		public int MoverFrame;
		public float MoverSpeed = 3f;
		public Vector3 MoverStart = new Vector3(7f, 0.35f, -1.6f);
		public Vector3 MoverEnd = new Vector3(-7f, 0.35f, -1.6f);
		public string EmitterName = "Orb0";

		public readonly List<Vector3> RegionPoints = new List<Vector3>();

		/// <summary>Edge of the logged squares, in screen pixels.</summary>
		public int RegionSize = 16;

		public int RegionStartFrame = 1;
		public FrameStatistics.Source RegionSource = FrameStatistics.Source.SceneColor;

		public int NoiseFrames;
		public int NoiseStartFrame;

		#endregion

		#region PrivateFields

		private EmissiveOrbiter emitter;
		private Transform mover;
		private bool regionsReported;

		#endregion

		#region UnityMethods

		public override void Start()
		{
			foreach (EmissiveOrbiter orbiter in gameObject.scene.FindObjectsOfType<EmissiveOrbiter>())
			{
				if (orbiter.gameObject.Name == EmitterName)
				{
					emitter = orbiter;
				}
			}

			if (MoverFrame > 0)
			{
				GameObject box = SceneModels.Instantiate(gameObject.scene, SceneModels.Cube, "Mover");
				box.transform.position = MoverStart;
				box.transform.scale = new Vector3(MoverStart.Y);
				MeshRenderer renderer = box.GetComponent<MeshRenderer>();
				renderer.SetMaterials(SceneModels.Material("LitGray.material"));
				renderer.IsStatic = false;
				mover = box.transform;
			}

			Game.Renderer.Statistics.ReadSource = RegionSource;
		}

		public override void Update()
		{
			// The frame this update prepares (Renderer.FrameIndex counts the frames already rendered).
			int frame = Game.Renderer.FrameIndex + 1;

			if (emitter != null && frame == EmitterStepFrame)
			{
				emitter.PhaseOffset += MathHelper.Pi;
				Console.WriteLine($"[GhostTest] frame {frame}: {EmitterName} jumps half its orbit");
			}

			if (emitter != null && frame == EmitterOffFrame)
			{
				emitter.Intensity = 0f;
				Console.WriteLine($"[GhostTest] frame {frame}: {EmitterName} off");
			}

			if (mover != null && frame >= MoverFrame)
			{
				Vector3 path = MoverEnd - MoverStart;
				float travelled = Math.Min(path.Length, (frame - MoverFrame) * Time.deltaTime * MoverSpeed);
				mover.position = MoverStart + path.Normalized() * travelled;
			}

			if (NoiseFrames > 0 && frame == NoiseStartFrame)
			{
				Game.Renderer.Statistics.StartNoise(NoiseFrames);
			}
		}

		public override void LateUpdate()
		{
			int frame = Game.Renderer.FrameIndex + 1;
			if (RegionPoints.Count == 0 || frame < RegionStartFrame || Camera.main == null)
			{
				return;
			}

			// The camera's matrices are last frame's (they update when the frame renders): exact for a still camera.
			Matrix4 viewProjection = Camera.main.ViewProjectionMatrix;
			int width = Game.Renderer.Width;
			int height = Game.Renderer.Height;
			var rects = new List<Vector4i>(RegionPoints.Count);
			foreach (Vector3 point in RegionPoints)
			{
				Vector4 clip = new Vector4(point, 1f) * viewProjection;
				Vector2 ndc = clip.W > 1e-4f ? clip.Xy / clip.W : new Vector2(-2f);
				int x = (int)Math.Round((ndc.X * 0.5f + 0.5f) * width) - RegionSize / 2;
				int y = (int)Math.Round((ndc.Y * 0.5f + 0.5f) * height) - RegionSize / 2;
				rects.Add(new Vector4i(x, y, RegionSize, RegionSize));
			}

			Game.Renderer.Statistics.SetRegions(rects);
			if (!regionsReported)
			{
				regionsReported = true;
				for (int i = 0; i < rects.Count; i++)
				{
					Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "[GhostTest] region {0}: ({1:0.00}, {2:0.00}, {3:0.00}) -> pixel ({4}, {5}) from the bottom left, {6} px",
						i, RegionPoints[i].X, RegionPoints[i].Y, RegionPoints[i].Z, rects[i].X + RegionSize / 2, rects[i].Y + RegionSize / 2, RegionSize));
				}
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Parses "x,y,z;x,y,z;..." (invariant culture) into <see cref="RegionPoints" />.</summary>
		public void ParseRegionPoints(string text)
		{
			RegionPoints.Clear();
			if (string.IsNullOrWhiteSpace(text))
			{
				return;
			}

			foreach (string point in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
			{
				string[] parts = point.Split(',');
				if (parts.Length == 3
					&& float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
					&& float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
					&& float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
				{
					RegionPoints.Add(new Vector3(x, y, z));
				}
			}
		}

		#endregion
	}
}
