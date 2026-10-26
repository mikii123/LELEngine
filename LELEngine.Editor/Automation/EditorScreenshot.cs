using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace LELEngine.Editor
{
	/// <summary>
	///     An image of one editor window (<see cref="EditorAutomation.GetScreenshot" />, <see cref="EditorWindow.GetScreenshot" />):
	///     RGBA pixels, top row first, plus the named UI items the window drew (<see cref="Anchors" />, in the image's pixels).
	/// </summary>
	public sealed class EditorScreenshot
	{
		#region Constructors

		internal EditorScreenshot(string window, int width, int height, byte[] pixels, bool live, IReadOnlyList<UIAnchor> anchors)
		{
			Window = window;
			Width = width;
			Height = height;
			Pixels = pixels;
			Live = live;
			Anchors = anchors ?? Array.Empty<UIAnchor>();
		}

		#endregion

		#region PublicFields

		/// <summary>The window's name (<see cref="EditorAutomation.GetWindows" />).</summary>
		public string Window { get; }

		public int Width { get; }
		public int Height { get; }

		/// <summary>RGBA, 8 bits per channel, <see cref="Width" /> x <see cref="Height" />, top row first.</summary>
		public byte[] Pixels { get; }

		/// <summary>
		///     True: the window as the editor's last frame drew it (at its size on screen, with its own open menus, popups and
		///     hover marks; other windows over it are never in the image). False: the window laid out again off screen at the
		///     requested size (no popups, nothing hovered).
		/// </summary>
		public bool Live { get; }

		/// <summary>Named items of the window (hierarchy rows, fields, buttons the editor names) where the image shows them.</summary>
		public IReadOnlyList<UIAnchor> Anchors { get; }

		#endregion

		#region PublicMethods

		/// <summary>Writes the image as a PNG file (folders are created).</summary>
		public void SavePng(string path)
		{
			string full = Path.GetFullPath(path);
			Directory.CreateDirectory(Path.GetDirectoryName(full));
			File.WriteAllBytes(full, EncodePng());
		}

		/// <summary>The image as PNG bytes (RGBA, no compression filters: fast, a little larger).</summary>
		public byte[] EncodePng()
		{
			return Png.Encode(Pixels, Width, Height);
		}

		#endregion
	}

	/// <summary>A named item of an editor window and its rectangle (pixels of the window's image or screen).</summary>
	public readonly struct UIAnchor
	{
		#region Constructors

		public UIAnchor(string name, float x, float y, float width, float height, string value = null)
		{
			Name = name;
			X = x;
			Y = y;
			Width = width;
			Height = height;
			Value = value;
		}

		#endregion

		#region PublicFields

		/// <summary>e.g. "hierarchy:Main Camera", "field:Transform.position", "project:Scenes/Main.scene", "menu:File/Save Scene".</summary>
		public string Name { get; }

		public float X { get; }
		public float Y { get; }
		public float Width { get; }
		public float Height { get; }

		/// <summary>A value field's value as text (what <see cref="EditorAutomation.Input" /> takes), else null.</summary>
		public string Value { get; }

		#endregion

		#region PublicMethods

		public override string ToString()
		{
			return $"{Name}{(Value != null ? " = " + Value : "")} ({X:0},{Y:0} {Width:0}x{Height:0})";
		}

		#endregion
	}

	/// <summary>Minimal PNG writer: 8-bit RGBA, one zlib stream, filter 0 on every row.</summary>
	internal static class Png
	{
		#region PrivateFields

		private static readonly uint[] crcTable = BuildCrcTable();

		#endregion

		#region PublicMethods

		public static byte[] Encode(byte[] rgba, int width, int height)
		{
			if (rgba == null || rgba.Length < width * height * 4)
			{
				throw new ArgumentException("The pixel buffer is smaller than width x height x 4.");
			}

			using var output = new MemoryStream();
			output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

			byte[] header = new byte[13];
			WriteBigEndian(header, 0, (uint)width);
			WriteBigEndian(header, 4, (uint)height);
			header[8] = 8; // bit depth
			header[9] = 6; // RGBA
			WriteChunk(output, "IHDR", header);

			using (var compressed = new MemoryStream())
			{
				using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
				{
					int row = width * 4;
					for (int y = 0; y < height; y++)
					{
						zlib.WriteByte(0);
						zlib.Write(rgba, y * row, row);
					}
				}

				WriteChunk(output, "IDAT", compressed.ToArray());
			}

			WriteChunk(output, "IEND", Array.Empty<byte>());
			return output.ToArray();
		}

		#endregion

		#region PrivateMethods

		private static void WriteChunk(Stream output, string type, byte[] data)
		{
			byte[] length = new byte[4];
			WriteBigEndian(length, 0, (uint)data.Length);
			output.Write(length);
			byte[] typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
			output.Write(typeBytes);
			output.Write(data);
			uint crc = Crc(Crc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
			byte[] crcBytes = new byte[4];
			WriteBigEndian(crcBytes, 0, crc);
			output.Write(crcBytes);
		}

		private static uint Crc(uint crc, byte[] data)
		{
			foreach (byte b in data)
			{
				crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
			}

			return crc;
		}

		private static uint[] BuildCrcTable()
		{
			var table = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				uint c = n;
				for (int k = 0; k < 8; k++)
				{
					c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				}

				table[n] = c;
			}

			return table;
		}

		private static void WriteBigEndian(byte[] buffer, int offset, uint value)
		{
			buffer[offset] = (byte)(value >> 24);
			buffer[offset + 1] = (byte)(value >> 16);
			buffer[offset + 2] = (byte)(value >> 8);
			buffer[offset + 3] = (byte)value;
		}

		#endregion
	}
}
