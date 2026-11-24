using System.Collections.Generic;

namespace LELEngine.Rendering
{
	/// <summary>The renderer's frame graph: the passes' declarations of the frame about to execute (<see cref="FrameGraph" />).</summary>
	public sealed partial class Renderer
	{
		#region PublicFields

		/// <summary>What the passes declared for the frame: its transient textures and the memory they share.</summary>
		public FrameGraph Frame { get; } = new FrameGraph();

		#endregion

		#region PrivateFields

		/// <summary>The contexts of the frame's cameras (same index as the stack), made before the frame executes.</summary>
		private readonly List<RenderContext> contexts = new List<RenderContext>();

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Asks every pass that will run in the frame what it makes and uses, in the order the frame runs them (the frame's
		///     passes with the lighting camera's context, every camera's passes with its own, the final ones), and plans the
		///     frame's transient textures.
		/// </summary>
		private void PlanFrame(int lightingIndex)
		{
			RenderContext lighting = contexts[lightingIndex];
			Frame.BeginSetup();
			foreach (RenderPass pass in passes)
			{
				if (pass.Enabled && pass.Scope == RenderPassScope.Frame)
				{
					Frame.BeginStep(pass, lightingIndex);
					pass.Setup(Frame, lighting);
				}
			}

			for (int i = 0; i < contexts.Count; i++)
			{
				RenderContext context = contexts[i];
				foreach (RenderPass pass in passes)
				{
					if (pass.Enabled && RunsFor(pass, context))
					{
						Frame.BeginStep(pass, i);
						pass.Setup(Frame, context);
					}
				}
			}

			foreach (RenderPass pass in passes)
			{
				if (pass.Enabled && pass.Scope == RenderPassScope.Final)
				{
					Frame.BeginStep(pass, lightingIndex);
					pass.Setup(Frame, lighting);
				}
			}

			Frame.Compile();
		}

		/// <summary>Whether a pass runs among a camera's passes (its scope: every camera's, the lighting camera's, the base camera's).</summary>
		private static bool RunsFor(RenderPass pass, RenderContext context)
		{
			return pass.Scope switch
			{
				RenderPassScope.EveryCamera => true,
				RenderPassScope.LightingCamera => context.IsLightingCamera,
				RenderPassScope.BaseCamera => context.IsBaseCamera,
				_ => false
			};
		}

		#endregion
	}
}
