using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>Preview images of texture assets for object fields, the picker and the inspector (cubemaps as a cross, volumes as a slice).</summary>
	internal static class AssetThumbnails
	{
		#region PrivateFields

		private static readonly Dictionary<string, Texture2D> previews = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		/// <summary>GL texture and texture coordinates showing an image asset (or a texture object) the right way up; false when there is none.</summary>
		public static bool TryGet(string assetPath, out int handle, out NVector2 uv0, out NVector2 uv1)
		{
			handle = 0;
			uv0 = NVector2.Zero;
			uv1 = NVector2.One;
			if (assetPath == null || !TextureAssets.IsImageFile(assetPath))
			{
				return false;
			}

			try
			{
				return TryGet(TextureAssets.LoadTexture(assetPath), out handle, out uv0, out uv1);
			}
			catch (Exception)
			{
				return false;
			}
		}

		public static bool TryGet(Texture texture, out int handle, out NVector2 uv0, out NVector2 uv1)
		{
			handle = 0;
			uv0 = NVector2.Zero;
			uv1 = NVector2.One;
			switch (texture)
			{
				case Texture2D texture2D:
					handle = texture2D.Handle;
					if (texture2D.bottomUp)
					{
						uv0 = new NVector2(0f, 1f);
						uv1 = new NVector2(1f, 0f);
					}

					return handle != 0;
				case Cubemap cubemap:
				{
					Texture2D preview = Preview(cubemap.AssetPath ?? cubemap.name, () => cubemap.CreatePreview());
					handle = preview?.Handle ?? 0;
					return handle != 0;
				}
				case Texture3D volume:
				{
					Texture2D preview = Preview(volume.AssetPath ?? volume.name, () => volume.GetSlice(volume.depth / 2));
					handle = preview?.Handle ?? 0;
					uv0 = new NVector2(0f, 1f);
					uv1 = new NVector2(1f, 0f);
					return handle != 0;
				}
				default:
					return false;
			}
		}

		/// <summary>Drops the cached preview of an asset (after a reimport).</summary>
		public static void Invalidate(string assetPath)
		{
			if (assetPath == null)
			{
				return;
			}

			Texture2D preview;
			if (previews.TryGetValue(assetPath, out preview))
			{
				preview.Release();
				previews.Remove(assetPath);
			}
		}

		public static void Clear()
		{
			foreach (Texture2D preview in previews.Values)
			{
				preview.Release();
			}

			previews.Clear();
		}

		#endregion

		#region PrivateMethods

		private static Texture2D Preview(string key, Func<Texture2D> create)
		{
			if (key == null)
			{
				return null;
			}

			Texture2D preview;
			if (!previews.TryGetValue(key, out preview))
			{
				preview = create();
				previews[key] = preview;
			}

			return preview;
		}

		#endregion
	}

	/// <summary>
	///     The "Select Asset" window of object fields (Unity's object picker): lists the project's assets of the
	///     field's type with a search box and thumbnails; a click assigns, a double click also closes.
	/// </summary>
	internal static class ObjectPicker
	{
		#region PrivateFields

		private struct Entry
		{
			public string Path;
			public string SubAsset;
			public string Label;
		}

		private static bool open;
		private static bool focusSearch;
		private static uint ownerKey;
		private static Type type;
		private static Func<string, bool> filter;
		private static string search = "";
		private static List<Entry> entries;
		private static int listedVersion = -1;
		private static object result;
		private static bool hasResult;
		private static string currentPath;
		private static string currentSubAsset;

		#endregion

		#region PublicMethods

		/// <summary>Opens the picker for an object field; results are collected with <see cref="TakeResult" />.</summary>
		public static void Open(uint key, Type assetType, object current, Func<string, bool> pathFilter)
		{
			ownerKey = key;
			type = assetType;
			filter = pathFilter;
			open = true;
			focusSearch = true;
			search = "";
			entries = null;
			hasResult = false;
			currentPath = (current as IAsset)?.AssetPath;
			currentSubAsset = (current as IAsset)?.SubAssetName;
		}

		/// <summary>True once per selection made for the field with this key (the value may be null: "None").</summary>
		public static bool TakeResult(uint key, out object value)
		{
			value = null;
			if (!hasResult || key != ownerKey)
			{
				return false;
			}

			hasResult = false;
			value = result;
			result = null;
			return true;
		}

		/// <summary>Draws the window when open (once per frame, from the editor's UI pass).</summary>
		public static void Draw(EditorApplication editor)
		{
			if (!open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(420, 480), ImGuiCond.FirstUseEver);
			ImGui.SetNextWindowPos(EditorGui.ViewportCenter(), ImGuiCond.FirstUseEver, new NVector2(0.5f, 0.5f));
			bool stillOpen = open;
			if (ImGui.Begin("Select " + TypeLabel(type) + "###object_picker", ref stillOpen))
			{
				try
				{
					DrawContent(editor);
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
			if (!stillOpen)
			{
				open = false;
			}
		}

		public static void Close()
		{
			open = false;
		}

		public static string TypeLabel(Type assetType)
		{
			if (assetType == null)
			{
				return "Asset";
			}

			return assetType == typeof(Shaders.ShaderProgram) ? "Shader" : assetType.Name;
		}

		#endregion

		#region PrivateMethods

		private static void DrawContent(EditorApplication editor)
		{
			if (focusSearch)
			{
				ImGui.SetKeyboardFocusHere();
				focusSearch = false;
			}

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##search", "Search", ref search, 128);
			if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
			{
				open = false;
			}

			if (entries == null || listedVersion != editor.AssetsVersion)
			{
				BuildEntries();
				listedVersion = editor.AssetsVersion;
			}

			ImGui.Separator();
			if (ImGui.BeginChild("##list", new NVector2(0, -ImGui.GetFrameHeightWithSpacing())))
			{
				float row = 36f;
				if (ImGui.Selectable("None", currentPath == null, ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, row)))
				{
					Pick(null, null, null);
					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						open = false;
					}
				}

				foreach (Entry entry in entries)
				{
					if (search.Length > 0 && entry.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					bool selected = string.Equals(entry.Path, currentPath, StringComparison.OrdinalIgnoreCase) && entry.SubAsset == currentSubAsset;
					NVector2 start = ImGui.GetCursorScreenPos();
					if (ImGui.Selectable("##" + entry.Label, selected, ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, row)))
					{
						Pick(entry.Path, entry.SubAsset, entry.Label);
						if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							open = false;
						}
					}

					if (ImGui.IsItemHovered())
					{
						ImGui.SetTooltip(entry.Path + (entry.SubAsset != null ? " / " + entry.SubAsset : ""));
					}

					// Thumbnail and label drawn over the selectable.
					NVector2 end = ImGui.GetCursorScreenPos();
					ImGui.SetCursorScreenPos(start + new NVector2(4f, 2f));
					int handle;
					NVector2 uv0;
					NVector2 uv1;
					if (AssetThumbnails.TryGet(entry.Path, out handle, out uv0, out uv1))
					{
						ImGui.Image(EditorGui.Texture(handle), new NVector2(row - 4f, row - 4f), uv0, uv1);
						ImGui.SameLine();
					}
					else
					{
						ImGui.Dummy(new NVector2(row - 4f, row - 4f));
						ImGui.SameLine();
					}

					ImGui.SetCursorPosY(ImGui.GetCursorPosY() + (row - 4f - ImGui.GetTextLineHeight()) * 0.5f);
					ImGui.TextUnformatted(entry.Label);
					ImGui.SetCursorScreenPos(end);
				}
			}

			ImGui.EndChild();
			ImGui.TextDisabled(entries.Count + " assets; click assigns, double click closes");
		}

		private static void BuildEntries()
		{
			entries = new List<Entry>();
			if (type == null)
			{
				return;
			}

			if (type == typeof(Shaders.ShaderProgram))
			{
				foreach (string path in AssetDatabase.FindAssets(type))
				{
					entries.Add(new Entry { Path = path, Label = Path.GetFileName(path) });
				}

				foreach (string engine in EngineShaders())
				{
					entries.Add(new Entry { Path = engine, Label = engine });
				}

				return;
			}

			foreach (string path in AssetDatabase.FindAssets(type))
			{
				if (filter != null && !filter(path))
				{
					continue;
				}

				if (type == typeof(Sprite))
				{
					IReadOnlyList<Sprite> sprites = TextureAssets.LoadSprites(path);
					foreach (Sprite sprite in sprites)
					{
						entries.Add(new Entry { Path = path, SubAsset = sprite.SubAssetName, Label = Path.GetFileName(path) + (sprite.SubAssetName != null ? " / " + sprite.SubAssetName : "") });
					}

					continue;
				}

				entries.Add(new Entry { Path = path, Label = Path.GetFileName(path) });
			}
		}

		/// <summary>Engine shaders a material can use (those declaring material properties), as engine-relative paths.</summary>
		public static List<string> EngineShaders()
		{
			var result = new List<string>();
			try
			{
				string folder = Path.Combine(AssetDatabase.EngineShadersRoot, "Engine");
				if (Directory.Exists(folder))
				{
					foreach (string file in Directory.GetFiles(folder, "*.shader"))
					{
						if (File.ReadAllText(file).Contains(Shaders.ShaderProgram.PropertiesTag))
						{
							result.Add("Engine/" + Path.GetFileName(file));
						}
					}
				}
			}
			catch (Exception)
			{
				// Engine folder unreadable: project shaders only.
			}

			result.Sort(StringComparer.OrdinalIgnoreCase);
			return result;
		}

		private static void Pick(string path, string subAsset, string label)
		{
			currentPath = path;
			currentSubAsset = subAsset;
			result = path != null ? AssetDatabase.Load(type, path, subAsset) : null;
			if (path != null && result == null)
			{
				Debug.LogWarning("[Editor] Cannot load " + label + " as " + TypeLabel(type));
				return;
			}

			hasResult = true;
		}

		#endregion
	}

	/// <summary>
	///     Unity-style object field: thumbnail and name of the referenced asset, a picker button and a drop target
	///     for files dragged from the project panel.
	/// </summary>
	internal static class AssetField
	{
		#region PublicMethods

		/// <summary>Draws the field in the current line (fills the available width); true when the value changed.</summary>
		public static bool Draw(Type type, ref object value, Func<string, bool> pathFilter = null)
		{
			bool changed = false;
			uint key = ImGui.GetID("##asset_field");
			float height = ImGui.GetFrameHeight();
			float buttonWidth = height * 1.4f;
			float width = ImGui.CalcItemWidth();
			if (width <= 0f)
			{
				width = ImGui.GetContentRegionAvail().X;
			}

			var asset = value as IAsset;
			string label = asset != null ? Path.GetFileName(asset.AssetPath ?? "runtime") + (asset.SubAssetName != null ? " / " + asset.SubAssetName : "") : "None (" + ObjectPicker.TypeLabel(type) + ")";

			// Framed label (looks like an input field).
			NVector2 start = ImGui.GetCursorScreenPos();
			float labelWidth = Math.Max(10f, width - buttonWidth - ImGui.GetStyle().ItemSpacing.X);
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(start, start + new NVector2(labelWidth, height), ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
			ImGui.InvisibleButton("##asset_label", new NVector2(labelWidth, height));
			bool hovered = ImGui.IsItemHovered();
			if (hovered && asset?.AssetPath != null)
			{
				ImGui.SetTooltip(asset.AssetPath);
			}

			float textX = start.X + 6f;
			int handle;
			NVector2 uv0;
			NVector2 uv1;
			if (value is Texture texture && AssetThumbnails.TryGet(texture, out handle, out uv0, out uv1))
			{
				float size = height - 4f;
				draw.AddImage(EditorGui.Texture(handle), start + new NVector2(2f, 2f), start + new NVector2(2f + size, 2f + size), uv0, uv1);
				textX += size + 2f;
			}
			else if (value is Sprite sprite && AssetThumbnails.TryGet(sprite.texture, out handle, out uv0, out uv1))
			{
				float size = height - 4f;
				OpenTK.Mathematics.Vector4 uv = sprite.uv;
				draw.AddImage(EditorGui.Texture(handle), start + new NVector2(2f, 2f), start + new NVector2(2f + size, 2f + size), new NVector2(uv.X, uv.W), new NVector2(uv.Z, uv.Y));
				textX += size + 2f;
			}

			uint textColor = ImGui.GetColorU32(asset != null ? ImGuiCol.Text : ImGuiCol.TextDisabled);
			draw.PushClipRect(start, start + new NVector2(labelWidth - 4f, height), true);
			draw.AddText(new NVector2(textX, start.Y + (height - ImGui.GetTextLineHeight()) * 0.5f), textColor, label);
			draw.PopClipRect();

			// Drop target: files from the project panel.
			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && ProjectPanel.DraggedAsset != null)
				{
					string path = ProjectPanel.DraggedAsset;
					if (AssetDatabase.AcceptsFile(type, path) && (pathFilter == null || pathFilter(path)))
					{
						object loaded = AssetDatabase.Load(type, path);
						if (loaded != null)
						{
							value = loaded;
							changed = true;
						}
					}
				}

				ImGui.EndDragDropTarget();
			}

			ImGui.SameLine();
			if (ImGui.Button("...##pick", new NVector2(buttonWidth, height)))
			{
				ObjectPicker.Open(key, type, value, pathFilter);
			}

			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Select " + ObjectPicker.TypeLabel(type));
			}

			object picked;
			if (ObjectPicker.TakeResult(key, out picked))
			{
				value = picked;
				changed = true;
			}

			return changed;
		}

		#endregion
	}
}
