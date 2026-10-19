using System;
using System.Runtime.InteropServices;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Whether this thread can make GL calls: a GL context is current on it. Assets loaded on a thread without one
	///     (<see cref="SceneManager.LoadSceneAsync" />) keep their GL work for the thread that holds the context: shader
	///     programs compile on first use (<see cref="Shaders.ShaderProgram.EnsureBuilt" />), textures and meshes upload
	///     on first use anyway.
	/// </summary>
	public static class GraphicsThread
	{
		#region PublicFields

		/// <summary>A GL context is current on the calling thread.</summary>
		public static bool HasContext => GetCurrentContext() != IntPtr.Zero;

		#endregion

		#region PrivateMethods

		/// <summary>The GLFW window whose context is current on the calling thread (any thread may ask).</summary>
		[DllImport("glfw3", EntryPoint = "glfwGetCurrentContext")]
		private static extern IntPtr GetCurrentContext();

		#endregion
	}
}
