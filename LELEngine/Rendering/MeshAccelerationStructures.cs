using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     The meshes of the geometry pool as bottom level acceleration structures, for what traces them with ray queries
	///     (<see cref="GraphicsCapabilities.RayQueries" />): one per mesh, of all its triangles where the pool holds them
	///     (the positions of the pool's vertices, its 32 bit indices, hidden ranges as they are on the GPU), made the first
	///     time it is asked for and built with the others asked for before the next <see cref="BuildPending" /> in one
	///     command; compacted once the GPU has told how small it can be (<see cref="Compact" />), built again when the
	///     mesh's triangles change on the GPU, freed when the mesh leaves the pool. Graphics thread only.
	/// </summary>
	public static class MeshAccelerationStructures
	{
		#region PublicFields

		/// <summary>
		///     Counts the changes of every mesh's structure (made, built again, compacted, freed): what holds them (a top
		///     level) builds again when it differs.
		/// </summary>
		public static int Version { get; private set; }

		/// <summary>Meshes with a structure.</summary>
		public static int Count => entries.Count;

		/// <summary>Bytes of the structures on the GPU.</summary>
		public static long Bytes { get; private set; }

		#endregion

		#region PrivateFields

		/// <summary>A compacted copy is made when it saves at least this share of the structure.</summary>
		private const float CompactionSaving = 0.125f;

		private static readonly Dictionary<Mesh, Entry> entries = new Dictionary<Mesh, Entry>();
		private static readonly List<GpuAccelerationStructure> pending = new List<GpuAccelerationStructure>();
		private static readonly List<Mesh> pendingMeshes = new List<Mesh>();
		private static readonly List<Mesh> compacting = new List<Mesh>();
		private static long pendingTriangles;

		#endregion

		#region PublicMethods

		/// <summary>
		///     The bottom level of a mesh's triangles in the pool (uploaded when it is not there yet); null for a mesh
		///     without triangles. A structure made (or made again) now is built by the next <see cref="BuildPending" />,
		///     which has to come before anything traces it.
		/// </summary>
		public static GpuAccelerationStructure Get(Mesh mesh)
		{
			if (!GeometryPool.Prepare(mesh))
			{
				return null;
			}

			GeometryPool.GetRange(mesh, out int baseVertex, out _, out int firstIndex, out int indexCount);
			if (entries.TryGetValue(mesh, out Entry entry))
			{
				if (!entry.Stale && entry.FirstIndex == firstIndex && entry.BaseVertex == baseVertex && entry.IndexCount == indexCount)
				{
					return entry.Structure;
				}

				// The triangles moved in the pool or changed there: a structure of them as they are now.
				Free(entry);
				entries.Remove(mesh);
			}

			AccelerationStructureTriangles[] triangles =
			{
				new AccelerationStructureTriangles
				{
					VertexBuffer = GeometryPool.VertexBuffer,
					VertexOffset = (long)baseVertex * Vertex.Size,
					VertexStride = Vertex.Size,
					// A mirrored twin shares its source's vertices, which its indices name.
					VertexCount = mesh.Vertices.Length,
					PositionFormat = VertexFormat.Float3,
					IndexBuffer = GeometryPool.IndexBuffer,
					IndexOffset = (long)firstIndex * sizeof(uint),
					IndexFormat = IndexFormat.UInt32,
					TriangleCount = indexCount / 3,
					Opaque = true
				}
			};
			string name = (mesh.SubAssetName ?? mesh.name) + " bottom level";
			GpuAccelerationStructure structure = Gfx.Device.CreateAccelerationStructure(AccelerationStructureDescriptor.BottomLevel(triangles,
				AccelerationStructureFlags.PreferFastTrace | AccelerationStructureFlags.AllowCompaction, name));
			entry = new Entry { Structure = structure, FirstIndex = firstIndex, BaseVertex = baseVertex, IndexCount = indexCount };
			entries.Add(mesh, entry);
			pending.Add(structure);
			pendingMeshes.Add(mesh);
			pendingTriangles += indexCount / 3;
			Bytes += structure.Size;
			Version++;
			return structure;
		}

		/// <summary>Builds the structures made since the last call, in one command (it ends an open pass).</summary>
		public static void BuildPending()
		{
			if (pending.Count == 0)
			{
				return;
			}

			Gfx.Commands.BuildBottomLevels(CollectionsMarshal.AsSpan(pending));
			Console.WriteLine($"[RayTracing] Bottom levels of {pending.Count} meshes built ({pendingTriangles:N0} triangles): {Bytes / 1048576.0:0.00} MB for {entries.Count} meshes");
			// Their compacted sizes are read once the GPU has run the builds.
			compacting.AddRange(pendingMeshes);
			pending.Clear();
			pendingMeshes.Clear();
			pendingTriangles = 0;
		}

		/// <summary>
		///     Puts a compacted copy in the place of each structure whose compacted size the GPU has told (after its build
		///     ran), when it saves at least an eighth; the others stay as they are. Changes <see cref="Version" /> when it
		///     swaps one.
		/// </summary>
		public static void Compact()
		{
			if (compacting.Count == 0)
			{
				return;
			}

			long before = Bytes;
			int swapped = 0;
			for (int i = compacting.Count - 1; i >= 0; i--)
			{
				Mesh mesh = compacting[i];
				if (!entries.TryGetValue(mesh, out Entry entry) || entry.Compacted)
				{
					compacting.RemoveAt(i);
					continue;
				}

				GpuAccelerationStructure structure = entry.Structure;
				long compacted = structure.IsBuilt ? structure.CompactedSize : 0;
				if (compacted <= 0)
				{
					// The GPU has not run the build yet.
					continue;
				}

				compacting.RemoveAt(i);
				entry.Compacted = true;
				if (compacted > structure.Size * (1f - CompactionSaving))
				{
					continue;
				}

				GpuAccelerationStructure copy = Gfx.Device.CreateAccelerationStructure(AccelerationStructureDescriptor.CompactCopyOf(structure));
				Gfx.Commands.CopyAccelerationStructure(structure, copy);
				Bytes += copy.Size - structure.Size;
				structure.Dispose();
				entry.Structure = copy;
				swapped++;
			}

			if (swapped > 0)
			{
				Version++;
				Console.WriteLine($"[RayTracing] {swapped} bottom levels compacted: {before / 1048576.0:0.00} MB -> {Bytes / 1048576.0:0.00} MB for {entries.Count} meshes");
			}
		}

		/// <summary>The mesh's triangles changed on the GPU (a range hidden or shown): its structure is made again when next asked for.</summary>
		public static void Invalidate(Mesh mesh)
		{
			if (mesh != null && entries.TryGetValue(mesh, out Entry entry))
			{
				entry.Stale = true;
			}
		}

		/// <summary>Frees the mesh's structure (its ranges left the pool).</summary>
		public static void Release(Mesh mesh)
		{
			if (mesh != null && entries.Remove(mesh, out Entry entry))
			{
				Free(entry);
			}
		}

		/// <summary>Frees every structure: the pool forgot its meshes, or nothing traces them any more.</summary>
		public static void Clear()
		{
			if (entries.Count == 0)
			{
				return;
			}

			foreach (Entry entry in entries.Values)
			{
				Free(entry);
			}

			entries.Clear();
			compacting.Clear();
			pendingMeshes.Clear();
			Console.WriteLine("[RayTracing] Bottom levels released");
		}

		#endregion

		#region PrivateMethods

		private static void Free(Entry entry)
		{
			pending.Remove(entry.Structure);
			Bytes -= entry.Structure.Size;
			entry.Structure.Dispose();
			Version++;
		}

		#endregion

		#region NestedTypes

		private sealed class Entry
		{
			public GpuAccelerationStructure Structure;

			/// <summary>The mesh's ranges in the pool when the structure was made.</summary>
			public int FirstIndex;

			public int BaseVertex;
			public int IndexCount;

			/// <summary>The mesh's triangles changed on the GPU since.</summary>
			public bool Stale;

			/// <summary>Its compacted size was read (and the copy made where it saved enough).</summary>
			public bool Compacted;
		}

		#endregion
	}
}
