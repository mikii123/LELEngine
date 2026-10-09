using System;
using System.IO;
using System.Runtime.InteropServices;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.Editor
{
	/// <summary>Operating system dialogs (the Windows shell folder picker).</summary>
	internal static class NativeDialogs
	{
		#region PrivateFields

		private const uint PickFolders = 0x20;
		private const uint ForceFileSystem = 0x40;
		private const uint PathMustExist = 0x800;
		private const uint FileSystemPath = 0x80058000;
		private const int Cancelled = unchecked((int)0x800704C7);

		#endregion

		#region PublicMethods

		/// <summary>True when <see cref="PickFolder" /> shows a system dialog on this platform.</summary>
		public static bool CanPickFolder => OperatingSystem.IsWindows();

		/// <summary>
		///     Shows the system folder picker, modal to the editor window, starting in <paramref name="initialFolder" />
		///     (or its closest existing parent). Returns the chosen folder, or null when cancelled.
		/// </summary>
		public static unsafe string PickFolder(EditorWindow owner, string title, string initialFolder)
		{
			if (!CanPickFolder)
			{
				return null;
			}

			IntPtr hwnd = owner != null ? GLFW.GetWin32Window(owner.WindowPtr) : IntPtr.Zero;
			var dialog = (IFileDialog)new FileOpenDialog();
			try
			{
				dialog.GetOptions(out uint options);
				dialog.SetOptions(options | PickFolders | ForceFileSystem | PathMustExist);
				dialog.SetTitle(title);
				string start = ExistingFolder(initialFolder);
				if (start != null)
				{
					Guid shellItem = typeof(IShellItem).GUID;
					if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref shellItem, out IShellItem folder) == 0)
					{
						dialog.SetFolder(folder);
						Marshal.ReleaseComObject(folder);
					}
				}

				int hr = dialog.Show(hwnd);
				if (hr == Cancelled)
				{
					return null;
				}

				Marshal.ThrowExceptionForHR(hr);
				dialog.GetResult(out IShellItem result);
				result.GetDisplayName(FileSystemPath, out string path);
				Marshal.ReleaseComObject(result);
				return path;
			}
			finally
			{
				Marshal.ReleaseComObject(dialog);
			}
		}

		#endregion

		#region PrivateMethods

		private static string ExistingFolder(string folder)
		{
			try
			{
				string current = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
				while (current != null && !Directory.Exists(current))
				{
					current = Path.GetDirectoryName(current);
				}

				return current;
			}
			catch (Exception)
			{
				return null;
			}
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
		private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IShellItem item);

		#endregion

		#region Nested

		[ComImport]
		[Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
		private class FileOpenDialog
		{
		}

		/// <summary>IFileDialog (IModalWindow first); only the methods up to GetResult are used.</summary>
		[ComImport]
		[Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IFileDialog
		{
			[PreserveSig]
			int Show(IntPtr parent);

			void SetFileTypes(uint count, IntPtr filters);
			void SetFileTypeIndex(uint index);
			void GetFileTypeIndex(out uint index);
			void Advise(IntPtr events, out uint cookie);
			void Unadvise(uint cookie);
			void SetOptions(uint options);
			void GetOptions(out uint options);
			void SetDefaultFolder(IShellItem item);
			void SetFolder(IShellItem item);
			void GetFolder(out IShellItem item);
			void GetCurrentSelection(out IShellItem item);
			void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
			void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
			void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
			void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
			void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
			void GetResult(out IShellItem item);
		}

		[ComImport]
		[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellItem
		{
			void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, out IntPtr result);
			void GetParent(out IShellItem parent);
			void GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string name);
		}

		#endregion
	}
}
