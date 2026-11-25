using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     Geometry a GI pass draws for itself with one program (the voxelization, the grid shadow map), drawn the way the
	///     GPU scene draws: an instance per renderer in a table of <see cref="GpuScene" />'s layout (Engine/Instancing.glsl's
	///     LelInstance, bound at <see cref="GpuScene.InstanceBinding" />), read by the program's instanced variant
	///     (<see cref="Shaders.ShaderProgram.InstancedDefine" />), and one multi-draw indirect call per batch of draws, whose
	///     commands the CPU writes. Nothing is culled on the GPU: a batch draws what its pass picked. So a batch costs one
	///     call where every sub-mesh was a call of its own with its model matrix set.
	///     A table of its own, not the GPU scene's: the GI draws its renderers (level 0 of every LOD group, those out of the
	///     camera's view and layers too), which the cameras' tables do not hold.
	///     An instance holds its model matrix and its mesh's range in the pool only (no normal matrix, material, layer or
	///     flags): the programs drawing from these tables work out the rest from the model (as they did from the per-object
	///     model matrix, so the results are the same).
	///     GL thread. A table is uploaded once a frame at most; a pass that draws twice in a frame uses two tables (an upload
	///     would replace what the first draws still read on the OpenGL device).
	/// </summary>
	internal sealed class GIDrawTable : IDisposable
	{
		#region PublicFields

		/// <summary>One VkDrawIndexedIndirectCommand: index count, instance count, first index, vertex offset, first instance.</summary>
		public const int CommandSize = 5 * sizeof(uint);

		/// <summary>
		///     The GI's passes draw through these tables: the device runs GPU-driven programs (<see cref="GpuScene.IsSupported" />)
		///     and LEL_GI_GPU_DRAWS is not 0 (0: every renderer one by one, as before; to compare pictures and costs).
		/// </summary>
		public static bool Enabled => GpuScene.IsSupported && !DisabledByEnvironment;

		/// <summary>Instances added since <see cref="Clear" />.</summary>
		public int InstanceCount => instanceCount;

		/// <summary>Batches ended since <see cref="Clear" />.</summary>
		public int BatchCount => batches.Count;

		#endregion

		#region PrivateFields

		private static readonly bool DisabledByEnvironment = Environment.GetEnvironmentVariable("LEL_GI_GPU_DRAWS") == "0";

		private readonly List<Batch> batches = new List<Batch>();
		private readonly string name;
		private GpuScene.GpuInstance[] instances = new GpuScene.GpuInstance[256];
		private DrawCommand[] commands = new DrawCommand[256];
		private int instanceCount;
		private int commandCount;
		private int batchStart;
		private GpuBuffer instanceBuffer;
		private GpuBuffer commandBuffer;

		#endregion

		#region Constructors

		public GIDrawTable(string name)
		{
			this.name = name;
		}

		#endregion

		#region PublicMethods

		/// <summary>Starts the table again (no instance, no draw, no batch).</summary>
		public void Clear()
		{
			instanceCount = 0;
			commandCount = 0;
			batchStart = 0;
			batches.Clear();
		}

		/// <summary>
		///     Adds the renderer as an instance: its world matrix and the mesh the GPU draws for it
		///     (<see cref="MeshRenderer.DrawnMesh" />: the twin wound the other way under a mirroring transform), uploaded into
		///     the pool if it was not. -1 when that mesh has no triangles (nothing to draw).
		/// </summary>
		public int AddInstance(MeshRenderer renderer)
		{
			Mesh mesh = renderer.DrawnMesh;
			if (!GeometryPool.Prepare(mesh))
			{
				return -1;
			}

			if (instanceCount == instances.Length)
			{
				Array.Resize(ref instances, instances.Length * 2);
			}

			instances[instanceCount] = new GpuScene.GpuInstance
			{
				Model = renderer.transform.LocalToWorld,
				NormalMatrix = Matrix4.Identity,
				BoundsMin = new Vector4(mesh.BoundsMin, 0f),
				BoundsMax = new Vector4(mesh.BoundsMax, 0f),
				IndexCount = (uint)mesh.PoolIndexCount,
				FirstIndex = (uint)mesh.PoolFirstIndex,
				BaseVertex = (uint)mesh.PoolBaseVertex,
				Bucket = uint.MaxValue
			};
			return instanceCount++;
		}

		/// <summary>A draw of one sub-mesh of an instance's mesh (nothing for an empty one).</summary>
		public void AddSubMesh(int instance, Mesh mesh, int subMesh)
		{
			SubMesh range = mesh.SubMeshes[subMesh];
			if (range.IndexCount > 0)
			{
				AddDraw(instance, mesh.PoolFirstIndex + range.FirstIndex, range.IndexCount, mesh.PoolBaseVertex);
			}
		}

		/// <summary>A draw of an instance's whole mesh (every sub-mesh in one range, as <see cref="GeometryPool.Draw" />).</summary>
		public void AddWholeMesh(int instance, Mesh mesh)
		{
			AddDraw(instance, mesh.PoolFirstIndex, mesh.PoolIndexCount, mesh.PoolBaseVertex);
		}

		/// <summary>Ends a batch: the draws added since the batch before. Its index for <see cref="Draw" />; an empty batch draws nothing.</summary>
		public int EndBatch()
		{
			batches.Add(new Batch { Start = batchStart, Count = commandCount - batchStart });
			batchStart = commandCount;
			return batches.Count - 1;
		}

		/// <summary>Sends the instances and the draws to the GPU (once, after the last batch and before the first draw).</summary>
		public void Upload()
		{
			if (commandCount == 0)
			{
				return;
			}

			if (instanceBuffer == null)
			{
				GraphicsDevice device = Gfx.Device;
				instanceBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, name + " instances"));
				commandBuffer = device.CreateBuffer(new BufferDescriptor(BufferUsage.Indirect, 0, true, name + " draws"));
			}

			instanceBuffer.SetData(instances, instanceCount);
			commandBuffer.SetData(commands, commandCount);
			// Without the draw parameters the instance index is a per-instance attribute read from an identity buffer.
			GeometryPool.EnsureInstanceIndices(instanceCount);
		}

		/// <summary>One batch's draws in one call, with the bound program (an instanced variant reading this table).</summary>
		public void Draw(int batch)
		{
			Batch range = batches[batch];
			if (range.Count == 0)
			{
				return;
			}

			CommandList list = Gfx.Commands;
			list.SetStorageBuffer(GpuScene.InstanceBinding, instanceBuffer);
			GeometryPool.Bind();
			list.DrawIndexedIndirect(commandBuffer, (long)range.Start * CommandSize, range.Count, CommandSize);
			DrawStatistics.DrawIndirect();
		}

		public void Dispose()
		{
			instanceBuffer?.Dispose();
			instanceBuffer = null;
			commandBuffer?.Dispose();
			commandBuffer = null;
			Clear();
		}

		#endregion

		#region PrivateMethods

		private void AddDraw(int instance, int firstIndex, int indexCount, int baseVertex)
		{
			if (indexCount <= 0)
			{
				return;
			}

			if (commandCount == commands.Length)
			{
				Array.Resize(ref commands, commands.Length * 2);
			}

			commands[commandCount++] = new DrawCommand
			{
				IndexCount = (uint)indexCount,
				InstanceCount = 1,
				FirstIndex = (uint)firstIndex,
				VertexOffset = baseVertex,
				FirstInstance = (uint)instance
			};
		}

		#endregion

		#region NestedTypes

		[StructLayout(LayoutKind.Sequential)]
		private struct DrawCommand
		{
			public uint IndexCount;
			public uint InstanceCount;
			public uint FirstIndex;
			public int VertexOffset;
			public uint FirstInstance;
		}

		private struct Batch
		{
			public int Start;
			public int Count;
		}

		#endregion
	}
}
