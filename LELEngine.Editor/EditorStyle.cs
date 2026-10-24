using System;
using System.Collections.Generic;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Colors of the editor's own drawing (beside ImGui's), part of the theme.</summary>
	internal enum EditorColor
	{
		/// <summary>Highlight of active toggle buttons (play mode running).</summary>
		Accent,
		Selection,
		Warning,
		Error,

		/// <summary>The console's icon of plain messages.</summary>
		Info,
		Dim,

		/// <summary>Clickable file and line references (stack traces).</summary>
		Link,

		/// <summary>Names of prefab instances' objects (hierarchy), prefab icons, the override bar (Unity's prefab blue).</summary>
		Prefab,

		/// <summary>Prefab instance objects that are inactive.</summary>
		PrefabDim,

		/// <summary>Instances whose prefab file is missing.</summary>
		PrefabMissing,

		/// <summary>Objects and components added to an instance (the "+" badge).</summary>
		Added,

		/// <summary>Material sections in the inspector: the header's tint, the bar on the left, the frame.</summary>
		Material,

		/// <summary>Component headers in the inspector by kind (a bar on the left and a light tint): rigidbodies, colliders, joints.</summary>
		ComponentPhysics,

		/// <summary>Mesh renderers.</summary>
		ComponentRendering,

		/// <summary>Audio emitters, listeners, triggers, bank loaders.</summary>
		ComponentAudio,
		ComponentCamera,

		/// <summary>Lights.</summary>
		ComponentLighting,

		/// <summary>Nav mesh surfaces, agents, obstacles, modifiers.</summary>
		ComponentNavigation,

		/// <summary>Visual effects.</summary>
		ComponentEffects,

		/// <summary>UI documents.</summary>
		ComponentUI,
		CodeBackground,
		CodeText,
		CodeKeyword,

		/// <summary>if, for, return, ... (flow keywords).</summary>
		CodeControl,
		CodeType,
		CodeMethod,
		CodeString,
		CodeNumber,
		CodeComment,
		CodePreprocessor,
		CodeLineNumber,

		/// <summary>Background of the line a stack frame points at.</summary>
		CodeHighlight
	}

	/// <summary>
	///     The editor's theme: a preset (<see cref="Presets" />: ImGui's colors and the editor's own,
	///     <see cref="EditorColor" />) with the user's changes and text size from the preferences
	///     (<see cref="ThemePreferences" />). Every window applies it to its ImGui context (again when the preferences
	///     change: <see cref="ImGuiController" />).
	/// </summary>
	internal static class EditorStyle
	{
		#region PublicFields

		public static readonly string[] Presets = { "Dark", "Midnight", "Light", "Classic" };

		public static NVector4 Warning => Get(EditorColor.Warning);
		public static NVector4 Error => Get(EditorColor.Error);
		public static NVector4 Dim => Get(EditorColor.Dim);
		public static NVector4 Selection => Get(EditorColor.Selection);
		public static NVector4 Accent => Get(EditorColor.Accent);
		public static NVector4 Prefab => Get(EditorColor.Prefab);
		public static NVector4 PrefabDim => Get(EditorColor.PrefabDim);
		public static NVector4 PrefabMissing => Get(EditorColor.PrefabMissing);
		public static NVector4 Added => Get(EditorColor.Added);
		public static NVector4 Material => Get(EditorColor.Material);

		/// <summary>The preset is light (backgrounds bright): drawings pick darker colors.</summary>
		public static bool IsLight { get; private set; }

		#endregion

		#region PrivateFields

		private static readonly NVector4[] editorColors = new NVector4[Enum.GetValues<EditorColor>().Length];

		#endregion

		#region PublicMethods

		public static NVector4 Get(EditorColor color)
		{
			return editorColors[(int)color];
		}

		public static uint GetU32(EditorColor color, float alpha = 1f)
		{
			NVector4 c = editorColors[(int)color];
			return ImGui.ColorConvertFloat4ToU32(new NVector4(c.X, c.Y, c.Z, c.W * alpha));
		}

		/// <summary>A style color moved towards <paramref name="tint" /> (opaque, so a tinted header stands out in every theme).</summary>
		public static NVector4 Tinted(ImGuiCol color, NVector4 tint, float amount)
		{
			NVector4 baseColor = ImGui.GetStyle().Colors[(int)color];
			NVector4 mixed = NVector4.Lerp(baseColor, tint, amount);
			mixed.W = Math.Max(baseColor.W, 0.9f);
			return mixed;
		}

		/// <summary>The preferences' theme, applied to the current ImGui context.</summary>
		public static void Apply()
		{
			ThemePreferences theme = EditorPreferences.Current.Theme;
			ApplyPreset(theme.Preset);
			ImGuiStylePtr style = ImGui.GetStyle();
			foreach (KeyValuePair<string, string> entry in theme.Colors)
			{
				if (!ColorHex.TryParse(entry.Value, out NVector4 color))
				{
					continue;
				}

				if (Enum.TryParse(entry.Key, out EditorColor editorColor))
				{
					editorColors[(int)editorColor] = color;
				}
				else if (Enum.TryParse(entry.Key, out ImGuiCol imguiColor) && imguiColor >= 0 && imguiColor < ImGuiCol.Count)
				{
					style.Colors[(int)imguiColor] = color;
				}
			}

			style.FontSizeBase = theme.FontSize;
		}

		/// <summary>A preset's value of a color (what "Reset" goes back to); <paramref name="name" /> as in <see cref="ThemePreferences.Colors" />.</summary>
		public static NVector4 PresetValue(string preset, string name)
		{
			// Applied to a scratch copy: the current context's colors stay.
			ImGuiStylePtr style = ImGui.GetStyle();
			var saved = new NVector4[(int)ImGuiCol.Count];
			for (int i = 0; i < saved.Length; i++)
			{
				saved[i] = style.Colors[i];
			}

			var savedEditor = (NVector4[])editorColors.Clone();
			bool savedLight = IsLight;
			ApplyPreset(preset);
			NVector4 value = Enum.TryParse(name, out EditorColor editorColor) ? editorColors[(int)editorColor] : Enum.TryParse(name, out ImGuiCol imguiColor) ? style.Colors[(int)imguiColor] : default;
			for (int i = 0; i < saved.Length; i++)
			{
				style.Colors[i] = saved[i];
			}

			Array.Copy(savedEditor, editorColors, editorColors.Length);
			IsLight = savedLight;
			return value;
		}

		/// <summary>The current value of a color (<see cref="ThemePreferences.Colors" /> names).</summary>
		public static NVector4 Current(string name)
		{
			if (Enum.TryParse(name, out EditorColor editorColor))
			{
				return editorColors[(int)editorColor];
			}

			return Enum.TryParse(name, out ImGuiCol imguiColor) ? ImGui.GetStyle().Colors[(int)imguiColor] : default;
		}

		#endregion

		#region PrivateMethods

		private static void ApplyPreset(string preset)
		{
			ImGuiStylePtr style = ImGui.GetStyle();
			style.WindowRounding = 2f;
			style.FrameRounding = 2f;
			style.GrabRounding = 2f;
			style.TabRounding = 2f;
			style.ScrollbarRounding = 2f;
			style.WindowPadding = new NVector2(8, 6);
			style.FramePadding = new NVector2(6, 3);
			style.ItemSpacing = new NVector2(6, 4);
			style.IndentSpacing = 14f;

			switch (preset)
			{
				case "Light":
					Light();
					break;
				case "Midnight":
					Dark();
					Midnight();
					break;
				case "Classic":
					ImGui.StyleColorsClassic();
					DarkEditorColors();
					break;
				default:
					Dark();
					break;
			}
		}

		private static void Dark()
		{
			IsLight = false;
			ImGui.StyleColorsDark();
			Set(ImGuiCol.WindowBg, 0.15f, 0.15f, 0.16f);
			Set(ImGuiCol.ChildBg, 0.15f, 0.15f, 0.16f);
			Set(ImGuiCol.PopupBg, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.TitleBg, 0.11f, 0.11f, 0.12f);
			Set(ImGuiCol.TitleBgActive, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.MenuBarBg, 0.12f, 0.12f, 0.13f);
			Set(ImGuiCol.FrameBg, 0.22f, 0.22f, 0.23f);
			Set(ImGuiCol.FrameBgHovered, 0.28f, 0.28f, 0.3f);
			Set(ImGuiCol.FrameBgActive, 0.3f, 0.3f, 0.33f);
			Set(ImGuiCol.Header, 0.24f, 0.24f, 0.26f);
			Set(ImGuiCol.HeaderHovered, 0.3f, 0.3f, 0.33f);
			Set(ImGuiCol.HeaderActive, 0.17f, 0.36f, 0.6f);
			Set(ImGuiCol.Button, 0.25f, 0.25f, 0.27f);
			Set(ImGuiCol.ButtonHovered, 0.32f, 0.32f, 0.35f);
			Set(ImGuiCol.ButtonActive, 0.17f, 0.36f, 0.6f);
			Set(ImGuiCol.Tab, 0.13f, 0.13f, 0.14f);
			Set(ImGuiCol.TabHovered, 0.24f, 0.24f, 0.26f);
			Set(ImGuiCol.TabSelected, 0.18f, 0.18f, 0.19f);
			Set(ImGuiCol.TabDimmed, 0.12f, 0.12f, 0.13f);
			Set(ImGuiCol.TabDimmedSelected, 0.16f, 0.16f, 0.17f);
			Set(ImGuiCol.CheckMark, 0.4f, 0.65f, 1f);
			Set(ImGuiCol.SliderGrab, 0.4f, 0.6f, 0.9f);
			Set(ImGuiCol.DockingPreview, 0.25f, 0.5f, 0.85f);
			Set(ImGuiCol.TableRowBg, 0f, 0f, 0f, 0f);
			Set(ImGuiCol.TableRowBgAlt, 1f, 1f, 1f, 0.025f);
			DarkEditorColors();
		}

		/// <summary>Darker backgrounds, a violet-blue accent.</summary>
		private static void Midnight()
		{
			Set(ImGuiCol.WindowBg, 0.08f, 0.085f, 0.1f);
			Set(ImGuiCol.ChildBg, 0.08f, 0.085f, 0.1f);
			Set(ImGuiCol.PopupBg, 0.07f, 0.075f, 0.09f);
			Set(ImGuiCol.TitleBg, 0.05f, 0.055f, 0.065f);
			Set(ImGuiCol.TitleBgActive, 0.07f, 0.075f, 0.09f);
			Set(ImGuiCol.MenuBarBg, 0.06f, 0.065f, 0.08f);
			Set(ImGuiCol.FrameBg, 0.13f, 0.14f, 0.17f);
			Set(ImGuiCol.FrameBgHovered, 0.18f, 0.19f, 0.24f);
			Set(ImGuiCol.FrameBgActive, 0.21f, 0.22f, 0.28f);
			Set(ImGuiCol.Header, 0.15f, 0.16f, 0.21f);
			Set(ImGuiCol.HeaderHovered, 0.2f, 0.21f, 0.28f);
			Set(ImGuiCol.HeaderActive, 0.32f, 0.28f, 0.62f);
			Set(ImGuiCol.Button, 0.15f, 0.16f, 0.2f);
			Set(ImGuiCol.ButtonHovered, 0.21f, 0.22f, 0.29f);
			Set(ImGuiCol.ButtonActive, 0.32f, 0.28f, 0.62f);
			Set(ImGuiCol.Tab, 0.06f, 0.065f, 0.08f);
			Set(ImGuiCol.TabHovered, 0.18f, 0.18f, 0.26f);
			Set(ImGuiCol.TabSelected, 0.11f, 0.115f, 0.14f);
			Set(ImGuiCol.TabDimmed, 0.06f, 0.065f, 0.08f);
			Set(ImGuiCol.TabDimmedSelected, 0.09f, 0.095f, 0.12f);
			Set(ImGuiCol.CheckMark, 0.62f, 0.55f, 1f);
			Set(ImGuiCol.SliderGrab, 0.55f, 0.48f, 0.95f);
			Set(ImGuiCol.DockingPreview, 0.45f, 0.38f, 0.9f);
			Set(ImGuiCol.Border, 0.18f, 0.19f, 0.24f);
			Set(EditorColor.Accent, 0.36f, 0.3f, 0.72f);
			Set(EditorColor.Selection, 0.72f, 0.6f, 1f);
			Set(EditorColor.Link, 0.62f, 0.62f, 1f);
			Set(EditorColor.CodeBackground, 0.055f, 0.06f, 0.075f);
			Set(EditorColor.CodeHighlight, 0.32f, 0.28f, 0.62f, 0.42f);
		}

		private static void DarkEditorColors()
		{
			Set(EditorColor.Accent, 0.2f, 0.45f, 0.75f);
			Set(EditorColor.Selection, 1f, 0.6f, 0.15f);
			Set(EditorColor.Warning, 1f, 0.8f, 0.35f);
			Set(EditorColor.Error, 1f, 0.42f, 0.38f);
			Set(EditorColor.Info, 0.72f, 0.74f, 0.78f);
			Set(EditorColor.Dim, 0.55f, 0.55f, 0.58f);
			Set(EditorColor.Link, 0.45f, 0.7f, 1f);
			Set(EditorColor.Prefab, 0.48f, 0.71f, 1f);
			Set(EditorColor.PrefabDim, 0.36f, 0.49f, 0.66f);
			Set(EditorColor.PrefabMissing, 1f, 0.42f, 0.38f);
			Set(EditorColor.Added, 0.38f, 0.82f, 0.45f);
			Set(EditorColor.Material, 0.95f, 0.58f, 0.35f);
			Set(EditorColor.ComponentPhysics, 0.4f, 0.8f, 0.45f);
			Set(EditorColor.ComponentRendering, 0.4f, 0.62f, 1f);
			Set(EditorColor.ComponentAudio, 0.98f, 0.62f, 0.25f);
			Set(EditorColor.ComponentCamera, 0.4f, 0.85f, 0.9f);
			Set(EditorColor.ComponentLighting, 0.98f, 0.86f, 0.35f);
			Set(EditorColor.ComponentNavigation, 0.62f, 0.5f, 0.98f);
			Set(EditorColor.ComponentEffects, 0.98f, 0.45f, 0.7f);
			Set(EditorColor.ComponentUI, 0.85f, 0.6f, 0.98f);

			// Visual Studio Code's Dark+.
			Set(EditorColor.CodeBackground, 0.115f, 0.115f, 0.12f);
			Set(EditorColor.CodeText, 0.83f, 0.83f, 0.83f);
			Set(EditorColor.CodeKeyword, 0.34f, 0.61f, 0.84f);
			Set(EditorColor.CodeControl, 0.77f, 0.53f, 0.75f);
			Set(EditorColor.CodeType, 0.31f, 0.79f, 0.69f);
			Set(EditorColor.CodeMethod, 0.86f, 0.86f, 0.67f);
			Set(EditorColor.CodeString, 0.81f, 0.57f, 0.47f);
			Set(EditorColor.CodeNumber, 0.71f, 0.81f, 0.66f);
			Set(EditorColor.CodeComment, 0.42f, 0.6f, 0.33f);
			Set(EditorColor.CodePreprocessor, 0.61f, 0.61f, 0.61f);
			Set(EditorColor.CodeLineNumber, 0.52f, 0.52f, 0.52f);
			Set(EditorColor.CodeHighlight, 0.55f, 0.45f, 0.12f, 0.38f);
		}

		private static void Light()
		{
			IsLight = true;
			ImGui.StyleColorsLight();
			Set(ImGuiCol.WindowBg, 0.93f, 0.93f, 0.94f);
			Set(ImGuiCol.ChildBg, 0.93f, 0.93f, 0.94f);
			Set(ImGuiCol.PopupBg, 0.97f, 0.97f, 0.98f);
			Set(ImGuiCol.MenuBarBg, 0.86f, 0.86f, 0.88f);
			Set(ImGuiCol.TitleBg, 0.82f, 0.82f, 0.84f);
			Set(ImGuiCol.TitleBgActive, 0.78f, 0.78f, 0.8f);
			Set(ImGuiCol.FrameBg, 1f, 1f, 1f);
			Set(ImGuiCol.Header, 0.82f, 0.84f, 0.88f);
			Set(ImGuiCol.HeaderHovered, 0.74f, 0.8f, 0.9f);
			Set(ImGuiCol.HeaderActive, 0.55f, 0.7f, 0.92f);
			Set(ImGuiCol.Button, 0.84f, 0.84f, 0.86f);
			Set(ImGuiCol.ButtonHovered, 0.76f, 0.8f, 0.88f);
			Set(ImGuiCol.ButtonActive, 0.55f, 0.7f, 0.92f);
			Set(ImGuiCol.Tab, 0.84f, 0.84f, 0.86f);
			Set(ImGuiCol.TabSelected, 0.93f, 0.93f, 0.94f);
			Set(ImGuiCol.TabDimmed, 0.84f, 0.84f, 0.86f);
			Set(ImGuiCol.TabDimmedSelected, 0.9f, 0.9f, 0.91f);
			Set(ImGuiCol.TableRowBg, 0f, 0f, 0f, 0f);
			Set(ImGuiCol.TableRowBgAlt, 0f, 0f, 0f, 0.035f);
			Set(EditorColor.Accent, 0.36f, 0.58f, 0.9f);
			Set(EditorColor.Selection, 0.85f, 0.42f, 0f);
			Set(EditorColor.Warning, 0.68f, 0.44f, 0f);
			Set(EditorColor.Error, 0.8f, 0.14f, 0.1f);
			Set(EditorColor.Info, 0.35f, 0.37f, 0.42f);
			Set(EditorColor.Dim, 0.45f, 0.45f, 0.48f);
			Set(EditorColor.Link, 0.1f, 0.38f, 0.8f);
			Set(EditorColor.Prefab, 0.1f, 0.38f, 0.85f);
			Set(EditorColor.PrefabDim, 0.4f, 0.52f, 0.72f);
			Set(EditorColor.PrefabMissing, 0.8f, 0.14f, 0.1f);
			Set(EditorColor.Added, 0.1f, 0.55f, 0.2f);
			Set(EditorColor.Material, 0.85f, 0.42f, 0.12f);
			Set(EditorColor.ComponentPhysics, 0.15f, 0.6f, 0.25f);
			Set(EditorColor.ComponentRendering, 0.15f, 0.4f, 0.85f);
			Set(EditorColor.ComponentAudio, 0.9f, 0.5f, 0.05f);
			Set(EditorColor.ComponentCamera, 0.05f, 0.6f, 0.7f);
			Set(EditorColor.ComponentLighting, 0.8f, 0.65f, 0f);
			Set(EditorColor.ComponentNavigation, 0.42f, 0.3f, 0.85f);
			Set(EditorColor.ComponentEffects, 0.85f, 0.2f, 0.5f);
			Set(EditorColor.ComponentUI, 0.62f, 0.3f, 0.8f);

			// Visual Studio's light.
			Set(EditorColor.CodeBackground, 1f, 1f, 1f);
			Set(EditorColor.CodeText, 0.12f, 0.12f, 0.12f);
			Set(EditorColor.CodeKeyword, 0f, 0f, 1f);
			Set(EditorColor.CodeControl, 0.56f, 0f, 0.6f);
			Set(EditorColor.CodeType, 0.15f, 0.5f, 0.6f);
			Set(EditorColor.CodeMethod, 0.45f, 0.32f, 0.1f);
			Set(EditorColor.CodeString, 0.64f, 0.08f, 0.08f);
			Set(EditorColor.CodeNumber, 0.04f, 0.53f, 0.35f);
			Set(EditorColor.CodeComment, 0f, 0.5f, 0f);
			Set(EditorColor.CodePreprocessor, 0.5f, 0.5f, 0.5f);
			Set(EditorColor.CodeLineNumber, 0.17f, 0.57f, 0.69f);
			Set(EditorColor.CodeHighlight, 1f, 0.85f, 0.2f, 0.45f);
		}

		private static void Set(ImGuiCol color, float r, float g, float b, float a = 1f)
		{
			ImGui.GetStyle().Colors[(int)color] = new NVector4(r, g, b, a);
		}

		private static void Set(EditorColor color, float r, float g, float b, float a = 1f)
		{
			editorColors[(int)color] = new NVector4(r, g, b, a);
		}

		#endregion
	}
}
