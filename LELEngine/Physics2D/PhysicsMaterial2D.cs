using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;

namespace LELEngine
{
	/// <summary>
	///     Surface properties of 2D colliders: friction and bounciness (restitution). An asset (.physicsMaterial2D
	///     document) shared by colliders, or created in code.
	/// </summary>
	public sealed class PhysicsMaterial2D : IAsset
	{
		#region PublicFields

		public const string Extension = ".physicsMaterial2D";

		public string AssetPath { get; internal set; }
		public string SubAssetName => null;
		public string name;

		/// <summary>Coefficient of friction (0 = ice, 1 = rough).</summary>
		[Range(0f, 1f)] public float friction = 0.4f;

		/// <summary>Restitution: how much velocity bounces back (0 = none, 1 = all).</summary>
		[Range(0f, 1f)] public float bounciness;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, PhysicsMaterial2D> cache = new Dictionary<string, PhysicsMaterial2D>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region Constructors

		public PhysicsMaterial2D()
		{ }

		public PhysicsMaterial2D(string name)
		{
			this.name = name;
		}

		#endregion

		#region PublicMethods

		/// <summary>Loads (or returns the cached) material at a root-relative path.</summary>
		public static PhysicsMaterial2D Load(string assetPath)
		{
			lock (cache)
			{
				PhysicsMaterial2D cached;
				if (cache.TryGetValue(assetPath, out cached))
				{
					return cached;
				}
			}

			PhysicsMaterial2D material;
			try
			{
				material = Documents.ReadObject(File.ReadAllBytes(AssetDatabase.ToAbsolute(assetPath)), typeof(PhysicsMaterial2D), new PhysicsMaterial2D()) as PhysicsMaterial2D;
			}
			catch (Exception e)
			{
				Debug.LogError($"[Physics2D] Cannot load {assetPath}: {e.Message}");
				material = new PhysicsMaterial2D();
			}

			material = material ?? new PhysicsMaterial2D();
			material.AssetPath = assetPath;
			material.name = Path.GetFileNameWithoutExtension(assetPath);
			lock (cache)
			{
				cache[assetPath] = material;
			}

			return material;
		}

		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The physics material has no asset path");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
			lock (cache)
			{
				cache[AssetPath] = this;
			}
		}

		public static void ClearCache()
		{
			lock (cache)
			{
				cache.Clear();
			}
		}

		/// <summary>A file or folder moved: the loaded materials keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (cache)
			{
				AssetDatabase.RekeyMoved(cache, fromPath, toPath, (material, path) => material.AssetPath = path);
			}
		}

		#endregion
	}
}
