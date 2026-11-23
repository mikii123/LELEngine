using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	internal sealed unsafe partial class VulkanProgram
	{
		/// <summary>The resources the descriptor set of the last draw or dispatch binds, with how the program uses them.</summary>
		internal TrackedResource[] Tracked = new TrackedResource[8];

		internal int TrackedCount;

		/// <summary>The device's <see cref="VulkanDevice.SyncEpoch" /> when the uses of the tracked resources were last noted (-1: not since they changed).</summary>
		internal int TrackedEpoch = -1;

		/// <summary>The pass (its serial) <see cref="SamplesPassDepth" /> was found for.</summary>
		internal long DepthCheckPass = -1;

		/// <summary>The program samples the depth attachment of the pass <see cref="DepthCheckPass" />: draws with it render that depth read-only.</summary>
		internal bool SamplesPassDepth;

		/// <summary>The layout each sampler's texture is read in, by unit: what the descriptor set says.</summary>
		private VkImageLayout[] textureLayouts = Array.Empty<VkImageLayout>();

		/// <summary>A resource a descriptor set of the program binds.</summary>
		internal struct TrackedResource
		{
			public VulkanTexture Texture;
			public VulkanBuffer Buffer;

			/// <summary>A top level the program traces (ray queries read it and its bottom levels).</summary>
			public VulkanAccelerationStructure Structure;

			public int Mip;
			public int Mips;
			public int Layer;
			public int Layers;
			public VkImageLayout Layout;
			public bool Write;

			/// <summary>Read by a sampler (through a view of levels 0 .. <see cref="Mips" /> - 1).</summary>
			public bool Sampled;
		}

		/// <summary>The resources of a new descriptor set are noted from here on (<see cref="Track" />).</summary>
		private void BeginTracking(VulkanCommandList list)
		{
			Array.Clear(Tracked, 0, TrackedCount);
			TrackedCount = 0;
			TrackedEpoch = -1;
			DepthCheckPass = -1;
			list.Resolving = this;
			device.NamedHandles.Clear();
			device.NamedHandles.Add(setLayout.VkHandle);
			if (textureLayouts.Length != Textures.Length)
			{
				textureLayouts = new VkImageLayout[Textures.Length];
			}
		}

		internal void Track(in TrackedResource resource)
		{
			if (TrackedCount == Tracked.Length)
			{
				Array.Resize(ref Tracked, Tracked.Length * 2);
			}

			Tracked[TrackedCount++] = resource;
		}

		/// <summary>A sampler's texture, read in a layout through levels 0 .. <paramref name="mips" /> - 1.</summary>
		internal void TrackTexture(int unit, VulkanTexture texture, int mips, VkImageLayout layout)
		{
			textureLayouts[unit] = layout;
			Track(new TrackedResource { Texture = texture, Mip = 0, Mips = mips, Layer = 0, Layers = texture.Layers, Layout = layout, Sampled = true });
		}

		/// <summary>Whether a stage writes the storage blocks of a binding number.</summary>
		internal bool WritesBuffer(int number)
		{
			foreach (Binding binding in bindings)
			{
				if (binding.Kind == BindingKind.StorageBuffer && binding.Number == number)
				{
					return binding.Writable;
				}
			}

			return false;
		}

		/// <summary>Whether a stage writes the image of a binding number.</summary>
		internal bool WritesImage(int number)
		{
			foreach (Binding binding in bindings)
			{
				if (binding.Kind == BindingKind.Image && number >= binding.Number && number < binding.Number + binding.Count)
				{
					return binding.Writable;
				}
			}

			return false;
		}

		/// <summary>Whether the descriptor set binds the texture for a sampler.</summary>
		internal bool Samples(VulkanTexture texture)
		{
			for (int i = 0; i < TrackedCount; i++)
			{
				if (Tracked[i].Texture == texture && Tracked[i].Sampled)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>The descriptor set of the last draw is not used again: the next one is looked up or written anew.</summary>
		internal void ForgetSet()
		{
			setEpoch = -1;
		}
	}
}
