using System;
using System.IO;
using System.Runtime.InteropServices;
using Hexa.NET.ImGui;
using Hexa.NET.ImGuizmo;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Common.Input;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Dear ImGui platform and renderer backend for an OpenTK window: feeds window input into ImGui and draws
	///     its draw lists with OpenGL 4. Implements the ImGui 1.92 texture protocol (the backend creates, updates and
	///     destroys the textures ImGui asks for, e.g. the font atlas as glyphs are added).
	/// </summary>
	internal sealed unsafe class ImGuiController : IDisposable
	{
		#region PublicFields

		public ImGuiContextPtr Context { get; }

		#endregion

		#region PrivateFields

		private readonly NativeWindow window;
		private readonly IntPtr iniPath;

		/// <summary>The preferences' theme version this context shows (<see cref="ApplyStyle" />).</summary>
		private int themeVersion = -1;

		private readonly ImGuiRenderer renderer;
		private ImGuiMouseCursor lastCursor = (ImGuiMouseCursor)(-2);

		#endregion

		#region Constructors

		public ImGuiController(NativeWindow window, string iniFile)
		{
			this.window = window;
			Context = ImGui.CreateContext();
			ImGui.SetCurrentContext(Context);
			ImGuizmo.SetImGuiContext(Context);

			ImGuiIOPtr io = ImGui.GetIO();
			io.ConfigFlags |= ImGuiConfigFlags.DockingEnable | ImGuiConfigFlags.NavEnableKeyboard;
			io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset | ImGuiBackendFlags.RendererHasTextures | ImGuiBackendFlags.HasMouseCursors;

			Directory.CreateDirectory(Path.GetDirectoryName(iniFile));
			if (ReadOnlyLayouts)
			{
				// Automated runs read the user's layout but never write it (a test docking windows would move theirs).
				if (File.Exists(iniFile))
				{
					ImGui.LoadIniSettingsFromDisk(iniFile);
				}

				io.IniFilename = null;
			}
			else
			{
				iniPath = Marshal.StringToHGlobalAnsi(iniFile);
				io.IniFilename = (byte*)iniPath;
			}

			string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeui.ttf");
			if (File.Exists(font))
			{
				io.Fonts.AddFontFromFileTTF(font, 17f);
			}
			else
			{
				io.Fonts.AddFontDefault();
			}

			// The bold face marks overridden prefab values in the inspector (EditorFonts.PushBold).
			string bold = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segoeuib.ttf");
			if (File.Exists(bold))
			{
				EditorFonts.Register((nint)Context.Handle, io.Fonts.AddFontFromFileTTF(bold, 17f));
			}

			// Source code (the console's source view): Consolas or Cascadia Mono, else ImGui's own monospaced font.
			string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
			string mono = Array.Find(new[] { "consola.ttf", "CascadiaMono.ttf" }, name => File.Exists(Path.Combine(fonts, name)));
			EditorFonts.RegisterMono((nint)Context.Handle, mono != null ? io.Fonts.AddFontFromFileTTF(Path.Combine(fonts, mono), 15f) : io.Fonts.AddFontDefault());

			renderer = new ImGuiRenderer();

			window.KeyDown += OnKeyDown;
			window.KeyUp += OnKeyUp;
			window.TextInput += OnTextInput;
			window.MouseDown += OnMouseDown;
			window.MouseUp += OnMouseUp;
			window.MouseMove += OnMouseMove;
			window.MouseWheel += OnMouseWheel;
			window.FocusedChanged += OnFocusChanged;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Applies the editor's theme to this context; again before the next frame when the preferences' theme
		///     changed. A window with a look of its own on top (the Audio Studio) sets its own.
		/// </summary>
		public Action ApplyStyle = EditorStyle.Apply;

		/// <summary>Automated runs: layout files (layout.ini, ...) are read, never saved.</summary>
		public static bool ReadOnlyLayouts;

		/// <summary>Makes this controller's ImGui context the current one (several windows have their own).</summary>
		public void Activate()
		{
			ImGui.SetCurrentContext(Context);
			ImGuizmo.SetImGuiContext(Context);
		}

		public void NewFrame(float deltaTime, int width, int height)
		{
			Activate();
			if (themeVersion != EditorPreferences.ThemeVersion)
			{
				themeVersion = EditorPreferences.ThemeVersion;
				ApplyStyle();
			}

			ImGuiIOPtr io = ImGui.GetIO();
			io.DisplaySize = new NVector2(Math.Max(1, width), Math.Max(1, height));
			io.DisplayFramebufferScale = new NVector2(1f, 1f);
			io.DeltaTime = deltaTime > 0f ? deltaTime : 1f / 60f;
			UpdateCursor();

			ImGui.NewFrame();
			ImGuizmo.BeginFrame();
			HasDrawn = true;
		}

		/// <summary>Ends the ImGui frame (call before rendering anything the frame's UI shows).</summary>
		public void EndFrame()
		{
			Activate();
			ImGui.Render();
		}

		/// <summary>Draws the ended frame into the bound framebuffer.</summary>
		public void RenderDrawData(int framebufferWidth, int framebufferHeight)
		{
			Activate();
			renderer.Render(framebufferWidth, framebufferHeight);
		}

		/// <summary>The window whose input and GL context this ImGui context has.</summary>
		public NativeWindow Window => window;

		/// <summary>
		///     The ImGui context drew a frame since it was made (window captures copy its layout and window state; a context
		///     that never drew, as the editor's in batch mode, has the layout it read only).
		/// </summary>
		public bool HasDrawn { get; private set; }

		/// <summary>
		///     Contexts sharing this one's font atlas (window captures, menu passes) need it set up by a frame of its owner:
		///     a context that never drew (batch mode) runs one empty frame first.
		/// </summary>
		public void EnsureAtlasReady()
		{
			if (HasDrawn)
			{
				return;
			}

			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			NewFrame(1f / 60f, 64, 64);
			ImGui.EndFrame();
			ImGui.SetCurrentContext(previous);
			ImGuizmo.SetImGuiContext(previous);
		}

		public void Dispose()
		{
			window.KeyDown -= OnKeyDown;
			window.KeyUp -= OnKeyUp;
			window.TextInput -= OnTextInput;
			window.MouseDown -= OnMouseDown;
			window.MouseUp -= OnMouseUp;
			window.MouseMove -= OnMouseMove;
			window.MouseWheel -= OnMouseWheel;
			window.FocusedChanged -= OnFocusChanged;

			Activate();
			ImGuiRenderer.DestroyOwnTextures();
			renderer.Dispose();
			EditorFonts.Forget((nint)Context.Handle);
			ImGui.DestroyContext(Context);
			Marshal.FreeHGlobal(iniPath);
		}

		#endregion

		#region PrivateMethods

		private void UpdateCursor()
		{
			ImGuiMouseCursor cursor = ImGui.GetMouseCursor();
			if (cursor == lastCursor)
			{
				return;
			}

			lastCursor = cursor;
			switch (cursor)
			{
				case ImGuiMouseCursor.TextInput:
					window.Cursor = MouseCursor.IBeam;
					break;
				case ImGuiMouseCursor.ResizeEw:
					window.Cursor = MouseCursor.HResize;
					break;
				case ImGuiMouseCursor.ResizeNs:
					window.Cursor = MouseCursor.VResize;
					break;
				case ImGuiMouseCursor.Hand:
					window.Cursor = MouseCursor.Hand;
					break;
				default:
					window.Cursor = MouseCursor.Default;
					break;
			}
		}

		// Window events arrive for one window at a time; each handler feeds its own context.

		private void OnKeyDown(KeyboardKeyEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			UpdateModifiers(e.Modifiers);
			ImGuiKey key = MapKey(e.Key);
			if (key != ImGuiKey.None)
			{
				ImGui.GetIO().AddKeyEvent(key, true);
			}
		}

		private void OnKeyUp(KeyboardKeyEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			UpdateModifiers(e.Modifiers);
			ImGuiKey key = MapKey(e.Key);
			if (key != ImGuiKey.None)
			{
				ImGui.GetIO().AddKeyEvent(key, false);
			}
		}

		private static void UpdateModifiers(KeyModifiers modifiers)
		{
			ImGuiIOPtr io = ImGui.GetIO();
			io.AddKeyEvent(ImGuiKey.ModCtrl, (modifiers & KeyModifiers.Control) != 0);
			io.AddKeyEvent(ImGuiKey.ModShift, (modifiers & KeyModifiers.Shift) != 0);
			io.AddKeyEvent(ImGuiKey.ModAlt, (modifiers & KeyModifiers.Alt) != 0);
			io.AddKeyEvent(ImGuiKey.ModSuper, (modifiers & KeyModifiers.Super) != 0);
		}

		private void OnTextInput(TextInputEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			ImGui.GetIO().AddInputCharacter((uint)e.Unicode);
		}

		private void OnMouseDown(MouseButtonEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			int button = MapMouseButton(e.Button);
			if (button >= 0)
			{
				ImGui.GetIO().AddMouseButtonEvent(button, true);
			}
		}

		private void OnMouseUp(MouseButtonEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			int button = MapMouseButton(e.Button);
			if (button >= 0)
			{
				ImGui.GetIO().AddMouseButtonEvent(button, false);
			}
		}

		private void OnMouseMove(MouseMoveEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			// A grabbed cursor (game view mouse look) has no meaningful position for the UI.
			if (window.CursorState != CursorState.Grabbed)
			{
				ImGui.GetIO().AddMousePosEvent(e.X, e.Y);
			}
		}

		private void OnMouseWheel(MouseWheelEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			ImGui.GetIO().AddMouseWheelEvent(e.OffsetX, e.OffsetY);
		}

		private void OnFocusChanged(FocusedChangedEventArgs e)
		{
			ImGui.SetCurrentContext(Context);
			ImGui.GetIO().AddFocusEvent(e.IsFocused);
		}

		private static int MapMouseButton(MouseButton button)
		{
			switch (button)
			{
				case MouseButton.Left: return 0;
				case MouseButton.Right: return 1;
				case MouseButton.Middle: return 2;
				case MouseButton.Button4: return 3;
				case MouseButton.Button5: return 4;
				default: return -1;
			}
		}

		/// <summary>ImGui's key for a window key (ImGuiKey.None for keys ImGui does not know); the Input Manager listens with it.</summary>
		internal static ImGuiKey MapKey(Keys key)
		{
			if (key >= Keys.A && key <= Keys.Z)
			{
				return ImGuiKey.A + (key - Keys.A);
			}

			if (key >= Keys.D0 && key <= Keys.D9)
			{
				return ImGuiKey.Key0 + (key - Keys.D0);
			}

			if (key >= Keys.F1 && key <= Keys.F12)
			{
				return ImGuiKey.F1 + (key - Keys.F1);
			}

			if (key >= Keys.KeyPad0 && key <= Keys.KeyPad9)
			{
				return ImGuiKey.Keypad0 + (key - Keys.KeyPad0);
			}

			switch (key)
			{
				case Keys.Tab: return ImGuiKey.Tab;
				case Keys.Left: return ImGuiKey.LeftArrow;
				case Keys.Right: return ImGuiKey.RightArrow;
				case Keys.Up: return ImGuiKey.UpArrow;
				case Keys.Down: return ImGuiKey.DownArrow;
				case Keys.PageUp: return ImGuiKey.PageUp;
				case Keys.PageDown: return ImGuiKey.PageDown;
				case Keys.Home: return ImGuiKey.Home;
				case Keys.End: return ImGuiKey.End;
				case Keys.Insert: return ImGuiKey.Insert;
				case Keys.Delete: return ImGuiKey.Delete;
				case Keys.Backspace: return ImGuiKey.Backspace;
				case Keys.Space: return ImGuiKey.Space;
				case Keys.Enter: return ImGuiKey.Enter;
				case Keys.KeyPadEnter: return ImGuiKey.KeypadEnter;
				case Keys.Escape: return ImGuiKey.Escape;
				case Keys.LeftControl: return ImGuiKey.LeftCtrl;
				case Keys.RightControl: return ImGuiKey.RightCtrl;
				case Keys.LeftShift: return ImGuiKey.LeftShift;
				case Keys.RightShift: return ImGuiKey.RightShift;
				case Keys.LeftAlt: return ImGuiKey.LeftAlt;
				case Keys.RightAlt: return ImGuiKey.RightAlt;
				case Keys.LeftSuper: return ImGuiKey.LeftSuper;
				case Keys.RightSuper: return ImGuiKey.RightSuper;
				case Keys.Menu: return ImGuiKey.Menu;
				case Keys.Apostrophe: return ImGuiKey.Apostrophe;
				case Keys.Comma: return ImGuiKey.Comma;
				case Keys.Minus: return ImGuiKey.Minus;
				case Keys.Period: return ImGuiKey.Period;
				case Keys.Slash: return ImGuiKey.Slash;
				case Keys.Semicolon: return ImGuiKey.Semicolon;
				case Keys.Equal: return ImGuiKey.Equal;
				case Keys.LeftBracket: return ImGuiKey.LeftBracket;
				case Keys.Backslash: return ImGuiKey.Backslash;
				case Keys.RightBracket: return ImGuiKey.RightBracket;
				case Keys.GraveAccent: return ImGuiKey.GraveAccent;
				case Keys.CapsLock: return ImGuiKey.CapsLock;
				case Keys.ScrollLock: return ImGuiKey.ScrollLock;
				case Keys.NumLock: return ImGuiKey.NumLock;
				case Keys.PrintScreen: return ImGuiKey.PrintScreen;
				case Keys.Pause: return ImGuiKey.Pause;
				case Keys.KeyPadDecimal: return ImGuiKey.KeypadDecimal;
				case Keys.KeyPadDivide: return ImGuiKey.KeypadDivide;
				case Keys.KeyPadMultiply: return ImGuiKey.KeypadMultiply;
				case Keys.KeyPadSubtract: return ImGuiKey.KeypadSubtract;
				case Keys.KeyPadAdd: return ImGuiKey.KeypadAdd;
				case Keys.KeyPadEqual: return ImGuiKey.KeypadEqual;
				default: return ImGuiKey.None;
			}
		}

		#endregion
	}
}
