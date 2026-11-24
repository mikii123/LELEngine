using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Vma;
using LELEngine.Windowing;
using BrutalDevice = Brutal.VulkanApi.Device;
using BrutalInstance = Brutal.VulkanApi.Instance;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The device on Vulkan 1.3. Commands are recorded into a stream of command buffers per queue (the graphics
	///     queue's, and an asynchronous compute queue's where the GPU has one: VulkanDeviceQueues.cs) and sent to the GPU
	///     at a present, a flush, a fence wait or a readback; every graphics submission has a serial number, and what it
	///     uses (buffers, textures, memory taken for a frame) is freed or reused once the GPU has finished it and the
	///     compute work recorded with it.
	///     <para>
	///         One thread at a time gives commands: the one that holds a window's context
	///         (<see cref="VulkanContext.MakeCurrent" />). Programs are built on any thread.
	///     </para>
	/// </summary>
	internal sealed unsafe partial class VulkanDevice : GraphicsDevice
	{
		/// <summary>Bytes of a block of the memory commands take for a frame (uniform values, texels on their way to a texture).</summary>
		private const long ChunkSize = 1 << 20;

		private const int QueriesPerPool = 1024;

		[ThreadStatic]
		private static GraphicsContext currentContext;

		[ThreadStatic]
		private static bool compileOnCallingThread;

		private readonly BrutalInstance instance;
		private readonly PhysicalDevice physicalDevice;
		private readonly BrutalDevice device;
		private readonly VkDebugUtilsMessengerEXT messenger;
		private readonly object retiredSync = new object();
		private readonly List<Retired> retired = new List<Retired>();
		private readonly Dictionary<SamplerDescriptor, VulkanSampler> samplers = new Dictionary<SamplerDescriptor, VulkanSampler>();
		private readonly FormatInfo[] formats;
		private readonly Dictionary<IntPtr, VkSurfaceKHR> surfaces = new Dictionary<IntPtr, VkSurfaceKHR>();

		/// <summary>The windows' contexts alive: what a device given up has to take down with it.</summary>
		private readonly List<VulkanContext> contexts = new List<VulkanContext>();

		// The submission being recorded (its command buffers are the streams': VulkanDeviceQueues.cs).
		private Submission recording = new Submission();
		private readonly Queue<Submission> pending = new Queue<Submission>();
		private readonly Stack<Submission> freeSubmissions = new Stack<Submission>();
		private readonly Stack<VkFence> freeFences = new Stack<VkFence>();
		private readonly Stack<Chunk> freeChunks = new Stack<Chunk>();
		private readonly Stack<VkDescriptorPool> freePools = new Stack<VkDescriptorPool>();
		private readonly Stack<VkSemaphore> freeSemaphores = new Stack<VkSemaphore>();
		private Chunk currentChunk;
		private VkDescriptorPool currentPool;

		// Timestamps.
		private readonly List<VkQueryPool> queryPools = new List<VkQueryPool>();
		private readonly Stack<int> freeQueries = new Stack<int>();

		// What stands in for a texture, a buffer or a vertex attribute nothing was set for.
		private readonly Dictionary<int, VulkanTexture> dummyTextures = new Dictionary<int, VulkanTexture>();
		private VulkanBuffer dummyBuffer;
		private VulkanBuffer attributeDefaults;

		internal static VulkanDevice Instance { get; private set; }

		/// <summary>Vulkan's functions of the device (the BRUTAL framework's table of them).</summary>
		internal readonly VkDevice.FunctionTable* Vk;

		internal readonly VkInstance.FunctionTable* InstanceVk;
		internal readonly VkDevice Handle;
		internal readonly VkPhysicalDevice PhysicalHandle;
		internal readonly VkInstance InstanceHandle;
		internal readonly VkQueue Queue;
		internal readonly int QueueFamily;
		internal readonly VmaAllocatorHandle Allocator;
		internal readonly VkPhysicalDeviceLimits Limits;
		internal readonly float TimestampPeriod;
		internal readonly bool Has2DViewsOf3D;

		/// <summary>Flat varyings take the value of a primitive's last vertex, as on OpenGL (else of its first).</summary>
		internal readonly bool HasProvokingVertexLast;
		internal readonly bool HasIndirectCount;
		internal readonly bool HasMultiDrawIndirect;
		internal readonly bool HasWireframe;
		internal VulkanCommandList CommandList { get; private set; }

		/// <summary>The device was given up (<see cref="Destroy" />): nothing of it exists in the system any more.</summary>
		internal bool IsDestroyed { get; private set; }

		/// <summary>The number of the submission being recorded.</summary>
		internal long RecordingSerial { get; private set; } = 1;

		/// <summary>The last submission the GPU has finished.</summary>
		internal long CompletedSerial { get; private set; }

		/// <summary>
		///     Counts what changes which objects the bindings by number stand for: a binding set, a buffer's storage
		///     replaced, a texture or buffer disposed. A program's descriptor set stays valid while it stands still.
		/// </summary>
		internal int BindingEpoch;

		/// <summary>
		///     The descriptor sets written, by everything bound in them: kept over submissions until an object they name is
		///     destroyed or their pools are full (<see cref="ForgetDescriptorSets" />).
		/// </summary>
		internal readonly Dictionary<DescriptorKey, VkDescriptorSet> DescriptorCache = new Dictionary<DescriptorKey, VkDescriptorSet>();

		/// <summary>
		///     Makes the device with the process's first window: the instance, the window's surface, the GPU that draws,
		///     computes and presents to it, the logical device and what its commands need. On a machine that cannot run
		///     it (no Vulkan loader, no GPU of Vulkan 1.3, an extension or a feature the engine needs missing) it throws
		///     with what is missing, and leaves nothing of the system behind: the caller may start another API.
		/// </summary>
		public VulkanDevice(NativeWindow window)
		{
			if (Instance != null)
			{
				throw new InvalidOperationException("The process has a Vulkan device already.");
			}

			try
			{
				RequireLoader();
				HashSet<string> instanceExtensions = new HashSet<string>(StringComparer.Ordinal);
				byte** required = Glfw.GetRequiredInstanceExtensions(out uint requiredCount);
				if (required == null)
				{
					throw new NotSupportedException("GLFW finds no Vulkan loader with window surfaces on this machine.");
				}

				for (uint i = 0; i < requiredCount; i++)
				{
					instanceExtensions.Add(Marshal.PtrToStringUTF8((IntPtr)required[i]));
				}

				HashSet<string> layers = new HashSet<string>(StringComparer.Ordinal);
				bool validation = false;
				if (Environment.GetEnvironmentVariable("LEL_VULKAN_VALIDATION") == "1")
				{
					if (HasLayer("VK_LAYER_KHRONOS_validation"))
					{
						layers.Add("VK_LAYER_KHRONOS_validation");
						instanceExtensions.Add("VK_EXT_debug_utils");
						validation = true;
					}
					else
					{
						Console.WriteLine("[Vulkan] LEL_VULKAN_VALIDATION=1, but the Khronos validation layer is not installed: going on without it.");
					}
				}

				byte[] engineName = Encoding.UTF8.GetBytes("LELEngine\0");
				fixed (byte* name = engineName)
				{
					try
					{
						instance = Brutal.VulkanApi.Vulkan.CreateInstance(new BrutalInstance.CreateInfo
						{
							AppInfo = new VkApplicationInfo { ApiVersion = 1 << 22 | 3 << 12, EngineName = name, ApplicationName = name },
							Flags = VkInstanceCreateFlags.None,
							EnabledExtensions = instanceExtensions,
							EnabledLayers = layers
						});
					}
					catch (VkResultError e)
					{
						throw new NotSupportedException("No Vulkan instance can be made on this machine (no GPU driver with Vulkan, or one without window surfaces): " + e.Message, e);
					}
				}

				InstanceHandle = instance.Handle;
				InstanceVk = (VkInstance.FunctionTable*)Unsafe.AsPointer(ref instance.PInvoke);
				validationLayerOn = validation;
				if (validation && InstanceVk->vkCreateDebugUtilsMessengerEXT.Invoke != null)
				{
					VkDebugUtilsMessengerCreateInfoEXT messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
					{
						MessageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT | VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT,
						MessageType = VkDebugUtilsMessageTypeFlagsEXT.GeneralBitEXT | VkDebugUtilsMessageTypeFlagsEXT.ValidationBitEXT,
						PfnUserCallback = new PfnVkDebugUtilsMessengerCallbackEXT(
							(delegate* unmanaged[Cdecl]<VkDebugUtilsMessageSeverityFlagsEXT, VkDebugUtilsMessageTypeFlagsEXT, VkDebugUtilsMessengerCallbackDataEXT*, void*, Brutal.Bool32>)
							(delegate* unmanaged[Cdecl]<VkDebugUtilsMessageSeverityFlagsEXT, VkDebugUtilsMessageTypeFlagsEXT, VkDebugUtilsMessengerCallbackDataEXT*, void*, int>)&OnDebugMessage)
					};
					VkDebugUtilsMessengerEXT made;
					if (InstanceVk->vkCreateDebugUtilsMessengerEXT.Invoke(InstanceHandle, &messengerInfo, null, &made) == VkResult.Success)
					{
						messenger = made;
						Console.WriteLine("[Vulkan] The Khronos validation layer is on.");
					}
				}

				// The window's surface comes first: the device has to be one that can present to it.
				VkSurfaceKHR surface = CreateSurface(window.Handle);
				surfaces[window.Handle.NativeHandle] = surface;

				string why = "";
				int bestScore = -1;
				foreach (PhysicalDevice candidate in instance.PhysicalDevices)
				{
					int score = Rate(candidate, surface, out int family, ref why);
					if (score > bestScore)
					{
						bestScore = score;
						physicalDevice = candidate;
						QueueFamily = family;
					}
				}

				if (physicalDevice == null)
				{
					throw new NotSupportedException(instance.PhysicalDevices.Length == 0 ? "This machine has no GPU with a Vulkan driver." : "No GPU of this machine can run the engine on Vulkan:" + why);
				}

				PhysicalHandle = physicalDevice.Handle;
				Has2DViewsOf3D = physicalDevice.SupportsExtension("VK_EXT_image_2d_view_of_3d");

				// Features: asked for what the device has of what the engine uses.
				HasProvokingVertexLast = physicalDevice.SupportsExtension("VK_EXT_provoking_vertex");
				VkPhysicalDeviceProvokingVertexFeaturesEXT provokingHave = new VkPhysicalDeviceProvokingVertexFeaturesEXT();
				VkPhysicalDeviceImage2DViewOf3DFeaturesEXT viewsHave = new VkPhysicalDeviceImage2DViewOf3DFeaturesEXT { Next = HasProvokingVertexLast ? &provokingHave : null };
				VkPhysicalDeviceDepthClipControlFeaturesEXT clipHave = new VkPhysicalDeviceDepthClipControlFeaturesEXT { Next = Has2DViewsOf3D ? &viewsHave : viewsHave.Next };
				VkPhysicalDeviceVulkan13Features have13 = new VkPhysicalDeviceVulkan13Features { Next = &clipHave };
				VkPhysicalDeviceVulkan12Features have12 = new VkPhysicalDeviceVulkan12Features { Next = &have13 };
				VkPhysicalDeviceVulkan11Features have11 = new VkPhysicalDeviceVulkan11Features { Next = &have12 };
				VkPhysicalDeviceFeatures2 have = new VkPhysicalDeviceFeatures2 { Next = &have11 };
				InstanceVk->vkGetPhysicalDeviceFeatures2.Invoke(PhysicalHandle, &have);
				Has2DViewsOf3D = Has2DViewsOf3D && viewsHave.Image2DViewOf3D;
				HasProvokingVertexLast = HasProvokingVertexLast && provokingHave.ProvokingVertexLast;

				VkPhysicalDeviceProvokingVertexFeaturesEXT provokingWant = new VkPhysicalDeviceProvokingVertexFeaturesEXT { ProvokingVertexLast = true };
				VkPhysicalDeviceImage2DViewOf3DFeaturesEXT viewsWant = new VkPhysicalDeviceImage2DViewOf3DFeaturesEXT { Image2DViewOf3D = true, Next = HasProvokingVertexLast ? &provokingWant : null };
				VkPhysicalDeviceDepthClipControlFeaturesEXT clipWant = new VkPhysicalDeviceDepthClipControlFeaturesEXT { DepthClipControl = true, Next = Has2DViewsOf3D ? &viewsWant : viewsWant.Next };
				VkPhysicalDeviceVulkan13Features want13 = new VkPhysicalDeviceVulkan13Features
				{
					DynamicRendering = true,
					Synchronization2 = true,
					Maintenance4 = have13.Maintenance4,
					// What glslang makes of "discard" for Vulkan 1.3.
					ShaderDemoteToHelperInvocation = have13.ShaderDemoteToHelperInvocation,
					ShaderTerminateInvocation = have13.ShaderTerminateInvocation,
					Next = &clipWant
				};
				VkPhysicalDeviceVulkan12Features want12 = new VkPhysicalDeviceVulkan12Features
				{
					DrawIndirectCount = have12.DrawIndirectCount,
					HostQueryReset = true,
					TimelineSemaphore = have12.TimelineSemaphore,
					ShaderOutputLayer = have12.ShaderOutputLayer,
					ShaderOutputViewportIndex = have12.ShaderOutputViewportIndex,
					ShaderSubgroupExtendedTypes = have12.ShaderSubgroupExtendedTypes,
					Next = &want13
				};
				VkPhysicalDeviceVulkan11Features want11 = new VkPhysicalDeviceVulkan11Features { ShaderDrawParameters = true, Next = &want12 };
				VkPhysicalDeviceFeatures2 want = new VkPhysicalDeviceFeatures2 { Next = &want11 };
				VkPhysicalDeviceFeatures core = have.Features;
				want.Features.MultiDrawIndirect = core.MultiDrawIndirect;
				want.Features.DrawIndirectFirstInstance = core.DrawIndirectFirstInstance;
				want.Features.SamplerAnisotropy = core.SamplerAnisotropy;
				want.Features.LargePoints = core.LargePoints;
				want.Features.FillModeNonSolid = core.FillModeNonSolid;
				want.Features.FragmentStoresAndAtomics = core.FragmentStoresAndAtomics;
				want.Features.VertexPipelineStoresAndAtomics = core.VertexPipelineStoresAndAtomics;
				want.Features.GeometryShader = core.GeometryShader;
				want.Features.TessellationShader = core.TessellationShader;
				want.Features.TextureCompressionBC = core.TextureCompressionBC;
				want.Features.ShaderStorageImageExtendedFormats = core.ShaderStorageImageExtendedFormats;
				want.Features.ShaderImageGatherExtended = core.ShaderImageGatherExtended;
				want.Features.ImageCubeArray = core.ImageCubeArray;
				want.Features.IndependentBlend = core.IndependentBlend;
				want.Features.DepthClamp = core.DepthClamp;
				want.Features.ShaderTessellationAndGeometryPointSize = core.ShaderTessellationAndGeometryPointSize;
				want.Features.ShaderClipDistance = core.ShaderClipDistance;
				want.Features.ShaderCullDistance = core.ShaderCullDistance;
				want.Features.ShaderStorageImageReadWithoutFormat = core.ShaderStorageImageReadWithoutFormat;
				want.Features.ShaderStorageImageWriteWithoutFormat = core.ShaderStorageImageWriteWithoutFormat;
				want.Features.SampleRateShading = core.SampleRateShading;
				want.Features.DualSrcBlend = core.DualSrcBlend;
				want.Features.ShaderFloat64 = core.ShaderFloat64;
				want.Features.ShaderInt64 = core.ShaderInt64;
				want.Features.ShaderInt16 = core.ShaderInt16;
				want.Features.ShaderResourceMinLod = core.ShaderResourceMinLod;
				want.Features.ShaderUniformBufferArrayDynamicIndexing = core.ShaderUniformBufferArrayDynamicIndexing;
				want.Features.ShaderSampledImageArrayDynamicIndexing = core.ShaderSampledImageArrayDynamicIndexing;
				want.Features.ShaderStorageBufferArrayDynamicIndexing = core.ShaderStorageBufferArrayDynamicIndexing;
				want.Features.ShaderStorageImageArrayDynamicIndexing = core.ShaderStorageImageArrayDynamicIndexing;
				want.Features.MultiViewport = core.MultiViewport;
				HasIndirectCount = have12.DrawIndirectCount;
				HasMultiDrawIndirect = core.MultiDrawIndirect;
				HasWireframe = core.FillModeNonSolid;

				HashSet<string> deviceExtensions = new HashSet<string>(StringComparer.Ordinal) { "VK_KHR_swapchain", "VK_EXT_depth_clip_control" };
				if (Has2DViewsOf3D)
				{
					deviceExtensions.Add("VK_EXT_image_2d_view_of_3d");
				}

				if (HasProvokingVertexLast)
				{
					deviceExtensions.Add("VK_EXT_provoking_vertex");
				}

				// Ray queries where the GPU has them (VulkanAccelerationStructure.cs); the device is made without them otherwise.
				RayQueryFeatures rayQueries = new RayQueryFeatures();
				HasRayQuery = AskForRayQueries(physicalDevice, deviceExtensions, &rayQueries, &want, &want12);
				AskForConservativeRasterization(physicalDevice, deviceExtensions);
				AskForShaderInfo(physicalDevice, deviceExtensions);

				// The graphics queue, and an asynchronous compute queue of a family of its own where the GPU has one
				// (VulkanDeviceQueues.cs); the device's timeline semaphores order the two.
				int computeFamily = FindComputeFamily(PhysicalHandle, QueueFamily, have12.TimelineSemaphore);
				float priority = 1f;
				VkDeviceQueueCreateInfo* queueInfos = stackalloc VkDeviceQueueCreateInfo[2];
				queueInfos[0] = new VkDeviceQueueCreateInfo { QueueFamilyIndex = QueueFamily, QueueCount = 1, QueuePriorities = &priority };
				queueInfos[1] = new VkDeviceQueueCreateInfo { QueueFamilyIndex = computeFamily, QueueCount = 1, QueuePriorities = &priority };
				BrutalDevice.CreateInfo deviceInfo = new BrutalDevice.CreateInfo();
				deviceInfo.Extensions = deviceExtensions;
				deviceInfo.QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(queueInfos, computeFamily >= 0 ? 2 : 1);
				deviceInfo.Next = &want;
				device = physicalDevice.CreateDevice(deviceInfo);
				Handle = device.Handle;
				Vk = (VkDevice.FunctionTable*)Unsafe.AsPointer(ref device.PInvoke);
				VkQueue queue;
				Vk->vkGetDeviceQueue.Invoke(Handle, QueueFamily, 0, &queue);
				Queue = queue;

				Allocator = device.CreateVmaAllocatorHandle(new VmaAllocatorCreateInfo
				{
					// Acceleration structure builds read their inputs by address.
					Flags = HasRayQuery ? VmaAllocatorCreateFlags.BufferDeviceAddressBit : VmaAllocatorCreateFlags.None,
					PhysicalDevice = PhysicalHandle,
					Device = Handle,
					Instance = InstanceHandle,
					VulkanApiVersion = 1 << 22 | 3 << 12
				});

				CreateStreams(computeFamily);

				VkPhysicalDeviceVulkan11Properties properties11 = new VkPhysicalDeviceVulkan11Properties();
				VkPhysicalDeviceProperties2 properties = new VkPhysicalDeviceProperties2 { Next = &properties11 };
				InstanceVk->vkGetPhysicalDeviceProperties2.Invoke(PhysicalHandle, &properties);
				Limits = properties.Properties.Limits;
				TimestampPeriod = Limits.TimestampPeriod;

				formats = new FormatInfo[(int)TextureFormat.BC7SRGB + 1];
				for (TextureFormat format = TextureFormat.R8; format <= TextureFormat.BC7SRGB; format++)
				{
					formats[(int)format] = FindFormat(format);
				}

				FillCapabilities(properties.Properties, properties11, core);
				Instance = this;
				CommandList = new VulkanCommandList(this);
			}
			catch
			{
				// Nothing of a device that cannot be made stays behind: another API may start in this process.
				Release();
				if (Instance == this)
				{
					Instance = null;
				}

				throw;
			}
		}

		public override GraphicsApi Api => GraphicsApi.Vulkan;

		public override CommandList Commands
		{
			get
			{
				if (!(currentContext is VulkanContext))
				{
					throw new InvalidOperationException("No window's graphics context is current on this thread: it cannot give graphics commands.");
				}

				return CommandList;
			}
		}

		public override GpuTexture Backbuffer => (currentContext as VulkanContext)?.Backbuffer;
		public override bool HasCurrentContext => currentContext != null;

		/// <summary>Programs made by the calling thread are built on it, not on the device's threads.</summary>
		internal static bool CompileOnCallingThread => compileOnCallingThread;

		internal static void SetCurrent(GraphicsContext context)
		{
			currentContext = context;
		}

		internal static GraphicsContext ContextOfThread => currentContext;

		#region Making things

		internal override GraphicsContext CreateWindowContext(NativeWindow window, NativeWindowSettings settings)
		{
			if (!surfaces.Remove(window.Handle.NativeHandle, out VkSurfaceKHR surface))
			{
				surface = CreateSurface(window.Handle);
			}

			VulkanContext context = new VulkanContext(this, window, surface);
			try
			{
				// The swapchain is made now: a window the device cannot show frames in fails here, where another API
				// can still be taken, not at its first present.
				context.Prepare();
			}
			catch
			{
				context.Dispose();
				throw;
			}

			return context;
		}

		/// <summary>A window's context is made (true) or disposed.</summary>
		internal void Register(VulkanContext context, bool alive)
		{
			if (alive)
			{
				contexts.Add(context);
			}
			else
			{
				contexts.Remove(context);
			}
		}

		/// <summary>
		///     Gives the device up before it ever drew (its first window's context could not be made): every object of
		///     the system it holds is destroyed (its windows' swapchains and surfaces, its memory and pools, the device,
		///     the debug messenger, the instance), so that another API can start in this process.
		/// </summary>
		internal override void Destroy()
		{
			if (IsDestroyed)
			{
				return;
			}

			Release();
			IsDestroyed = true;
			if (Instance == this)
			{
				Instance = null;
			}

			if (currentContext is VulkanContext || currentContext is VulkanWorkerContext)
			{
				currentContext = null;
			}
		}

		/// <summary>
		///     Destroys what the device holds of the system, as far as it was made, in the order Vulkan asks for: what
		///     the device made for itself (after the GPU has finished with it), the device, the surfaces, the debug
		///     messenger, the instance. Objects the caller made with the device (textures, buffers, programs) are the
		///     caller's to dispose before.
		/// </summary>
		private void Release()
		{
			if (device != null)
			{
				Vk->vkDeviceWaitIdle.Invoke(Handle);
				while (pending.Count > 0)
				{
					Complete(pending.Dequeue());
				}

				if (graphicsStream != null)
				{
					ReleaseBinarySemaphores(recording.BinaryWaited);
				}

				foreach (VkFence computeFence in recording.ComputeFences)
				{
					freeFences.Push(computeFence);
				}

				recording.ComputeFences.Clear();

				// Swapchains given up before (a resize) go before the surfaces they were made for.
				DestroyAllRetired();
				foreach (VulkanContext context in contexts.ToArray())
				{
					context.Abandon();
				}

				contexts.Clear();
				ReleaseDescriptorCache();

				// What was recorded and never sent goes back unsent: its memory and pools are free.
				foreach (Chunk chunk in recording.Chunks)
				{
					Vma.PInvoke.vmaDestroyBuffer(Allocator, chunk.Buffer, chunk.Allocation);
				}

				foreach (VkDescriptorPool pool in recording.Pools)
				{
					Vk->vkDestroyDescriptorPool.Invoke(Handle, pool, null);
				}

				recording.Chunks.Clear();
				recording.Pools.Clear();
				currentChunk = null;
				currentPool = default;
				foreach (VulkanTexture texture in dummyTextures.Values)
				{
					texture.Dispose();
				}

				dummyTextures.Clear();
				dummyBuffer?.Dispose();
				dummyBuffer = null;
				attributeDefaults?.Dispose();
				attributeDefaults = null;
				ReleaseRayQueries();
				DestroyAllRetired();
				foreach (VulkanSampler sampler in samplers.Values)
				{
					Vk->vkDestroySampler.Invoke(Handle, sampler.Handle, null);
				}

				samplers.Clear();
				while (freeChunks.Count > 0)
				{
					Chunk chunk = freeChunks.Pop();
					Vma.PInvoke.vmaDestroyBuffer(Allocator, chunk.Buffer, chunk.Allocation);
				}

				while (freePools.Count > 0)
				{
					Vk->vkDestroyDescriptorPool.Invoke(Handle, freePools.Pop(), null);
				}

				while (freeFences.Count > 0)
				{
					Vk->vkDestroyFence.Invoke(Handle, freeFences.Pop(), null);
				}

				while (freeSemaphores.Count > 0)
				{
					Vk->vkDestroySemaphore.Invoke(Handle, freeSemaphores.Pop(), null);
				}

				foreach (VkQueryPool queries in queryPools)
				{
					Vk->vkDestroyQueryPool.Invoke(Handle, queries, null);
				}

				queryPools.Clear();
				freeQueries.Clear();
				ReleaseQueryValues();
				ReleaseQueues();

				if (!pipelineCache.IsNull())
				{
					Vk->vkDestroyPipelineCache.Invoke(Handle, pipelineCache, null);
					pipelineCache = default;
				}

				if (!Allocator.IsNull())
				{
					Vma.PInvoke.vmaDestroyAllocator(Allocator);
				}

				device.Dispose();
			}

			if (instance != null)
			{
				foreach (VkSurfaceKHR surface in surfaces.Values)
				{
					InstanceVk->vkDestroySurfaceKHR.Invoke(InstanceHandle, surface, null);
				}

				surfaces.Clear();
				if (!messenger.IsNull())
				{
					InstanceVk->vkDestroyDebugUtilsMessengerEXT.Invoke(InstanceHandle, messenger, null);
				}

				instance.Dispose();
			}
		}

		public override GraphicsContext CreateWorkerContext()
		{
			return new VulkanWorkerContext();
		}

		public override void SetParallelShaderCompile(bool enabled)
		{
			compileOnCallingThread = !enabled;
		}

		public override GpuTexture CreateTexture(in TextureDescriptor descriptor)
		{
			return new VulkanTexture(this, descriptor, false);
		}

		public override GpuBuffer CreateBuffer(in BufferDescriptor descriptor)
		{
			return new VulkanBuffer(this, descriptor);
		}

		public override GpuSampler GetSampler(in SamplerDescriptor descriptor)
		{
			lock (samplers)
			{
				if (!samplers.TryGetValue(descriptor, out VulkanSampler sampler))
				{
					sampler = new VulkanSampler(this, descriptor);
					samplers.Add(descriptor, sampler);
				}

				return sampler;
			}
		}

		public override GpuProgram CreateProgram(ProgramDescriptor descriptor)
		{
			return new VulkanProgram(this, descriptor, null);
		}

		public override GpuProgram CreateProgramFromBinary(ProgramDescriptor descriptor, byte[] binary)
		{
			List<KeyValuePair<ShaderStage, byte[]>> stages = VulkanProgram.ReadBinary(descriptor, binary);
			return stages != null ? new VulkanProgram(this, descriptor, stages) : null;
		}

		public override GpuFence CreateFence()
		{
			return new VulkanFence(this, FenceSerial);
		}

		public override GpuTimestamp CreateTimestamp()
		{
			return new VulkanTimestamp(this);
		}

		public override void Flush()
		{
			Submit();
		}

		public override void Finish()
		{
			Submit();
			WaitFor(RecordingSerial - 1, long.MaxValue);
		}

		private VkSurfaceKHR CreateSurface(GlfwWindow window)
		{
			int result = Glfw.CreateWindowSurface(InstanceHandle.VkHandle, window, out ulong surface);
			if (result != 0)
			{
				throw new InvalidOperationException("GLFW could not make a Vulkan surface for the window: " + (VkResult)result);
			}

			return new VkSurfaceKHR((nint)surface);
		}

		#endregion

		#region Formats

		internal ref readonly FormatInfo Format(TextureFormat format)
		{
			return ref formats[(int)format];
		}

		internal VkFormatFeatureFlags FormatFeatures(VkFormat format)
		{
			VkFormatProperties properties;
			InstanceVk->vkGetPhysicalDeviceFormatProperties.Invoke(PhysicalHandle, format, &properties);
			return properties.OptimalTilingFeatures;
		}

		private FormatInfo FindFormat(TextureFormat format)
		{
			int number;
			int sampled = 0;
			switch (format)
			{
				case TextureFormat.R8: number = 9; break;
				case TextureFormat.RG8: number = 16; break;
				case TextureFormat.RGBA8: number = 37; break;
				case TextureFormat.SRGBA8:
					// The engine writes sRGB textures as they are (draws, blending, clears, blits and copies take and give
					// the stored bytes, as OpenGL without GL_FRAMEBUFFER_SRGB does) and only samplers decode: the image
					// is R8G8B8A8_UNORM and samplers read it through an R8G8B8A8_SRGB view.
					number = 37;
					sampled = 43;
					break;
				case TextureFormat.R16: number = 70; break;
				case TextureFormat.RG16: number = 77; break;
				case TextureFormat.RGBA16: number = 91; break;
				case TextureFormat.R16F: number = 76; break;
				case TextureFormat.RG16F: number = 83; break;
				case TextureFormat.RGBA16F: number = 97; break;
				case TextureFormat.R32F: number = 100; break;
				case TextureFormat.RG32F: number = 103; break;
				case TextureFormat.RGBA32F: number = 109; break;
				case TextureFormat.R11G11B10F: number = 122; break;
				case TextureFormat.R8UI: number = 13; break;
				case TextureFormat.R16UI: number = 74; break;
				case TextureFormat.R32UI: number = 98; break;
				case TextureFormat.RGBA32UI: number = 107; break;
				case TextureFormat.Depth24:
					// VK_FORMAT_X8_D24_UNORM_PACK32 where the device draws into it, else 32 bit floats.
					number = (FormatFeatures((VkFormat)125) & VkFormatFeatureFlags.DepthStencilAttachmentBit) != 0 ? 125 : 126;
					break;
				case TextureFormat.Depth32F: number = 126; break;
				case TextureFormat.BC1: number = 131; break;
				case TextureFormat.BC1SRGB: number = 132; break;
				case TextureFormat.BC1A: number = 133; break;
				case TextureFormat.BC1ASRGB: number = 134; break;
				case TextureFormat.BC2: number = 135; break;
				case TextureFormat.BC2SRGB: number = 136; break;
				case TextureFormat.BC3: number = 137; break;
				case TextureFormat.BC3SRGB: number = 138; break;
				case TextureFormat.BC4: number = 139; break;
				case TextureFormat.BC5: number = 141; break;
				case TextureFormat.BC6H: number = 143; break;
				case TextureFormat.BC6HSigned: number = 144; break;
				case TextureFormat.BC7: number = 145; break;
				default: number = 146; break;
			}

			return new FormatInfo
			{
				Format = (VkFormat)number,
				SampledFormat = (VkFormat)(sampled != 0 ? sampled : number),
				Features = FormatFeatures((VkFormat)number),
				PackedDepth = number == 125
			};
		}

		#endregion

		#region Recording and submitting

		/// <summary>Something was recorded for the submission being made (by either queue).</summary>
		internal bool HasRecorded => graphicsStream.HasRecorded || recording.ComputeValue != 0 || (computeStream != null && computeStream.HasRecorded);

		/// <summary>The submission a fence made now waits for.</summary>
		internal long FenceSerial => HasRecorded ? RecordingSerial : RecordingSerial - 1;

		/// <summary>The command buffer of the stream of the queue being recorded for; begun on first use.</summary>
		internal VkCommandBuffer MainCommands
		{
			get
			{
				VulkanStream stream = recordingStream;
				if (!stream.MainBegun)
				{
					stream.Main = BeginCommandBuffer(stream);
					stream.MainBegun = true;
				}

				return stream.Main;
			}
		}

		/// <summary>
		///     The command buffer that runs before the stream's, for a command recorded outside the tracking of uses: what
		///     it does is made visible to the whole stream (a barrier ends the command buffer).
		/// </summary>
		internal VkCommandBuffer BeginSetupCommand()
		{
			recordingStream.SetupUntracked = true;
			return SetupCommands;
		}

		/// <summary>
		///     The command buffer that runs before the stream's: for uploads into what no command of the stream has
		///     used yet, which so need not interrupt a render pass, and for the barriers of such resources. Each command
		///     recorded into it comes with the barriers its uses need (<see cref="Sync" />).
		/// </summary>
		internal VkCommandBuffer SetupCommands
		{
			get
			{
				VulkanStream stream = recordingStream;
				if (!stream.SetupBegun)
				{
					stream.Setup = BeginCommandBuffer(stream);
					stream.SetupBegun = true;
				}

				return stream.Setup;
			}
		}

		/// <summary>
		///     Every earlier command's writes become visible to every later command: for commands recorded outside the
		///     tracking of uses (<see cref="VulkanCommandList.BeginTransfer" />).
		/// </summary>
		internal void GlobalBarrier(VkCommandBuffer commands)
		{
			VkMemoryBarrier2 barrier = new VkMemoryBarrier2
			{
				SrcStageMask = VkPipelineStageFlags2.AllCommandsBit,
				SrcAccessMask = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit,
				DstStageMask = VkPipelineStageFlags2.AllCommandsBit,
				DstAccessMask = VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit
			};
			VkDependencyInfo dependency = new VkDependencyInfo { MemoryBarrierCount = 1, MemoryBarriers = &barrier };
			Vk->vkCmdPipelineBarrier2.Invoke(commands, &dependency);
		}

		/// <summary>A command buffer of a stream's pool, begun.</summary>
		private VkCommandBuffer BeginCommandBuffer(VulkanStream stream)
		{
			VkCommandBuffer commands;
			if (stream.Free.Count > 0)
			{
				commands = stream.Free.Pop();
			}
			else
			{
				commands = Brutal.VulkanApi.Abstractions.DeviceExtensions.AllocateCommandBuffer(device, new VkCommandBufferAllocateInfo { CommandPool = stream.Pool, Level = VkCommandBufferLevel.Primary })
					.Handle;
			}

			VkCommandBufferBeginInfo begin = new VkCommandBufferBeginInfo { Flags = VkCommandBufferUsageFlags.OneTimeSubmitBit };
			Check(Vk->vkBeginCommandBuffer.Invoke(commands, &begin), "vkBeginCommandBuffer");
			return commands;
		}

		/// <summary>
		///     Sends what was recorded to the GPU (nothing when nothing was recorded and no semaphore is given) and
		///     starts the next submission: the compute queue's recording first (it belongs to this submission), then the
		///     graphics queue's. An open render pass goes on in the next one. The commands of the stages
		///     <paramref name="waitStages" /> wait for the semaphore <paramref name="wait" />.
		/// </summary>
		internal void Submit(VkSemaphore wait = default, VkSemaphore signal = default, VkPipelineStageFlags2 waitStages = VkPipelineStageFlags2.AllCommandsBit)
		{
			SubmitCompute();
			VulkanStream stream = graphicsStream;
			if (!stream.HasRecorded && recording.ComputeValue == 0 && wait.IsNull() && signal.IsNull())
			{
				Poll();
				return;
			}

			VulkanStream before = recordingStream;
			recordingStream = stream;
			CommandList.BeforeSubmit(stream);
			VkCommandBufferSubmitInfo* buffers = stackalloc VkCommandBufferSubmitInfo[2];
			int count = EndCommandBuffers(stream, buffers, recording.Commands);
			// Waits: the swapchain's image (the stages given), the compute queue's work this submission needs. Signals:
			// the present's semaphore, the graphics queue's timeline (the compute queue waits for its values).
			if (!wait.IsNull())
			{
				submitWaits.Add(new VkSemaphoreSubmitInfo { Semaphore = wait, StageMask = waitStages });
			}

			if (!signal.IsNull())
			{
				submitSignals.Add(new VkSemaphoreSubmitInfo { Semaphore = signal, StageMask = VkPipelineStageFlags2.AllCommandsBit });
			}

			AddQueueSemaphores(stream);
			VkFence fence = TakeFence();
			SendBatch(Queue, buffers, count, fence);
			if (traceQueues && computeStream != null)
			{
				Console.WriteLine($"[queues] graphics sends {stream.RecordingValue} (serial {RecordingSerial}, {count} command buffers), waiting for compute {stream.WaitValue}");
			}

			stream.SubmittedValue++;
			stream.WaitValue = 0;
			recording.Serial = RecordingSerial;
			recording.Fence = fence;
			recording.WaitSemaphore = wait;
			pending.Enqueue(recording);
			recording = freeSubmissions.Count > 0 ? freeSubmissions.Pop() : new Submission();
			RecordingSerial++;
			currentChunk = null;
			recordingStream = before;
			CommandList.AfterSubmit(stream);
			Poll();
		}

		/// <summary>An unsignalled fence.</summary>
		private VkFence TakeFence()
		{
			if (freeFences.Count > 0)
			{
				return freeFences.Pop();
			}

			VkFenceCreateInfo fenceInfo = new VkFenceCreateInfo();
			VkFence fence;
			Check(Vk->vkCreateFence.Invoke(Handle, &fenceInfo, null, &fence), "vkCreateFence");
			return fence;
		}

		/// <summary>Whether the GPU has run a submission: its graphics commands and the compute queue's recorded with it.</summary>
		private bool Finished(Submission submission)
		{
			if (Vk->vkGetFenceStatus.Invoke(Handle, submission.Fence) != VkResult.Success)
			{
				return false;
			}

			foreach (VkFence computeFence in submission.ComputeFences)
			{
				if (Vk->vkGetFenceStatus.Invoke(Handle, computeFence) != VkResult.Success)
				{
					return false;
				}
			}

			return submission.ComputeValue == 0 || ComputeReached(submission.ComputeValue);
		}

		/// <summary>Takes note of the submissions the GPU has finished, and frees or takes back what they used.</summary>
		internal void Poll()
		{
			while (pending.Count > 0 && Finished(pending.Peek()))
			{
				Complete(pending.Dequeue());
			}

			if (pending.Count == 0)
			{
				CollectRetired();
			}
		}

		/// <summary>Waits until the GPU has finished a submission (sending it first when it is the one being recorded); false when the time runs out.</summary>
		internal bool WaitFor(long serial, long timeoutNanoseconds)
		{
			if (serial <= CompletedSerial)
			{
				return true;
			}

			if (serial >= RecordingSerial)
			{
				Submit();
				if (serial >= RecordingSerial)
				{
					// Nothing was recorded for it: there is nothing to wait for.
					return true;
				}
			}

			while (pending.Count > 0 && CompletedSerial < serial)
			{
				Submission submission = pending.Peek();
				VkFence fence = submission.Fence;
				VkResult result = Vk->vkWaitForFences.Invoke(Handle, 1, &fence, true, (nint)Math.Max(0, timeoutNanoseconds));
				if (result == VkResult.Timeout)
				{
					return false;
				}

				Check(result, "vkWaitForFences");
				foreach (VkFence computeFence in submission.ComputeFences)
				{
					VkFence waited = computeFence;
					Check(Vk->vkWaitForFences.Invoke(Handle, 1, &waited, true, nint.MaxValue), "vkWaitForFences");
				}

				if (submission.ComputeValue != 0 && !WaitForCompute(submission.ComputeValue, timeoutNanoseconds))
				{
					return false;
				}

				Complete(pending.Dequeue());
			}

			return true;
		}

		/// <summary>Submissions the GPU has not finished.</summary>
		internal int PendingSubmissions => pending.Count;

		/// <summary>Waits for the oldest unfinished submission.</summary>
		internal void WaitForOldest()
		{
			if (pending.Count > 0)
			{
				WaitFor(pending.Peek().Serial, long.MaxValue);
			}
		}

		private void Complete(Submission submission)
		{
			CompletedSerial = submission.Serial;
			VkFence fence = submission.Fence;
			Vk->vkResetFences.Invoke(Handle, 1, &fence);
			freeFences.Push(fence);
			foreach (VkCommandBuffer commands in submission.Commands)
			{
				graphicsStream.Free.Push(commands);
			}

			foreach (VkCommandBuffer commands in submission.ComputeCommands)
			{
				computeStream.Free.Push(commands);
			}

			foreach (Chunk chunk in submission.Chunks)
			{
				if (--chunk.Users > 0)
				{
					// A later submission uses it too (a split between the queues in the middle of a command).
					continue;
				}

				if (chunk.Size == ChunkSize)
				{
					chunk.Used = 0;
					freeChunks.Push(chunk);
				}
				else
				{
					Vma.PInvoke.vmaDestroyBuffer(Allocator, chunk.Buffer, chunk.Allocation);
				}
			}

			foreach (VkDescriptorPool pool in submission.Pools)
			{
				Vk->vkResetDescriptorPool.Invoke(Handle, pool, default);
				freePools.Push(pool);
			}

			releasedQueries.AddRange(submission.Queries);

			if (!submission.WaitSemaphore.IsNull())
			{
				freeSemaphores.Push(submission.WaitSemaphore);
			}

			if (submission.BinaryWaited.Count > 0)
			{
				ReleaseBinarySemaphores(submission.BinaryWaited);
			}

			foreach (VkFence computeFence in submission.ComputeFences)
			{
				VkFence reset = computeFence;
				Vk->vkResetFences.Invoke(Handle, 1, &reset);
				freeFences.Push(reset);
			}

			submission.ComputeFences.Clear();

			submission.Commands.Clear();
			submission.ComputeCommands.Clear();
			submission.ComputeValue = 0;
			submission.Chunks.Clear();
			submission.Pools.Clear();
			submission.Queries.Clear();
			submission.WaitSemaphore = default;
			freeSubmissions.Push(submission);
			CollectRetired();
		}

		#endregion

		#region Memory for a frame

		/// <summary>
		///     Host memory the GPU reads as a buffer, valid until the submission being recorded has finished: uniform
		///     values, data on its way into a texture or a buffer.
		/// </summary>
		internal byte* AllocateTransient(long size, long alignment, out VkBuffer buffer, out long offset)
		{
			Chunk chunk = currentChunk;
			long start = chunk != null ? (chunk.Used + alignment - 1) / alignment * alignment : 0;
			if (chunk == null || start + size > chunk.Size)
			{
				if (size > ChunkSize)
				{
					// A block of its own, freed with the submission.
					chunk = CreateChunk(size);
					chunk.Users = 1;
					recording.Chunks.Add(chunk);
					buffer = chunk.Buffer;
					offset = 0;
					return chunk.Mapped;
				}

				chunk = freeChunks.Count > 0 ? freeChunks.Pop() : CreateChunk(ChunkSize);
				chunk.Users = 1;
				recording.Chunks.Add(chunk);
				currentChunk = chunk;
				start = 0;
			}

			chunk.Used = start + size;
			buffer = chunk.Buffer;
			offset = start;
			return chunk.Mapped + start;
		}

		private Chunk CreateChunk(long size)
		{
			VkBufferCreateInfo info = new VkBufferCreateInfo
			{
				Size = (Brutal.ByteSize64)(ulong)size,
				Usage = VkBufferUsageFlags.UniformBufferBit | VkBufferUsageFlags.IndirectBufferBit | VkBufferUsageFlags.TransferSrcBit | VkBufferUsageFlags.TransferDstBit
			};
			int* families = stackalloc int[2];
			ShareBuffer(ref info, families);
			VmaAllocationCreateInfo allocation = new VmaAllocationCreateInfo
			{
				Usage = VmaMemoryUsage.Auto,
				Flags = VmaAllocationCreateFlags.MappedBit | VmaAllocationCreateFlags.HostAccessSequentialWriteBit
			};
			Chunk chunk = new Chunk { Size = size };
			VkBuffer buffer;
			VmaAllocation handle;
			VmaAllocationInfo made;
			Check(Vma.PInvoke.vmaCreateBuffer(Allocator, &info, &allocation, &buffer, &handle, &made), "vmaCreateBuffer");
			chunk.Buffer = buffer;
			chunk.Allocation = handle;
			chunk.Mapped = (byte*)(void*)made.PMappedData;
			return chunk;
		}

		/// <summary>A descriptor set of a layout, for the descriptor cache: alive until the cache gives it up (<see cref="ForgetDescriptorSets" />).</summary>
		internal VkDescriptorSet AllocateDescriptorSet(VkDescriptorSetLayout layout)
		{
			for (int attempt = 0; attempt < 2; attempt++)
			{
				if (currentPool.IsNull())
				{
					if (cachePools.Count >= MaxCachePools)
					{
						ForgetDescriptorSets();
					}

					currentPool = freePools.Count > 0 ? freePools.Pop() : CreateDescriptorPool();
					cachePools.Add(currentPool);
				}

				VkDescriptorSetAllocateInfo info = new VkDescriptorSetAllocateInfo { DescriptorPool = currentPool, DescriptorSetCount = 1, SetLayouts = &layout };
				VkDescriptorSet set;
				VkResult result = Vk->vkAllocateDescriptorSets.Invoke(Handle, &info, &set);
				if (result == VkResult.Success)
				{
					DescriptorSetsWritten++;
					return set;
				}

				// The pool is full: the next one.
				currentPool = default;
			}

			throw new InvalidOperationException("A descriptor set does not fit an empty descriptor pool: the program binds more than the device's pools hold.");
		}

		private VkDescriptorPool CreateDescriptorPool()
		{
			int count = HasRayQuery ? 7 : 6;
			VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[7];
			sizes[0] = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 8192 };
			sizes[1] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBufferDynamic, DescriptorCount = 2048 };
			sizes[2] = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = 4096 };
			sizes[3] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 4096 };
			sizes[4] = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageImage, DescriptorCount = 2048 };
			sizes[5] = new VkDescriptorPoolSize { Type = VkDescriptorType.SampledImage, DescriptorCount = 16 };
			sizes[6] = new VkDescriptorPoolSize { Type = VkDescriptorType.AccelerationStructureKHR, DescriptorCount = 256 };
			VkDescriptorPoolCreateInfo info = new VkDescriptorPoolCreateInfo { MaxSets = 1024, PoolSizeCount = count, PoolSizes = sizes };
			VkDescriptorPool pool;
			Check(Vk->vkCreateDescriptorPool.Invoke(Handle, &info, null, &pool), "vkCreateDescriptorPool");
			return pool;
		}

		/// <summary>A semaphore for one acquire of a swapchain image; it comes back when the submission that waits on it has finished.</summary>
		internal VkSemaphore TakeSemaphore()
		{
			return freeSemaphores.Count > 0 ? freeSemaphores.Pop() : CreateSemaphore();
		}

		internal void ReturnSemaphore(VkSemaphore semaphore)
		{
			freeSemaphores.Push(semaphore);
		}

		internal VkSemaphore CreateSemaphore()
		{
			VkSemaphoreCreateInfo info = new VkSemaphoreCreateInfo();
			VkSemaphore semaphore;
			Check(Vk->vkCreateSemaphore.Invoke(Handle, &info, null, &semaphore), "vkCreateSemaphore");
			return semaphore;
		}

		#endregion

		#region Timestamps

		/// <summary>A reset timestamp query no unfinished command writes, for the submission being recorded.</summary>
		internal int TakeQuery()
		{
			if (freeQueries.Count == 0 && releasedQueries.Count > 0)
			{
				ResetReleasedQueries();
			}

			if (freeQueries.Count == 0)
			{
				VkQueryPoolCreateInfo info = new VkQueryPoolCreateInfo { QueryType = VkQueryType.Timestamp, QueryCount = QueriesPerPool };
				VkQueryPool pool;
				Check(Vk->vkCreateQueryPool.Invoke(Handle, &info, null, &pool), "vkCreateQueryPool");
				Vk->vkResetQueryPool.Invoke(Handle, pool, 0, QueriesPerPool);
				int first = queryPools.Count * QueriesPerPool;
				queryPools.Add(pool);
				AddQueryValues();
				for (int i = QueriesPerPool - 1; i >= 0; i--)
				{
					freeQueries.Push(first + i);
				}
			}

			int query = freeQueries.Pop();
			queryWrites[query] = RecordingSerial;
			recordingStream.WrittenQueries.Add(query);
			return query;
		}

		/// <summary>The query is given back once the given submission has finished (0: now); it is reset before it is taken again.</summary>
		internal void ReleaseQuery(int query, long serial)
		{
			if (serial <= CompletedSerial)
			{
				releasedQueries.Add(query);
			}
			else if (serial >= RecordingSerial)
			{
				recording.Queries.Add(query);
			}
			else
			{
				foreach (Submission submission in pending)
				{
					if (submission.Serial == serial)
					{
						submission.Queries.Add(query);
						return;
					}
				}

				releasedQueries.Add(query);
			}
		}

		internal VkQueryPool QueryPool(int query, out int index)
		{
			index = query % QueriesPerPool;
			return queryPools[query / QueriesPerPool];
		}

		#endregion

		#region Lifetimes

		/// <summary>
		///     Destroys an object once the submission that used it last has finished; at once when that is so already.
		///     From any thread.
		/// </summary>
		internal void Retire(RetiredKind kind, nint handle, nint allocation, long lastUse, VulkanBuffer owner = null)
		{
			if (handle == 0)
			{
				return;
			}

			lock (retiredSync)
			{
				retired.Add(new Retired { Kind = kind, Handle = handle, Allocation = allocation, Serial = lastUse, Owner = owner });
			}
		}

		private void CollectRetired()
		{
			lock (retiredSync)
			{
				int kept = 0;
				for (int i = 0; i < retired.Count; i++)
				{
					Retired item = retired[i];
					if (item.Serial > CompletedSerial)
					{
						retired[kept++] = item;
						continue;
					}

					DestroyRetired(item, false);
				}

				retired.RemoveRange(kept, retired.Count - kept);
			}
		}

		/// <summary>Destroys every object waiting for the GPU to finish with it, now: the device is being given up and the GPU is idle.</summary>
		private void DestroyAllRetired()
		{
			lock (retiredSync)
			{
				foreach (Retired item in retired)
				{
					DestroyRetired(item, true);
				}

				retired.Clear();
			}
		}

		/// <param name="final">The device is being given up: a buffer's storage is not kept for the buffer's next use.</param>
		private void DestroyRetired(in Retired item, bool final)
		{
			if (item.Kind == RetiredKind.Buffer && !final && item.Owner != null && item.Owner.TakeBack(item.Handle, item.Allocation))
			{
				// Kept for the buffer's next storage.
				return;
			}

			if (NamedByDescriptorSets(item.Kind) && DescriptorCache.Count > 0)
			{
				// The handle may come back as another object's: no cached set may name it then.
				ForgetDescriptorSetsNaming(item.Kind, item.Handle);
			}

			switch (item.Kind)
			{
				case RetiredKind.Buffer:
					Vma.PInvoke.vmaDestroyBuffer(Allocator, new VkBuffer(item.Handle), new VmaAllocation(item.Allocation));
					break;
				case RetiredKind.Image:
					Vma.PInvoke.vmaDestroyImage(Allocator, new VkImage(item.Handle), new VmaAllocation(item.Allocation));
					break;
				case RetiredKind.ImageView:
					Vk->vkDestroyImageView.Invoke(Handle, new VkImageView(item.Handle), null);
					break;
				case RetiredKind.Pipeline:
					Vk->vkDestroyPipeline.Invoke(Handle, new VkPipeline(item.Handle), null);
					break;
				case RetiredKind.ShaderModule:
					Vk->vkDestroyShaderModule.Invoke(Handle, new VkShaderModule(item.Handle), null);
					break;
				case RetiredKind.SetLayout:
					Vk->vkDestroyDescriptorSetLayout.Invoke(Handle, new VkDescriptorSetLayout(item.Handle), null);
					break;
				case RetiredKind.PipelineLayout:
					Vk->vkDestroyPipelineLayout.Invoke(Handle, new VkPipelineLayout(item.Handle), null);
					break;
				case RetiredKind.Swapchain:
					Vk->vkDestroySwapchainKHR.Invoke(Handle, new VkSwapchainKHR(item.Handle), null);
					break;
				case RetiredKind.Semaphore:
					Vk->vkDestroySemaphore.Invoke(Handle, new VkSemaphore(item.Handle), null);
					break;
				case RetiredKind.AccelerationStructure:
					Vk->vkDestroyAccelerationStructureKHR.Invoke(Handle, new VkAccelerationStructureKHR(item.Handle), null);
					break;
			}
		}

		#endregion

		#region Stand-ins

		/// <summary>
		///     A 1 x 1 texture of zeros (opaque black where it has an alpha) of a kind and a component type, for a
		///     sampler or an image nothing was set for. An image's stand-in has the format the shader declares
		///     (<paramref name="imageFormat" />: SPIR-V's number of it, 0 for none).
		/// </summary>
		internal VulkanTexture DummyTexture(TextureKind kind, SpirvBaseType type, bool depth, int imageFormat = 0)
		{
			bool integer = type == SpirvBaseType.UInt || type == SpirvBaseType.Int;
			// VK_FORMAT_D32_SFLOAT, R8G8B8A8_UNORM, R32G32B32A32_UINT / _SINT.
			int raw = depth ? 126 : type == SpirvBaseType.UInt ? 107 : type == SpirvBaseType.Int ? 108 : 37;
			if (!depth && imageFormat > 0 && imageFormat < imageFormats.Length)
			{
				raw = imageFormats[imageFormat];
			}

			int key = (int)kind | raw << 4;
			lock (dummyTextures)
			{
				if (!dummyTextures.TryGetValue(key, out VulkanTexture texture))
				{
					TextureFormat format = depth ? TextureFormat.Depth32F : integer ? TextureFormat.RGBA32UI : TextureFormat.RGBA8;
					VkFormatFeatureFlags features = FormatFeatures((VkFormat)raw);
					TextureUsage usage = (features & VkFormatFeatureFlags.StorageImageBit) != 0 ? TextureUsage.Sampled | TextureUsage.Storage : TextureUsage.Sampled;
					texture = new VulkanTexture(this, new TextureDescriptor { Kind = kind, Format = format, Width = 1, Height = 1, Depth = 1, MipLevels = 1, Usage = usage, Name = "Unset " + kind },
						false, raw);
					CommandList.ClearDummy(texture);
					dummyTextures.Add(key, texture);
				}

				return texture;
			}
		}

		/// <summary>Vulkan's formats by SPIR-V's image formats (Rgba32f = 1 .. R8ui = 39).</summary>
		private static readonly int[] imageFormats =
		{
			0, 109, 97, 100, 37, 38, 103, 83, 122, 76, 91, 64, 77, 16, 70, 9, 92, 78, 17, 71, 10, 108, 96, 42, 99, 102, 82, 21, 75, 14, 107, 95, 41, 98, 68, 101, 81, 20, 74, 13
		};

		/// <summary>A buffer of zeros of at least a size, for a uniform or storage block nothing was set for.</summary>
		internal VulkanBuffer DummyBuffer(long size)
		{
			if (dummyBuffer == null || dummyBuffer.Size < size)
			{
				dummyBuffer?.Dispose();
				long capacity = Math.Max(65536, size);
				dummyBuffer = new VulkanBuffer(this, new BufferDescriptor(BufferUsage.Uniform | BufferUsage.Storage | BufferUsage.Indirect, 0, true, "Unset block"));
				byte[] zeros = new byte[capacity];
				dummyBuffer.SetData<byte>(zeros);
			}

			return dummyBuffer;
		}

		/// <summary>What a vertex attribute without a buffer reads: (0, 0, 0, 1) as floats, then as integers.</summary>
		internal VulkanBuffer AttributeDefaults
		{
			get
			{
				if (attributeDefaults == null)
				{
					attributeDefaults = new VulkanBuffer(this, new BufferDescriptor(BufferUsage.Vertex, 0, true, "Unset vertex attributes"));
					uint* values = stackalloc uint[8] { 0, 0, 0, 0x3F800000, 0, 0, 0, 1 };
					attributeDefaults.SetData(values, 32);
				}

				return attributeDefaults;
			}
		}

		#endregion

		#region Starting up

		internal static void Check(VkResult result, string what)
		{
			if (result != VkResult.Success)
			{
				throw new InvalidOperationException("Vulkan: " + what + " failed with " + result + ".");
			}
		}

		/// <summary>Throws when this machine has no Vulkan loader, or one older than Vulkan 1.1 (the queries of devices the device makes need 1.1).</summary>
		private static void RequireLoader()
		{
			try
			{
				// The bindings load vulkan-1 when their first static member is read.
				if (Brutal.VulkanApi.Vulkan.GetInstanceProcAddr.Invoke == null)
				{
					throw new NotSupportedException("The Vulkan loader of this machine has no vkGetInstanceProcAddr.");
				}
			}
			catch (TypeInitializationException e)
			{
				throw new NotSupportedException("This machine has no Vulkan loader (vulkan-1): " + (e.InnerException ?? e).Message, e);
			}

			int version;
			try
			{
				version = 0;
				Check(Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceVersion(&version), "vkEnumerateInstanceVersion");
			}
			catch (EntryPointNotFoundException)
			{
				// A loader of Vulkan 1.0 has no vkEnumerateInstanceVersion.
				version = 1 << 22;
			}

			if (version >> 22 < 1 || (version >> 22 == 1 && (version >> 12 & 0x3FF) < 1))
			{
				throw new NotSupportedException($"The Vulkan loader of this machine is of Vulkan {version >> 22}.{version >> 12 & 0x3FF}; the engine needs 1.3 (a newer GPU driver brings one).");
			}
		}

		private static bool HasLayer(string name)
		{
			int count = 0;
			Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceLayerProperties(&count, null);
			if (count == 0)
			{
				return false;
			}

			VkLayerProperties[] layers = new VkLayerProperties[count];
			fixed (VkLayerProperties* pointer = layers)
			{
				Brutal.VulkanApi.Vulkan.PInvoke.vkEnumerateInstanceLayerProperties(&count, pointer);
				for (int i = 0; i < count; i++)
				{
					if (Marshal.PtrToStringUTF8((IntPtr)pointer[i].LayerName) == name)
					{
						return true;
					}
				}
			}

			return false;
		}

		[UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
		private static int OnDebugMessage(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
		{
			string kind = (severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0 ? "error" : "warning";
			Console.WriteLine("[Vulkan validation " + kind + "] " + Marshal.PtrToStringUTF8((IntPtr)data->Message));
			return 0;
		}

		/// <summary>How well a device suits (-1: not at all, with the reason added to <paramref name="why" />).</summary>
		private int Rate(PhysicalDevice candidate, VkSurfaceKHR surface, out int family, ref string why)
		{
			family = -1;
			// Vulkan 1.0's query first: the queries of later versions are asked of devices that have them.
			VkPhysicalDeviceProperties properties;
			InstanceVk->vkGetPhysicalDeviceProperties.Invoke(candidate.Handle, &properties);
			string name = Marshal.PtrToStringUTF8((IntPtr)properties.DeviceName);
			int version = properties.ApiVersion;
			if (version >> 22 < 1 || (version >> 22 == 1 && (version >> 12 & 0x3FF) < 3))
			{
				why += $"\n  {name}: Vulkan {version >> 22}.{version >> 12 & 0x3FF}, 1.3 is needed.";
				return -1;
			}

			if (!candidate.SupportsExtension("VK_KHR_swapchain"))
			{
				why += $"\n  {name}: no VK_KHR_swapchain.";
				return -1;
			}

			if (!candidate.SupportsExtension("VK_EXT_depth_clip_control"))
			{
				why += $"\n  {name}: no VK_EXT_depth_clip_control (the engine's clip space has depth from -1 to 1).";
				return -1;
			}

			VkPhysicalDeviceDepthClipControlFeaturesEXT clip = new VkPhysicalDeviceDepthClipControlFeaturesEXT();
			VkPhysicalDeviceVulkan13Features features13 = new VkPhysicalDeviceVulkan13Features { Next = &clip };
			VkPhysicalDeviceVulkan12Features features12 = new VkPhysicalDeviceVulkan12Features { Next = &features13 };
			VkPhysicalDeviceVulkan11Features features11 = new VkPhysicalDeviceVulkan11Features { Next = &features12 };
			VkPhysicalDeviceFeatures2 features = new VkPhysicalDeviceFeatures2 { Next = &features11 };
			InstanceVk->vkGetPhysicalDeviceFeatures2.Invoke(candidate.Handle, &features);
			if (!clip.DepthClipControl || !features13.DynamicRendering || !features13.Synchronization2 || !features12.HostQueryReset || !features11.ShaderDrawParameters)
			{
				why += $"\n  {name}: lacks one of depth clip control, dynamic rendering, synchronization2, host query reset, shader draw parameters.";
				return -1;
			}

			int count = 0;
			InstanceVk->vkGetPhysicalDeviceQueueFamilyProperties.Invoke(candidate.Handle, &count, null);
			VkQueueFamilyProperties* families = stackalloc VkQueueFamilyProperties[count];
			InstanceVk->vkGetPhysicalDeviceQueueFamilyProperties.Invoke(candidate.Handle, &count, families);
			for (int i = 0; i < count; i++)
			{
				Brutal.Bool32 presents = false;
				InstanceVk->vkGetPhysicalDeviceSurfaceSupportKHR.Invoke(candidate.Handle, i, surface, &presents);
				VkQueueFlags needed = VkQueueFlags.GraphicsBit | VkQueueFlags.ComputeBit;
				if ((families[i].QueueFlags & needed) == needed && presents)
				{
					family = i;
					break;
				}
			}

			if (family < 0)
			{
				why += $"\n  {name}: no queue that draws, computes and presents to the window.";
				return -1;
			}

			string asked = Environment.GetEnvironmentVariable("LEL_VULKAN_DEVICE");
			if (!string.IsNullOrEmpty(asked) && name.Contains(asked, StringComparison.OrdinalIgnoreCase))
			{
				return 100;
			}

			switch (properties.DeviceType)
			{
				case VkPhysicalDeviceType.DiscreteGpu: return 4;
				case VkPhysicalDeviceType.IntegratedGpu: return 3;
				case VkPhysicalDeviceType.VirtualGpu: return 2;
				default: return 1;
			}
		}

		private void FillCapabilities(in VkPhysicalDeviceProperties properties, in VkPhysicalDeviceVulkan11Properties properties11, in VkPhysicalDeviceFeatures features)
		{
			DeviceCapabilities caps = Capabilities;
			int version = properties.ApiVersion;
			int driver = properties.DriverVersion;
			string name;
			fixed (byte* pointer = properties.DeviceName)
			{
				name = Marshal.PtrToStringUTF8((IntPtr)pointer);
			}

			StringBuilder uuid = new StringBuilder();
			for (int i = 0; i < 16; i++)
			{
				uuid.Append(properties.PipelineCacheUUID[i].ToString("x2"));
			}

			caps.Api = GraphicsApi.Vulkan;
			caps.ApiVersion = $"Vulkan {version >> 22 & 0x7F}.{version >> 12 & 0x3FF}.{version & 0xFFF}";
			switch (properties.VendorID)
			{
				case 0x1002: caps.Vendor = "AMD"; break;
				case 0x10DE: caps.Vendor = "NVIDIA"; break;
				case 0x8086: caps.Vendor = "Intel"; break;
				case 0x13B5: caps.Vendor = "ARM"; break;
				case 0x5143: caps.Vendor = "Qualcomm"; break;
				case 0x106B: caps.Vendor = "Apple"; break;
				default: caps.Vendor = "0x" + properties.VendorID.ToString("X4"); break;
			}

			caps.Renderer = name ?? "";
			caps.DriverVersion = properties.VendorID == 0x10DE
				? $"{driver >> 22 & 0x3FF}.{driver >> 14 & 0xFF}.{driver >> 6 & 0xFF}"
				: $"{driver >> 22 & 0x3FF}.{driver >> 12 & 0x3FF}.{driver & 0xFFF}";
			caps.DriverIdentity = caps.Vendor + "\n" + caps.Renderer + "\n" + caps.DriverVersion + "\n" + caps.ApiVersion + "\n" + uuid + "\nSPIR-V programs " + VulkanProgram.BinaryVersion;
			caps.MaxAnisotropy = features.SamplerAnisotropy ? properties.Limits.MaxSamplerAnisotropy : 1f;
			caps.MaxTextureSize = properties.Limits.MaxImageDimension2D;
			caps.Max3DTextureSize = properties.Limits.MaxImageDimension3D;
			caps.MaxSampledTextures = (int)Math.Min((uint)properties.Limits.MaxPerStageDescriptorSampledImages, 1024u);
			caps.MaxImages = (int)Math.Min((uint)properties.Limits.MaxPerStageDescriptorStorageImages, (uint)VulkanCommandList.MaxImageBindings);
			int storage = (int)Math.Min((uint)properties.Limits.MaxPerStageDescriptorStorageBuffers, (uint)VulkanCommandList.MaxBufferBindings);
			caps.MaxStorageBufferBindings = storage;
			caps.VertexStorageBlocks = features.VertexPipelineStoresAndAtomics ? storage : 0;
			caps.FragmentStorageBlocks = features.FragmentStoresAndAtomics ? storage : 0;
			int samples = (int)properties.Limits.FramebufferNoAttachmentsSampleCounts & 0x7F;
			caps.MaxPassSamples = 1;
			while ((samples & caps.MaxPassSamples * 2) != 0)
			{
				caps.MaxPassSamples *= 2;
			}

			caps.UniformBufferOffsetAlignment = (int)Math.Max(1UL, properties.Limits.MinUniformBufferOffsetAlignment.Value);
			caps.DrawParameters = true;
			caps.DrawParametersExtension = false;
			caps.IndirectCount = HasIndirectCount;
			VkSubgroupFeatureFlags operations = VkSubgroupFeatureFlags.BasicBit | VkSubgroupFeatureFlags.ArithmeticBit;
			caps.ComputeSubgroups = (properties11.SubgroupSupportedStages & VkShaderStageFlags.ComputeBit) != 0 && (properties11.SubgroupSupportedOperations & operations) == operations;
			caps.ParallelShaderCompile = true;
			caps.ProgramBinaries = true;
			caps.RayQuery = HasRayQuery;
			caps.RayTracingPositionFetch = HasRayQuery && HasPositionFetch;
			caps.ConservativeRasterization = HasConservativeRasterization;
			caps.CompilesBlockEveryThread = false;
			caps.AsyncCompute = computeStream != null;
			// Measured on the Radeon 780M (driver 2.0.353): colour attachments are never compressed, a clear writes every texel.
			caps.FastColorClears = !(properties.VendorID == 0x1002 && properties.DeviceType == VkPhysicalDeviceType.IntegratedGpu);
			caps.DiscreteGpu = properties.DeviceType == VkPhysicalDeviceType.DiscreteGpu;
		}

		#endregion

		private sealed class Submission
		{
			public long Serial;
			public VkFence Fence;
			public VkSemaphore WaitSemaphore;

			/// <summary>The graphics queue's command buffers.</summary>
			public readonly List<VkCommandBuffer> Commands = new List<VkCommandBuffer>();

			/// <summary>The compute queue's command buffers recorded with it, and the value of the compute queue's timeline its last one signals (0: none).</summary>
			public readonly List<VkCommandBuffer> ComputeCommands = new List<VkCommandBuffer>();

			public long ComputeValue;

			/// <summary>
			///     LEL_VULKAN_QUEUE_SEMAPHORES=binary: the binary semaphores its submissions waited for (free again once it has
			///     run), and the fences of the compute queue's submissions.
			/// </summary>
			public readonly List<VkSemaphore> BinaryWaited = new List<VkSemaphore>();

			public readonly List<VkFence> ComputeFences = new List<VkFence>();
			public readonly List<Chunk> Chunks = new List<Chunk>();
			public readonly List<VkDescriptorPool> Pools = new List<VkDescriptorPool>();
			public readonly List<int> Queries = new List<int>();
		}

		private sealed class Chunk
		{
			public VkBuffer Buffer;
			public VmaAllocation Allocation;
			public byte* Mapped;
			public long Size;
			public long Used;

			/// <summary>Submissions that use it: it is free once the last of them has finished.</summary>
			public int Users;
		}

		private struct Retired
		{
			public RetiredKind Kind;
			public nint Handle;
			public nint Allocation;
			public long Serial;
			public VulkanBuffer Owner;
		}
	}

	internal enum RetiredKind : byte
	{
		Buffer,
		Image,
		ImageView,
		Pipeline,
		ShaderModule,
		SetLayout,
		PipelineLayout,
		Swapchain,
		Semaphore,
		AccelerationStructure
	}

	/// <summary>Vulkan's format behind a texel format, and what the device does with it.</summary>
	internal struct FormatInfo
	{
		/// <summary>The image's format: what attachments, images, copies, blits and clears read and write.</summary>
		public VkFormat Format;

		/// <summary>The format samplers read the texture through (another one than the image's for sRGB textures, which decode).</summary>
		public VkFormat SampledFormat;

		public VkFormatFeatureFlags Features;

		/// <summary>Depth stored as 24 bit integers: texels are converted from and to the 32 bit floats the engine gives and reads.</summary>
		public bool PackedDepth;
	}

	/// <summary>Everything a descriptor set binds, as numbers: the key of a written set.</summary>
	internal readonly struct DescriptorKey : IEquatable<DescriptorKey>
	{
		private readonly ulong[] values;
		private readonly int count;
		private readonly int hash;

		public DescriptorKey(ulong[] values, int count)
		{
			this.values = values;
			this.count = count;
			ulong h = 14695981039346656037UL;
			for (int i = 0; i < count; i++)
			{
				h = (h ^ values[i]) * 1099511628211UL;
			}

			hash = (int)(h ^ h >> 32);
		}

		/// <summary>The key with numbers of its own (the given one's are a buffer written again for the next key).</summary>
		public DescriptorKey Copy()
		{
			ulong[] copy = new ulong[count];
			Array.Copy(values, copy, count);
			return new DescriptorKey(copy, count);
		}

		public bool Equals(DescriptorKey other)
		{
			return hash == other.hash && count == other.count && new ReadOnlySpan<ulong>(values, 0, count).SequenceEqual(new ReadOnlySpan<ulong>(other.values, 0, count));
		}

		public override bool Equals(object obj)
		{
			return obj is DescriptorKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return hash;
		}
	}
}
