namespace LELEngine
{
	/// <summary>
	///     A store of import results shared between machines (the editor's library cache server): what one editor made
	///     (a model's import, an image's bake, a mesh's distance field) another takes instead of making it again. Results
	///     are kept under a kind ("models", "textures", ...) and a key computed from everything the result depends on (the
	///     source file's content, its import settings, the importer's version), never from paths or file times, so the
	///     same inputs give the same key on every machine. Players never have one.
	/// </summary>
	public interface IImportResultCache
	{
		/// <summary>The store is set up to give results (each fetch can still miss).</summary>
		bool CanFetch { get; }

		/// <summary>The store is set up to take results.</summary>
		bool CanOffer { get; }

		/// <summary>
		///     Writes the result stored under <paramref name="kind" /> / <paramref name="key" /> to <paramref name="file" />
		///     (whole or not at all, its bytes checked against the store's hash); false when there is none or the store
		///     cannot be reached. Any thread; it waits for the transfer.
		/// </summary>
		bool TryFetch(string kind, string key, string file);

		/// <summary>
		///     Offers <paramref name="file" />, a result just made here, for <paramref name="kind" /> / <paramref name="key" />:
		///     queued and sent in the background (not when the store has it already). Any thread; returns at once.
		/// </summary>
		void Offer(string kind, string key, string file);
	}
}
