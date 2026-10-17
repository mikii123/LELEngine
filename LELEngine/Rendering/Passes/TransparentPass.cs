using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Forward shading of the transparent renderers (materials in a queue above <see cref="MaterialQueue.GeometryLast" />)
	///     over the opaque image and the sky: sorted by queue, then back to front by the distance from the camera to
	///     the object's bounds centre, drawn one by one with the shader's blend, depth and cull state and no depth
	///     writes unless the shader asks (ZWrite On). Double-sided materials (Cull Off) draw their back faces before
	///     their front faces, so a convex object composites its inside behind its outside. The fragments are not in
	///     the depth buffer, so the GI comes from the volume (Lighting.SetVolumeGIUniforms, Engine/VolumeLighting.glsl)
	///     rather than the screen-space resolve. These renderers never go through the GPU-driven tables; their shadows
	///     are drawn by <see cref="TransparentShadows" />.
	/// </summary>
	public sealed class TransparentPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Transparent";

		/// <summary>Transparent objects drawn last frame (stats).</summary>
		public int DrawnCount { get; private set; }

		#endregion

		#region PrivateFields

		private struct Entry
		{
			public MeshRenderer Renderer;
			public VFX.VfxDrawEntry Vfx;
			public int Queue;
			public float DistanceSquared;
			public ulong Order;
		}

		private readonly List<Entry> entries = new List<Entry>();
		private readonly List<VFX.VfxDrawEntry> vfxEntries = new List<VFX.VfxDrawEntry>();

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			IReadOnlyList<MeshRenderer> renderers = context.TransparentRenderers;
			DrawnCount = 0;
			Collect(renderers, context.CameraPosition);
			vfxEntries.Clear();
			VFX.VfxRuntime.CollectTransparent(context, vfxEntries);
			if (entries.Count == 0 && vfxEntries.Count == 0)
			{
				return;
			}

			// Particle outputs sort among the meshes by queue and distance (their bounds centre).
			foreach (VFX.VfxDrawEntry vfx in vfxEntries)
			{
				entries.Add(new Entry { Vfx = vfx, Queue = vfx.Queue, DistanceSquared = vfx.DistanceSquared, Order = (ulong)(vfx.System * 64 + vfx.Output) });
			}

			SortEntries();

			// The view block of the particle draws and the back-to-front sorts of their sorted outputs, in one batch.
			if (vfxEntries.Count > 0)
			{
				context.Renderer.Profiler.Split("Transparent.VfxSort");
				VFX.VfxRuntime.PrepareDraws(context, vfxEntries);
				context.Renderer.Profiler.Split("Transparent.Draw");
			}

			context.SceneTarget.Bind();

			// The scene depth with the opaques for soft particles (and the next simulation's depth collisions).
			VFX.VfxDepthCapture depth = null;
			if (vfxEntries.Count > 0 && VFX.VfxRuntime.NeedsDepth)
			{
				depth = VFX.VfxRuntime.CaptureDepth(context);
				context.SceneTarget.Bind();
			}

			ShaderProgram active = null;
			bool receiveShadows = false;
			bool receiveGI = false;
			bool resend = true;
			bool afterParticles = false;
			bool afterMesh = false;
			foreach (Entry entry in entries)
			{
				if (entry.Vfx != null)
				{
					// Consecutive particle draws skip the state they share; a mesh drawn in between resets that.
					if (afterMesh)
					{
						VFX.VfxRuntime.InvalidateDrawState();
						afterMesh = false;
					}

					entry.Vfx.Instance.Draw(context, entry.Vfx.System, entry.Vfx.Output, false, depth);
					afterParticles = true;
					active = null;
					resend = true;
					DrawnCount++;
					continue;
				}

				if (afterParticles)
				{
					GLState.SetDepth(true, false, DepthFunction.Lequal);
					afterParticles = false;
				}

				afterMesh = true;
				MeshRenderer renderer = entry.Renderer;
				Material material = renderer.Material;
				ShaderProgram program = material.UsingShader;
				if (program != active)
				{
					program.Use();
					active = program;
					resend = true;
				}

				// Camera and lighting uniforms once per program and flag combination, as the opaque pass does.
				if (resend || renderer.ReceiveShadows != receiveShadows || renderer.ReceiveGI != receiveGI)
				{
					context.Camera.SetUniforms(program);
					Lighting.SetUniforms(program, renderer.ReceiveShadows, false);
					Lighting.SetVolumeGIUniforms(program, renderer.ReceiveGI);
					receiveShadows = renderer.ReceiveShadows;
					receiveGI = renderer.ReceiveGI;
					resend = false;
				}

				renderer.transform.SetModelMatrix(program);
				material.SetUniforms();
				if (material.OverridesSharedUniforms())
				{
					resend = true;
				}

				ShaderRenderState state = material.RenderState;
				state.ApplyTransparent();
				if (state.Cull == CullMode.Off)
				{
					// Back faces first, then front faces: the inside of a convex object ends up behind its outside.
					GLState.SetCull(true, TriangleFace.Front);
					renderer.DrawGeometry();
					GLState.SetCull(true, TriangleFace.Back);
				}

				renderer.DrawGeometry();
				DrawnCount++;
			}

			entries.Clear();
			vfxEntries.Clear();
			GLState.SetBlend(false);
			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.Reset();
		}

		public override void ReleaseSceneReferences()
		{
			entries.Clear();
			vfxEntries.Clear();
		}

		#endregion

		#region PrivateMethods

		// Drawable renderers with their sort keys: queue first, then far to near (the centre of the mesh bounds in
		// the world, like Unity's default transparent sorting).
		private void Collect(IReadOnlyList<MeshRenderer> renderers, Vector3 cameraPosition)
		{
			entries.Clear();
			if (renderers == null)
			{
				return;
			}

			foreach (MeshRenderer renderer in renderers)
			{
				Mesh mesh = renderer.Mesh;
				Material material = renderer.Material;
				if (mesh == null || material?.UsingShader == null || !material.UsingShader.IsLinked)
				{
					continue;
				}

				Vector3 center = Vector3.TransformPosition((mesh.BoundsMin + mesh.BoundsMax) * 0.5f, renderer.transform.LocalToWorld);
				entries.Add(new Entry { Renderer = renderer, Queue = material.renderQueue, DistanceSquared = (center - cameraPosition).LengthSquared, Order = renderer.Id });
			}
		}

		private void SortEntries()
		{
			// List.Sort is not stable; the order key settles ties so the order is the same every frame.
			entries.Sort((a, b) =>
			{
				if (a.Queue != b.Queue) return a.Queue.CompareTo(b.Queue);
				int byDistance = b.DistanceSquared.CompareTo(a.DistanceSquared);
				return byDistance != 0 ? byDistance : a.Order.CompareTo(b.Order);
			});
		}

		#endregion
	}
}
