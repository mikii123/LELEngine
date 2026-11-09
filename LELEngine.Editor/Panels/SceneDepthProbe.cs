using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     What the scene view drew, as depths on the CPU: after the view renders, a compute pass reduces the render's
	///     depth to a grid of <see cref="GridWidth" /> x <see cref="GridHeight" /> cells (each the nearest surface of a 4 x 4
	///     pattern over it), read back through a buffer and a fence a frame or two later (no stall). The scene view's
	///     navigation takes the depth to pan and zoom at from it when the cursor is over no mesh: the nearest surface drawn
	///     near the cursor, else the typical depth of what is drawn (2026-11-08, the user: after zooms here and there a pan
	///     barely moved or raced, as it kept the depth of whatever the last zoom hit, or of the ground plane far away).
	///     The GI's global distance field was not used for it: it only exists while the GI uses one, only inside the GI's
	///     volume, and holds distances within a band of a few voxels from surfaces (SdfBandVoxels, e.g. 8).
	/// </summary>
	internal sealed class SceneDepthProbe : IDisposable
	{
		#region PublicFields

		public const int GridWidth = 64;
		public const int GridHeight = 36;

		/// <summary>Seconds between two captures while the view keeps rendering.</summary>
		public const double CaptureInterval = 0.05;

		#endregion

		#region PrivateFields

		private const string Source = @"#version 430
layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;
uniform sampler2D SceneDepth;
uniform ivec2 sourceSize;
uniform ivec2 gridSize;
layout(std430, binding = 0) writeonly buffer Cells { float cells[]; };
void main()
{
	ivec2 cell = ivec2(gl_GlobalInvocationID.xy);
	if (any(greaterThanEqual(cell, gridSize)))
	{
		return;
	}

	// The nearest surface of a 4 x 4 pattern over the cell (thin objects still count).
	vec2 cellSize = vec2(sourceSize) / vec2(gridSize);
	float nearest = 1.0;
	for (int j = 0; j < 4; j++)
	{
		for (int i = 0; i < 4; i++)
		{
			ivec2 texel = ivec2((vec2(cell) + (vec2(i, j) + 0.5) / 4.0) * cellSize);
			nearest = min(nearest, texelFetch(SceneDepth, clamp(texel, ivec2(0), sourceSize - 1), 0).r);
		}
	}

	cells[cell.y * gridSize.x + cell.x] = nearest;
}
";

		private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

		private ShaderProgram program;
		private int buffer;
		private IntPtr fence;
		private double lastCapture = -1.0;

		// The capture in flight and the last one read: the camera it was drawn with.
		private Matrix4 pendingInverseViewProjection;
		private readonly float[] cells = new float[GridWidth * GridHeight];
		private Matrix4 inverseViewProjection;
		private bool hasCells;
		private readonly List<float> depths = new List<float>(GridWidth * GridHeight);

		#endregion

		#region PublicProperties

		/// <summary>A capture has been read (the queries answer from it).</summary>
		public bool HasData => hasCells;

		#endregion

		#region PublicMethods

		/// <summary>After the view rendered: reduces its depth unless a capture is in flight or the last one is very recent.</summary>
		public void Capture(Renderer renderer, Camera camera)
		{
			if (fence != IntPtr.Zero || renderer?.SceneTarget == null || Clock.Elapsed.TotalSeconds - lastCapture < CaptureInterval)
			{
				return;
			}

			RenderTexture depth = renderer.SceneDepth;
			if (depth == null || depth.Handle == 0)
			{
				return;
			}

			program ??= ShaderProgram.FromSources("Editor/SceneDepthProbe", new[] { new KeyValuePair<ShaderType, string>(ShaderType.ComputeShader, Source) });
			if (buffer == 0)
			{
				buffer = GL.GenBuffer();
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
				GL.BufferData(BufferTarget.ShaderStorageBuffer, cells.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.StreamRead);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			}

			// Other passes keep their own storage buffer on binding 0: put it back afterwards.
			GL.GetInteger((GetIndexedPName)All.ShaderStorageBufferBinding, 0, out int previous);
			program.Use();
			program.SetTexture("SceneDepth", TextureTarget.Texture2D, depth.Handle, 0);
			program.SetInt2("sourceSize", depth.Width, depth.Height);
			program.SetInt2("gridSize", GridWidth, GridHeight);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, buffer);
			GL.DispatchCompute((GridWidth + 7) / 8, (GridHeight + 7) / 8, 1);
			GL.MemoryBarrier(MemoryBarrierFlags.BufferUpdateBarrierBit | MemoryBarrierFlags.ClientMappedBufferBarrierBit);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, previous);
			GL.UseProgram(0);
			fence = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
			pendingInverseViewProjection = camera.ViewProjectionMatrix.Inverted();
			lastCapture = Clock.Elapsed.TotalSeconds;
		}

		/// <summary>Every frame: takes the capture in flight once the GPU finished it.</summary>
		public void Update()
		{
			if (fence == IntPtr.Zero)
			{
				return;
			}

			WaitSyncStatus status = GL.ClientWaitSync(fence, ClientWaitSyncFlags.None, 0);
			if (status == WaitSyncStatus.TimeoutExpired)
			{
				return;
			}

			GL.DeleteSync(fence);
			fence = IntPtr.Zero;
			if (status == WaitSyncStatus.WaitFailed)
			{
				return;
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
			IntPtr mapped = GL.MapBufferRange(BufferTarget.ShaderStorageBuffer, IntPtr.Zero, cells.Length * sizeof(float), MapBufferAccessMask.MapReadBit);
			if (mapped != IntPtr.Zero)
			{
				System.Runtime.InteropServices.Marshal.Copy(mapped, cells, 0, cells.Length);
				GL.UnmapBuffer(BufferTarget.ShaderStorageBuffer);
				inverseViewProjection = pendingInverseViewProjection;
				hasCells = true;
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		/// <summary>
		///     The depth, along the view of the camera now at <paramref name="eye" /> looking along <paramref name="forward" />,
		///     of the surface drawn nearest to a point of the view within <paramref name="radius" /> (shares of the view's
		///     height); <paramref name="view" /> runs 0..1 from the top left. The surface is placed with the camera that drew
		///     it, so a camera that moved since (a zoom between two captures) still gets its true depth. False when nothing
		///     within it was drawn.
		/// </summary>
		public bool TryDepthNear(Vector2 view, float radius, Vector3 eye, Vector3 forward, out float depth)
		{
			depth = 0f;
			if (!hasCells)
			{
				return false;
			}

			// Cells are rows of the texture: row 0 is the view's bottom.
			float cx = view.X * GridWidth - 0.5f;
			float cy = (1f - view.Y) * GridHeight - 0.5f;
			float reach = radius * GridHeight;
			int x0 = Math.Max(0, (int)MathF.Floor(cx - reach));
			int x1 = Math.Min(GridWidth - 1, (int)MathF.Ceiling(cx + reach));
			int y0 = Math.Max(0, (int)MathF.Floor(cy - reach));
			int y1 = Math.Min(GridHeight - 1, (int)MathF.Ceiling(cy + reach));
			float best = float.MaxValue;
			for (int y = y0; y <= y1; y++)
			{
				for (int x = x0; x <= x1; x++)
				{
					float d = cells[y * GridWidth + x];
					float distance = (x - cx) * (x - cx) + (y - cy) * (y - cy);
					if (d < 1f && distance <= reach * reach && distance < best && TryDepth(x, y, d, eye, forward, out float linear))
					{
						best = distance;
						depth = linear;
					}
				}
			}

			return best < float.MaxValue;
		}

		/// <summary>The depth of what was drawn at a point of the view (its cell), from the camera now; false for the sky.</summary>
		public bool TryDepthAt(Vector2 view, Vector3 eye, Vector3 forward, out float depth)
		{
			depth = 0f;
			if (!hasCells)
			{
				return false;
			}

			int x = Math.Clamp((int)(view.X * GridWidth), 0, GridWidth - 1);
			int y = Math.Clamp((int)((1f - view.Y) * GridHeight), 0, GridHeight - 1);
			float d = cells[y * GridWidth + x];
			return d < 1f && TryDepth(x, y, d, eye, forward, out depth);
		}

		/// <summary>The median depth of everything drawn, from the camera now (false when the view drew only the sky).</summary>
		public bool TryTypicalDepth(Vector3 eye, Vector3 forward, out float depth)
		{
			depth = 0f;
			if (!hasCells)
			{
				return false;
			}

			depths.Clear();
			for (int y = 0; y < GridHeight; y++)
			{
				for (int x = 0; x < GridWidth; x++)
				{
					float d = cells[y * GridWidth + x];
					if (d < 1f && TryDepth(x, y, d, eye, forward, out float linear))
					{
						depths.Add(linear);
					}
				}
			}

			if (depths.Count == 0)
			{
				return false;
			}

			depths.Sort();
			depth = depths[depths.Count / 2];
			return true;
		}

		public void Dispose()
		{
			if (fence != IntPtr.Zero)
			{
				GL.DeleteSync(fence);
				fence = IntPtr.Zero;
			}

			if (buffer != 0)
			{
				GL.DeleteBuffer(buffer);
				buffer = 0;
			}

			program?.Delete();
			program = null;
			hasCells = false;
		}

		#endregion

		#region PrivateMethods

		// A cell's surface placed in the world with the camera that drew it (the cell's centre unprojected), as a depth along
		// the view of the camera now; false behind it.
		private bool TryDepth(int x, int y, float d, Vector3 eye, Vector3 forward, out float linear)
		{
			var ndc = new Vector4((x + 0.5f) / GridWidth * 2f - 1f, (y + 0.5f) / GridHeight * 2f - 1f, d * 2f - 1f, 1f);
			Vector4 world = ndc * inverseViewProjection;
			linear = 0f;
			if (Math.Abs(world.W) < 1e-12f)
			{
				return false;
			}

			Vector3 point = world.Xyz / world.W;
			linear = Vector3.Dot(point - eye, forward);
			return linear > 0f && float.IsFinite(linear);
		}

		#endregion
	}
}
