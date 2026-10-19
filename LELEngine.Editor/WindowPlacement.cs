using System;
using System.IO;
using System.Text.Json;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;

namespace LELEngine.Editor
{
	/// <summary>
	///     Where an operating-system window of the editor is (the editor's window, the UI Builder's): its bounds while
	///     not maximized and whether it is maximized, in a file of the editor's data folder, put back the next time the
	///     window opens. The owner calls <see cref="Track" /> when the window moves or resizes, <see cref="Restore" />
	///     on the hidden window it created and <see cref="Save" /> when it closes.
	/// </summary>
	internal sealed class WindowPlacement
	{
		#region PrivateFields

		private readonly NativeWindow window;
		private readonly string file;

		// The bounds while not maximized (what "restore" goes back to), and whether it is maximized.
		private Vector2i normalLocation;
		private Vector2i normalSize;
		private bool maximized;

		#endregion

		#region Constructors

		/// <param name="fileName">File in the editor's data folder (window.json, builder-window.json).</param>
		public WindowPlacement(NativeWindow window, string fileName)
		{
			this.window = window;
			file = Path.Combine(RecentProjects.EditorDataFolder, fileName);
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Puts the hidden window where it was last time (its bounds while not maximized first, so restoring from
		///     maximized goes back to them), shows it, then maximizes it when it was. Centered when nothing is saved,
		///     <paramref name="load" /> is false (automated runs) or the saved place is on no monitor (unplugged since).
		/// </summary>
		public void Restore(bool load)
		{
			Saved saved = load ? Saved.Load(file) : null;
			if (saved != null && saved.Width >= 200 && saved.Height >= 150 && IsOnAMonitor(new Vector2i(saved.X, saved.Y)))
			{
				window.ClientSize = new Vector2i(saved.Width, saved.Height);
				window.ClientLocation = new Vector2i(saved.X, saved.Y);
			}
			else
			{
				saved = null;
				window.CenterWindow();
			}

			normalLocation = window.ClientLocation;
			normalSize = window.ClientSize;
			window.IsVisible = true;
			if (saved != null && saved.Maximized)
			{
				window.WindowState = WindowState.Maximized;
				maximized = true;
			}
		}

		/// <summary>Follows the window's bounds while it is not maximized, and whether it is maximized (minimized keeps both).</summary>
		public void Track()
		{
			switch (window.WindowState)
			{
				case WindowState.Normal:
					normalLocation = window.ClientLocation;
					normalSize = window.ClientSize;
					maximized = false;
					break;
				case WindowState.Maximized:
					maximized = true;
					break;
			}
		}

		public void Save()
		{
			if (normalSize.X <= 0 || normalSize.Y <= 0)
			{
				return;
			}

			bool isMaximized = window.WindowState == WindowState.Maximized || (window.WindowState == WindowState.Minimized && maximized);
			new Saved { X = normalLocation.X, Y = normalLocation.Y, Width = normalSize.X, Height = normalSize.Y, Maximized = isMaximized }.Save(file);
		}

		#endregion

		#region PrivateMethods

		/// <summary>A point a little inside the window's top left corner lies on a connected monitor.</summary>
		private static bool IsOnAMonitor(Vector2i location)
		{
			Vector2i point = location + new Vector2i(40, 10);
			foreach (MonitorInfo monitor in Monitors.GetMonitors())
			{
				Box2i area = monitor.ClientArea;
				if (point.X >= area.Min.X && point.X < area.Max.X && point.Y >= area.Min.Y && point.Y < area.Max.Y)
				{
					return true;
				}
			}

			return false;
		}

		#endregion

		#region NestedTypes

		private sealed class Saved
		{
			public int X { get; set; }
			public int Y { get; set; }
			public int Width { get; set; }
			public int Height { get; set; }
			public bool Maximized { get; set; }

			public static Saved Load(string file)
			{
				try
				{
					return File.Exists(file) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(file)) : null;
				}
				catch (Exception e) when (e is IOException || e is JsonException || e is UnauthorizedAccessException)
				{
					return null;
				}
			}

			public void Save(string file)
			{
				try
				{
					Directory.CreateDirectory(Path.GetDirectoryName(file));
					File.WriteAllText(file, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					Console.WriteLine("[Window] Cannot save the window placement: " + e.Message);
				}
			}
		}

		#endregion
	}
}
