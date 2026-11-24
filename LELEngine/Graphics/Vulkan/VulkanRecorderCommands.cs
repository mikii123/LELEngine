using System;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The commands the recorder writes and records (VulkanRecorder.cs): one method per Vulkan command the command
	///     list gives, writing its arguments, and the replay that records them into a command buffer.
	/// </summary>
	internal sealed unsafe partial class VulkanRecorder
	{
		#region Writing

		public void BindPipeline(VkPipelineBindPoint point, VkPipeline pipeline)
		{
			BindPipelineArgs* args = (BindPipelineArgs*)Begin(Op.BindPipeline, sizeof(BindPipelineArgs));
			args->Point = point;
			args->Pipeline = pipeline;
			End();
		}

		/// <summary>Binds one descriptor set (set 0) with its dynamic offsets (at most eight).</summary>
		public void BindDescriptorSet(VkPipelineBindPoint point, VkPipelineLayout layout, VkDescriptorSet set, int count, int* offsets)
		{
			BindSetArgs* args = (BindSetArgs*)Begin(Op.BindDescriptorSet, sizeof(BindSetArgs));
			args->Point = point;
			args->Layout = layout;
			args->Set = set;
			args->Count = count;
			for (int i = 0; i < count; i++)
			{
				args->Offsets[i] = offsets[i];
			}

			End();
		}

		public void PushConstants(VkPipelineLayout layout, VkShaderStageFlags stages, int size, void* values)
		{
			PushArgs* args = (PushArgs*)Begin(Op.PushConstants, sizeof(PushArgs) + size);
			args->Layout = layout;
			args->Stages = stages;
			args->Size = size;
			Buffer.MemoryCopy(values, args + 1, size, size);
			End();
		}

		public void BindVertexBuffer(int index, VkBuffer buffer, long offset)
		{
			BufferArgs* args = (BufferArgs*)Begin(Op.BindVertexBuffer, sizeof(BufferArgs));
			args->Buffer = buffer;
			args->Offset = offset;
			args->A = index;
			End();
		}

		public void BindIndexBuffer(VkBuffer buffer, VkIndexType type)
		{
			BufferArgs* args = (BufferArgs*)Begin(Op.BindIndexBuffer, sizeof(BufferArgs));
			args->Buffer = buffer;
			args->A = (int)type;
			End();
		}

		/// <summary>A dynamic state of one value (<see cref="StateKind" />).</summary>
		public void SetState(StateKind kind, int value, float a = 0f, float b = 0f)
		{
			StateArgs* args = (StateArgs*)Begin(Op.SetState, sizeof(StateArgs));
			args->Kind = kind;
			args->Value = value;
			args->A = a;
			args->B = b;
			End();
		}

		public void SetViewport(in VkViewport viewport)
		{
			*(VkViewport*)Begin(Op.SetViewport, sizeof(VkViewport)) = viewport;
			End();
		}

		public void SetScissor(in VkRect2D rect)
		{
			*(VkRect2D*)Begin(Op.SetScissor, sizeof(VkRect2D)) = rect;
			End();
		}

		public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
		{
			int* args = (int*)Begin(Op.Draw, 16);
			args[0] = vertexCount;
			args[1] = instanceCount;
			args[2] = firstVertex;
			args[3] = firstInstance;
			End();
		}

		public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance)
		{
			int* args = (int*)Begin(Op.DrawIndexed, 20);
			args[0] = indexCount;
			args[1] = instanceCount;
			args[2] = firstIndex;
			args[3] = baseVertex;
			args[4] = firstInstance;
			End();
		}

		/// <summary>Indirect draws (<paramref name="indexed" />: of indices) reading their arguments from a buffer.</summary>
		public void DrawIndirect(bool indexed, VkBuffer buffer, long offset, int drawCount, int stride)
		{
			BufferArgs* args = (BufferArgs*)Begin(indexed ? Op.DrawIndexedIndirect : Op.DrawIndirect, sizeof(BufferArgs));
			args->Buffer = buffer;
			args->Offset = offset;
			args->A = drawCount;
			args->B = stride;
			End();
		}

		public void DrawIndexedIndirectCount(VkBuffer buffer, long offset, VkBuffer counts, long countOffset, int maxDrawCount, int stride)
		{
			CountArgs* args = (CountArgs*)Begin(Op.DrawIndexedIndirectCount, sizeof(CountArgs));
			args->Buffer = buffer;
			args->Offset = offset;
			args->Counts = counts;
			args->CountOffset = countOffset;
			args->MaxDrawCount = maxDrawCount;
			args->Stride = stride;
			End();
		}

		public void Dispatch(int x, int y, int z)
		{
			int* args = (int*)Begin(Op.Dispatch, 12);
			args[0] = x;
			args[1] = y;
			args[2] = z;
			End();
		}

		public void DispatchIndirect(VkBuffer buffer, long offset)
		{
			BufferArgs* args = (BufferArgs*)Begin(Op.DispatchIndirect, sizeof(BufferArgs));
			args->Buffer = buffer;
			args->Offset = offset;
			End();
		}

		/// <summary>Begins rendering as the info says (its attachments are copied).</summary>
		public void BeginRendering(VkRenderingInfo* info)
		{
			int colors = info->ColorAttachmentCount;
			RenderingArgs* args = (RenderingArgs*)Begin(Op.BeginRendering, sizeof(RenderingArgs) + colors * sizeof(VkRenderingAttachmentInfo));
			args->Area = info->RenderArea;
			args->LayerCount = info->LayerCount;
			args->ColorCount = colors;
			args->HasDepth = info->DepthAttachment != null ? 1 : 0;
			if (info->DepthAttachment != null)
			{
				args->Depth = *info->DepthAttachment;
			}

			VkRenderingAttachmentInfo* copies = (VkRenderingAttachmentInfo*)(args + 1);
			for (int i = 0; i < colors; i++)
			{
				copies[i] = info->ColorAttachments[i];
			}

			End();
		}

		public void EndRendering()
		{
			Begin(Op.EndRendering, 0);
			End();
		}

		public void ClearAttachment(in VkClearAttachment clear, in VkClearRect rect)
		{
			ClearArgs* args = (ClearArgs*)Begin(Op.ClearAttachment, sizeof(ClearArgs));
			args->Clear = clear;
			args->Rect = rect;
			End();
		}

		/// <summary>A pipeline barrier of a memory barrier (null: none) and image barriers.</summary>
		public void PipelineBarrier(VkMemoryBarrier2* memory, VkImageMemoryBarrier2* images, int imageCount)
		{
			BarrierArgs* args = (BarrierArgs*)Begin(Op.PipelineBarrier, sizeof(BarrierArgs) + imageCount * sizeof(VkImageMemoryBarrier2));
			args->HasMemory = memory != null ? 1 : 0;
			args->ImageCount = imageCount;
			if (memory != null)
			{
				args->Memory = *memory;
			}

			VkImageMemoryBarrier2* copies = (VkImageMemoryBarrier2*)(args + 1);
			for (int i = 0; i < imageCount; i++)
			{
				copies[i] = images[i];
			}

			End();
		}

		public void CopyBuffer(VkBuffer source, VkBuffer destination, in VkBufferCopy region)
		{
			CopyBufferArgs* args = (CopyBufferArgs*)Begin(Op.CopyBuffer, sizeof(CopyBufferArgs));
			args->Source = source;
			args->Destination = destination;
			args->Region = region;
			End();
		}

		public void CopyImage(VkImage source, VkImageLayout sourceLayout, VkImage destination, VkImageLayout destinationLayout, in VkImageCopy region)
		{
			CopyImageArgs* args = (CopyImageArgs*)Begin(Op.CopyImage, sizeof(CopyImageArgs));
			args->Source = source;
			args->SourceLayout = sourceLayout;
			args->Destination = destination;
			args->DestinationLayout = destinationLayout;
			args->Region = region;
			End();
		}

		public void BlitImage(VkImage source, VkImageLayout sourceLayout, VkImage destination, VkImageLayout destinationLayout, in VkImageBlit region, VkFilter filter)
		{
			BlitArgs* args = (BlitArgs*)Begin(Op.BlitImage, sizeof(BlitArgs));
			args->Source = source;
			args->SourceLayout = sourceLayout;
			args->Destination = destination;
			args->DestinationLayout = destinationLayout;
			args->Region = region;
			args->Filter = filter;
			End();
		}

		public void ClearColorImage(VkImage image, VkImageLayout layout, in VkClearColorValue color, in VkImageSubresourceRange range)
		{
			ClearImageArgs* args = (ClearImageArgs*)Begin(Op.ClearColorImage, sizeof(ClearImageArgs));
			args->Image = image;
			args->Layout = layout;
			*(VkClearColorValue*)args->Value = color;
			args->Range = range;
			End();
		}

		public void ClearDepthImage(VkImage image, VkImageLayout layout, in VkClearDepthStencilValue depth, in VkImageSubresourceRange range)
		{
			ClearImageArgs* args = (ClearImageArgs*)Begin(Op.ClearDepthImage, sizeof(ClearImageArgs));
			args->Image = image;
			args->Layout = layout;
			*(VkClearDepthStencilValue*)args->Value = depth;
			args->Range = range;
			End();
		}

		public void CopyImageToBuffer(VkImage image, VkImageLayout layout, VkBuffer buffer, in VkBufferImageCopy region)
		{
			ImageBufferArgs* args = (ImageBufferArgs*)Begin(Op.CopyImageToBuffer, sizeof(ImageBufferArgs));
			args->Image = image;
			args->Layout = layout;
			args->Buffer = buffer;
			args->Region = region;
			End();
		}

		public void WriteTimestamp(VkPipelineStageFlags2 stage, VkQueryPool pool, int query)
		{
			TimestampArgs* args = (TimestampArgs*)Begin(Op.WriteTimestamp, sizeof(TimestampArgs));
			args->Stage = stage;
			args->Pool = pool;
			args->Query = query;
			End();
		}

		#endregion

		#region Recording

		/// <summary>Records the commands written between two addresses into a command buffer.</summary>
		private void Replay(byte* start, byte* end, VkCommandBuffer commands)
		{
			byte* position = start;
			while (position < end)
			{
				Header* header = (Header*)position;
				byte* body = position + sizeof(Header);
				position += header->Size;
				switch (header->Op)
				{
					case Op.BindPipeline:
					{
						BindPipelineArgs* args = (BindPipelineArgs*)body;
						vk->vkCmdBindPipeline.Invoke(commands, args->Point, args->Pipeline);
						break;
					}
					case Op.BindDescriptorSet:
					{
						BindSetArgs* args = (BindSetArgs*)body;
						VkDescriptorSet set = args->Set;
						vk->vkCmdBindDescriptorSets.Invoke(commands, args->Point, args->Layout, 0, 1, &set, args->Count, args->Offsets);
						break;
					}
					case Op.PushConstants:
					{
						PushArgs* args = (PushArgs*)body;
						vk->vkCmdPushConstants.Invoke(commands, args->Layout, args->Stages, 0, args->Size, args + 1);
						break;
					}
					case Op.BindVertexBuffer:
					{
						BufferArgs* args = (BufferArgs*)body;
						VkBuffer buffer = args->Buffer;
						Brutal.ByteSize64 offset = (Brutal.ByteSize64)(ulong)args->Offset;
						vk->vkCmdBindVertexBuffers.Invoke(commands, args->A, 1, &buffer, &offset);
						break;
					}
					case Op.BindIndexBuffer:
					{
						BufferArgs* args = (BufferArgs*)body;
						vk->vkCmdBindIndexBuffer.Invoke(commands, args->Buffer, default, (VkIndexType)args->A);
						break;
					}
					case Op.SetState:
						ReplayState(commands, (StateArgs*)body);
						break;
					case Op.SetViewport:
						vk->vkCmdSetViewportWithCount.Invoke(commands, 1, (VkViewport*)body);
						break;
					case Op.SetScissor:
						vk->vkCmdSetScissorWithCount.Invoke(commands, 1, (VkRect2D*)body);
						break;
					case Op.Draw:
					{
						int* args = (int*)body;
						vk->vkCmdDraw.Invoke(commands, args[0], args[1], args[2], args[3]);
						break;
					}
					case Op.DrawIndexed:
					{
						int* args = (int*)body;
						vk->vkCmdDrawIndexed.Invoke(commands, args[0], args[1], args[2], args[3], args[4]);
						break;
					}
					case Op.DrawIndirect:
					{
						BufferArgs* args = (BufferArgs*)body;
						vk->vkCmdDrawIndirect.Invoke(commands, args->Buffer, (Brutal.ByteSize64)(ulong)args->Offset, args->A, args->B);
						break;
					}
					case Op.DrawIndexedIndirect:
					{
						BufferArgs* args = (BufferArgs*)body;
						vk->vkCmdDrawIndexedIndirect.Invoke(commands, args->Buffer, (Brutal.ByteSize64)(ulong)args->Offset, args->A, args->B);
						break;
					}
					case Op.DrawIndexedIndirectCount:
					{
						CountArgs* args = (CountArgs*)body;
						vk->vkCmdDrawIndexedIndirectCount.Invoke(commands, args->Buffer, (Brutal.ByteSize64)(ulong)args->Offset, args->Counts, (Brutal.ByteSize64)(ulong)args->CountOffset,
							args->MaxDrawCount, args->Stride);
						break;
					}
					case Op.Dispatch:
					{
						int* args = (int*)body;
						vk->vkCmdDispatch.Invoke(commands, args[0], args[1], args[2]);
						break;
					}
					case Op.DispatchIndirect:
					{
						BufferArgs* args = (BufferArgs*)body;
						vk->vkCmdDispatchIndirect.Invoke(commands, args->Buffer, (Brutal.ByteSize64)(ulong)args->Offset);
						break;
					}
					case Op.BeginRendering:
					{
						RenderingArgs* args = (RenderingArgs*)body;
						VkRenderingInfo info = new VkRenderingInfo
						{
							RenderArea = args->Area,
							LayerCount = args->LayerCount,
							ColorAttachmentCount = args->ColorCount,
							ColorAttachments = (VkRenderingAttachmentInfo*)(args + 1),
							DepthAttachment = args->HasDepth != 0 ? &args->Depth : null
						};
						vk->vkCmdBeginRendering.Invoke(commands, &info);
						break;
					}
					case Op.EndRendering:
						vk->vkCmdEndRendering.Invoke(commands);
						break;
					case Op.ClearAttachment:
					{
						ClearArgs* args = (ClearArgs*)body;
						vk->vkCmdClearAttachments.Invoke(commands, 1, &args->Clear, 1, &args->Rect);
						break;
					}
					case Op.PipelineBarrier:
					{
						BarrierArgs* args = (BarrierArgs*)body;
						VkDependencyInfo dependency = new VkDependencyInfo
						{
							MemoryBarrierCount = args->HasMemory,
							MemoryBarriers = &args->Memory,
							ImageMemoryBarrierCount = args->ImageCount,
							ImageMemoryBarriers = (VkImageMemoryBarrier2*)(args + 1)
						};
						vk->vkCmdPipelineBarrier2.Invoke(commands, &dependency);
						break;
					}
					case Op.CopyBuffer:
					{
						CopyBufferArgs* args = (CopyBufferArgs*)body;
						vk->vkCmdCopyBuffer.Invoke(commands, args->Source, args->Destination, 1, &args->Region);
						break;
					}
					case Op.CopyImage:
					{
						CopyImageArgs* args = (CopyImageArgs*)body;
						vk->vkCmdCopyImage.Invoke(commands, args->Source, args->SourceLayout, args->Destination, args->DestinationLayout, 1, &args->Region);
						break;
					}
					case Op.BlitImage:
					{
						BlitArgs* args = (BlitArgs*)body;
						vk->vkCmdBlitImage.Invoke(commands, args->Source, args->SourceLayout, args->Destination, args->DestinationLayout, 1, &args->Region, args->Filter);
						break;
					}
					case Op.ClearColorImage:
					{
						ClearImageArgs* args = (ClearImageArgs*)body;
						vk->vkCmdClearColorImage.Invoke(commands, args->Image, args->Layout, (VkClearColorValue*)args->Value, 1, &args->Range);
						break;
					}
					case Op.ClearDepthImage:
					{
						ClearImageArgs* args = (ClearImageArgs*)body;
						vk->vkCmdClearDepthStencilImage.Invoke(commands, args->Image, args->Layout, (VkClearDepthStencilValue*)args->Value, 1, &args->Range);
						break;
					}
					case Op.CopyImageToBuffer:
					{
						ImageBufferArgs* args = (ImageBufferArgs*)body;
						vk->vkCmdCopyImageToBuffer.Invoke(commands, args->Image, args->Layout, args->Buffer, 1, &args->Region);
						break;
					}
					case Op.WriteTimestamp:
					{
						TimestampArgs* args = (TimestampArgs*)body;
						vk->vkCmdWriteTimestamp2.Invoke(commands, args->Stage, args->Pool, args->Query);
						break;
					}
					default:
						throw new InvalidOperationException("Unknown recorded command " + header->Op + ".");
				}
			}
		}

		private void ReplayState(VkCommandBuffer commands, StateArgs* args)
		{
			switch (args->Kind)
			{
				case StateKind.CullMode:
					vk->vkCmdSetCullMode.Invoke(commands, (VkCullModeFlags)args->Value);
					break;
				case StateKind.DepthTest:
					vk->vkCmdSetDepthTestEnable.Invoke(commands, args->Value != 0);
					break;
				case StateKind.DepthWrite:
					vk->vkCmdSetDepthWriteEnable.Invoke(commands, args->Value != 0);
					break;
				case StateKind.DepthCompare:
					vk->vkCmdSetDepthCompareOp.Invoke(commands, (VkCompareOp)args->Value);
					break;
				case StateKind.DepthBiasEnable:
					vk->vkCmdSetDepthBiasEnable.Invoke(commands, args->Value != 0);
					break;
				case StateKind.DepthBias:
					vk->vkCmdSetDepthBias.Invoke(commands, args->A, 0f, args->B);
					break;
				default:
					vk->vkCmdSetPrimitiveTopology.Invoke(commands, (VkPrimitiveTopology)args->Value);
					break;
			}
		}

		#endregion

		internal enum Op
		{
			BindPipeline = 1,
			BindDescriptorSet,
			PushConstants,
			BindVertexBuffer,
			BindIndexBuffer,
			SetState,
			SetViewport,
			SetScissor,
			Draw,
			DrawIndexed,
			DrawIndirect,
			DrawIndexedIndirect,
			DrawIndexedIndirectCount,
			Dispatch,
			DispatchIndirect,
			BeginRendering,
			EndRendering,
			ClearAttachment,
			PipelineBarrier,
			CopyBuffer,
			CopyImage,
			BlitImage,
			ClearColorImage,
			ClearDepthImage,
			CopyImageToBuffer,
			WriteTimestamp
		}

		/// <summary>The dynamic states of one value: culling, depth test, write and compare, depth bias (on and its values), topology.</summary>
		internal enum StateKind
		{
			CullMode,
			DepthTest,
			DepthWrite,
			DepthCompare,
			DepthBiasEnable,
			DepthBias,
			Topology
		}

		private struct BindPipelineArgs
		{
			public VkPipelineBindPoint Point;
			public VkPipeline Pipeline;
		}

		private struct BindSetArgs
		{
			public VkPipelineBindPoint Point;
			public int Count;
			public VkPipelineLayout Layout;
			public VkDescriptorSet Set;
			public fixed int Offsets[8];
		}

		private struct PushArgs
		{
			public VkPipelineLayout Layout;
			public VkShaderStageFlags Stages;
			public int Size;
		}

		private struct BufferArgs
		{
			public VkBuffer Buffer;
			public long Offset;
			public int A;
			public int B;
		}

		private struct CountArgs
		{
			public VkBuffer Buffer;
			public long Offset;
			public VkBuffer Counts;
			public long CountOffset;
			public int MaxDrawCount;
			public int Stride;
		}

		private struct StateArgs
		{
			public StateKind Kind;
			public int Value;
			public float A;
			public float B;
		}

		private struct RenderingArgs
		{
			public VkRect2D Area;
			public int LayerCount;
			public int ColorCount;
			public int HasDepth;
			public VkRenderingAttachmentInfo Depth;
		}

		private struct ClearArgs
		{
			public VkClearAttachment Clear;
			public VkClearRect Rect;
		}

		private struct BarrierArgs
		{
			public int HasMemory;
			public int ImageCount;
			public VkMemoryBarrier2 Memory;
		}

		private struct CopyBufferArgs
		{
			public VkBuffer Source;
			public VkBuffer Destination;
			public VkBufferCopy Region;
		}

		private struct CopyImageArgs
		{
			public VkImage Source;
			public VkImage Destination;
			public VkImageLayout SourceLayout;
			public VkImageLayout DestinationLayout;
			public VkImageCopy Region;
		}

		private struct BlitArgs
		{
			public VkImage Source;
			public VkImage Destination;
			public VkImageLayout SourceLayout;
			public VkImageLayout DestinationLayout;
			public VkFilter Filter;
			public VkImageBlit Region;
		}

		private struct ClearImageArgs
		{
			public VkImage Image;
			public VkImageLayout Layout;

			/// <summary>A colour (16 bytes) or a depth and stencil value.</summary>
			public fixed uint Value[4];

			public VkImageSubresourceRange Range;
		}

		private struct ImageBufferArgs
		{
			public VkImage Image;
			public VkBuffer Buffer;
			public VkImageLayout Layout;
			public VkBufferImageCopy Region;
		}

		private struct TimestampArgs
		{
			public VkPipelineStageFlags2 Stage;
			public VkQueryPool Pool;
			public int Query;
		}
	}
}
