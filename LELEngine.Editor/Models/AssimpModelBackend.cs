using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenTK.Mathematics;
using Silk.NET.Assimp;
using AiMaterial = Silk.NET.Assimp.Material;
using AiMesh = Silk.NET.Assimp.Mesh;
using AiNode = Silk.NET.Assimp.Node;
using AiScene = Silk.NET.Assimp.Scene;
using AiTexture = Silk.NET.Assimp.Texture;
using NMatrix4x4 = System.Numerics.Matrix4x4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Imports model files with Assimp (Silk.NET.Assimp): triangulated, identical vertices joined, tangent space
	///     computed, points and lines dropped. Every node becomes a <see cref="ModelNode" />; the meshes a node holds
	///     become one mesh with a sub-mesh per Assimp mesh (each has one material). Materials are described from their
	///     standard keys (base / diffuse color, opacity, emission, roughness or shininess) and their albedo and normal
	///     maps: project files resolved next to the model, or textures embedded in the file. FBX files report their
	///     unit (UnitScaleFactor) for the import's unit conversion.
	/// </summary>
	internal sealed unsafe class AssimpModelBackend : IModelImportBackend
	{
		#region PrivateFields

		private static readonly string[] Formats = { ".fbx", ".gltf", ".glb", ".dae", ".3ds", ".blend", ".ply", ".stl" };

		// aiPrimitiveType_POINT | aiPrimitiveType_LINE, aiComponent_NORMALS, AI_SCENE_FLAGS_INCOMPLETE.
		private const int PointsAndLines = 0x1 | 0x2;
		private const int NormalsComponent = 0x2;
		private const uint IncompleteFlag = 0x1;

		private Assimp assimp;

		#endregion

		#region PublicFields

		// 2: the second UV set (texture coordinate channel 1), FBX geometric transforms in the meshes, tangents along +u / +v.
		public int Version => 2;

		#endregion

		#region PublicMethods

		public bool CanImport(string extension)
		{
			return Array.IndexOf(Formats, extension) >= 0;
		}

		public ModelData Import(string assetPath, ModelImporter settings)
		{
			assimp ??= Assimp.GetApi();
			string file = AssetDatabase.ToAbsolute(assetPath);
			// No CalculateTangentSpace: Assimp's bitangents point along -v (the engine's lit shaders and Unity read the normal
			// map's green along +v); tangents come from Mesh.WithTangents, as for .obj files.
			PostProcessSteps steps = PostProcessSteps.Triangulate | PostProcessSteps.JoinIdenticalVertices
				| PostProcessSteps.SortByPrimitiveType | PostProcessSteps.FindDegenerates | PostProcessSteps.FindInvalidData
				| PostProcessSteps.ValidateDataStructure | PostProcessSteps.GenerateUVCoords | PostProcessSteps.TransformUVCoords
				| PostProcessSteps.GenerateSmoothNormals;
			if (settings.normals == ModelNormals.Calculate)
			{
				steps |= PostProcessSteps.RemoveComponent;
			}

			if (settings.flipV)
			{
				steps |= PostProcessSteps.FlipUVs;
			}

			if (settings.optimizeMesh)
			{
				steps |= PostProcessSteps.ImproveCacheLocality;
			}

			PropertyStore* store = assimp.CreatePropertyStore();
			AiScene* scene;
			try
			{
				assimp.SetImportPropertyFloat(store, "PP_GSN_MAX_SMOOTHING_ANGLE", settings.smoothingAngle);
				assimp.SetImportPropertyInteger(store, "PP_SBP_REMOVE", PointsAndLines);
				assimp.SetImportPropertyInteger(store, "PP_RVC_FLAGS", NormalsComponent);
				// $AssimpFbx$ helper nodes for an FBX model's pivot chain: Visit folds them into one node per model, and the
				// geometric ones (they move only the model's own geometry) into its mesh, as Unity does.
				assimp.SetImportPropertyInteger(store, "IMPORT_FBX_PRESERVE_PIVOTS", 1);
				scene = assimp.ImportFileExWithProperties(file, (uint)steps, null, store);
			}
			finally
			{
				assimp.ReleasePropertyStore(store);
			}

			if (scene == null || scene->MRootNode == null || (scene->MFlags & IncompleteFlag) != 0)
			{
				string error = assimp.GetErrorStringS();
				if (scene != null)
				{
					assimp.ReleaseImport(scene);
				}

				throw new InvalidDataException(string.IsNullOrEmpty(error) ? "Assimp could not read the file" : error);
			}

			try
			{
				return new Converter(assimp, scene, file).Convert(Path.GetFileNameWithoutExtension(assetPath));
			}
			finally
			{
				assimp.ReleaseImport(scene);
			}
		}

		#endregion

		#region NestedTypes

		/// <summary>One import: the Assimp scene to <see cref="ModelData" />.</summary>
		private sealed class Converter
		{
			private readonly Assimp assimp;
			private readonly AiScene* scene;
			private readonly string file;
			private readonly ModelData data = new ModelData();
			private readonly Dictionary<string, int> meshesByContent = new Dictionary<string, int>();
			private readonly HashSet<string> meshNames = new HashSet<string>(StringComparer.Ordinal);

			public Converter(Assimp assimp, AiScene* scene, string file)
			{
				this.assimp = assimp;
				this.scene = scene;
				this.file = file;
			}

			public ModelData Convert(string modelName)
			{
				for (uint i = 0; i < scene->MNumMaterials; i++)
				{
					data.Materials.Add(ReadMaterial(scene->MMaterials[i], (int)i, modelName));
				}

				Visit(scene->MRootNode, -1);
				data.UnitScale = FileUnitScale();
				return data;
			}

			// ------------------------------------------------------------ hierarchy

			private void Visit(AiNode* node, int parent)
			{
				// An FBX model's pivot chain ("<model>_$AssimpFbx$_<part>" nodes down to the model's own node): the transform
				// parts make the node's local transform, the geometric parts (after them) move its geometry only.
				NMatrix4x4 local = NMatrix4x4.Identity;
				NMatrix4x4 geometric = NMatrix4x4.Identity;
				bool hasGeometric = false;
				var others = new List<IntPtr>();
				string chain = HelperBase(node);
				while (chain != null && HelperBase(node) == chain)
				{
					if (HelperPart(node).StartsWith("Geometric", StringComparison.Ordinal))
					{
						geometric *= node->MTransformation;
						hasGeometric = true;
					}
					else
					{
						local *= node->MTransformation;
					}

					AiNode* next = null;
					for (uint i = 0; i < node->MNumChildren; i++)
					{
						AiNode* child = node->MChildren[i];
						if (next == null && (HelperBase(child) == chain || child->MName.AsString == chain))
						{
							next = child;
						}
						else
						{
							others.Add((IntPtr)child);
						}
					}

					if (next == null)
					{
						break;
					}

					node = next;
				}

				if (hasGeometric)
				{
					geometric *= node->MTransformation;
				}
				else
				{
					local *= node->MTransformation;
				}

				int index = data.Nodes.Count;
				string name = HelperBase(node) ?? node->MName.AsString;
				var result = new ModelNode { Name = string.IsNullOrEmpty(name) ? "Node " + index : name, Parent = parent };
				Decompose(local, result);
				data.Nodes.Add(result);
				if (node->MNumMeshes > 0)
				{
					result.Mesh = MeshOf(node, result.Name, geometric);
				}

				for (uint i = 0; i < node->MNumChildren; i++)
				{
					Visit(node->MChildren[i], index);
				}

				foreach (IntPtr other in others)
				{
					Visit((AiNode*)other, index);
				}
			}

			private const string PivotHelperTag = "_$AssimpFbx$_";

			/// <summary>The model a pivot helper node belongs to, or null for any other node.</summary>
			private static string HelperBase(AiNode* node)
			{
				string name = node->MName.AsString;
				int at = name.IndexOf(PivotHelperTag, StringComparison.Ordinal);
				return at >= 0 ? name.Substring(0, at) : null;
			}

			private static string HelperPart(AiNode* node)
			{
				string name = node->MName.AsString;
				int at = name.IndexOf(PivotHelperTag, StringComparison.Ordinal);
				return at >= 0 ? name.Substring(at + PivotHelperTag.Length) : "";
			}

			/// <summary>Assimp stores aiMatrix4x4 row-major with the translation in the 4th column.</summary>
			private static void Decompose(NMatrix4x4 m, ModelNode node)
			{
				NMatrix4x4 rowVector = NMatrix4x4.Transpose(m);
				if (NMatrix4x4.Decompose(rowVector, out System.Numerics.Vector3 scale, out System.Numerics.Quaternion rotation, out System.Numerics.Vector3 translation))
				{
					node.LocalPosition = new Vector3(translation.X, translation.Y, translation.Z);
					node.LocalRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
					node.LocalScale = new Vector3(scale.X, scale.Y, scale.Z);
				}
				else
				{
					node.LocalPosition = new Vector3(rowVector.M41, rowVector.M42, rowVector.M43);
				}
			}

			// ------------------------------------------------------------ meshes

			/// <summary>
			///     The node's Assimp meshes as one mesh (a sub-mesh each), moved by <paramref name="geometric" /> (an FBX
			///     model's geometric transform, Assimp's layout); nodes sharing the same meshes and transform share it.
			/// </summary>
			private int MeshOf(AiNode* node, string nodeName, NMatrix4x4 geometric)
			{
				var sources = new uint[node->MNumMeshes];
				for (uint i = 0; i < node->MNumMeshes; i++)
				{
					sources[i] = node->MMeshes[i];
				}

				bool moved = !geometric.IsIdentity;
				string key = string.Join(",", sources) + (moved ? ":" + geometric : "");
				if (meshesByContent.TryGetValue(key, out int existing))
				{
					return existing;
				}

				// Normals move by the inverse transpose; a mirroring transform turns the triangles round.
				NMatrix4x4.Invert(geometric, out NMatrix4x4 inverse);
				bool mirrored = moved && geometric.GetDeterminant() < 0f;
				var vertices = new List<Vertex>();
				var indices = new List<uint>();
				var subMeshes = new List<SubMesh>();
				var materials = new List<int>();
				foreach (uint source in sources)
				{
					AiMesh* mesh = scene->MMeshes[source];
					if (mesh->MNumFaces == 0)
					{
						continue;
					}

					uint baseVertex = (uint)vertices.Count;
					for (uint v = 0; v < mesh->MNumVertices; v++)
					{
						Vertex vertex = ReadVertex(mesh, v);
						if (moved)
						{
							vertex = new Vertex(TransformPoint(geometric, vertex.position), TransformNormal(inverse, vertex.normal), vertex.texcoord, vertex.tangent,
								vertex.bitangent, vertex.color, vertex.texcoord2);
						}

						vertices.Add(vertex);
					}

					int first = indices.Count;
					for (uint f = 0; f < mesh->MNumFaces; f++)
					{
						Face face = mesh->MFaces[f];
						if (face.MNumIndices != 3)
						{
							continue;
						}

						indices.Add(baseVertex + face.MIndices[0]);
						indices.Add(baseVertex + face.MIndices[mirrored ? 2u : 1u]);
						indices.Add(baseVertex + face.MIndices[mirrored ? 1u : 2u]);
					}

					subMeshes.Add(new SubMesh(first, indices.Count - first));
					materials.Add(mesh->MMaterialIndex < data.Materials.Count ? (int)mesh->MMaterialIndex : -1);
				}

				if (subMeshes.Count == 0)
				{
					meshesByContent[key] = -1;
					return -1;
				}

				Vertex[] vertexArray = vertices.ToArray();
				uint[] indexArray = indices.ToArray();
				var result = new ModelMeshData
				{
					Name = UniqueName(nodeName),
					Vertices = LELEngine.Mesh.WithTangents(vertexArray, indexArray),
					Indices = indexArray,
					SubMeshes = subMeshes.ToArray(),
					Materials = materials.ToArray()
				};
				data.Meshes.Add(result);
				meshesByContent[key] = data.Meshes.Count - 1;
				return data.Meshes.Count - 1;
			}

			private static Vertex ReadVertex(AiMesh* mesh, uint v)
			{
				System.Numerics.Vector3 p = mesh->MVertices[v];
				Vector3 normal = mesh->MNormals != null ? ToVector(mesh->MNormals[v]) : Vector3.UnitY;
				Vector3 tangent = mesh->MTangents != null ? ToVector(mesh->MTangents[v]) : Vector3.UnitX;
				Vector3 bitangent = mesh->MBitangents != null ? ToVector(mesh->MBitangents[v]) : Vector3.UnitZ;
				System.Numerics.Vector3* uvs = mesh->MTextureCoords[0];
				Vector2 uv = uvs != null ? new Vector2(uvs[v].X, uvs[v].Y) : Vector2.Zero;
				System.Numerics.Vector3* uvs2 = mesh->MTextureCoords[1];
				Vector2 uv2 = uvs2 != null ? new Vector2(uvs2[v].X, uvs2[v].Y) : Vector2.Zero;
				System.Numerics.Vector4* colors = mesh->MColors[0];
				Color4 color = colors != null ? new Color4(colors[v].X, colors[v].Y, colors[v].Z, colors[v].W) : Color4.White;
				return new Vertex(new Vector3(p.X, p.Y, p.Z), normal, uv, tangent, bitangent, color, uv2);
			}

			private static Vector3 ToVector(System.Numerics.Vector3 v)
			{
				return new Vector3(v.X, v.Y, v.Z);
			}

			/// <summary>A point through a matrix in Assimp's layout (row-major, translation in the 4th column).</summary>
			private static Vector3 TransformPoint(NMatrix4x4 m, Vector3 p)
			{
				return new Vector3(m.M11 * p.X + m.M12 * p.Y + m.M13 * p.Z + m.M14, m.M21 * p.X + m.M22 * p.Y + m.M23 * p.Z + m.M24,
					m.M31 * p.X + m.M32 * p.Y + m.M33 * p.Z + m.M34);
			}

			/// <summary>A normal through the inverse transpose of a matrix, given its inverse in Assimp's layout.</summary>
			private static Vector3 TransformNormal(NMatrix4x4 inverse, Vector3 n)
			{
				var result = new Vector3(inverse.M11 * n.X + inverse.M21 * n.Y + inverse.M31 * n.Z, inverse.M12 * n.X + inverse.M22 * n.Y + inverse.M32 * n.Z,
					inverse.M13 * n.X + inverse.M23 * n.Y + inverse.M33 * n.Z);
				return result.LengthSquared > 0f ? result.Normalized() : n;
			}

			private string UniqueName(string name)
			{
				string candidate = string.IsNullOrEmpty(name) ? "Mesh" : name;
				for (int n = 2; !meshNames.Add(candidate); n++)
				{
					candidate = name + " " + n;
				}

				return candidate;
			}

			// ------------------------------------------------------------ materials

			private ModelMaterial ReadMaterial(AiMaterial* material, int index, string modelName)
			{
				var result = new ModelMaterial { Name = MaterialName(material, index, modelName) };
				System.Numerics.Vector4 color;
				if (assimp.GetMaterialColor(material, "$clr.base", 0, 0, &color) == Return.Success
					|| assimp.GetMaterialColor(material, "$clr.diffuse", 0, 0, &color) == Return.Success)
				{
					result.Color = new Vector4(color.X, color.Y, color.Z, color.W);
				}

				// Some exporters write opacity 0 for opaque materials; such a value is ignored.
				float opacity = Float(material, "$mat.opacity", 1f);
				if (opacity > 0.001f && opacity < 1f)
				{
					result.Color.W *= opacity;
				}

				if (assimp.GetMaterialColor(material, "$clr.emissive", 0, 0, &color) == Return.Success && (color.X > 0f || color.Y > 0f || color.Z > 0f))
				{
					result.Emissive = new Vector4(color.X, color.Y, color.Z, Float(material, "$mat.emissiveIntensity", 1f));
				}

				float roughness = Float(material, "$mat.roughnessFactor", -1f);
				if (roughness < 0f)
				{
					float shininess = Float(material, "$mat.shininess", -1f);
					roughness = shininess > 0f ? MathF.Sqrt(2f / (shininess + 2f)) : 0.6f;
				}

				result.Roughness = Math.Clamp(roughness, 0.02f, 1f);
				result.AlbedoMap = Texture(material, TextureType.BaseColor) ?? Texture(material, TextureType.Diffuse);
				result.NormalMap = Texture(material, TextureType.Normals) ?? Texture(material, TextureType.NormalCamera) ?? Texture(material, TextureType.Height);
				return result;
			}

			private string MaterialName(AiMaterial* material, int index, string modelName)
			{
				AssimpString name;
				if (assimp.GetMaterialString(material, "?mat.name", 0, 0, &name) == Return.Success && name.Length > 0)
				{
					return name.AsString;
				}

				return modelName + " Material " + index;
			}

			private float Float(AiMaterial* material, string key, float fallback)
			{
				float value;
				uint count = 1;
				return assimp.GetMaterialFloatArray(material, key, 0, 0, &value, &count) == Return.Success && count > 0 ? value : fallback;
			}

			private ModelTexture Texture(AiMaterial* material, TextureType type)
			{
				AssimpString path;
				if (assimp.GetMaterialTexture(material, type, 0, &path, null, null, null, null, null, null) != Return.Success)
				{
					return null;
				}

				string reference = path.AsString;
				if (string.IsNullOrEmpty(reference))
				{
					return null;
				}

				AiTexture* embedded = Embedded(reference);
				if (embedded != null)
				{
					return EmbeddedTexture(embedded, reference, Path.GetFileNameWithoutExtension(file));
				}

				string assetPath = FindTextureFile(reference);
				if (assetPath == null)
				{
					Debug.LogWarning($"[Model] {Path.GetFileName(file)}: texture '{reference}' not found in the project");
					return null;
				}

				return new ModelTexture { AssetPath = assetPath, Name = Path.GetFileNameWithoutExtension(assetPath) };
			}

			/// <summary>"*n" names the n-th embedded texture; FBX files name embedded textures by their file name.</summary>
			private AiTexture* Embedded(string reference)
			{
				if (reference[0] == '*' && int.TryParse(reference.AsSpan(1), out int index))
				{
					return index >= 0 && index < scene->MNumTextures ? scene->MTextures[index] : null;
				}

				string fileName = Path.GetFileName(reference.Replace('\\', '/'));
				for (uint i = 0; i < scene->MNumTextures; i++)
				{
					AiTexture* texture = scene->MTextures[i];
					if (string.Equals(Path.GetFileName(texture->MFilename.AsString.Replace('\\', '/')), fileName, StringComparison.OrdinalIgnoreCase))
					{
						return texture;
					}
				}

				return null;
			}

			/// <summary>
			///     A compressed embedded image as is; raw texels as an uncompressed TGA. Unnamed images (glTF) are named after
			///     the model, so two models in one folder do not share them.
			/// </summary>
			private static ModelTexture EmbeddedTexture(AiTexture* texture, string reference, string modelName)
			{
				string name = Path.GetFileNameWithoutExtension(texture->MFilename.AsString.Replace('\\', '/'));
				if (string.IsNullOrEmpty(name))
				{
					name = modelName + " Texture " + reference.TrimStart('*');
				}

				if (texture->MHeight == 0)
				{
					var bytes = new byte[texture->MWidth];
					new ReadOnlySpan<byte>(texture->PcData, bytes.Length).CopyTo(bytes);
					string hint = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(texture->AchFormatHint, 9).ToArray()).TrimEnd('\0');
					string extension = hint.Length > 0 ? "." + hint.ToLowerInvariant() : ".png";
					return new ModelTexture { Data = bytes, Extension = extension == ".jpeg" ? ".jpg" : extension, Name = name };
				}

				return new ModelTexture { Data = Tga((byte*)texture->PcData, (int)texture->MWidth, (int)texture->MHeight), Extension = ".tga", Name = name };
			}

			/// <summary>32-bit uncompressed TGA (BGRA texels, top-left origin), which the texture importer reads.</summary>
			private static byte[] Tga(byte* bgra, int width, int height)
			{
				var bytes = new byte[18 + width * height * 4];
				bytes[2] = 2;
				bytes[12] = (byte)(width & 0xFF);
				bytes[13] = (byte)(width >> 8);
				bytes[14] = (byte)(height & 0xFF);
				bytes[15] = (byte)(height >> 8);
				bytes[16] = 32;
				bytes[17] = 0x28;
				new ReadOnlySpan<byte>(bgra, width * height * 4).CopyTo(bytes.AsSpan(18));
				return bytes;
			}

			/// <summary>
			///     The project file a texture reference means: the path relative to the model, then the file name next to
			///     the model, in a Textures folder beside or above it, and anywhere in the project. Artists' files often
			///     reference the source image (.psd, .tga) of what the project has as .png or .jpg: where the exact file
			///     is missing, an image with the same base name counts.
			/// </summary>
			private string FindTextureFile(string reference)
			{
				string folder = Path.GetDirectoryName(file);
				string normalized = reference.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
				string fileName = Path.GetFileName(normalized);
				var candidates = new List<string>();
				try
				{
					candidates.Add(Path.IsPathRooted(normalized) ? normalized : Path.GetFullPath(Path.Combine(folder, normalized)));
				}
				catch (ArgumentException)
				{
				}

				candidates.Add(Path.Combine(folder, fileName));
				candidates.Add(Path.Combine(folder, "Textures", fileName));
				candidates.Add(Path.Combine(Path.GetDirectoryName(folder) ?? folder, "Textures", fileName));
				foreach (bool anyExtension in new[] { false, true })
				{
					foreach (string candidate in candidates.Distinct())
					{
						string found = anyExtension ? WithImageExtension(candidate) : System.IO.File.Exists(candidate) ? candidate : null;
						string assetPath = found != null ? AssetDatabase.ToAssetPath(found) : null;
						if (assetPath != null)
						{
							return assetPath;
						}
					}
				}

				string exact = AssetDatabase.Resolve(fileName, null);
				if (exact != null)
				{
					return exact;
				}

				// Anywhere in the project by base name, when only one image has it.
				string baseName = Path.GetFileNameWithoutExtension(fileName);
				List<string> matches = AssetDatabase.GetAllAssetPaths(TextureAssets.ImageExtensions)
					.Where(p => string.Equals(Path.GetFileNameWithoutExtension(p), baseName, StringComparison.OrdinalIgnoreCase)).ToList();
				return matches.Count == 1 ? matches[0] : null;
			}

			/// <summary>An existing image file with the candidate's folder and base name (any image extension).</summary>
			private static string WithImageExtension(string candidate)
			{
				string directory = Path.GetDirectoryName(candidate);
				string baseName = Path.GetFileNameWithoutExtension(candidate);
				if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
				{
					return null;
				}

				foreach (string extension in TextureAssets.ImageExtensions)
				{
					string path = Path.Combine(directory, baseName + extension);
					if (System.IO.File.Exists(path))
					{
						return path;
					}
				}

				return null;
			}

			// ------------------------------------------------------------ units

			/// <summary>Meters per file unit: FBX's UnitScaleFactor is in centimeters (1 = cm, 100 = m); others are meters.</summary>
			private float FileUnitScale()
			{
				Metadata* metadata = scene->MMetaData;
				if (metadata == null)
				{
					return 1f;
				}

				for (uint i = 0; i < metadata->MNumProperties; i++)
				{
					if (metadata->MKeys[i].AsString != "UnitScaleFactor")
					{
						continue;
					}

					MetadataEntry entry = metadata->MValues[i];
					double factor = entry.MType switch
					{
						MetadataType.Float => *(float*)entry.MData,
						MetadataType.Double => *(double*)entry.MData,
						MetadataType.Int32 => *(int*)entry.MData,
						_ => 100.0
					};
					return factor > 0 ? (float)(factor / 100.0) : 1f;
				}

				return 1f;
			}
		}

		#endregion
	}
}
