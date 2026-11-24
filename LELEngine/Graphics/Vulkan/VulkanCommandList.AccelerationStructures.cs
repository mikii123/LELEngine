using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using LELEngine.Mathematics;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The commands of acceleration structures: builds (one command of several bottom levels, in batches as large as
	///     the device's build memory allows), top levels from instances copied into their buffers first, copies and
	///     compaction, and the top levels bound by number for the programs that trace them. Each command, like a
	///     dispatch, ends the open pass and starts after the barriers its uses need (<see cref="Sync" />: structures,
	///     their triangles and instances, the build memory; a trace waits for its top level's and bottom levels' builds).
	/// </summary>
	internal sealed unsafe partial class VulkanCommandList
	{
		public const int MaxAccelerationStructureBindings = 8;

		private readonly VulkanAccelerationStructure[] accelerationStructures = new VulkanAccelerationStructure[MaxAccelerationStructureBindings];
		private readonly HashSet<VulkanAccelerationStructure> placedBottomLevels = new HashSet<VulkanAccelerationStructure>();

		public override void SetAccelerationStructure(int binding, GpuAccelerationStructure topLevel)
		{
			RequireRayQueries();
			if (binding < 0 || binding >= MaxAccelerationStructureBindings)
			{
				throw new ArgumentOutOfRangeException(nameof(binding), $"Acceleration structure binding {binding} is outside 0..{MaxAccelerationStructureBindings - 1}.");
			}

			VulkanAccelerationStructure structure = (VulkanAccelerationStructure)topLevel;
			if (structure != null && structure.Level != AccelerationStructureLevel.Top)
			{
				throw new ArgumentException($"Acceleration structure '{structure.Name}' is a bottom level: shaders trace top levels.");
			}

			if (accelerationStructures[binding] != structure)
			{
				accelerationStructures[binding] = structure;
				device.BindingEpoch++;
			}
		}

		/// <summary>The top level behind an acceleration structure of a program: the built one set for its number, or an empty one.</summary>
		internal VkAccelerationStructureKHR ResolveAccelerationStructure(int number)
		{
			VulkanAccelerationStructure structure = number < MaxAccelerationStructureBindings ? accelerationStructures[number] : null;
			if (structure == null || structure.IsDisposed || !structure.IsBuilt)
			{
				structure = device.EmptyTopLevel;
			}

			structure.Use();
			// Traced by the program's commands: they wait for its build (and its bottom levels').
			Track(new VulkanProgram.TrackedResource { Structure = structure }, structure.Handle.VkHandle);
			return structure.Handle;
		}

		public override void BuildBottomLevels(ReadOnlySpan<GpuAccelerationStructure> bottomLevels, bool update = false)
		{
			RequireRayQueries();
			EndRenderPass();
			int count = bottomLevels.Length;
			if (count == 0)
			{
				return;
			}

			UseComputeQueue(true);

			VulkanAccelerationStructure[] structures = new VulkanAccelerationStructure[count];
			for (int i = 0; i < count; i++)
			{
				VulkanAccelerationStructure structure = (VulkanAccelerationStructure)bottomLevels[i];
				RequireBuildable(structure, AccelerationStructureLevel.Bottom, update);
				foreach (AccelerationStructureTriangles triangles in structure.Geometries)
				{
					GpuBuffer indices = triangles.IndexBuffer;
					if (triangles.VertexBuffer.IsDisposed || triangles.VertexBuffer.Size == 0 || (indices != null && (indices.IsDisposed || indices.Size == 0)))
					{
						throw new InvalidOperationException($"A buffer of bottom level '{structure.Name}' is disposed or holds nothing.");
					}
				}

				structures[i] = structure;
			}

			// Batches whose build memory fits the device's budget for one command (one structure always goes).
			int alignment = device.ScratchAlignment;
			int start = 0;
			while (start < count)
			{
				long scratch = 0;
				int end = start;
				while (end < count)
				{
					long need = Align(update ? structures[end].UpdateScratchSize : structures[end].BuildScratchSize, alignment);
					if (end > start && scratch + need > VulkanDevice.ScratchBatchBytes)
					{
						break;
					}

					scratch += need;
					end++;
				}

				RecordBottomLevels(structures, start, end, update, scratch);
				start = end;
			}
		}

		/// <summary>One build command of the bottom levels start .. end - 1, each in its own part of the build memory.</summary>
		private void RecordBottomLevels(VulkanAccelerationStructure[] structures, int start, int end, bool update, long scratch)
		{
			int count = end - start;
			int geometryCount = 0;
			for (int i = start; i < end; i++)
			{
				geometryCount += structures[i].Geometries.Length;
			}

			// The build memory first (when it is made anew, nothing used it), then the build's barriers: the structures
			// are written, their triangles read, the build memory read and written.
			ulong scratchAddress = device.Scratch(scratch);
			for (int i = start; i < end; i++)
			{
				VulkanAccelerationStructure structure = structures[i];
				RequireTarget(structure.Storage, Sync.AccelerationBuild, true);
				foreach (AccelerationStructureTriangles triangles in structure.Geometries)
				{
					RequireBuffer((VulkanBuffer)triangles.VertexBuffer, Sync.AccelerationBuild, false);
					if (triangles.IndexBuffer != null)
					{
						RequireBuffer((VulkanBuffer)triangles.IndexBuffer, Sync.AccelerationBuild, false);
					}
				}
			}

			RequireTarget(device.ScratchTarget, Sync.AccelerationBuild, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			int alignment = device.ScratchAlignment;
			VkAccelerationStructureBuildGeometryInfoKHR[] infos = new VkAccelerationStructureBuildGeometryInfoKHR[count];
			VkAccelerationStructureGeometryKHR[] geometries = new VkAccelerationStructureGeometryKHR[geometryCount];
			VkAccelerationStructureBuildRangeInfoKHR[] ranges = new VkAccelerationStructureBuildRangeInfoKHR[geometryCount];
			nint[] rangeLists = new nint[count];
			fixed (VkAccelerationStructureBuildGeometryInfoKHR* info = infos)
			fixed (VkAccelerationStructureGeometryKHR* geometry = geometries)
			fixed (VkAccelerationStructureBuildRangeInfoKHR* range = ranges)
			fixed (nint* rangeList = rangeLists)
			{
				int first = 0;
				ulong offset = 0;
				for (int i = 0; i < count; i++)
				{
					VulkanAccelerationStructure structure = structures[start + i];
					structure.Describe(geometry + first, range + first, true);
					info[i] = new VkAccelerationStructureBuildGeometryInfoKHR
					{
						Type = VkAccelerationStructureTypeKHR.BottomLevelKHR,
						Flags = VulkanAccelerationStructure.BuildFlags(structure.Flags),
						Mode = update ? VkBuildAccelerationStructureModeKHR.UpdateKHR : VkBuildAccelerationStructureModeKHR.BuildKHR,
						SrcAccelerationStructure = update ? structure.Handle : default,
						DstAccelerationStructure = structure.Handle,
						GeometryCount = structure.Geometries.Length,
						Geometries = geometry + first,
						ScratchData = new VkDeviceOrHostAddressKHR { DeviceAddress = new VkDeviceAddress(scratchAddress + offset) }
					};
					rangeList[i] = (nint)(range + first);
					first += structure.Geometries.Length;
					offset += (ulong)Align(update ? structure.UpdateScratchSize : structure.BuildScratchSize, alignment);
				}

				vk->vkCmdBuildAccelerationStructuresKHR.Invoke(commands, count, info, (VkAccelerationStructureBuildRangeInfoKHR**)rangeList);
			}

			for (int i = start; i < end; i++)
			{
				structures[i].MarkBuilt(0, null);
			}

			if (!update)
			{
				WriteCompactedSizes(commands, structures, start, end);
			}
		}

		public override void BuildTopLevel(GpuAccelerationStructure topLevel, ReadOnlySpan<AccelerationStructureInstance> instances, bool update = false)
		{
			RequireRayQueries();
			EndRenderPass();
			UseComputeQueue(true);
			VulkanAccelerationStructure structure = (VulkanAccelerationStructure)topLevel;
			RequireBuildable(structure, AccelerationStructureLevel.Top, update);
			int count = instances.Length;
			if (count > structure.MaxInstances)
			{
				throw new ArgumentException($"Top level '{structure.Name}' takes at most {structure.MaxInstances} instances, {count} were given.");
			}

			if (update && count != structure.BuiltInstances)
			{
				throw new ArgumentException($"An update of top level '{structure.Name}' gives the {structure.BuiltInstances} instances of its build, not {count}.");
			}

			VulkanAccelerationStructure[] placed = null;
			if (count > 0)
			{
				// The instances as the build reads them, through the frame's memory into the structure's buffer.
				byte* memory = device.AllocateTransient((long)count * VulkanAccelerationStructure.InstanceSize, 16, out VkBuffer source, out long sourceOffset);
				placedBottomLevels.Clear();
				for (int i = 0; i < count; i++)
				{
					ref readonly AccelerationStructureInstance instance = ref instances[i];
					VulkanAccelerationStructure bottom = (VulkanAccelerationStructure)instance.BottomLevel;
					if (bottom == null || bottom.IsDisposed || !bottom.IsBuilt || bottom.Level != AccelerationStructureLevel.Bottom)
					{
						throw new InvalidOperationException($"Instance {i} of top level '{structure.Name}' names no built bottom level ({(bottom == null ? "none" : "'" + bottom.Name + "'")}).");
					}

					WriteInstance(memory + (long)i * VulkanAccelerationStructure.InstanceSize, instance, bottom.Address);
					if (placedBottomLevels.Add(bottom))
					{
						bottom.Use();
					}
				}

				placed = new VulkanAccelerationStructure[placedBottomLevels.Count];
				placedBottomLevels.CopyTo(placed);
				placedBottomLevels.Clear();
				RequireTarget(structure.Instances, Sync.Transfer, true);
				ApplyRequirements(false);
				VkBufferCopy region = new VkBufferCopy
				{
					SrcOffset = (Brutal.ByteSize64)(ulong)sourceOffset,
					DstOffset = (Brutal.ByteSize64)(ulong)structure.InstanceOffset,
					Size = (Brutal.ByteSize64)(ulong)((long)count * VulkanAccelerationStructure.InstanceSize)
				};
				vk->vkCmdCopyBuffer.Invoke(device.MainCommands, source, structure.InstanceBuffer, 1, &region);
			}

			// The build reads the instances, the bottom levels they place and the build memory, and writes the structure.
			ulong scratch = device.Scratch(update ? structure.UpdateScratchSize : structure.BuildScratchSize);
			if (count > 0)
			{
				RequireTarget(structure.Instances, Sync.AccelerationBuild, false);
				foreach (VulkanAccelerationStructure bottom in placed)
				{
					RequireTarget(bottom.Storage, Sync.AccelerationBuild, false, false);
				}
			}

			RequireTarget(structure.Storage, Sync.AccelerationBuild, true);
			RequireTarget(device.ScratchTarget, Sync.AccelerationBuild, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			RecordTopLevel(commands, structure, count, update, scratch);
			structure.MarkBuilt(count, placed);
			if (!update)
			{
				WriteCompactedSizes(commands, new[] { structure }, 0, 1);
			}
		}

		/// <summary>
		///     Builds an empty top level ahead of the stream (the device's stand-in for bindings given none). The build's
		///     own barriers come with it, and as a command ahead of the stream outside its order (it may share the build
		///     memory with builds the stream holds already), the whole stream waits for it.
		/// </summary>
		internal void BuildEmptyTopLevel(VulkanAccelerationStructure structure)
		{
			OnGraphicsQueue(() => RecordEmptyTopLevel(structure));
		}

		private void RecordEmptyTopLevel(VulkanAccelerationStructure structure)
		{
			ulong scratch = device.Scratch(structure.BuildScratchSize);
			SyncBatch setup = device.SetupBatch;
			ApplyTarget(structure.Storage, setup, Sync.AccelerationBuild, true);
			ApplyTarget(device.ScratchTarget, setup, Sync.AccelerationBuild, true);
			VkCommandBuffer commands = device.BeginSetupCommand();
			setup.Record(device, commands);
			RecordTopLevel(commands, structure, 0, false, scratch);
			structure.MarkBuilt(0, null);
		}

		/// <summary>The build command of a top level from the first <paramref name="count" /> instances of its buffer, in the build memory at <paramref name="scratch" />.</summary>
		private void RecordTopLevel(VkCommandBuffer commands, VulkanAccelerationStructure structure, int count, bool update, ulong scratch)
		{
			VkAccelerationStructureGeometryKHR geometry = structure.InstanceGeometry(structure.InstanceAddress);
			VkAccelerationStructureBuildRangeInfoKHR range = new VkAccelerationStructureBuildRangeInfoKHR { PrimitiveCount = count };
			VkAccelerationStructureBuildRangeInfoKHR* rangeList = &range;
			VkAccelerationStructureBuildGeometryInfoKHR info = new VkAccelerationStructureBuildGeometryInfoKHR
			{
				Type = VkAccelerationStructureTypeKHR.TopLevelKHR,
				Flags = VulkanAccelerationStructure.BuildFlags(structure.Flags),
				Mode = update ? VkBuildAccelerationStructureModeKHR.UpdateKHR : VkBuildAccelerationStructureModeKHR.BuildKHR,
				SrcAccelerationStructure = update ? structure.Handle : default,
				DstAccelerationStructure = structure.Handle,
				GeometryCount = 1,
				Geometries = &geometry,
				ScratchData = new VkDeviceOrHostAddressKHR { DeviceAddress = new VkDeviceAddress(scratch) }
			};
			vk->vkCmdBuildAccelerationStructuresKHR.Invoke(commands, 1, &info, &rangeList);
		}

		public override void CopyAccelerationStructure(GpuAccelerationStructure source, GpuAccelerationStructure destination)
		{
			RequireRayQueries();
			EndRenderPass();
			UseComputeQueue(true);
			VulkanAccelerationStructure from = (VulkanAccelerationStructure)source;
			VulkanAccelerationStructure to = (VulkanAccelerationStructure)destination;
			if (from == null || to == null || from.IsDisposed || to.IsDisposed || from == to)
			{
				throw new ArgumentException("A copy of an acceleration structure goes between two structures alive.");
			}

			if (!from.IsBuilt || from.Level != to.Level)
			{
				throw new InvalidOperationException($"Acceleration structure '{from.Name}' is copied once built, into a structure of its level ('{to.Name}').");
			}

			if (!to.IsCompactCopy && to.Size < from.Size)
			{
				throw new InvalidOperationException($"Acceleration structure '{to.Name}' ({to.Size} bytes) cannot take a copy of '{from.Name}' ({from.Size} bytes).");
			}

			RequireTarget(from.Storage, Sync.AccelerationBuild, false);
			RequireTarget(to.Storage, Sync.AccelerationBuild, true);
			ApplyRequirements(false);
			VkCommandBuffer commands = device.MainCommands;
			VkCopyAccelerationStructureInfoKHR info = new VkCopyAccelerationStructureInfoKHR
			{
				Src = from.Handle,
				Dst = to.Handle,
				Mode = to.IsCompactCopy ? VkCopyAccelerationStructureModeKHR.CompactKHR : VkCopyAccelerationStructureModeKHR.CloneKHR
			};
			vk->vkCmdCopyAccelerationStructureKHR.Invoke(commands, &info);
			from.Use();
			to.MarkBuilt(from.BuiltInstances, from.BottomLevels);
		}

		/// <summary>
		///     After full builds (their barrier first: the queries read the finished structures), the compacted size of
		///     each structure made with AllowCompaction goes into a query of its own.
		/// </summary>
		private void WriteCompactedSizes(VkCommandBuffer commands, VulkanAccelerationStructure[] structures, int start, int end)
		{
			bool any = false;
			for (int i = start; i < end; i++)
			{
				if ((structures[i].Flags & AccelerationStructureFlags.AllowCompaction) != 0)
				{
					RequireTarget(structures[i].Storage, Sync.AccelerationBuild, false);
					any = true;
				}
			}

			if (!any)
			{
				return;
			}

			ApplyRequirements(false);
			for (int i = start; i < end; i++)
			{
				VulkanAccelerationStructure structure = structures[i];
				if ((structure.Flags & AccelerationStructureFlags.AllowCompaction) == 0)
				{
					continue;
				}

				int query = structure.TakeCompactionQuery();
				VkQueryPool pool = device.CompactionPool(query, out int index);
				VkAccelerationStructureKHR handle = structure.Handle;
				vk->vkCmdWriteAccelerationStructuresPropertiesKHR.Invoke(commands, 1, &handle, VkQueryType.AccelerationStructureCompactedSizeKHR, pool, index);
			}
		}

		/// <summary>A device made without ray queries takes no acceleration structure commands (as OpenGL).</summary>
		private void RequireRayQueries()
		{
			if (!device.HasRayQuery)
			{
				throw new NotSupportedException("This Vulkan device has no acceleration structures (DeviceCapabilities.RayQuery is false).");
			}
		}

		/// <summary>Throws when a structure cannot be built (or updated) by a build of the given level.</summary>
		private static void RequireBuildable(VulkanAccelerationStructure structure, AccelerationStructureLevel level, bool update)
		{
			if (structure == null || structure.IsDisposed)
			{
				throw new ArgumentException("An acceleration structure build without a structure, or of a disposed one.");
			}

			if (structure.Level != level || structure.IsCompactCopy)
			{
				throw new InvalidOperationException($"Acceleration structure '{structure.Name}' is {(structure.IsCompactCopy ? "a compacted copy (copies are not built)" : "a " + structure.Level.ToString().ToLowerInvariant() + " level")}: this command builds {level.ToString().ToLowerInvariant()} levels.");
			}

			if (update && ((structure.Flags & AccelerationStructureFlags.AllowUpdate) == 0 || !structure.IsBuilt))
			{
				throw new InvalidOperationException($"Acceleration structure '{structure.Name}' is updated only when made with AllowUpdate and built before.");
			}
		}

		/// <summary>
		///     An instance as Vulkan reads it (VkAccelerationStructureInstanceKHR): the transform's three rows (the engine's
		///     matrix transposed: it transforms row vectors), the custom index and mask, the flags, the bottom level's address.
		///     Vulkan's facing is the engine's without a flag (the device tests: a triangle a ray sees counter-clockwise,
		///     cross(v1 - v0, v2 - v0) pointing back at it, is hit on its front).
		/// </summary>
		private static void WriteInstance(byte* target, in AccelerationStructureInstance instance, ulong bottomLevel)
		{
			float* transform = (float*)target;
			Matrix4 m = instance.Transform;
			transform[0] = m.Row0.X;
			transform[1] = m.Row1.X;
			transform[2] = m.Row2.X;
			transform[3] = m.Row3.X;
			transform[4] = m.Row0.Y;
			transform[5] = m.Row1.Y;
			transform[6] = m.Row2.Y;
			transform[7] = m.Row3.Y;
			transform[8] = m.Row0.Z;
			transform[9] = m.Row1.Z;
			transform[10] = m.Row2.Z;
			transform[11] = m.Row3.Z;
			int flags = 0;
			if ((instance.Flags & AccelerationStructureInstanceFlags.TwoSided) != 0)
			{
				flags |= 1;
			}

			if ((instance.Flags & AccelerationStructureInstanceFlags.FlipFacing) != 0)
			{
				flags |= 2;
			}

			if ((instance.Flags & AccelerationStructureInstanceFlags.ForceOpaque) != 0)
			{
				flags |= 4;
			}

			if ((instance.Flags & AccelerationStructureInstanceFlags.ForceNotOpaque) != 0)
			{
				flags |= 8;
			}

			uint* words = (uint*)target;
			words[12] = ((uint)instance.CustomIndex & 0xFFFFFF) | (uint)instance.Mask << 24;
			words[13] = (uint)flags << 24;
			*(ulong*)(target + 56) = bottomLevel;
		}

		private static long Align(long value, int alignment)
		{
			return (value + alignment - 1) / alignment * alignment;
		}
	}
}
