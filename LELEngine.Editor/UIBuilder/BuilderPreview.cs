using System;
using System.Collections.Generic;
using LELEngine.Rendering;
using LELEngine.UIElements;
using LELEngine.UIElements.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Editor.UIBuilder
{
	/// <summary>
	///     The live preview of a document: a runtime panel instantiated from the model's XML, rendered into a texture
	///     by the game's UI renderer. Maps preview elements back to model nodes for picking and selection outlines.
	///     Everything here runs with the builder window's GL context current.
	/// </summary>
	internal sealed class BuilderPreview : IDisposable
	{
		#region PublicFields

		public RuntimePanel panel { get; }
		public int Texture => target?.ColorAttachments[0].Handle ?? 0;
		public int Width => target?.Width ?? 0;
		public int Height => target?.Height ?? 0;
		public List<string> errors { get; } = new List<string>();

		/// <summary>Apply the built-in theme (as a UIDocument with useDefaultTheme does).</summary>
		public bool useDefaultTheme
		{
			get => themeApplied;
			set
			{
				if (themeApplied == value)
				{
					return;
				}

				themeApplied = value;
				if (value) panel.visualTree.styleSheets.Add(DefaultTheme.StyleSheet);
				else panel.visualTree.styleSheets.Remove(DefaultTheme.StyleSheet);
			}
		}

		#endregion

		#region PrivateFields

		private Framebuffer target;
		private VisualElement container;
		private readonly Dictionary<VisualElement, UxmlNode> nodeByElement = new Dictionary<VisualElement, UxmlNode>();
		private readonly Dictionary<int, VisualElement> elementByNodeId = new Dictionary<int, VisualElement>();
		private bool themeApplied;

		#endregion

		#region Constructors

		public BuilderPreview()
		{
			panel = new RuntimePanel { name = "UI Builder" };
			useDefaultTheme = true;
		}

		#endregion

		#region PublicMethods

		/// <summary>Instantiates the document again (after any model change).</summary>
		public void Rebuild(UxmlDocument document)
		{
			errors.Clear();
			nodeByElement.Clear();
			elementByNodeId.Clear();
			container?.RemoveFromHierarchy();
			container = new VisualElement { name = "document", pickingMode = PickingMode.Ignore };
			container.style.position = Position.Absolute;
			container.style.left = 0f;
			container.style.top = 0f;
			container.style.right = 0f;
			container.style.bottom = 0f;
			container.pseudoStates |= PseudoStates.Root;
			panel.visualTree.Add(container);

			try
			{
				VisualTreeAsset asset = VisualTreeAsset.Parse(document.ToXml(), document.assetPath, "builder");
				errors.AddRange(asset.errors);
				asset.CloneTree(container);
				Map(container.Children(), document.root.children);
			}
			catch (Exception e)
			{
				errors.Add(e.Message);
			}
		}

		/// <summary>Renders the panel into the texture at the given size and scale.</summary>
		public void Render(int width, int height, float scale)
		{
			width = Math.Max(16, width);
			height = Math.Max(16, height);
			if (target == null)
			{
				target = new Framebuffer(width, height);
				target.AddColorAttachment(RenderTextureFormat.RGBA8);
				target.Validate();
			}
			else if (target.Width != width || target.Height != height)
			{
				target.Resize(width, height);
			}

			panel.SetTargetSize(width, height, scale);
			panel.Update(UIRuntime.TimeMs);
			target.Bind();
			GL.Disable(EnableCap.ScissorTest);
			GL.ClearColor(0.16f, 0.16f, 0.18f, 1f);
			GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
			panel.renderer ??= new UIRenderer();
			panel.renderer.Render(panel, width, height);
			GLState.Reset();
		}

		/// <summary>The model node under a point (panel units), or null.</summary>
		public UxmlNode NodeAt(Vector2 panelPoint)
		{
			VisualElement hit = panel.Pick(panelPoint);
			for (VisualElement e = hit; e != null; e = e.hierarchy.parent)
			{
				UxmlNode node;
				if (nodeByElement.TryGetValue(e, out node))
				{
					return node;
				}
			}

			return null;
		}

		public VisualElement ElementOf(UxmlNode node)
		{
			VisualElement element;
			return node != null && elementByNodeId.TryGetValue(node.id, out element) ? element : null;
		}

		/// <summary>World bounds (panel units) of the node's element; empty when it has none.</summary>
		public Rect BoundsOf(UxmlNode node)
		{
			VisualElement element = ElementOf(node);
			return element != null ? element.worldBound : Rect.zero;
		}

		public void Dispose()
		{
			panel.Dispose();
			target?.Delete();
			target = null;
		}

		#endregion

		#region PrivateMethods

		private void Map(IEnumerable<VisualElement> elements, List<UxmlNode> nodes)
		{
			int index = 0;
			foreach (VisualElement element in elements)
			{
				if (index >= nodes.Count)
				{
					break;
				}

				UxmlNode node = nodes[index++];
				nodeByElement[element] = node;
				elementByNodeId[node.id] = element;
				if (!node.isInstance)
				{
					Map(element.Children(), node.children);
				}
			}
		}

		#endregion
	}
}
