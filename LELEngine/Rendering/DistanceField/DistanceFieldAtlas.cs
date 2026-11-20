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

		/// <summary>
		///     False while its texels are still being computed (a big mesh's build goes on over frames,
		///     <see cref="DistanceFieldAtlas.PairsPerFrame" />): the atlas hands a field out only once it is built.
		/// </summary>
		public bool IsBuilt;

		#endregion
	}

	/// <summary>
	///     Mesh distance fields kept between runs: an atlas asks it for a mesh's field before building it by brute force
	///     and hands it every field it built (<see cref="DistanceFieldAtlas.Store" />). The editor keeps them in the
	///     project's Library (the cache game builds bake their fields into), so a field is computed once, not in every
	///     session; players get their fields baked next to the models and need none.
	/// </summary>
	public interface IDistanceFieldStore
	{
		/// <summary>
		///     The stored field of a mesh at a shape (<see cref="DistanceFieldAtlas.ShapeKey" />) with <paramref name="size" />
		///     texels, or null. Called on the GL thread inside a frame: a lookup and one small file at most.
		/// </summary>
		BakedDistanceField Load(Mesh mesh, Vector3i shapeKey, Vector3i size);

		/// <summary>A field was built for the mesh (GL thread; the store writes it where it likes).</summary>
		void Save(Mesh mesh, BakedDistanceField field);
	}

	/// <summary>
	///     Builds and stores mesh distance fields in one R16F 3D texture (slabs stacked along Z), one field per mesh and
	///     shape (<see cref="ShapeKey" />: uniform scales share it). Fields are built on the GPU by brute force over the
	///     mesh triangles; the sign comes from the generalized winding number, which only needs consistently oriented
	///     closed meshes. A build tests every texel against every triangle, in dispatches of at most
	///     <see cref="PairsPerDispatch" /> pairs and at most <see cref="PairsPerFrame" /> a frame: a field of a few
	///     thousand triangles is built when asked for, one of millions over frames (the renderer has none meanwhile).
	///     Slices are handed out first fit from a free list. When no free range fits, the fields nobody used for
	///     <see cref="EvictAfterFrames" /> frames are freed (a field freed and needed again is uploaded from the copy kept
	///     on its mesh), then the texture doubles in depth up to <see cref="MaxDepth" />; only then does a request fail,
	///     reported once.
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

		/// <summary>
		///     Texel x triangle pairs one build dispatch tests at most: a build takes its mesh's triangles in chunks of this
		///     many over the field's texels. One dispatch over all of them ran for seconds with a mesh of millions of
		///     triangles (hundreds of models merged by the Mesh Baker), and Windows reset the GPU's driver (TDR, after
		///     2 s), which ended the process.
		/// </summary>
		public const long PairsPerDispatch = 1L << 27;

		/// <summary>
		///     Texel x triangle pairs the builds of one frame test at most (a frame's first chunk runs whatever its size: at
		///     most <see cref="PairsPerDispatch" />), the smallest build first; what is left goes on in the next frames
		///     (<see cref="BeginFrame" />).
		/// </summary>
		public const long PairsPerFrame = 1L << 27;

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

		/// <summary>Fields kept between runs (null: none; see <see cref="IDistanceFieldStore" />). Set by the host.</summary>
		public static IDistanceFieldStore Store { get; set; }

		/// <summary>
		///     A field built here is read back and kept with its mesh, and given to the <see cref="Store" /> (the renderers'
		///     atlases). Off for an atlas whose owner reads the fields itself (the build's baker).
		/// </summary>
		public bool KeepsFields { get; set; } = true;

		/// <summary>
		///     A build goes on over frames once it has spent the frame's <see cref="PairsPerFrame" /> (on by default). Off: a
		///     build runs whole when asked for, still in dispatches of <see cref="PairsPerDispatch" /> (the build's baker,
		///     which reads each field at once).
		/// </summary>
		public bool BuildsOverFrames { get; set; } = true;

		/// <summary>Builds still going on (their fields not handed out yet).</summary>
		public int PendingBuildCount => pending.Count;

		#endregion

		#region PrivateFields

		private const int GrowLimit = 4096;

		// A built field on its way back from the GPU: copied into a buffer when it was built, taken out of it once the
		// GPU got there (BeginFrame looks), so no frame waits for the build to finish.
		private struct Readback
		{
			public Mesh Mesh;
			public Vector3i Key;
			public Vector3i Size;
			public int Buffer;
			public IntPtr Sync;
		}

		private readonly List<Readback> readbacks = new List<Readback>();

		// A field being built: its triangles on the GPU, every texel's nearest distance and winding so far, and how far the
		// chunks got.
		private sealed class PendingBuild
		{
			public MeshDistanceField Field;
			public Vector3i Key;
			public int Triangles;
			public int Partial;
			public int TriangleCount;
			public int ChunkTriangles;
			public int NextTriangle;
			public long Texels;
			public int Dispatches;
			public int FirstFrame;

			public long RemainingPairs => (long)(TriangleCount - NextTriangle) * Texels;

			// The next chunk (a mesh without triangles still has one, which writes the field).
			public long NextChunkPairs => Math.Max(Math.Min(ChunkTriangles, TriangleCount - NextTriangle), 1) * Texels;
		}

		// Triangles uploaded a block at a time (no copy of the whole mesh's positions).
		private const int UploadBlockVertices = 3 * 65536;

		// Bytes of one triangle in the build's buffer (three vec4).
		private const int TriangleBytes = 48;

		private readonly List<PendingBuild> pending = new List<PendingBuild>();
		private long spentThisFrame;

		private readonly Dictionary<(Mesh, Vector3i), MeshDistanceField> fields = new Dictionary<(Mesh, Vector3i), MeshDistanceField>();

		// Free slice ranges, sorted by start, neighbours merged.
		private readonly List<(int Start, int Count)> free = new List<(int Start, int Count)>();
		private readonly List<(Mesh, Vector3i)> evicted = new List<(Mesh, Vector3i)>();
		private readonly ComputeShader build;
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

		/// <summary>
		///     Starts a frame: fields requested from now on count as used on it (unused ones may be freed for room), the
		///     fields built in earlier frames that the GPU has finished are taken over (kept with their meshes, stored), and
		///     the builds still going on take the frame's <see cref="PairsPerFrame" />, the smallest first. A build nobody
		///     asked for in <see cref="EvictAfterFrames" /> frames (its renderer went) is dropped.
		/// </summary>
		public void BeginFrame(int frameIndex)
		{
			frame = frameIndex;
			spentThisFrame = 0;
			if (readbacks.Count > 0)
			{
				CollectReadbacks(false);
			}

			if (pending.Count > 0)
			{
				ContinueBuilds();
			}
		}

		/// <summary>Marks a field the caller keeps using as used this frame.</summary>
		public void Use(MeshDistanceField field)
		{
			if (field != null)
			{
				field.LastUsedFrame = frame;
			}
		}

		/// <summary>
		///     The field of a mesh at a shape when the atlas holds it built (marked as used), else null. Builds nothing; a
		///     build going on counts as asked for (it is not dropped).
		/// </summary>
		public MeshDistanceField Find(Mesh mesh, Vector3i shapeKey)
		{
			if (fields.TryGetValue((mesh, shapeKey), out MeshDistanceField field))
			{
				field.LastUsedFrame = frame;
				return field.IsBuilt ? field : null;
			}

			return null;
		}

		/// <summary>
		///     The field of a mesh at a shape (<see cref="ShapeKey" />), built on first use (or uploaded from the one a game
		///     build baked or an earlier build kept, <see cref="Mesh.BakedDistanceFields" />). Null while its build goes on
		///     over frames (<see cref="PairsPerFrame" />), and when there is no room even after freeing unused fields and
		///     growing.
		/// </summary>
		public MeshDistanceField GetOrBuild(Mesh mesh, Vector3i shapeKey)
		{
			if (fields.ContainsKey((mesh, shapeKey)))
			{
				return Find(mesh, shapeKey);
			}

			if (fullFrame == frame)
			{
				return null;
			}

			MeshDistanceField field = Build(mesh, shapeKey);
			if (field == null)
			{
				return null;
			}

			fields[(mesh, shapeKey)] = field;
			return field.IsBuilt ? field : null;
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
			DropBuilds();
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
			// The fields still on their way are dropped (their meshes build or load them again when needed).
			foreach (Readback readback in readbacks)
			{
				GL.DeleteSync(readback.Sync);
				GL.DeleteBuffer(readback.Buffer);
			}

			readbacks.Clear();
			DropBuilds();
			if (Texture != 0)
			{
				GL.DeleteTexture(Texture);
				Texture = 0;
			}
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
			long started = FrameProfiler.Timestamp;
			string meshName = (mesh.AssetPath ?? "") + "#" + (mesh.SubAssetName ?? mesh.name);
			ushort[] baked = FindBaked(mesh, key, size);
			if (baked == null && KeepsFields && Store != null)
			{
				// Built in an earlier run (the editor's cache): kept with the mesh from now on, like a build's baked field.
				BakedDistanceField stored = null;
				try
				{
					stored = Store.Load(mesh, key, size);
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[SDF] The stored distance field of {meshName} could not be read ({e.Message}); it is built.");
				}

				if (stored != null && stored.Size == size && stored.Voxels != null && stored.Voxels.Length == size.X * size.Y * size.Z)
				{
					stored.ShapeKey = key;
					(mesh.BakedDistanceFields ??= new List<BakedDistanceField>()).Add(stored);
					baked = stored.Voxels;
				}
			}

			if (baked != null)
			{
				UploadVoxels(field, baked);
				// Only with the hitch profiler on (its text is not built otherwise).
				if (started != 0)
				{
					FrameProfiler.EventSince(started, "distance field", meshName, $"baked or kept field uploaded ({size.X}x{size.Y}x{size.Z})");
				}
				return field;
			}

			long texels = (long)size.X * size.Y * size.Z;
			var job = new PendingBuild
			{
				Field = field,
				Key = key,
				TriangleCount = mesh.TriangleCount,
				// Whole multiples of 64 triangles: every chunk's range of the buffer starts 3,072 bytes apart, aligned for
				// any GPU's storage buffer offsets.
				ChunkTriangles = (int)Math.Max(64L, PairsPerDispatch / texels / 64 * 64),
				Texels = texels,
				FirstFrame = frame,
				Triangles = UploadTriangles(mesh, shape),
				Partial = GL.GenBuffer()
			};
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, job.Partial);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)(texels * 8), IntPtr.Zero, BufferUsageHint.StreamCopy);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			pending.Add(job);

			long pairs = job.RemainingPairs;
			Run(job);
			// Only with the hitch profiler on (its text is not built otherwise).
			if (started != 0)
			{
				FrameProfiler.EventSince(started, "distance field", meshName, field.IsBuilt
					? $"BUILT on the GPU: {size.X}x{size.Y}x{size.Z} texels x {mesh.TriangleCount} triangles in {job.Dispatches} dispatches (the build queued, its texels read back in a later frame)"
					: $"BUILD STARTED on the GPU: {size.X}x{size.Y}x{size.Z} texels x {mesh.TriangleCount} triangles, {pairs:N0} pairs, {job.Dispatches} dispatches this frame, the rest over the next frames");
			}

			if (!field.IsBuilt)
			{
				Console.WriteLine($"[SDF] Building a {size.X}x{size.Y}x{size.Z} field ({mesh.TriangleCount:N0} triangles, {pairs:N0} texel x triangle pairs) over frames, {PairsPerFrame:N0} pairs a frame");
			}

			return field;
		}

		/// <summary>
		///     The builds going on take what the frame has left of <see cref="PairsPerFrame" />, the one with the least left to
		///     do first (a field asked for meanwhile is not held up by a huge one); builds nobody asked for in
		///     <see cref="EvictAfterFrames" /> frames are dropped.
		/// </summary>
		private void ContinueBuilds()
		{
			for (int i = pending.Count - 1; i >= 0; i--)
			{
				PendingBuild job = pending[i];
				if (frame - job.Field.LastUsedFrame >= EvictAfterFrames)
				{
					DropBuild(job);
				}
			}

			pending.Sort((a, b) => a.RemainingPairs.CompareTo(b.RemainingPairs));
			// Copied: a build that ends leaves the list.
			foreach (PendingBuild job in pending.ToArray())
			{
				if (spentThisFrame >= PairsPerFrame)
				{
					break;
				}

				Run(job);
			}
		}

		/// <summary>
		///     Runs chunks of a build until it is done or the next chunk does not fit in what the frame has left of
		///     <see cref="PairsPerFrame" /> (the frame's first chunk always runs; with <see cref="BuildsOverFrames" /> off
		///     the build runs whole). A build whose field another atlas finished meanwhile (kept on the mesh) takes that one.
		/// </summary>
		private void Run(PendingBuild job)
		{
			MeshDistanceField field = job.Field;
			ushort[] kept = job.Dispatches > 0 ? FindBaked(field.Mesh, job.Key, field.Size) : null;
			if (kept != null)
			{
				UploadVoxels(field, kept);
				EndBuild(job, false, "the copy another atlas built and kept on the mesh");
				return;
			}

			while (!field.IsBuilt)
			{
				if (BuildsOverFrames && spentThisFrame > 0 && spentThisFrame + job.NextChunkPairs > PairsPerFrame)
				{
					break;
				}

				spentThisFrame += DispatchChunk(job);
			}
		}

		/// <summary>One chunk of a build's triangles over all its texels; the last one writes the field. The pairs it tested.</summary>
		private long DispatchChunk(PendingBuild job)
		{
			MeshDistanceField field = job.Field;
			int count = Math.Min(job.ChunkTriangles, job.TriangleCount - job.NextTriangle);
			bool first = job.NextTriangle == 0;
			bool last = job.NextTriangle + count >= job.TriangleCount;

			build.Use();
			// Only this chunk's triangles are bound: a range stays under the smallest storage block GL allows (128 MB)
			// whatever the mesh (the chunk is at most PairsPerDispatch / the field's 125 texels or more, 51 MB).
			GL.BindBufferRange(BufferRangeTarget.ShaderStorageBuffer, 0, job.Triangles, (IntPtr)((long)job.NextTriangle * TriangleBytes), Math.Max(count, 1) * TriangleBytes);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, 1, job.Partial);
			GL.BindImageTexture(0, Texture, 0, true, 0, TextureAccess.WriteOnly, SizedInternalFormat.R16f);

			build.Program.SetInt3("atlasOffset", 0, 0, field.AtlasZ);
			build.Program.SetInt3("sdfSize", field.Size.X, field.Size.Y, field.Size.Z);
			build.Program.SetVector3("boundsMin", field.BoundsMin);
			build.Program.SetVector3("sdfTexelSize", field.TexelSize);
			build.Program.SetInt("chunkTriangles", count);
			build.Program.SetInt("firstChunk", first ? 1 : 0);
			build.Program.SetInt("lastChunk", last ? 1 : 0);

			// A work group per texel.
			build.Dispatch(field.Size.X, field.Size.Y, field.Size.Z);
			ComputeShader.Barrier(last
				? MemoryBarrierFlags.ShaderImageAccessBarrierBit | MemoryBarrierFlags.TextureFetchBarrierBit | MemoryBarrierFlags.TextureUpdateBarrierBit
				: MemoryBarrierFlags.ShaderStorageBarrierBit);
			// Each chunk goes to the GPU on its own: the driver can schedule other work (the desktop) between them.
			GL.Flush();

			job.NextTriangle += count;
			job.Dispatches++;
			if (last)
			{
				EndBuild(job, KeepsFields);
			}

			return Math.Max(count, 1) * job.Texels;
		}

		// The build is done: its field is handed out from now on (and read back to be kept, when asked). Source: where the
		// texels came from when the build did not finish them itself.
		private void EndBuild(PendingBuild job, bool keep, string source = null)
		{
			MeshDistanceField field = job.Field;
			field.IsBuilt = true;
			pending.Remove(job);
			GL.DeleteBuffer(job.Triangles);
			GL.DeleteBuffer(job.Partial);
			if (keep)
			{
				KeepOnMesh(field.Mesh, job.Key, field);
			}

			string how = source != null
				? $", taken from {source} after {job.Dispatches} dispatches"
				: frame != job.FirstFrame
					? $" in {job.Dispatches} dispatches over {frame - job.FirstFrame + 1} frames"
					: job.Dispatches > 1
						? $" in {job.Dispatches} dispatches"
						: "";
			Console.WriteLine($"[SDF] Built {field.Size.X}x{field.Size.Y}x{field.Size.Z} field ({job.TriangleCount} triangles) at atlas slice {field.AtlasZ}{how}");
		}

		// A build nobody wants any more: its buffers and slices go.
		private void DropBuild(PendingBuild job)
		{
			MeshDistanceField field = job.Field;
			pending.Remove(job);
			GL.DeleteBuffer(job.Triangles);
			GL.DeleteBuffer(job.Partial);
			fields.Remove((field.Mesh, job.Key));
			field.IsValid = false;
			Release(field.AtlasZ, field.Size.Z);
			UsedSlices -= field.Size.Z;
			FieldCount--;
			Console.WriteLine($"[SDF] Dropped the build of a {field.Size.X}x{field.Size.Y}x{field.Size.Z} field ({job.TriangleCount} triangles): not asked for in {EvictAfterFrames} frames");
		}

		// Every build going on stops (Reset, Delete): only their buffers go, the caller forgets the fields.
		private void DropBuilds()
		{
			foreach (PendingBuild job in pending)
			{
				GL.DeleteBuffer(job.Triangles);
				GL.DeleteBuffer(job.Partial);
				job.Field.IsValid = false;
			}

			pending.Clear();
		}

		private void UploadVoxels(MeshDistanceField field, ushort[] voxels)
		{
			GL.BindTexture(TextureTarget.Texture3D, Texture);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 2);
			GL.TexSubImage3D(TextureTarget.Texture3D, 0, 0, 0, field.AtlasZ, field.Size.X, field.Size.Y, field.Size.Z, PixelFormat.Red, PixelType.HalfFloat, voxels);
			GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
			GL.BindTexture(TextureTarget.Texture3D, 0);
			field.IsBuilt = true;
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
				// A build going on writes its slices (an unwanted one is dropped by BeginFrame).
				if (entry.Value.IsBuilt && frame - entry.Value.LastUsedFrame >= EvictAfterFrames)
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
		///     renderer, the atlas after a scene switch, this one after freeing it) uploads it instead of building it again,
		///     and the <see cref="Store" /> keeps it for the next run. The texels are copied into a buffer on the GPU, behind
		///     the build, and taken out of it in a later frame, once the GPU has done both (<see cref="CollectReadbacks" />):
		///     reading them here made the frame wait for the build, 40 to 60 ms a field with the GPU busy.
		/// </summary>
		private void KeepOnMesh(Mesh mesh, Vector3i key, MeshDistanceField field)
		{
			if (!GLCapabilities.DirectStateAccess)
			{
				return;
			}

			int bytes = field.Size.X * field.Size.Y * field.Size.Z * 2;
			int buffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.PixelPackBuffer, buffer);
			GL.BufferData(BufferTarget.PixelPackBuffer, bytes, IntPtr.Zero, BufferUsageHint.StreamRead);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 2);
			// With a pack buffer bound the "pixels" pointer is an offset into it.
			GL.GetTextureSubImage(Texture, 0, 0, 0, field.AtlasZ, field.Size.X, field.Size.Y, field.Size.Z, PixelFormat.Red, PixelType.HalfFloat, bytes, IntPtr.Zero);
			GL.PixelStore(PixelStoreParameter.PackAlignment, 4);
			GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
			IntPtr sync = GL.FenceSync(SyncCondition.SyncGpuCommandsComplete, WaitSyncFlags.None);
			readbacks.Add(new Readback { Mesh = mesh, Key = key, Size = field.Size, Buffer = buffer, Sync = sync });
		}

		/// <summary>
		///     Takes over the built fields the GPU has finished (<paramref name="wait" />: all of them, waiting for it): each is
		///     kept with its mesh and handed to the <see cref="Store" />.
		/// </summary>
		private void CollectReadbacks(bool wait)
		{
			for (int i = 0; i < readbacks.Count; i++)
			{
				Readback readback = readbacks[i];
				WaitSyncStatus status = GL.ClientWaitSync(readback.Sync, wait ? ClientWaitSyncFlags.SyncFlushCommandsBit : ClientWaitSyncFlags.None, wait ? 1_000_000_000L : 0L);
				if (status == WaitSyncStatus.TimeoutExpired)
				{
					// In build order: the later ones are not done either.
					break;
				}

				GL.DeleteSync(readback.Sync);
				if (status != WaitSyncStatus.WaitFailed)
				{
					var voxels = new ushort[readback.Size.X * readback.Size.Y * readback.Size.Z];
					GL.GetNamedBufferSubData(readback.Buffer, IntPtr.Zero, voxels.Length * 2, voxels);
					var kept = new BakedDistanceField { ShapeKey = readback.Key, Size = readback.Size, Voxels = voxels };
					if (FindBaked(readback.Mesh, readback.Key, readback.Size) == null)
					{
						(readback.Mesh.BakedDistanceFields ??= new List<BakedDistanceField>()).Add(kept);
					}

					try
					{
						Store?.Save(readback.Mesh, kept);
					}
					catch (Exception e)
					{
						Debug.LogWarning($"[SDF] The distance field of {readback.Mesh.name} could not be stored ({e.Message}).");
					}
				}

				GL.DeleteBuffer(readback.Buffer);
				readbacks.RemoveAt(i--);
			}
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

		/// <summary>
		///     A buffer of the mesh's triangles at the shape, three vec4 each, filled a block at a time straight from the
		///     mesh's vertices and indices (not through <see cref="Mesh.GetTrianglePositions" />, which keeps its copy on the
		///     mesh for good: hundreds of megabytes for a mesh of millions of triangles).
		/// </summary>
		private static int UploadTriangles(Mesh mesh, Vector3 shape)
		{
			Vertex[] vertices = mesh.Vertices;
			uint[] indices = mesh.Indices;
			int count = mesh.TriangleCount * 3;
			int buffer = GL.GenBuffer();
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, buffer);
			// At least one triangle's room: a chunk binds that much even of a mesh without triangles.
			GL.BufferData(BufferTarget.ShaderStorageBuffer, (IntPtr)Math.Max((long)count * 16, TriangleBytes), IntPtr.Zero, BufferUsageHint.StaticDraw);
			var block = new Vector4[Math.Max(1, Math.Min(count, UploadBlockVertices))];
			for (int start = 0; start < count; start += block.Length)
			{
				int n = Math.Min(block.Length, count - start);
				for (int i = 0; i < n; i++)
				{
					block[i] = new Vector4(vertices[indices[start + i]].position * shape, 0f);
				}

				GL.BufferSubData(BufferTarget.ShaderStorageBuffer, (IntPtr)((long)start * 16), n * 16, block);
			}

			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			return buffer;
		}

		#endregion
	}
}
