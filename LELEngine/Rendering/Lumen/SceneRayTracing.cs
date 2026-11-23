using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Lumen
{
	/// <summary>
	///     A <see cref="LumenScene" />'s objects as a top level acceleration structure, which Lumen's rays trace with ray
	///     queries (<see cref="Lighting.HardwareRayTracingActive" />, Engine/SceneRayQuery.glsl): each object is an instance
	///     of its mesh's bottom level (<see cref="MeshAccelerationStructures" />) placed as the GI sees it (scaled-local
	///     space: the scale the object's field and cards have, then its rotation and position), its custom index the
	///     object's row of the object table. Built again when the objects or their bottom levels changed, refitted when
	///     only placements did (built again after <see cref="RefitsBeforeRebuild" /> refits: a refit keeps the tree of the
	///     last build, which fits worse the further things moved), left as it is otherwise. A table of each object's range
	///     in the geometry pool lets a hit read its triangle.
	/// </summary>
	public sealed class SceneRayTracing
	{
		#region PublicFields

		/// <summary>The acceleration structure binding the shaders trace (Engine/SceneRayQuery.glsl).</summary>
		public const int SceneBinding = 0;

		/// <summary>The storage bindings of the geometry pool's vertices and indices, and of the objects' ranges in them.</summary>
		public const int VertexBinding = 19;

		public const int IndexBinding = 20;
		public const int RangeBinding = 21;

		/// <summary>Refits of the top level in a row before it is built again.</summary>
		public const int RefitsBeforeRebuild = 60;

		/// <summary>The top level of this frame (null before the first update, or without objects).</summary>
		public GpuAccelerationStructure TopLevel => instanceCount > 0 ? topLevel : null;

		#endregion

		#region PrivateFields

		private GpuAccelerationStructure topLevel;
		private AccelerationStructureInstance[] instances = new AccelerationStructureInstance[16];
		private int instanceCount;
		private int builtCount = -1;
		private int refits;

		// Per object: first index and base vertex of its mesh in the pool (uvec2), never empty.
		private uint[] ranges = new uint[2];
		private GpuBuffer rangeBuffer;
		private int rangeBufferBytes;

		#endregion

		#region PublicMethods

		/// <summary>The top level of the objects as they are this frame: built, refitted or kept (with the bottom levels it needs built first).</summary>
		public void Update(IReadOnlyList<LumenScene.SceneObject> objects)
		{
			MeshAccelerationStructures.Compact();
			int objectCount = objects.Count;
			if (instances.Length < objectCount)
			{
				Array.Resize(ref instances, Math.Max(objectCount, instances.Length * 2));
			}

			if (ranges.Length < Math.Max(1, objectCount) * 2)
			{
				Array.Resize(ref ranges, Math.Max(objectCount * 2, ranges.Length * 2));
			}

			// What changed since the last build: the instances (their count, what they hold), or only their placements.
			bool rebuild = false;
			bool moved = false;
			int count = 0;
			for (int i = 0; i < objectCount; i++)
			{
				LumenScene.SceneObject o = objects[i];
				Mesh mesh = o.Renderer.Mesh;
				GpuAccelerationStructure bottom = MeshAccelerationStructures.Get(mesh);
				if (bottom == null)
				{
					continue;
				}

				GeometryPool.GetRange(mesh, out int baseVertex, out _, out int firstIndex, out _);
				ranges[i * 2] = (uint)firstIndex;
				ranges[i * 2 + 1] = (uint)baseVertex;
				Transform t = o.Renderer.transform;
				Matrix4 placement = Matrix4.CreateScale(o.Scale) * Matrix4.CreateFromQuaternion(t.rotation) * Matrix4.CreateTranslation(t.position);
				ref AccelerationStructureInstance instance = ref instances[count];
				if (count >= builtCount || instance.BottomLevel != bottom || instance.CustomIndex != o.Index)
				{
					rebuild = true;
				}
				else if (instance.Transform != placement)
				{
					moved = true;
				}

				instance = new AccelerationStructureInstance(bottom, placement, o.Index);
				count++;
			}

			instanceCount = count;
			rebuild |= count != builtCount;
			MeshAccelerationStructures.BuildPending();
			if (count == 0)
			{
				builtCount = 0;
				return;
			}

			if (topLevel == null || topLevel.MaxInstances < count)
			{
				topLevel?.Dispose();
				int capacity = 64;
				while (capacity < count)
				{
					capacity *= 2;
				}

				topLevel = Gfx.Device.CreateAccelerationStructure(AccelerationStructureDescriptor.TopLevel(capacity, AccelerationStructureFlags.PreferFastTrace | AccelerationStructureFlags.AllowUpdate,
					"Lumen scene top level"));
				rebuild = true;
			}

			if (rebuild || (moved && refits >= RefitsBeforeRebuild))
			{
				Gfx.Commands.BuildTopLevel(topLevel, new ReadOnlySpan<AccelerationStructureInstance>(instances, 0, count));
				builtCount = count;
				refits = 0;
				UploadRanges(objectCount);
			}
			else if (moved)
			{
				Gfx.Commands.BuildTopLevel(topLevel, new ReadOnlySpan<AccelerationStructureInstance>(instances, 0, count), true);
				refits++;
			}
		}

		/// <summary>Binds the top level, the geometry pool's buffers and the objects' ranges for Engine/SceneRayQuery.glsl.</summary>
		public void Bind()
		{
			CommandList commands = Gfx.Commands;
			commands.SetAccelerationStructure(SceneBinding, TopLevel);
			commands.SetStorageBuffer(VertexBinding, GeometryPool.VertexBuffer);
			commands.SetStorageBuffer(IndexBinding, GeometryPool.IndexBuffer);
			commands.SetStorageBuffer(RangeBinding, rangeBuffer);
		}

		public void Delete()
		{
			topLevel?.Dispose();
			topLevel = null;
			rangeBuffer?.Dispose();
			rangeBuffer = null;
			instanceCount = 0;
			builtCount = -1;
		}

		#endregion

		#region PrivateMethods

		private void UploadRanges(int objectCount)
		{
			rangeBuffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Lumen scene mesh ranges"));
			int bytes = Math.Max(1, objectCount) * 2 * sizeof(uint);
			if (bytes > rangeBufferBytes)
			{
				rangeBuffer.SetData(ranges);
				rangeBufferBytes = ranges.Length * sizeof(uint);
			}
			else
			{
				rangeBuffer.SetSubData(0, new ReadOnlySpan<uint>(ranges, 0, Math.Max(1, objectCount) * 2));
			}
		}

		#endregion
	}
}
