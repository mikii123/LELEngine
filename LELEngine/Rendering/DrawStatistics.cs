namespace LELEngine.Rendering
{
	/// <summary>
	///     What the engine has asked the GPU to draw since the process started: every draw call the engine makes adds to these
	///     counters (on the GL thread, where draws are made). A renderer keeps what its last frame added
	///     (<see cref="Renderer.LastFrame" />). An indirect draw (the GPU-driven geometry's multi-draws, particle outputs) is
	///     one draw call whose count the GPU decides: its triangles are not known here and are not in <see cref="Triangles" />.
	/// </summary>
	public static class DrawStatistics
	{
		#region PublicProperties

		/// <summary>Draw calls (a multi-draw is one).</summary>
		public static long DrawCalls { get; private set; }

		/// <summary>Triangles of the draw calls whose counts the CPU gives (points and lines add none).</summary>
		public static long Triangles { get; private set; }

		/// <summary>Draw calls whose counts the GPU decides (indirect draws and multi-draws), also in <see cref="DrawCalls" />.</summary>
		public static long IndirectDrawCalls { get; private set; }

		#endregion

		#region InternalMethods

		/// <summary>A draw call of <paramref name="triangles" /> triangles (0 for points and lines).</summary>
		internal static void Draw(int triangles)
		{
			DrawCalls++;
			Triangles += triangles;
		}

		/// <summary>A draw call whose count the GPU decides.</summary>
		internal static void DrawIndirect()
		{
			DrawCalls++;
			IndirectDrawCalls++;
		}

		#endregion
	}
}
