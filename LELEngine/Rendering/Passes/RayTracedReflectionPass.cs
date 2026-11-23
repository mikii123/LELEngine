using System;
using LELEngine.Graphics;
using LELEngine.Mathematics;
using LELEngine.Rendering.Lumen;
using LELEngine.Shaders;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Reflections of glossy surfaces traced through the scene's triangles (<see cref="Lighting.RayTracedReflections" />:
	///     Lumen with its hardware ray tracing), after the screen probe gather: Engine/RayTracedReflections.shader traces one
	///     ray a pixel along the surface's GGX lobe for every pixel of the resolve below
	///     <see cref="RayTracingSettings.ReflectionMaxRoughness" />, lights what it hits from the surface cache (as Lumen's
	///     rays do), accumulates it over frames, and Engine/RayTracedReflectionFilter.shader smooths it and blends it into
	///     the probes' rough reflection, written over the gather's specular (<see cref="GlobalIlluminationSettings.ResolvedSpecular" />)
	///     where the surface was traced, which the materials read. A surface too rough to trace costs one normal read and
	///     a small write.
	/// </summary>
	public sealed class RayTracedReflectionPass : RenderPass
	{
		#region PublicFields

		public override string Name => "RayTracedReflections";

		#endregion

		#region PrivateFields

		private ComputeShader trace;
		private ComputeShader filter;
		private readonly GpuTexture[] history = new GpuTexture[2];
		private readonly GpuTexture[] meta = new GpuTexture[2];
		private int current;
		private int width;
		private int height;
		private bool historyValid;
		private int frameIndex;
		private Matrix4 previousViewProjection;
		private Vector3 previousCameraPosition;
		private Vector3 previousCameraForward;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			GlobalIlluminationSettings gi = Lighting.GI;
			LumenScene scene = context.Renderer.LumenScene;
			ScreenProbeGatherPass gather = context.Renderer.GetPass<ScreenProbeGatherPass>();
			bool active = Lighting.RayTracedReflections && gather != null && gather.ProbesReady && gi.ResolveActive && gi.ResolvedSpecular != null
				&& scene.SurfaceCache != null && scene.RayTracing?.TopLevel != null && context.Motion != null && context.NormalRoughness != null;
			if (!active)
			{
				historyValid = false;
				if (!Lighting.RayTracedReflections)
				{
					Release();
				}

				return;
			}

			trace ??= new ComputeShader("Engine/RayTracedReflections.shader", Lighting.RayTracingDefines);
			filter ??= new ComputeShader("Engine/RayTracedReflectionFilter.shader");
			EnsureTargets(gi.ResolveWidth, gi.ResolveHeight);
			frameIndex++;
			int previous = 1 - current;

			context.Renderer.Profiler.Split("RayTracedReflections.trace");
			CommandList commands = Gfx.Commands;
			ShaderProgram program = trace.Program;
			trace.Use();
			scene.SetUniforms(program);
			Lighting.SetSdfUniforms(program);
			Lighting.SetSkyUniforms(program);
			program.SetTexture("SceneDepth", context.SceneDepth);
			program.SetTexture("NormalRoughness", context.NormalRoughness);
			program.SetTexture("Motion", context.Motion);
			program.SetTexture("ProbeSpecular", gi.ResolvedSpecular);
			program.SetTexture("FinalLighting", scene.SurfaceCache.FinalLightingCurrent);
			program.SetTexture("HistoryReflection", history[previous]);
			program.SetTexture("HistoryMeta", meta[previous]);
			program.SetMatrix4("invViewProjection", Matrix4.Invert(context.ViewProjection));
			program.SetMatrix4("previousViewProjection", previousViewProjection);
			program.SetVector3("cameraPosition", context.CameraPosition);
			program.SetVector3("cameraForward", context.CameraForward);
			program.SetVector3("previousCameraPosition", previousCameraPosition);
			program.SetVector3("previousCameraForward", previousCameraForward);
			program.SetInt2("screenSize", context.Width, context.Height);
			program.SetInt2("outputSize", width, height);
			program.SetFloat("maxRoughness", Math.Clamp(Lighting.RayTracing.ReflectionMaxRoughness, 0.05f, 1f));
			program.SetInt("frameIndex", frameIndex);
			program.SetInt("historyValid", historyValid ? 1 : 0);
			commands.SetImage(1, history[current], 0, 0, ImageAccess.Write);
			commands.SetImage(2, meta[current], 0, 0, ImageAccess.Write);
			trace.DispatchThreads(width, height, 1, 8, 8, 1);

			// Smoothed over the surface's pixels and blended into the probes' reflection, written over it where traced: the
			// materials read it there (the gather's next frame takes it as its history: on the traced surfaces it is
			// written over again).
			context.Renderer.Profiler.Split("RayTracedReflections.filter");
			ShaderProgram filterProgram = filter.Program;
			filter.Use();
			filterProgram.SetTexture("TracedReflection", history[current]);
			filterProgram.SetTexture("Meta", meta[current]);
			filterProgram.SetInt2("outputSize", width, height);
			filterProgram.SetFloat("maxRoughness", Math.Clamp(Lighting.RayTracing.ReflectionMaxRoughness, 0.05f, 1f));
			commands.SetImage(0, gi.ResolvedSpecular, 0, 0, ImageAccess.ReadWrite);
			filter.DispatchThreads(width, height, 1, 8, 8, 1);

			previousViewProjection = context.ViewProjection;
			previousCameraPosition = context.CameraPosition;
			previousCameraForward = context.CameraForward;
			historyValid = true;
			current = previous;
		}

		public override void Resize(int width, int height)
		{
			historyValid = false;
		}

		public override void ReleaseUnusedResources()
		{
			if (!Lighting.RayTracedReflections)
			{
				Release();
			}
		}

		public override void Dispose()
		{
			Release();
		}

		#endregion

		#region PrivateMethods

		private void EnsureTargets(int newWidth, int newHeight)
		{
			if (newWidth == width && newHeight == height && history[0] != null)
			{
				return;
			}

			DeleteTargets();
			width = newWidth;
			height = newHeight;
			historyValid = false;
			const TextureUsage written = TextureUsage.Sampled | TextureUsage.Storage;
			for (int i = 0; i < 2; i++)
			{
				history[i] = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA16F, width, height, 1, written, "Traced reflections history " + i));
				history[i].Sampler = Samplers.PointClamp;
				meta[i] = Gfx.Device.CreateTexture(TextureDescriptor.Texture2D(TextureFormat.RGBA16F, width, height, 1, written, "Traced reflections meta " + i));
				meta[i].Sampler = Samplers.PointClamp;
			}

			Console.WriteLine($"[RayTracing] Traced reflections {width}x{height} (~{(long)width * height * 8 * 4 / 1048576.0:0} MB of history)");
		}

		private void DeleteTargets()
		{
			for (int i = 0; i < 2; i++)
			{
				history[i]?.Dispose();
				history[i] = null;
				meta[i]?.Dispose();
				meta[i] = null;
			}

			width = 0;
			height = 0;
		}

		private void Release()
		{
			if (trace == null && history[0] == null)
			{
				return;
			}

			trace?.Delete();
			trace = null;
			filter?.Delete();
			filter = null;
			DeleteTargets();
			historyValid = false;
		}

		#endregion
	}
}
