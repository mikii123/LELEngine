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

		// Groups draws by program to minimise program switches.
		private static void PerShader(IReadOnlyList<MeshRenderer> renderers)
		{
			foreach (KeyValuePair<string, ShaderProgram> shader in InternalStorage.Shaders)
			{
				bool used = false;
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

					renderer.Render();
				}
			}
		}

		#endregion
	}
}
