using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>One multi-draw call of the opaque pass: the instanced variant of a shader and the uniform state its instances share.</summary>
	public sealed class OpaqueBucket
	{
		#region PublicFields

		/// <summary>Instanced variant of the materials' shader.</summary>
		public ShaderProgram Program { get; internal set; }

		/// <summary>Material whose own (non-standard) uniforms the call sets; null when the materials only have standard parameters.</summary>
		public Material Material { get; internal set; }

		public bool ReceiveShadows { get; internal set; }
		public bool ReceiveGI { get; internal set; }

		/// <summary>First instance of the bucket in the instance table, and their number.</summary>
		public int Start { get; internal set; }

		public int Count { get; internal set; }

		#endregion

		#region InternalFields

		internal readonly List<MeshRenderer> Members = new List<MeshRenderer>();

		#endregion
	}

	/// <summary>
	///     The frame's renderers on the GPU, for GPU-driven rendering: an instance table (world and normal matrices,
	///     local bounds, the mesh range in <see cref="GeometryPool" />, material index, flags), a material table (the
	///     standard parameters Color, Emissive, Roughness) and the opaque buckets (one per shader variant and shared
	///     uniform state). Rebuilt every frame; each view (camera, shadow map) is culled by Engine/InstanceCull.shader,
	///     which writes the draw commands, and drawn by one multi-draw indirect call per bucket.
	///     Native path: commands compacted in instance order, the draw count read by the GPU
	///     (glMultiDrawElementsIndirectCount), the instance index from gl_BaseInstance, the culling scan with subgroup
	///     operations. Fallbacks, each on its own: one command per instance with instance count 0 when culled, a
	///     per-instance attribute (see <see cref="GeometryPool" />), a shared memory scan.
	///     Holds no scene references between frames (<see cref="EndFrame" />), so an unloaded scene can be collected.
	/// </summary>
	public sealed class GpuScene : IDisposable
	{
		#region PublicFields

		public const int InstanceBinding = 10;
		public const int MaterialBinding = 11;
		public const int CommandBinding = 12;
		public const int CountBinding = 13;
		public const int SegmentBinding = 14;

		/// <summary>Instance flag: casts shadows.</summary>
		public const uint CastsShadowsFlag = 1;

		/// <summary>The context can run GPU-driven rendering (shader storage blocks in vertex and fragment shaders, bindings up to 14).</summary>
		public static bool IsSupported => GLCapabilities.MaxShaderStorageBindings > SegmentBinding && GLCapabilities.VertexShaderStorageBlocks >= 1 && GLCapabilities.FragmentShaderStorageBlocks >= 2;

		/// <summary>Built for this frame and not empty: the passes draw GPU-driven.</summary>
		public bool Active { get; private set; }

		public int InstanceCount { get; private set; }
		public int MaterialCount => materials.Count;

		/// <summary>CPU time of the last <see cref="Build" /> and its instance count (they survive <see cref="EndFrame" />).</summary>
		public double LastBuildMilliseconds { get; private set; }

		public int LastInstanceCount { get; private set; }
		public IReadOnlyList<OpaqueBucket> Buckets => activeBuckets;

		/// <summary>Renderers the opaque pass draws one by one: their shader has no instanced variant.</summary>
		public IReadOnlyList<MeshRenderer> PerObjectRenderers => perObject;

		#endregion

		#region PrivateFields

		private const int CommandSize = 5 * sizeof(uint);

		private readonly List<OpaqueBucket> bucketPool = new List<OpaqueBucket>();
		private readonly List<OpaqueBucket> activeBuckets = new List<OpaqueBucket>();
		private readonly List<MeshRenderer> perObject = new List<MeshRenderer>();
		private readonly List<MeshRenderer> unbucketed = new List<MeshRenderer>();
		private readonly List<Material> materials = new List<Material>();
		private readonly Dictionary<Material, int> materialIndices = new Dictionary<Material, int>();
		private readonly CullView cameraView = new CullView();
		private readonly CullView shadowView = new CullView();
		private GpuInstance[] instances = new GpuInstance[256];
		private GpuMaterial[] materialData = new GpuMaterial[64];
		private int instanceBuffer;
		private int materialBuffer;
		private ComputeShader cull;
		private bool cameraCulled;
		private bool reported;

		#endregion

		#region PublicMethods

		/// <summary>Builds and uploads the instance and material tables from the frame's renderers.</summary>
		public void Build(IReadOnlyList<MeshRenderer> renderers)
		{
			long started = System.Diagnostics.Stopwatch.GetTimestamp();
			try
			{
				BuildTables(renderers);
			}
			finally
			{
				LastBuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
				LastInstanceCount = InstanceCount;
			}
		}

		private void BuildTables(IReadOnlyList<MeshRenderer> renderers)
		{
			EndFrame();
			foreach (MeshRenderer renderer in renderers)
			{
				if (!GeometryPool.Prepare(renderer.Mesh))
				{
					continue;
				}

				Material material = renderer.Material;
				ShaderProgram instanced = material?.UsingShader?.GetInstancedVariant();
				if (instanced == null)
				{
					// Depth passes still draw it from the tables; the opaque pass draws it per object.
					unbucketed.Add(renderer);
					if (material?.UsingShader != null)
					{
						perObject.Add(renderer);
					}

					continue;
				}

				Bucket(instanced, material.HasOnlyStandardUniforms() ? null : material, renderer.ReceiveShadows, renderer.ReceiveGI).Members.Add(renderer);
			}

			materials.Add(null); // index 0: no material (the standard defaults)
			int count = unbucketed.Count;
			foreach (OpaqueBucket bucket in activeBuckets)
			{
				count += bucket.Members.Count;
			}

			if (instances.Length < count)
			{
				Array.Resize(ref instances, Math.Max(count, instances.Length * 2));
			}

			int index = 0;
			for (int b = 0; b < activeBuckets.Count; b++)
			{
				OpaqueBucket bucket = activeBuckets[b];
				bucket.Start = index;
				bucket.Count = bucket.Members.Count;
				foreach (MeshRenderer renderer in bucket.Members)
				{
					Write(index++, renderer, b);
				}
			}

			foreach (MeshRenderer renderer in unbucketed)
			{
				Write(index++, renderer, -1);
			}

			InstanceCount = index;
			Active = index > 0;
			cameraCulled = false;
			if (!Active)
			{
				return;
			}

			Upload();
			if (!reported)
			{
				reported = true;
				Console.WriteLine("[GpuScene] GPU-driven rendering: draw count " + (GLCapabilities.IndirectParameters ? "from the GPU (indirect count)" : "fixed (fallback: culled draws have 0 instances)")
					+ ", instance index " + (GLCapabilities.ShaderDrawParameters ? "gl_BaseInstance" : "per-instance attribute (fallback)")
					+ ", culling scan " + (GLCapabilities.ComputeSubgroups ? "subgroups" : "shared memory (fallback)"));
			}
		}

		/// <summary>Culls the camera view once per frame: per-bucket commands (opaque pass) and all instances (depth prepass).</summary>
		public void EnsureCameraCulled(Matrix4 viewProjection)
		{
			if (!Active || cameraCulled)
			{
				return;
			}

			cameraCulled = true;
			cameraView.Segments.Clear();
			foreach (OpaqueBucket bucket in activeBuckets)
			{
				cameraView.Segments.Add(new CullSegment { InstanceStart = (uint)bucket.Start, InstanceCount = (uint)bucket.Count, CommandOffset = (uint)bucket.Start, RequiredFlags = 0 });
			}

			cameraView.Segments.Add(new CullSegment { InstanceStart = 0, InstanceCount = (uint)InstanceCount, CommandOffset = (uint)InstanceCount, RequiredFlags = 0 });
			Dispatch(cameraView, viewProjection, InstanceCount * 2);
		}

		/// <summary>Culls the shadow casters against the light's view.</summary>
		public void CullShadows(Matrix4 lightViewProjection)
		{
			if (!Active)
			{
				return;
			}

			shadowView.Segments.Clear();
			shadowView.Segments.Add(new CullSegment { InstanceStart = 0, InstanceCount = (uint)InstanceCount, CommandOffset = 0, RequiredFlags = CastsShadowsFlag });
			Dispatch(shadowView, lightViewProjection, InstanceCount);
		}

		/// <summary>Every visible instance with the bound program (depth prepass). Needs <see cref="EnsureCameraCulled" />.</summary>
		public void DrawCameraAll()
		{
			Draw(cameraView, cameraView.Segments.Count - 1);
		}

		/// <summary>The visible instances of one opaque bucket with the bound program.</summary>
		public void DrawCameraBucket(int bucket)
		{
			Draw(cameraView, bucket);
		}

		/// <summary>The shadow casters inside the light's view with the bound program. Needs <see cref="CullShadows" />.</summary>
		public void DrawShadowCasters()
		{
			Draw(shadowView, 0);
		}

		/// <summary>Drops every reference to renderers and materials (end of the frame, scene replaced).</summary>
		public void EndFrame()
		{
			foreach (OpaqueBucket bucket in activeBuckets)
			{
				bucket.Members.Clear();
				bucket.Program = null;
				bucket.Material = null;
			}

			activeBuckets.Clear();
			perObject.Clear();
			unbucketed.Clear();
			materials.Clear();
			materialIndices.Clear();
			Active = false;
			InstanceCount = 0;
		}

		public void Dispose()
		{
			EndFrame();
			DeleteBuffer(ref instanceBuffer);
			DeleteBuffer(ref materialBuffer);
			cameraView.Delete();
			shadowView.Delete();
			cull?.Delete();
			cull = null;
		}

		#endregion

		#region PrivateMethods

		private OpaqueBucket Bucket(ShaderProgram program, Material uniformSource, bool receiveShadows, bool receiveGI)
		{
			foreach (OpaqueBucket existing in activeBuckets)
			{
				if (existing.Program == program && existing.Material == uniformSource && existing.ReceiveShadows == receiveShadows && existing.ReceiveGI == receiveGI)
				{
					return existing;
				}
			}

			if (bucketPool.Count <= activeBuckets.Count)
			{
				bucketPool.Add(new OpaqueBucket());
			}

			OpaqueBucket bucket = bucketPool[activeBuckets.Count];
			bucket.Program = program;
			bucket.Material = uniformSource;
			bucket.ReceiveShadows = receiveShadows;
			bucket.ReceiveGI = receiveGI;
			activeBuckets.Add(bucket);
			return bucket;
		}

		private void Write(int index, MeshRenderer renderer, int bucket)
		{
			Mesh mesh = renderer.Mesh;
			Matrix4 model = renderer.transform.LocalToWorld;
			instances[index] = new GpuInstance
			{
				Model = model,
				NormalMatrix = NormalMatrix(model),
				BoundsMin = new Vector4(mesh.BoundsMin, 0f),
				BoundsMax = new Vector4(mesh.BoundsMax, 0f),
				IndexCount = (uint)mesh.PoolIndexCount,
				FirstIndex = (uint)mesh.PoolFirstIndex,
				BaseVertex = (uint)mesh.PoolBaseVertex,
				Flags = renderer.CastShadows ? CastsShadowsFlag : 0u,
				Material = (uint)MaterialIndex(renderer.Material),
				Bucket = bucket < 0 ? uint.MaxValue : (uint)bucket
			};
		}

		/// <summary>
		///     What GLSL computes as transpose(inverse(model)), uploaded as a matrix the shader reads directly (OpenTK's
		///     row-vector matrices reach GLSL transposed).
		/// </summary>
		private static Matrix4 NormalMatrix(Matrix4 model)
		{
			return Math.Abs(model.Determinant) < 1e-30f ? Matrix4.Identity : Matrix4.Transpose(model.Inverted());
		}

		private int MaterialIndex(Material material)
		{
			if (material == null)
			{
				return 0;
			}

			if (!materialIndices.TryGetValue(material, out int index))
			{
				index = materials.Count;
				materials.Add(material);
				materialIndices.Add(material, index);
			}

			return index;
		}

		private void Upload()
		{
			if (materialData.Length < materials.Count)
			{
				Array.Resize(ref materialData, Math.Max(materials.Count, materialData.Length * 2));
			}

			materialData[0] = new GpuMaterial { Color = Vector4.One };
			for (int i = 1; i < materials.Count; i++)
			{
				materials[i].GetStandardParameters(out Vector4 color, out Vector4 emissive, out float roughness);
				materialData[i] = new GpuMaterial { Color = color, Emissive = emissive, Parameters = new Vector4(roughness, 0f, 0f, 0f) };
			}

			if (instanceBuffer == 0)
			{
				instanceBuffer = GL.GenBuffer();
				materialBuffer = GL.GenBuffer();
			}

			// Re-specified every frame (orphaning): the driver gives a fresh store while the last frame still reads.
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, instanceBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, InstanceCount * Marshal.SizeOf<GpuInstance>(), instances, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, materialBuffer);
			GL.BufferData(BufferTarget.ShaderStorageBuffer, materials.Count * Marshal.SizeOf<GpuMaterial>(), materialData, BufferUsageHint.StreamDraw);
			GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			GeometryPool.EnsureInstanceIndices(InstanceCount);
		}

		private void BindTables()
		{
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, InstanceBinding, instanceBuffer);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, MaterialBinding, materialBuffer);
		}

		private void Dispatch(CullView view, Matrix4 viewProjection, int commandCount)
		{
			cull ??= new ComputeShader("Engine/InstanceCull.shader");
			view.Upload(commandCount);
			BindTables();
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CommandBinding, view.CommandBuffer);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, CountBinding, view.CountBuffer);
			GL.BindBufferBase(BufferRangeTarget.ShaderStorageBuffer, SegmentBinding, view.SegmentBuffer);
			cull.Use();
			cull.Program.SetVector4Array("frustumPlanes", FrustumPlanes(viewProjection));
			cull.Program.SetInt("compactCommands", GLCapabilities.IndirectParameters ? 1 : 0);
			cull.Dispatch(view.Segments.Count);
			// The commands and the counts are read by the following indirect draws.
			GL.MemoryBarrier(MemoryBarrierFlags.CommandBarrierBit);
		}

		private void Draw(CullView view, int segment)
		{
			if (!Active || segment < 0 || segment >= view.Segments.Count)
			{
				return;
			}

			CullSegment range = view.Segments[segment];
			if (range.InstanceCount == 0)
			{
				return;
			}

			BindTables();
			GeometryPool.Bind();
			GL.BindBuffer(BufferTarget.DrawIndirectBuffer, view.CommandBuffer);
			if (GLCapabilities.IndirectParameters)
			{
				GL.BindBuffer(BufferTarget.ParameterBuffer, view.CountBuffer);
				GL.MultiDrawElementsIndirectCount(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (IntPtr)(range.CommandOffset * CommandSize), (IntPtr)(segment * sizeof(uint)), (int)range.InstanceCount, CommandSize);
				GL.BindBuffer(BufferTarget.ParameterBuffer, 0);
			}
			else
			{
				GL.MultiDrawElementsIndirect(PrimitiveType.Triangles, DrawElementsType.UnsignedInt, (IntPtr)(range.CommandOffset * CommandSize), (int)range.InstanceCount, CommandSize);
			}

			GL.BindBuffer(BufferTarget.DrawIndirectBuffer, 0);
		}

		/// <summary>World-space planes (inside: dot(n, p) + d >= 0) of a row-vector view-projection matrix.</summary>
		private static Vector4[] FrustumPlanes(Matrix4 m)
		{
			Vector4 c0 = m.Column0;
			Vector4 c1 = m.Column1;
			Vector4 c2 = m.Column2;
			Vector4 c3 = m.Column3;
			var planes = new[] { c3 + c0, c3 - c0, c3 + c1, c3 - c1, c3 + c2, c3 - c2 };
			for (int i = 0; i < planes.Length; i++)
			{
				float length = planes[i].Xyz.Length;
				if (length > 0f)
				{
					planes[i] /= length;
				}
			}

			return planes;
		}

		private static void DeleteBuffer(ref int buffer)
		{
			if (buffer != 0)
			{
				GL.DeleteBuffer(buffer);
				buffer = 0;
			}
		}

		#endregion

		#region NestedTypes

		[StructLayout(LayoutKind.Sequential)]
		private struct GpuInstance
		{
			public Matrix4 Model;
			public Matrix4 NormalMatrix;
			public Vector4 BoundsMin;
			public Vector4 BoundsMax;
			public uint IndexCount;
			public uint FirstIndex;
			public uint BaseVertex;
			public uint Flags;
			public uint Material;
			public uint Bucket;
			public uint Unused0;
			public uint Unused1;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct GpuMaterial
		{
			public Vector4 Color;
			public Vector4 Emissive;
			public Vector4 Parameters;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct CullSegment
		{
			public uint InstanceStart;
			public uint InstanceCount;
			public uint CommandOffset;
			public uint RequiredFlags;
		}

		/// <summary>Command, count and segment buffers of one culled view.</summary>
		private sealed class CullView
		{
			public readonly List<CullSegment> Segments = new List<CullSegment>();
			public int CommandBuffer;
			public int CountBuffer;
			public int SegmentBuffer;
			private int commandCapacity;
			private int countCapacity;

			public void Upload(int commandCount)
			{
				if (CommandBuffer == 0)
				{
					CommandBuffer = GL.GenBuffer();
					CountBuffer = GL.GenBuffer();
					SegmentBuffer = GL.GenBuffer();
				}

				if (commandCount > commandCapacity)
				{
					commandCapacity = Math.Max(commandCount, commandCapacity * 2);
					GL.BindBuffer(BufferTarget.ShaderStorageBuffer, CommandBuffer);
					GL.BufferData(BufferTarget.ShaderStorageBuffer, commandCapacity * CommandSize, IntPtr.Zero, BufferUsageHint.DynamicDraw);
				}

				if (Segments.Count > countCapacity)
				{
					countCapacity = Math.Max(Segments.Count, countCapacity * 2);
					GL.BindBuffer(BufferTarget.ShaderStorageBuffer, CountBuffer);
					GL.BufferData(BufferTarget.ShaderStorageBuffer, countCapacity * sizeof(uint), IntPtr.Zero, BufferUsageHint.DynamicDraw);
				}

				CullSegment[] data = Segments.ToArray();
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, SegmentBuffer);
				GL.BufferData(BufferTarget.ShaderStorageBuffer, data.Length * Marshal.SizeOf<CullSegment>(), data, BufferUsageHint.StreamDraw);
				GL.BindBuffer(BufferTarget.ShaderStorageBuffer, 0);
			}

			public void Delete()
			{
				DeleteBuffer(ref CommandBuffer);
				DeleteBuffer(ref CountBuffer);
				DeleteBuffer(ref SegmentBuffer);
				commandCapacity = 0;
				countCapacity = 0;
			}
		}

		#endregion
	}
}
