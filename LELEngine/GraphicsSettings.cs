using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine
{
	/// <summary>
	///     An asset the project settings name: by its GUID, so it stays the same asset when its file is renamed or moved,
	///     and with the path it had when it was last seen (what the editor shows while the asset is missing, and how it is
	///     found again when its GUID is not known). Plain values: reading the settings loads nothing.
	/// </summary>
	[Serializable]
	public sealed class IncludedAsset
	{
		#region PublicFields

		public string Guid;

		/// <summary>Root-relative path when the asset was last seen.</summary>
		public string Path;

		#endregion

		#region PublicMethods

		/// <summary>The asset's root-relative path now, or null when it is gone (updates <see cref="Path" /> after a rename or a move).</summary>
		public string Resolve()
		{
			string path = string.IsNullOrEmpty(Guid) ? null : AssetDatabase.GetPath(Guid);
			if (path == null && !string.IsNullOrEmpty(Path) && File.Exists(AssetDatabase.ToAbsolute(Path)))
			{
				path = Path.Replace('\\', '/');
			}

			if (path != null)
			{
				Path = path;
			}

			return path;
		}

		#endregion
	}

	/// <summary>
	///     Project-wide graphics settings (Project Settings > Graphics): the shaders every build ships. A build ships its
	///     scenes, the Resources folders and everything those use: a shader nothing of that references is left out, and a
	///     script that loads it by its path finds nothing in the built game.
	/// </summary>
	[Serializable]
	public sealed class GraphicsSettings
	{
		#region PublicFields

		/// <summary>
		///     Shaders every build ships, with their #include files, although no material of the build uses them: the
		///     shaders scripts load by path (<c>new ShaderProgram("Shaders/Fog.shader")</c>). The build also checks that
		///     they compile, and the built game compiles them while it loads.
		/// </summary>
		public List<IncludedAsset> AlwaysIncludedShaders = new List<IncludedAsset>();

		#endregion

		#region PublicMethods

		/// <summary>The shader at this root-relative path is on the list.</summary>
		public bool IncludesShader(string assetPath)
		{
			return IndexOfShader(assetPath) >= 0;
		}

		/// <summary>Puts a shader on the list; false when it is there already (or the path is empty).</summary>
		public bool AddShader(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath) || IncludesShader(assetPath))
			{
				return false;
			}

			assetPath = assetPath.Replace('\\', '/');
			AlwaysIncludedShaders.Add(new IncludedAsset { Guid = AssetDatabase.GetGuid(assetPath), Path = assetPath });
			return true;
		}

		/// <summary>Takes a shader off the list; false when it was not on it.</summary>
		public bool RemoveShader(string assetPath)
		{
			int index = IndexOfShader(assetPath);
			if (index < 0)
			{
				return false;
			}

			AlwaysIncludedShaders.RemoveAt(index);
			return true;
		}

		#endregion

		#region PrivateMethods

		private int IndexOfShader(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return -1;
			}

			assetPath = assetPath.Replace('\\', '/');
			string guid = AssetDatabase.GetGuid(assetPath);
			for (int i = 0; i < AlwaysIncludedShaders.Count; i++)
			{
				IncludedAsset entry = AlwaysIncludedShaders[i];
				if (entry == null)
				{
					continue;
				}

				if ((guid != null && string.Equals(entry.Guid, guid, StringComparison.OrdinalIgnoreCase)) || string.Equals(entry.Resolve(), assetPath, StringComparison.OrdinalIgnoreCase))
				{
					return i;
				}
			}

			return -1;
		}

		#endregion
	}
}
