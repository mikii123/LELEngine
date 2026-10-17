using System;
using System.Collections.Generic;
using System.Globalization;
using LELEngine;
using LELEngine.Rendering;
using LELEngine.VFX;
using OpenTK.Mathematics;

namespace TestGame.Scenes
{
	/// <summary>
	///     Load test of the GPU particle system (vfx=1 vfxstress=1): one big additive system (vfxbig=capacity, a million by
	///     default) swirling in turbulence, a sorted soft alpha system with a large capacity and few particles alive
	///     (vfxsorted=capacity), many small sorted effects (vfxsmall=count, the default graph) and effects that finished
	///     their single burst and sit idle (vfxidle=count). <see cref="VfxStressStats" /> prints the CPU and GPU cost.
	/// </summary>
	public static class VfxStressScene
	{
		#region PublicMethods

		public static void Load(Scene scene, GITestScene.Options options, int big, int sorted, int small, int idle, int behind = 0)
		{
			VfxTestScene.BuildEnvironment(scene, options);

			if (big > 0)
			{
				Effect(scene, "Million", CreateBig(big), new Vector3(0f, 2f, 3f));
			}

			if (sorted > 0)
			{
				Effect(scene, "SortedSmoke", CreateSorted(sorted), new Vector3(-3f, 0.5f, 1f));
			}

			if (small > 0)
			{
				VfxGraph shared = VfxGraphBuilder.CreateDefault("SmallPuffs");
				int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(small * 2.0)));
				for (int i = 0; i < small; i++)
				{
					float x = (i % columns - columns * 0.5f) * 0.8f;
					float z = 6f + i / columns * 0.8f;
					Effect(scene, "Small" + i, shared, new Vector3(x, 0.2f, z));
				}
			}

			if (idle > 0)
			{
				VfxGraph pop = CreatePop();
				for (int i = 0; i < idle; i++)
				{
					Effect(scene, "Idle" + i, pop, new Vector3(-8f + i % 20 * 0.8f, 0.5f, -4f - i / 20 * 0.8f));
				}
			}

			// Effects behind the camera: frozen by visibility culling (the default graph simulates only while seen).
			if (behind > 0)
			{
				VfxGraph unseen = VfxGraphBuilder.CreateDefault("Unseen");
				for (int i = 0; i < behind; i++)
				{
					Effect(scene, "Behind" + i, unseen, new Vector3(-8f + i % 20 * 0.8f, 0.5f, -20f - i / 20 * 0.8f));
				}
			}

