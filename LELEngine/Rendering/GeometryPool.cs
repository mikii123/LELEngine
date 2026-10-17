using System;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Every mesh's vertices and indices in two shared GPU buffers behind one vertex array, so any set of meshes
	///     can be drawn by a single multi-draw call, and a mesh used by many renderers is on the GPU once. A mesh
	///     uploads on first use and stays until the assets are cleared (<see cref="Clear" />); its sub-meshes are
	///     ranges of its index range (<see cref="DrawSubMesh" />).
	///     Location <see cref="VertexLayout.InstanceIndexLocation" /> is a per-instance attribute reading an identity
	///     buffer through the draw's base instance: the instance index of GPU-driven draws on contexts without shader
	///     draw parameters.
	/// </summary>
	public static class GeometryPool
	{
		#region PublicFields

		public static int MeshCount { get; private set; }
		public static int VertexCount => vertexCount;
		public static int IndexCount => indexCount;

		/// <summary>GL handle of the shared vertex buffer (readable as a storage buffer: <see cref="Vertex" /> layout, 18 floats per vertex).</summary>
		public static int VertexBufferHandle
		{
			get
			{
				EnsureCreated();
				return vertexBuffer;
			}
		}

		/// <summary>GL handle of the shared index buffer.</summary>
		public static int IndexBufferHandle
		{
			get
			{
				EnsureCreated();
				return indexBuffer;
			}
		}

		/// <summary>The mesh's range in the pool (after <see cref="Prepare" />): base vertex, vertex count, first index, index count.</summary>
		public static void GetRange(Mesh mesh, out int baseVertex, out int vertexCount, out int firstIndex, out int indexCount)
		{
			if (mesh == null || !Prepare(mesh))
			{
				baseVertex = 0;
				vertexCount = 0;
				firstIndex = 0;
				indexCount = 0;
				return;
			}

			baseVertex = mesh.PoolBaseVertex;
			vertexCount = mesh.PoolVertexCount;
			firstIndex = mesh.PoolFirstIndex;
			indexCount = mesh.PoolIndexCount;
		}

		#endregion

		#region PrivateFields

		private static int vertexArray;
		private static int vertexBuffer;
		private static int indexBuffer;
		private static int instanceIndexBuffer;
		private static int vertexCapacity;
		private static int indexCapacity;
		private static int instanceIndexCapacity;
		private static int vertexCount;
		private static int indexCount;
		private static int generation = 1;

		#endregion

		#region PublicMethods

		/// <summary>Uploads the mesh when it is not in the pool yet; false for meshes without triangles.</summary>
		public static bool Prepare(Mesh mesh)
		{
			if (mesh == null)
			{
				return false;
			}

			if (mesh.PoolGeneration != generation)
			{
				Upload(mesh);
			}

			return mesh.PoolIndexCount > 0;
		}

		/// <summary>Binds the pool's vertex array (all meshes, the standard attribute layout).</summary>
		public static void Bind()
		{
			EnsureCreated();
			GL.BindVertexArray(vertexArray);
		}

		/// <summary>Draws one mesh with the bound program (per-object path; the pool must be bound).</summary>
		public static void Draw(Mesh mesh)
		{
			if (!Prepare(mesh))
			{
				return;
			}

			GL.BindVertexArray(vertexArray);
			GL.DrawElementsBaseVertex(PrimitiveType.Triangles, mesh.PoolIndexCount, DrawElementsType.UnsignedInt, (IntPtr)(mesh.PoolFirstIndex * sizeof(uint)), mesh.PoolBaseVertex);
		}

		/// <summary>Draws one sub-mesh with the bound program (per-object path).</summary>
		public static void DrawSubMesh(Mesh mesh, int subMesh)
		{
			if (!Prepare(mesh) || subMesh < 0 || subMesh >= mesh.SubMeshCount)
			{
				return;
			}

			SubMesh range = mesh.SubMeshes[subMesh];
			if (range.IndexCount == 0)
			{
				return;
			}

			GL.BindVertexArray(vertexArray);
			GL.DrawElementsBaseVertex(PrimitiveType.Triangles, range.IndexCount, DrawElementsType.UnsignedInt, (IntPtr)((mesh.PoolFirstIndex + range.FirstIndex) * sizeof(uint)), mesh.PoolBaseVertex);
		}

		/// <summary>Draws one mesh as instance <paramref name="instance" /> of the GPU scene (instanced variants).</summary>
		public static void DrawInstance(Mesh mesh, int instance)
		{
			if (!Prepare(mesh))
			{
				return;
			}

			GL.BindVertexArray(vertexArray);
			GL.DrawElementsInstancedBaseVertexBaseInstance(PrimitiveType.Triangles, mesh.PoolIndexCount, DrawElementsType.UnsignedInt, (IntPtr)(mesh.PoolFirstIndex * sizeof(uint)), 1, mesh.PoolBaseVertex, instance);
		}

		/// <summary>The identity instance index buffer covers instances [0, count).</summary>
		public static void EnsureInstanceIndices(int count)
		{
			EnsureCreated();
			if (count <= instanceIndexCapacity)
			{
				return;
			}

			int capacity = Math.Max(1024, instanceIndexCapacity);
			while (capacity < count)
			{
				capacity *= 2;
			}

			var indices = new uint[capacity];
			for (int i = 0; i < capacity; i++)
			{
				indices[i] = (uint)i;
			}

			if (instanceIndexBuffer != 0)
			{
				GL.DeleteBuffer(instanceIndexBuffer);
			}

			instanceIndexBuffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, instanceIndexBuffer);
			GL.BufferData(BufferTarget.CopyWriteBuffer, capacity * sizeof(uint), indices, BufferUsageHint.StaticDraw);
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			instanceIndexCapacity = capacity;
			SetupVertexArray();
		}

		/// <summary>Forgets every mesh (the assets were cleared); the buffers are kept for reuse.</summary>
		public static void Clear()
		{
			generation++;
			vertexCount = 0;
			indexCount = 0;
			MeshCount = 0;
		}

		#endregion

		#region PrivateMethods

		private static void EnsureCreated()
		{
			if (vertexArray != 0)
			{
				return;
			}

			vertexArray = GL.GenVertexArray();
			vertexBuffer = Grow(0, 0, 64 * 1024 * Vertex.Size);
			vertexCapacity = 64 * 1024;
			indexBuffer = Grow(0, 0, 256 * 1024 * sizeof(uint));
			indexCapacity = 256 * 1024;
			EnsureInstanceIndices(1024);
			SetupVertexArray();
		}

		private static void Upload(Mesh mesh)
		{
			EnsureCreated();
			Vertex[] vertices = mesh.Vertices;
			uint[] indices = mesh.Indices;

			if (vertexCount + vertices.Length > vertexCapacity || indexCount + indices.Length > indexCapacity)
			{
				int newVertexCapacity = vertexCapacity;
				while (vertexCount + vertices.Length > newVertexCapacity)
				{
					newVertexCapacity *= 2;
				}

				int newIndexCapacity = indexCapacity;
				while (indexCount + indices.Length > newIndexCapacity)
				{
					newIndexCapacity *= 2;
				}

				if (newVertexCapacity != vertexCapacity)
				{
					vertexBuffer = Grow(vertexBuffer, vertexCount * Vertex.Size, newVertexCapacity * Vertex.Size);
					vertexCapacity = newVertexCapacity;
				}

				if (newIndexCapacity != indexCapacity)
				{
					indexBuffer = Grow(indexBuffer, indexCount * sizeof(uint), newIndexCapacity * sizeof(uint));
					indexCapacity = newIndexCapacity;
				}

				SetupVertexArray();
			}

			if (indices.Length > 0)
			{
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, vertexBuffer);
				GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)(vertexCount * Vertex.Size), vertices.Length * Vertex.Size, vertices);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, indexBuffer);
				GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)(indexCount * sizeof(uint)), indices.Length * sizeof(uint), indices);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			}

			mesh.PoolGeneration = generation;
			mesh.PoolFirstIndex = indexCount;
			mesh.PoolIndexCount = indices.Length;
			mesh.PoolBaseVertex = vertexCount;
			mesh.PoolVertexCount = vertices.Length;
			vertexCount += vertices.Length;
			indexCount += indices.Length;
			MeshCount++;
		}

		/// <summary>New buffer of the given size holding the first <paramref name="usedBytes" /> of the old one.</summary>
		private static int Grow(int buffer, int usedBytes, int capacityBytes)
		{
			int fresh = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, fresh);
			GL.BufferData(BufferTarget.CopyWriteBuffer, capacityBytes, IntPtr.Zero, BufferUsageHint.StaticDraw);
			if (buffer != 0)
			{
				if (usedBytes > 0)
				{
					GL.BindBuffer(BufferTarget.CopyReadBuffer, buffer);
					GL.CopyBufferSubData(BufferTarget.CopyReadBuffer, BufferTarget.CopyWriteBuffer, IntPtr.Zero, IntPtr.Zero, usedBytes);
					GL.BindBuffer(BufferTarget.CopyReadBuffer, 0);
				}

				GL.DeleteBuffer(buffer);
			}

			GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			return fresh;
		}

		private static void SetupVertexArray()
		{
			if (vertexArray == 0 || vertexBuffer == 0 || indexBuffer == 0)
			{
				return;
			}

			GL.BindVertexArray(vertexArray);
			GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
			foreach (VertexAttribute attribute in VertexLayout.CreateStandardAttributes())
			{
				attribute.Set();
			}

			if (instanceIndexBuffer != 0)
			{
				GL.BindBuffer(BufferTarget.ArrayBuffer, instanceIndexBuffer);
				GL.EnableVertexAttribArray(VertexLayout.InstanceIndexLocation);
				GL.VertexAttribIPointer(VertexLayout.InstanceIndexLocation, 1, VertexAttribIntegerType.UnsignedInt, sizeof(uint), IntPtr.Zero);
				GL.VertexAttribDivisor(VertexLayout.InstanceIndexLocation, 1);
			}

			// The element buffer binding is vertex array state.
			GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
			GL.BindVertexArray(0);
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
		}

		#endregion
	}
}
