using System;
using System.Globalization;
using System.Text;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Impostors
{
	/// <summary>
	///     The mesh an impostor draws with (Engine/Impostor.shader): the box of the object's bounds, so everything that
	///     reads a renderer's mesh sees the object's place and size (culling, LOD group bounds, picking and selection
	///     in the editor, and a plain box wherever the shader's own passes are not used). The shader does not draw the
	///     box: each corner carries, in its texture coordinates, a corner of a hexagon around the unit circle (the two
	///     opposite corners (0, 0, 0) and (1, 1, 1) its centre), and the vertex stage lays those out as a billboard facing
	///     the view. Laid out so, the three faces around the first corner cover the hexagon once, facing the view, and the
	///     three around the opposite corner cover it again facing away, where back-face culling drops them: six triangles
	///     are drawn.
	/// </summary>
	public static class ImpostorMesh
	{
		#region PrivateFields

		// The hexagon's corner of each box corner (index: x | y << 1 | z << 2), in sixths of a turn; -1: its centre.
		// The order makes the box's outward faces around corner 0 counter-clockwise in the billboard.
		private static readonly int[] HexagonCorner = { -1, 0, 4, 5, 2, 1, 3, -1 };

		// A hexagon around the unit circle, a corner every sixth of a turn from +X: its corners at 1 / cos(30 degrees).
		private const float Circumradius = 1.1547005f;

		private static readonly Vector2[] Hexagon =
		{
			new Vector2(Circumradius, 0f), new Vector2(Circumradius * 0.5f, 1f), new Vector2(-Circumradius * 0.5f, 1f),
			new Vector2(-Circumradius, 0f), new Vector2(-Circumradius * 0.5f, -1f), new Vector2(Circumradius * 0.5f, -1f)
		};

		// The box's faces, counter-clockwise seen from outside: -X, -Y, -Z, +X, +Y, +Z.
		private static readonly int[][] Faces =
		{
			new[] { 0, 4, 6, 2 }, new[] { 0, 1, 5, 4 }, new[] { 0, 2, 3, 1 },
			new[] { 1, 3, 7, 5 }, new[] { 2, 6, 7, 3 }, new[] { 4, 5, 7, 6 }
		};

		#endregion

		#region PublicMethods

		/// <summary>
		///     The proxy's geometry for bounds <paramref name="min" /> .. <paramref name="max" />: the eight corners, each
		///     with its corner of the billboard's outline (the texture coordinate: 0.5 + half the corner), and the twelve
		///     triangles.
		/// </summary>
		public static void GetGeometry(Vector3 min, Vector3 max, out Vector3[] positions, out Vector2[] texcoords, out uint[] indices)
		{
			positions = new Vector3[8];
			texcoords = new Vector2[8];
			for (int corner = 0; corner < 8; corner++)
			{
				positions[corner] = new Vector3((corner & 1) != 0 ? max.X : min.X, (corner & 2) != 0 ? max.Y : min.Y, (corner & 4) != 0 ? max.Z : min.Z);
				int sixth = HexagonCorner[corner];
				Vector2 outline = sixth < 0 ? Vector2.Zero : Hexagon[sixth];
				texcoords[corner] = outline * 0.5f + new Vector2(0.5f);
			}

			indices = new uint[36];
			int index = 0;
			foreach (int[] face in Faces)
			{
				indices[index++] = (uint)face[0];
				indices[index++] = (uint)face[1];
				indices[index++] = (uint)face[2];
				indices[index++] = (uint)face[0];
				indices[index++] = (uint)face[2];
				indices[index++] = (uint)face[3];
			}
		}

		/// <summary>The proxy as a mesh made in code (an impostor baked at runtime; assets get a model file, <see cref="ToObj" />).</summary>
		public static Mesh Create(Vector3 min, Vector3 max, string name = "Impostor")
		{
			GetGeometry(min, max, out Vector3[] positions, out Vector2[] texcoords, out uint[] indices);
			Vector3 centre = (min + max) * 0.5f;
			var vertices = new Vertex[positions.Length];
			for (int i = 0; i < vertices.Length; i++)
			{
				Vector3 outward = positions[i] - centre;
				Vector3 normal = outward.LengthSquared > 1e-20f ? outward.Normalized() : Vector3.UnitY;
				vertices[i] = new Vertex(positions[i], normal, texcoords[i], Vector3.UnitX, Vector3.UnitZ);
			}

			return new Mesh(name, vertices, indices);
		}

		/// <summary>The proxy as the text of a Wavefront .obj file: what the editor writes next to an impostor asset.</summary>
		public static string ToObj(Vector3 min, Vector3 max, string comment = null)
		{
			GetGeometry(min, max, out Vector3[] positions, out Vector2[] texcoords, out uint[] indices);
			var text = new StringBuilder();
			text.Append("# Impostor proxy: the box of the object's bounds; the texture coordinates are the corners of the billboard\n");
			text.Append("# Engine/Impostor.shader lays out. Written by the bake: edits are lost at the next one.\n");
			if (!string.IsNullOrEmpty(comment))
			{
				text.Append("# ").Append(comment.Replace('\n', ' ')).Append('\n');
			}

			foreach (Vector3 position in positions)
			{
				text.Append("v ").Append(Number(position.X)).Append(' ').Append(Number(position.Y)).Append(' ').Append(Number(position.Z)).Append('\n');
			}

			foreach (Vector2 texcoord in texcoords)
			{
				text.Append("vt ").Append(Number(texcoord.X)).Append(' ').Append(Number(texcoord.Y)).Append('\n');
			}

			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				text.Append("f");
				for (int corner = 0; corner < 3; corner++)
				{
					uint one = indices[i + corner] + 1;
					text.Append(' ').Append(one).Append('/').Append(one);
				}

				text.Append('\n');
			}

			return text.ToString();
		}

		#endregion

		#region PrivateMethods

		// Plain decimals (no exponent: not every .obj reader takes one), enough of them that the box read back is the
		// box written.
		private static string Number(float value)
		{
			// As a double: a float's own formatting stops at seven digits.
			return ((double)value).ToString("0.#########", CultureInfo.InvariantCulture);
		}

		#endregion
	}
}
