using System;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What the current OpenGL context offers beyond the 4.3 baseline the engine requires. Queried once after
	///     context creation; passes use it to pick faster paths (texture clears, anisotropic filtering) and fall
	///     back to 4.3 code otherwise.
	/// </summary>
	public static class GLCapabilities
	{
		#region PublicFields

		public static int Major { get; private set; }
		public static int Minor { get; private set; }

		/// <summary>glClearTexImage / glClearTexSubImage (OpenGL 4.4).</summary>
		public static bool ClearTexture { get; private set; }

		/// <summary>Direct state access (OpenGL 4.5).</summary>
		public static bool DirectStateAccess { get; private set; }

		/// <summary>Anisotropic texture filtering (OpenGL 4.6 core, or EXT_texture_filter_anisotropic).</summary>
		public static bool AnisotropicFiltering { get; private set; }

		public static float MaxAnisotropy { get; private set; } = 1f;

		public static int MaxImageUnits { get; private set; }

		public static bool Initialized { get; private set; }

		#endregion

		#region PublicMethods

		public static void Initialize()
		{
			Major = GL.GetInteger(GetPName.MajorVersion);
			Minor = GL.GetInteger(GetPName.MinorVersion);
			ClearTexture = IsAtLeast(4, 4);
			DirectStateAccess = IsAtLeast(4, 5);
			AnisotropicFiltering = IsAtLeast(4, 6) || HasExtension("GL_EXT_texture_filter_anisotropic") || HasExtension("GL_ARB_texture_filter_anisotropic");
			if (AnisotropicFiltering)
			{
				// GL_MAX_TEXTURE_MAX_ANISOTROPY (same value in core 4.6 and the EXT extension).
				MaxAnisotropy = GL.GetFloat((GetPName)0x84FF);
			}
			MaxImageUnits = GL.GetInteger((GetPName)0x8F38);
			Initialized = true;

			Console.WriteLine($"[GL] {Major}.{Minor} | clear texture {(ClearTexture ? "yes" : "no")} | DSA {(DirectStateAccess ? "yes" : "no")} | anisotropy {(AnisotropicFiltering ? MaxAnisotropy.ToString("0") + "x" : "no")} | image units {MaxImageUnits}");
		}

		public static bool IsAtLeast(int major, int minor)
		{
			return Major > major || (Major == major && Minor >= minor);
		}

		#endregion

		#region PrivateMethods

		private static bool HasExtension(string name)
		{
			int count = GL.GetInteger(GetPName.NumExtensions);
			for (int i = 0; i < count; i++)
			{
				if (GL.GetString(StringNameIndexed.Extensions, i) == name)
				{
					return true;
				}
			}

			return false;
		}

		#endregion
	}
}
