using System;
using System.Collections.Generic;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The device's part for ray queries. Made with acceleration structures, ray queries and buffer device addresses
	///     where the GPU has all of them (<see cref="HasRayQuery" />; without them otherwise), it makes the structures and
	///     keeps what their builds need: one scratch buffer the builds work in (grown as they ask), the queries that tell a
	///     built structure's compacted size, and an empty top level for bindings given none.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		private const int CompactionQueriesPerPool = 256;

		/// <summary>Build memory one command takes at most (unless one structure alone needs more): larger sets of builds go in several commands.</summary>
		internal const long ScratchBatchBytes = 64L << 20;

		/// <summary>The device has acceleration structures and ray queries (<see cref="DeviceCapabilities.RayQuery" />).</summary>
		internal readonly bool HasRayQuery;

		private int scratchAlignment;
		private VkBuffer scratchBuffer;
		private VmaAllocation scratchAllocation;
		private long scratchSize;
		private ulong scratchAddress;

		/// <summary>What the commands recorded so far do to the build memory (every build reads and writes it).</summary>
		internal readonly SyncTarget ScratchTarget = new SyncTarget();
		private readonly List<VkQueryPool> compactionPools = new List<VkQueryPool>();
		private readonly Stack<int> freeCompactionQueries = new Stack<int>();
		private readonly List<(int Query, long Serial)> releasedCompactionQueries = new List<(int Query, long Serial)>();
		private VulkanAccelerationStructure emptyTopLevel;

		/// <summary>The features of acceleration structures and ray queries a device is made with, linked into the chain of those asked for.</summary>
		private struct RayQueryFeatures
		{
			public VkPhysicalDeviceAccelerationStructureFeaturesKHR Structures;
			public VkPhysicalDeviceRayQueryFeaturesKHR Queries;
		}

		/// <summary>
		///     Asks for what ray queries need when the GPU has all of it (the extensions of acceleration structures, ray
		///     queries and deferred host operations; the features acceleration structures, ray queries and buffer device
		///     addresses): the extensions go into the set, the features into the chain of <paramref name="want" />
		///     (<paramref name="features" />, the caller's, holds them until the device is made). False, asking for nothing,
		///     when one is missing or LEL_VULKAN_DISABLE names "rayqueries" (a device without them, to test the fallbacks).
		/// </summary>
		private bool AskForRayQueries(PhysicalDevice candidate, HashSet<string> extensions, RayQueryFeatures* features, VkPhysicalDeviceFeatures2* want, VkPhysicalDeviceVulkan12Features* want12)
		{
			string disabled = Environment.GetEnvironmentVariable("LEL_VULKAN_DISABLE") ?? "";
			if (disabled.IndexOf("rayqueries", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Console.WriteLine("[Vulkan] Disabled by LEL_VULKAN_DISABLE: rayqueries");
				return false;
			}

			if (!candidate.SupportsExtension("VK_KHR_acceleration_structure") || !candidate.SupportsExtension("VK_KHR_ray_query") || !candidate.SupportsExtension("VK_KHR_deferred_host_operations"))
			{
				return false;
			}

			VkPhysicalDeviceRayQueryFeaturesKHR queriesHave = new VkPhysicalDeviceRayQueryFeaturesKHR();
			VkPhysicalDeviceAccelerationStructureFeaturesKHR structuresHave = new VkPhysicalDeviceAccelerationStructureFeaturesKHR { Next = &queriesHave };
			VkPhysicalDeviceVulkan12Features have12 = new VkPhysicalDeviceVulkan12Features { Next = &structuresHave };
			VkPhysicalDeviceFeatures2 have = new VkPhysicalDeviceFeatures2 { Next = &have12 };
			InstanceVk->vkGetPhysicalDeviceFeatures2.Invoke(candidate.Handle, &have);
			if (!structuresHave.AccelerationStructure || !queriesHave.RayQuery || !have12.BufferDeviceAddress)
			{
				return false;
			}

			features->Structures = new VkPhysicalDeviceAccelerationStructureFeaturesKHR { AccelerationStructure = true };
			features->Queries = new VkPhysicalDeviceRayQueryFeaturesKHR { RayQuery = true, Next = want->Next };
			features->Structures.Next = &features->Queries;
			want->Next = &features->Structures;
			want12->BufferDeviceAddress = true;
			extensions.Add("VK_KHR_acceleration_structure");
			extensions.Add("VK_KHR_ray_query");
			extensions.Add("VK_KHR_deferred_host_operations");
			return true;
		}

		public override GpuAccelerationStructure CreateAccelerationStructure(in AccelerationStructureDescriptor descriptor)
		{
			if (!HasRayQuery)
			{
				throw new NotSupportedException("This Vulkan device makes no acceleration structures (DeviceCapabilities.RayQuery is false).");
			}

			return new VulkanAccelerationStructure(this, descriptor);
		}

		/// <summary>The GPU's address of a buffer made with VK_BUFFER_USAGE_SHADER_DEVICE_ADDRESS_BIT.</summary>
		internal ulong BufferAddress(VkBuffer buffer)
		{
			VkBufferDeviceAddressInfo info = new VkBufferDeviceAddressInfo { Buffer = buffer };
			return Vk->vkGetBufferDeviceAddress.Invoke(Handle, &info).Value;
		}

		/// <summary>What the build memory of every build starts at a multiple of (minAccelerationStructureScratchOffsetAlignment).</summary>
		internal int ScratchAlignment
		{
			get
			{
				if (scratchAlignment == 0)
				{
					VkPhysicalDeviceAccelerationStructurePropertiesKHR structures = new VkPhysicalDeviceAccelerationStructurePropertiesKHR();
					VkPhysicalDeviceProperties2 properties = new VkPhysicalDeviceProperties2 { Next = &structures };
					InstanceVk->vkGetPhysicalDeviceProperties2.Invoke(PhysicalHandle, &properties);
					scratchAlignment = Math.Max(16, structures.MinAccelerationStructureScratchOffsetAlignment);
				}

				return scratchAlignment;
			}
		}

		/// <summary>
		///     The address of build memory of at least <paramref name="size" /> bytes, aligned for builds: the device's one
		///     scratch buffer, made larger when it is too small (the smaller one goes with the commands that used it). Builds
		///     of one command take parts of it side by side; commands that follow each other are ordered by the barrier
		///     their uses of it need (<see cref="ScratchTarget" />). Called before the barriers of the build are noted.
		/// </summary>
		internal ulong Scratch(long size)
		{
			if (size > scratchSize)
			{
				if (!scratchBuffer.IsNull())
				{
					Retire(RetiredKind.Buffer, scratchBuffer.VkHandle, scratchAllocation.VkHandle, RecordingSerial);
				}

				long capacity = Math.Max(size, Math.Max(1L << 20, scratchSize * 2));
				// Room to move the start to the alignment builds ask for, whatever the buffer's own is.
				CreateDeviceBuffer(capacity + ScratchAlignment, VkBufferUsageFlags.StorageBufferBit | VkBufferUsageFlags.ShaderDeviceAddressBit, "acceleration structure builds", out scratchBuffer,
					out scratchAllocation);
				scratchSize = capacity;
				ulong alignment = (ulong)ScratchAlignment;
				scratchAddress = (BufferAddress(scratchBuffer) + alignment - 1) / alignment * alignment;
				ScratchTarget.Forget();
			}

			return scratchAddress;
		}

		/// <summary>A buffer in the GPU's own memory, of the device's own use (build memory, a structure's storage, a top level's instances).</summary>
		internal void CreateDeviceBuffer(long size, VkBufferUsageFlags usage, string what, out VkBuffer buffer, out VmaAllocation allocation)
		{
			VkBufferCreateInfo info = new VkBufferCreateInfo { Size = (Brutal.ByteSize64)(ulong)size, Usage = usage };
			int* families = stackalloc int[2];
			ShareBuffer(ref info, families);
			VmaAllocationCreateInfo allocationInfo = new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.Auto };
			VkBuffer made;
			VmaAllocation handle;
			Check(Vma.PInvoke.vmaCreateBuffer(Allocator, &info, &allocationInfo, &made, &handle, default), "vmaCreateBuffer (" + what + ", " + size + " bytes)");
			buffer = made;
			allocation = handle;
		}

		/// <summary>A query of a structure's compacted size, reset, that no unfinished command writes.</summary>
		internal int TakeCompactionQuery()
		{
			for (int i = releasedCompactionQueries.Count - 1; i >= 0; i--)
			{
				if (releasedCompactionQueries[i].Serial <= CompletedSerial)
				{
					freeCompactionQueries.Push(releasedCompactionQueries[i].Query);
					releasedCompactionQueries.RemoveAt(i);
				}
			}

			if (freeCompactionQueries.Count == 0)
			{
				VkQueryPoolCreateInfo info = new VkQueryPoolCreateInfo { QueryType = VkQueryType.AccelerationStructureCompactedSizeKHR, QueryCount = CompactionQueriesPerPool };
				VkQueryPool pool;
				Check(Vk->vkCreateQueryPool.Invoke(Handle, &info, null, &pool), "vkCreateQueryPool (compacted sizes)");
				int first = compactionPools.Count * CompactionQueriesPerPool;
				compactionPools.Add(pool);
				for (int i = CompactionQueriesPerPool - 1; i >= 0; i--)
				{
					freeCompactionQueries.Push(first + i);
				}
			}

			int query = freeCompactionQueries.Pop();
			Vk->vkResetQueryPool.Invoke(Handle, compactionPools[query / CompactionQueriesPerPool], query % CompactionQueriesPerPool, 1);
			return query;
		}

		internal VkQueryPool CompactionPool(int query, out int index)
		{
			index = query % CompactionQueriesPerPool;
			return compactionPools[query / CompactionQueriesPerPool];
		}

		/// <summary>The query is free again once the given submission has finished.</summary>
		internal void ReleaseCompactionQuery(int query, long serial)
		{
			releasedCompactionQueries.Add((query, serial));
		}

		/// <summary>The compacted size the query holds (its command has run); 0 when it holds none.</summary>
		internal long ReadCompactionQuery(int query)
		{
			ulong bytes = 0;
			VkResult result = Vk->vkGetQueryPoolResults.Invoke(Handle, compactionPools[query / CompactionQueriesPerPool], query % CompactionQueriesPerPool, 1, sizeof(ulong), &bytes,
				(Brutal.ByteSize64)(ulong)sizeof(ulong), VkQueryResultFlags._64Bit);
			return result == VkResult.Success ? (long)bytes : 0;
		}

		/// <summary>An empty top level, built ahead of the stream on first use: what an acceleration structure binding given nothing traces.</summary>
		internal VulkanAccelerationStructure EmptyTopLevel
		{
			get
			{
				if (emptyTopLevel == null)
				{
					emptyTopLevel = new VulkanAccelerationStructure(this, AccelerationStructureDescriptor.TopLevel(1, AccelerationStructureFlags.None, "Unset top level"));
					CommandList.BuildEmptyTopLevel(emptyTopLevel);
				}

				return emptyTopLevel;
			}
		}

		/// <summary>Gives up what the device keeps for builds (the device is being given up; the GPU is idle).</summary>
		private void ReleaseRayQueries()
		{
			emptyTopLevel?.Dispose();
			emptyTopLevel = null;
			if (!scratchBuffer.IsNull())
			{
				Retire(RetiredKind.Buffer, scratchBuffer.VkHandle, scratchAllocation.VkHandle, 0);
				scratchBuffer = default;
				scratchSize = 0;
			}

			foreach (VkQueryPool pool in compactionPools)
			{
				Vk->vkDestroyQueryPool.Invoke(Handle, pool, null);
			}

			compactionPools.Clear();
			freeCompactionQueries.Clear();
			releasedCompactionQueries.Clear();
		}
	}

	/// <summary>
	///     An acceleration structure on Vulkan: the structure in a buffer of its own, of the size its triangles (bottom
	///     level) or its most instances (top level) need; a top level also has a buffer its builds read the instances
	///     from (written by a copy ordered with the commands). A structure made with AllowCompaction writes its compacted
	///     size into a query after every full build.
	/// </summary>
	internal sealed unsafe class VulkanAccelerationStructure : GpuAccelerationStructure
	{
		/// <summary>Bytes of an instance as builds read it (VkAccelerationStructureInstanceKHR).</summary>
		public const int InstanceSize = 64;

		private static readonly VulkanAccelerationStructure[] noBottomLevels = Array.Empty<VulkanAccelerationStructure>();

		private readonly VulkanDevice device;
		private readonly VkBuffer buffer;
		private readonly VmaAllocation allocation;
		private readonly VkBuffer instanceBuffer;
		private readonly VmaAllocation instanceAllocation;
		private int compactionQuery = -1;
		private long compactionSerial;
		private long compactedSize;
		private long bottomLevelsMarked;

		public readonly VkAccelerationStructureKHR Handle;

		/// <summary>The structure's address: what the instances of a top level name a bottom level by.</summary>
		public readonly ulong Address;

		/// <summary>A bottom level's triangles: a copy of what it was made with, which every build reads.</summary>
		public readonly AccelerationStructureTriangles[] Geometries;

		public readonly long BuildScratchSize;
		public readonly long UpdateScratchSize;

		/// <summary>A top level: the address its builds read the instances from (aligned to 16 bytes, as builds ask).</summary>
		public readonly ulong InstanceAddress;

		/// <summary>A top level: where <see cref="InstanceAddress" /> is in its instance buffer.</summary>
		public readonly long InstanceOffset;

		/// <summary>Made to take a compacted copy (<see cref="AccelerationStructureDescriptor.CompactedFrom" />): no build writes it.</summary>
		public readonly bool IsCompactCopy;

		/// <summary>A top level: the instances of its last build (an update gives as many).</summary>
		public int BuiltInstances;

		/// <summary>A top level: the bottom levels its last build placed (each once), which a trace of it reads.</summary>
		public VulkanAccelerationStructure[] BottomLevels = noBottomLevels;

		/// <summary>The last submission whose commands used the structure (built it, copied it, traced it).</summary>
		public long LastUse;

		/// <summary>What the commands recorded so far do to the structure (builds and copies write it; builds, copies, queries and traces read it).</summary>
		internal readonly SyncTarget Storage = new SyncTarget(true);

		/// <summary>A top level: what they do to its instances buffer (a copy writes it, builds read it).</summary>
		internal readonly SyncTarget Instances = new SyncTarget();

		public VulkanAccelerationStructure(VulkanDevice device, in AccelerationStructureDescriptor descriptor)
			: base(descriptor.CompactedFrom != null ? descriptor.CompactedFrom.Level : descriptor.Level, descriptor.CompactedFrom != null ? descriptor.CompactedFrom.Flags : descriptor.Flags,
				descriptor.Name)
		{
			this.device = device;
			bool top = Level == AccelerationStructureLevel.Top;
			long size;
			if (descriptor.CompactedFrom != null)
			{
				VulkanAccelerationStructure source = (VulkanAccelerationStructure)descriptor.CompactedFrom;
				size = source.CompactedSize;
				if (source.IsDisposed || size <= 0)
				{
					throw new InvalidOperationException($"The compacted size of acceleration structure '{source.Name}' is not known (CompactedSize 0): made with AllowCompaction, built, and the GPU done with the build?");
				}

				IsCompactCopy = true;
				MaxInstances = source.MaxInstances;
				Geometries = source.Geometries;
			}
			else
			{
				VkAccelerationStructureBuildSizesInfoKHR sizes = new VkAccelerationStructureBuildSizesInfoKHR();
				if (top)
				{
					if (descriptor.MaxInstances < 0)
					{
						throw new ArgumentException("A top level's MaxInstances cannot be negative.");
					}

					MaxInstances = descriptor.MaxInstances;
					VkAccelerationStructureGeometryKHR geometry = InstanceGeometry(0);
					VkAccelerationStructureBuildGeometryInfoKHR build = new VkAccelerationStructureBuildGeometryInfoKHR
					{
						Type = VkAccelerationStructureTypeKHR.TopLevelKHR,
						Flags = BuildFlags(Flags),
						Mode = VkBuildAccelerationStructureModeKHR.BuildKHR,
						GeometryCount = 1,
						Geometries = &geometry
					};
					int count = Math.Max(1, MaxInstances);
					device.Vk->vkGetAccelerationStructureBuildSizesKHR.Invoke(device.Handle, VkAccelerationStructureBuildTypeKHR.DeviceKHR, &build, &count, &sizes);
				}
				else
				{
					if (descriptor.Geometries.Length == 0)
					{
						throw new ArgumentException($"Bottom level '{Name}' is made without triangles.");
					}

					Geometries = descriptor.Geometries.ToArray();
					foreach (AccelerationStructureTriangles triangles in Geometries)
					{
						Validate(triangles);
					}

					VkAccelerationStructureGeometryKHR[] geometries = new VkAccelerationStructureGeometryKHR[Geometries.Length];
					VkAccelerationStructureBuildRangeInfoKHR[] ranges = new VkAccelerationStructureBuildRangeInfoKHR[Geometries.Length];
					int[] counts = new int[Geometries.Length];
					fixed (VkAccelerationStructureGeometryKHR* described = geometries)
					fixed (VkAccelerationStructureBuildRangeInfoKHR* range = ranges)
					fixed (int* primitives = counts)
					{
						Describe(described, range, false);
						for (int i = 0; i < counts.Length; i++)
						{
							primitives[i] = range[i].PrimitiveCount;
						}

						VkAccelerationStructureBuildGeometryInfoKHR build = new VkAccelerationStructureBuildGeometryInfoKHR
						{
							Type = VkAccelerationStructureTypeKHR.BottomLevelKHR,
							Flags = BuildFlags(Flags),
							Mode = VkBuildAccelerationStructureModeKHR.BuildKHR,
							GeometryCount = Geometries.Length,
							Geometries = described
						};
						device.Vk->vkGetAccelerationStructureBuildSizesKHR.Invoke(device.Handle, VkAccelerationStructureBuildTypeKHR.DeviceKHR, &build, primitives, &sizes);
					}
				}

				size = (long)sizes.AccelerationStructureSize.Value;
				BuildScratchSize = (long)sizes.BuildScratchSize.Value;
				UpdateScratchSize = (long)sizes.UpdateScratchSize.Value;
			}

			device.CreateDeviceBuffer(size, VkBufferUsageFlags.AccelerationStructureStorageBitKHR | VkBufferUsageFlags.ShaderDeviceAddressBit, Name ?? "acceleration structure", out buffer, out allocation);
			VkAccelerationStructureCreateInfoKHR info = new VkAccelerationStructureCreateInfoKHR
			{
				Buffer = buffer,
				Size = (Brutal.ByteSize64)(ulong)size,
				Type = top ? VkAccelerationStructureTypeKHR.TopLevelKHR : VkAccelerationStructureTypeKHR.BottomLevelKHR
			};
			VkAccelerationStructureKHR made;
			VkResult result = device.Vk->vkCreateAccelerationStructureKHR.Invoke(device.Handle, &info, null, &made);
			if (result != VkResult.Success)
			{
				device.Retire(RetiredKind.Buffer, buffer.VkHandle, allocation.VkHandle, 0);
				VulkanDevice.Check(result, "vkCreateAccelerationStructureKHR (" + Name + ")");
			}

			Handle = made;
			VkAccelerationStructureDeviceAddressInfoKHR addressInfo = new VkAccelerationStructureDeviceAddressInfoKHR { AccelerationStructure = made };
			Address = device.Vk->vkGetAccelerationStructureDeviceAddressKHR.Invoke(device.Handle, &addressInfo).Value;
			Size = size;
			if (top && !IsCompactCopy)
			{
				// Builds read instances at an address of a multiple of 16; a buffer of this use may start at any 4: room to move.
				device.CreateDeviceBuffer((long)Math.Max(1, MaxInstances) * InstanceSize + 16,
					VkBufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKHR | VkBufferUsageFlags.ShaderDeviceAddressBit | VkBufferUsageFlags.TransferDstBit, (Name ?? "top level") + " instances",
					out instanceBuffer, out instanceAllocation);
				ulong start = device.BufferAddress(instanceBuffer);
				InstanceAddress = (start + 15) / 16 * 16;
				InstanceOffset = (long)(InstanceAddress - start);
			}
		}

		/// <summary>A top level's instances buffer, written by a copy before every build.</summary>
		public VkBuffer InstanceBuffer => instanceBuffer;

		public override long CompactedSize
		{
			get
			{
				if (compactedSize == 0 && compactionQuery >= 0 && !IsDisposed)
				{
					if (compactionSerial > device.CompletedSerial && VulkanDevice.ContextOfThread != null)
					{
						device.Poll();
					}

					if (compactionSerial <= device.CompletedSerial)
					{
						compactedSize = device.ReadCompactionQuery(compactionQuery);
						device.ReleaseCompactionQuery(compactionQuery, 0);
						compactionQuery = -1;
					}
				}

				return compactedSize;
			}
		}

		/// <summary>
		///     A command of the stream uses the structure. A top level being traced reads the bottom levels it holds: they
		///     are marked as well (once a submission).
		/// </summary>
		public void Use()
		{
			long serial = device.RecordingSerial;
			LastUse = serial;
			if (bottomLevelsMarked != serial)
			{
				bottomLevelsMarked = serial;
				foreach (VulkanAccelerationStructure bottom in BottomLevels)
				{
					bottom.LastUse = serial;
				}
			}
		}

		/// <summary>A build or a copy into the structure has been recorded.</summary>
		public void MarkBuilt(int instances, VulkanAccelerationStructure[] bottomLevels)
		{
			IsBuilt = true;
			BuiltInstances = instances;
			BottomLevels = bottomLevels ?? noBottomLevels;
			bottomLevelsMarked = 0;
			Use();
		}

		/// <summary>A command writes the structure's compacted size (after a full build) into a query of its own.</summary>
		public int TakeCompactionQuery()
		{
			if (compactionQuery >= 0)
			{
				device.ReleaseCompactionQuery(compactionQuery, device.RecordingSerial);
			}

			compactionQuery = device.TakeCompactionQuery();
			compactionSerial = device.RecordingSerial;
			compactedSize = 0;
			return compactionQuery;
		}

		/// <summary>The bottom level's triangles as a build reads them (addresses only with <paramref name="withAddresses" />: a size query reads none).</summary>
		public void Describe(VkAccelerationStructureGeometryKHR* geometries, VkAccelerationStructureBuildRangeInfoKHR* ranges, bool withAddresses)
		{
			for (int i = 0; i < Geometries.Length; i++)
			{
				AccelerationStructureTriangles source = Geometries[i];
				VulkanBuffer vertices = (VulkanBuffer)source.VertexBuffer;
				VulkanBuffer indices = (VulkanBuffer)source.IndexBuffer;
				VkAccelerationStructureGeometryTrianglesDataKHR triangles = new VkAccelerationStructureGeometryTrianglesDataKHR
				{
					// R32G32_SFLOAT or R32G32B32_SFLOAT.
					VertexFormat = (VkFormat)(source.PositionFormat == VertexFormat.Float2 ? 103 : 106),
					VertexStride = (Brutal.ByteSize64)(ulong)source.VertexStride,
					MaxVertex = Math.Max(0, source.VertexCount - 1),
					IndexType = indices == null ? VkIndexType.NoneKHR : source.IndexFormat == IndexFormat.UInt16 ? VkIndexType.UInt16 : VkIndexType.UInt32
				};
				if (withAddresses)
				{
					triangles.VertexData = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(device.BufferAddress(vertices.Handle) + (ulong)source.VertexOffset) };
					vertices.Use();
					if (indices != null)
					{
						triangles.IndexData = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(device.BufferAddress(indices.Handle) + (ulong)source.IndexOffset) };
						indices.Use();
					}
				}

				geometries[i] = new VkAccelerationStructureGeometryKHR
				{
					GeometryType = VkGeometryTypeKHR.TrianglesKHR,
					Flags = source.Opaque ? VkGeometryFlagsKHR.OpaqueBitKHR : VkGeometryFlagsKHR.None
				};
				geometries[i].Geometry.Triangles = triangles;
				ranges[i] = new VkAccelerationStructureBuildRangeInfoKHR { PrimitiveCount = source.TriangleCount };
			}
		}

		/// <summary>The instances of a top level's build, read from its instances buffer.</summary>
		public VkAccelerationStructureGeometryKHR InstanceGeometry(ulong address)
		{
			VkAccelerationStructureGeometryKHR geometry = new VkAccelerationStructureGeometryKHR { GeometryType = VkGeometryTypeKHR.InstancesKHR };
			geometry.Geometry.Instances = new VkAccelerationStructureGeometryInstancesDataKHR
			{
				ArrayOfPointers = false,
				Data = new VkDeviceOrHostAddressConstKHR { DeviceAddress = new VkDeviceAddress(address) }
			};
			return geometry;
		}

		public static VkBuildAccelerationStructureFlagsKHR BuildFlags(AccelerationStructureFlags flags)
		{
			VkBuildAccelerationStructureFlagsKHR result = VkBuildAccelerationStructureFlagsKHR.None;
			if ((flags & AccelerationStructureFlags.AllowUpdate) != 0)
			{
				result |= VkBuildAccelerationStructureFlagsKHR.AllowUpdateBitKHR;
			}

			if ((flags & AccelerationStructureFlags.AllowCompaction) != 0)
			{
				result |= VkBuildAccelerationStructureFlagsKHR.AllowCompactionBitKHR;
			}

			if ((flags & AccelerationStructureFlags.PreferFastTrace) != 0)
			{
				result |= VkBuildAccelerationStructureFlagsKHR.PreferFastTraceBitKHR;
			}
			else if ((flags & AccelerationStructureFlags.PreferFastBuild) != 0)
			{
				result |= VkBuildAccelerationStructureFlagsKHR.PreferFastBuildBitKHR;
			}

			return result;
		}

		/// <summary>Throws when a bottom level's triangles cannot be built from.</summary>
		private void Validate(in AccelerationStructureTriangles triangles)
		{
			if (triangles.VertexBuffer == null || triangles.VertexStride <= 0 || triangles.VertexCount <= 0 || triangles.TriangleCount < 0)
			{
				throw new ArgumentException($"Bottom level '{Name}': triangles without a vertex buffer, a stride or vertices.");
			}

			if (triangles.PositionFormat != VertexFormat.Float3 && triangles.PositionFormat != VertexFormat.Float2)
			{
				throw new ArgumentException($"Bottom level '{Name}': positions are Float3 or Float2, not {triangles.PositionFormat}.");
			}

			if (triangles.VertexOffset % 4 != 0 || (triangles.IndexBuffer != null && triangles.IndexOffset % (triangles.IndexFormat == IndexFormat.UInt16 ? 2 : 4) != 0))
			{
				throw new ArgumentException($"Bottom level '{Name}': the vertex offset is a multiple of 4 and the index offset of the index size.");
			}

			RequireInput(triangles.VertexBuffer);
			if (triangles.IndexBuffer != null)
			{
				RequireInput(triangles.IndexBuffer);
			}
		}

		private void RequireInput(GpuBuffer input)
		{
			if ((input.Usage & BufferUsage.AccelerationStructureInput) == 0)
			{
				throw new InvalidOperationException($"Buffer '{input.Name}' holds triangles of bottom level '{Name}' but was made without BufferUsage.AccelerationStructureInput.");
			}
		}

		public override void Dispose()
		{
			if (IsDisposed)
			{
				return;
			}

			IsDisposed = true;
			device.BindingEpoch++;
			if (compactionQuery >= 0)
			{
				device.ReleaseCompactionQuery(compactionQuery, LastUse);
				compactionQuery = -1;
			}

			// The structure before the memory it lies in.
			device.Retire(RetiredKind.AccelerationStructure, Handle.VkHandle, 0, LastUse);
			device.Retire(RetiredKind.Buffer, buffer.VkHandle, allocation.VkHandle, LastUse);
			if (!instanceBuffer.IsNull())
			{
				device.Retire(RetiredKind.Buffer, instanceBuffer.VkHandle, instanceAllocation.VkHandle, LastUse);
			}

			BottomLevels = noBottomLevels;
		}
	}
}
