using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The command list's side of the device's ordering (<see cref="Sync" />): what each command uses is noted as
	///     requirements, and the barriers they need are recorded before the command. A pass begins rendering with its
	///     first draw or clear (or at its end when it only clears), so the barriers of the first draw come before the
	///     rendering; a later draw that needs one ends the rendering and begins it again, unless the stream has not used
	///     the resource in this submission yet: its barrier then goes ahead of the stream.
	/// </summary>
	internal sealed unsafe partial class VulkanCommandList
	{
		// The open pass's attachments: their levels and layers, and what they begin with.
		private readonly int[] passColorMips = new int[MaxColors];
		private readonly int[] passColorLayers = new int[MaxColors];
		private readonly LoadOperation[] passColorLoads = new LoadOperation[MaxColors];
		private readonly ClearValue[] passColorClears = new ClearValue[MaxColors];
		private int passDepthMip;
		private int passDepthLayer;
		private LoadOperation passDepthLoad;
		private float passDepthClear;

		/// <summary>Rendering has begun for the open pass: when it begins again it keeps what the attachments hold.</summary>
		private bool passStarted;

		/// <summary>The depth attachment renders read-only (draws sample it): it is tested, not written, and not stored.</summary>
		private bool depthReadOnly;

		/// <summary>Whether the command buffer was last told to write depth.</summary>
		private bool appliedDepthWrite;

		/// <summary>Numbers the passes: a texture remembers the last one that drew into it.</summary>
		private long passSerial;

		/// <summary>
		///     Per queue (<see cref="VulkanStream.Index" />): a command was recorded outside the tracking of uses
		///     (<see cref="BeginTransfer" />), and the next tracked one waits for everything.
		/// </summary>
		private readonly bool[] untrackedStreams = new bool[2];

		private ref bool untracked => ref untrackedStreams[device.Recording.Index];

		private Requirement[] requirements = new Requirement[32];
		private int requirementCount;

		/// <summary>The barriers before the next command of the stream.</summary>
		private readonly SyncBatch streamBatch = new SyncBatch();

		/// <summary>The barriers at the end of a submission (its timestamps' copies, the host's reads): apart from <see cref="streamBatch" />, which a submission in the middle of a command may leave filled.</summary>
		private readonly SyncBatch endBatch = new SyncBatch();

		// The vertex and index buffers the last draw looked at, and the epoch it looked in.
		private readonly VulkanBuffer[] checkedVertexBuffers = new VulkanBuffer[MaxVertexBuffers];
		private VulkanBuffer checkedIndexBuffer;
		private int checkedEpoch = -1;

		/// <summary>The program whose descriptor set is being written: the resources it binds are noted for its draws.</summary>
		internal VulkanProgram Resolving;

		#region Requirements

		/// <summary>A use of a resource by the next command.</summary>
		private struct Requirement
		{
			public VulkanTexture Texture;
			public VulkanBuffer Buffer;
			public SyncTarget Target;
			public int Mip;
			public int Mips;
			public int Layer;
			public int Layers;
			public VkImageLayout Layout;
			public VkPipelineStageFlags2 Stages;
			public bool Write;
			public bool Discard;

			/// <summary>No command of the stream had used the resource in this submission: its barrier can go ahead of the stream.</summary>
			public bool Ahead;
		}

		/// <summary>Notes a use of levels and layers of a texture by the next command (joined with its other uses of the same subresources).</summary>
		private void RequireTexture(VulkanTexture texture, int mip, int mips, int layer, int layers, VkImageLayout layout, VkPipelineStageFlags2 stages, bool write,
			bool discard = false)
		{
			for (int i = 0; i < requirementCount; i++)
			{
				ref Requirement other = ref requirements[i];
				if (other.Texture != texture || mip >= other.Mip + other.Mips || other.Mip >= mip + mips || layer >= other.Layer + other.Layers || other.Layer >= layer + layers)
				{
					continue;
				}

				// The uses of one command happen together: one use by all their stages, over both ranges.
				if (other.Layout != layout)
				{
					throw new InvalidOperationException($"Texture '{texture.Name}' would be used in two layouts at once ({other.Layout}, {layout}).");
				}

				int end = Math.Max(other.Mip + other.Mips, mip + mips);
				other.Mip = Math.Min(other.Mip, mip);
				other.Mips = end - other.Mip;
				end = Math.Max(other.Layer + other.Layers, layer + layers);
				other.Layer = Math.Min(other.Layer, layer);
				other.Layers = end - other.Layer;
				other.Stages |= stages;
				other.Write |= write;
				other.Discard &= discard;
				return;
			}

			ref Requirement added = ref Add();
			added.Texture = texture;
			added.Mip = mip;
			added.Mips = mips;
			added.Layer = layer;
			added.Layers = layers;
			added.Layout = layout;
			added.Stages = stages;
			added.Write = write;
			added.Discard = discard;
			added.Ahead = texture.StreamUnused;
			texture.Use();
		}

		/// <summary>Notes a use of a buffer by the next command (joined with its other uses of it).</summary>
		private void RequireBuffer(VulkanBuffer buffer, VkPipelineStageFlags2 stages, bool write)
		{
			for (int i = 0; i < requirementCount; i++)
			{
				ref Requirement other = ref requirements[i];
				if (other.Buffer == buffer)
				{
					other.Stages |= stages;
					other.Write |= write;
					return;
				}
			}

			ref Requirement added = ref Add();
			added.Buffer = buffer;
			added.Layout = VkImageLayout.Undefined;
			added.Stages = stages;
			added.Write = write;
			added.Ahead = buffer.MainUse != device.RecordingSerial;
			buffer.Use();
		}

		/// <summary>
		///     Notes a use of a resource of the device's own by the next command, joined with its other uses of it
		///     (<paramref name="join" />: reads of many structures skip the look, as a read noted twice is harmless).
		/// </summary>
		private void RequireTarget(SyncTarget target, VkPipelineStageFlags2 stages, bool write, bool join = true)
		{
			for (int i = 0; join && i < requirementCount; i++)
			{
				ref Requirement other = ref requirements[i];
				if (other.Target == target)
				{
					other.Stages |= stages;
					other.Write |= write;
					return;
				}
			}

			ref Requirement added = ref Add();
			added.Target = target;
			added.Layout = VkImageLayout.Undefined;
			added.Stages = stages;
			added.Write = write;
			added.Ahead = target.MainUse != device.RecordingSerial;
			if (!device.RecordingCompute)
			{
				target.MainUse = device.RecordingSerial;
			}
		}

		/// <summary>
		///     Records a use of a resource of the device's own into a batch: the barrier it needs, and the new state (on a
		///     device with two queues, after the other queue's conflicting uses, in the state of the queue being recorded for).
		/// </summary>
		private void ApplyTarget(SyncTarget target, SyncBatch batch, VkPipelineStageFlags2 stages, bool write)
		{
			VkAccessFlags2 extra = target.AccelerationStructure ? VkAccessFlags2.AccelerationStructureReadBitKHR : VkAccessFlags2.None;
			bool compute = false;
			if (device.HasAsyncCompute && !device.AsideRecording)
			{
				// The other queue's state keeps its own uses: its next use waits for this one by the timeline.
				device.OrderAcrossQueues(ref target.Queues, write, target.AccelerationStructure ? "an acceleration structure" : "the device's build memory or instances");
				compute = device.RecordingCompute;
			}

			if (Sync.Apply(ref compute ? ref target.ComputeState : ref target.State, batch, default, VkImageAspectFlags.None, 0, 0, 0, 0, VkImageLayout.Undefined, stages, write, false, extra))
			{
				device.SyncEpoch++;
			}
		}

		private ref Requirement Add()
		{
			if (requirementCount == requirements.Length)
			{
				Array.Resize(ref requirements, requirements.Length * 2);
			}

			ref Requirement added = ref requirements[requirementCount++];
			added = default;
			return ref added;
		}

		/// <summary>Whether a noted use has to wait for a command of the stream: a barrier in the stream before the next command.</summary>
		private bool RequirementsWait()
		{
			if (untracked)
			{
				return true;
			}

			for (int i = 0; i < requirementCount; i++)
			{
				ref Requirement r = ref requirements[i];
				if (r.Ahead)
				{
					continue;
				}

				bool needs = r.Texture != null ? r.Texture.Needs(r.Mip, r.Mips, r.Layer, r.Layers, r.Layout, r.Stages, r.Write)
					: r.Buffer != null ? r.Buffer.Needs(r.Stages, r.Write) : Sync.Needs(r.Target.State, VkImageLayout.Undefined, r.Stages, r.Write);
				if (needs)
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     Records the barriers the noted uses need and forgets the uses: in the stream before its next command, or
		///     (with <paramref name="ahead" />) for resources the stream had not used in this submission, ahead of the
		///     stream, which leaves an open rendering undisturbed.
		/// </summary>
		private void ApplyRequirements(bool ahead)
		{
			if (untracked)
			{
				streamBatch.AddMemory(VkPipelineStageFlags2.AllCommandsBit, VkAccessFlags2.MemoryWriteBit, VkPipelineStageFlags2.AllCommandsBit,
					VkAccessFlags2.MemoryReadBit | VkAccessFlags2.MemoryWriteBit);
				untracked = false;
			}

			SyncBatch setup = device.SetupBatch;
			if (requirementCount == 0 && streamBatch.IsEmpty && setup.IsEmpty)
			{
				return;
			}

			for (int i = 0; i < requirementCount; i++)
			{
				ref Requirement r = ref requirements[i];
				SyncBatch target = ahead && r.Ahead ? setup : streamBatch;
				if (r.Texture != null)
				{
					r.Texture.Require(target, r.Mip, r.Mips, r.Layer, r.Layers, r.Layout, r.Stages, r.Write, r.Discard);
				}
				else if (r.Buffer != null)
				{
					r.Buffer.Require(target, r.Stages, r.Write);
				}
				else
				{
					ApplyTarget(r.Target, target, r.Stages, r.Write);
				}
			}

			// The reads are ordered now, in the epoch the command's changes leave: the same reads again need no look
			// until something is written or changes layout.
			int epoch = device.SyncEpoch;
			for (int i = 0; i < requirementCount; i++)
			{
				ref Requirement r = ref requirements[i];
				if (!r.Write)
				{
					if (r.Texture != null)
					{
						r.Texture.MarkReady(epoch, r.Mip, r.Mips, r.Layer, r.Layers, r.Layout, r.Stages);
					}
					else if (r.Buffer != null)
					{
						r.Buffer.MarkReady(epoch, r.Stages);
					}
				}

				r = default;
			}

			requirementCount = 0;
			if (!setup.IsEmpty)
			{
				setup.Record(device, device.SetupCommands);
			}

			if (!streamBatch.IsEmpty)
			{
				RecordStreamBarriers(streamBatch);
			}
		}

		/// <summary>
		///     Records barriers into the stream where its commands go now: the device's own command buffer while one is
		///     open, else the recording thread's segment (barriers need nothing bound: no new segment for them).
		/// </summary>
		private void RecordStreamBarriers(SyncBatch batch)
		{
			VulkanStream stream = device.Recording;
			if (stream.MainBegun)
			{
				batch.Record(device, stream.Main);
			}
			else
			{
				batch.Record(device, stream.Recorder);
			}
		}

		#endregion

		#region Passes

		/// <summary>A pass is being begun: its rendering begins with its first draw or clear (or at its end when it only clears).</summary>
		private void OpenPass()
		{
			passSerial++;
			passStarted = false;
			depthReadOnly = false;
			device.SyncEpoch++;
		}

		/// <summary>
		///     Notes a colour attachment of the pass being begun, and what it begins with. The stream uses it from now on:
		///     what is uploaded into it after this is ordered after the pass's clear.
		/// </summary>
		private void NoteColorAttachment(int index, in ColorAttachment color)
		{
			passColorMips[index] = color.Mip;
			passColorLayers[index] = color.Layer;
			passColorLoads[index] = color.Load;
			passColorClears[index] = color.Clear;
			VulkanTexture texture = passColorTextures[index];
			texture.Use();
			if (texture.ColorPass != passSerial)
			{
				texture.ColorPass = passSerial;
				texture.ColorMip = color.Mip;
			}
			else
			{
				texture.ColorMip = Math.Min(texture.ColorMip, color.Mip);
			}
		}

		/// <summary>Notes the depth attachment of the pass being begun, and what it begins with (used by the stream from now on).</summary>
		private void NoteDepthAttachment(in DepthAttachment depth)
		{
			passDepthTexture.Use();
			passDepthMip = depth.Mip;
			passDepthLayer = depth.Layer;
			passDepthLoad = depth.Load;
			passDepthClear = depth.Clear;
		}

		/// <summary>Whether beginning the open pass's rendering clears an attachment.</summary>
		private bool PassClears()
		{
			for (int i = 0; i < passColorCount; i++)
			{
				if (passColorLoads[i] == LoadOperation.Clear)
				{
					return true;
				}
			}

			return passDepthTexture != null && passDepthLoad == LoadOperation.Clear;
		}

		/// <summary>Whether a clear's rectangle covers the whole pass.</summary>
		private bool CoversPass(int x, int y, int width, int height)
		{
			return width < 0 || height < 0 || (x <= 0 && y <= 0 && x + width >= passWidth && y + height >= passHeight);
		}

		/// <summary>Whether rendering into a level of a texture covers its subresource whole (every slice of a 3D texture's level).</summary>
		private bool OverwritesLevel(VulkanTexture texture, int mip)
		{
			return texture.MipWidth(mip) == passWidth && texture.MipHeight(mip) == passHeight && (texture.Kind != TextureKind.Texture3D || texture.MipDepth(mip) == 1);
		}

		/// <summary>Whether the open pass draws into the texture.</summary>
		private bool IsPassAttachment(VulkanTexture texture)
		{
			return passOpen && (texture.ColorPass == passSerial || texture == passDepthTexture);
		}

		/// <summary>
		///     Begins rendering into the open pass's attachments, after the barriers they and the uses noted for the next
		///     draw need: with the pass's own load operations the first time, keeping what the attachments hold after that.
		/// </summary>
		private void StartRendering()
		{
			bool first = !passStarted;
			for (int i = 0; i < passColorCount; i++)
			{
				VulkanTexture texture = passColorTextures[i];
				int mip = passColorMips[i];
				bool discard = first && passColorLoads[i] != LoadOperation.Load && OverwritesLevel(texture, mip);
				RequireTexture(texture, mip, 1, texture.SubresourceLayer(passColorLayers[i]), 1, texture.AttachmentLayout, Sync.ColorOutput, true, discard);
			}

			VulkanTexture depthTexture = passDepthTexture;
			if (depthTexture != null)
			{
				bool discard = first && passDepthLoad != LoadOperation.Load && OverwritesLevel(depthTexture, passDepthMip);
				RequireTexture(depthTexture, passDepthMip, 1, depthTexture.SubresourceLayer(passDepthLayer), 1, depthReadOnly ? depthTexture.SampledLayout : depthTexture.AttachmentLayout,
					Sync.DepthTests, !depthReadOnly, discard);
			}

			ApplyRequirements(false);
			VkRenderingAttachmentInfo* colors = stackalloc VkRenderingAttachmentInfo[Math.Max(1, passColorCount)];
			for (int i = 0; i < passColorCount; i++)
			{
				VulkanTexture texture = passColorTextures[i];
				colors[i] = new VkRenderingAttachmentInfo
				{
					ImageView = texture.AttachmentView(passColorMips[i], passColorLayers[i]),
					ImageLayout = texture.AttachmentLayout,
					LoadOp = first ? Load(passColorLoads[i]) : VkAttachmentLoadOp.Load,
					StoreOp = VkAttachmentStoreOp.Store
				};
				if (first)
				{
					WriteClearValue(&colors[i].ClearValue, passColorClears[i], passColorFormats[i]);
				}
			}

			VkRenderingAttachmentInfo depth = new VkRenderingAttachmentInfo();
			if (depthTexture != null)
			{
				depth.ImageView = depthTexture.AttachmentView(passDepthMip, passDepthLayer);
				if (depthReadOnly)
				{
					// Tested and sampled, never written: nothing to store.
					depth.ImageLayout = depthTexture.SampledLayout;
					depth.LoadOp = VkAttachmentLoadOp.Load;
					depth.StoreOp = VkAttachmentStoreOp.None;
				}
				else
				{
					depth.ImageLayout = depthTexture.AttachmentLayout;
					depth.LoadOp = first ? Load(passDepthLoad) : VkAttachmentLoadOp.Load;
					depth.StoreOp = VkAttachmentStoreOp.Store;
					if (first)
					{
						*(float*)&depth.ClearValue = passDepthClear;
					}
				}
			}

			VkRenderingInfo info = new VkRenderingInfo
			{
				RenderArea = new VkRect2D { Extent = new VkExtent2D { Width = passWidth, Height = passHeight } },
				LayerCount = 1,
				ColorAttachmentCount = passColorCount,
				ColorAttachments = colors,
				DepthAttachment = depthTexture != null ? &depth : null
			};
			device.GraphicsStream.Recorder.BeginRendering(&info);
			rendering = true;
			passStarted = true;
		}

		#endregion

		#region Draws and dispatches

		/// <summary>
		///     Notes what a draw reads and writes and makes the rendering ready for it: begun after the barriers it and the
		///     attachments need, or ended and begun again when a barrier has to come between the pass's draws (a barrier of
		///     a resource the stream has not used in this submission goes ahead of the stream instead). Returns the
		///     descriptor set, made anew when the views it samples textures the pass draws into through are not right.
		/// </summary>
		private VkDescriptorSet NoteDraw(VulkanProgram current, VkDescriptorSet set, int* offsets, bool indexed, VulkanBuffer arguments, VulkanBuffer counts)
		{
			bool revisit = current.TrackedEpoch != device.SyncEpoch || current.WritesStorage;
			if (revisit && SampledViewsChanged(current))
			{
				current.ForgetSet();
				set = current.PrepareDescriptors(this, offsets);
			}

			// A draw that samples the depth attachment renders it read-only; the others write it.
			if (passDepthTexture != null)
			{
				if (current.DepthCheckPass != passSerial)
				{
					current.SamplesPassDepth = current.Samples(passDepthTexture);
					current.DepthCheckPass = passSerial;
				}

				if (current.SamplesPassDepth != depthReadOnly)
				{
					SuspendRendering();
					if (current.SamplesPassDepth && !passStarted && passDepthLoad == LoadOperation.Clear)
					{
						// The pass clears the depth the draw reads: the clear renders first.
						StartRendering();
						SuspendRendering();
					}

					depthReadOnly = current.SamplesPassDepth;
					stateChanged = true;
				}
			}

			if (revisit)
			{
				NoteProgram(current, Sync.GraphicsShaders);
			}

			bool recheck = checkedEpoch != device.SyncEpoch;
			VertexBufferDescriptor[] buffers = layout.Buffers;
			for (int i = 0; i < buffers.Length; i++)
			{
				VulkanBuffer buffer = vertexBuffers[i];
				if (buffer != null && (recheck || checkedVertexBuffers[i] != buffer))
				{
					RequireBuffer(buffer, Sync.VertexInput, false);
				}
			}

			if (indexed && (recheck || checkedIndexBuffer != indexBuffer))
			{
				RequireBuffer(indexBuffer, Sync.IndexInput, false);
			}

			if (arguments != null)
			{
				RequireBuffer(arguments, Sync.Indirect, false);
			}

			if (counts != null)
			{
				RequireBuffer(counts, Sync.Indirect, false);
			}

			if (rendering && RequirementsWait())
			{
				SuspendRendering();
				device.RenderingSuspensions++;
			}

			if (rendering)
			{
				ApplyRequirements(true);
			}

			if (!rendering)
			{
				// Also after a use that waited for the compute queue: the submission was sent, rendering goes on in the next.
				StartRendering();
			}

			current.TrackedEpoch = device.SyncEpoch;
			// A draw that writes storage looks at its buffers again next time: it may have written them.
			checkedEpoch = current.WritesStorage ? -1 : device.SyncEpoch;
			for (int i = 0; i < buffers.Length; i++)
			{
				checkedVertexBuffers[i] = vertexBuffers[i];
			}

			if (indexed)
			{
				checkedIndexBuffer = indexBuffer;
			}

			return set;
		}

		/// <summary>Notes what a dispatch reads and writes, and records the barriers it needs before it.</summary>
		private void NoteDispatch(VulkanProgram current, VulkanBuffer arguments)
		{
			NoteProgram(current, Sync.ComputeShader);
			if (arguments != null)
			{
				RequireBuffer(arguments, Sync.Indirect, false);
			}

			ApplyRequirements(false);
			current.TrackedEpoch = device.SyncEpoch;
		}

		/// <summary>
		///     Notes a resource the descriptor set being written binds, for the program's draws; marks it as used when no
		///     program's set is being written.
		/// </summary>
		private void Track(in VulkanProgram.TrackedResource resource)
		{
			if (Resolving != null)
			{
				Resolving.Track(resource);
				return;
			}

			resource.Texture?.Use();
			resource.Buffer?.Use();
			resource.Structure?.Use();
		}

		/// <summary>
		///     Notes the uses of the resources the program's descriptor set binds, by the program's stages. A read that an
		///     earlier command ordered in the same sync epoch is only marked as used: nothing was written or changed layout
		///     since, so it needs nothing.
		/// </summary>
		private void NoteProgram(VulkanProgram current, VkPipelineStageFlags2 stages)
		{
			int epoch = device.SyncEpoch;
			for (int i = 0; i < current.TrackedCount; i++)
			{
				ref VulkanProgram.TrackedResource entry = ref current.Tracked[i];
				if (entry.Texture != null)
				{
					VulkanTexture texture = entry.Texture;
					if (!entry.Write && texture.IsReadyFor(epoch, entry.Mip, entry.Mips, entry.Layer, entry.Layers, entry.Layout, stages))
					{
						texture.Use();
						continue;
					}

					RequireTexture(texture, entry.Mip, entry.Mips, entry.Layer, entry.Layers, entry.Layout, stages, entry.Write);
				}
				else if (entry.Buffer != null)
				{
					if (!entry.Write && entry.Buffer.IsReadyFor(epoch, stages))
					{
						entry.Buffer.Use();
						continue;
					}

					RequireBuffer(entry.Buffer, stages, entry.Write);
				}
				else
				{
					// A traced top level, and the bottom levels its last build placed (a ray reads them too).
					VulkanAccelerationStructure structure = entry.Structure;
					structure.Use();
					RequireTarget(structure.Storage, stages, false);
					foreach (VulkanAccelerationStructure bottom in structure.BottomLevels)
					{
						RequireTarget(bottom.Storage, stages, false, false);
					}
				}
			}
		}

		/// <summary>
		///     Whether the views of the textures the program samples no longer fit the open pass: a texture the pass draws
		///     into is sampled through a view of every level, or one that left a level out for another pass still is.
		/// </summary>
		private bool SampledViewsChanged(VulkanProgram current)
		{
			for (int i = 0; i < current.TrackedCount; i++)
			{
				ref VulkanProgram.TrackedResource entry = ref current.Tracked[i];
				if (entry.Sampled && entry.Mips != SampledMips(entry.Texture))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>
		///     The levels samplers may read of a texture now: all of them, or while the open pass draws into a level of it,
		///     those above that one (0: it draws into the first level).
		/// </summary>
		private int SampledMips(VulkanTexture texture)
		{
			return passOpen && texture.ColorPass == passSerial && !texture.GeneralMode ? texture.ColorMip : texture.MipLevels;
		}

		/// <summary>
		///     The view a sampler of a program reads a texture through, noted for the program's draws: the whole texture,
		///     or while the open pass draws into one of its levels the levels above that one (the others may be sampled; a
		///     view with the level drawn into may not be). A texture the pass draws into at its first level is taken into
		///     the general layout instead (<see cref="VulkanTexture.EnterGeneralMode" />).
		/// </summary>
		internal VkImageView SampledView(VulkanProgram owner, VulkanTexture texture, int unit)
		{
			int mips = SampledMips(texture);
			if (mips == 0)
			{
				texture.EnterGeneralMode();
				mips = texture.MipLevels;
			}

			owner.TrackTexture(unit, texture, mips, texture.SampledLayout);
			return texture.SampledLevels(mips);
		}

		#endregion

		#region Transfers

		/// <summary>
		///     The command buffer an upload into levels and layers of a texture goes into, after the barrier it needs:
		///     ahead of the stream when the stream has not used the texture in this submission (an open pass goes on
		///     undisturbed), else in the stream, where rendering is suspended for it. <paramref name="discard" />: the
		///     upload overwrites the subresources whole.
		/// </summary>
		internal VkCommandBuffer UploadCommands(VulkanTexture texture, int mip, int mips, int layer, int layers, bool discard)
		{
			// Given in asynchronous compute work, it goes to the compute queue when the texture is shared with it and no
			// open pass draws into it.
			UseComputeQueue(texture.Concurrent && !IsPassAttachment(texture));
			return TransferCommands(texture, texture.StreamUnused, mip, mips, layer, layers, texture.CopyDestinationLayout, true, discard);
		}

		/// <summary>
		///     Records the barrier a transfer's use of levels and layers of a texture needs and gives the command buffer
		///     the transfer goes into: ahead of the stream (<paramref name="ahead" />: the stream has not used the texture
		///     in this submission), or the stream's, with rendering suspended.
		/// </summary>
		internal VkCommandBuffer TransferCommands(VulkanTexture texture, bool ahead, int mip, int mips, int layer, int layers, VkImageLayout layout, bool write, bool discard)
		{
			if (device.RecordingCompute)
			{
				// The compute queue's commands go in order (nothing of its own ahead): no pass to keep going there.
				RequireTexture(texture, mip, mips, layer, layers, layout, Sync.Transfer, write, discard);
				ApplyRequirements(false);
				return device.MainCommands;
			}

			if (ahead)
			{
				SyncBatch setup = device.SetupBatch;
				texture.Require(setup, mip, mips, layer, layers, layout, Sync.Transfer, write, discard);
				VkCommandBuffer commands = device.SetupCommands;
				setup.Record(device, commands);
				texture.LastUse = device.RecordingSerial;
				return commands;
			}

			BeginStreamTransfer(texture);
			RequireTexture(texture, mip, mips, layer, layers, layout, Sync.Transfer, write, discard);
			ApplyRequirements(false);
			return device.MainCommands;
		}

		/// <summary>The command buffer a blit from a level of a texture into the next goes into, after the barriers it needs (as <see cref="TransferCommands" />).</summary>
		internal VkCommandBuffer MipCommands(VulkanTexture texture, bool ahead, int level)
		{
			if (ahead)
			{
				SyncBatch setup = device.SetupBatch;
				texture.Require(setup, level - 1, 1, 0, texture.Layers, texture.CopySourceLayout, Sync.Transfer, false, false);
				texture.Require(setup, level, 1, 0, texture.Layers, texture.CopyDestinationLayout, Sync.Transfer, true, true);
				VkCommandBuffer commands = device.SetupCommands;
				setup.Record(device, commands);
				texture.LastUse = device.RecordingSerial;
				return commands;
			}

			BeginStreamTransfer(texture);
			RequireTexture(texture, level - 1, 1, 0, texture.Layers, texture.CopySourceLayout, Sync.Transfer, false);
			RequireTexture(texture, level, 1, 0, texture.Layers, texture.CopyDestinationLayout, Sync.Transfer, true, true);
			ApplyRequirements(false);
			return device.MainCommands;
		}

		/// <summary>A transfer into a texture is recorded in the stream: rendering is suspended (a pass that clears the texture clears it first).</summary>
		private void BeginStreamTransfer(VulkanTexture texture)
		{
			System.Diagnostics.Debug.Assert(requirementCount == 0, "A transfer is recorded while a command's uses are being noted.");
			if (passOpen && !passStarted && PassClears() && IsPassAttachment(texture))
			{
				// The pass's clear comes before what is written into its attachments after it began.
				StartRendering();
			}

			SuspendRendering();
		}

		/// <summary>The command buffer a write of a buffer's bytes goes into, after the barrier it needs (as <see cref="UploadCommands(VulkanTexture, int, int, int, int, bool)" />).</summary>
		internal VkCommandBuffer UploadCommands(VulkanBuffer buffer)
		{
			if (UseComputeQueue(true))
			{
				RequireBuffer(buffer, Sync.Transfer, true);
				ApplyRequirements(false);
				return device.MainCommands;
			}

			if (buffer.MainUse != device.RecordingSerial)
			{
				SyncBatch setup = device.SetupBatch;
				buffer.Require(setup, Sync.Transfer, true);
				VkCommandBuffer commands = device.SetupCommands;
				setup.Record(device, commands);
				buffer.LastUse = device.RecordingSerial;
				return commands;
			}

			System.Diagnostics.Debug.Assert(requirementCount == 0, "A transfer is recorded while a command's uses are being noted.");
			SuspendRendering();
			RequireBuffer(buffer, Sync.Transfer, true);
			ApplyRequirements(false);
			return device.MainCommands;
		}

		/// <summary>The stream's command buffer for a read of a buffer by a copy (a readback), after the barrier it needs; rendering is suspended.</summary>
		internal VkCommandBuffer ReadCommands(VulkanBuffer buffer)
		{
			SuspendRendering();
			RequireBuffer(buffer, Sync.Transfer, false);
			ApplyRequirements(false);
			return device.MainCommands;
		}

		/// <summary>
		///     A command buffer sent on its own (<see cref="VulkanDevice.SubmitAsideAndWait" />) for a readback of levels and
		///     layers of a texture the submission being recorded has not used, after the barrier the copy's read needs.
		/// </summary>
		internal VkCommandBuffer AsideCommands(VulkanTexture texture, int mip, int mips, int layer, int layers)
		{
			VkCommandBuffer commands = device.BeginAside();
			device.AsideRecording = true;
			texture.Require(device.AsideBatch, mip, mips, layer, layers, texture.CopySourceLayout, Sync.Transfer, false, false);
			device.AsideRecording = false;
			device.AsideBatch.Record(device, commands);
			return commands;
		}

		/// <summary>As <see cref="AsideCommands(VulkanTexture, int, int, int, int)" />, for a buffer.</summary>
		internal VkCommandBuffer AsideCommands(VulkanBuffer buffer)
		{
			VkCommandBuffer commands = device.BeginAside();
			device.AsideRecording = true;
			buffer.Require(device.AsideBatch, Sync.Transfer, false);
			device.AsideRecording = false;
			device.AsideBatch.Record(device, commands);
			return commands;
		}

		/// <summary>Makes what the transfers recorded so far wrote visible to the CPU (a readback's copy, before the CPU reads it).</summary>
		internal void TransfersToHost(VkCommandBuffer commands)
		{
			endBatch.AddMemory(Sync.Transfer, VkAccessFlags2.TransferWriteBit, Sync.Host, VkAccessFlags2.HostReadBit);
			endBatch.Record(device, commands);
		}

		/// <summary>
		///     The stream's command buffer for presenting: the back buffer is read by the blit into the window's image.
		///     The blit waits for every earlier write in its own command buffer, also when the back buffer's state needs no
		///     barrier (a readback of it right before): synchronization validation checked such a blit against a use of
		///     the back buffer older than its last one (after the CPU had waited for the readback), the order through the
		///     submissions in between lost to it.
		/// </summary>
		internal VkCommandBuffer PresentCommands(VulkanTexture backbuffer)
		{
			UseGraphicsQueue();
			SuspendRendering();
			if (backbuffer != null)
			{
				RequireTexture(backbuffer, 0, 1, 0, 1, backbuffer.CopySourceLayout, Sync.Transfer, false);
				streamBatch.AddMemory(VkPipelineStageFlags2.AllCommandsBit, VkAccessFlags2.MemoryWriteBit, Sync.Transfer, VkAccessFlags2.TransferReadBit);
			}

			ApplyRequirements(false);
			return device.MainCommands;
		}

		/// <summary>
		///     After the present's blit, in its command buffer: the back buffer goes back to the layout it is drawn in,
		///     ordered right after the blit's read. The next frame's first draw into it then needs no layout transition: one
		///     made there, in a later submission than the blit's (which waits for the swapchain's image), was reported by
		///     sync validation as racing the blit's read.
		/// </summary>
		internal void AfterPresent(VulkanTexture backbuffer)
		{
			RequireTexture(backbuffer, 0, 1, 0, 1, backbuffer.AttachmentLayout, Sync.ColorOutput, false);
			ApplyRequirements(false);
		}

		/// <summary>
		///     The submission is about to end: the values of the timestamps it wrote are copied into memory the CPU reads,
		///     after everything before (the stamps' writes, and the last submission's copies into the same memory).
		/// </summary>
		private void CopyTimestamps()
		{
			if (!device.HasWrittenQueries)
			{
				return;
			}

			VkCommandBuffer commands = device.MainCommands;
			endBatch.AddMemory(VkPipelineStageFlags2.AllCommandsBit, VkAccessFlags2.TransferWriteBit, Sync.Transfer, VkAccessFlags2.TransferWriteBit);
			endBatch.Record(device, commands);
			device.CopyQueryResults(commands);
			device.HostReads |= Sync.Transfer;
		}

		/// <summary>The submission is about to end: what the GPU wrote into buffers the CPU reads is made visible to it.</summary>
		private void FinishForHost()
		{
			if (device.HostReads == VkPipelineStageFlags2.None)
			{
				return;
			}

			endBatch.AddMemory(device.HostReads, Sync.WriteAccess(device.HostReads), Sync.Host, VkAccessFlags2.HostReadBit);
			endBatch.Record(device, device.MainCommands);
			device.HostReads = VkPipelineStageFlags2.None;
		}

		#endregion
	}
}
