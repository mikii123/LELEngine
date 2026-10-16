using System;

namespace LELEngine
{
	/// <summary>
	///     The sidecar document of an asset file ("file.ext.meta"): the GUID scenes reference the file by, and the
	///     import settings of asset types that have them (<see cref="TextureImporter" />). Written as a readable
	///     JSON document by the editor; a legacy meta holding only {"guid": "..."} reads as a meta without importer.
	/// </summary>
	[Serializable]
	public sealed class AssetMeta
	{
		#region PublicFields

		public string guid;

		/// <summary>How the file is imported; null for assets without settings (meshes, scripts, scenes).</summary>
		public AssetImporter importer;

		#endregion
	}

	/// <summary>Import settings stored in an asset's meta file; subclasses per asset type.</summary>
	[Serializable]
	public abstract class AssetImporter
	{
		#region PublicFields

		/// <summary>Bumped by the editor on every Apply, so loaded assets can tell their settings changed.</summary>
		public int version;

		#endregion
	}
}
