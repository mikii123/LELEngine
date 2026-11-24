using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's texture assets from the bakes game builds ship (<see cref="TextureAssets.BakeSource" />): an image a
	///     build bakes loads from its KTX file in Library/TextureCache, made by <see cref="TextureBaker" /> with the
	///     asset's import settings in its meta file (compression, maximum size, color space, mip levels), so the editor
	///     holds and draws the texels the game does. BC7 takes a quarter of RGBA8's memory and comes with its levels made,
	///     which the GPU no longer generates. A bake is made once per image and settings and serves builds too; a change
	///     of the image or of its meta file makes it again.
	///     <para>
	///         A missing or outdated bake is made on the worker thread of the scene load that asks for it; for the high
	///         quality setting (minutes for a large image) and for loads on the main thread (a reimport, an inspector) it
	///         is made in the background while the image loads as it is, and the loaded texture is imported again from
	///         the bake once it is there (<see cref="Update" />).
	///     </para>
	/// </summary>
	internal sealed class EditorTextureBakes : ITextureBakeSource
	{
		private readonly ProjectInfo project;
		private readonly object sync = new object();
		private readonly Queue<string> queue = new Queue<string>();
		private readonly HashSet<string> queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		private readonly ConcurrentQueue<string> baked = new ConcurrentQueue<string>();

		// Bakes that failed, by asset path, with the stamp of its files then: tried again when they change.
		private readonly ConcurrentDictionary<string, long> failed = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
		private bool running;
		private bool closed;

		public EditorTextureBakes(ProjectInfo project)
		{
			this.project = project;
		}

		public string Find(string assetPath, TextureImporter importer, bool bakeNow)
		{
			if (closed || !Bakes(assetPath, importer))
			{
				return null;
			}

			try
			{
				string current = TextureBaker.CurrentBake(project, assetPath);
				if (current != null)
				{
					return current;
				}

				if (failed.TryGetValue(assetPath, out long stamp) && stamp == FilesStamp(assetPath))
				{
					return null;
				}

				if (bakeNow && importer.compression != TextureCompression.BC7HighQuality)
				{
					long started = System.Diagnostics.Stopwatch.GetTimestamp();
					string file = TextureBaker.BakeFile(project, assetPath, importer, out bool fromCache);
					if (!fromCache)
					{
						Debug.Log($"[Textures] Baked {assetPath} ({importer.compression}) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: the editor and builds load it from the Library");
					}

					return file;
				}
			}
			catch (Exception e)
			{
				Fail(assetPath, e);
				return null;
			}

			Enqueue(assetPath);
			return null;
		}

		/// <summary>Main thread, every editor frame: the loaded textures whose bake the background made are imported again from it.</summary>
		public void Update()
		{
			while (baked.TryDequeue(out string assetPath))
			{
				if (!closed)
				{
					TextureAssets.Reimport(assetPath);
				}
			}
		}

		/// <summary>The project closes: queued bakes are dropped (one being made finishes into its Library).</summary>
		public void Close()
		{
			lock (sync)
			{
				closed = true;
				queue.Clear();
				queued.Clear();
			}
		}

		/// <summary>
		///     The images a build bakes, but for HDR images and volumes, whose bakes hold what their images give already
		///     (RGBA16F, RGBA8): sprites stay images in builds (the UI draws them as they are).
		/// </summary>
		private static bool Bakes(string assetPath, TextureImporter importer)
		{
			return importer.textureType != TextureImporterType.Sprite
				&& importer.shape != TextureImporterShape.Texture3D
				&& !assetPath.EndsWith(".hdr", StringComparison.OrdinalIgnoreCase);
		}

		private void Enqueue(string assetPath)
		{
			lock (sync)
			{
				if (closed || !queued.Add(assetPath))
				{
					return;
				}

				queue.Enqueue(assetPath);
				if (!running)
				{
					running = true;
					Task.Run(BakeQueued);
				}
			}
		}

		/// <summary>One background thread bakes the queue in turn (each bake spreads its blocks over the cores itself).</summary>
		private void BakeQueued()
		{
			while (true)
			{
				string assetPath;
				lock (sync)
				{
					if (closed || queue.Count == 0)
					{
						running = false;
						return;
					}

					assetPath = queue.Dequeue();
				}

				try
				{
					TextureImporter importer = TextureAssets.GetImporter(assetPath);
					if (File.Exists(AssetDatabase.ToAbsolute(assetPath)) && Bakes(assetPath, importer))
					{
						long started = System.Diagnostics.Stopwatch.GetTimestamp();
						TextureBaker.BakeFile(project, assetPath, importer, out bool fromCache);
						if (!fromCache)
						{
							Debug.Log($"[Textures] Baked {assetPath} ({importer.compression}) in the background in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} ms: the editor and builds load it from the Library");
						}

						baked.Enqueue(assetPath);
					}
				}
				catch (Exception e)
				{
					Fail(assetPath, e);
				}
				finally
				{
					lock (sync)
					{
						queued.Remove(assetPath);
					}
				}
			}
		}

		private void Fail(string assetPath, Exception e)
		{
			long stamp = FilesStamp(assetPath);
			if (!failed.TryGetValue(assetPath, out long known) || known != stamp)
			{
				Debug.LogWarning($"[Textures] Cannot bake {assetPath} ({e.Message}): the editor loads the image as it is");
			}

			failed[assetPath] = stamp;
		}

		/// <summary>The image's and its meta file's sizes and times, folded: a failed bake is tried again once they change.</summary>
		private static long FilesStamp(string assetPath)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			var image = new FileInfo(absolute);
			var meta = new FileInfo(absolute + AssetDatabase.MetaExtension);
			long stamp = 17;
			stamp = stamp * 31 + (image.Exists ? image.Length ^ image.LastWriteTimeUtc.Ticks : 0);
			stamp = stamp * 31 + (meta.Exists ? meta.Length ^ meta.LastWriteTimeUtc.Ticks : 0);
			return stamp;
		}
	}
}
