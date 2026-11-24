namespace LELEngine
{
	/// <summary>
	///     Baked files texture assets load from instead of their images (<see cref="TextureAssets.BakeSource" />): the
	///     editor's, which are the KTX files a game build ships (every mip level made, compressed as the import settings
	///     say), kept in the project's Library. Players have none: their data holds the baked files themselves.
	/// </summary>
	public interface ITextureBakeSource
	{
		/// <summary>
		///     The baked KTX file (absolute path) of a texture asset as its image and import settings are now, or null: the
		///     image loads as it is (a texture of a kind that is not baked, a bake that failed, or one not made yet).
		///     <paramref name="bakeNow" />: the caller is a load's worker thread and may wait for a missing bake that is
		///     quick to make; otherwise a missing or outdated bake is made in the background, and the texture, if loaded,
		///     is imported again from it once it is there. Any thread.
		/// </summary>
		string Find(string assetPath, TextureImporter importer, bool bakeNow);
	}
}
