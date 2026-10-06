using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Small helpers for commonly toggled pipeline state so passes stay readable.
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

		public static void SetDepth(bool test, bool write, DepthFunction function = DepthFunction.Less)
		{
			if (test)
			{
				GL.Enable(EnableCap.DepthTest);
			}
			else
			{
				GL.Disable(EnableCap.DepthTest);
			}

			GL.DepthMask(write);
			GL.DepthFunc(function);
		}

		public static void SetCull(bool enabled, TriangleFace face = TriangleFace.Back)
		{
			if (enabled)
			{
				GL.Enable(EnableCap.CullFace);
				GL.CullFace(face);
			}
			else
			{
				GL.Disable(EnableCap.CullFace);
			}
		}

		public static void SetColorWrite(bool enabled)
		{
			GL.ColorMask(enabled, enabled, enabled, enabled);
		}

		public static void SetBlend(bool enabled)
		{
			if (enabled)
			{
				GL.Enable(EnableCap.Blend);
				GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
			}
			else
			{
				GL.Disable(EnableCap.Blend);
			}
		}

		#endregion
	}
}
