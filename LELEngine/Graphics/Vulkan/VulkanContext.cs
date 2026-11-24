using System;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using LELEngine.Windowing;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A window on Vulkan: its surface and swapchain. The window's back buffer is a texture of the device like any
	///     other (first row lowest, as everywhere in the engine); presenting copies it into the swapchain's image
	///     turned upside down, which is where the engine's row order meets the screen's.
	///     <para>
	///         Making the context current gives the calling thread the right to give the device commands; there is
	///         one stream of commands whichever window is current.
	///     </para>
	/// </summary>
	internal sealed unsafe class VulkanContext : GraphicsContext
	{
		private readonly VulkanDevice device;
		private readonly NativeWindow nativeWindow;
		private readonly VkSurfaceKHR surface;
		private VkSwapchainKHR swapchain;
		private VkImage[] swapImages = Array.Empty<VkImage>();
		private VkSemaphore[] presentSemaphores = Array.Empty<VkSemaphore>();
		private int swapWidth;
		private int swapHeight;
		private bool swapStale = true;
		private VulkanTexture backbuffer;
		private int swapInterval;
		private int width;
		private int height;

		private bool disposed;

		internal VulkanContext(VulkanDevice device, NativeWindow window, VkSurfaceKHR surface)
			: base(window.Handle)
		{
			this.device = device;
			this.surface = surface;
			nativeWindow = window;
			window.FramebufferResize += OnResize;
			Glfw.PInvoke.GetFramebufferSize(window.Handle, out width, out height);
			device.Register(this, true);
		}

		/// <summary>Makes the swapchain for the window's size now (a window without a size gets it when it has one); throws when the surface cannot be presented to.</summary>
		internal void Prepare()
		{
			if (width > 0 && height > 0)
			{
				CreateSwapchain(width, height);
			}
		}

		public override GpuTexture Backbuffer
		{
			get
			{
				int w = Math.Max(1, width);
				int h = Math.Max(1, height);
				if (backbuffer == null || backbuffer.Width != w || backbuffer.Height != h)
				{
					backbuffer?.Dispose();
					backbuffer = new VulkanTexture(device, TextureDescriptor.Texture2D(TextureFormat.RGBA8, w, h, 1, TextureUsage.Attachment | TextureUsage.Sampled, "Backbuffer"), true);
				}

				return backbuffer;
			}
		}

		public override bool IsCurrent => VulkanDevice.ContextOfThread == this;

		public override int SwapInterval
		{
			get => swapInterval;
			set
			{
				if (swapInterval != value)
				{
					swapInterval = value;
					swapStale = true;
				}
			}
		}

		public override void MakeCurrent()
		{
			VulkanDevice.SetCurrent(this);
			SetCurrent(this);
		}

		public override void MakeNoneCurrent()
		{
			VulkanDevice.SetCurrent(null);
			SetCurrent(null);
		}

		public override void SwapBuffers()
		{
			VulkanCommandList list = device.CommandList;
			list.EndRenderPass();
			Glfw.PInvoke.GetFramebufferSize(Window, out int w, out int h);
			if (w <= 0 || h <= 0 || !Acquire(w, h, out int index, out VkSemaphore acquired))
			{
				// Nothing to show the frame in (a minimized window): its commands run all the same.
				device.Submit();
				return;
			}

			VulkanTexture source = backbuffer != null && !backbuffer.IsDisposed ? backbuffer : null;
			VkCommandBuffer commands = list.PresentCommands(source);
			VkImage image = swapImages[index];
			Transition(commands, image, VkImageLayout.Undefined, VkImageLayout.TransferDstOptimal);
			if (source != null)
			{
				VkImageBlit blit = default;
				blit.SrcSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
				blit.DstSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
				VkOffset3D* from = (VkOffset3D*)&blit.SrcOffsets;
				VkOffset3D* to = (VkOffset3D*)&blit.DstOffsets;
				// The back buffer's first row is the lowest, the screen's the highest.
				from[0] = new VkOffset3D { X = 0, Y = source.Height, Z = 0 };
				from[1] = new VkOffset3D { X = source.Width, Y = 0, Z = 1 };
				to[1] = new VkOffset3D { X = swapWidth, Y = swapHeight, Z = 1 };
				device.Vk->vkCmdBlitImage.Invoke(commands, source.Image, source.CopySourceLayout, image, VkImageLayout.TransferDstOptimal, 1, &blit, VkFilter.Nearest);
				list.AfterPresent(source);
			}

			Transition(commands, image, VkImageLayout.TransferDstOptimal, VkImageLayout.PresentSrcKHR);
			VkSemaphore rendered = presentSemaphores[index];
			// Only the blit into the window's image waits for the image: the frame's other commands need not.
			device.Submit(acquired, rendered, Sync.Transfer);
			device.CountFrame();
			device.SavePipelineCacheWhenQuiet();
			VkSwapchainKHR chain = swapchain;
			VkPresentInfoKHR present = new VkPresentInfoKHR { WaitSemaphoreCount = 1, WaitSemaphores = &rendered, SwapchainCount = 1, Swapchains = &chain, ImageIndices = &index };
			VkResult result = device.Vk->vkQueuePresentKHR.Invoke(device.Queue, &present);
			if (result == VkResult.ErrorOutOfDateKHR || result == VkResult.SuboptimalKHR)
			{
				swapStale = true;
			}
			else
			{
				VulkanDevice.Check(result, "vkQueuePresentKHR");
			}

			// The CPU records at most one frame ahead of the GPU: the frame before this one has to have run (counted by
			// frames, whatever the number of submissions a frame takes: flushes, the queues' waits for each other).
			long presented = device.RecordingSerial - 1;
			if (previousPresent > 0)
			{
				device.WaitFor(previousPresent, long.MaxValue);
			}

			previousPresent = presented;
		}

		/// <summary>The submission that presented the frame before (0: none yet).</summary>
		private long previousPresent;

		public override void Dispose()
		{
			if (disposed)
			{
				return;
			}

			disposed = true;
			if (nativeWindow != null)
			{
				nativeWindow.FramebufferResize -= OnResize;
			}

			if (VulkanDevice.ContextOfThread == this)
			{
				MakeNoneCurrent();
			}

			if (device.IsDestroyed)
			{
				// The device took the swapchain and the surface down with it.
				return;
			}

			device.Finish();
			// The presentation engine lets go of the semaphores of the last presents (the queue's operations).
			device.Vk->vkQueueWaitIdle.Invoke(device.Queue);
			device.SavePipelineCache();
			backbuffer?.Dispose();
			backbuffer = null;
			DestroySwapchain(true);
			device.InstanceVk->vkDestroySurfaceKHR.Invoke(device.InstanceHandle, surface, null);
			device.Register(this, false);
		}

		/// <summary>
		///     The device is being given up and the GPU is idle: the swapchain, its semaphores and the surface are
		///     destroyed at once, the back buffer handed to the device's last clean-up.
		/// </summary>
		internal void Abandon()
		{
			disposed = true;
			if (nativeWindow != null)
			{
				nativeWindow.FramebufferResize -= OnResize;
			}

			backbuffer?.Dispose();
			backbuffer = null;
			foreach (VkSemaphore semaphore in presentSemaphores)
			{
				device.Vk->vkDestroySemaphore.Invoke(device.Handle, semaphore, null);
			}

			presentSemaphores = Array.Empty<VkSemaphore>();
			if (!swapchain.IsNull())
			{
				device.Vk->vkDestroySwapchainKHR.Invoke(device.Handle, swapchain, null);
				swapchain = default;
			}

			device.InstanceVk->vkDestroySurfaceKHR.Invoke(device.InstanceHandle, surface, null);
			if (VulkanDevice.ContextOfThread == this)
			{
				MakeNoneCurrent();
			}
		}

		private void OnResize(FramebufferResizeEventArgs e)
		{
			width = e.Width;
			height = e.Height;
		}

		/// <summary>The next image of the swapchain (made anew when the window's size or the swap interval changed); false when there is none to get.</summary>
		private bool Acquire(int w, int h, out int index, out VkSemaphore acquired)
		{
			index = 0;
			acquired = default;
			for (int attempt = 0; attempt < 2; attempt++)
			{
				if (swapchain.IsNull() || swapStale || w != swapWidth || h != swapHeight)
				{
					if (!CreateSwapchain(w, h))
					{
						return false;
					}
				}

				VkSemaphore semaphore = device.TakeSemaphore();
				int image;
				VkResult result = device.Vk->vkAcquireNextImageKHR.Invoke(device.Handle, swapchain, -1, semaphore, default, &image);
				if (result == VkResult.Success || result == VkResult.SuboptimalKHR)
				{
					swapStale |= result == VkResult.SuboptimalKHR;
					index = image;
					acquired = semaphore;
					return true;
				}

				device.ReturnSemaphore(semaphore);
				if (result != VkResult.ErrorOutOfDateKHR)
				{
					VulkanDevice.Check(result, "vkAcquireNextImageKHR");
				}

				swapStale = true;
			}

			return false;
		}

		private bool CreateSwapchain(int w, int h)
		{
			VkSurfaceCapabilitiesKHR capabilities;
			VulkanDevice.Check(device.InstanceVk->vkGetPhysicalDeviceSurfaceCapabilitiesKHR.Invoke(device.PhysicalHandle, surface, &capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
			VkExtent2D extent = capabilities.CurrentExtent;
			if (extent.Width == -1)
			{
				extent.Width = Math.Clamp(w, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width);
				extent.Height = Math.Clamp(h, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);
			}

			if (extent.Width <= 0 || extent.Height <= 0)
			{
				return false;
			}

			int formatCount = 0;
			device.InstanceVk->vkGetPhysicalDeviceSurfaceFormatsKHR.Invoke(device.PhysicalHandle, surface, &formatCount, null);
			VkSurfaceFormatKHR* formats = stackalloc VkSurfaceFormatKHR[Math.Max(1, formatCount)];
			device.InstanceVk->vkGetPhysicalDeviceSurfaceFormatsKHR.Invoke(device.PhysicalHandle, surface, &formatCount, formats);
			if (formatCount == 0)
			{
				throw new NotSupportedException("The window's Vulkan surface offers no format to present in.");
			}

			// The back buffer's bytes go to the screen as they are: an 8 bit format without sRGB encoding of its own
			// (VK_FORMAT_B8G8R8A8_UNORM 44, VK_FORMAT_R8G8B8A8_UNORM 37).
			VkSurfaceFormatKHR chosen = formats[0];
			for (int i = formatCount - 1; i >= 0; i--)
			{
				if ((int)formats[i].Format == 44 || (int)formats[i].Format == 37)
				{
					chosen = formats[i];
				}
			}

			int modeCount = 0;
			device.InstanceVk->vkGetPhysicalDeviceSurfacePresentModesKHR.Invoke(device.PhysicalHandle, surface, &modeCount, null);
			VkPresentModeKHR* modes = stackalloc VkPresentModeKHR[Math.Max(1, modeCount)];
			device.InstanceVk->vkGetPhysicalDeviceSurfacePresentModesKHR.Invoke(device.PhysicalHandle, surface, &modeCount, modes);
			VkPresentModeKHR mode = VkPresentModeKHR.FifoKHR;
			if (swapInterval <= 0)
			{
				// Without waiting for the display: at once, else the newest finished frame at the next refresh.
				bool immediate = false;
				bool mailbox = false;
				for (int i = 0; i < modeCount; i++)
				{
					immediate |= modes[i] == VkPresentModeKHR.ImmediateKHR;
					mailbox |= modes[i] == VkPresentModeKHR.MailboxKHR;
				}

				mode = immediate ? VkPresentModeKHR.ImmediateKHR : mailbox ? VkPresentModeKHR.MailboxKHR : VkPresentModeKHR.FifoKHR;
			}

			int imageCount = Math.Max(capabilities.MinImageCount + 1, 3);
			if (capabilities.MaxImageCount > 0)
			{
				imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
			}

			VkCompositeAlphaFlagsKHR alpha = (capabilities.SupportedCompositeAlpha & VkCompositeAlphaFlagsKHR.OpaqueBitKHR) != 0 ? VkCompositeAlphaFlagsKHR.OpaqueBitKHR : VkCompositeAlphaFlagsKHR.InheritBitKHR;
			VkSwapchainKHR old = swapchain;
			VkSwapchainCreateInfoKHR info = new VkSwapchainCreateInfoKHR
			{
				Surface = surface,
				MinImageCount = imageCount,
				ImageFormat = chosen.Format,
				ImageColorSpace = chosen.ColorSpace,
				ImageExtent = extent,
				ImageArrayLayers = 1,
				ImageUsage = VkImageUsageFlags.TransferDstBit | VkImageUsageFlags.ColorAttachmentBit,
				ImageSharingMode = VkSharingMode.Exclusive,
				PreTransform = capabilities.CurrentTransform,
				CompositeAlpha = alpha,
				PresentMode = mode,
				Clipped = true,
				OldSwapchain = old
			};
			VkSwapchainKHR made;
			VulkanDevice.Check(device.Vk->vkCreateSwapchainKHR.Invoke(device.Handle, &info, null, &made), "vkCreateSwapchainKHR");
			DestroySwapchain(false);
			swapchain = made;
			int count = 0;
			device.Vk->vkGetSwapchainImagesKHR.Invoke(device.Handle, swapchain, &count, null);
			swapImages = new VkImage[count];
			fixed (VkImage* pointer = swapImages)
			{
				device.Vk->vkGetSwapchainImagesKHR.Invoke(device.Handle, swapchain, &count, pointer);
			}

			presentSemaphores = new VkSemaphore[count];
			for (int i = 0; i < count; i++)
			{
				presentSemaphores[i] = device.CreateSemaphore();
			}

			swapWidth = extent.Width;
			swapHeight = extent.Height;
			swapStale = false;
			return true;
		}

		/// <summary>Gives up the swapchain and its semaphores: freed when the frames that present with them are done.</summary>
		private void DestroySwapchain(bool now)
		{
			long serial = now ? 0 : device.RecordingSerial;
			foreach (VkSemaphore semaphore in presentSemaphores)
			{
				device.Retire(RetiredKind.Semaphore, semaphore.VkHandle, 0, serial);
			}

			presentSemaphores = Array.Empty<VkSemaphore>();
			if (!swapchain.IsNull())
			{
				device.Retire(RetiredKind.Swapchain, swapchain.VkHandle, 0, serial);
				swapchain = default;
			}

			if (now)
			{
				device.Poll();
			}
		}

		private void Transition(VkCommandBuffer commands, VkImage image, VkImageLayout from, VkImageLayout to)
		{
			// From undefined: after the acquire, which the transfer stage waits for (the submission's semaphore).
			VkImageMemoryBarrier2 barrier = new VkImageMemoryBarrier2
			{
				SrcStageMask = from == VkImageLayout.Undefined ? Sync.Transfer : VkPipelineStageFlags2.AllCommandsBit,
				SrcAccessMask = from == VkImageLayout.Undefined ? VkAccessFlags2.None : VkAccessFlags2.MemoryWriteBit,
				DstStageMask = VkPipelineStageFlags2.AllCommandsBit,
				DstAccessMask = to == VkImageLayout.PresentSrcKHR ? VkAccessFlags2.None : VkAccessFlags2.MemoryWriteBit,
				OldLayout = from,
				NewLayout = to,
				SrcQueueFamilyIndex = -1,
				DstQueueFamilyIndex = -1,
				Image = image,
				SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 }
			};
			VkDependencyInfo dependency = new VkDependencyInfo { ImageMemoryBarrierCount = 1, ImageMemoryBarriers = &barrier };
			device.Vk->vkCmdPipelineBarrier2.Invoke(commands, &dependency);
		}
	}

	/// <summary>
	///     What a thread that only builds programs holds on Vulkan: nothing of the API's (programs are built without a
	///     context), only the mark that the thread may make them.
	/// </summary>
	internal sealed class VulkanWorkerContext : GraphicsContext
	{
		public VulkanWorkerContext()
			: base(null)
		{ }

		public override bool IsCurrent => VulkanDevice.ContextOfThread == this;
		public override int SwapInterval { get; set; }
		public override GpuTexture Backbuffer => null;

		public override void MakeCurrent()
		{
			VulkanDevice.SetCurrent(this);
			SetCurrent(this);
		}

		public override void MakeNoneCurrent()
		{
			VulkanDevice.SetCurrent(null);
			SetCurrent(null);
		}

		public override void SwapBuffers()
		{ }
	}
}
