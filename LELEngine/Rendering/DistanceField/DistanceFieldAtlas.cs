using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.DistanceField
{
	/// <summary>
	///     Signed distance field of one mesh at one shape, stored as a slab of the <see cref="DistanceFieldAtlas" />.
	///     The shape is a scale divided by its largest component (<see cref="DistanceFieldAtlas.ShapeKey" />), so every
	///     uniform scale of a mesh shares one field. Bounds and distances are in shape units: a renderer whose scale is
	///     u x <see cref="Shape" /> covers u x the bounds and multiplies the distances by u (rotation and translation are
	///     applied when sampling, as before).
	/// </summary>
	public sealed class MeshDistanceField
	{
		#region PublicFields

		public Mesh Mesh;

		/// <summary>The scale the field was built at: its largest component is 1.</summary>
		public Vector3 Shape;

		/// <summary>Shape-space bounds covered by the field, including padding.</summary>
		public Vector3 BoundsMin;

		public Vector3 BoundsMax;

		/// <summary>Texels per axis.</summary>
		public Vector3i Size;

		/// <summary>Per-axis texel size in shape units (the field is anisotropic for flat meshes).</summary>
		public Vector3 TexelSize;

		/// <summary>First atlas slice of this field; x and y start at 0.</summary>
		public int AtlasZ;

		/// <summary>The atlas frame the field was last used on (see <see cref="DistanceFieldAtlas.BeginFrame" />).</summary>
		public int LastUsedFrame;

		/// <summary>False once the atlas freed the field to make room: its slices may hold another field.</summary>
		public bool IsValid = true;

		#endregion
	}

	/// <summary>
	///     Builds and stores mesh distance fields in one R16F 3D texture (slabs stacked along Z), one field per mesh and
	///     shape (<see cref="ShapeKey" />: uniform scales share it). Fields are built on the GPU by brute force over the
	///     mesh triangles; the sign comes from the generalized winding number, which only needs consistently oriented
	///     closed meshes. Slices are handed out first fit from a free list. When no free range fits, the fields nobody
	///     used for <see cref="EvictAfterFrames" /> frames are freed (a field freed and needed again is uploaded from the
	///     copy kept on its mesh), then the texture doubles in depth up to <see cref="MaxDepth" />; only then does a
	///     request fail, reported once.
	/// </summary>
	public sealed class DistanceFieldAtlas
	{
		#region PublicFields

		/// <summary>Atlas width and height in texels; a field can use at most this many texels per axis.</summary>
		public const int AtlasXY = 64;

		/// <summary>Texels along the mesh's longest axis (excluding padding).</summary>
		public const int TexelsAlongMaxAxis = 40;

		public const int Padding = 2;

		/// <summary>Slices a renderer's atlas starts with (8 MB).</summary>
		public const int InitialDepth = 1024;

		/// <summary>Fields unused for this many frames are freed when the atlas needs their room.</summary>
		public const int EvictAfterFrames = 30;

		/// <summary>Shape keys are the shape's components in thousandths.</summary>
		public const float KeyScale = 1000f;

		public int Texture { get; private set; }

		/// <summary>Slices of the texture now.</summary>
		public int Depth { get; private set; }

		/// <summary>The depth the atlas may grow to: the context's 3D texture limit, at most 4096 slices (32 MB).</summary>
		public int MaxDepth { get; }

		/// <summary>Slices held by fields.</summary>
		public int UsedSlices { get; private set; }

		/// <summary>Fields the atlas holds.</summary>
		public int FieldCount { get; private set; }

		public Vector3 TexelCount => new Vector3(AtlasXY, AtlasXY, Depth);

		#endregion

		#region PrivateFields

		private const int GrowLimit = 4096;

		private readonly Dictionary<(Mesh, Vector3i), MeshDistanceField> fields = new Dictionary<(Mesh, Vector3i), MeshDistanceField>();

		// Free slice ranges, sorted by start, neighbours merged.
		private readonly List<(int Start, int Count)> free = new List<(int Start, int Count)>();
		private readonly List<(Mesh, Vector3i)> evicted = new List<(Mesh, Vector3i)>();
		private readonly ComputeShader build;
		private readonly int triangleBuffer;
		private int frame;

		// A request found no room this frame: later ones fail at once until the next frame.
		private int fullFrame = int.MinValue;
		private bool fullReported;

		#endregion

		#region Constructors

		public DistanceFieldAtlas(int depth = InitialDepth)
		{
			Depth = depth;
			MaxDepth = Math.Max(depth, Math.Min(GrowLimit, GLCapabilities.Max3DTextureSize));
			Texture = CreateTexture(depth);
			free.Add((0, depth));

			build = new ComputeShader("Engine/SdfBuild.shader");
			triangleBuffer = GL.GenBuffer();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     A scale's shape: its absolute value divided by its largest component, in thousandths. Every uniform scale is
		///     (1000, 1000, 1000); a zero scale too (such renderers have no size: <see cref="UniformScale" /> is 0).
		/// </summary>
		public static Vector3i ShapeKey(Vector3 scale)
		{
			Vector3 size = new Vector3(Math.Abs(scale.X), Math.Abs(scale.Y), Math.Abs(scale.Z));
			float largest = Math.Max(size.X, Math.Max(size.Y, size.Z));
			if (!(largest > 1e-12f))
			{
				return new Vector3i((int)KeyScale);
			}

			size /= largest;
			return new Vector3i((int)Math.Round(size.X * KeyScale), (int)Math.Round(size.Y * KeyScale), (int)Math.Round(size.Z * KeyScale));
		}

		/// <summary>The shape a key stands for (what its field is built at).</summary>
		public static Vector3 ShapeOf(Vector3i key)
		{
			return new Vector3(key.X, key.Y, key.Z) / KeyScale;
		}

		/// <summary>
		///     The factor a field of <paramref name="shape" /> is drawn at for a renderer of <paramref name="scale" />: the
		///     largest u for which u x shape fits inside the scale on every axis (the scale's largest component when the
		///     shape is the scale's own; smaller for another shape, so a borrowed field never covers more than the object).
		///     0 for a renderer without size.
		/// </summary>
		public static float UniformScale(Vector3 scale, Vector3 shape)
		{
			float uniform = float.MaxValue;
			for (int axis = 0; axis < 3; axis++)
			{
				if (shape[axis] > 1e-6f)
				{
					uniform = Math.Min(uniform, Math.Abs(scale[axis]) / shape[axis]);
				}
			}

			return uniform < float.MaxValue ? uniform : 0f;
		}

		/// <summary>Starts a frame: fields requested from now on count as used on it (unused ones may be freed for room).</summary>
		public void BeginFrame(int frameIndex)
		{
			frame = frameIndex;
		}

		/// <summary>Marks a field the caller keeps using as used this frame.</summary>
		public void Use(MeshDistanceField field)
		{
			if (field != null)
			{
				field.LastUsedFrame = frame;
			}
		}

		/// <summary>The field of a mesh at a shape when the atlas holds it (marked as used), else null. Builds nothing.</summary>
		public MeshDistanceField Find(Mesh mesh, Vector3i shapeKey)
		{
			if (fields.TryGetValue((mesh, shapeKey), out MeshDistanceField field))
			{
				field.LastUsedFrame = frame;
				return field;
			}

			return null;
		}

		/// <summary>
		///     The field of a mesh at a shape (<see cref="ShapeKey" />), built on first use (or uploaded from the one a game
		///     build baked or an earlier build kept, <see cref="Mesh.BakedDistanceFields" />). Null when there is no room
		///     even after freeing unused fields and growing.
		/// </summary>
		public MeshDistanceField GetOrBuild(Mesh mesh, Vector3i shapeKey)
		{
			MeshDistanceField field = Find(mesh, shapeKey);
			if (field != null || fullFrame == frame)
			{
				return field;
			}

			field = Build(mesh, shapeKey);
			if (field != null)
			{
				fields[(mesh, shapeKey)] = field;
			}

			return field;
		}

		/// <summary>The texels of a field as half floats, x fastest (build-time baking; waits for the GPU).</summary>
		public ushort[] Read(MeshDistanceField field)
		{
			var atlas = new ushort[AtlasXY * AtlasXY * Depth];
			GL.BindTexture(TextureTarget.Texture3D, Texture);
			GL.GetTexImage(TextureTarget.Texture3D, 0, PixelFormat.Red, PixelType.HalfFloat, atlas);
			GL.BindTexture(TextureTarget.Texture3D, 0);

			var voxels = new ushort[field.Size.X * field.Size.Y * field.Size.Z];
			for (int z = 0; z < field.Size.Z; z++)
			{
				for (int y = 0; y < field.Size.Y; y++)
				{
					Array.Copy(atlas, ((field.AtlasZ + z) * AtlasXY + y) * AtlasXY, voxels, (z * field.Size.Y + y) * field.Size.X, field.Size.X);
				}
			}

			return voxels;
		}

		/// <summary>Forgets every field (the slabs are reused); for build-time baking one field after another.</summary>
		public void Reset()
		{
			foreach (MeshDistanceField field in fields.Values)
			{
				field.IsValid = false;
			}

			fields.Clear();
			free.Clear();
			free.Add((0, Depth));
			UsedSlices = 0;
			FieldCount = 0;
			fullFrame = int.MinValue;
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
			foreach (MeshDistanceField field in fields.Values)
			{
				field.IsValid = false;
			}

			fields.Clear();
		}

		#endregion

		#region PrivateMethods

		private MeshDistanceField Build(Mesh mesh, Vector3i key)
		{
			Vector3 shape = ShapeOf(key);
			Vector3 scaledMin = mesh.BoundsMin * shape;
			Vector3 scaledMax = mesh.BoundsMax * shape;
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

			int atlasZ = Allocate(size.Z);
			if (atlasZ < 0)
			{
				ReportFull(mesh, size.Z);
				return null;
			}

			Vector3 center = (scaledMin + scaledMax) * 0.5f;
			Vector3 halfCovered = new Vector3(size.X * texelSize.X, size.Y * texelSize.Y, size.Z * texelSize.Z) * 0.5f;

			MeshDistanceField field = new MeshDistanceField
			{
				Mesh = mesh,
				Shape = shape,
				BoundsMin = center - halfCovered,
				BoundsMax = center + halfCovered,
				Size = size,
				TexelSize = texelSize,
				AtlasZ = atlasZ,
				LastUsedFrame = frame
			};
			UsedSlices += size.Z;
			FieldCount++;

			// A field baked by the game build or kept by an earlier build: an upload instead of the brute-force build
			// (seconds for big meshes).
			ushort[] baked = FindBaked(mesh, key, size);
			if (baked != null)
			{
				GL.BindTexture(TextureTarget.Texture3D, Texture);
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
				GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, field.AtlasZ, size.X, size.Y, size.Z, PixelFormat.Red, PixelType.HalfFloat, baked);
				GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
				GL.BindTexture(TextureTarget.Texture3D, 0);
				return field;
			}

			UploadTriangles(mesh, shape);
			Dispatch(field);
			KeepOnMesh(mesh, key, field);

			Console.WriteLine($"[SDF] Built {size.X}x{size.Y}x{size.Z} field ({mesh.TriangleCount} triangles) at atlas slice {field.AtlasZ}");
			return field;
		}

		// The first free range that fits; else the room of fields unused for a while; else a deeper texture. -1: none.
		private int Allocate(int slices)
		{
			int start = TakeFree(slices);
			if (start >= 0)
			{
				return start;
			}

			if (EvictUnused())
			{
				start = TakeFree(slices);
				if (start >= 0)
				{
					return start;
				}
			}

			while (Depth < MaxDepth)
			{
				Grow();
				start = TakeFree(slices);
				if (start >= 0)
				{
					return start;
				}
			}

			return -1;
		}

		private int TakeFree(int slices)
		{
			for (int i = 0; i < free.Count; i++)
			{
				(int start, int count) = free[i];
				if (count < slices)
				{
					continue;
				}

				if (count == slices)
				{
					free.RemoveAt(i);
				}
				else
				{
					free[i] = (start + slices, count - slices);
				}

				return start;
			}

			return -1;
		}

		private void Release(int start, int count)
		{
			int index = 0;
			while (index < free.Count && free[index].Start < start)
			{
				index++;
			}

			free.Insert(index, (start, count));
			// Merge with the next range, then with the previous one.
			if (index + 1 < free.Count && free[index].Start + free[index].Count == free[index + 1].Start)
			{
				free[index] = (free[index].Start, free[index].Count + free[index + 1].Count);
				free.RemoveAt(index + 1);
			}

			if (index > 0 && free[index - 1].Start + free[index - 1].Count == free[index].Start)
			{
				free[index - 1] = (free[index - 1].Start, free[index - 1].Count + free[index].Count);
				free.RemoveAt(index);
			}
		}

		// Frees the fields unused for EvictAfterFrames frames; true when one was freed.
		private bool EvictUnused()
		{
			evicted.Clear();
			foreach (KeyValuePair<(Mesh, Vector3i), MeshDistanceField> entry in fields)
			{
				if (frame - entry.Value.LastUsedFrame >= EvictAfterFrames)
				{
					evicted.Add(entry.Key);
				}
			}

			foreach ((Mesh, Vector3i) key in evicted)
			{
				MeshDistanceField field = fields[key];
				fields.Remove(key);
				field.IsValid = false;
				Release(field.AtlasZ, field.Size.Z);
				UsedSlices -= field.Size.Z;
				FieldCount--;
			}

			return evicted.Count > 0;
		}

		// Twice the slices (at most MaxDepth): the fields are copied over on the GPU, the new slices are free.
		private void Grow()
		{
			int depth = Math.Min(MaxDepth, Depth * 2);
			int texture = CreateTexture(depth);
			GL.CopyImageSubData(Texture, ImageTarget.Texture3D, 0, 0, 0, 0, texture, ImageTarget.Texture3D, 0, 0, 0, 0, AtlasXY, AtlasXY, Depth);
			GL.DeleteTexture(Texture);
			Texture = texture;
			Release(Depth, depth - Depth);
			Console.WriteLine($"[SDF] Distance field atlas grown to {depth} slices ({AtlasXY * AtlasXY * (long)depth * 2 / (1024 * 1024)} MB)");
			Depth = depth;
		}

		private void ReportFull(Mesh mesh, int slices)
		{
			fullFrame = frame;
			if (fullReported)
			{
				return;
			}

			fullReported = true;
			Debug.LogWarning($"[SDF] Distance field atlas full: {FieldCount} fields fill {UsedSlices} of {Depth} slices (the deepest atlas here), all used in the last {EvictAfterFrames} frames. '{mesh.name}' ({slices} slices) and the fields asked for after it are left out of the GI's distance field until room frees up (reported once).");
		}

		private static int CreateTexture(int depth)
		{
			int texture = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture3D, texture);
			GL.TexImage3D(TextureTarget.Texture3D, 0, PixelInternalFormat.R16f, AtlasXY, AtlasXY, depth, 0, PixelFormat.Red, PixelType.Float, IntPtr.Zero);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureWrapR, (int)TextureWrapMode.ClampToEdge);
			GL.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMaxLevel, 0);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			return texture;
		}

		/// <summary>
		///     A field built here is kept with the mesh (read back, GL 4.5): the next atlas that needs it (another view's
		///     renderer, the atlas after a scene switch, this one after freeing it) uploads it instead of building it again.
		/// </summary>
		private void KeepOnMesh(Mesh mesh, Vector3i key, MeshDistanceField field)
		{
			if (!GLCapabilities.DirectStateAccess)
			{
				return;
			}

			var voxels = new ushort[field.Size.X * field.Size.Y * field.Size.Z];
			GL.PixelStore(PixelStoreParameter.PackAlignment, 2);
			GL.GetTextureSubImage(Texture, 0, 0, 0, field.AtlasZ, field.Size.X, field.Size.Y, field.Size.Z, PixelFormat.Red, PixelType.HalfFloat, voxels.Length * 2, voxels);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			(mesh.BakedDistanceFields ??= new List<BakedDistanceField>()).Add(new BakedDistanceField { ShapeKey = key, Size = field.Size, Voxels = voxels });
		}

		private static ushort[] FindBaked(Mesh mesh, Vector3i key, Vector3i size)
		{
			List<BakedDistanceField> baked = mesh.BakedDistanceFields;
			if (baked == null)
			{
				return null;
			}

			foreach (BakedDistanceField field in baked)
			{
				if (field.ShapeKey == key && field.Size == size)
				{
					return field.Voxels;
				}
			}

			return null;
		}

		private void UploadTriangles(Mesh mesh, Vector3 shape)
		{
			Vector3[] triangles = mesh.GetTrianglePositions();
			int vertexCount = triangles.Length;
			var data = new Vector4[vertexCount];
			for (int i = 0; i < vertexCount; i++)
			{
				Vector3 p = triangles[i] * shape;
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
			build.Program.SetInt("triangleCount", field.Mesh.TriangleCount);

			build.DispatchThreads(field.Size.X, field.Size.Y, field.Size.Z, 4, 4, 4);
			ComputeShader.Barrier(MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit);
		}

		#endregion
	}
}
