using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Desktop;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Images of single editor windows (<see cref="IEditorPanel.GetScreenshot" />). A capture is exclusive: only the
	///     window asked for is drawn, never another window lying over it.
	///     <para>
	///         Last frame: a window shown at the size asked is drawn again from the draw lists of the editor's last frame
	///         (valid until the next one), but only its own: the window, its child windows, the popups, menus and tooltips it
	///         opened, and its dock tab bar (<see cref="FrameCapture" />). Hover marks and open menus of that window are in it.
	///     </para>
	///     <para>
	///         Layout: any other size, a window that is not shown (a tab behind another, closed), or no UI at all (batch
	///         mode). The window is drawn again in an ImGui context of its own that shares the owner's fonts and copies its
	///         style and layout settings (tables), docked alone in a dock space of the size asked (a whole context, the main
	///         window or an OS window: its own docking), for <see cref="LayoutFrames" /> frames: after the first, the owner's
	///         state of the window and its child windows (tree nodes and headers open, scroll) is copied over, so the window
	///         shows what it shows on screen, laid out for the new size. The views (scene, game) render their image at the
	///         new size (<see cref="ViewTexture" />). Nothing is drawn in between: in batch mode a capture is the only time a
	///         window is drawn at all. The frames get no input (no hover, no clicks, no popups), and code drawing a window
	///         can tell a capture by <see cref="Active" /> (the views skip their camera controls and gizmo then).
	///     </para>
	/// </summary>
	internal static unsafe class WindowCapture
	{
		#region PublicFields

		/// <summary>Frames a layout capture draws (state copied after the first two: child windows appear in the second).</summary>
		public const int LayoutFrames = 4;

		/// <summary>Frames a view renders for a capture at a size it has not rendered at (the GI and temporal effects settle).</summary>
		public const int DefaultViewFrames = 20;

		/// <summary>
		///     UI frames of the editor's windows being built (main window, OS windows): a capture cannot run inside one (the
		///     windows' drawing code is not reentrant).
		/// </summary>
		public static int UIFrameDepth;

		/// <summary>A layout capture is drawing (windows skip input handling and other side effects).</summary>
		public static bool Active => session != null;

		/// <summary>
		///     A capture of a whole context starts from settings without docking (the owner never saved a layout, e.g. batch
		///     mode without the user's layout file): its first frame builds the default one.
		/// </summary>
		public static bool LayoutMissing => session != null && !session.HasDocking && session.Frame == 0;

		#endregion

		#region PrivateFields

		private sealed class Session
		{
			public bool HasUI;
			public bool HasDocking;
			public int Frame;
			public int ViewFrames;
			public readonly Dictionary<object, int> Views = new Dictionary<object, int>();
		}

		private static Session session;

		/// <summary>One renderer per GL context (vertex arrays are not shared).</summary>
		private static readonly Dictionary<NativeWindow, ImGuiRenderer> renderers = new Dictionary<NativeWindow, ImGuiRenderer>();

		/// <summary>Renderers of the views for sizes their panel does not show (kept: their allocations are reused).</summary>
		private static readonly Dictionary<object, ViewRenderer> viewRenderers = new Dictionary<object, ViewRenderer>();

		#endregion

		#region PublicMethods

		/// <summary>
		///     The window's image. Width / height 0: its size on screen (or <see cref="IEditorPanel.DefaultCaptureSize" />).
		///     <paramref name="layout" />: lay it out again even at its size on screen (no popups, no hover).
		/// </summary>
		public static EditorScreenshot Capture(IEditorPanel panel, int width, int height, CaptureOptions options)
		{
			options ??= new CaptureOptions();
			EditorApplication editor = EditorApplication.Instance ?? throw new InvalidOperationException("The editor is not running.");
			if (UIFrameDepth > 0 || session != null)
			{
				throw new InvalidOperationException("A window capture runs between frames: call it from Update, a menu item, an automation script or command, not while the UI draws (OnGUI).");
			}

			ImGuiController owner = panel.Owner;
			if (owner == null)
			{
				throw new InvalidOperationException(panel.PanelName + " has no window now (open it first).");
			}

			bool onScreen = TryGetScreenRect(panel, editor, out NVector2 min, out NVector2 size);
			NVector2 fallback = panel.DefaultCaptureSize;
			int w = width > 0 ? width : (int)(onScreen ? size.X : fallback.X);
			int h = height > 0 ? height : (int)(onScreen ? size.Y : fallback.Y);
			if (w < 16 || h < 16 || w > 16384 || h > 16384)
			{
				throw new ArgumentException($"Capture size {w}x{h} is out of range (16..16384).");
			}

			if (!options.Layout && onScreen && editor.HasUI && w == (int)size.X && h == (int)size.Y)
			{
				EditorScreenshot own = FrameCapture(panel, editor, owner, min, w, h);
				if (own != null)
				{
					return own;
				}
			}

			return LayoutCapture(panel, editor, owner, w, h, options);
		}

		/// <summary>
		///     Where the window is in its OS window's client area and its size; false when it is not shown (closed, a tab
		///     behind another, its OS window minimized, or no UI at all).
		/// </summary>
		public static bool TryGetScreenRect(IEditorPanel panel, EditorApplication editor, out NVector2 min, out NVector2 size)
		{
			min = default;
			size = default;
			ImGuiController owner = panel.Owner;
			if (owner == null || !editor.HasUI || !owner.HasDrawn || !panel.PanelOpen)
			{
				return false;
			}

			NativeWindow window = owner.Window;
			if (!window.IsVisible || window.WindowState == OpenTK.Windowing.Common.WindowState.Minimized || window.ClientSize.X <= 0 || window.ClientSize.Y <= 0)
			{
				return false;
			}

			if (panel.IsWholeContext)
			{
				size = new NVector2(window.ClientSize.X, window.ClientSize.Y);
				return true;
			}

			ImGuiWindowPtr found = FindWindow(owner.Context, panel.ImGuiName);
			if (found.IsNull || !found.Active || found.Hidden || found.Collapsed || (found.DockIsActive && !found.DockTabIsVisible))
			{
				return false;
			}

			min = found.Pos;
			size = found.Size;
			return true;
		}

		/// <summary>
		///     Scrolls a window by <paramref name="pixels" /> (infinities: to the top / bottom): the window itself or the child
		///     area of it with the most to scroll (lists live in child windows). Applied at its next frame.
		/// </summary>
		public static void ScrollWindow(ImGuiContextPtr context, string imguiName, float pixels)
		{
			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGui.SetCurrentContext(context);
			try
			{
				ImGuiWindowPtr target = ImGuiP.FindWindowByName(imguiName);
				if (target.IsNull)
				{
					throw new InvalidOperationException("The window was never drawn.");
				}

				ImGuiWindowPtr best = target;
				ref ImVector<ImGuiWindowPtr> windows = ref context.Windows;
				for (int i = 0; i < windows.Size; i++)
				{
					ImGuiWindowPtr window = windows.Data[i];
					if (window.Active && window.RootWindow.Handle == target.Handle && window.ScrollMax.Y > best.ScrollMax.Y)
					{
						best = window;
					}
				}

				float y = float.IsNegativeInfinity(pixels) ? 0f : float.IsPositiveInfinity(pixels) ? best.ScrollMax.Y : Math.Clamp(best.Scroll.Y + pixels, 0f, best.ScrollMax.Y);
				ImGuiP.SetScrollY(best, y);
			}
			finally
			{
				ImGui.SetCurrentContext(previous);
			}
		}

		/// <summary>The ImGui window of a panel in its owner's context (null pointer when it never was drawn).</summary>
		public static ImGuiWindowPtr FindWindow(ImGuiContextPtr context, string imguiName)
		{
			if (imguiName == null)
			{
				return default;
			}

			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGui.SetCurrentContext(context);
			ImGuiWindowPtr window = ImGuiP.FindWindowByName(imguiName);
			ImGui.SetCurrentContext(previous);
			return window;
		}

		/// <summary>
		///     The texture a view panel shows in a capture at <paramref name="width" /> x <paramref name="height" />: its own
		///     image when it is current at that size (<paramref name="ownIsCurrent" />), else an image rendered for the capture
		///     (once per capture; <paramref name="render" /> draws one frame of the view into the renderer it is given).
		/// </summary>
		public static int ViewTexture(object view, ViewRenderer own, bool ownIsCurrent, int width, int height, Action<ViewRenderer> render)
		{
			width = Math.Max(16, width);
			height = Math.Max(16, height);
			if (own != null && ownIsCurrent && session.HasUI && own.Width == width && own.Height == height)
			{
				return own.Texture;
			}

			if (session.Views.TryGetValue(view, out int texture))
			{
				return texture;
			}

			// Without UI the view's own renderer is free (nothing else shows it): it renders at the capture's size, which
			// spares a second set of GI resources.
			ViewRenderer target = own;
			if (session.HasUI || own == null)
			{
				if (!viewRenderers.TryGetValue(view, out target))
				{
					target = new ViewRenderer(width, height);
					viewRenderers[view] = target;
				}
			}

			target.EnsureSize(width, height);
			for (int i = 0; i < Math.Max(1, session.ViewFrames); i++)
			{
				render(target);
			}

			session.Views[view] = target.Texture;
			return target.Texture;
		}

		/// <summary>Frees the views' capture renderers (project closed, editor closing).</summary>
		public static void ReleaseViews()
		{
			foreach (ViewRenderer renderer in viewRenderers.Values)
			{
				renderer.Dispose();
			}

			viewRenderers.Clear();
		}

		/// <summary>Frees the capture's GL objects of a window's context (the window is closing; its context is current).</summary>
		public static void Forget(NativeWindow window)
		{
			if (renderers.Remove(window, out ImGuiRenderer renderer))
			{
				renderer.Dispose();
			}
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     The window as the editor's last frame drew it, and nothing else: its own draw lists (its child windows, the
		///     popups, menus and tooltips it opened, its dock tab bar clipped to its rectangle) drawn again off screen; other
		///     windows over it are left out. A whole context: its whole last frame. Null when that frame is gone.
		/// </summary>
		private static EditorScreenshot FrameCapture(IEditorPanel panel, EditorApplication editor, ImGuiController owner, NVector2 min, int width, int height)
		{
			NativeWindow glWindow = owner.Window;
			glWindow.MakeCurrent();
			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGui.SetCurrentContext(owner.Context);
			try
			{
				ImDrawData* data = ImGui.GetDrawData().Handle;
				if (data == null || data->Valid == 0 || data->CmdListsCount == 0)
				{
					return null;
				}

				var lists = new List<(nint list, System.Numerics.Vector4 clip)>();
				NVector2 origin;
				uint? windowId = null;
				if (panel.IsWholeContext)
				{
					for (int n = 0; n < data->CmdListsCount; n++)
					{
						lists.Add(((nint)data->CmdLists.Data[n].Handle, ImGuiRenderer.NoClip));
					}

					origin = data->DisplayPos;
				}
				else
				{
					ImGuiWindowPtr target = ImGuiP.FindWindowByName(panel.ImGuiName);
					if (target.IsNull)
					{
						return null;
					}

					windowId = target.ID;
					origin = min;
					var rect = new System.Numerics.Vector4(min.X, min.Y, min.X + width, min.Y + height);
					Dictionary<nint, System.Numerics.Vector4> own = OwnDrawLists(owner.Context, target, rect);
					for (int n = 0; n < data->CmdListsCount; n++)
					{
						var list = (nint)data->CmdLists.Data[n].Handle;
						if (own.TryGetValue(list, out System.Numerics.Vector4 clip))
						{
							lists.Add((list, clip));
						}
					}
				}

				byte[] pixels = RenderToPixels(glWindow, width, height, renderer => renderer.RenderLists(lists, origin, new NVector2(width, height), width, height));
				List<UIAnchor> anchors = TestAnchors.GetAnchors(windowId, min);
				return new EditorScreenshot(panel.PanelName, width, height, pixels, true, anchors);
			}
			finally
			{
				ImGui.SetCurrentContext(previous);
				editor.Window.MakeEditorCurrent();
			}
		}

		/// <summary>
		///     The draw lists of a window's last frame and where each may draw: the window and its child windows (anywhere),
		///     the popups / menus / tooltips opened from it (anywhere: they are clipped to the image), and the dock host's list
		///     (tab bar, node background) inside the window's rectangle only.
		/// </summary>
		private static Dictionary<nint, System.Numerics.Vector4> OwnDrawLists(ImGuiContextPtr context, ImGuiWindowPtr target, System.Numerics.Vector4 rect)
		{
			var result = new Dictionary<nint, System.Numerics.Vector4>();
			// A docked window's tab bar is drawn by its dock node's host window and the host's dock space child window (a
			// floating window is its own host): those count inside the window's rectangle only.
			ImGuiWindowPtr host = target.RootWindowDockTree;
			bool docked = !host.IsNull && host.Handle != target.Handle;
			ref ImVector<ImGuiWindowPtr> windows = ref context.Windows;
			for (int i = 0; i < windows.Size; i++)
			{
				ImGuiWindowPtr window = windows.Data[i];
				if (!window.Active || window.DrawList.IsNull)
				{
					continue;
				}

				if (window.Handle == target.Handle || window.RootWindow.Handle == target.Handle || OpenedFrom(window, target))
				{
					result[(nint)window.DrawList.Handle] = ImGuiRenderer.NoClip;
				}
				else if (docked && (window.Handle == host.Handle || window.RootWindow.Handle == host.Handle))
				{
					result[(nint)window.DrawList.Handle] = rect;
				}
			}

			return result;
		}

		/// <summary>A popup, menu or tooltip (or a child of one) opened from the window, through any chain of popups.</summary>
		private static bool OpenedFrom(ImGuiWindowPtr window, ImGuiWindowPtr target)
		{
			ImGuiWindowPtr current = window.RootWindow;
			for (int depth = 0; depth < 16 && !current.IsNull; depth++)
			{
				if ((current.Flags & (ImGuiWindowFlags.Popup | ImGuiWindowFlags.ChildMenu | ImGuiWindowFlags.Tooltip)) == 0)
				{
					return current.Handle == target.Handle;
				}

				ImGuiWindowPtr parent = current.ParentWindow;
				if (parent.IsNull)
				{
					return false;
				}

				current = parent.RootWindow;
			}

			return false;
		}

		private static EditorScreenshot LayoutCapture(IEditorPanel panel, EditorApplication editor, ImGuiController owner, int width, int height, CaptureOptions options)
		{
			NativeWindow glWindow = owner.Window;
			glWindow.MakeCurrent();
			owner.EnsureAtlasReady();
			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGuiContextPtr ownerContext = owner.Context;

			// The owner's settings as text: window places, tables, docking. Writing them would also clear the owner's own
			// pending save of its layout file; its timer is put back.
			ImGui.SetCurrentContext(ownerContext);
			float dirtyTimer = ownerContext.SettingsDirtyTimer;
			string ownerIni = ImGui.SaveIniSettingsToMemoryS() ?? "";
			ownerContext.SettingsDirtyTimer = dirtyTimer;
			string ini = panel.IsWholeContext ? ownerIni : SingleWindowIni(ownerIni, panel.ImGuiName, width, height);
			string targetName = panel.IsWholeContext ? null : WindowName(FindWindow(ownerContext, panel.ImGuiName));

			ImGuiContextPtr context = ImGui.CreateContext(ownerContext.IO.Fonts);
			bool wasOpen = panel.PanelOpen;
			var copied = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				ImGui.SetCurrentContext(context);
				ImGuizmo.SetImGuiContext(context);
				ImGuiIOPtr io = ImGui.GetIO();
				var ownerIo = new ImGuiIOPtr(&ownerContext.Handle->IO);
				io.IniFilename = null;
				io.ConfigFlags = ownerIo.ConfigFlags;
				io.BackendFlags = ownerIo.BackendFlags;
				io.FontDefault = ownerIo.FontDefault;
				context.Style = ownerContext.Style;
				EditorFonts.Share((nint)ownerContext.Handle, (nint)context.Handle);
				ImGui.LoadIniSettingsFromMemory(ini);

				session = new Session
				{
					HasUI = editor.HasUI,
					HasDocking = ini.Contains("[Docking][Data]", StringComparison.Ordinal) && ini.Contains("DockSpace", StringComparison.Ordinal),
					ViewFrames = options.ViewFrames
				};
				if (!panel.IsWholeContext)
				{
					panel.PanelOpen = true;
				}

				for (int frame = 0; frame < LayoutFrames; frame++)
				{
					session.Frame = frame;
					io.DisplaySize = new NVector2(width, height);
					io.DisplayFramebufferScale = new NVector2(1f, 1f);
					io.DeltaTime = 1f / 60f;
					ImGui.NewFrame();
					ImGuizmo.BeginFrame();
					TestAnchors.BeginCapture();
					if (!panel.IsWholeContext)
					{
						DockAlone(panel.ImGuiName, width, height, frame == 0);
					}

					try
					{
						panel.Draw();
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}

					ImGui.Render();
					if (frame < 2)
					{
						CopyWindowStates(ownerContext, context, targetName, copied);
					}

					panel.AfterCaptureFrame();
				}

				List<UIAnchor> anchors = TestAnchors.EndCapture(NVector2.Zero, new NVector2(width, height));
				byte[] pixels = RenderToPixels(glWindow, width, height, renderer => renderer.Render(width, height));
				return new EditorScreenshot(panel.PanelName, width, height, pixels, false, anchors);
			}
			finally
			{
				TestAnchors.EndCapture(NVector2.Zero, NVector2.Zero);
				session = null;
				if (!panel.IsWholeContext && panel.PanelOpen != wasOpen)
				{
					panel.PanelOpen = wasOpen;
				}

				ImGui.SetCurrentContext(context);
				EditorFonts.Forget((nint)context.Handle);
				ImGui.DestroyContext(context);
				ImGui.SetCurrentContext(previous);
				ImGuizmo.SetImGuiContext(previous);
				editor.Window.MakeEditorCurrent();
			}
		}

		/// <summary>
		///     A capture of one window docks it into a dock space over the whole capture, so it has the editor's look (its tab,
		///     no border or resize grip) and its size is exactly the capture's; the dock node is built on the first frame.
		/// </summary>
		private static void DockAlone(string imguiName, int width, int height, bool build)
		{
			uint dockspace = ImGui.DockSpaceOverViewport();
			if (!build)
			{
				return;
			}

			ImGuiP.DockBuilderRemoveNode(dockspace);
			// ImGuiDockNodeFlags_DockSpace (internal).
			ImGuiP.DockBuilderAddNode(dockspace, (ImGuiDockNodeFlags)(1 << 10));
			ImGuiP.DockBuilderSetNodeSize(dockspace, new NVector2(width, height));
			ImGuiP.DockBuilderDockWindow(imguiName, dockspace);
			ImGuiP.DockBuilderFinish(dockspace);
		}

		/// <summary>
		///     The owner's settings for a capture of one window: no docking (the window floats alone), the window itself at
		///     (0, 0) with the capture's size. Everything else (tables' columns, other windows) stays as the owner has it.
		/// </summary>
		private static string SingleWindowIni(string ownerIni, string imguiName, int width, int height)
		{
			uint target = ImGuiP.ImHashStr(imguiName);
			string place = $"Pos=0,0\nSize={width},{height}\nCollapsed=0\n";
			var result = new StringBuilder();
			bool skipSection = false;
			bool inTarget = false;
			bool written = false;
			foreach (string raw in ownerIni.Split('\n'))
			{
				string line = raw.TrimEnd('\r');
				if (line.StartsWith("[", StringComparison.Ordinal))
				{
					inTarget = false;
					skipSection = line.StartsWith("[Docking]", StringComparison.Ordinal);
					if (skipSection)
					{
						continue;
					}

					if (line.StartsWith("[Window][", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal)
						&& ImGuiP.ImHashStr(line.Substring(9, line.Length - 10)) == target)
					{
						inTarget = true;
						written = true;
						result.Append(line).Append('\n').Append(place);
						continue;
					}

					result.Append(line).Append('\n');
					continue;
				}

				if (skipSection || line.StartsWith("DockId=", StringComparison.Ordinal))
				{
					continue;
				}

				if (inTarget && (line.StartsWith("Pos=", StringComparison.Ordinal) || line.StartsWith("Size=", StringComparison.Ordinal) || line.StartsWith("Collapsed=", StringComparison.Ordinal)))
				{
					continue;
				}

				result.Append(line).Append('\n');
			}

			if (!written)
			{
				result.Append("\n[Window][").Append(imguiName).Append("]\n").Append(place);
			}

			return result.ToString();
		}

		/// <summary>
		///     Copies the owner's state of the captured windows (all of them for a whole context; else the target and its
		///     child windows) into the capture's windows of the same name: ImGui's per-window storage (tree nodes, headers,
		///     tab bars) and the scroll position. Once per window: later capture frames draw with it.
		/// </summary>
		private static void CopyWindowStates(ImGuiContextPtr ownerContext, ImGuiContextPtr context, string targetName, HashSet<string> copied)
		{
			ref ImVector<ImGuiWindowPtr> windows = ref ownerContext.Windows;
			for (int i = 0; i < windows.Size; i++)
			{
				ImGuiWindowPtr source = windows.Data[i];
				string name = WindowName(source);
				if (name == null || copied.Contains(name) || (source.Flags & (ImGuiWindowFlags.Popup | ImGuiWindowFlags.Tooltip | ImGuiWindowFlags.ChildMenu)) != 0)
				{
					continue;
				}

				if (targetName != null && name != targetName && !name.StartsWith(targetName + "/", StringComparison.Ordinal))
				{
					continue;
				}

				ImGuiWindowPtr destination = ImGuiP.FindWindowByName(name);
				if (destination.IsNull)
				{
					continue;
				}

				copied.Add(name);
				var from = new ImGuiStoragePtr(&source.Handle->StateStorage);
				var to = new ImGuiStoragePtr(&destination.Handle->StateStorage);
				to.Clear();
				for (int p = 0; p < from.Data.Size; p++)
				{
					ImGuiStoragePair pair = from.Data.Data[p];
					to.SetVoidPtr(pair.Key, pair.Union.ValP);
				}

				ImGuiP.SetScrollX(destination, source.Scroll.X);
				ImGuiP.SetScrollY(destination, source.Scroll.Y);
			}
		}

		private static string WindowName(ImGuiWindowPtr window)
		{
			return window.IsNull ? null : Marshal.PtrToStringUTF8((nint)window.Name);
		}

		/// <summary>Draws (<paramref name="draw" />, with the GL context's ImGui renderer) into a framebuffer of its own and reads it back (RGBA, top row first).</summary>
		private static byte[] RenderToPixels(NativeWindow glWindow, int width, int height, Action<ImGuiRenderer> draw)
		{
			if (!renderers.TryGetValue(glWindow, out ImGuiRenderer renderer))
			{
				renderer = new ImGuiRenderer();
				renderers[glWindow] = renderer;
			}

			var target = new Framebuffer(width, height);
			try
			{
				target.AddColorAttachment(RenderTextureFormat.RGBA8);
				target.Validate();
				target.Bind();
				GL.ClearColor(0.11f, 0.11f, 0.12f, 1f);
				GL.Clear(ClearBufferMask.ColorBufferBit);
				draw(renderer);

				byte[] pixels = new byte[width * height * 4];
				GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, target.Handle);
				GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
				GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
				GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
				FlipAndOpaque(pixels, width, height);
				return pixels;
			}
			finally
			{
				GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
				target.Delete();
			}
		}

		/// <summary>GL rows are bottom-up: top row first; alpha 255 (the UI is opaque, blending left fractions in places).</summary>
		private static void FlipAndOpaque(byte[] pixels, int width, int height)
		{
			int row = width * 4;
			byte[] swap = new byte[row];
			for (int y = 0; y < height / 2; y++)
			{
				int top = y * row;
				int bottom = (height - 1 - y) * row;
				System.Buffer.BlockCopy(pixels, top, swap, 0, row);
				System.Buffer.BlockCopy(pixels, bottom, pixels, top, row);
				System.Buffer.BlockCopy(swap, 0, pixels, bottom, row);
			}

			for (int i = 3; i < pixels.Length; i += 4)
			{
				pixels[i] = 255;
			}
		}

		#endregion
	}

	/// <summary>How a window is captured.</summary>
	internal sealed class CaptureOptions
	{
		/// <summary>Lay the window out again even at its size on screen (no popups or hover marks).</summary>
		public bool Layout;

		/// <summary>Frames a view (scene, game) renders for a size it does not show (GI and temporal effects settle).</summary>
		public int ViewFrames = WindowCapture.DefaultViewFrames;
	}
}
