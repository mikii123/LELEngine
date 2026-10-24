using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
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
	///     The "Select" window of object fields (Unity's object picker): the scene objects (Scene tab) and the project
	///     assets (Assets tab) that fit the field's type, with a search box and thumbnails. An interface field lists
	///     the components implementing it and the ScriptableObject assets implementing it. A click assigns, a double
	///     click also closes.
	/// </summary>
	internal static class ObjectPicker
	{
		#region PrivateFields

		private struct Entry
		{
			public string Path;
			public string SubAsset;
			public string Label;
			public string Detail;
			public IconKind Icon;

			/// <summary>Scene entries: the GameObject or component.</summary>
			public object Object;
		}

		private static bool open;
		private static bool focusSearch;
		private static uint ownerKey;
		private static Type type;
		private static Scene scene;
		private static string search = "";
		private static List<Entry> assetEntries;
		private static List<Entry> sceneEntries;
		private static int listedVersion = -1;
		private static int listedSceneVersion = -1;
		private static bool showScene;
		private static bool selectSceneTab;
		private static object result;
		private static bool hasResult;
		private static object current;

		#endregion

		#region PublicMethods

		/// <summary>Opens the picker for an object field; results are collected with <see cref="TakeResult" />.</summary>
		public static void Open(uint key, Type declaredType, object currentValue, Scene currentScene)
		{
			ownerKey = key;
			type = declaredType;
			scene = currentScene;
			current = currentValue;
			open = true;
			focusSearch = true;
			search = "";
			assetEntries = null;
			sceneEntries = null;
			hasResult = false;
			// Scene objects first for fields of scene types (and for references already pointing into the scene).
			showScene = !ShowsAssets(declaredType) || currentValue is GameObject || currentValue is Behaviour;
			selectSceneTab = showScene;
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

			if (scene != null && scene != editor.Scene)
			{
				// The scene was replaced (play mode, a reload): its objects are not offered any more.
				Close();
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(440, 500), ImGuiCond.FirstUseEver);
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
				Close();
			}
		}

		public static void Close()
		{
			open = false;
			scene = null;
			current = null;
			sceneEntries = null;
			assetEntries = null;
		}

		public static string TypeLabel(Type assetType)
		{
			return ObjectField.TypeLabel(assetType);
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

		#endregion

		#region PrivateMethods

		/// <summary>Fields of this type take project assets (asset types, ScriptableObjects, interfaces).</summary>
		private static bool ShowsAssets(Type declaredType)
		{
			return declaredType != null && (SerializationUtility.IsAssetType(declaredType) || SerializationUtility.CanHoldReference(declaredType));
		}

		/// <summary>Fields of this type take scene objects (GameObjects, components, interfaces, EngineObject).</summary>
		private static bool ShowsScene(Type declaredType)
		{
			return declaredType != null && (SerializationUtility.IsReferenceType(declaredType) || SerializationUtility.CanHoldReference(declaredType));
		}

		private static void DrawContent(EditorApplication editor)
		{
			bool assets = ShowsAssets(type);
			bool sceneObjects = ShowsScene(type) && scene != null;
			if (assets && sceneObjects)
			{
				if (ImGui.BeginTabBar("##picker_tabs"))
				{
					if (ImGui.BeginTabItem("Assets", ImGuiTabItemFlags.None))
					{
						showScene = false;
						ImGui.EndTabItem();
					}

					if (ImGui.BeginTabItem("Scene", selectSceneTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None))
					{
						showScene = true;
						ImGui.EndTabItem();
					}

					selectSceneTab = false;
					ImGui.EndTabBar();
				}
			}
			else
			{
				showScene = sceneObjects;
			}

			if (focusSearch)
			{
				ImGui.SetKeyboardFocusHere();
				focusSearch = false;
			}

			ImGui.SetNextItemWidth(-1);
			ImGui.InputTextWithHint("##search", "Search", ref search, 128);
			if (ImGui.IsKeyPressed(ImGuiKey.Escape, false))
			{
				Close();
				return;
			}

			List<Entry> entries;
			if (showScene)
			{
				if (sceneEntries == null || listedSceneVersion != editor.SceneVersion)
				{
					sceneEntries = BuildSceneEntries();
					listedSceneVersion = editor.SceneVersion;
				}

				entries = sceneEntries;
			}
			else
			{
				if (assetEntries == null || listedVersion != editor.AssetsVersion)
				{
					assetEntries = BuildAssetEntries();
					listedVersion = editor.AssetsVersion;
				}

				entries = assetEntries;
			}

			ImGui.Separator();
			if (ImGui.BeginChild("##list", new NVector2(0, -ImGui.GetFrameHeightWithSpacing())))
			{
				float row = showScene ? ImGui.GetFrameHeight() + 4f : 36f;
				if (ImGui.Selectable("None", current == null, ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, row)))
				{
					Pick(default);
					if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						Close();
						return;
					}
				}

				for (int i = 0; i < entries.Count; i++)
				{
					Entry entry = entries[i];
					if (search.Length > 0 && entry.Label.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					bool selected = entry.Object != null ? ReferenceEquals(entry.Object, current) : current is IAsset asset && string.Equals(entry.Path, asset.AssetPath, StringComparison.OrdinalIgnoreCase) && entry.SubAsset == asset.SubAssetName;
					NVector2 start = ImGui.GetCursorScreenPos();
					if (ImGui.Selectable("##entry" + i, selected, ImGuiSelectableFlags.AllowDoubleClick, new NVector2(0, row)))
					{
						bool doubleClick = ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left);
						Pick(entry);
						if (doubleClick)
						{
							Close();
							return;
						}
					}

					if (ImGui.IsItemHovered() && entry.Detail != null)
					{
						ImGui.SetTooltip(entry.Detail);
					}

					// Thumbnail or icon, then the label, drawn over the selectable (no items: the layout stays the selectable's).
					float iconSize = row - 4f;
					int handle;
					NVector2 uv0;
					NVector2 uv1;
					ImDrawListPtr draw = ImGui.GetWindowDrawList();
					if (!showScene && AssetThumbnails.TryGet(entry.Path, out handle, out uv0, out uv1))
					{
						draw.AddImage(EditorGui.Texture(handle), start + new NVector2(4f, 2f), start + new NVector2(4f + iconSize, 2f + iconSize), uv0, uv1);
					}
					else
					{
						float small = Math.Min(iconSize, ImGui.GetTextLineHeight() + 2f);
						EditorIcons.Draw(draw, start + new NVector2(4f + (iconSize - small) * 0.5f, 2f + (iconSize - small) * 0.5f), small, entry.Icon, ImGui.GetColorU32(ImGuiCol.Text, 0.8f));
					}

					float textY = start.Y + (row - ImGui.GetTextLineHeight()) * 0.5f;
					draw.AddText(new NVector2(start.X + iconSize + 12f, textY), ImGui.GetColorU32(ImGuiCol.Text), entry.Label);
				}
			}

			ImGui.EndChild();
			ImGui.TextDisabled(entries.Count + (showScene ? " objects" : " assets") + "; click assigns, double click closes");
		}

		/// <summary>The scene's objects or components fitting the type, in hierarchy order.</summary>
		private static List<Entry> BuildSceneEntries()
		{
			var entries = new List<Entry>();
			if (scene == null || type == null)
			{
				return entries;
			}

			foreach (GameObject root in scene.GetRootGameObjects())
			{
				AddSceneEntries(root, entries);
			}

			return entries;
		}

		private static void AddSceneEntries(GameObject go, List<Entry> entries)
		{
			if (go.dead || (go.hideFlags & HideFlags.HideInHierarchy) != 0)
			{
				return;
			}

			string path = ObjectField.HierarchyPath(go);
			if (type.IsInstanceOfType(go))
			{
				entries.Add(new Entry { Object = go, Label = go.Name, Detail = path, Icon = IconKind.GameObject });
			}
			else
			{
				foreach (Behaviour component in go.Components)
				{
					if (!component.dead && type.IsInstanceOfType(component))
					{
						entries.Add(new Entry { Object = component, Label = go.Name + " (" + component.GetType().Name + ")", Detail = component.GetType().Name + " on " + path, Icon = IconKind.Component });
					}
				}
			}

			foreach (Transform child in go.transform.Children)
			{
				AddSceneEntries(child.gameObject, entries);
			}
		}

		/// <summary>The project's assets fitting the type (sub-assets listed one by one: sprites of sheets, meshes of models).</summary>
		private static List<Entry> BuildAssetEntries()
		{
			var entries = new List<Entry>();
			if (type == null || !ShowsAssets(type))
			{
				return entries;
			}

			if (type == typeof(Shaders.ShaderProgram))
			{
				foreach (string path in AssetDatabase.FindAssets(type))
				{
					entries.Add(new Entry { Path = path, Label = Path.GetFileName(path), Detail = path, Icon = IconKind.Asset });
				}

				foreach (string engine in EngineShaders())
				{
					entries.Add(new Entry { Path = engine, Label = engine, Detail = "Engine shader " + engine, Icon = IconKind.Asset });
				}

				return entries;
			}

			foreach (string path in AssetDatabase.FindAssets(type))
			{
				if (!DragAndDrop.AcceptsAssetFile(type, path))
				{
					continue;
				}

				if (type == typeof(Sprite))
				{
					IReadOnlyList<Sprite> sprites = TextureAssets.LoadSprites(path);
					foreach (Sprite sprite in sprites)
					{
						entries.Add(new Entry { Path = path, SubAsset = sprite.SubAssetName, Label = Path.GetFileName(path) + (sprite.SubAssetName != null ? " / " + sprite.SubAssetName : ""), Detail = path, Icon = IconKind.Asset });
					}

					continue;
				}

				if (type == typeof(Mesh))
				{
					// Every mesh of a model file (one per node with geometry).
					try
					{
						Model model = ModelAssets.Load(path);
						foreach (Mesh mesh in model.Meshes)
						{
							string label = model.Meshes.Count > 1 ? Path.GetFileName(path) + " / " + mesh.SubAssetName : Path.GetFileName(path);
							entries.Add(new Entry { Path = path, SubAsset = mesh.SubAssetName, Label = label, Detail = path, Icon = IconKind.Asset });
						}
					}
					catch (Exception e)
					{
						Debug.LogWarning($"[Editor] Cannot list the meshes of {path}: {e.Message}");
					}

					continue;
				}

				if (ScriptableObjectAssets.IsAssetFile(path))
				{
					string stored = ScriptableObjectAssets.GetStoredType(path)?.Name ?? "?";
					entries.Add(new Entry { Path = path, Label = Path.GetFileNameWithoutExtension(path) + "  (" + stored + ")", Detail = path, Icon = IconKind.ScriptableObject });
					continue;
				}

				entries.Add(new Entry { Path = path, Label = Path.GetFileName(path), Detail = path, Icon = path.EndsWith(Prefab.Extension, StringComparison.OrdinalIgnoreCase) ? IconKind.Prefab : IconKind.Asset });
			}

			AddBuiltinEntries(entries);
			return entries;
		}

		/// <summary>The engine's built-in assets of the type (primitive meshes and models, the default material): always there, read-only.</summary>
		private static void AddBuiltinEntries(List<Entry> entries)
		{
			bool meshes = type == typeof(Mesh);
			bool models = type == typeof(Model);
			bool materials = type == typeof(Shaders.Material);
			if (!meshes && !models && !materials)
			{
				return;
			}

			foreach (string path in AssetDatabase.FindEngineAssets(materials ? new[] { Shaders.Material.Extension } : ModelAssets.Extensions))
			{
				string label = Path.GetFileName(path) + "  (built-in)";
				if (!meshes)
				{
					entries.Add(new Entry { Path = path, Label = label, Detail = "Built-in " + path, Icon = IconKind.Asset });
					continue;
				}

				try
				{
					foreach (Mesh mesh in ModelAssets.Load(path).Meshes)
					{
						entries.Add(new Entry { Path = path, SubAsset = mesh.SubAssetName, Label = label, Detail = "Built-in " + path, Icon = IconKind.Asset });
					}
				}
				catch (Exception e)
				{
					Debug.LogWarning($"[Editor] Cannot list the meshes of {path}: {e.Message}");
				}
			}
		}

		private static void Pick(Entry entry)
		{
			if (entry.Object != null)
			{
				current = entry.Object;
				result = entry.Object;
				hasResult = true;
				return;
			}

			if (entry.Path == null)
			{
				current = null;
				result = null;
				hasResult = true;
				return;
			}

			object loaded = SerializationUtility.CanHoldReference(type)
				? AssetDatabase.LoadMainAsset(entry.Path, entry.SubAsset)
				: AssetDatabase.Load(type, entry.Path, entry.SubAsset);
			if (loaded == null || !type.IsInstanceOfType(loaded))
			{
				Debug.LogWarning("[Editor] Cannot load " + entry.Label + " as " + TypeLabel(type));
				return;
			}

			current = loaded;
			result = loaded;
			hasResult = true;
		}

		#endregion
	}

	/// <summary>
	///     Asset reference field of the editor's own inspectors (material textures, VFX properties, the splash logo):
	///     an <see cref="ObjectField" /> (drag and drop, picker, ping); image files are offered for the field's texture
	///     kind only.
	/// </summary>
	internal static class AssetField
	{
		#region PublicMethods

		/// <summary>Draws the field in the current line (fills the available width); true when the value changed.</summary>
		public static bool Draw(Type type, ref object value, Func<string, bool> pathFilter = null)
		{
			return ObjectField.Draw(type, ref value, null);
		}

		#endregion
	}
}
