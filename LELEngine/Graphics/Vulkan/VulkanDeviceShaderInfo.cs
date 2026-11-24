using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     What the driver made of the programs, for finding out why a shader runs slower than it should: with
	///     LEL_VULKAN_SHADER_INFO set (1: the statistics in the console; a folder: also each stage's machine code there) on
	///     a GPU with VK_AMD_shader_info, every pipeline made prints its stages' registers (used / of the hardware), local
	///     memory and scratch memory (registers spilled), and the compute stages their work group size.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		/// <summary>The device reports its pipelines' statistics (LEL_VULKAN_SHADER_INFO on a GPU with VK_AMD_shader_info).</summary>
		internal bool HasShaderInfo { get; private set; }

		private string shaderInfoFolder;
		private readonly HashSet<string> shaderInfoWritten = new HashSet<string>(StringComparer.Ordinal);

		/// <summary>Asks for VK_AMD_shader_info when LEL_VULKAN_SHADER_INFO names what to report and the GPU has it.</summary>
		private void AskForShaderInfo(PhysicalDevice candidate, HashSet<string> extensions)
		{
			string value = Environment.GetEnvironmentVariable("LEL_VULKAN_SHADER_INFO");
			if (string.IsNullOrEmpty(value) || value == "0")
			{
				return;
			}

			if (!candidate.SupportsExtension("VK_AMD_shader_info"))
			{
				Console.WriteLine("[Vulkan] LEL_VULKAN_SHADER_INFO: this GPU has no VK_AMD_shader_info, nothing is reported.");
				return;
			}

			extensions.Add("VK_AMD_shader_info");
			HasShaderInfo = true;
			if (value != "1")
			{
				shaderInfoFolder = Path.GetFullPath(value);
				Directory.CreateDirectory(shaderInfoFolder);
			}
		}

		/// <summary>Prints what the driver made of one stage of a pipeline (and writes its machine code to the folder asked for).</summary>
		internal void ReportShaderInfo(VkPipeline pipeline, VkShaderStageFlags stage, string program, string variant)
		{
			if (!HasShaderInfo || Vk->vkGetShaderInfoAMD.Invoke == null)
			{
				return;
			}

			VkShaderStatisticsInfoAMD statistics = default;
			nuint size = (nuint)sizeof(VkShaderStatisticsInfoAMD);
			if (Vk->vkGetShaderInfoAMD.Invoke(Handle, pipeline, stage, VkShaderInfoTypeAMD.StatisticsAMD, &size, &statistics) != VkResult.Success)
			{
				return;
			}

			VkShaderResourceUsageAMD usage = statistics.ResourceUsage;
			string group = stage == VkShaderStageFlags.ComputeBit
				? $", work group {statistics.ComputeWorkGroupSize.X}x{statistics.ComputeWorkGroupSize.Y}x{statistics.ComputeWorkGroupSize.Z}"
				: "";
			Console.WriteLine($"[Vulkan] Shader info {program} {StageName(stage)}{variant}: VGPRs {usage.NumUsedVgprs} of {statistics.NumAvailableVgprs} ({statistics.NumPhysicalVgprs}), "
				+ $"SGPRs {usage.NumUsedSgprs} of {statistics.NumAvailableSgprs}, LDS {(ulong)usage.LdsUsageSizeInBytes} bytes, scratch {(ulong)usage.ScratchMemUsageInBytes} bytes{group}");

			if (shaderInfoFolder == null)
			{
				return;
			}

			string file = Path.Combine(shaderInfoFolder, Sanitize(program + "-" + StageName(stage) + variant) + ".txt");
			if (!shaderInfoWritten.Add(file))
			{
				return;
			}

			nuint length = 0;
			if (Vk->vkGetShaderInfoAMD.Invoke(Handle, pipeline, stage, VkShaderInfoTypeAMD.DisassemblyAMD, &length, null) != VkResult.Success || length == 0)
			{
				return;
			}

			byte[] text = new byte[(int)length];
			fixed (byte* bytes = text)
			{
				if (Vk->vkGetShaderInfoAMD.Invoke(Handle, pipeline, stage, VkShaderInfoTypeAMD.DisassemblyAMD, &length, bytes) != VkResult.Success)
				{
					return;
				}
			}

			int end = Array.IndexOf(text, (byte)0);
			File.WriteAllText(file, Encoding.UTF8.GetString(text, 0, end < 0 ? text.Length : end));
		}

		private static string StageName(VkShaderStageFlags stage)
		{
			switch (stage)
			{
				case VkShaderStageFlags.VertexBit: return "vertex";
				case VkShaderStageFlags.FragmentBit: return "fragment";
				case VkShaderStageFlags.GeometryBit: return "geometry";
				case VkShaderStageFlags.ComputeBit: return "compute";
				default: return stage.ToString();
			}
		}

		private static string Sanitize(string name)
		{
			StringBuilder builder = new StringBuilder(name.Length);
			foreach (char c in name)
			{
				builder.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_');
			}

			return builder.ToString();
		}
	}
}