			scene.CreateGameObject("VfxStressStats").AddComponent<VfxStressStats>();
			GITestScene.InstallHarness(scene, options);
		}

		#endregion

		#region PrivateMethods

		private static void Effect(Scene scene, string name, VfxGraph graph, Vector3 position)
		{
			GameObject go = scene.CreateGameObject(name);
			go.transform.position = position;
			go.AddComponent<VisualEffect>().visualEffectAsset = graph;
		}

		/// <summary>A steady cloud of tiny additive particles at full capacity (turbulence, gravity, a floor, drag).</summary>
		private static VfxGraph CreateBig(int capacity)
		{
			var b = new VfxGraphBuilder("Million");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData rate = b.Block(spawn, "Block.ConstantSpawnRate");
			b.Inputs(rate, ("Rate", VfxValue.FromFloat(capacity / 4f)));

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Cloud");
			b.Settings(initialize, ("Capacity", capacity.ToString(CultureInfo.InvariantCulture)), ("Space", "World"), ("BoundsMode", "Automatic"));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(3.5f)), ("Max", VfxValue.FromFloat(3.95f)));
			VfxNodeData position = b.Block(initialize, "Block.PositionShape");
			b.Settings(position, ("Shape", "Sphere"), ("Mode", "Volume"));
			b.Inputs(position, ("Radius", VfxValue.FromFloat(1.5f)));
			VfxNodeData velocity = b.Block(initialize, "Block.VelocityRandomDirection");
			b.Inputs(velocity, ("Min Speed", VfxValue.FromFloat(0.2f)), ("Max Speed", VfxValue.FromFloat(1f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Inputs(size, ("Value", VfxValue.FromFloat(0.015f)));
			VfxNodeData color = b.Block(initialize, "Block.ColorFromGradientRandom");
			var palette = new VfxGradient();
			palette.ColorKeys.Add(new VfxGradientKey(0f, new Color4(1f, 0.4f, 0.1f, 1f)));
			palette.ColorKeys.Add(new VfxGradientKey(1f, new Color4(0.2f, 0.5f, 1f, 1f)));
			palette.AlphaKeys.Add(new VfxGradientKey(0f, Color4.White));
			palette.AlphaKeys.Add(new VfxGradientKey(1f, Color4.White));
			b.Inputs(color, ("Gradient", VfxValue.FromGradient(palette)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 620f);
			VfxNodeData turbulence = b.Block(update, "Block.Turbulence");
			b.Inputs(turbulence, ("Intensity", VfxValue.FromFloat(1.5f)), ("Frequency", VfxValue.FromFloat(0.6f)), ("Drag", VfxValue.FromFloat(1f)));
			VfxNodeData gravity = b.Block(update, "Block.Gravity");
			b.Inputs(gravity, ("Force", VfxValue.FromVector3(0f, -0.3f, 0f)));
			VfxNodeData floor = b.Block(update, "Block.CollidePlane");
			b.Inputs(floor, ("Position", VfxValue.FromVector3(0f, 0f, 0f)));
			VfxNodeData fade = b.Block(update, "Block.AlphaOverLife");
			b.Settings(fade, ("Composition", "Set"));

			VfxNodeData output = b.Context(VfxContextNodes.OutputQuad, 0f, 900f);
			// LEL_VFX_BIGSORTED=1: alpha blended, so the whole cloud sorts back to front every frame (a long sort).
			string bigSorted = Environment.GetEnvironmentVariable("LEL_VFX_BIGSORTED");
			b.Settings(output, ("Blend", bigSorted == "1" || bigSorted == "2" ? "Alpha" : "Additive"), ("Sort", bigSorted == "2" ? "Off" : "Auto"));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		/// <summary>Soft alpha smoke drawn back to front: a large capacity with about two thousand particles alive.</summary>
		private static VfxGraph CreateSorted(int capacity)
		{
			var b = new VfxGraphBuilder("SortedSmoke");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			VfxNodeData rate = b.Block(spawn, "Block.ConstantSpawnRate");
			b.Inputs(rate, ("Rate", VfxValue.FromFloat(600f)));

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Smoke");
			b.Settings(initialize, ("Capacity", capacity.ToString(CultureInfo.InvariantCulture)), ("BoundsMode", "Automatic"));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Settings(lifetime, ("Random", "Uniform"));
			b.Inputs(lifetime, ("Min", VfxValue.FromFloat(3f)), ("Max", VfxValue.FromFloat(4f)));
			VfxNodeData position = b.Block(initialize, "Block.PositionShape");
			b.Settings(position, ("Shape", "Sphere"), ("Mode", "Volume"));
			b.Inputs(position, ("Radius", VfxValue.FromFloat(0.6f)));
			VfxNodeData velocity = b.Block(initialize, "Block.VelocityDirection");
			b.Inputs(velocity, ("Direction", VfxValue.FromVector3(0f, 1f, 0f)), ("Min Speed", VfxValue.FromFloat(0.2f)), ("Max Speed", VfxValue.FromFloat(0.5f)), ("Direction Blend", VfxValue.FromFloat(0.6f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Inputs(size, ("Value", VfxValue.FromFloat(0.3f)));
			VfxNodeData color = b.Block(initialize, "Block.Set.color");
			b.Inputs(color, ("Value", VfxValue.FromVector3(0.6f, 0.65f, 0.7f)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 620f);
			VfxNodeData turbulence = b.Block(update, "Block.Turbulence");
			b.Inputs(turbulence, ("Intensity", VfxValue.FromFloat(0.6f)), ("Frequency", VfxValue.FromFloat(0.5f)), ("Drag", VfxValue.FromFloat(1f)));

			VfxNodeData output = b.Context(VfxContextNodes.OutputQuad, 0f, 900f);
			b.Settings(output, ("Blend", "Alpha"), ("SoftParticles", "1"), ("Sort", "Auto"));
			VfxNodeData colorOverLife = b.Block(output, "Block.ColorOverLife");
			var gradient = new VfxGradient();
			gradient.ColorKeys.Add(new VfxGradientKey(0f, Color4.White));
			gradient.ColorKeys.Add(new VfxGradientKey(1f, Color4.White));
			gradient.AlphaKeys.Add(new VfxGradientKey(0f, new Color4(1f, 1f, 1f, 0f)));
			gradient.AlphaKeys.Add(new VfxGradientKey(0.2f, new Color4(1f, 1f, 1f, 0.35f)));
			gradient.AlphaKeys.Add(new VfxGradientKey(1f, new Color4(1f, 1f, 1f, 0f)));
			b.Inputs(colorOverLife, ("Gradient", VfxValue.FromGradient(gradient)));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		/// <summary>One burst of sparks when the effect starts, then nothing: an effect that stays enabled with no particles.</summary>
		private static VfxGraph CreatePop()
		{
			var b = new VfxGraphBuilder("Pop");
			VfxNodeData spawn = b.Context(VfxContextNodes.Spawn, 0f, 0f);
			b.Settings(spawn, ("LoopMode", "Constant"));
			b.Inputs(spawn, ("Loop Duration", VfxValue.FromFloat(0.5f)), ("Loop Count", VfxValue.FromInt(1)));
			VfxNodeData burst = b.Block(spawn, "Block.SingleBurst");
			b.Inputs(burst, ("Count", VfxValue.FromFloat(16f)));

			VfxNodeData initialize = b.Context(VfxContextNodes.Initialize, 0f, 180f, "Sparks");
			b.Settings(initialize, ("Capacity", "64"));
			VfxNodeData lifetime = b.Block(initialize, "Block.Set.lifetime");
			b.Inputs(lifetime, ("Value", VfxValue.FromFloat(0.4f)));
			VfxNodeData velocity = b.Block(initialize, "Block.VelocityRandomDirection");
			b.Inputs(velocity, ("Min Speed", VfxValue.FromFloat(1f)), ("Max Speed", VfxValue.FromFloat(2f)));
			VfxNodeData size = b.Block(initialize, "Block.Set.size");
			b.Inputs(size, ("Value", VfxValue.FromFloat(0.05f)));

			VfxNodeData update = b.Context(VfxContextNodes.Update, 0f, 520f);
			b.Block(update, "Block.Gravity");

			VfxNodeData output = b.Context(VfxContextNodes.OutputQuad, 0f, 760f);
			b.Settings(output, ("Blend", "Additive"));

			b.Flow(spawn, initialize);
			b.Flow(initialize, update);
			b.Flow(update, output);
			return b.Graph;
		}

		#endregion
	}

	/// <summary>Prints the particle system's cost every two seconds: CPU update and render, the VFX passes on the GPU, alive particles.</summary>
	public sealed class VfxStressStats : Behaviour
	{
		#region PrivateFields

		private static readonly string[] Sections = { "VfxSimulate", "Shadows", "VfxOpaque", "Transparent", "Transparent.VfxSort", "Transparent.Draw" };
		private readonly Dictionary<string, double> sums = new Dictionary<string, double>();
		private double updateSum;
		private double renderSum;
		private double simulateCpuSum;
		private double prepareSum;
		private double packSum;
		private double dispatchSum;
		private double readbackSum;
		private double wallSum;
		private int frames;
		private float timer;
		private int reports;

		#endregion

		#region UnityMethods

		public override void Update()
		{
			GpuProfiler profiler = Game.Renderer?.Profiler;
			if (profiler == null)
			{
				return;
			}

			// Skip the warm-up (compilation, buffers filling up).
			if (Time.time < 3f)
			{
				return;
			}

			frames++;
			timer += Time.deltaTime;
			updateSum += Time.cpuUpdateMs;
			renderSum += Time.cpuRenderMs;
			simulateCpuSum += VfxRuntime.LastSimulationCpuMs;
			VfxFrameStats vfx = VfxRuntime.Stats;
			prepareSum += vfx.PrepareMs;
			packSum += vfx.PackMs;
			dispatchSum += vfx.DispatchMs;
			readbackSum += vfx.ReadbackMs;
			wallSum += profiler.FrameWallMilliseconds;
			foreach (KeyValuePair<string, double> result in profiler.Results)
			{
				foreach (string section in Sections)
				{
					if (result.Key == section)
					{
						double sum;
						sums.TryGetValue(section, out sum);
						sums[section] = sum + result.Value;
					}
				}
			}

			if (timer < 2f)
			{
				return;
			}

			VfxFrameStats vfxLast = VfxRuntime.Stats;
			var line = new System.Text.StringBuilder();
			line.Append("[VfxStress] ").Append(F(frames / timer, "0.0")).Append(" fps | CPU update ").Append(F(updateSum / frames))
				.Append(" ms render ").Append(F(renderSum / frames)).Append(" ms (vfx simulate ").Append(F(simulateCpuSum / frames))
				.Append(": prepare ").Append(F(prepareSum / frames)).Append(" pack ").Append(F(packSum / frames))
				.Append(" dispatch ").Append(F(dispatchSum / frames)).Append(" readback ").Append(F(readbackSum / frames))
				.Append(") | GPU frame ").Append(F(wallSum / frames)).Append(" ms:");
			foreach (string section in Sections)
			{
				double sum;
				sums.TryGetValue(section, out sum);
				line.Append(' ').Append(section).Append(' ').Append(F(sum / frames));
			}

			line.Append(" | alive ").Append(VfxRuntime.TotalAlive).Append(" in ").Append(VfxRuntime.Instances.Count).Append(" effects (")
				.Append(vfxLast.SimulatedSystems).Append(" systems simulated, ").Append(vfxLast.FrozenInstances).Append(" frozen, ")
				.Append(vfxLast.Dispatches).Append(" dispatches, ").Append(vfxLast.Barriers).Append(" barriers, ")
				.Append(vfxLast.AllocatedBytes).Append(" bytes allocated)");
			Console.WriteLine(line.ToString());
			reports++;
			frames = 0;
			timer = 0f;
			updateSum = renderSum = simulateCpuSum = wallSum = 0;
			prepareSum = packSum = dispatchSum = readbackSum = 0;
			sums.Clear();
		}

		#endregion

		#region PrivateMethods

		private static string F(double value, string format = "0.00")
		{
			return value.ToString(format, CultureInfo.InvariantCulture);
		}

		#endregion
	}
}
