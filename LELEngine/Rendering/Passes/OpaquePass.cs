using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Forward shading of all mesh renderers into the HDR scene target using their own material shaders.
	/// </summary>
	public sealed class OpaquePass : RenderPass
	{
		#region PublicFields

		public override string Name => "Opaque";

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

			switch (context.RenderQueue)
			{
				case RenderQueue.PerObject:
					PerObject(context.Renderers);
					break;
				default:
					PerShader(context.Renderers);
					break;
			}

			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.Reset();
		}

		#endregion

		#region PrivateMethods

		private static void PerObject(IReadOnlyList<MeshRenderer> renderers)
		{
			foreach (MeshRenderer renderer in renderers)
			{
				if (renderer.UsingShader == null)
				{
					continue;
				}

				renderer.UsingShader.Use();
				renderer.Render();
			}
		}

		// Groups draws by program to minimise program switches. Camera and lighting uniforms are the same for
		// every object drawn with a program, so they are sent once per program and again only when an object's
		// ReceiveShadows / ReceiveGI flags differ from the previous object's or the previous object's material
		// overrode one of them (Material.OverridesSharedUniforms); each draw then only sends its model matrix
		// and material uniforms.
		private static void PerShader(IReadOnlyList<MeshRenderer> renderers)
		{
			foreach (KeyValuePair<string, ShaderProgram> shader in InternalStorage.Shaders)
			{
				bool used = false;
				bool resend = true;
				bool receiveShadows = false;
				bool receiveGI = false;
				foreach (MeshRenderer renderer in renderers)
				{
					if (renderer.UsingShader != shader.Value)
					{
						continue;
					}

					if (!used)
					{
						shader.Value.Use();
						used = true;
					}

					if (resend || renderer.ReceiveShadows != receiveShadows || renderer.ReceiveGI != receiveGI)
					{
						Camera.main.SetUniforms(shader.Value);
						Lighting.SetUniforms(shader.Value, renderer.ReceiveShadows, renderer.ReceiveGI);
						receiveShadows = renderer.ReceiveShadows;
						receiveGI = renderer.ReceiveGI;
						resend = false;
					}

					renderer.RenderObject();
					if (renderer.Material != null && renderer.Material.OverridesSharedUniforms())
					{
						resend = true;
					}
				}
			}
		}

		#endregion
	}
}
