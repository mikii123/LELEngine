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

		#endregion

		#region PrivateFields

		private readonly List<RendererPart> parts = new List<RendererPart>();

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
				PerShader(gpu.PerObjectParts, context.Camera);
			}
			else
			{
				RendererPart.CollectOpaque(context.Renderers, parts);
				PerShader(parts, context.Camera);
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
			gpu.EnsureCameraCulled(context.ViewProjection);
			IReadOnlyList<OpaqueBucket> buckets = gpu.Buckets;
			for (int i = 0; i < buckets.Count; i++)
			{
				OpaqueBucket bucket = buckets[i];
				ShaderProgram program = bucket.Program;
				program.Use();
				program.RenderState.ApplyCull();
				context.Camera.SetUniforms(program);
				Lighting.SetUniforms(program, bucket.ReceiveShadows, bucket.ReceiveGI);
				// A material with its own uniforms (textures, custom values) sets them; standard ones come from the table.
				bucket.Material?.SetUniforms(program);
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
		// model matrix and material uniforms.
		private static void PerShader(IReadOnlyList<RendererPart> parts, Camera camera)
		{
			foreach (ShaderProgram program in InternalStorage.ShaderList)
			{
				bool used = false;
				bool resend = true;
				bool receiveShadows = false;
				bool receiveGI = false;
				foreach (RendererPart part in parts)
				{
					Material material = part.Material;
					if (material?.UsingShader != program)
					{
						continue;
					}

					MeshRenderer renderer = part.Renderer;

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
		}

		#endregion
	}
}
