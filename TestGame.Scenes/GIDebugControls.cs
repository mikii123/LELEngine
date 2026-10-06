using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using LELEngine;
using LELEngine.Rendering;
using LELEngine.Rendering.Passes;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace TestGame.Scenes
{
	/// <summary>
	///     Keyboard toggles for inspecting the lighting pipeline:
	///     F1 GI on/off, F2 debug view (off / voxels / SDF), F3/F4 voxel view mip -/+, F5 shadows on/off,
	///     F6 voxel resolution 64/128/256, F7 depth prepass on/off, F8 tonemap on/off,
	///     F9 half-res GI resolve on/off, F10 rebuild static caches, F11 multi-bounce on/off,
	///     T cone visibility: SDF detail trace / voxel cones only.
	/// </summary>
	public sealed class GIDebugControls : Behaviour
	{
		#region PublicFields

		/// <summary>Print average frame time to the console every <see cref="StatsInterval" /> seconds.</summary>
		public bool PrintStats;

		public float StatsInterval = 2f;

		public enum DebugView
		{
			Off,
			Voxels,
			DistanceField
		}

		public DebugView View { get; private set; } = DebugView.Off;

		#endregion

		#region PrivateFields

		private static readonly int[] resolutions = { 64, 128, 256 };
		private float statsTimer;
		private int statsFrames;
		private readonly Dictionary<string, double> passSums = new Dictionary<string, double>();
		private readonly List<string> passOrder = new List<string>();

		#endregion

		#region UnityMethods

		public override void Start()
		{
			Console.WriteLine("[Controls] WASD move, Shift sprint, Esc cursor, F fly | F1 GI, F2 debug view, F3/F4 mip, F5 shadows, F6 voxel res, F7 depth prepass, F8 tonemap, F9 resolve, F10 cache, F11 bounce, T trace mode");
		}

		public override void Update()
		{
			if (PrintStats)
			{
				AccumulateStats();
			}

			Renderer renderer = Game.Mono.Renderer;
			VoxelDebugPass voxelDebug = renderer.GetPass<VoxelDebugPass>();
			SdfDebugPass sdfDebug = renderer.GetPass<SdfDebugPass>();

			if (Input.GetKeyDown(Keys.F1))
			{
				Lighting.GI.Enabled = !Lighting.GI.Enabled;
				Console.WriteLine("[GI] " + (Lighting.GI.Enabled ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.F2))
			{
				View = (DebugView)(((int)View + 1) % 3);
				if (voxelDebug != null) voxelDebug.Enabled = View == DebugView.Voxels;
				if (sdfDebug != null) sdfDebug.Enabled = View == DebugView.DistanceField;
				Console.WriteLine("[Debug] view " + View);
			}

			if (Input.GetKeyDown(Keys.F3) && voxelDebug != null)
			{
				voxelDebug.Mip = Math.Max(0f, voxelDebug.Mip - 1f);
				Console.WriteLine("[GI] voxel view mip " + voxelDebug.Mip);
			}

			if (Input.GetKeyDown(Keys.F4) && voxelDebug != null)
			{
				voxelDebug.Mip = Math.Min((float)Math.Log(Lighting.GI.Resolution, 2), voxelDebug.Mip + 1f);
				Console.WriteLine("[GI] voxel view mip " + voxelDebug.Mip);
			}

			if (Input.GetKeyDown(Keys.F5))
			{
				Lighting.Shadows.Enabled = !Lighting.Shadows.Enabled;
				Console.WriteLine("[Shadows] " + (Lighting.Shadows.Enabled ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.F6))
			{
				int index = Array.IndexOf(resolutions, Lighting.GI.Resolution);
				Lighting.GI.Resolution = resolutions[(index + 1) % resolutions.Length];
				Console.WriteLine("[GI] voxel resolution " + Lighting.GI.Resolution);
			}

			if (Input.GetKeyDown(Keys.F7))
			{
				renderer.Settings.DepthPrepass = !renderer.Settings.DepthPrepass;
				Console.WriteLine("[Renderer] depth prepass " + (renderer.Settings.DepthPrepass ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.F8))
			{
				renderer.Settings.Tonemap = !renderer.Settings.Tonemap;
				Console.WriteLine("[Renderer] tonemap " + (renderer.Settings.Tonemap ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.F9))
			{
				Lighting.GI.ScreenSpaceResolve = !Lighting.GI.ScreenSpaceResolve;
				Console.WriteLine("[GI] half-res resolve " + (Lighting.GI.ScreenSpaceResolve ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.F10))
			{
				Lighting.GI.InvalidateStatic();
				Console.WriteLine("[GI] static caches invalidated");
			}

			if (Input.GetKeyDown(Keys.F11))
			{
				Lighting.GI.BounceStrength = Lighting.GI.BounceStrength > 0f ? 0f : 1f;
				Console.WriteLine("[GI] multi-bounce " + (Lighting.GI.BounceStrength > 0f ? "on" : "off"));
			}

			if (Input.GetKeyDown(Keys.T))
			{
				Lighting.GI.TraceMode = Lighting.GI.TraceMode == GITraceMode.SdfDetail ? GITraceMode.VoxelCones : GITraceMode.SdfDetail;
				Console.WriteLine("[GI] trace mode " + Lighting.GI.TraceMode);
			}
		}

		#endregion

		#region PrivateMethods

		// Averages GPU pass times over the stats interval; single frames are too noisy to read.
		private void AccumulateStats()
		{
			GpuProfiler profiler = Game.Mono.Renderer.Profiler;
			statsTimer += Time.deltaTime;
			statsFrames++;

			foreach (KeyValuePair<string, double> result in profiler.Results)
			{
				double sum;
				if (!passSums.TryGetValue(result.Key, out sum))
				{
					passOrder.Add(result.Key);
				}
				passSums[result.Key] = sum + result.Value;
			}

			if (statsTimer < StatsInterval)
			{
				return;
			}

			StringBuilder passes = new StringBuilder();
			double total = 0;
			foreach (string name in passOrder)
			{
				double average = passSums[name] / statsFrames;
				total += average;
				if (average < 0.005)
				{
					continue;
				}
				passes.Append(' ').Append(name).Append(' ').Append(average.ToString("0.00", CultureInfo.InvariantCulture));
			}

			Console.WriteLine(
				$"[Stats] {statsFrames / statsTimer:0.0} fps | GPU {total.ToString("0.00", CultureInfo.InvariantCulture)} ms |{passes}" +
				$" | GI {(Lighting.GI.Enabled ? "on" : "off")} {Lighting.GI.Resolution}^3 resolve {(Lighting.GI.ResolveActive ? "on" : "off")} trace {Lighting.GI.TraceMode}");

			statsTimer = 0f;
			statsFrames = 0;
			passSums.Clear();
			passOrder.Clear();
		}

		#endregion
	}
}
