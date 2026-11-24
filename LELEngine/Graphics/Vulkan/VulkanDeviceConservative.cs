using System;
using System.Collections.Generic;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     Conservative rasterization (VK_EXT_conservative_rasterization): pipelines whose render state asks for it
	///     (<see cref="RenderState.ConservativeRasterization" />) overestimate, so every pixel a triangle touches gets a
	///     fragment. The extension has no feature to enable, only properties.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>The device rasterizes conservatively where a render state asks (<see cref="DeviceCapabilities.ConservativeRasterization" />).</summary>
		internal bool HasConservativeRasterization { get; private set; }

		/// <summary>
		///     Asks for VK_EXT_conservative_rasterization when the GPU has it and it overestimates by no more than half a
		///     pixel (the extension allows a GPU any overestimation). LEL_VULKAN_DISABLE naming "conservative" makes a device
		///     without it on any GPU (the fallbacks' test).
		/// </summary>
		private void AskForConservativeRasterization(PhysicalDevice candidate, HashSet<string> extensions)
		{
			string disabled = Environment.GetEnvironmentVariable("LEL_VULKAN_DISABLE") ?? "";
			if (disabled.IndexOf("conservative", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				Console.WriteLine("[Vulkan] Disabled by LEL_VULKAN_DISABLE: conservative");
				return;
			}

			if (!candidate.SupportsExtension("VK_EXT_conservative_rasterization"))
			{
				return;
			}

			VkPhysicalDeviceConservativeRasterizationPropertiesEXT conservative = new VkPhysicalDeviceConservativeRasterizationPropertiesEXT();
			VkPhysicalDeviceProperties2 properties = new VkPhysicalDeviceProperties2 { Next = &conservative };
			InstanceVk->vkGetPhysicalDeviceProperties2.Invoke(candidate.Handle, &properties);
			if (conservative.PrimitiveOverestimationSize > 0.5f)
			{
				return;
			}

			extensions.Add("VK_EXT_conservative_rasterization");
			HasConservativeRasterization = true;
		}
	}
}
