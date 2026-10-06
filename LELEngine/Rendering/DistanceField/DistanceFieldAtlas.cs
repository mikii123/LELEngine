using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.DistanceField
{
	/// <summary>
	///     Signed distance field of one mesh at one scale, stored as a slab of the <see cref="DistanceFieldAtlas" />.
	///     Distances are in world units in "scaled local" space: the renderer's scale is baked into the field,
	///     so only rotation and translation (distance preserving) are applied when sampling.
	/// </summary>
	public sealed class MeshDistanceField
	{
		#region PublicFields

		public Mesh Mesh;
		public Vector3 Scale;

		/// <summary>Scaled-local bounds covered by the field, including padding.</summary>
		public Vector3 BoundsMin;

		public Vector3 BoundsMax;

		/// <summary>Texels per axis.</summary>
		public Vector3i Size;

		/// <summary>Per-axis texel size in world units (the field is anisotropic for flat meshes).</summary>
		public Vector3 TexelSize;

		/// <summary>First atlas slice of this field; x and y start at 0.</summary>
		public int AtlasZ;

		#endregion
	}

	/// <summary>
	///     Builds and stores mesh distance fields in one R16F 3D texture (slabs stacked along Z).
	///     Fields are built on the GPU by brute force over the mesh triangles; the sign comes from the
	///     generalized winding number, which only needs consistently oriented closed meshes.
	/// </summary>
	public sealed class DistanceFieldAtlas
	{
		#region PublicFields

		/// <summary>Atlas width and height in texels; a field can use at most this many texels per axis.</summary>
		public const int AtlasXY = 64;

		/// <summary>Texels along the mesh's longest axis (excluding padding).</summary>
		public const int TexelsAlongMaxAxis = 40;

		public const int Padding = 2;

		public int Texture { get; private set; }
		public int Depth { get; }
		public int UsedSlices => nextZ;
		public Vector3 TexelCount => new Vector3(AtlasXY, AtlasXY, Depth);

		#endregion

		#region PrivateFields

		private readonly Dictionary<(Mesh, Vector3i), MeshDistanceField> fields = new Dictionary<(Mesh, Vector3i), MeshDistanceField>();
		private readonly ComputeShader build;
		private readonly int triangleBuffer;
		private int nextZ;

		#endregion

		#region Constructors

		public DistanceFieldAtlas(int depth = 1024)
		{
			Depth = depth;

			Texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, Texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16f, AtlasXY, AtlasXY, depth, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);

			build = new ComputeShader("Engine/SdfBuild.shader");
			triangleBuffer = GL.GenBuffer();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Returns the field for a mesh at a scale, building it on first use. Null when the atlas is full.
		/// </summary>
		public MeshDistanceField GetOrBuild(Mesh mesh, Vector3 scale)
		{
			Vector3 absScale = new Vector3(Math.Abs(scale.X), Math.Abs(scale.Y), Math.Abs(scale.Z));
			// Quantize so tiny float noise in transforms does not create duplicate fields.
			Vector3i key = new Vector3i((int)Math.Round(absScale.X * 1000f), (int)Math.Round(absScale.Y * 1000f), (int)Math.Round(absScale.Z * 1000f));

			MeshDistanceField field;
			if (fields.TryGetValue((mesh, key), out field))
			{
				return field;
			}

			field = Build(mesh, absScale);
			fields[(mesh, key)] = field;
			return field;
		}

		public void Delete()
		{
			if (Texture != 0)
			{
				GL.DeleteTexture(Texture);
				Texture = 0;
			}
			GL.DeleteBuffer(triangleBuffer);
			build.Delete();
			fields.Clear();
		}

		#endregion

		#region PrivateMethods

		private MeshDistanceField Build(Mesh mesh, Vector3 scale)
		{
			Vector3 scaledMin = mesh.BoundsMin * scale;
			Vector3 scaledMax = mesh.BoundsMax * scale;
			Vector3 extent = scaledMax - scaledMin;

			float maxExtent = Math.Max(extent.X, Math.Max(extent.Y, extent.Z));
			if (maxExtent <= 0f)
			{
				maxExtent = 1f;
			}
			float targetTexel = maxExtent / TexelsAlongMaxAxis;

			// Flat meshes still get a few texels across so trilinear filtering can find the zero crossing.
			int maxInner = AtlasXY - 2 * Padding;
			Vector3i inner = new Vector3i(
				Math.Clamp((int)Math.Ceiling(extent.X / targetTexel), 1, maxInner),
				Math.Clamp((int)Math.Ceiling(extent.Y / targetTexel), 1, maxInner),
				Math.Clamp((int)Math.Ceiling(extent.Z / targetTexel), 1, maxInner));
			Vector3 texelSize = new Vector3(
				Math.Max(extent.X, targetTexel) / inner.X,
				Math.Max(extent.Y, targetTexel) / inner.Y,
				Math.Max(extent.Z, targetTexel) / inner.Z);
			Vector3i size = inner + new Vector3i(2 * Padding);

			if (nextZ + size.Z > Depth)
			{
				Console.WriteLine("[SDF] Atlas full, cannot build distance field for mesh with " + mesh.Verticies.Count / 3 + " triangles");
				return null;
			}

			Vector3 center = (scaledMin + scaledMax) * 0.5f;
			Vector3 halfCovered = new Vector3(size.X * texelSize.X, size.Y * texelSize.Y, size.Z * texelSize.Z) * 0.5f;

			MeshDistanceField field = new MeshDistanceField
			{
				Mesh = mesh,
				Scale = scale,
				BoundsMin = center - halfCovered,
				BoundsMax = center + halfCovered,
				Size = size,
				TexelSize = texelSize,
				AtlasZ = nextZ
			};
			nextZ += size.Z;

			UploadTriangles(mesh, scale);
			Dispatch(field);

			Console.WriteLine($"[SDF] Built {size.X}x{size.Y}x{size.Z} field ({mesh.Verticies.Count / 3} triangles) at atlas slice {field.AtlasZ}");
			return field;
		}

		private void UploadTriangles(Mesh mesh, Vector3 scale)
		{
			int vertexCount = mesh.Verticies.Count / 3 * 3;
			var data = new Vector4[vertexCount];
			for (int i = 0; i < vertexCount; i++)
			{
				Vector3 p = mesh.Verticies[i].position * scale;
				data[i] = new Vector4(p, 0f);
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, triangleBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, vertexCount * 16, data, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
		}

		private void Dispatch(MeshDistanceField field)
		{
			build.Use();
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 0, triangleBuffer);
			GL.BindImageTexture(0, Texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);

			build.Program.SetInt3("atlasOffset", 0, 0, field.AtlasZ);
			build.Program.SetInt3("sdfSize", field.Size.X, field.Size.Y, field.Size.Z);
			build.Program.SetVector3("boundsMin", field.BoundsMin);
			build.Program.SetVector3("sdfTexelSize", field.TexelSize);
			build.Program.SetInt("triangleCount", field.Mesh.Verticies.Count / 3);

			build.DispatchThreads(field.Size.X, field.Size.Y, field.Size.Z, 4, 4, 4);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
