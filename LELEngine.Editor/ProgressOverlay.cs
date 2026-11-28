using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's progress bar (<see cref="Progress" />): a panel at the top of the editor's window with the task's
	///     title, a bar and what it does now, over a black dim of the whole window, which also takes the clicks while the
	///     task runs (the user's ask, 2026-11-23). It is a window of its own (Windows: a layered tool window, painted with
	///     per-pixel alpha) run by a thread of its own, so the bar moves, follows the editor's window and offers Cancel
	///     while the work blocks the editor's main thread. It shows a task that has run <see cref="ShowDelay" /> seconds
	///     (short ones never flash), and keeps just above the editor's window in the z-order (not owned by it: a window owned
	///     across threads would join the two threads' input, and a busy main thread would hold it up).
	///     <para>
	///         <see cref="Standalone" />: the same panel as a small window of its own in the middle of the screen, with a
	///         taskbar button, while the editor's window is not shown yet (the project's full import before the editor
	///         opens it, <see cref="AssetPreimport.BeforeOpen" />; the user's ask, 2026-11-25).
	///     </para>
	/// </summary>
	internal static class ProgressOverlay
	{
		#region PrivateFields

		/// <summary>Seconds a task runs before the bar shows.</summary>
		private const double ShowDelay = 0.3;

		// The dim over the editor's window (premultiplied black).
		private const byte DimAlpha = 150;

		// The panel's size at 96 DPI.
		private const int PanelWidth = 560;
		private const int PanelHeight = 70;

		private const string Ellipsis = "…";

		private static Thread thread;
		private static volatile bool stopping;
		private static volatile bool standalone;
		private static IntPtr owner;
		private static IntPtr window;

		// The standalone window, where it stands while shown and its scale there, and the window shown now (either, or none).
		private static IntPtr alone;
		private static POINT aloneOrigin;
		private static float aloneScale = 1f;
		private static IntPtr shownWindow;
		private static WndProc procedure;

		// The painted frame: a top-down 32-bit DIB of the editor's client size, selected into memoryDC.
		private static IntPtr memoryDC;
		private static IntPtr bitmap;
		private static IntPtr oldBitmap;
		private static IntPtr bits;
		private static int bitmapWidth;
		private static int bitmapHeight;
		private static IntPtr titleFont;
		private static IntPtr textFont;
		private static int fontDpi;
		private static IntPtr arrowCursor;
		private static IntPtr busyCursor;
		private static RECT cancelRect;
		private static long paintedVersion = -1;
		private static double paintedTime;
		private static double lastShot;
		private static int shots;

		#endregion

		#region PublicFields

		/// <summary>Test runs (<c>progressshots=folder</c>): every painted frame (twice a second at most) as folder/progress_NN.png.</summary>
		public static string ShotsFolder;

		/// <summary>
		///     The bar shows as a small window of its own in the middle of the screen (the monitor under the mouse), not over
		///     the editor's window: before that window shows. Any thread.
		/// </summary>
		public static bool Standalone
		{
			get => standalone;
			set => standalone = value;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Starts the bar's thread for the editor's window (Windows only; nothing elsewhere); without the window yet
		///     (<see cref="IntPtr.Zero" />: the standalone bar before it is made), a later call gives it.
		/// </summary>
		public static void Start(IntPtr editorWindow)
		{
			if (!OperatingSystem.IsWindows())
			{
				return;
			}

			if (editorWindow != IntPtr.Zero)
			{
				Volatile.Write(ref owner, editorWindow);
			}

			if (thread != null)
			{
				return;
			}

			stopping = false;
			thread = new Thread(Run) { IsBackground = true, Name = "Progress bar" };
			thread.Start();
		}

		/// <summary>Ends the thread (the editor closes).</summary>
		public static void Stop()
		{
			if (thread == null)
			{
				return;
			}

			stopping = true;
			thread.Join(1000);
			thread = null;
		}

		#endregion

		#region PrivateMethods

		private static void Run()
		{
			try
			{
				CreateWindow();
				var clock = System.Diagnostics.Stopwatch.StartNew();
				while (!stopping)
				{
					while (PeekMessageW(out MSG message, IntPtr.Zero, 0, 0, PM_REMOVE))
					{
						TranslateMessage(ref message);
						DispatchMessageW(ref message);
					}

					Tick(clock.Elapsed.TotalSeconds);
					// 30 times a second, or at once on input (a click on Cancel).
					MsgWaitForMultipleObjects(0, null, false, 33, QS_ALLINPUT);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] The progress bar stopped: " + e.Message);
			}
			finally
			{
				ReleaseBitmap();
				if (titleFont != IntPtr.Zero) DeleteObject(titleFont);
				if (textFont != IntPtr.Zero) DeleteObject(textFont);
				if (window != IntPtr.Zero) DestroyWindow(window);
				if (alone != IntPtr.Zero) DestroyWindow(alone);
				window = IntPtr.Zero;
				alone = IntPtr.Zero;
				shownWindow = IntPtr.Zero;
			}
		}

		private static void CreateWindow()
		{
			procedure = WindowProcedure;
			IntPtr instance = GetModuleHandleW(null);
			arrowCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW);
			busyCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_APPSTARTING);
			var windowClass = new WNDCLASSEXW
			{
				cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
				lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
				hInstance = instance,
				hCursor = busyCursor,
				lpszClassName = "LELEngineProgressBar"
			};
			if (RegisterClassExW(ref windowClass) == 0)
			{
				Debug.LogWarning("[Editor] The progress bar's window class was not registered (error " + Marshal.GetLastWin32Error() + ")");
			}

			window = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "LELEngineProgressBar", "LELEngine progress", WS_POPUP,
				0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
			if (window == IntPtr.Zero)
			{
				throw new InvalidOperationException("its window was not made (error " + Marshal.GetLastWin32Error() + ")");
			}

			// The standalone panel: a window of its own with a taskbar button (the editor's is not shown yet).
			alone = CreateWindowExW(WS_EX_LAYERED | WS_EX_APPWINDOW | WS_EX_NOACTIVATE, "LELEngineProgressBar", "LELEngine - importing assets", WS_POPUP,
				0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
		}

		private static void Tick(double time)
		{
			ProgressState state = Progress.Current;
			bool single = standalone && alone != IntPtr.Zero;
			IntPtr target = single ? alone : window;
			IntPtr editor = Volatile.Read(ref owner);
			// The standalone bar shows at once: it is all there is to see.
			bool show = state.Running && (single || state.Seconds >= ShowDelay && IsWindow(editor) && IsWindowVisible(editor) && !IsIconic(editor));
			int width = 0;
			int height = 0;
			var origin = new POINT();
			if (show && single)
			{
				// The panel alone, centred on the monitor under the mouse (where the project was chosen) at its scale, where it
				// stays.
				if (shownWindow != alone)
				{
					PlaceAlone();
				}

				width = (int)Math.Round(PanelWidth * aloneScale);
				height = (int)Math.Round(PanelHeight * aloneScale);
				origin = aloneOrigin;
			}
			else if (show)
			{
				GetClientRect(editor, out RECT client);
				width = client.Right;
				height = client.Bottom;
				show = width > 40 && height > 40;
				ClientToScreen(editor, ref origin);
			}

			if (shownWindow != IntPtr.Zero && (!show || shownWindow != target))
			{
				ShowWindow(shownWindow, SW_HIDE);
				shownWindow = IntPtr.Zero;
				paintedVersion = -1;
			}

			if (!show)
			{
				paintedVersion = -1;
				return;
			}

			// Repainted when the task changes, the window's size does, or a bar of unknown length moves (and the time shown).
			bool animate = state.Fraction < 0f && time - paintedTime > 1.0 / 30.0;
			if (state.Version != paintedVersion || width != bitmapWidth || height != bitmapHeight || animate || time - paintedTime > 0.5)
			{
				Paint(state, width, height, time, single);
				paintedVersion = state.Version;
				paintedTime = time;
				var size = new SIZE { cx = width, cy = height };
				var source = new POINT();
				var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
				IntPtr screen = GetDC(IntPtr.Zero);
				UpdateLayeredWindow(target, screen, ref origin, ref size, memoryDC, ref source, 0, ref blend, ULW_ALPHA);
				ReleaseDC(IntPtr.Zero, screen);
				SaveShot(width, height, time);
			}
			else if (!single)
			{
				SetWindowPos(window, IntPtr.Zero, origin.X, origin.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
			}

			if (!single)
			{
				// Just above the editor's window (below whatever is above it: another application in front stays in front).
				IntPtr above = GetWindow(editor, GW_HWNDPREV);
				if (above != window)
				{
					SetWindowPos(window, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
				}
			}

			if (shownWindow != target)
			{
				ShowWindow(target, SW_SHOWNOACTIVATE);
				if (single)
				{
					// In front of what is there (the project chooser just closed): it is all the editor shows.
					SetWindowPos(target, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
					Console.WriteLine($"[Editor] The progress bar's own window shown {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:F0} ms after the process started ({state.Title})");
				}

				shownWindow = target;
			}
		}

		// The scale of the editor's window's monitor.
		private static float Scale()
		{
			return Math.Max(1f, GetDpiForWindow(Volatile.Read(ref owner)) / 96f);
		}

		// The standalone panel's scale and place: the middle of the work area of the monitor under the mouse, at its DPI.
		private static void PlaceAlone()
		{
			GetCursorPos(out POINT cursor);
			IntPtr monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTOPRIMARY);
			aloneScale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpi, out _) == 0 ? Math.Max(1f, dpi / 96f) : 1f;
			int width = (int)Math.Round(PanelWidth * aloneScale);
			int height = (int)Math.Round(PanelHeight * aloneScale);
			var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
			if (!GetMonitorInfoW(monitor, ref info))
			{
				aloneOrigin = new POINT { X = 100, Y = 100 };
				return;
			}

			RECT work = info.rcWork;
			aloneOrigin = new POINT { X = work.Left + (work.Right - work.Left - width) / 2, Y = work.Top + (work.Bottom - work.Top - height) / 2 };
		}

		// ---------------------------------------------------------------- painting

		// The frame: the panel at the top of a dim over the editor's client area, or (single) the panel alone, the whole bitmap.
		private static unsafe void Paint(ProgressState state, int width, int height, double time, bool single)
		{
			EnsureBitmap(width, height);
			float scale = single ? aloneScale : Scale();
			EnsureFonts((int)(scale * 96f));
			uint* pixels = (uint*)bits;
			uint dim = (uint)DimAlpha << 24;
			new Span<uint>(pixels, width * height).Fill(dim);

			int S(float value) => (int)Math.Round(value * scale);
			int panelWidth = single ? width : Math.Min(S(PanelWidth), width - S(32));
			int panelHeight = single ? height : S(PanelHeight);
			int left = single ? 0 : (width - panelWidth) / 2;
			int top = single ? 0 : S(10);
			int padding = S(14);
			Fill(pixels, width, height, left, top, panelWidth, panelHeight, Rgb(0.36f, 0.38f, 0.42f));
			Fill(pixels, width, height, left + 1, top + 1, panelWidth - 2, panelHeight - 2, Rgb(0.13f, 0.135f, 0.15f));

			// The bar: the share done, or a piece going back and forth while it is not known.
			int barLeft = left + padding;
			int barWidth = panelWidth - 2 * padding;
			int barTop = top + S(34);
			int barHeight = Math.Max(4, S(6));
			Fill(pixels, width, height, barLeft, barTop, barWidth, barHeight, Rgb(0.24f, 0.25f, 0.27f));
			uint accent = state.CancelRequested ? Rgb(0.75f, 0.45f, 0.2f) : Rgb(0.26f, 0.56f, 0.96f);
			if (state.Fraction >= 0f)
			{
				Fill(pixels, width, height, barLeft, barTop, (int)(barWidth * state.Fraction), barHeight, accent);
			}
			else
			{
				double phase = time * 0.7 % 1.0;
				int piece = barWidth / 4;
				int start = (int)((phase * 1.25 - 0.25) * barWidth);
				int from = Math.Max(0, start);
				int to = Math.Min(barWidth, start + piece);
				Fill(pixels, width, height, barLeft + from, barTop, to - from, barHeight, accent);
			}

			// Texts (GDI on the panel, made opaque after: GDI leaves alpha 0 where it draws).
			SetBkMode(memoryDC, TRANSPARENT);
			IntPtr previousFont = SelectObject(memoryDC, titleFont);
			SetTextColor(memoryDC, ColorRef(0.93f, 0.94f, 0.96f));
			string right = state.Fraction >= 0f ? (int)Math.Floor(state.Fraction * 100f) + " %" : "";
			if (state.Seconds >= 3)
			{
				right += (right.Length > 0 ? "   " : "") + FormatTime(state.Seconds);
			}

			var titleRect = new RECT { Left = left + padding, Top = top + S(8), Right = left + panelWidth - padding, Bottom = top + S(30) };
			DrawTextW(memoryDC, right, right.Length, ref titleRect, DT_RIGHT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
			titleRect.Right -= S(8) + MeasureWidth(right);
			string title = state.Title ?? "Working";
			DrawTextW(memoryDC, title, title.Length, ref titleRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);

			SelectObject(memoryDC, textFont);
			var infoRect = new RECT { Left = left + padding, Top = barTop + barHeight + S(4), Right = left + panelWidth - padding, Bottom = top + panelHeight - S(4) };
			cancelRect = default;
			if (state.Cancelable)
			{
				string cancel = state.CancelRequested ? "Canceling..." : "Cancel";
				int cancelWidth = MeasureWidth(cancel) + S(16);
				cancelRect = new RECT { Left = infoRect.Right - cancelWidth, Top = infoRect.Top + S(1), Right = infoRect.Right, Bottom = infoRect.Bottom - S(1) };
				if (!state.CancelRequested)
				{
					Fill(pixels, width, height, cancelRect.Left, cancelRect.Top, cancelRect.Right - cancelRect.Left, cancelRect.Bottom - cancelRect.Top, Rgb(0.26f, 0.27f, 0.3f));
				}

				SetTextColor(memoryDC, ColorRef(0.9f, 0.9f, 0.92f));
				RECT label = cancelRect;
				DrawTextW(memoryDC, cancel, cancel.Length, ref label, DT_CENTER | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
				infoRect.Right = cancelRect.Left - S(8);
			}

			SetTextColor(memoryDC, ColorRef(0.66f, 0.68f, 0.72f));
			if (state.Steps > 0)
			{
				// The counter at the right, in the width of its widest value: the text before it stays put as it counts.
				string counter = state.Step + " / " + state.Steps;
				RECT counterRect = infoRect;
				DrawTextW(memoryDC, counter, counter.Length, ref counterRect, DT_RIGHT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
				infoRect.Right -= MeasureWidth(state.Steps + " / " + state.Steps) + S(12);
			}

			if (!string.IsNullOrEmpty(state.Info))
			{
				string info = Fit(state.Info, infoRect.Right - infoRect.Left);
				DrawTextW(memoryDC, info, info.Length, ref infoRect, DT_LEFT | DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX);
			}

			SelectObject(memoryDC, previousFont);
			GdiFlush();
			MakeOpaque(pixels, width, height, left, top, panelWidth, panelHeight);
		}

		private static string FormatTime(double seconds)
		{
			int whole = (int)seconds;
			return whole >= 60 ? whole / 60 + ":" + (whole % 60).ToString("00") : whole + " s";
		}

		private static unsafe void Fill(uint* pixels, int width, int height, int x, int y, int w, int h, uint color)
		{
			int x0 = Math.Max(0, x);
			int y0 = Math.Max(0, y);
			int x1 = Math.Min(width, x + w);
			int y1 = Math.Min(height, y + h);
			for (int row = y0; row < y1; row++)
			{
				new Span<uint>(pixels + row * width + x0, Math.Max(0, x1 - x0)).Fill(color);
			}
		}

		private static unsafe void MakeOpaque(uint* pixels, int width, int height, int x, int y, int w, int h)
		{
			int x0 = Math.Max(0, x);
			int y0 = Math.Max(0, y);
			int x1 = Math.Min(width, x + w);
			int y1 = Math.Min(height, y + h);
			for (int row = y0; row < y1; row++)
			{
				uint* line = pixels + row * width;
				for (int column = x0; column < x1; column++)
				{
					line[column] |= 0xFF000000u;
				}
			}
		}

		/// <summary>An opaque color as the DIB stores it (BGRA).</summary>
		private static uint Rgb(float r, float g, float b)
		{
			return 0xFF000000u | (uint)(r * 255f) << 16 | (uint)(g * 255f) << 8 | (uint)(b * 255f);
		}

		private static uint ColorRef(float r, float g, float b)
		{
			return (uint)(r * 255f) | (uint)(g * 255f) << 8 | (uint)(b * 255f) << 16;
		}

		// The text cut to the width (the font selected now), with an ellipsis: a path loses the middle of its folders while
		// its file name fits (GDI's DT_PATH_ELLIPSIS looks for backslashes only: a path with slashes was clipped at the
		// edge), anything else its end.
		private static string Fit(string text, int width)
		{
			if (width <= 0)
			{
				return "";
			}

			if (MeasureWidth(text) <= width)
			{
				return text;
			}

			int separator = LastPathSeparator(text);
			if (separator > 0)
			{
				string name = text.Substring(separator);
				int kept = LongestFit(separator, count => text.Substring(0, count) + Ellipsis + name, width);
				if (kept >= 0)
				{
					return text.Substring(0, kept) + Ellipsis + name;
				}

				text = Ellipsis + name;
			}

			int start = LongestFit(text.Length - 1, count => text.Substring(0, count) + Ellipsis, width);
			return start >= 0 ? text.Substring(0, start) + Ellipsis : "";
		}

		// The most characters, 0..most, whose text (made) fits the width; -1 when not even none do.
		private static int LongestFit(int most, Func<int, string> made, int width)
		{
			if (MeasureWidth(made(0)) > width)
			{
				return -1;
			}

			int low = 0;
			int high = most;
			while (low < high)
			{
				int middle = (low + high + 1) / 2;
				if (MeasureWidth(made(middle)) <= width)
				{
					low = middle;
				}
				else
				{
					high = middle - 1;
				}
			}

			return low;
		}

		// The last slash or backslash with a name on both sides (" / " is prose or a count, not a path), or -1.
		private static int LastPathSeparator(string text)
		{
			for (int i = text.Length - 2; i > 0; i--)
			{
				if ((text[i] == '/' || text[i] == '\\') && text[i - 1] != ' ' && text[i + 1] != ' ')
				{
					return i;
				}
			}

			return -1;
		}

		private static int MeasureWidth(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return 0;
			}

			var rect = new RECT();
			DrawTextW(memoryDC, text, text.Length, ref rect, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
			return rect.Right - rect.Left;
		}

		private static void EnsureBitmap(int width, int height)
		{
			if (bitmap != IntPtr.Zero && width == bitmapWidth && height == bitmapHeight)
			{
				return;
			}

			ReleaseBitmap();
			IntPtr screen = GetDC(IntPtr.Zero);
			memoryDC = CreateCompatibleDC(screen);
			ReleaseDC(IntPtr.Zero, screen);
			var info = new BITMAPINFOHEADER
			{
				biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
				biWidth = width,
				// Negative: top row first.
				biHeight = -height,
				biPlanes = 1,
				biBitCount = 32,
				biCompression = 0
			};
			bitmap = CreateDIBSection(memoryDC, ref info, 0, out bits, IntPtr.Zero, 0);
			oldBitmap = SelectObject(memoryDC, bitmap);
			bitmapWidth = width;
			bitmapHeight = height;
		}

		private static void ReleaseBitmap()
		{
			if (memoryDC != IntPtr.Zero)
			{
				SelectObject(memoryDC, oldBitmap);
				DeleteDC(memoryDC);
				memoryDC = IntPtr.Zero;
			}

			if (bitmap != IntPtr.Zero)
			{
				DeleteObject(bitmap);
				bitmap = IntPtr.Zero;
			}

			bitmapWidth = 0;
			bitmapHeight = 0;
		}

		private static void EnsureFonts(int dpi)
		{
			if (dpi == fontDpi && titleFont != IntPtr.Zero)
			{
				return;
			}

			if (titleFont != IntPtr.Zero) DeleteObject(titleFont);
			if (textFont != IntPtr.Zero) DeleteObject(textFont);
			float scale = dpi / 96f;
			titleFont = CreateFontW(-(int)Math.Round(15 * scale), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
			textFont = CreateFontW(-(int)Math.Round(13 * scale), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");
			fontDpi = dpi;
		}

		/// <summary>Test runs: the painted frame as a PNG (RGBA, straight alpha over black is what the screen shows of the dim).</summary>
		private static unsafe void SaveShot(int width, int height, double time)
		{
			string folder = ShotsFolder;
			if (folder == null || time - lastShot < 0.5)
			{
				return;
			}

			lastShot = time;
			var rgba = new byte[width * height * 4];
			uint* pixels = (uint*)bits;
			for (int i = 0; i < width * height; i++)
			{
				uint p = pixels[i];
				rgba[i * 4] = (byte)(p >> 16);
				rgba[i * 4 + 1] = (byte)(p >> 8);
				rgba[i * 4 + 2] = (byte)p;
				rgba[i * 4 + 3] = (byte)(p >> 24);
			}

			try
			{
				Directory.CreateDirectory(folder);
				File.WriteAllBytes(Path.Combine(folder, "progress_" + (++shots).ToString("00") + ".png"), Png.Encode(rgba, width, height));
			}
			catch (Exception)
			{
				// A test aid only.
			}
		}

		private static IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
		{
			switch (message)
			{
				case WM_MOUSEACTIVATE:
					return (IntPtr)MA_NOACTIVATE;
				case WM_SETCURSOR:
				{
					GetCursorPosition(hwnd, out int x, out int y);
					SetCursor(InCancel(x, y) ? arrowCursor : busyCursor);
					return (IntPtr)1;
				}
				case WM_LBUTTONDOWN:
				{
					int x = (short)((long)lParam & 0xFFFF);
					int y = (short)(((long)lParam >> 16) & 0xFFFF);
					if (InCancel(x, y))
					{
						Progress.RequestCancel();
					}

					return IntPtr.Zero;
				}
			}

			return DefWindowProcW(hwnd, message, wParam, lParam);
		}

		private static bool InCancel(int x, int y)
		{
			return cancelRect.Right > cancelRect.Left && x >= cancelRect.Left && x < cancelRect.Right && y >= cancelRect.Top && y < cancelRect.Bottom;
		}

		private static void GetCursorPosition(IntPtr hwnd, out int x, out int y)
		{
			GetCursorPos(out POINT point);
			ScreenToClient(hwnd, ref point);
			x = point.X;
			y = point.Y;
		}

		#endregion

		#region Native

		private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

		private const uint WS_POPUP = 0x80000000;
		private const uint WS_EX_LAYERED = 0x00080000;
		private const uint WS_EX_TOOLWINDOW = 0x00000080;
		private const uint WS_EX_APPWINDOW = 0x00040000;
		private const uint WS_EX_NOACTIVATE = 0x08000000;
		private const uint MONITOR_DEFAULTTOPRIMARY = 1;
		private const int MDT_EFFECTIVE_DPI = 0;
		private const uint PM_REMOVE = 1;
		private const uint QS_ALLINPUT = 0x04FF;
		private const int SW_HIDE = 0;
		private const int SW_SHOWNOACTIVATE = 4;
		private const uint SWP_NOSIZE = 0x0001;
		private const uint SWP_NOMOVE = 0x0002;
		private const uint SWP_NOZORDER = 0x0004;
		private const uint SWP_NOACTIVATE = 0x0010;
		private const uint SWP_NOOWNERZORDER = 0x0200;
		private const uint GW_HWNDPREV = 3;
		private static readonly IntPtr HWND_TOP = IntPtr.Zero;
		private const byte AC_SRC_ALPHA = 1;
		private const uint ULW_ALPHA = 2;
		private const int TRANSPARENT = 1;
		private const uint DT_LEFT = 0;
		private const uint DT_CENTER = 1;
		private const uint DT_RIGHT = 2;
		private const uint DT_VCENTER = 4;
		private const uint DT_SINGLELINE = 0x20;
		private const uint DT_CALCRECT = 0x400;
		private const uint DT_NOPREFIX = 0x800;
		private const uint DT_END_ELLIPSIS = 0x8000;
		private const uint CLEARTYPE_QUALITY = 5;
		private const uint WM_SETCURSOR = 0x0020;
		private const uint WM_MOUSEACTIVATE = 0x0021;
		private const uint WM_LBUTTONDOWN = 0x0201;
		private const int MA_NOACTIVATE = 3;
		private const int IDC_ARROW = 32512;
		private const int IDC_APPSTARTING = 32650;

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct WNDCLASSEXW
		{
			public uint cbSize;
			public uint style;
			public IntPtr lpfnWndProc;
			public int cbClsExtra;
			public int cbWndExtra;
			public IntPtr hInstance;
			public IntPtr hIcon;
			public IntPtr hCursor;
			public IntPtr hbrBackground;
			public string lpszMenuName;
			public string lpszClassName;
			public IntPtr hIconSm;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MSG
		{
			public IntPtr hwnd;
			public uint message;
			public IntPtr wParam;
			public IntPtr lParam;
			public uint time;
			public POINT pt;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct POINT
		{
			public int X;
			public int Y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SIZE
		{
			public int cx;
			public int cy;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct RECT
		{
			public int Left;
			public int Top;
			public int Right;
			public int Bottom;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MONITORINFO
		{
			public uint cbSize;
			public RECT rcMonitor;
			public RECT rcWork;
			public uint dwFlags;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct BLENDFUNCTION
		{
			public byte BlendOp;
			public byte BlendFlags;
			public byte SourceConstantAlpha;
			public byte AlphaFormat;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct BITMAPINFOHEADER
		{
			public uint biSize;
			public int biWidth;
			public int biHeight;
			public ushort biPlanes;
			public ushort biBitCount;
			public uint biCompression;
			public uint biSizeImage;
			public int biXPelsPerMeter;
			public int biYPelsPerMeter;
			public uint biClrUsed;
			public uint biClrImportant;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern ushort RegisterClassExW(ref WNDCLASSEXW windowClass);

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

		[DllImport("user32.dll")]
		private static extern bool DestroyWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

		[DllImport("user32.dll")]
		private static extern bool PeekMessageW(out MSG message, IntPtr hwnd, uint filterMin, uint filterMax, uint remove);

		[DllImport("user32.dll")]
		private static extern bool TranslateMessage(ref MSG message);

		[DllImport("user32.dll")]
		private static extern IntPtr DispatchMessageW(ref MSG message);

		[DllImport("user32.dll")]
		private static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds, uint wakeMask);

		[DllImport("user32.dll")]
		private static extern bool ShowWindow(IntPtr hwnd, int command);

		[DllImport("user32.dll")]
		private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

		[DllImport("user32.dll")]
		private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

		[DllImport("user32.dll")]
		private static extern bool IsWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool IsWindowVisible(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool IsIconic(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

		[DllImport("user32.dll")]
		private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);

		[DllImport("user32.dll")]
		private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);

		[DllImport("user32.dll")]
		private static extern bool GetCursorPos(out POINT point);

		[DllImport("user32.dll")]
		private static extern IntPtr SetCursor(IntPtr cursor);

		[DllImport("user32.dll")]
		private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);

		[DllImport("user32.dll")]
		private static extern uint GetDpiForWindow(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

		[DllImport("shcore.dll")]
		private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

		[DllImport("user32.dll")]
		private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

		[DllImport("user32.dll")]
		private static extern IntPtr GetDC(IntPtr hwnd);

		[DllImport("user32.dll")]
		private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

		[DllImport("user32.dll")]
		private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destination, ref POINT position, ref SIZE size, IntPtr source, ref POINT sourcePosition, uint colorKey, ref BLENDFUNCTION blend, uint flags);

		[DllImport("user32.dll", CharSet = CharSet.Unicode)]
		private static extern int DrawTextW(IntPtr dc, string text, int length, ref RECT rect, uint format);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateCompatibleDC(IntPtr dc);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteDC(IntPtr dc);

		[DllImport("gdi32.dll")]
		private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);

		[DllImport("gdi32.dll")]
		private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr gdiObject);

		[DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr CreateFontW(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

		[DllImport("gdi32.dll")]
		private static extern uint SetTextColor(IntPtr dc, uint color);

		[DllImport("gdi32.dll")]
		private static extern int SetBkMode(IntPtr dc, int mode);

		[DllImport("gdi32.dll")]
		private static extern bool GdiFlush();

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string name);

		#endregion
	}
}
