using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.UIElements;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Draws the scene's <see cref="UIDocument" /> panels over the final image (after post-processing), lowest
	///     sort order first. Each panel updates its styles, layout and geometry here, right before it is drawn.
	/// </summary>
	public sealed class UIRenderPass : RenderPass
	{
		#region PublicFields

		public override string Name => "UI";

		public override bool RunsWithoutCamera => true;

		#endregion

		#region PrivateFields

		private readonly List<UIDocument> documents = new List<UIDocument>();

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			if (!context.Settings.RenderUI)
			{
				return;
			}

			UIRuntime.CollectDocuments(context.Behaviours, documents);
			if (documents.Count == 0)
			{
				return;
			}

			context.Renderer.BindOutput();
			long now = UIRuntime.TimeMs;
			foreach (UIDocument document in documents)
			{
				RuntimePanel panel = document.runtimePanel;
				if (panel == null)
				{
					continue;
				}

				try
				{
					document.PreparePanel(context.Width, context.Height);
					panel.Update(now);
					panel.renderer ??= new UIElements.Rendering.UIRenderer();
					panel.renderer.Render(panel, context.Width, context.Height);
				}
				catch (System.Exception e)
				{
					Debug.LogException(e, document);
				}
			}

			GLState.Reset();
		}

		public override void ReleaseSceneReferences()
		{
			documents.Clear();
		}

		#endregion
	}
}
