using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace LELEngine
{
	/// <summary>Deletes files and folders to the Recycle Bin on Windows (they can be restored), for good elsewhere.</summary>
	internal static class RecycleBin
	{
		#region PrivateFields

		private const uint Delete = 3;
		private const ushort Silent = 0x4;
		private const ushort NoConfirmation = 0x10;
		private const ushort AllowUndo = 0x40;
		private const ushort NoErrorUI = 0x400;

		#endregion

		#region PublicMethods

		/// <summary>True when <see cref="Send" /> keeps what it deletes in the Recycle Bin on this platform.</summary>
		public static bool Available => OperatingSystem.IsWindows();

		/// <summary>Deletes the files and folders (absolute paths, folders with everything in them). Throws an IOException when it fails.</summary>
		public static void Send(IReadOnlyList<string> paths)
		{
			if (paths.Count == 0)
			{
				return;
			}

			if (!Available)
			{
				foreach (string path in paths)
				{
					if (Directory.Exists(path))
					{
						Directory.Delete(path, true);
					}
					else if (File.Exists(path))
					{
						File.Delete(path);
					}
				}

				return;
			}

			// The shell's list: full paths, each ended by a null, the list by another.
			var from = new System.Text.StringBuilder();
			foreach (string path in paths)
			{
				from.Append(Path.GetFullPath(path)).Append('\0');
			}

			from.Append('\0');
			var operation = new FileOperation
			{
				Function = Delete,
				From = from.ToString(),
				Flags = (ushort)(AllowUndo | NoConfirmation | NoErrorUI | Silent)
			};

			int result = SHFileOperationW(ref operation);
			if (result != 0 || operation.AnyOperationsAborted)
			{
				throw new IOException("The shell could not move " + string.Join(", ", paths) + " to the Recycle Bin (error 0x" + result.ToString("X") + "); a program may hold it open.");
			}

			foreach (string path in paths)
			{
				if (File.Exists(path) || Directory.Exists(path))
				{
					throw new IOException(path + " is still there after the delete; a program may hold it open.");
				}
			}
		}

		#endregion

		#region PrivateMethods

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		private struct FileOperation
		{
			public IntPtr Window;
			public uint Function;
			public string From;
			public string To;
			public ushort Flags;
			[MarshalAs(UnmanagedType.Bool)] public bool AnyOperationsAborted;
			public IntPtr NameMappings;
			public string ProgressTitle;
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
		private static extern int SHFileOperationW(ref FileOperation operation);

		#endregion
	}
}
