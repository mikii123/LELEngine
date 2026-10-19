using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LELEngine.Rendering;
using LELEngine.Rendering.DistanceField;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Editor
{
	/// <summary>
	///     Bakes the mesh distance fields of a game build: every mesh its scenes draw for the GI, at the scale each
	///     renderer has (and at scale 1, for copies made at runtime), with the renderer's own GPU build. The player
	///     uploads them instead of building them, which takes seconds for big meshes right when a scene appears.
	///     Baking needs the GL context on the calling thread; without it only cached fields are used. Fields are cached
	///     in Library/DistanceFieldCache, valid while the model file, its meta file and the field layout are the same.
	/// </summary>
	internal static class DistanceFieldBaker
	{
		#region PublicFields

		/// <summary>What one collection found.</summary>
		public sealed class Result
		{
			/// <summary>Per model asset path, the fields of its meshes by mesh name.</summary>
			public readonly Dictionary<string, List<KeyValuePair<string, BakedDistanceField>>> Models = new Dictionary<string, List<KeyValuePair<string, BakedDistanceField>>>(StringComparer.OrdinalIgnoreCase);

			public int Baked;
			public int Cached;

			/// <summary>Fields neither cached nor bakeable here (no GL context): the player builds them.</summary>
			public int Missing;

			public override string ToString()
			{
				return $"Distance fields: {Baked + Cached} ({Baked} baked, {Cached} from the cache{(Missing > 0 ? $", {Missing} left to the player" : "")}).";
			}
		}

		#endregion

		#region PrivateFields

		private const int Version = 1;

		private struct Request
		{
			public string ModelPath;
			public string MeshName;
			public Vector3 Scale;
		}

		#endregion

		#region PublicMethods

		/// <summary>The fields of the meshes the scenes (asset paths) draw: baked now when this thread has the GL context, else from the cache.</summary>
		public static Result Collect(ProjectInfo project, IEnumerable<string> scenes, Action<string> log)
		{
			var result = new Result();
			var requests = new Dictionary<string, Request>(StringComparer.OrdinalIgnoreCase);
			foreach (string scene in scenes)
			{
				try
				{
					CollectRequests(scene, requests);
				}
				catch (Exception e)
				{
					log("Warning: the distance fields of " + scene + " could not be listed (" + e.Message + ").");
				}
			}

			DistanceFieldAtlas atlas = null;
			try
			{
				foreach (Request request in requests.Values)
				{
					Vector3i key = DistanceFieldAtlas.ScaleKey(request.Scale);
					string cacheFile = CacheFile(project, request, key);
					string stamp = Stamp(request.ModelPath);
					BakedDistanceField field = ReadCache(cacheFile, stamp);
					if (field != null)
					{
						result.Cached++;
					}
					else if (GraphicsThread.HasContext)
					{
						Mesh mesh = ModelAssets.LoadMesh(request.ModelPath, request.MeshName);
						if (mesh == null || mesh.TriangleCount == 0)
						{
							continue;
						}

						atlas ??= new DistanceFieldAtlas(DistanceFieldAtlas.AtlasXY);
						atlas.Reset();
						MeshDistanceField built = atlas.GetOrBuild(mesh, request.Scale);
						if (built == null)
						{
							continue;
						}

						field = new BakedDistanceField { ScaleKey = key, Size = built.Size, Voxels = atlas.Read(built) };
						DistanceFieldFile.Write(cacheFile, stamp, new[] { new KeyValuePair<string, BakedDistanceField>(request.MeshName, field) });
						result.Baked++;
					}
					else
					{
						result.Missing++;
						continue;
					}

					if (!result.Models.TryGetValue(request.ModelPath, out List<KeyValuePair<string, BakedDistanceField>> fields))
					{
						fields = new List<KeyValuePair<string, BakedDistanceField>>();
						result.Models[request.ModelPath] = fields;
					}

					fields.Add(new KeyValuePair<string, BakedDistanceField>(request.MeshName, field));
				}
			}
			finally
			{
				atlas?.Delete();
			}

			return result;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     The GI meshes of a scene file and their renderers' world scales (the product of the local scales up the
		///     hierarchy, as <see cref="Transform.lossyScale" /> computes it), plus scale 1 for each mesh.
		/// </summary>
		private static void CollectRequests(string scenePath, Dictionary<string, Request> requests)
		{
			byte[] data = File.ReadAllBytes(AssetDatabase.ToAbsolute(scenePath));
			if (!Documents.IsJson(data))
			{
				return;
			}

			using (JsonDocument document = JsonDocument.Parse(data))
			{
				if (!document.RootElement.TryGetProperty("gameObjects", out JsonElement objects) || objects.ValueKind != JsonValueKind.Array)
				{
					return;
				}

				var parents = new Dictionary<ulong, ulong>();
				var localScales = new Dictionary<ulong, Vector3>();
				var renderers = new List<(ulong Owner, string ModelPath, string MeshName)>();
				foreach (JsonElement entry in objects.EnumerateArray())
				{
					ulong id = entry.TryGetProperty("id", out JsonElement idValue) && idValue.TryGetUInt64(out ulong parsed) ? parsed : 0;
					parents[id] = entry.TryGetProperty("parent", out JsonElement parentValue) && parentValue.TryGetUInt64(out ulong parent) ? parent : 0;
					if (!entry.TryGetProperty("components", out JsonElement components) || components.ValueKind != JsonValueKind.Array)
					{
						continue;
					}

					foreach (JsonElement component in components.EnumerateArray())
					{
						string type = component.TryGetProperty("$type", out JsonElement typeValue) ? typeValue.GetString() ?? "" : "";
						if (type.StartsWith("Transform,", StringComparison.Ordinal) && component.TryGetProperty("m_LocalScale", out JsonElement scale) && scale.GetArrayLength() == 3)
						{
							localScales[id] = new Vector3(scale[0].GetSingle(), scale[1].GetSingle(), scale[2].GetSingle());
						}
						else if (type.StartsWith("MeshRenderer,", StringComparison.Ordinal)
							&& !(component.TryGetProperty("ContributesToGI", out JsonElement gi) && gi.ValueKind == JsonValueKind.False)
							&& component.TryGetProperty("mesh", out JsonElement mesh) && mesh.ValueKind == JsonValueKind.Object)
						{
							string guid = mesh.TryGetProperty("guid", out JsonElement g) ? g.GetString() : null;
							string path = mesh.TryGetProperty("path", out JsonElement p) ? p.GetString() : null;
							string name = mesh.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
							string modelPath = AssetDatabase.GetPath(guid) ?? path;
							if (modelPath != null && ModelAssets.IsModelFile(modelPath))
							{
								renderers.Add((id, modelPath, name));
							}
						}
					}
				}

				foreach ((ulong owner, string modelPath, string meshName) in renderers)
				{
					Add(requests, modelPath, meshName, WorldScale(owner, parents, localScales, 0));
					Add(requests, modelPath, meshName, Vector3.One);
				}
			}
		}

		private static Vector3 WorldScale(ulong id, Dictionary<ulong, ulong> parents, Dictionary<ulong, Vector3> localScales, int depth)
		{
			Vector3 local = localScales.TryGetValue(id, out Vector3 scale) ? scale : Vector3.One;
			ulong parent = parents.TryGetValue(id, out ulong p) ? p : 0;
			return parent == 0 || depth > 64 || !parents.ContainsKey(parent) ? local : WorldScale(parent, parents, localScales, depth + 1) * local;
		}

		private static void Add(Dictionary<string, Request> requests, string modelPath, string meshName, Vector3 scale)
		{
			Vector3i key = DistanceFieldAtlas.ScaleKey(scale);
			requests[$"{modelPath}|{meshName}|{key.X},{key.Y},{key.Z}"] = new Request { ModelPath = modelPath, MeshName = meshName, Scale = scale };
		}

		private static string CacheFile(ProjectInfo project, Request request, Vector3i key)
		{
			string guid = AssetDatabase.GetGuid(request.ModelPath) ?? Hash(request.ModelPath);
			return Path.Combine(project.LibraryPath, "DistanceFieldCache", $"{guid}-{Hash(request.MeshName ?? "")}-{key.X}_{key.Y}_{key.Z}{DistanceFieldFile.Extension}");
		}

		/// <summary>What a field depends on: the model file and its import settings, the field layout and this baker.</summary>
		private static string Stamp(string modelPath)
		{
			string absolute = AssetDatabase.ToAbsolute(modelPath);
			var info = new FileInfo(absolute);
			string meta = absolute + AssetDatabase.MetaExtension;
			string metaHash = File.Exists(meta) ? Hash(File.ReadAllText(meta)) : "";
			return $"{Version}|{DistanceFieldAtlas.TexelsAlongMaxAxis}|{DistanceFieldAtlas.Padding}|{DistanceFieldAtlas.AtlasXY}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{metaHash}";
		}

		private static BakedDistanceField ReadCache(string file, string stamp)
		{
			if (!File.Exists(file))
			{
				return null;
			}

			try
			{
				List<KeyValuePair<string, BakedDistanceField>> fields = DistanceFieldFile.Read(file, out string cachedStamp);
				return fields != null && fields.Count == 1 && cachedStamp == stamp ? fields[0].Value : null;
			}
			catch (Exception)
			{
				return null;
			}
		}

		private static string Hash(string text)
		{
			return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
		}

		#endregion
	}
}
