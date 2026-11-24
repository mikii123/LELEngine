using System;
using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     Graphics pipeline libraries (VK_EXT_graphics_pipeline_library): a pipeline is linked from four parts made
	///     apart, so that a new combination of a program with a render state, a vertex layout or a pass's formats costs a
	///     link of parts that exist, not a compilation of its shaders. A program compiles its shaders' parts
	///     (pre-rasterization and fragment shader) when it is built, on the thread that builds it; the vertex input parts
	///     are a program's, the fragment output parts (formats, blending, samples) the device's, shared by every program.
	///     The first draw of a combination links the parts as they are (fast linking, which the device has to offer for
	///     them to be used), and a thread of the pool links them again optimized, which replaces the fast pipeline once
	///     it is made: the shaders run as a monolithic pipeline's would after that. Used by default where the driver is
	///     NVIDIA's (<see cref="AskForPipelineLibraries" />); LEL_VULKAN_ENABLE naming "pipelinelibraries" uses them on any
	///     GPU that has them, LEL_VULKAN_DISABLE naming it never (monolithic pipelines made at the first draw);
	///     LEL_VULKAN_PIPELINE_LIBRARIES=fast: the fast pipelines stay (to compare).
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>Pipelines are linked from libraries (this file's notes).</summary>
		internal bool HasPipelineLibraries { get; private set; }

		/// <summary>The fast pipelines are not replaced by optimized ones (LEL_VULKAN_PIPELINE_LIBRARIES=fast).</summary>
		internal static readonly bool FastPipelinesOnly = Environment.GetEnvironmentVariable("LEL_VULKAN_PIPELINE_LIBRARIES") == "fast";

		/// <summary>The fragment output parts made so far, by formats, blending and samples.</summary>
		private readonly Dictionary<OutputLibraryKey, VkPipeline> outputLibraries = new Dictionary<OutputLibraryKey, VkPipeline>();

		/// <summary>
		///     Asks for graphics pipeline libraries when the GPU has them with fast linking (without it a link may cost a
		///     compilation, and there is nothing to gain) and its driver is one they are used with by default (NVIDIA's:
		///     what DXVK relies on), or LEL_VULKAN_ENABLE names "pipelinelibraries". Not by default on AMD's driver
		///     (2.0.353, Radeon 780M): it crashed compiling the fragment part of an engine shader (GIMediaDebug) that it
		///     compiles whole without trouble; other drivers have not been tried. The feature goes into the chain of
		///     <paramref name="want" /> (<paramref name="feature" />, the caller's, holds it until the device is made).
		/// </summary>
		private void AskForPipelineLibraries(PhysicalDevice candidate, HashSet<string> extensions, VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT* feature, VkPhysicalDeviceFeatures2* want)
		{
			string disabled = Environment.GetEnvironmentVariable("LEL_VULKAN_DISABLE") ?? "";
			if (disabled.IndexOf("pipelinelibraries", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Console.WriteLine("[Vulkan] Disabled by LEL_VULKAN_DISABLE: pipelinelibraries");
				return;
			}

			if (!candidate.SupportsExtension("VK_EXT_graphics_pipeline_library") || !candidate.SupportsExtension("VK_KHR_pipeline_library"))
			{
				return;
			}

			VkPhysicalDeviceDriverProperties driver = new VkPhysicalDeviceDriverProperties();
			VkPhysicalDeviceProperties2 driverProperties = new VkPhysicalDeviceProperties2 { Next = &driver };
			InstanceVk->vkGetPhysicalDeviceProperties2.Invoke(candidate.Handle, &driverProperties);
			bool asked = (Environment.GetEnvironmentVariable("LEL_VULKAN_ENABLE") ?? "").IndexOf("pipelinelibraries", StringComparison.OrdinalIgnoreCase) >= 0;
			if (!asked && driver.DriverID != VkDriverId.NvidiaProprietaryKHR)
			{
				return;
			}

			VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT have = new VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT();
			VkPhysicalDeviceFeatures2 features = new VkPhysicalDeviceFeatures2 { Next = &have };
			InstanceVk->vkGetPhysicalDeviceFeatures2.Invoke(candidate.Handle, &features);
			VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT linking = new VkPhysicalDeviceGraphicsPipelineLibraryPropertiesEXT();
			VkPhysicalDeviceProperties2 properties = new VkPhysicalDeviceProperties2 { Next = &linking };
			InstanceVk->vkGetPhysicalDeviceProperties2.Invoke(candidate.Handle, &properties);
			if (!have.GraphicsPipelineLibrary || !linking.GraphicsPipelineLibraryFastLinking)
			{
				return;
			}

			*feature = new VkPhysicalDeviceGraphicsPipelineLibraryFeaturesEXT { GraphicsPipelineLibrary = true, Next = want->Next };
			want->Next = feature;
			extensions.Add("VK_KHR_pipeline_library");
			extensions.Add("VK_EXT_graphics_pipeline_library");
			HasPipelineLibraries = true;
		}

		/// <summary>The fragment output part of pipelines drawing into these formats with this blending and colour mask (made the first time; the drawing thread's).</summary>
		internal VkPipeline FragmentOutputLibrary(in PipelineKey key, in RenderState state, TextureFormat[] colorFormats, int colorCount, TextureFormat depthFormat)
		{
			OutputLibraryKey output = new OutputLibraryKey { State = key.State & ~(1UL << 32), Colors = key.Colors, ColorCount = key.ColorCount, DepthFormat = key.DepthFormat, Samples = key.Samples };
			if (outputLibraries.TryGetValue(output, out VkPipeline library))
			{
				return library;
			}

			VkPipelineColorBlendAttachmentState* blends = stackalloc VkPipelineColorBlendAttachmentState[Math.Max(1, colorCount)];
			VkFormat* formats = stackalloc VkFormat[Math.Max(1, colorCount)];
			for (int i = 0; i < colorCount; i++)
			{
				formats[i] = Format(colorFormats[i]).Format;
				blends[i] = VulkanProgram.BlendAttachment(this, colorFormats[i], state);
			}

			VkGraphicsPipelineLibraryCreateInfoEXT part = new VkGraphicsPipelineLibraryCreateInfoEXT { Flags = VkGraphicsPipelineLibraryFlagsEXT.FragmentOutputInterfaceBitEXT };
			VkPipelineRenderingCreateInfo rendering = new VkPipelineRenderingCreateInfo
			{
				Next = &part,
				ColorAttachmentCount = colorCount,
				ColorAttachmentFormats = formats,
				DepthAttachmentFormat = depthFormat != TextureFormat.None ? Format(depthFormat).Format : default
			};
			VkPipelineColorBlendStateCreateInfo colorBlend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = colorCount, Attachments = blends };
			VkPipelineMultisampleStateCreateInfo multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)Math.Max(1, (int)key.Samples) };
			VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
			{
				Next = &rendering,
				Flags = LibraryFlags,
				ColorBlendState = &colorBlend,
				MultisampleState = &multisample
			};
			Check(Vk->vkCreateGraphicsPipelines.Invoke(Handle, PipelineCache, 1, &info, null, &library), "vkCreateGraphicsPipelines (fragment output library)");
			outputLibraries.Add(output, library);
			return library;
		}

		/// <summary>
		///     A note every shader module of a device with pipeline libraries carries (<see cref="SpirvModule.AddProcessedNote" />),
		///     so that a driver's own cache of compiled shaders keeps them apart from those of a device without libraries.
		///     AMD's driver (2.0.353, Radeon 780M) keys its cache by the shader's words alone, and the compiled shaders of
		///     the two are not interchangeable: a compute pipeline compiled by one and found in the cache by the other wrote
		///     nothing or lost the device (a creation flag of the pipeline did not keep them apart).
		/// </summary>
		internal const string LibrariesNote = "LELEngine: a device with graphics pipeline libraries";

		/// <summary>The flags of a pipeline library: one, keeping what an optimized link needs.</summary>
		internal static VkPipelineCreateFlags LibraryFlags => VkPipelineCreateFlags.LibraryBitKHR | VkPipelineCreateFlags.RetainLinkTimeOptimizationInfoBitEXT;

		/// <summary>The device is being given up: the fragment output parts go.</summary>
		private void ReleasePipelineLibraries()
		{
			foreach (VkPipeline library in outputLibraries.Values)
			{
				Vk->vkDestroyPipeline.Invoke(Handle, library, null);
			}

			outputLibraries.Clear();
		}

		/// <summary>What a fragment output part is made for: the blending and colour mask, the formats, the samples.</summary>
		private struct OutputLibraryKey : IEquatable<OutputLibraryKey>
		{
			public ulong State;
			public ulong Colors;
			public byte ColorCount;
			public byte DepthFormat;
			public byte Samples;

			public bool Equals(OutputLibraryKey other)
			{
				return State == other.State && Colors == other.Colors && ColorCount == other.ColorCount && DepthFormat == other.DepthFormat && Samples == other.Samples;
			}

			public override bool Equals(object obj)
			{
				return obj is OutputLibraryKey other && Equals(other);
			}

			public override int GetHashCode()
			{
				return HashCode.Combine(State, Colors, ColorCount | DepthFormat << 8 | Samples << 16);
			}
		}
	}
}
