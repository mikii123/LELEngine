using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Mathematics;

namespace LELEngine
{
	/// <summary>
	///     The built-in importer of Wavefront .obj files (it needs no native library, so .obj models also load without
	///     the editor): positions, texture coordinates and normals; faces as "v", "v/vt", "v//vn" or "v/vt/vn"
	///     (negative indices count from the end), polygons triangulated as fans. Every "usemtl" material becomes a
	///     sub-mesh (in order of first use) described by the .mtl library (Kd, d / Tr, Ke, Ns, map_Kd, map_Bump / norm).
	///     Identical corners share a vertex; corners without a normal get the smooth normal of their position, tangents
	///     follow the UV layout. One node holds the mesh.
	/// </summary>
	internal sealed class ObjModelBackend : IModelImportBackend
	{
		#region PublicFields

		public int Version => 1;

		#endregion

		#region PublicMethods

		public bool CanImport(string extension)
		{
			return extension == ".obj";
		}

		public ModelData Import(string assetPath, ModelImporter settings)
		{
			string file = AssetDatabase.ToAbsolute(assetPath);
			string folder = Path.GetDirectoryName(file);
			var positions = new List<Vector3>();
			var uvs = new List<Vector2>();
			var normals = new List<Vector3>();
			var corners = new List<(int v, int t, int n)>();
			var lookup = new Dictionary<(int, int, int), uint>();
			var groups = new List<(string material, List<uint> indices)>();
			var libraries = new List<string>();
			List<uint> current = null;
			var face = new List<uint>();
			bool useFileNormals = settings.normals == ModelNormals.Import;

			foreach (string rawLine in File.ReadLines(file))
			{
				string line = rawLine.Trim();
				if (line.Length == 0 || line[0] == '#')
				{
					continue;
				}

				string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
				switch (words[0])
				{
					case "v":
						positions.Add(new Vector3(Extensions.ParseFloat(words[1]), Extensions.ParseFloat(words[2]), Extensions.ParseFloat(words[3])));
						break;
					case "vt":
						float u = Extensions.ParseFloat(words[1]);
						float v = words.Length > 2 ? Extensions.ParseFloat(words[2]) : 0f;
						uvs.Add(new Vector2(u, settings.flipV ? 1f - v : v));
						break;
					case "vn":
						normals.Add(new Vector3(Extensions.ParseFloat(words[1]), Extensions.ParseFloat(words[2]), Extensions.ParseFloat(words[3])));
						break;
					case "mtllib":
						libraries.Add(Rest(line, words));
						break;
					case "usemtl":
						current = Group(groups, Rest(line, words));
						break;
					case "f":
						current ??= Group(groups, "");
						face.Clear();
						for (int i = 1; i < words.Length; i++)
						{
							(int pv, int pt, int pn) = ParseCorner(words[i], positions.Count, uvs.Count, normals.Count);
							(int, int, int) key = (pv, pt, useFileNormals ? pn : -1);
							if (!lookup.TryGetValue(key, out uint index))
							{
								index = (uint)corners.Count;
								corners.Add(key);
								lookup.Add(key, index);
							}

							face.Add(index);
						}

						for (int i = 1; i + 1 < face.Count; i++)
						{
							current.Add(face[0]);
							current.Add(face[i]);
							current.Add(face[i + 1]);
						}

						break;
				}
			}

			Dictionary<string, ModelMaterial> library = ReadLibraries(libraries, folder);
			var data = new ModelData();
			var mesh = new ModelMeshData { Name = Path.GetFileNameWithoutExtension(assetPath) };
			var allIndices = new List<uint>();
			var ranges = new List<SubMesh>();
			var slots = new List<int>();
			foreach ((string material, List<uint> groupIndices) in groups)
			{
				if (groupIndices.Count == 0)
				{
					continue;
				}

				ranges.Add(new SubMesh(allIndices.Count, groupIndices.Count));
				allIndices.AddRange(groupIndices);
				if (material.Length == 0)
				{
					slots.Add(-1);
					continue;
				}

				slots.Add(data.Materials.Count);
				data.Materials.Add(library.TryGetValue(material, out ModelMaterial described) ? described : new ModelMaterial { Name = material });
			}

			uint[] indices = allIndices.ToArray();
			Vector3[] smooth = SmoothNormals(corners, positions, indices);
			var raw = new Vertex[corners.Count];
			for (int i = 0; i < raw.Length; i++)
			{
				(int v, int t, int n) = corners[i];
				Vector3 position = v >= 0 && v < positions.Count ? positions[v] : Vector3.Zero;
				Vector2 uv = t >= 0 && t < uvs.Count ? uvs[t] : Vector2.Zero;
				Vector3 normal = n >= 0 && n < normals.Count ? normals[n] : v >= 0 && v < smooth.Length ? smooth[v] : Vector3.UnitY;
				raw[i] = new Vertex(position, normal.LengthSquared > 0f ? normal.Normalized() : Vector3.UnitY, uv, Vector3.UnitX, Vector3.UnitZ);
			}

			mesh.Vertices = Mesh.WithTangents(raw, indices);
			mesh.Indices = indices;
			mesh.SubMeshes = ranges.Count > 0 ? ranges.ToArray() : new[] { new SubMesh(0, 0) };
			mesh.Materials = slots.Count > 0 ? slots.ToArray() : new[] { -1 };
			data.Meshes.Add(mesh);
			data.Nodes.Add(new ModelNode { Name = mesh.Name, Mesh = 0 });
			return data;
		}

