using System;
using System.Collections.Generic;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>A range of a mesh's index buffer drawn with one material (Unity's sub-mesh): material slot i of a renderer.</summary>
	public readonly struct SubMesh
	{
		#region Constructors

		public SubMesh(int firstIndex, int indexCount)
		{
			FirstIndex = firstIndex;
			IndexCount = indexCount;
		}

		#endregion

		#region PublicFields

		/// <summary>First index of the range in <see cref="Mesh.Indices" />.</summary>
		public int FirstIndex { get; }

		/// <summary>Number of indices (three per triangle).</summary>
		public int IndexCount { get; }

		#endregion
	}

	/// <summary>
	///     Indexed triangle geometry: shared vertices, one index buffer, and sub-meshes (index ranges) drawn with one
	///     material each. Meshes of asset files are sub-assets of a <see cref="Model" /> (one per node with geometry,
	///     loaded through <see cref="ModelAssets" />); meshes can also be built in code.
	/// </summary>
	public sealed class Mesh : IAsset
	{
		#region PublicFields

		/// <summary>Root-relative path of the source file (see <see cref="AssetDatabase" />).</summary>
		public string AssetPath { get; internal set; }

		/// <summary>Name of the mesh inside its model file (null for a mesh built in code).</summary>
		public string SubAssetName { get; }

		public string name;

		/// <summary>The vertices (shared by the triangles of every sub-mesh).</summary>
		public Vertex[] Vertices { get; }

		/// <summary>Triangle list indices into <see cref="Vertices" />, sub-mesh after sub-mesh.</summary>
		public uint[] Indices { get; }

		public IReadOnlyList<SubMesh> SubMeshes => subMeshes;
		public int SubMeshCount => subMeshes.Length;
		public int TriangleCount => Indices.Length / 3;

		/// <summary>Local-space axis-aligned bounds of all vertices.</summary>
		public Vector3 BoundsMin { get; private set; }

		public Vector3 BoundsMax { get; private set; }

		#endregion

		#region InternalFields

		/// <summary>Distance fields a game build computed for this mesh (at the scales its scenes draw it); null outside builds.</summary>
		internal List<Rendering.DistanceField.BakedDistanceField> BakedDistanceFields;

		// Range of this mesh in the shared GPU buffers (Rendering.GeometryPool); valid while PoolGeneration matches.
		internal int PoolGeneration;
		internal int PoolFirstIndex;
		internal int PoolIndexCount;
		internal int PoolBaseVertex;
		internal int PoolVertexCount;

		#endregion

		#region PrivateFields

		private readonly SubMesh[] subMeshes;
		private Vector3[] trianglePositions;

		#endregion

		#region Constructors

		/// <summary>A mesh from its data; <paramref name="subMeshes" /> null: one sub-mesh with every index.</summary>
		public Mesh(string name, Vertex[] vertices, uint[] indices, SubMesh[] subMeshes = null)
			: this(name, vertices, indices, subMeshes, null, null)
		{
		}

		/// <summary>A mesh of a model file.</summary>
		internal Mesh(string name, Vertex[] vertices, uint[] indices, SubMesh[] subMeshes, string assetPath, string subAssetName)
		{
			this.name = name;
			Vertices = vertices ?? Array.Empty<Vertex>();
			Indices = indices ?? Array.Empty<uint>();
			this.subMeshes = subMeshes ?? new[] { new SubMesh(0, Indices.Length) };
			AssetPath = assetPath;
			SubAssetName = subAssetName;
			ComputeBounds();
		}

		#endregion

		#region PublicMethods

		/// <summary>Positions of every triangle, three per triangle (CPU-side geometry queries: picking, baking).</summary>
		public Vector3[] GetTrianglePositions()
		{
			if (trianglePositions == null)
			{
				var positions = new Vector3[Indices.Length / 3 * 3];
				for (int i = 0; i < positions.Length; i++)
				{
					positions[i] = Vertices[Indices[i]].position;
				}

				trianglePositions = positions;
			}

			return trianglePositions;
		}

		/// <summary>
		///     Vertices with tangents and bitangents from the UV layout (per-vertex sums of the triangle tangents,
		///     made orthogonal to the normal; the bitangent keeps the UV handedness).
		/// </summary>
		public static Vertex[] WithTangents(Vertex[] vertices, uint[] indices)
		{
			var tangents = new Vector3[vertices.Length];
			var bitangents = new Vector3[vertices.Length];
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				uint a = indices[i], b = indices[i + 1], c = indices[i + 2];
				Vector3 edge1 = vertices[b].position - vertices[a].position;
				Vector3 edge2 = vertices[c].position - vertices[a].position;
				Vector2 duv1 = vertices[b].texcoord - vertices[a].texcoord;
				Vector2 duv2 = vertices[c].texcoord - vertices[a].texcoord;
				float determinant = duv1.X * duv2.Y - duv2.X * duv1.Y;
				if (Math.Abs(determinant) < 1e-12f)
				{
					continue;
				}

				float f = 1f / determinant;
				Vector3 tangent = (edge1 * duv2.Y - edge2 * duv1.Y) * f;
				Vector3 bitangent = (edge2 * duv1.X - edge1 * duv2.X) * f;
				tangents[a] += tangent;
				tangents[b] += tangent;
				tangents[c] += tangent;
				bitangents[a] += bitangent;
				bitangents[b] += bitangent;
				bitangents[c] += bitangent;
			}

			var result = new Vertex[vertices.Length];
			for (int i = 0; i < vertices.Length; i++)
			{
				Vertex v = vertices[i];
				Vector3 n = v.normal;
				Vector3 t = tangents[i] - n * Vector3.Dot(n, tangents[i]);
				if (t.LengthSquared < 1e-20f)
				{
					// No usable UVs: any direction perpendicular to the normal.
					t = Vector3.Cross(Math.Abs(n.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX, n);
				}

				t = t.Normalized();
				Vector3 bt = Vector3.Cross(n, t);
				if (Vector3.Dot(bt, bitangents[i]) < 0f)
				{
					bt = -bt;
				}

				result[i] = new Vertex(v.position, n, v.texcoord, t, bt, v.color);
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		private void ComputeBounds()
		{
			if (Vertices.Length == 0)
			{
				BoundsMin = Vector3.Zero;
				BoundsMax = Vector3.Zero;
				return;
			}

			Vector3 min = Vertices[0].position;
			Vector3 max = min;
			foreach (Vertex vertex in Vertices)
			{
				min = Vector3.ComponentMin(min, vertex.position);
				max = Vector3.ComponentMax(max, vertex.position);
			}

			BoundsMin = min;
			BoundsMax = max;
		}

		#endregion
	}
}
