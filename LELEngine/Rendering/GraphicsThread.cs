using OpenTK.Windowing.GraphicsLibraryFramework;

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

		/// <summary>
		///     A GL context is current on the calling thread: the GLFW window whose context it is (any thread may ask),
		///     through OpenTK's binding, which loads the GLFW library of each platform.
		/// </summary>
		public static unsafe bool HasContext => GLFW.GetCurrentContext() != null;

		#endregion
	}
}