		#endregion

		#region PrivateMethods

		private static string Rest(string line, string[] words)
		{
			return words.Length > 1 ? line.Substring(line.IndexOf(words[1], words[0].Length, StringComparison.Ordinal)).Trim() : "";
		}

		private static List<uint> Group(List<(string material, List<uint> indices)> groups, string material)
		{
			foreach ((string name, List<uint> indices) in groups)
			{
				if (name == material)
				{
					return indices;
				}
			}

			var created = new List<uint>();
			groups.Add((material, created));
			return created;
		}

		/// <summary>(position, uv, normal) indices, zero based; -1 for a missing one.</summary>
		private static (int, int, int) ParseCorner(string token, int positionCount, int uvCount, int normalCount)
		{
			string[] parts = token.Split('/');
			int v = Index(parts[0], positionCount);
			int t = parts.Length > 1 ? Index(parts[1], uvCount) : -1;
			int n = parts.Length > 2 ? Index(parts[2], normalCount) : -1;
			return (v, t, n);
		}

		private static int Index(string text, int count)
		{
			if (text.Length == 0 || !int.TryParse(text, out int value))
			{
				return -1;
			}

			return value < 0 ? count + value : value - 1;
		}

		/// <summary>Area-weighted face normals summed per position (for corners without their own normal).</summary>
		private static Vector3[] SmoothNormals(List<(int v, int t, int n)> corners, List<Vector3> positions, uint[] indices)
		{
			var sums = new Vector3[positions.Count];
			for (int i = 0; i + 2 < indices.Length; i += 3)
			{
				int a = corners[(int)indices[i]].v, b = corners[(int)indices[i + 1]].v, c = corners[(int)indices[i + 2]].v;
				if (a < 0 || b < 0 || c < 0 || a >= positions.Count || b >= positions.Count || c >= positions.Count)
				{
					continue;
				}

				Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
				sums[a] += normal;
				sums[b] += normal;
				sums[c] += normal;
			}

			return sums;
		}

		/// <summary>The materials of the .mtl files (Kd, d / Tr, Ke, Ns, map_Kd, map_Bump / bump / norm).</summary>
		private static Dictionary<string, ModelMaterial> ReadLibraries(List<string> libraries, string folder)
		{
			var result = new Dictionary<string, ModelMaterial>(StringComparer.Ordinal);
			foreach (string library in libraries)
			{
				string file = Path.Combine(folder, library);
				if (!File.Exists(file))
				{
					Debug.LogWarning("[Model] Material library not found: " + file);
					continue;
				}

				ModelMaterial current = null;
				foreach (string rawLine in File.ReadLines(file))
				{
					string line = rawLine.Trim();
					if (line.Length == 0 || line[0] == '#')
					{
						continue;
					}

					string[] words = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
					string key = words[0].ToLowerInvariant();
					if (key == "newmtl")
					{
						current = new ModelMaterial { Name = Rest(line, words) };
						result[current.Name] = current;
						continue;
					}

					if (current == null || words.Length < 2)
					{
						continue;
					}

					switch (key)
					{
						case "kd":
							current.Color = new Vector4(Float(words, 1), Float(words, 2), Float(words, 3), current.Color.W);
							break;
						case "d":
							current.Color.W = Float(words, 1);
							break;
						case "tr":
							current.Color.W = 1f - Float(words, 1);
							break;
						case "ke":
							var emissive = new Vector3(Float(words, 1), Float(words, 2), Float(words, 3));
							current.Emissive = emissive.LengthSquared > 0f ? new Vector4(emissive, 1f) : Vector4.Zero;
							break;
						case "ns":
							// Blinn-Phong exponent to a roughness of the same highlight width.
							current.Roughness = Math.Clamp(MathF.Sqrt(2f / (Float(words, 1) + 2f)), 0.02f, 1f);
							break;
						case "map_kd":
							current.AlbedoMap = Texture(TexturePath(words), folder);
							break;
						case "map_bump":
						case "bump":
						case "norm":
							current.NormalMap = Texture(TexturePath(words), folder);
							break;
					}
				}
			}

			return result;
		}

		private static float Float(string[] words, int index)
		{
			return index < words.Length ? Extensions.ParseFloat(words[index]) : 0f;
		}

		/// <summary>The file name of a map statement (the last word: options like "-bm 1" come first).</summary>
		private static string TexturePath(string[] words)
		{
			return words[words.Length - 1];
		}

		/// <summary>A texture file next to the model (or anywhere in the project with that name).</summary>
		private static ModelTexture Texture(string path, string folder)
		{
			string file = Path.GetFullPath(Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar)));
			string assetPath = File.Exists(file) ? AssetDatabase.ToAssetPath(file) : null;
			assetPath ??= AssetDatabase.Resolve(Path.GetFileName(path), null);
			if (assetPath == null)
			{
				Debug.LogWarning("[Model] Texture not found: " + path);
				return null;
			}

			return new ModelTexture { AssetPath = assetPath, Name = Path.GetFileNameWithoutExtension(assetPath) };
		}

		#endregion
	}
}
