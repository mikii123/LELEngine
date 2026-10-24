using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.DistanceField
{
	/// <summary>
	///     A mesh distance field computed by the game build (the same GPU build the renderer runs), at one shape: the
	///     atlas uploads it instead of building it, which takes seconds for meshes of 100k triangles.
	/// </summary>
	public sealed class BakedDistanceField
	{
		#region PublicFields

		/// <summary>
		///     The shape it was built at (<see cref="DistanceFieldAtlas.ShapeKey" />). Files written when this was the
		///     renderer's scale key read the same: a key always stands for a field built at key / 1000.
		/// </summary>
		public Vector3i ShapeKey;

		/// <summary>Texels per axis, padding included (the atlas checks its own layout matches).</summary>
		public Vector3i Size;

		/// <summary>Half-float distances, x fastest.</summary>
		public ushort[] Voxels;

		#endregion
	}

	/// <summary>
	///     The baked distance fields of a model's meshes, next to the model in a game build ("model.fbx.sdf"; the model
	///     loader attaches them to the meshes, <see cref="Mesh.BakedDistanceFields" />). Also the editor's cache format.
	/// </summary>
	public static class DistanceFieldFile
	{
		#region PublicFields

		public const string Extension = ".sdf";

		#endregion

		#region PrivateFields

		private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LELSDF01");

		#endregion

		#region PublicMethods

		/// <summary>Writes fields by mesh name, with a stamp (the editor's cache: what they were built from).</summary>
		public static void Write(string path, string stamp, IReadOnlyList<KeyValuePair<string, BakedDistanceField>> fields)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
			using (var writer = new BinaryWriter(File.Create(path)))
			{
				writer.Write(Magic);
				writer.Write(stamp ?? "");
				writer.Write(fields.Count);
				foreach (KeyValuePair<string, BakedDistanceField> entry in fields)
				{
					BakedDistanceField field = entry.Value;
					writer.Write(entry.Key ?? "");
					writer.Write(field.ShapeKey.X);
					writer.Write(field.ShapeKey.Y);
					writer.Write(field.ShapeKey.Z);
					writer.Write(field.Size.X);
					writer.Write(field.Size.Y);
					writer.Write(field.Size.Z);
					writer.Write(field.Voxels.Length);
					byte[] bytes = new byte[field.Voxels.Length * 2];
					Buffer.BlockCopy(field.Voxels, 0, bytes, 0, bytes.Length);
					writer.Write(bytes);
				}
			}
		}

		/// <summary>The fields of a file by mesh name (null when it is not a distance field file).</summary>
		public static List<KeyValuePair<string, BakedDistanceField>> Read(string path, out string stamp)
		{
			stamp = null;
			using (var reader = new BinaryReader(File.OpenRead(path)))
			{
				byte[] magic = reader.ReadBytes(Magic.Length);
				if (!magic.AsSpan().SequenceEqual(Magic))
				{
					return null;
				}

				stamp = reader.ReadString();
				int count = reader.ReadInt32();
				var fields = new List<KeyValuePair<string, BakedDistanceField>>(count);
				for (int i = 0; i < count; i++)
				{
					string mesh = reader.ReadString();
					var field = new BakedDistanceField
					{
						ShapeKey = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
						Size = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32())
					};
					int length = reader.ReadInt32();
					field.Voxels = new ushort[length];
					byte[] bytes = reader.ReadBytes(length * 2);
					Buffer.BlockCopy(bytes, 0, field.Voxels, 0, bytes.Length);
					fields.Add(new KeyValuePair<string, BakedDistanceField>(mesh, field));
				}

				return fields;
			}
		}

		#endregion
	}
}
