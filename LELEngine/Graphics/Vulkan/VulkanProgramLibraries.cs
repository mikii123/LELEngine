using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A program's pipelines linked from libraries (VulkanDevicePipelineLibraries.cs): its shaders' parts are compiled
	///     when it is built (pre-rasterization for filled triangles, fragment shader for one sample; other variants the
	///     first time they are drawn), its vertex input parts per vertex layout and kind of primitive. The first draw of a
	///     combination links four parts as they are, and a thread of the pool links them again optimized, which takes the
	///     fast pipeline's place (the fast one is freed once the GPU is done with it).
	/// </summary>
	internal sealed unsafe partial class VulkanProgram
	{
		/// <summary>The shaders' parts made so far, by variant; null when the program's pipelines are compiled whole.</summary>
		private Dictionary<int, VkPipeline> shaderLibraries;

		/// <summary>The vertex input parts by vertex layout and kind of primitive.</summary>
		private Dictionary<long, VertexLibrary> vertexLibraries;

		/// <summary>Guards the libraries' end: optimized links still being made, and whether the program was given up meanwhile.</summary>
		private readonly object librarySync = new object();

		private int optimizing;
		private bool librariesOrphaned;

		/// <summary>
		///     The usual shader parts, while the program is built (on the thread that builds it): what a first draw would
		///     otherwise compile. A device that refuses them leaves the program to whole pipelines.
		/// </summary>
		private void MakeShaderLibraries()
		{
			shaderLibraries = new Dictionary<int, VkPipeline>();
			vertexLibraries = new Dictionary<long, VertexLibrary>();
			try
			{
				PreRasterizationLibrary(false, false);
				FragmentShaderLibrary(1);
			}
			catch (InvalidOperationException e)
			{
				Console.WriteLine("[Vulkan] Program '" + Name + "' is drawn with whole pipelines: " + e.Message);
				foreach (VkPipeline library in shaderLibraries.Values)
				{
					device.Retire(RetiredKind.Pipeline, library.VkHandle, 0, 0);
				}

				shaderLibraries = null;
				vertexLibraries = null;
			}
		}

		/// <summary>A pipeline linked from the parts of its combination (made where missing), and given to a thread to link optimized.</summary>
		private PipelineEntry LinkPipeline(in PipelineKey key, VertexInputLayout layout, in RenderState state, TextureFormat[] colorFormats, int colorCount, TextureFormat depthFormat)
		{
			VertexLibrary input = VertexInputLibrary(layout, key.TopologyClass);
			VkPipeline* parts = stackalloc VkPipeline[4];
			parts[0] = input.Pipeline;
			parts[1] = PreRasterizationLibrary(state.Wireframe && device.HasWireframe, (key.State & 1UL << 32) != 0);
			parts[2] = FragmentShaderLibrary(Math.Max(1, (int)key.Samples));
			parts[3] = device.FragmentOutputLibrary(key, state, colorFormats, colorCount, depthFormat);
			PipelineEntry entry = new PipelineEntry { DefaultsBinding = input.DefaultsBinding, Pipeline = Link(parts, false) };
			if (!VulkanDevice.FastPipelinesOnly)
			{
				Optimize(entry, parts[0], parts[1], parts[2], parts[3]);
			}

			return entry;
		}

		/// <summary>Links four parts into a pipeline: as they are (fast), or optimized over the stages.</summary>
		private VkPipeline Link(VkPipeline* parts, bool optimized)
		{
			VkPipelineLibraryCreateInfoKHR libraries = new VkPipelineLibraryCreateInfoKHR { LibraryCount = 4, Libraries = parts };
			VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
			{
				Next = &libraries,
				Flags = optimized ? VkPipelineCreateFlags.LinkTimeOptimizationBitEXT : VkPipelineCreateFlags.None,
				Layout = pipelineLayout
			};
			VkPipeline pipeline;
			VulkanDevice.Check(device.Vk->vkCreateGraphicsPipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines (" + Name + ", linked)");
			device.PipelineMade();
			return pipeline;
		}

		/// <summary>Links the parts optimized on a thread of the pool; the result takes the fast pipeline's place in the entry.</summary>
		private void Optimize(PipelineEntry entry, VkPipeline input, VkPipeline preRasterization, VkPipeline fragment, VkPipeline output)
		{
			lock (librarySync)
			{
				optimizing++;
			}

			Task.Run(() =>
			{
				try
				{
					VkPipeline* parts = stackalloc VkPipeline[4] { input, preRasterization, fragment, output };
					VkPipeline optimized = Link(parts, true);
					VkPipeline replaced;
					lock (entry)
					{
						replaced = entry.Pipeline;
						if (!replaced.IsNull())
						{
							entry.Pipeline = optimized;
						}
					}

					// The fast pipeline may be bound in the submission being recorded (or the program is gone and the
					// optimized one is not needed): freed after the next one has run.
					device.Retire(RetiredKind.Pipeline, replaced.IsNull() ? optimized.VkHandle : replaced.VkHandle, 0, device.RecordingSerial + 1);
					if (device.HasShaderInfo && !replaced.IsNull())
					{
						foreach (Stage stage in stages)
						{
							device.ReportShaderInfo(optimized, stage.Flag, Name, " (linked optimized)");
						}
					}
				}
				catch (InvalidOperationException e)
				{
					// The fast pipeline stays.
					Console.WriteLine("[Vulkan] " + e.Message);
				}
				finally
				{
					EndOptimizing();
				}
			});
		}

		/// <summary>An optimized link has ended: the last one after the program was given up frees the libraries.</summary>
		private void EndOptimizing()
		{
			bool release;
			lock (librarySync)
			{
				optimizing--;
				release = optimizing == 0 && librariesOrphaned;
			}

			if (release)
			{
				RetireLibraries();
			}
		}

		/// <summary>The program is given up: its pipelines go, and its libraries once no optimized link uses them any more.</summary>
		private void ReleaseLibraries(long serial)
		{
			foreach (PipelineEntry entry in pipelines.Values)
			{
				lock (entry)
				{
					device.Retire(RetiredKind.Pipeline, entry.Pipeline.VkHandle, 0, serial);
					entry.Pipeline = default;
				}
			}

			if (shaderLibraries == null)
			{
				return;
			}

			bool release;
			lock (librarySync)
			{
				librariesOrphaned = true;
				release = optimizing == 0;
			}

			if (release)
			{
				RetireLibraries();
			}
		}

		private void RetireLibraries()
		{
			long serial = Math.Max(LastUse, device.RecordingSerial);
			foreach (VkPipeline library in shaderLibraries.Values)
			{
				device.Retire(RetiredKind.Pipeline, library.VkHandle, 0, serial);
			}

			foreach (VertexLibrary library in vertexLibraries.Values)
			{
				device.Retire(RetiredKind.Pipeline, library.Pipeline.VkHandle, 0, serial);
			}
		}

		/// <summary>The part of the vertex stages (and tessellation and geometry): rasterization of filled or lined triangles, conservative or not.</summary>
		private VkPipeline PreRasterizationLibrary(bool wireframe, bool conservativeRasterization)
		{
			int variant = 1 | (wireframe ? 2 : 0) | (conservativeRasterization ? 4 : 0);
			if (shaderLibraries.TryGetValue(variant, out VkPipeline library))
			{
				return library;
			}

			fixed (byte* main = "main\0"u8)
			{
				VkPipelineShaderStageCreateInfo* shaderStages = stackalloc VkPipelineShaderStageCreateInfo[stages.Length];
				int stageCount = 0;
				bool tessellated = false;
				for (int i = 0; i < stages.Length; i++)
				{
					if (stages[i].Kind != ShaderStage.Fragment)
					{
						shaderStages[stageCount++] = new VkPipelineShaderStageCreateInfo { Stage = stages[i].Flag, Module = stages[i].Module, Name = main };
						tessellated |= stages[i].Kind == ShaderStage.TessControl;
					}
				}

				VkPipelineTessellationStateCreateInfo tessellation = new VkPipelineTessellationStateCreateInfo { PatchControlPoints = 3 };
				VkPipelineViewportDepthClipControlCreateInfoEXT clip = new VkPipelineViewportDepthClipControlCreateInfoEXT { NegativeOneToOne = true };
				VkPipelineViewportStateCreateInfo viewport = new VkPipelineViewportStateCreateInfo { Next = &clip };
				VkPipelineRasterizationProvokingVertexStateCreateInfoEXT provoking = new VkPipelineRasterizationProvokingVertexStateCreateInfoEXT
				{
					ProvokingVertexMode = VkProvokingVertexModeEXT.LastVertexEXT
				};
				// Disabled named outright where the device has the extension (see VulkanProgram's pipelines: Intel's driver).
				VkPipelineRasterizationConservativeStateCreateInfoEXT conservative = new VkPipelineRasterizationConservativeStateCreateInfoEXT
				{
					Next = device.HasProvokingVertexLast ? &provoking : null,
					ConservativeRasterizationMode = conservativeRasterization ? VkConservativeRasterizationModeEXT.OverestimateEXT : VkConservativeRasterizationModeEXT.DisabledEXT
				};
				VkPipelineRasterizationStateCreateInfo rasterization = new VkPipelineRasterizationStateCreateInfo
				{
					Next = device.HasConservativeRasterization ? &conservative : device.HasProvokingVertexLast ? &provoking : null,
					PolygonMode = wireframe ? VkPolygonMode.Line : VkPolygonMode.Fill,
					FrontFace = VkFrontFace.Clockwise,
					LineWidth = 1f
				};
				VkDynamicState* dynamics = stackalloc VkDynamicState[5]
				{
					VkDynamicState.ViewportWithCount, VkDynamicState.ScissorWithCount, VkDynamicState.CullMode, VkDynamicState.DepthBiasEnable, VkDynamicState.DepthBias
				};
				VkPipelineDynamicStateCreateInfo dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 5, DynamicStates = dynamics };
				VkGraphicsPipelineLibraryCreateInfoEXT part = new VkGraphicsPipelineLibraryCreateInfoEXT { Flags = VkGraphicsPipelineLibraryFlagsEXT.PreRasterizationShadersBitEXT };
				VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
				{
					Next = &part,
					Flags = VulkanDevice.LibraryFlags,
					StageCount = stageCount,
					Stages = shaderStages,
					ViewportState = &viewport,
					RasterizationState = &rasterization,
					TessellationState = tessellated ? &tessellation : null,
					DynamicState = &dynamic,
					Layout = pipelineLayout
				};
				VulkanDevice.Check(device.Vk->vkCreateGraphicsPipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &library), "vkCreateGraphicsPipelines (" + Name + ", vertex stages library)");
			}

			shaderLibraries.Add(variant, library);
			return library;
		}

		/// <summary>The part of the fragment stage (none when the program has none), for passes of a sample count.</summary>
		private VkPipeline FragmentShaderLibrary(int samples)
		{
			int variant = 8 | samples << 4;
			if (shaderLibraries.TryGetValue(variant, out VkPipeline library))
			{
				return library;
			}

			fixed (byte* main = "main\0"u8)
			{
				VkPipelineShaderStageCreateInfo fragment = default;
				int stageCount = 0;
				foreach (Stage stage in stages)
				{
					if (stage.Kind == ShaderStage.Fragment)
					{
						fragment = new VkPipelineShaderStageCreateInfo { Stage = stage.Flag, Module = stage.Module, Name = main };
						stageCount = 1;
					}
				}

				VkPipelineDepthStencilStateCreateInfo depthStencil = new VkPipelineDepthStencilStateCreateInfo();
				VkPipelineMultisampleStateCreateInfo multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = (VkSampleCountFlags)samples };
				VkDynamicState* dynamics = stackalloc VkDynamicState[3] { VkDynamicState.DepthTestEnable, VkDynamicState.DepthWriteEnable, VkDynamicState.DepthCompareOp };
				VkPipelineDynamicStateCreateInfo dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 3, DynamicStates = dynamics };
				VkGraphicsPipelineLibraryCreateInfoEXT part = new VkGraphicsPipelineLibraryCreateInfoEXT { Flags = VkGraphicsPipelineLibraryFlagsEXT.FragmentShaderBitEXT };
				VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
				{
					Next = &part,
					Flags = VulkanDevice.LibraryFlags,
					StageCount = stageCount,
					Stages = &fragment,
					DepthStencilState = &depthStencil,
					MultisampleState = &multisample,
					DynamicState = &dynamic,
					Layout = pipelineLayout
				};
				VulkanDevice.Check(device.Vk->vkCreateGraphicsPipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &library), "vkCreateGraphicsPipelines (" + Name + ", fragment stage library)");
			}

			shaderLibraries.Add(variant, library);
			return library;
		}

		/// <summary>The part of the vertex input: a layout's buffers and attributes for the vertex stage's inputs, and the kind of primitive.</summary>
		private VertexLibrary VertexInputLibrary(VertexInputLayout layout, int topologyClass)
		{
			long variant = (long)layout.Id << 8 | (uint)topologyClass;
			if (vertexLibraries.TryGetValue(variant, out VertexLibrary made))
			{
				return made;
			}

			bool tessellated = false;
			foreach (Stage stage in stages)
			{
				tessellated |= stage.Kind == ShaderStage.TessControl;
			}

			VkVertexInputBindingDescription* inputBindings = stackalloc VkVertexInputBindingDescription[layout.Buffers.Length + 1];
			VkVertexInputAttributeDescription* inputAttributes = stackalloc VkVertexInputAttributeDescription[layout.Attributes.Length + vertexInputs.Count];
			made.DefaultsBinding = DescribeVertexInput(layout, inputBindings, inputAttributes, out int bufferCount, out int attributeCount);
			VkPipelineVertexInputStateCreateInfo vertexInput = new VkPipelineVertexInputStateCreateInfo
			{
				VertexBindingDescriptionCount = bufferCount,
				VertexBindingDescriptions = inputBindings,
				VertexAttributeDescriptionCount = attributeCount,
				VertexAttributeDescriptions = inputAttributes
			};
			VkPrimitiveTopology topology = topologyClass == 0 ? VkPrimitiveTopology.TriangleList : topologyClass == 1 ? VkPrimitiveTopology.LineList : VkPrimitiveTopology.PointList;
			VkPipelineInputAssemblyStateCreateInfo inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = tessellated ? VkPrimitiveTopology.PatchList : topology };
			VkDynamicState topologyState = VkDynamicState.PrimitiveTopology;
			VkPipelineDynamicStateCreateInfo dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 1, DynamicStates = &topologyState };
			VkGraphicsPipelineLibraryCreateInfoEXT part = new VkGraphicsPipelineLibraryCreateInfoEXT { Flags = VkGraphicsPipelineLibraryFlagsEXT.VertexInputInterfaceBitEXT };
			VkGraphicsPipelineCreateInfo info = new VkGraphicsPipelineCreateInfo
			{
				Next = &part,
				Flags = VulkanDevice.LibraryFlags,
				VertexInputState = &vertexInput,
				InputAssemblyState = &inputAssembly,
				DynamicState = &dynamic
			};
			VkPipeline library;
			VulkanDevice.Check(device.Vk->vkCreateGraphicsPipelines.Invoke(device.Handle, device.PipelineCache, 1, &info, null, &library), "vkCreateGraphicsPipelines (" + Name + ", vertex input library)");
			made.Pipeline = library;
			vertexLibraries.Add(variant, made);
			return made;
		}

		private struct VertexLibrary
		{
			public VkPipeline Pipeline;
			public int DefaultsBinding;
		}
	}
}
