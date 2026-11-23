using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	// LEGACY(gl): used by LELEngine.Editor/UIBuilder/BuilderPreview.cs
	/// <summary>
	///     For the code that still gives OpenGL commands itself: the bindings it expects to start from.
	/// </summary>
	public static class GLState
	{
		#region PublicMethods

		/// <summary>
		///     Unbinds buffers, textures and programs so the next draw starts from a known state.
		/// </summary>
		public static void Reset()
		{
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
			GL.ActiveTexture(TextureUnit.Texture0);
			GL.BindTexture(TextureTarget.Texture2D, 0);
			GL.UseProgram(0);
		}

		#endregion
	}
}
