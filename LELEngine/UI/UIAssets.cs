using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LELEngine.UIElements
{
	/// <summary>
	///     Loads and caches the UI's asset types by asset path: style sheets (.uss), UXML documents (.uxml), fonts
	///     (.ttf / .otf) and textures (.png / .jpg / .jpeg / .tga / .bmp). <see cref="AssetDatabase.Load" /> routes
	///     these types here; <see cref="Clear" /> drops everything when the project changes. Loads may run on any thread
	///     (background loads read UI on the loading thread): files are parsed outside the caches' locks and the first
	///     result stored wins.
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

		// Files being parsed on this thread: a sheet importing itself or a document using itself as a template (directly
		// or through others) finds nothing instead of parsing forever.
		[ThreadStatic] private static HashSet<string> parsing;

		/// <summary>Font faces a loaded UI asks for besides the regular one (glyph warm-up).</summary>
		[Flags]
		private enum FaceStyles
		{
			None = 0,
			Bold = 1,
			Italic = 2,
			BoldItalic = 4
		}

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

		/// <summary>True when the text names a file <see cref="LoadResource" /> loads (by its extension).</summary>
		public static bool IsResourceFile(string text)
		{
			if (string.IsNullOrWhiteSpace(text) || text.IndexOfAny(new[] { '\n', '\r', '<', '>', '"' }) >= 0)
			{
				return false;
			}

			string extension = Path.GetExtension(text.Trim()).ToLowerInvariant();
			return extension.Length > 1 && (Array.IndexOf(TextureExtensions, extension) >= 0 || Array.IndexOf(FontExtensions, extension) >= 0 ||
				Array.IndexOf(StyleSheetExtensions, extension) >= 0 || Array.IndexOf(VisualTreeExtensions, extension) >= 0);
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

			lock (styleSheets)
			{
				if (styleSheets.TryGetValue(resolved, out StyleSheet cached))
				{
					return cached;
				}
			}

			if (!BeginParse(resolved))
			{
				Debug.LogWarning("[UI] Style sheet " + resolved + " imports itself (through the sheets it imports)");
				return null;
			}

			StyleSheet sheet;
			DateTime time;
			try
			{
				string absolute = AssetDatabase.ToAbsolute(resolved);
				time = File.GetLastWriteTimeUtc(absolute);
				sheet = StyleSheet.Parse(File.ReadAllText(absolute), resolved);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot load style sheet " + resolved + ": " + e.Message);
				return null;
			}
			finally
			{
				parsing.Remove(resolved);
			}

			lock (styleSheets)
			{
				if (styleSheets.TryGetValue(resolved, out StyleSheet raced))
				{
					return raced;
				}

				styleSheets[resolved] = sheet;
			}

			SetFileTime(resolved, time);
			return sheet;
		}

		public static VisualTreeAsset LoadVisualTree(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			lock (visualTrees)
			{
				if (visualTrees.TryGetValue(resolved, out VisualTreeAsset cached))
				{
					return cached;
				}
			}

			if (!BeginParse(resolved))
			{
				Debug.LogWarning("[UI] UXML " + resolved + " uses itself as a template (through the templates it uses)");
				return null;
			}

			VisualTreeAsset asset;
			DateTime time;
			try
			{
				string absolute = AssetDatabase.ToAbsolute(resolved);
				time = File.GetLastWriteTimeUtc(absolute);
				asset = VisualTreeAsset.Parse(File.ReadAllText(absolute), resolved);
			}
			catch (Exception e)
			{
				Debug.LogWarning("[UI] Cannot load UXML " + resolved + ": " + e.Message);
				return null;
			}
			finally
			{
				parsing.Remove(resolved);
			}

			lock (visualTrees)
			{
				if (visualTrees.TryGetValue(resolved, out VisualTreeAsset raced))
				{
					return raced;
				}

				visualTrees[resolved] = asset;
			}

			SetFileTime(resolved, time);
			return asset;
		}

		public static FontAsset LoadFont(string assetPath)
		{
			string resolved = Resolve(assetPath);
			if (resolved == null)
			{
				return null;
			}

			lock (fonts)
			{
				if (fonts.TryGetValue(resolved, out FontAsset cached))
				{
					return cached;
				}
			}

			FontAsset font = FontAsset.LoadFromFile(AssetDatabase.ToAbsolute(resolved), resolved);
			if (font == null)
			{
				return null;
			}

			lock (fonts)
			{
				if (fonts.TryGetValue(resolved, out FontAsset raced))
				{
					return raced;
				}

				fonts[resolved] = font;
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
					if (IsFileTime(pair.Key, time))
					{
						continue;
					}

					try
					{
						pair.Value.Reparse(File.ReadAllText(absolute));
						SetFileTime(pair.Key, time);
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
					if (IsFileTime(pair.Key, time))
					{
						continue;
					}

					try
					{
						pair.Value.Reparse(File.ReadAllText(absolute));
						SetFileTime(pair.Key, time);
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

		/// <summary>A file or folder moved: the loaded sheets, documents and fonts keep their identity under the new path.</summary>
		internal static void AssetMoved(string fromPath, string toPath)
		{
			lock (styleSheets) AssetDatabase.RekeyMoved(styleSheets, fromPath, toPath, (sheet, path) => sheet.AssetPath = path);
			lock (visualTrees) AssetDatabase.RekeyMoved(visualTrees, fromPath, toPath, (tree, path) => tree.AssetPath = path);
			lock (fonts) AssetDatabase.RekeyMoved(fonts, fromPath, toPath, (font, path) => font.AssetPath = path);
			lock (fileTimes) AssetDatabase.RekeyMoved(fileTimes, fromPath, toPath);
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

			lock (fileTimes) fileTimes.Clear();
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     Loading thread, after a background load read its assets: when they hold UI (documents, style sheets, fonts),
		///     rasterizes the glyphs it will show into the fonts' atlases (printable ASCII and every character of the
		///     documents' attribute values: texts, labels), loads the system fonts its style sheets name and resolves the
		///     documents' element types, so the first frame showing the UI only uploads the atlases. The fonts are added to
		///     <paramref name="assets" /> (their atlas pages upload while the load is prepared). Returns the number of fonts
		///     warmed up.
		/// </summary>
		internal static int WarmUp(List<object> assets)
		{
			var used = new List<FontAsset>();
			var trees = new HashSet<VisualTreeAsset>();
			var codepoints = new HashSet<int>();
			var text = new StringBuilder(FontAsset.BasicCharacters);
			bool hasUI = false;
			FaceStyles styles = FaceStyles.None;
			foreach (object asset in assets)
			{
				switch (asset)
				{
					case FontAsset font:
						AddFont(used, font);
						break;
					case StyleSheet sheet:
						hasUI = true;
						ScanSheet(sheet, used, ref styles);
						break;
					case VisualTreeAsset tree:
						hasUI = true;
						tree.WarmUp(value =>
						{
							AddCharacters(value, codepoints, text);
							styles |= StylesIn(value);
						}, trees);
						break;
				}
			}

			if (hasUI)
			{
				AddFont(used, FontAsset.Default);
			}

			string characters = text.ToString();
			foreach (FontAsset font in used)
			{
				font.WarmUp(characters);
				// The faces the UI asks for (synthesized from this face when the font has none).
				if ((styles & FaceStyles.Bold) != 0) font.boldVariant?.WarmUp(characters);
				if ((styles & FaceStyles.Italic) != 0) font.italicVariant?.WarmUp(characters);
				if ((styles & FaceStyles.BoldItalic) != 0) font.boldItalicVariant?.WarmUp(characters);
				if (!assets.Contains(font))
				{
					assets.Add(font);
				}
			}

			return used.Count;
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

		private static bool BeginParse(string resolved)
		{
			parsing ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			return parsing.Add(resolved);
		}

		private static void SetFileTime(string resolved, DateTime time)
		{
			lock (fileTimes)
			{
				fileTimes[resolved] = time;
			}
		}

		private static bool IsFileTime(string resolved, DateTime time)
		{
			lock (fileTimes)
			{
				return fileTimes.TryGetValue(resolved, out DateTime known) && known == time;
			}
		}

		private static void AddFont(List<FontAsset> used, FontAsset font)
		{
			if (font != null && !used.Contains(font))
			{
				used.Add(font);
			}
		}

		private static void AddCharacters(string value, HashSet<int> codepoints, StringBuilder text)
		{
			if (string.IsNullOrEmpty(value))
			{
				return;
			}

			for (int i = 0; i < value.Length; i++)
			{
				char c = value[i];
				if (c >= 32 && c < 127)
				{
					// Printable ASCII is warmed up anyway.
					continue;
				}

				if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
				{
					if (codepoints.Add(char.ConvertToUtf32(c, value[i + 1])))
					{
						text.Append(c).Append(value[i + 1]);
					}

					i++;
				}
				else if (!char.IsSurrogate(c) && c >= 32 && codepoints.Add(c))
				{
					text.Append(c);
				}
			}
		}

		/// <summary>The fonts a sheet names (system fonts by family are loaded here) and the bold / italic styles it sets.</summary>
		private static void ScanSheet(StyleSheet sheet, List<FontAsset> used, ref FaceStyles styles)
		{
			foreach (StyleRule rule in sheet.rules)
			{
				if (rule.declarations == null)
				{
					continue;
				}

				foreach (StyleDeclaration declaration in rule.declarations)
				{
					if (declaration.values == null || declaration.values.Length == 0)
					{
						continue;
					}

					StyleValue first = declaration.values[0];
					switch (declaration.id)
					{
						case StylePropertyId.UnityFont:
						case StylePropertyId.UnityFontDefinition:
							if (first.reference is FontAsset font)
							{
								AddFont(used, font);
							}
							else if (first.type == StyleValueType.String || first.type == StyleValueType.Identifier)
							{
								AddFont(used, FontAsset.FindSystemFont(first.text));
							}

							break;
						case StylePropertyId.UnityFontStyleAndWeight:
							if (first.type == StyleValueType.Identifier)
							{
								styles |= FaceStyleOf(first.text);
							}

							break;
					}
				}
			}
		}

		/// <summary>The faces an attribute value asks for: a style attribute's -unity-font-style, rich text tags.</summary>
		private static FaceStyles StylesIn(string value)
		{
			if (string.IsNullOrEmpty(value))
			{
				return FaceStyles.None;
			}

			FaceStyles styles = FaceStyles.None;
			int property = value.IndexOf("-unity-font-style", StringComparison.OrdinalIgnoreCase);
			if (property >= 0)
			{
				int colon = value.IndexOf(':', property);
				int end = colon >= 0 ? value.IndexOf(';', colon) : -1;
				if (colon >= 0)
				{
					styles |= FaceStyleOf(value.Substring(colon + 1, (end >= 0 ? end : value.Length) - colon - 1).Trim());
				}
			}

			bool bold = value.IndexOf("<b>", StringComparison.OrdinalIgnoreCase) >= 0;
			bool italic = value.IndexOf("<i>", StringComparison.OrdinalIgnoreCase) >= 0;
			if (bold) styles |= FaceStyles.Bold;
			if (italic) styles |= FaceStyles.Italic;
			if (bold && italic) styles |= FaceStyles.BoldItalic;
			return styles;
		}

		private static FaceStyles FaceStyleOf(string value)
		{
			switch (value?.ToLowerInvariant())
			{
				case "bold": return FaceStyles.Bold;
				case "italic": return FaceStyles.Italic;
				case "bold-and-italic": return FaceStyles.BoldItalic;
				default: return FaceStyles.None;
			}
		}

		#endregion
	}
}
