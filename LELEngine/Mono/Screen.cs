using System;
using System.Collections.Generic;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine
{
	/// <summary>A display mode the primary monitor supports (Unity's Resolution).</summary>
	public struct Resolution
	{
		public int width;
		public int height;
		public int refreshRate;

		public override string ToString() => width + "x" + height + (refreshRate > 0 ? " @ " + refreshRate + " Hz" : "");
	}

	/// <summary>
	///     The game's image and window (Unity's Screen): its size in pixels, full screen on or off, the modes the display
	///     offers. In the editor the size is the game view's; the window settings are the editor's own and stay as they are.
	/// </summary>
	public static class Screen
	{
		/// <summary>Width of the game's image in pixels (the game view in the editor).</summary>
		public static int width => Input.Host != null ? Math.Max(1, Input.Host.ClientSize.X) : 1280;

		public static int height => Input.Host != null ? Math.Max(1, Input.Host.ClientSize.Y) : 720;

		public static Vector2i size => new Vector2i(width, height);

		/// <summary>The game window covers the display. Setting it switches at once (nothing in the editor).</summary>
		public static bool fullScreen
		{
			get => Game.Mono != null && Game.Mono.WindowState == WindowState.Fullscreen;
			set
			{
				GameHost host = Game.Mono;
				if (host == null || fullScreen == value)
				{
					return;
				}

				host.WindowState = value ? WindowState.Fullscreen : WindowState.Normal;
			}
		}

		/// <summary>The display's current mode, or the window's size when it cannot be read.</summary>
		public static Resolution currentResolution
		{
			get
			{
				try
				{
					MonitorInfo monitor = Monitors.GetPrimaryMonitor();
					VideoMode mode = monitor.CurrentVideoMode;
					return new Resolution { width = mode.Width, height = mode.Height, refreshRate = mode.RefreshRate };
				}
				catch (Exception)
				{
					return new Resolution { width = width, height = height, refreshRate = 0 };
				}
			}
		}

		/// <summary>Modes the primary display supports, lowest first (each size once per refresh rate).</summary>
		public static Resolution[] resolutions
		{
			get
			{
				var list = new List<Resolution>();
				try
				{
					MonitorInfo monitor = Monitors.GetPrimaryMonitor();
					foreach (VideoMode mode in monitor.SupportedVideoModes)
					{
						var resolution = new Resolution { width = mode.Width, height = mode.Height, refreshRate = mode.RefreshRate };
						if (!list.Exists(r => r.width == resolution.width && r.height == resolution.height && r.refreshRate == resolution.refreshRate))
						{
							list.Add(resolution);
						}
					}
				}
				catch (Exception)
				{
					// No display information (headless): the window's own size is the one mode.
				}

				if (list.Count == 0)
				{
					list.Add(currentResolution);
				}

				list.Sort((a, b) => a.width != b.width ? a.width.CompareTo(b.width) : a.height != b.height ? a.height.CompareTo(b.height) : a.refreshRate.CompareTo(b.refreshRate));
				return list.ToArray();
			}
		}

		/// <summary>
		///     Resizes the game window (windowed: its client area; full screen: the display mode when the driver allows it)
		///     and sets full screen on or off. Nothing happens in the editor.
		/// </summary>
		public static void SetResolution(int newWidth, int newHeight, bool fullscreen)
		{
			GameHost host = Game.Mono;
			if (host == null)
			{
				Debug.Log("[Screen] SetResolution ignored in the editor");
				return;
			}

			newWidth = Math.Max(16, newWidth);
			newHeight = Math.Max(16, newHeight);
			if (fullscreen)
			{
				// Leave full screen first so the new size takes; GLFW then uses the closest video mode.
				if (host.WindowState == WindowState.Fullscreen)
				{
					host.WindowState = WindowState.Normal;
				}

				host.ClientSize = new Vector2i(newWidth, newHeight);
				host.WindowState = WindowState.Fullscreen;
			}
			else
			{
				if (host.WindowState != WindowState.Normal)
				{
					host.WindowState = WindowState.Normal;
				}

				host.ClientSize = new Vector2i(newWidth, newHeight);
			}
		}
	}
}
