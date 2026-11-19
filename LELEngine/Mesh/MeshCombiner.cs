using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LELEngine.Shaders;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>One mesh of a combine (Unity's CombineInstance): the mesh, where it goes and what it is drawn with.</summary>
	public struct CombineInstance
	{
		public Mesh Mesh;

		/// <summary>The mesh's space to the combined mesh's space (row vectors, v * M: as <c>Transform.LocalToWorld</c>).</summary>
		public Matrix4 Transform;

		/// <summary>
		///     The material of each sub-mesh, as a renderer's slots (fewer than the sub-meshes: the last one repeats;
		///     null or empty: no material). Sub-meshes of the same material end up in one sub-mesh.
		/// </summary>
		public IReadOnlyList<Material> Materials;

		public CombineInstance(Mesh mesh, Matrix4 transform, IReadOnlyList<Material> materials = null)
		{
			Mesh = mesh;
			Transform = transform;
			Materials = materials;
		}
	}

	/// <summary>What <see cref="MeshCombiner.Combine" /> made: the mesh and the material of each of its sub-meshes.</summary>
	public sealed class CombinedMesh
	{
		#region PublicFields

		/// <summary>The combined mesh (built in code: not an asset until it is saved as one, <see cref="ModelAssets.SaveMesh" />).</summary>
		public Mesh Mesh { get; }

		/// <summary>The material of each sub-mesh of <see cref="Mesh" />, in order: a renderer's material slots.</summary>
		public List<Material> Materials { get; }

		/// <summary>The instances that had triangles (the others added nothing).</summary>
		public int InstanceCount { get; }

		#endregion

		#region Constructors

		internal CombinedMesh(Mesh mesh, List<Material> materials, int instanceCount)
		{
			Mesh = mesh;
			Materials = materials;
			InstanceCount = instanceCount;
		}

		#endregion
	}

	/// <summary>
	///     Merges meshes into one: every instance's vertices moved into the combined mesh's space, and one sub-mesh per
	///     material (in the order the materials first appear), so the result draws with one call per material whatever
	///     the number of meshes it was made of. Works anywhere (editor, game); the mesh it returns is built in code.
	/// </summary>
	public static class MeshCombiner
	{
		#region PrivateFields

		// Below this many vertices the instances are filled one after another (threads would cost more than they save).
		private const int ParallelVertices = 1 << 16;

		// The largest arrays the combine allocates (elements; .NET's own limit is a little below 2^31).
		private const long MaxElements = int.MaxValue - 64;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Combines the instances into one mesh. Normals, tangents and bitangents follow each instance's transform
		///     (normals stay perpendicular under a non-uniform scale), a mirroring transform (negative scale) turns the
		///     triangles so they keep facing outwards. Instances without a mesh or triangles are left out; with none left
		///     the result is a mesh without triangles and sub-meshes.
		/// </summary>
		public static CombinedMesh Combine(IReadOnlyList<CombineInstance> instances, string name = "Combined Mesh")
		{
			if (instances == null)
			{
				throw new ArgumentNullException(nameof(instances));
			}

			// 1. What goes where: the sub-mesh of each material, the vertices of each instance, the index totals.
			var materials = new List<Material>();
			var slotOfMaterial = new Dictionary<Material, int>(ReferenceEqualityComparer.Instance);
			int slotWithoutMaterial = -1;
			var indexCounts = new List<long>();
			var baseVertices = new int[instances.Count];
			var slots = new int[instances.Count][];
			long vertexTotal = 0;
			long indexTotal = 0;
			int used = 0;
			for (int i = 0; i < instances.Count; i++)
			{
				Mesh mesh = instances[i].Mesh;
				if (mesh == null || mesh.Indices.Length < 3 || mesh.Vertices.Length == 0)
				{
					baseVertices[i] = -1;
					continue;
				}

				baseVertices[i] = (int)Math.Min(vertexTotal, int.MaxValue);
				vertexTotal += mesh.Vertices.Length;
				used++;
				var own = new int[mesh.SubMeshCount];
				for (int s = 0; s < own.Length; s++)
				{
					Material material = MaterialOf(instances[i].Materials, s);
					int slot;
					if (material == null)
					{
						if (slotWithoutMaterial < 0)
						{
							slotWithoutMaterial = materials.Count;
							materials.Add(null);
							indexCounts.Add(0);
						}

						slot = slotWithoutMaterial;
					}
					else if (!slotOfMaterial.TryGetValue(material, out slot))
					{
						slot = materials.Count;
						slotOfMaterial[material] = slot;
						materials.Add(material);
						indexCounts.Add(0);
					}

					own[s] = slot;
					int count = mesh.SubMeshes[s].IndexCount / 3 * 3;
					indexCounts[slot] += count;
					indexTotal += count;
				}

				slots[i] = own;
			}

			if (vertexTotal > MaxElements || indexTotal > MaxElements)
			{
				throw new InvalidOperationException($"The combined mesh would have {vertexTotal} vertices and {indexTotal} indices: too many for one mesh (the limit is {MaxElements} of each). Combine fewer meshes at once.");
			}

			// 2. The sub-meshes' ranges, one after another, and each instance's place in them (in the instances' order,
			//    so the same input always gives the same mesh).
			var subMeshes = new SubMesh[materials.Count];
			var cursors = new int[materials.Count];
			int first = 0;
			for (int slot = 0; slot < subMeshes.Length; slot++)
			{
				subMeshes[slot] = new SubMesh(first, (int)indexCounts[slot]);
				cursors[slot] = first;
				first += (int)indexCounts[slot];
			}

			var destinations = new int[instances.Count][];
			for (int i = 0; i < instances.Count; i++)
			{
				if (baseVertices[i] < 0)
				{
					continue;
				}

				Mesh mesh = instances[i].Mesh;
				var own = new int[slots[i].Length];
				for (int s = 0; s < own.Length; s++)
				{
					own[s] = cursors[slots[i][s]];
					cursors[slots[i][s]] += mesh.SubMeshes[s].IndexCount / 3 * 3;
				}

				destinations[i] = own;
			}

			// 3. The data: every instance writes its own ranges, so they fill side by side.
			var vertices = new Vertex[vertexTotal];
			var indices = new uint[indexTotal];
			void FillInstance(int i)
			{
				if (baseVertices[i] >= 0)
				{
					Fill(instances[i], baseVertices[i], destinations[i], vertices, indices);
				}
			}

			if (vertexTotal >= ParallelVertices && used > 1)
			{
				Parallel.For(0, instances.Count, FillInstance);
			}
			else
			{
				for (int i = 0; i < instances.Count; i++)
				{
					FillInstance(i);
				}
			}

			return new CombinedMesh(new Mesh(name, vertices, indices, subMeshes), materials, used);
		}

		#endregion

		#region PrivateMethods

		/// <summary>A renderer's rule: the sub-mesh's slot, else the last material, else none.</summary>
		private static Material MaterialOf(IReadOnlyList<Material> materials, int subMesh)
		{
			if (materials == null || materials.Count == 0)
			{
				return null;
			}

			return materials[subMesh < materials.Count ? subMesh : materials.Count - 1];
		}

		private static void Fill(CombineInstance instance, int baseVertex, int[] destinations, Vertex[] vertices, uint[] indices)
		{
			Mesh mesh = instance.Mesh;
			Matrix4 matrix = instance.Transform;

			// The linear part by rows: a direction d goes to d.X * row0 + d.Y * row1 + d.Z * row2.
			Vector3 row0 = matrix.Row0.Xyz;
			Vector3 row1 = matrix.Row1.Xyz;
			Vector3 row2 = matrix.Row2.Xyz;
			float determinant = Vector3.Dot(Vector3.Cross(row0, row1), row2);
			bool mirrored = determinant < 0f;

			// Normals go through the cofactors (the inverse transpose without its division, which the normalization takes
			// care of: no inverse to fail on a flattened object), turned back where the transform mirrors.
			float side = mirrored ? -1f : 1f;
			Vector3 cofactor0 = Vector3.Cross(row1, row2) * side;
			Vector3 cofactor1 = Vector3.Cross(row2, row0) * side;
			Vector3 cofactor2 = Vector3.Cross(row0, row1) * side;

			Vertex[] source = mesh.Vertices;
			for (int v = 0; v < source.Length; v++)
			{
				Vertex vertex = source[v];
				Vector3 normal = vertex.normal;
				Vector3 tangent = vertex.tangent;
				Vector3 bitangent = vertex.bitangent;
				vertices[baseVertex + v] = new Vertex(
					Vector3.TransformPosition(vertex.position, matrix),
					Direction(cofactor0 * normal.X + cofactor1 * normal.Y + cofactor2 * normal.Z, normal),
					vertex.texcoord,
					Direction(row0 * tangent.X + row1 * tangent.Y + row2 * tangent.Z, tangent),
					Direction(row0 * bitangent.X + row1 * bitangent.Y + row2 * bitangent.Z, bitangent),
					vertex.color,
					vertex.texcoord2);
			}

			uint[] sourceIndices = mesh.Indices;
			uint offset = (uint)baseVertex;
			for (int s = 0; s < destinations.Length; s++)
			{
				SubMesh range = mesh.SubMeshes[s];
				int destination = destinations[s];
				int end = range.FirstIndex + range.IndexCount / 3 * 3;
				for (int i = range.FirstIndex; i < end; i += 3)
				{
					// A mirrored triangle is wound the other way round: two of its corners swap.
					indices[destination] = sourceIndices[i] + offset;
					indices[destination + 1] = sourceIndices[mirrored ? i + 2 : i + 1] + offset;
					indices[destination + 2] = sourceIndices[mirrored ? i + 1 : i + 2] + offset;
					destination += 3;
				}
			}
		}

		/// <summary>The transformed direction at unit length; the direction it had where the transform leaves nothing of it.</summary>
		private static Vector3 Direction(Vector3 transformed, Vector3 original)
		{
			float length = transformed.LengthSquared;
			return length > 1e-30f ? transformed / MathF.Sqrt(length) : original;
		}

		#endregion
	}
}
