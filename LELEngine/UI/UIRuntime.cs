using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Game-side glue of the UI: the list of enabled documents, the frame clock, and the bridge from the window's
	///     raw input (<see cref="Input" />) to the panels' events. Input runs once per frame from the scene update;
	///     the topmost panel under the pointer receives pointer events, the panel holding the focused element the
	///     keyboard.
	/// </summary>
	public static class UIRuntime
	{
		#region PublicFields

		/// <summary>Milliseconds since the engine started (transitions and scheduled callbacks run on this clock).</summary>
		public static long TimeMs => clock.ElapsedMilliseconds;

		/// <summary>DPI of the display the game runs on (ConstantPhysicalSize scaling); 96 when unknown.</summary>
		public static float DisplayDpi { get; set; } = 96f;

		/// <summary>The pointer is over a UI element (game code can ignore clicks the UI takes).</summary>
		public static bool IsPointerOverUI { get; private set; }

		/// <summary>Cursor the hovered element asks for; hosts apply it to the window.</summary>
		public static CursorType DesiredCursor { get; private set; } = CursorType.Arrow;

		/// <summary>Enabled documents (any scene).</summary>
		public static IReadOnlyList<UIDocument> Documents => documents;

		/// <summary>A document's panel runs transitions or scheduled callbacks (editor views rendering on demand keep rendering).</summary>
		public static bool AnyPanelAnimating
		{
			get
			{
				for (int i = 0; i < documents.Count; i++)
				{
					if (documents[i].runtimePanel != null && documents[i].runtimePanel.needsContinuousUpdate)
					{
						return true;
					}
				}

				return false;
			}
		}

		/// <summary>Clipboard access for text fields; hosts assign the window's clipboard.</summary>
		public static Func<string> GetClipboard { get; set; } = () => "";

		public static Action<string> SetClipboard { get; set; } = text => { };

		#endregion

		#region PrivateFields

		private static readonly Stopwatch clock = Stopwatch.StartNew();
		private static readonly List<UIDocument> documents = new List<UIDocument>();
		private static readonly List<UIDocument> frameDocuments = new List<UIDocument>();
		private static Vector2 lastMousePosition = new Vector2(float.NaN, float.NaN);
		private static bool subscribed;

		#endregion

		#region PublicMethods

		/// <summary>Top-level documents of these behaviours, lowest sorting order first (draw order).</summary>
		public static void CollectDocuments(IReadOnlyList<Behaviour> behaviours, List<UIDocument> result)
		{
			result.Clear();
			if (behaviours == null)
			{
				return;
			}

			for (int i = 0; i < behaviours.Count; i++)
			{
				if (behaviours[i] is UIDocument document && document.runtimePanel != null && document.rootVisualElement != null && document.isActiveAndEnabled)
				{
					result.Add(document);
				}
			}

			if (result.Count > 1)
			{
				// Stable sort: equal orders keep their scene order.
				var indexed = new List<KeyValuePair<int, UIDocument>>(result.Count);
				for (int i = 0; i < result.Count; i++) indexed.Add(new KeyValuePair<int, UIDocument>(i, result[i]));
				indexed.Sort((a, b) =>
				{
					int order = a.Value.sortingOrder.CompareTo(b.Value.sortingOrder);
					return order != 0 ? order : a.Key.CompareTo(b.Key);
				});

				result.Clear();
				foreach (KeyValuePair<int, UIDocument> pair in indexed) result.Add(pair.Value);
			}
		}

		/// <summary>
		///     Dispatches the frame's raw input to the scene's panels. Called by <see cref="Scene.Update" /> before the
		///     components update, so UI callbacks run before game logic reads the input.
		/// </summary>
		public static void ProcessInput(Scene scene)
		{
			List<Input.RawInputEvent> events = Input.UIEvents;
			CollectDocuments(scene?.ActiveBehaviours, frameDocuments);
			if (frameDocuments.Count == 0)
			{
				events.Clear();
				IsPointerOverUI = false;
				DesiredCursor = CursorType.Arrow;
				return;
			}

			// Topmost first for picking.
			frameDocuments.Reverse();

			Vector2 mouse = Input.mousePosition;
			EventModifiers modifiers = CurrentModifiers();
			int pressed = PressedButtons();

			bool moved = float.IsNaN(lastMousePosition.X) || mouse != lastMousePosition;
			lastMousePosition = mouse;
			if (moved)
			{
				DispatchPointerMove(mouse, pressed, modifiers);
			}

			for (int i = 0; i < events.Count; i++)
			{
				Input.RawInputEvent e = events[i];
				switch (e.kind)
				{
					case Input.RawInputKind.MouseDown:
					{
						RuntimePanel panel = PanelForPointer(e.position);
						panel?.ProcessPointerDown(e.position / panel.scale, MapButton(e.button), pressed | (1 << MapButton(e.button)), Convert(e.modifiers));
						break;
					}

					case Input.RawInputKind.MouseUp:
					{
						RuntimePanel panel = PanelForPointer(e.position);
						panel?.ProcessPointerUp(e.position / panel.scale, MapButton(e.button), pressed & ~(1 << MapButton(e.button)), Convert(e.modifiers));
						break;
					}

					case Input.RawInputKind.MouseWheel:
					{
						RuntimePanel panel = PanelForPointer(mouse);
						panel?.ProcessWheel(mouse / panel.scale, e.delta, modifiers);
						break;
					}

					case Input.RawInputKind.KeyDown:
					{
						RuntimePanel panel = PanelForKeyboard();
						panel?.ProcessKeyDown(e.key, Convert(e.modifiers));
						break;
					}

					case Input.RawInputKind.KeyUp:
					{
						RuntimePanel panel = PanelForKeyboard();
						panel?.ProcessKeyUp(e.key, Convert(e.modifiers));
						break;
					}

					case Input.RawInputKind.TextInput:
					{
						RuntimePanel panel = PanelForKeyboard();
						panel?.ProcessTextInput(e.character, modifiers);
						break;
					}
				}
			}

			events.Clear();

			RuntimePanel hovered = PanelForPointer(mouse);
			IsPointerOverUI = hovered != null;
			DesiredCursor = hovered?.cursor ?? CursorType.Arrow;
		}

		/// <summary>Maps the UI cursor to a window cursor (hosts call it with the panel's desired cursor).</summary>
		public static MouseCursor ToWindowCursor(CursorType cursor)
		{
			switch (cursor)
			{
				case CursorType.Text: return MouseCursor.IBeam;
				case CursorType.ResizeHorizontal:
				case CursorType.SplitResizeLeftRight: return MouseCursor.HResize;
				case CursorType.ResizeVertical:
				case CursorType.SplitResizeUpDown: return MouseCursor.VResize;
				case CursorType.Link: return MouseCursor.Hand;
				case CursorType.MoveArrow:
				case CursorType.Pan: return MouseCursor.Crosshair;
				default: return MouseCursor.Default;
			}
		}

		#endregion

		#region InternalMethods

		internal static void Register(UIDocument document)
		{
			if (!documents.Contains(document))
			{
				documents.Add(document);
			}

			if (!subscribed)
			{
				subscribed = true;
				UIAssets.AssetReloaded += OnAssetReloaded;
			}
		}

		internal static void Unregister(UIDocument document)
		{
			documents.Remove(document);
		}

		#endregion

		#region PrivateMethods

		private static void OnAssetReloaded(object asset)
		{
			foreach (UIDocument document in documents.ToArray())
			{
				if (document.runtimePanel != null)
				{
					document.runtimePanel.InvalidateStyles();
				}

				if (asset is VisualTreeAsset tree && ReferenceEquals(document.visualTreeAsset, tree))
				{
					document.RebuildTree();
				}
			}
		}

		private static void DispatchPointerMove(Vector2 mouse, int pressed, EventModifiers modifiers)
		{
			RuntimePanel target = PanelForPointer(mouse);
			for (int i = 0; i < frameDocuments.Count; i++)
			{
				RuntimePanel panel = frameDocuments[i].runtimePanel;
				if (panel == null)
				{
					continue;
				}

				if (panel == target || panel.pointerCapture.IsCaptured(PointerId.mousePointerId))
				{
					panel.ProcessPointerMove(mouse / panel.scale, pressed, modifiers);
				}
				else
				{
					panel.ProcessPointerLeave(modifiers);
				}
			}
		}

		/// <summary>The panel that should get pointer events: a capturing panel, else the topmost one with an element under the point.</summary>
		private static RuntimePanel PanelForPointer(Vector2 pixelPosition)
		{
			for (int i = 0; i < frameDocuments.Count; i++)
			{
				RuntimePanel panel = frameDocuments[i].runtimePanel;
				if (panel != null && panel.pointerCapture.IsCaptured(PointerId.mousePointerId))
				{
					return panel;
				}
			}

			for (int i = 0; i < frameDocuments.Count; i++)
			{
				RuntimePanel panel = frameDocuments[i].runtimePanel;
				if (panel != null && panel.HitTest(pixelPosition / panel.scale))
				{
					return panel;
				}
			}

			return null;
		}

		private static RuntimePanel PanelForKeyboard()
		{
			for (int i = 0; i < frameDocuments.Count; i++)
			{
				RuntimePanel panel = frameDocuments[i].runtimePanel;
				if (panel != null && panel.focusController.focusedElement != null)
				{
					return panel;
				}
			}

			return frameDocuments.Count > 0 ? frameDocuments[0].runtimePanel : null;
		}

		private static int MapButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton button)
		{
			switch (button)
			{
				case OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right: return 1;
				case OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Middle: return 2;
				case OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left: return 0;
				default: return 0;
			}
		}

		private static int PressedButtons()
		{
			int buttons = 0;
			if (Input.GetMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Left)) buttons |= 1;
			if (Input.GetMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Right)) buttons |= 2;
			if (Input.GetMouseButton(OpenTK.Windowing.GraphicsLibraryFramework.MouseButton.Middle)) buttons |= 4;
			return buttons;
		}

		private static EventModifiers CurrentModifiers()
		{
			EventModifiers modifiers = EventModifiers.None;
			if (Input.GetKey(Keys.LeftShift) || Input.GetKey(Keys.RightShift)) modifiers |= EventModifiers.Shift;
			if (Input.GetKey(Keys.LeftControl) || Input.GetKey(Keys.RightControl)) modifiers |= EventModifiers.Control;
			if (Input.GetKey(Keys.LeftAlt) || Input.GetKey(Keys.RightAlt)) modifiers |= EventModifiers.Alt;
			if (Input.GetKey(Keys.LeftSuper) || Input.GetKey(Keys.RightSuper)) modifiers |= EventModifiers.Command;
			return modifiers;
		}

		private static EventModifiers Convert(KeyModifiers modifiers)
		{
			EventModifiers result = EventModifiers.None;
			if ((modifiers & KeyModifiers.Shift) != 0) result |= EventModifiers.Shift;
			if ((modifiers & KeyModifiers.Control) != 0) result |= EventModifiers.Control;
			if ((modifiers & KeyModifiers.Alt) != 0) result |= EventModifiers.Alt;
			if ((modifiers & KeyModifiers.Super) != 0) result |= EventModifiers.Command;
			if ((modifiers & KeyModifiers.CapsLock) != 0) result |= EventModifiers.CapsLock;
			if ((modifiers & KeyModifiers.NumLock) != 0) result |= EventModifiers.Numeric;
			return result;
		}

		#endregion
	}
}
