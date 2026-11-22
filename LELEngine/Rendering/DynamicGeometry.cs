using System;
using LELEngine.Shaders;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Triangles rebuilt every frame (trails, lines): one vertex and one index buffer in the standard vertex
	///     layout, whose storage is replaced at every upload, drawn in ranges.
	/// </summary>
	internal sealed class DynamicGeometry
	{
		#region PrivateFields

		private GpuBuffer vertexBuffer;
		private GpuBuffer indexBuffer;
		private int vertexCapacity;
		private int indexCapacity;

		#endregion

		#region PublicMethods

		/// <summary>Replaces the stream's contents.</summary>
		public void Upload(Vertex[] vertices, int vertexCount, uint[] indices, int indexCount)
		{
			if (vertexCount == 0 || indexCount == 0)
			{
				return;
			}

			EnsureCreated();
			vertexCapacity = Math.Max(vertexCapacity, vertexCount);
			indexCapacity = Math.Max(indexCapacity, indexCount);
			vertexBuffer.SetData(new ReadOnlySpan<Vertex>(vertices, 0, vertexCount), (long)vertexCapacity * Vertex.Size);
			indexBuffer.SetData(new ReadOnlySpan<uint>(indices, 0, indexCount), (long)indexCapacity * sizeof(uint));
		}

		/// <summary>Draws a range of the uploaded indices with the program in use.</summary>
		public void Draw(int firstIndex, int indexCount)
		{
			if (vertexBuffer == null || indexCount == 0)
			{
				return;
			}

			CommandList commands = GraphicsDevice.Current.Commands;
			commands.SetVertexInput(VertexLayout.Standard);
			commands.SetVertexBuffer(0, vertexBuffer);
			commands.SetIndexBuffer(indexBuffer);
			commands.DrawIndexed(indexCount, firstIndex);
			DrawStatistics.Draw(indexCount / 3);
		}

		public void Delete()
		{
			vertexBuffer?.Dispose();
			indexBuffer?.Dispose();
			vertexBuffer = indexBuffer = null;
			vertexCapacity = indexCapacity = 0;
		}

		#endregion

		#region PrivateMethods

		private void EnsureCreated()
		{
			if (vertexBuffer != null)
			{
				return;
			}

			GraphicsDevice device = GraphicsDevice.Current;
			vertexBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Vertex, 0, true, "Dynamic geometry vertices"));
			indexBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Index, 0, true, "Dynamic geometry indices"));
		}

		#endregion
	}
}
