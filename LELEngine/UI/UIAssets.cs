using System;
using System.Collections.Generic;
using System.IO;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Loads and caches the UI's asset types by asset path: style sheets (.uss), UXML documents (.uxml), fonts
	///     (.ttf / .otf) and textures (.png / .jpg / .jpeg / .tga / .bmp). <see cref="AssetDatabase.Load" /> routes
	///     these types here; <see cref="Clear" /> drops everything when the project changes.
	/// </summary>
	public static class UIAssets
	{
		#region PublicFields

		public static readonly string[] StyleSheetExtensions = { ".uss" };
		public static readonly string[] VisualTreeExtensions = { ".uxml" };
		public static readonly string[] FontExtensions = { ".ttf", ".otf" };
		public static readonly string[] TextureExtensions = { ".png", ".jpg", ".jpeg", ".tga", ".bmp" };

		/// <summary>Raised after a cached asset was reloaded from its file (editor hot reload).</summary>
		public static event Action<object> AssetReloaded;

		#endregion

		#region PrivateFields

		private static readonly Dictionary<string, StyleSheet> styleSheets = new Dictionary<string, StyleSheet>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, VisualTreeAsset> visualTrees = new Dictionary<string, VisualTreeAsset>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, FontAsset> fonts = new Dictionary<string, FontAsset>(StringComparer.OrdinalIgnoreCase);
		private static readonly Dictionary<string, DateTime> fileTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		public static bool IsUIAssetType(Type type)
		{
			return type == typeof(StyleSheet) || type == typeof(VisualTreeAsset) || type == typeof(FontAsset) || type == typeof(Texture);
		}

		public static string[] ExtensionsOf(Type type)
		{
			if (type == typeof(StyleSheet)) return StyleSheetExtensions;
			if (type == typeof(VisualTreeAsset)) return VisualTreeExtensions;
			if (type == typeof(FontAsset)) return FontExtensions;
			if (type == typeof(Texture)) return TextureExtensions;
			return Array.Empty<string>();
		}

		public static object Load(Type type, string assetPath)
		{
			if (type == typeof(StyleSheet)) return LoadStyleSheet(assetPath);
			if (type == typeof(VisualTreeAsset)) return LoadVisualTree(assetPath);
			if (type == typeof(FontAsset)) return LoadFont(assetPath);
			if (type == typeof(Texture)) return LoadTexture(assetPath);
			return null;
		}

		/// <summary>Loads whatever the file is by its extension (url() in style sheets): a texture, a font or a sheet.</summary>
		public static object LoadResource(string assetPath)
		{
			if (string.IsNullOrEmpty(assetPath))
			{
				return null;
			}

			string extension = Path.GetExtension(assetPath).ToLowerInvariant();
			if (Array.IndexOf(TextureExtensions, extension) >= 0) return LoadTexture(assetPath);
			if (Array.IndexOf(FontExtensions, extension) >= 0) return LoadFont(assetPath);
			if (Array.IndexOf(StyleSheetExtensions, extension) >= 0) return LoadStyleSheet(assetPath);
			if (Array.IndexOf(VisualTreeExtensions, extension) >= 0) return LoadVisualTree(assetPath);
			return null;
		}

		public static StyleSheet LoadStyleSheet(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			StyleSheet sheet;
			lock (styleSheets)
			{
				if (styleSheets.TryGetValue(resolved, out sheet))
				{
					return sheet;
				}

				string absolute = AssetDatabase.ToAbsolute(resolved);
				try
				{
					sheet = StyleSheet.Parse(File.ReadAllText(absolute), resolved);
					styleSheets[resolved] = sheet;
					fileTimes[resolved] = File.GetLastWriteTimeUtc(absolute);
				}
				catch (Exception e)
				{
					Debug.LogWarning("[UI] Cannot load style sheet " + resolved + ": " + e.Message);
					return null;
				}
			}

			return sheet;
		}

		public static VisualTreeAsset LoadVisualTree(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			VisualTreeAsset asset;
			lock (visualTrees)
			{
				if (visualTrees.TryGetValue(resolved, out asset))
				{
					return asset;
				}

				string absolute = AssetDatabase.ToAbsolute(resolved);
				try
				{
					asset = VisualTreeAsset.Parse(File.ReadAllText(absolute), resolved);
					visualTrees[resolved] = asset;
					fileTimes[resolved] = File.GetLastWriteTimeUtc(absolute);
				}
				catch (Exception e)
				{
					Debug.LogWarning("[UI] Cannot load UXML " + resolved + ": " + e.Message);
					return null;
				}
			}

			return asset;
		}

		public static FontAsset LoadFont(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			FontAsset font;
			lock (fonts)
			{
				if (fonts.TryGetValue(resolved, out font))
				{
					return font;
				}

				font = FontAsset.LoadFromFile(AssetDatabase.ToAbsolute(resolved), resolved);
				if (font != null)
				{
					fonts[resolved] = font;
				}
			}

			return font;
		}

		/// <summary>An image for the UI (top-down, display color space); see <see cref="TextureAssets.LoadForUI" />.</summary>
		public static Texture LoadTexture(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			return TextureAssets.LoadForUI(AssetDatabase.ToAbsolute(resolved), resolved);
		}

		/// <summary>
		///     Re-reads style sheets and UXML files that changed on disk since they were loaded (editor hot reload).
		///     Returns true when something changed; documents then rebuild their trees.
		/// </summary>
		public static bool ReloadChanged()
		{
			bool changed = false;
			lock (styleSheets)
			{
				foreach (KeyValuePair<string, StyleSheet> pair in new List<KeyValuePair<string, StyleSheet>>(styleSheets))
				{
					string absolute = AssetDatabase.ToAbsolute(pair.Key);
					if (!File.Exists(absolute))
					{
						continue;
					}

					DateTime time = File.GetLastWriteTimeUtc(absolute);
					DateTime known;
					if (fileTimes.TryGetValue(pair.Key, out known) && known == time)
					{
						continue;
					}

					try
					{
						pair.Value.Reparse(File.ReadAllText(absolute));
						fileTimes[pair.Key] = time;
						changed = true;
						AssetReloaded?.Invoke(pair.Value);
					}
					catch (Exception e)
					{
						Debug.LogWarning("[UI] Cannot reload style sheet " + pair.Key + ": " + e.Message);
					}
				}
			}

			lock (visualTrees)
			{
				foreach (KeyValuePair<string, VisualTreeAsset> pair in new List<KeyValuePair<string, VisualTreeAsset>>(visualTrees))
				{
					string absolute = AssetDatabase.ToAbsolute(pair.Key);
					if (!File.Exists(absolute))
					{
						continue;
					}

					DateTime time = File.GetLastWriteTimeUtc(absolute);
					DateTime known;
					if (fileTimes.TryGetValue(pair.Key, out known) && known == time)
					{
						continue;
					}

					try
					{
						pair.Value.Reparse(File.ReadAllText(absolute));
						fileTimes[pair.Key] = time;
						changed = true;
						AssetReloaded?.Invoke(pair.Value);
					}
					catch (Exception e)
					{
						Debug.LogWarning("[UI] Cannot reload UXML " + pair.Key + ": " + e.Message);
					}
				}
			}

			return changed;
		}

		/// <summary>Tells the documents that a cached asset was changed in memory (the UI Builder's live edits).</summary>
		public static void NotifyReloaded(object asset)
		{
			AssetReloaded?.Invoke(asset);
		}

		/// <summary>Drops every cached asset (project closed) and releases GL textures.</summary>
		public static void Clear()
		{
			lock (styleSheets) styleSheets.Clear();
			lock (visualTrees) visualTrees.Clear();
			lock (fonts)
			{
				foreach (FontAsset font in fonts.Values)
				{
					font.Release();
				}

				fonts.Clear();
			}

			fileTimes.Clear();
		}

		#endregion

		#region PrivateMethods

		private static string Resolve(string assetPath)
		{
			if (string.IsNullOrWhiteSpace(assetPath))
			{
				return null;
			}

			return AssetDatabase.Resolve(assetPath, null) ?? (File.Exists(AssetDatabase.ToAbsolute(assetPath)) ? assetPath.Replace('\\', '/') : null);
		}

		#endregion
	}
}
