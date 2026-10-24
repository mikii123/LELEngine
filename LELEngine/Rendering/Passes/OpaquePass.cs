using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Forward shading of the opaque mesh renderers into the HDR scene target using their own material shaders.
	///     GPU-driven: one indirect call per opaque bucket (instanced shader variant + shared uniform state) with the
	///     camera culling of the GPU scene; renderers whose shader has no instanced variant draw one by one. Of the
	///     shader's render state only the cull mode applies here (transparent materials have their own pass).
	/// </summary>
	public sealed class OpaquePass : RenderPass
	{
		#region PublicFields

		public override string Name => "Opaque";

		public override RenderPassScope Scope => RenderPassScope.EveryCamera;

		#endregion

		#region PrivateFields

		private readonly List<RendererPart> parts = new List<RendererPart>();
		private static readonly List<ShaderProgram> programs = new List<ShaderProgram>();
		private static readonly List<ShaderProgram> partPrograms = new List<ShaderProgram>();

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			context.SceneTarget.Bind();
			GLState.SetCull(true, TriangleFace.Back);

			if (context.DepthPrepassDone)
			{
				// Depth already resolved: shade only the visible surface, no depth writes needed.
				GLState.SetDepth(true, false, DepthFunction.Lequal);
			}
			else
			{
				GLState.SetDepth(true, true, DepthFunction.Less);
			}

			GpuScene gpu = context.Renderer.GpuScene;
			if (context.RenderQueue == RenderQueue.PerObject)
			{
				PerObject(context.Renderers);
			}
			else if (gpu.Active)
			{
				GpuDriven(gpu, context);
				// The tables hold every camera's renderers: this camera draws those on its layers.
				PerShader(gpu.PerObjectParts, context.Camera, context.CullingMask);
			}
			else
			{
				RendererPart.CollectOpaque(context.Renderers, parts);
				PerShader(parts, context.Camera, -1);
				parts.Clear();
			}

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.Reset();
		}

		#endregion

		#region PrivateMethods

		private static void GpuDriven(GpuScene gpu, RenderContext context)
		{
			gpu.EnsureCameraCulled(context.ViewProjection, context.CullingMask);
			IReadOnlyList<OpaqueBucket> buckets = gpu.Buckets;
			for (int i = 0; i < buckets.Count; i++)
			{
				OpaqueBucket bucket = buckets[i];
				ShaderProgram program = bucket.Program;
				program.Use();
				program.RenderState.ApplyCull();
				context.Camera.SetUniforms(program);
				Lighting.SetUniforms(program, bucket.ReceiveShadows, bucket.ReceiveGI);
				// A material with its own uniforms (textures, custom values) sets them; standard ones come from the table
				// (their declared textures get the neutral defaults).
				if (bucket.Material != null)
				{
					bucket.Material.SetUniforms(program);
				}
				else
				{
					Material.SetDefaultTextures(program);
				}
				gpu.DrawCameraBucket(i);
			}
		}

		private static void PerObject(IReadOnlyList<MeshRenderer> renderers)
		{
			foreach (MeshRenderer renderer in renderers)
			{
				// Activates the material shader of every opaque sub-mesh itself.
				renderer.Render();
			}
		}

		// Groups draws by program to minimise program switches. Camera and lighting uniforms are the same for
		// every object drawn with a program, so they are sent once per program and again only when an object's
		// ReceiveShadows / ReceiveGI flags differ from the previous object's or the previous object's material
		// overrode one of them (Material.OverridesSharedUniforms); each draw (one sub-mesh) then only sends its
		// model matrix and material uniforms. The programs are the materials' variants (Material.Program, looked up once
		// per part), in the order the parts first use them.
		private static void PerShader(IReadOnlyList<RendererPart> parts, Camera camera, int layerMask)
		{
			programs.Clear();
			partPrograms.Clear();
			foreach (RendererPart part in parts)
			{
				ShaderProgram candidate = part.Material?.Program;
				partPrograms.Add(candidate);
				if (candidate != null && !programs.Contains(candidate))
				{
					programs.Add(candidate);
				}
			}

			foreach (ShaderProgram program in programs)
			{
				bool used = false;
				bool resend = true;
				bool receiveShadows = false;
				bool receiveGI = false;
				for (int i = 0; i < parts.Count; i++)
				{
					if (partPrograms[i] != program)
					{
						continue;
					}

					RendererPart part = parts[i];
					Material material = part.Material;
					MeshRenderer renderer = part.Renderer;
					if ((layerMask & (1 << (renderer.gameObject.layer & 31))) == 0)
					{
						continue;
					}

					if (!used)
					{
						program.Use();
						program.RenderState.ApplyCull();
						used = true;
					}

					if (resend || renderer.ReceiveShadows != receiveShadows || renderer.ReceiveGI != receiveGI)
					{
						camera.SetUniforms(program);
						Lighting.SetUniforms(program, renderer.ReceiveShadows, renderer.ReceiveGI);
						receiveShadows = renderer.ReceiveShadows;
						receiveGI = renderer.ReceiveGI;
						resend = false;
					}

					renderer.RenderSubMesh(part.SubMesh);
					if (material.OverridesSharedUniforms())
					{
						resend = true;
					}
				}
			}

			programs.Clear();
			partPrograms.Clear();
		}

		#endregion
	}
}
