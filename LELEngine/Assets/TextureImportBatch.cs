using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LELEngine
{
	/// <summary>
	///     The textures a scene load asks for, imported on the thread pool while the rest of the scene is read. Inside the
	///     batch (<see cref="Begin" />) <see cref="TextureAssets" /> makes each 2D texture from its file's header (size, color
	///     space, mipmaps: materials get the final object at once) and hands the reading and decoding to a worker (an image's
	///     pixels in the editor, a build's KTX file in a player). Disposing the batch waits for them and fills the textures,
	///     so a load still returns them complete; anything that needs a texture's texels sooner (its GL handle, its CPU
	///     pixels) waits for that one import (<see cref="Texture2D" />). One batch per thread: a batch begun inside another
	///     joins it.
	/// </summary>
	internal sealed class TextureImportBatch : IDisposable
	{
		#region PublicFields

		/// <summary>The batch open on this thread, or null.</summary>
		public static TextureImportBatch Current => current;

		#endregion

		#region PrivateFields

		[ThreadStatic] private static TextureImportBatch current;

		private readonly List<PendingTextureImport> imports = new List<PendingTextureImport>();

		// What Begin returns inside an open batch: its textures belong to the outer batch.
		private sealed class Joined : IDisposable
		{
			public static readonly Joined Instance = new Joined();

			public void Dispose()
			{ }
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens a batch on this thread (or joins the open one); dispose it to wait for its textures.</summary>
		public static IDisposable Begin()
		{
			if (current != null)
			{
				return Joined.Instance;
			}

			current = new TextureImportBatch();
			return current;
		}

		/// <summary>Waits for every import of the batch and fills the textures (on this thread).</summary>
		public void Dispose()
		{
			if (current == this)
			{
				current = null;
			}

			if (imports.Count == 0)
			{
				return;
			}

			long start = Stopwatch.GetTimestamp();
			foreach (PendingTextureImport import in imports)
			{
				import.Complete();
			}

			// The load's timing summary: what the reads waited for (the header reads are its "textures").
			AssetLoadScope.Current?.AddWait("textures on worker threads", imports.Count, Stopwatch.GetTimestamp() - start);
			imports.Clear();
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Starts <paramref name="work" /> on the thread pool for <paramref name="texture" />: it reads and decodes the
		///     file and returns what fills the texture with the result.
		/// </summary>
		internal PendingTextureImport Defer(Texture2D texture, string assetPath, Func<Action<Texture2D>> work)
		{
			var import = new PendingTextureImport(texture, assetPath, Task.Run(work));
			imports.Add(import);
			return import;
		}

		#endregion
	}

	/// <summary>One texture's import running on the thread pool (<see cref="TextureImportBatch" />).</summary>
	internal sealed class PendingTextureImport
	{
		#region PrivateFields

		private readonly Texture2D texture;
		private readonly string assetPath;
		private readonly Task<Action<Texture2D>> work;
		private readonly object sync = new object();
		private bool completed;

		#endregion

		#region Constructors

		public PendingTextureImport(Texture2D texture, string assetPath, Task<Action<Texture2D>> work)
		{
			this.texture = texture;
			this.assetPath = assetPath;
			this.work = work;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Waits for the import and fills the texture, once, on whichever thread asks first (the others wait for it). A
		///     texture given other texels meanwhile (reimport, SetPixels) keeps them; a failed import leaves it empty.
		/// </summary>
		public void Complete()
		{
			lock (sync)
			{
				if (completed)
				{
					return;
				}

				completed = true;
				if (texture.PendingImport != this)
				{
					return;
				}

				Action<Texture2D> fill;
				try
				{
					fill = work.GetAwaiter().GetResult();
				}
				catch (Exception e)
				{
					Debug.LogWarning("[Assets] Cannot import " + assetPath + ": " + e.Message);
					texture.ClearPendingImport();
					return;
				}

				fill(texture);
			}
		}

		#endregion
	}
}
