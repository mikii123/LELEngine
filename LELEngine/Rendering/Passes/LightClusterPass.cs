using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     The lights' clusters of each camera's view (Engine/Lights.glsl's LelLightsAt), made before the camera's passes:
	///     the view cut into tiles of about <see cref="TileSize" /> pixels x <see cref="Slices" /> slices of depth
	///     (logarithmic from the camera to its far plane), each with a bit per light of the list (<see cref="LightBuffer" />)
	///     whose range reaches the cluster's box. A lit pixel visits only its cluster's lights, so the list has no limit: no
	///     light takes another's place, and a light costs only the pixels it may reach (a bullet's light near the camera
	///     does not put out the lamps further away, and costs nothing where it does not shine). Made on the GPU
	///     (Engine/LightClusters.shader: one thread per cluster and 32 lights), one set per camera of the frame, so a later
	///     camera never writes masks an earlier one's draws read. Nothing is kept between frames but the buffers.
	/// </summary>
	public sealed class LightClusterPass : RenderPass
	{
		#region PublicFields

		/// <summary>Pixels of the render target across a tile (the screen is cut into even steps of NDC of about this size).</summary>
		public const int TileSize = 64;

		/// <summary>Slices of depth, logarithmic: each reaches a fixed multiple of its near depth.</summary>
		public const int Slices = 32;

		public override string Name => "LightClusters";

		public override RenderPassScope Scope => RenderPassScope.EveryCamera;

		#endregion

		#region PrivateFields

		// The build's output binding: not the list's or the masks' (the build reads those), nor one of 0..12, which the
		// particles (VFX) cache; the GPU-driven draws bind their previous models there before each use (GpuScene).
		private const int BuildBinding = 15;
		private const int GroupSize = 64;

		private readonly List<Clusters> perCamera = new List<Clusters>();
		private ComputeShader build;
		private int frame = -1;
		private int next;

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			int count = LightBuffer.Count;
			Camera camera = context.Camera;
			if (count == 0 || camera == null || context.Width <= 0 || context.Height <= 0)
			{
				// Nothing to cluster: every light (none, or no view to cut).
				LightBuffer.UseNoClusters();
				return;
			}

			// One set per camera of the frame, in the order the cameras render.
			if (frame != context.Renderer.FrameIndex)
			{
				frame = context.Renderer.FrameIndex;
				next = 0;
			}

			if (next == perCamera.Count)
			{
				perCamera.Add(new Clusters());
			}

			Clusters clusters = perCamera[next++];
			int tilesX = (context.Width + TileSize - 1) / TileSize;
			int tilesY = (context.Height + TileSize - 1) / TileSize;
			int words = (count + 31) / 32;
			int cells = tilesX * tilesY * Slices;
			clusters.Ensure((long)cells * words * sizeof(uint));

			// Slices from the camera to a little past its far plane: a depth's slice is floor(log(depth) x perLog + bias),
			// the first reaching back to the camera.
			float near = MathF.Max(camera.NearClip, 1e-4f);
			float far = MathF.Max(camera.FarClip, near * 2f);
			float perLog = Slices / MathF.Log(far / near);
			var slicing = new Vector4(perLog, -MathF.Log(near) * perLog, 0f, far * 1.01f);
			// The view depth of a world point p: -(p, 1) . the view matrix's third column (row vectors, the camera looks along -z).
			Vector4 depth = -context.View.Column2;
			LightBuffer.WriteHeader(clusters.Header, count, words, tilesX, tilesY, Slices, context.ViewProjection, depth, slicing);

			build ??= new ComputeShader("Engine/LightClusters.shader");
			ShaderProgram program = build.Program;
			if (!program.IsLinked)
			{
				LightBuffer.UseNoClusters();
				return;
			}

			LightBuffer.EnsureBound();
			build.Use();
			program.SetMatrix4("clusterView", context.View);
			program.SetMatrix4("clusterInverseProjection", context.Projection.Inverted());
			program.SetInt3("clusterGrid", tilesX, tilesY, Slices);
			program.SetInt("clusterWords", words);
			program.SetInt("clusterLights", count);
			program.SetVector4("clusterSlicing", slicing);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BuildBinding, clusters.Masks);
			build.Dispatch((cells + GroupSize - 1) / GroupSize, words);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, BuildBinding, 0);
			GL.MemoryBarrier(MemoryBarrierFlags.ShaderStorageBarrierBit);
			LightBuffer.UseClusters(clusters.Header, clusters.Masks);
		}

		public override void Dispose()
		{
			foreach (Clusters clusters in perCamera)
			{
				clusters.Delete();
			}

			perCamera.Clear();
			build?.Delete();
			build = null;
		}

		#endregion

		#region NestedTypes

		/// <summary>A camera's clusters: their header (uniform buffer) and masks (storage buffer, grown as needed).</summary>
		private sealed class Clusters
		{
			public int Header;
			public int Masks;
			private long maskBytes;

			public void Ensure(long bytes)
			{
				if (Header == 0)
				{
					Header = GL.GenBuffer();
					GL.BindBuffer(BufferTarget.UniformBuffer, Header);
					GL.BufferData(BufferTarget.UniformBuffer, LightBuffer.HeaderBytes, IntPtr.Zero, BufferUsageHint.DynamicDraw);
					GL.BindBuffer(BufferTarget.UniformBuffer, 0);
				}

				if (Masks == 0 || bytes > maskBytes)
				{
					if (Masks == 0)
					{
						Masks = GL.GenBuffer();
					}

					// Doubling: more lights or a larger view reallocate rarely.
					maskBytes = Math.Max(bytes, maskBytes * 2);
					GL.BindBuffer(BufferTarget.ShaderStorageBuffer, Masks);
					GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)maskBytes, IntPtr.Zero, BufferUsageHint.DynamicCopy);
					GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
				}
			}

			public void Delete()
			{
				LightBuffer.ForgetClusters(Header, Masks);
				if (Header != 0)
				{
					GL.DeleteBuffer(Header);
				}

				if (Masks != 0)
				{
					GL.DeleteBuffer(Masks);
				}

				Header = 0;
				Masks = 0;
				maskBytes = 0;
			}
		}

		#endregion
	}
}
