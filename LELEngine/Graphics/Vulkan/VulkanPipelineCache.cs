using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using LELEngine.Shaders;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     The pipeline cache of the device, kept on disk next to the program binaries
	///     (<see cref="ProgramBinaryCache.DriverFolder" />, a folder per driver): a later run's pipelines come out of what
	///     the driver compiled for an earlier one. Read when the first pipeline is made, written once pipelines stop
	///     coming (a second after the last one, at a present) and when a window's context goes.
	/// </summary>
	internal sealed unsafe partial class VulkanDevice
	{
		private const string PipelineCacheFileName = "pipelines.vkcache";

		private readonly object pipelineCacheSync = new object();
		private VkPipelineCache pipelineCache;

		/// <summary>The file the cache is kept in (null: none, no program binary cache).</summary>
		private string pipelineCacheFile;

		/// <summary>Pipelines made since the cache was read or written.</summary>
		private int pipelinesMade;

		private long lastPipelineMade;
		private Task pipelineCacheSave = Task.CompletedTask;

		/// <summary>The device's pipeline cache: made with the first pipeline, from its file when there is one.</summary>
		internal VkPipelineCache PipelineCache
		{
			get
			{
				VkPipelineCache cache = pipelineCache;
				if (!cache.IsNull())
				{
					return cache;
				}

				lock (pipelineCacheSync)
				{
					if (pipelineCache.IsNull())
					{
						pipelineCache = CreatePipelineCache();
					}

					return pipelineCache;
				}
			}
		}

		/// <summary>A pipeline was made (on any thread): the cache is written once they stop coming.</summary>
		internal void PipelineMade()
		{
			Interlocked.Increment(ref pipelinesMade);
			Volatile.Write(ref lastPipelineMade, Stopwatch.GetTimestamp());
		}

		/// <summary>Writes the cache in the background when pipelines were made, the last one more than a second ago.</summary>
		internal void SavePipelineCacheWhenQuiet()
		{
			if (pipelineCacheFile == null || Volatile.Read(ref pipelinesMade) == 0 || !pipelineCacheSave.IsCompleted)
			{
				return;
			}

			if (Stopwatch.GetTimestamp() - Volatile.Read(ref lastPipelineMade) < Stopwatch.Frequency)
			{
				return;
			}

			pipelineCacheSave = Task.Run(SavePipelineCache);
		}

		/// <summary>Writes the cache to its file when pipelines were made since it was read or last written.</summary>
		internal void SavePipelineCache()
		{
			lock (pipelineCacheSync)
			{
				if (pipelineCacheFile == null || pipelineCache.IsNull() || Interlocked.Exchange(ref pipelinesMade, 0) == 0)
				{
					return;
				}

				try
				{
					nuint size = 0;
					Check(Vk->vkGetPipelineCacheData.Invoke(Handle, pipelineCache, &size, null), "vkGetPipelineCacheData");
					byte[] data = new byte[(int)size];
					fixed (byte* pointer = data)
					{
						Check(Vk->vkGetPipelineCacheData.Invoke(Handle, pipelineCache, &size, pointer), "vkGetPipelineCacheData");
					}

					// Written aside and moved in: another instance never reads half a file.
					string temporary = pipelineCacheFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
					File.WriteAllBytes(temporary, data.AsSpan(0, (int)size).ToArray());
					File.Move(temporary, pipelineCacheFile, true);
				}
				catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
				{
					Console.WriteLine("[Vulkan] The pipeline cache could not be written: " + e.Message);
				}
			}
		}

		private VkPipelineCache CreatePipelineCache()
		{
			byte[] data = null;
			try
			{
				if (ProgramBinaryCache.Enabled)
				{
					pipelineCacheFile = Path.Combine(ProgramBinaryCache.DriverFolder, PipelineCacheFileName);
					if (File.Exists(pipelineCacheFile))
					{
						data = File.ReadAllBytes(pipelineCacheFile);
					}
				}
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Console.WriteLine("[Vulkan] The pipeline cache could not be read: " + e.Message);
				data = null;
			}

			VkPipelineCache cache;
			fixed (byte* initial = data)
			{
				// The driver checks the data's header (its own identity) and starts empty with data of another driver.
				VkPipelineCacheCreateInfo info = new VkPipelineCacheCreateInfo { InitialDataSize = (nuint)(data?.Length ?? 0), InitialData = initial };
				if (data == null || Vk->vkCreatePipelineCache.Invoke(Handle, &info, null, &cache) != VkResult.Success)
				{
					info = new VkPipelineCacheCreateInfo();
					Check(Vk->vkCreatePipelineCache.Invoke(Handle, &info, null, &cache), "vkCreatePipelineCache");
				}
			}

			return cache;
		}
	}
}
