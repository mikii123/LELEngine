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
	///     command: everything at once in the first frame after a load (<see cref="BuildEverythingNext" />), later at most
	///     <see cref="TrianglesPerFrame" /> a frame, so a mesh that appears in play waits a frame or two instead of stopping
	///     one (<see cref="GetBuilt" />); compacted once the GPU has told how small it can be (<see cref="Compact" />), built
	///     again when the mesh's triangles change on the GPU, freed when the mesh leaves the pool. Made with data access: the
	///     rays read their hit triangles' corners from them where the device fetches positions. Graphics thread only.
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
		private static readonly List<Entry> pending = new List<Entry>();
		private static readonly List<GpuAccelerationStructure> building = new List<GpuAccelerationStructure>();
		private static readonly List<Mesh> pendingMeshes = new List<Mesh>();
		private static readonly List<Mesh> compacting = new List<Mesh>();
		private static bool buildEverything;
		private static int frameBuilds;

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
			// Data access: the rays read the hit triangle's corners from it where the device fetches positions.
			GpuAccelerationStructure structure = Gfx.Device.CreateAccelerationStructure(AccelerationStructureDescriptor.BottomLevel(triangles,
				AccelerationStructureFlags.PreferFastTrace | AccelerationStructureFlags.AllowCompaction | AccelerationStructureFlags.AllowDataAccess, name));
			entry = new Entry { Structure = structure, FirstIndex = firstIndex, BaseVertex = baseVertex, IndexCount = indexCount, Triangles = indexCount / 3 };
			entries.Add(mesh, entry);
			pending.Add(entry);
			pendingMeshes.Add(mesh);
			Bytes += structure.Size;
			Version++;
			return structure;
		}

		/// <summary>
		///     The bottom level of a mesh when it is built (or its build is recorded) by now: null while it waits for a later
		///     frame's <see cref="BuildPending" /> (a frame builds <see cref="TrianglesPerFrame" /> at most), and for a mesh
		///     without triangles. What traces the scene leaves such a mesh out until then.
		/// </summary>
		public static GpuAccelerationStructure GetBuilt(Mesh mesh)
		{
			GpuAccelerationStructure structure = Get(mesh);
			return structure != null && entries.TryGetValue(mesh, out Entry entry) && entry.Recorded ? structure : null;
		}

		/// <summary>
		///     Triangles whose bottom levels one frame builds at most, the smallest meshes first; what is left waits for the next
		///     frames (a mesh that appears in play does not stop a frame). The frames after a load
		///     (<see cref="BuildEverythingNext" />) build all they are given.
		/// </summary>
		public static long TrianglesPerFrame { get; set; } = 250000;

		/// <summary>The next <see cref="BuildPending" /> builds everything that waits, whatever its size: a scene was loaded (its first frame is the load's).</summary>
		public static void BuildEverythingNext()
		{
			buildEverything = true;
		}

		/// <summary>
		///     Builds structures made since the last call, in one command (it ends an open pass): all of them after a load
		///     (<see cref="BuildEverythingNext" />, the first frame), else up to <see cref="TrianglesPerFrame" /> (at least
		///     one), smallest first.
		/// </summary>
		public static void BuildPending()
		{
			if (pending.Count == 0)
			{
				buildEverything = false;
				return;
			}

			building.Clear();
			long triangles = 0;
			if (buildEverything || frameBuilds == 0)
			{
				foreach (Entry entry in pending)
				{
					building.Add(entry.Structure);
					entry.Recorded = true;
					triangles += entry.Triangles;
				}

				pending.Clear();
			}
			else
			{
				pending.Sort((a, b) => a.Triangles.CompareTo(b.Triangles));
				int taken = 0;
				while (taken < pending.Count && (taken == 0 || triangles + pending[taken].Triangles <= TrianglesPerFrame))
				{
					building.Add(pending[taken].Structure);
					pending[taken].Recorded = true;
					triangles += pending[taken].Triangles;
					taken++;
				}

				pending.RemoveRange(0, taken);
			}

			buildEverything = false;
			frameBuilds++;
			Gfx.Commands.BuildBottomLevels(CollectionsMarshal.AsSpan(building));
			Console.WriteLine($"[RayTracing] Bottom levels of {building.Count} meshes built ({triangles:N0} triangles){(pending.Count > 0 ? $", {pending.Count} wait for the next frames" : "")}: {Bytes / 1048576.0:0.00} MB for {entries.Count} meshes");
			// Their compacted sizes are read once the GPU has run the builds.
			for (int i = pendingMeshes.Count - 1; i >= 0; i--)
			{
				if (entries.TryGetValue(pendingMeshes[i], out Entry entry) && entry.Recorded)
				{
					compacting.Add(pendingMeshes[i]);
					pendingMeshes.RemoveAt(i);
				}
			}

			building.Clear();
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
			pending.Clear();
			pendingMeshes.Clear();
			// Tracing again later starts as a load does: its first frame builds everything.
			frameBuilds = 0;
			Console.WriteLine("[RayTracing] Bottom levels released");
		}

		#endregion

		#region PrivateMethods

		private static void Free(Entry entry)
		{
			pending.Remove(entry);
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
			public long Triangles;

			/// <summary>Its build is recorded (a top level may place it).</summary>
			public bool Recorded;

			/// <summary>The mesh's triangles changed on the GPU since.</summary>
			public bool Stale;

			/// <summary>Its compacted size was read (and the copy made where it saved enough).</summary>
			public bool Compacted;
		}

		#endregion
	}
}
