using System;
using System.Collections.Generic;
using LELEngine.Shaders;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Every mesh's vertices and indices in two shared GPU buffers behind one vertex array, so any set of meshes
	///     can be drawn by a single multi-draw call, and a mesh used by many renderers is on the GPU once. A mesh
	///     uploads on first use and stays until its asset is unloaded (<see cref="Release" />: later meshes reuse its
	///     ranges) or the assets are cleared (<see cref="Clear" />); its sub-meshes are ranges of its index range
	///     (<see cref="DrawSubMesh" />).
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

		// Ranges given back by released meshes (sorted by start; the end of the used space is vertexCount / indexCount).
		private static readonly List<(int Start, int Count)> freeVertices = new List<(int Start, int Count)>();
		private static readonly List<(int Start, int Count)> freeIndices = new List<(int Start, int Count)>();

		// DrawRanges' arguments (GL thread).
		private static int[] drawCounts = new int[64];
		private static IntPtr[] drawOffsets = new IntPtr[64];
		private static int[] drawBaseVertices = new int[64];

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
				long started = FrameProfiler.Timestamp;
				Upload(mesh);
				// Only with the hitch profiler on (its text is not built otherwise).
				if (started != 0)
				{
					FrameProfiler.EventSince(started, "mesh upload", (mesh.AssetPath ?? "") + "#" + (mesh.SubAssetName ?? mesh.name), mesh.Vertices.Length + " vertices, " + mesh.TriangleCount + " triangles");
				}
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
			DrawStatistics.Draw(mesh.PoolIndexCount / 3);
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
			DrawStatistics.Draw(range.IndexCount / 3);
		}

		/// <summary>
		///     Draws <paramref name="count" /> ranges of a mesh's triangles in their order with one call (a multi-draw):
		///     ranges[2 i] is the first index of range i in <see cref="Mesh.Indices" />, ranges[2 i + 1] its index count.
		///     How the transparent pass draws the parts of a static batch that follow each other back to front.
		/// </summary>
		public static void DrawRanges(Mesh mesh, int[] ranges, int count)
		{
			if (count <= 0 || !Prepare(mesh))
			{
				return;
			}

			if (drawCounts.Length < count)
			{
				int size = Math.Max(count, drawCounts.Length * 2);
				drawCounts = new int[size];
				drawOffsets = new IntPtr[size];
				drawBaseVertices = new int[size];
			}

			int triangles = 0;
			for (int i = 0; i < count; i++)
			{
				drawCounts[i] = ranges[i * 2 + 1];
				drawOffsets[i] = (IntPtr)((long)(mesh.PoolFirstIndex + ranges[i * 2]) * sizeof(uint));
				drawBaseVertices[i] = mesh.PoolBaseVertex;
				triangles += ranges[i * 2 + 1] / 3;
			}

			GL.BindVertexArray(vertexArray);
			GL.MultiDrawElementsBaseVertex(PrimitiveType.Triangles, drawCounts, DrawElementsType.UnsignedInt, drawOffsets, count, drawBaseVertices);
			DrawStatistics.Draw(triangles);
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
			DrawStatistics.Draw(mesh.PoolIndexCount / 3);
		}

		/// <summary>
		///     Shows or hides a range of a mesh's triangles on the GPU (<paramref name="firstIndex" />,
		///     <paramref name="indexCount" /> of <see cref="Mesh.Indices" />, whole triangles): a hidden range's indices all
		///     name one vertex, so its triangles have no area and nothing of them is drawn, in any pass, whatever draws the
		///     mesh (its own draw, a sub-mesh's, a multi-draw). The mesh keeps its indices; the hidden ranges are the mesh's
		///     (<see cref="Mesh.HiddenIndexRanges" />) and hold when it uploads again. A static batch hides the parts whose
		///     objects were disabled or destroyed this way (<see cref="StaticBatch" />). GL thread.
		/// </summary>
		public static void SetIndexRangeVisible(Mesh mesh, int firstIndex, int indexCount, bool visible)
		{
			if (mesh == null || indexCount <= 0 || firstIndex < 0 || firstIndex + indexCount > mesh.Indices.Length)
			{
				return;
			}

			List<(int First, int Count)> hidden = mesh.HiddenIndexRanges;
			int at = hidden != null ? hidden.IndexOf((firstIndex, indexCount)) : -1;
			if (visible == at < 0)
			{
				return;
			}

			if (visible)
			{
				hidden.RemoveAt(at);
				if (hidden.Count == 0)
				{
					mesh.HiddenIndexRanges = null;
				}
			}
			else
			{
				(mesh.HiddenIndexRanges ??= new List<(int, int)>()).Add((firstIndex, indexCount));
			}

			// Not in the pool now (or no context: the process is ending): its next upload writes the ranges as they are then.
			if (mesh.PoolGeneration == generation && mesh.PoolIndexCount > 0 && GraphicsThread.HasContext)
			{
				WriteIndexRange(mesh, firstIndex, indexCount, !visible);
			}
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
			freeVertices.Clear();
			freeIndices.Clear();
		}

		/// <summary>
		///     Gives a mesh's ranges back for the meshes uploaded later (its asset was unloaded with its last scene). The
		///     mesh stays usable: drawn again, it uploads again.
		/// </summary>
		public static void Release(Mesh mesh)
		{
			if (mesh == null)
			{
				return;
			}

			// A mirrored twin draws this mesh's vertices: it goes with them.
			if (mesh.MirroredIfMade != null)
			{
				Release(mesh.MirroredIfMade);
			}

			if (mesh.PoolGeneration != generation)
			{
				return;
			}

			mesh.PoolGeneration = 0;
			Free(freeVertices, ref vertexCount, mesh.PoolBaseVertex, mesh.PoolVertexCount);
			Free(freeIndices, ref indexCount, mesh.PoolFirstIndex, mesh.PoolIndexCount);
			MeshCount--;
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
			// A mirrored twin (Mesh.Mirrored) is only an index range over its source's vertices.
			Mesh source = mesh.MirrorSource;
			if (source != null && !Prepare(source))
			{
				source = null;
			}

			Vertex[] vertices = source != null ? Array.Empty<Vertex>() : mesh.Vertices;
			uint[] indices = mesh.Indices;

			// Ranges of released meshes first (first fit), the end of the buffers otherwise.
			int baseVertex = indices.Length > 0 && source == null ? Allocate(freeVertices, vertices.Length) : -1;
			int firstIndex = indices.Length > 0 ? Allocate(freeIndices, indices.Length) : -1;
			bool appendVertices = baseVertex < 0 && source == null;
			bool appendIndices = firstIndex < 0;
			if (source != null)
			{
				baseVertex = source.PoolBaseVertex;
			}
			else if (appendVertices)
			{
				baseVertex = vertexCount;
			}

			if (appendIndices)
			{
				firstIndex = indexCount;
			}

			int vertexEnd = appendVertices ? vertexCount + vertices.Length : vertexCount;
			int indexEnd = appendIndices ? indexCount + indices.Length : indexCount;
			if (vertexEnd > vertexCapacity || indexEnd > indexCapacity)
			{
				int newVertexCapacity = vertexCapacity;
				while (vertexEnd > newVertexCapacity)
				{
					newVertexCapacity *= 2;
				}

				int newIndexCapacity = indexCapacity;
				while (indexEnd > newIndexCapacity)
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
				if (vertices.Length > 0)
				{
					GL.BindBuffer(BufferTarget.CopyWriteBuffer, vertexBuffer);
					GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)(baseVertex * Vertex.Size), vertices.Length * Vertex.Size, vertices);
				}

				GL.BindBuffer(BufferTarget.CopyWriteBuffer, indexBuffer);
				GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)(firstIndex * sizeof(uint)), indices.Length * sizeof(uint), indices);
				GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
			}

			mesh.PoolGeneration = generation;
			mesh.PoolFirstIndex = firstIndex;
			mesh.PoolIndexCount = indices.Length;
			mesh.PoolBaseVertex = baseVertex;
			mesh.PoolVertexCount = vertices.Length;
			vertexCount = vertexEnd;
			indexCount = indexEnd;
			MeshCount++;

			// The ranges hidden while the mesh was not in the pool (or before it was first drawn).
			if (mesh.HiddenIndexRanges != null && indices.Length > 0)
			{
				foreach ((int first, int count) in mesh.HiddenIndexRanges)
				{
					WriteIndexRange(mesh, first, count, true);
				}
			}
		}

		// A range of an uploaded mesh's indices: its own, or (hidden) every index the range's first, which leaves
		// triangles without area.
		private static void WriteIndexRange(Mesh mesh, int first, int count, bool hidden)
		{
			uint[] indices = mesh.Indices;
			GL.BindBuffer(BufferTarget.CopyWriteBuffer, indexBuffer);
			if (hidden)
			{
				var collapsed = new uint[count];
				Array.Fill(collapsed, indices[first]);
				GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)((mesh.PoolFirstIndex + first) * sizeof(uint)), count * sizeof(uint), collapsed);
			}
			else
			{
				GL.BufferSubData(BufferTarget.CopyWriteBuffer, (IntPtr)((mesh.PoolFirstIndex + first) * sizeof(uint)), count * sizeof(uint), ref indices[first]);
			}

			GL.BindBuffer(BufferTarget.CopyWriteBuffer, 0);
		}

		/// <summary>The start of a free range of <paramref name="count" /> elements (taken from the list), or -1.</summary>
		private static int Allocate(List<(int Start, int Count)> free, int count)
		{
			for (int i = 0; i < free.Count; i++)
			{
				(int start, int available) = free[i];
				if (available < count)
				{
					continue;
				}

				if (available == count)
				{
					free.RemoveAt(i);
				}
				else
				{
					free[i] = (start + count, available - count);
				}

				return start;
			}

			return -1;
		}

		/// <summary>Adds a range to the sorted free list, merging neighbours; free space at the end lowers the end instead.</summary>
		private static void Free(List<(int Start, int Count)> free, ref int end, int start, int count)
		{
			if (count <= 0)
			{
				return;
			}

			int i = 0;
			while (i < free.Count && free[i].Start < start)
			{
				i++;
			}

			free.Insert(i, (start, count));
			if (i + 1 < free.Count && free[i].Start + free[i].Count == free[i + 1].Start)
			{
				free[i] = (free[i].Start, free[i].Count + free[i + 1].Count);
				free.RemoveAt(i + 1);
			}

			if (i > 0 && free[i - 1].Start + free[i - 1].Count == free[i].Start)
			{
				free[i - 1] = (free[i - 1].Start, free[i - 1].Count + free[i].Count);
				free.RemoveAt(i);
			}

			(int lastStart, int lastCount) = free[free.Count - 1];
			if (lastStart + lastCount == end)
			{
				end = lastStart;
				free.RemoveAt(free.Count - 1);
			}
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
