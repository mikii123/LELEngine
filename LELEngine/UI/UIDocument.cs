using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine.UIElements
{
	public enum PanelScaleMode
	{
		/// <summary>One panel unit is <see cref="PanelSettings.scale" /> pixels.</summary>
		ConstantPixelSize,

		/// <summary>Panel units scale with the display's DPI (<see cref="PanelSettings.referenceDpi" /> = 1:1).</summary>
		ConstantPhysicalSize,

		/// <summary>The panel is designed for <see cref="PanelSettings.referenceResolution" /> and scales with the screen.</summary>
		ScaleWithScreenSize
	}

	public enum PanelScreenMatchMode
	{
		/// <summary>Blend between matching the width (match 0) and the height (match 1).</summary>
		MatchWidthOrHeight,

		/// <summary>The reference resolution always fits inside the screen.</summary>
		Expand,

		/// <summary>The reference resolution always covers the screen.</summary>
		Shrink
	}

	/// <summary>How a document's panel maps to the screen and which theme it starts from.</summary>
	[Serializable]
	public sealed class PanelSettings
	{
		#region PublicFields

		public PanelScaleMode scaleMode = PanelScaleMode.ConstantPixelSize;

		[Tooltip("Pixels per panel unit (ConstantPixelSize).")]
		public float scale = 1f;

		public float referenceDpi = 96f;
		public float fallbackDpi = 96f;

		[Tooltip("Resolution the UI was designed for (ScaleWithScreenSize).")]
		public Vector2i referenceResolution = new Vector2i(1920, 1080);

		public PanelScreenMatchMode screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;

		[Range(0f, 1f)] public float match;

		[Tooltip("Style sheet applied to the whole panel before the documents' own sheets.")]
		public StyleSheet themeStyleSheet;

		[Tooltip("Start from the built-in theme (control styles); off for a fully custom look.")]
		public bool useDefaultTheme = true;

		#endregion

		#region PublicMethods

		/// <summary>Panel units to pixels factor for a target of the given size.</summary>
		public float ComputeScale(int targetWidth, int targetHeight, float dpi)
		{
			switch (scaleMode)
			{
				case PanelScaleMode.ConstantPhysicalSize:
				{
					float effective = dpi > 0f ? dpi : fallbackDpi;
					return Math.Max(0.01f, effective / Math.Max(1f, referenceDpi));
				}

				case PanelScaleMode.ScaleWithScreenSize:
				{
					float refW = Math.Max(1, referenceResolution.X);
					float refH = Math.Max(1, referenceResolution.Y);
					float scaleX = targetWidth / refW;
					float scaleY = targetHeight / refH;
					switch (screenMatchMode)
					{
						case PanelScreenMatchMode.Expand: return Math.Max(0.01f, Math.Min(scaleX, scaleY));
						case PanelScreenMatchMode.Shrink: return Math.Max(0.01f, Math.Max(scaleX, scaleY));
						default:
						{
							float t = Math.Clamp(match, 0f, 1f);
							return Math.Max(0.01f, (float)Math.Pow(2.0, Lerp(Math.Log2(scaleX), Math.Log2(scaleY), t)));
						}
					}
				}

				default:
					return Math.Max(0.01f, scale);
			}
		}

		private static double Lerp(double a, double b, double t)
		{
			return a + (b - a) * t;
		}

		#endregion
	}

	/// <summary>
	///     Shows a UI (a UXML asset, or elements built in code) in the game. One component is one document; its
	///     <see cref="rootVisualElement" /> is where code adds elements. Documents on child GameObjects of another
	///     document nest inside it (one panel for the whole family); independent documents each own a panel, drawn in
	///     <see cref="sortingOrder" /> order over the final image. Works in edit mode so the editor's game view shows
	///     the UI.
	/// </summary>
	[ExecuteAlways]
	[InspectorColor(InspectorColorKind.UI)]
	public sealed class UIDocument : Behaviour
	{
		#region PublicFields

		/// <summary>UXML instantiated into the root when the document is enabled (null: code builds the UI).</summary>
		public VisualTreeAsset visualTreeAsset
		{
			get => m_VisualTreeAsset;
			set
			{
				if (ReferenceEquals(m_VisualTreeAsset, value))
				{
					return;
				}

				m_VisualTreeAsset = value;
				if (rootVisualElement != null)
				{
					RebuildTree();
				}
			}
		}

		public PanelSettings panelSettings = new PanelSettings();

		/// <summary>Documents with a higher order draw on top (and receive input first).</summary>
		public float sortingOrder;

		/// <summary>The document's root element: add your elements here. Created when the document is enabled.</summary>
		public VisualElement rootVisualElement { get; private set; }

		/// <summary>The panel the document is drawn in (its own, or the parent document's).</summary>
		public IPanel panel => runtimePanel ?? parentUI?.panel;

		/// <summary>The document this one nests inside (a UIDocument on a parent GameObject), or null.</summary>
		public UIDocument parentUI { get; private set; }

		/// <summary>Raised after the tree was (re)built from the asset: hook up callbacks here.</summary>
		public event Action<UIDocument> treeRebuilt;

		#endregion

		#region InternalFields

		internal RuntimePanel runtimePanel;

		#endregion

		#region PrivateFields

		[SerializeField] private VisualTreeAsset m_VisualTreeAsset;
		private VisualTreeAsset builtFrom;
		private StyleSheet appliedTheme;
		private bool appliedDefaultTheme;
		private int builtVersion;
		private readonly List<UIDocument> children = new List<UIDocument>();

		#endregion

		#region PublicMethods

		public override void OnEnable()
		{
			UIRuntime.Register(this);
			Attach();
		}

		public override void OnDisable()
		{
			Detach();
			UIRuntime.Unregister(this);
		}

		public override void OnValidate()
		{
			if (rootVisualElement == null)
			{
				return;
			}

			ApplyTheme();
			if (!ReferenceEquals(builtFrom, m_VisualTreeAsset))
			{
				RebuildTree();
			}
		}

		/// <summary>Re-instantiates the UXML asset (after it changed on disk). Code-built elements are removed too.</summary>
		public void RebuildTree()
		{
			if (rootVisualElement == null)
			{
				return;
			}

			rootVisualElement.Clear();
			rootVisualElement.styleSheets.Clear();
			ApplyTheme();
			if (m_VisualTreeAsset != null)
			{
				m_VisualTreeAsset.CloneTree(rootVisualElement);
				builtVersion = m_VisualTreeAsset.version;
			}

			builtFrom = m_VisualTreeAsset;
			// Nested documents keep their place.
			foreach (UIDocument child in children)
			{
				if (child.rootVisualElement != null)
				{
					rootVisualElement.Add(child.rootVisualElement);
				}
			}

			SortNested();
			treeRebuilt?.Invoke(this);
		}

		/// <summary>True when the pointer (panel coordinates in pixels) is over one of this document's elements.</summary>
		public bool IsPointerOver(Vector2 pixelPosition)
		{
			RuntimePanel p = runtimePanel ?? (parentUI?.runtimePanel);
			if (p == null)
			{
				return false;
			}

			VisualElement hit = p.Pick(pixelPosition / p.scale);
			return hit != null && hit != p.visualTree && rootVisualElement != null && (hit == rootVisualElement || rootVisualElement.Contains(hit));
		}

		#endregion

		#region InternalMethods

		/// <summary>Sizes and scales the panel for the target (called by the render pass before drawing).</summary>
		internal void PreparePanel(int targetWidth, int targetHeight)
		{
			if (runtimePanel == null)
			{
				return;
			}

			float scale = panelSettings.ComputeScale(targetWidth, targetHeight, UIRuntime.DisplayDpi);
			runtimePanel.SetTargetSize(targetWidth, targetHeight, scale);
			if (m_VisualTreeAsset != null && builtVersion != m_VisualTreeAsset.version)
			{
				RebuildTree();
			}
		}

		/// <summary>A document that may be this one's parent was enabled: nest under it when it is an ancestor.</summary>
		internal void OnPotentialParentEnabled(UIDocument candidate)
		{
			if (candidate == this || rootVisualElement == null || !IsAncestor(candidate))
			{
				return;
			}

			Detach();
			Attach();
		}

		protected internal override void ReleaseResources()
		{
			Detach();
			UIRuntime.Unregister(this);
		}

		#endregion

		#region PrivateMethods

		private void Attach()
		{
			parentUI = FindParentDocument();
			rootVisualElement ??= new VisualElement { name = gameObject?.Name ?? "UIDocument", pickingMode = PickingMode.Ignore };
			// :root in a document's style sheets matches the document root (its variables apply to the whole document).
			rootVisualElement.pseudoStates |= PseudoStates.Root;
			rootVisualElement.style.position = Position.Absolute;
			rootVisualElement.style.left = 0f;
			rootVisualElement.style.top = 0f;
			rootVisualElement.style.right = 0f;
			rootVisualElement.style.bottom = 0f;
			rootVisualElement.userData = this;

			if (parentUI != null && parentUI.rootVisualElement != null)
			{
				runtimePanel?.Dispose();
				runtimePanel = null;
				parentUI.children.Add(this);
				parentUI.rootVisualElement.Add(rootVisualElement);
				parentUI.SortNested();
			}
			else
			{
				parentUI = null;
				runtimePanel ??= new RuntimePanel { name = gameObject?.Name ?? "UIDocument" };
				runtimePanel.visualTree.Add(rootVisualElement);
			}

			RebuildTree();

			// Documents below this one in the GameObject hierarchy move under this root.
			foreach (UIDocument other in new List<UIDocument>(UIRuntime.Documents))
			{
				other.OnPotentialParentEnabled(this);
			}
		}

		private void Detach()
		{
			foreach (UIDocument child in children.ToArray())
			{
				// Children fall back to their own panels while this document is disabled.
				child.Detach();
				child.Attach();
			}

			children.Clear();
			if (rootVisualElement != null)
			{
				rootVisualElement.RemoveFromHierarchy();
			}

			if (parentUI != null)
			{
				parentUI.children.Remove(this);
				parentUI = null;
			}

			runtimePanel?.Dispose();
			runtimePanel = null;
		}

		private void ApplyTheme()
		{
			VisualElement target = runtimePanel?.visualTree;
			if (target == null || rootVisualElement == null)
			{
				return;
			}

			if (appliedDefaultTheme != panelSettings.useDefaultTheme)
			{
				if (panelSettings.useDefaultTheme) target.styleSheets.Add(DefaultTheme.StyleSheet);
				else target.styleSheets.Remove(DefaultTheme.StyleSheet);
				appliedDefaultTheme = panelSettings.useDefaultTheme;
			}
			else if (panelSettings.useDefaultTheme && !target.styleSheets.Contains(DefaultTheme.StyleSheet))
			{
				target.styleSheets.Add(DefaultTheme.StyleSheet);
				appliedDefaultTheme = true;
			}

			if (!ReferenceEquals(appliedTheme, panelSettings.themeStyleSheet))
			{
				if (appliedTheme != null) target.styleSheets.Remove(appliedTheme);
				if (panelSettings.themeStyleSheet != null) target.styleSheets.Add(panelSettings.themeStyleSheet);
				appliedTheme = panelSettings.themeStyleSheet;
			}
		}

		private void SortNested()
		{
			if (children.Count < 2 || rootVisualElement == null)
			{
				return;
			}

			children.Sort((a, b) => a.sortingOrder.CompareTo(b.sortingOrder));
			foreach (UIDocument child in children)
			{
				child.rootVisualElement?.BringToFront();
			}
		}

		private UIDocument FindParentDocument()
		{
			for (Transform t = transform?.parent; t != null; t = t.parent)
			{
				UIDocument document = t.gameObject.GetComponent<UIDocument>();
				if (document != null && document.isActiveAndEnabled && document.rootVisualElement != null)
				{
					return document;
				}
			}

			return null;
		}

		private bool IsAncestor(UIDocument candidate)
		{
			for (Transform t = transform?.parent; t != null; t = t.parent)
			{
				if (t.gameObject == candidate.gameObject)
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
