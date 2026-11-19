using System;
using Hexa.NET.ImGui;
using LELEngine.Rendering.Impostors;
using LELEngine.Shaders;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     Below an <see cref="Impostor" /> asset's fields: the Bake button (the source drawn from every view into the
	///     atlases, written as files: <see cref="ImpostorAssets" />), what the last bake made (views, atlas size,
	///     triangles), the files it wrote and a look at an atlas with the empty texels of every view dimmed.
	/// </summary>
	internal static class ImpostorInspector
	{
		#region PrivateFields

		private static readonly string[] AtlasNames = { "Base", "Shape", "Specular", "Emission" };

		// The atlas shown and its picture: a copy at 512 texels at most, opaque (an atlas keeps data in its alpha),
		// made again when another asset is shown or anything was baked.
		private static int shownAtlas;
		private static Texture2D preview;
		private static Texture2D previewOf;
		private static int previewVersion = -1;

		#endregion

		#region PublicMethods

		public static void Draw(Impostor impostor, InspectorContext context)
		{
			// Under the settings' own "Bake" header.
			EditorApplication editor = context.Editor;
			ImGui.PushID("impostor");
			string reason = ImpostorAssets.WhyNot(impostor, editor);
			ImGui.BeginDisabled(reason != null);
			if (EditorGui.Button(impostor.IsBaked ? "Bake Again" : "Bake", new NVector2(ImGui.GetContentRegionAvail().X, 0f)))
			{
				// After the windows are drawn: a bake may rebuild the scene they show.
				editor.Defer(() => Bake(impostor, editor));
			}

			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(reason ?? "Draws the source from every view into the atlases and writes them, the proxy model\nand the material next to this asset (files of an earlier bake are written again).");
			}

			if (!impostor.IsBaked)
			{
				ImGui.TextDisabled(reason ?? "Not baked yet.");
				ImGui.PopID();
				return;
			}

			DrawSummary(impostor);
			ImGui.SeparatorText("Baked Assets");
			ImGui.PushID("baked");
			Reference("Mesh", typeof(Mesh), impostor.Mesh, context);
			Reference("Material", typeof(Material), impostor.Material, context);
			Reference("Shape Map", typeof(Texture2D), impostor.ShapeMap, context);
			Reference("Base Map", typeof(Texture2D), impostor.BaseMap, context);
			if (impostor.SpecularMap != null)
			{
				Reference("Specular Map", typeof(Texture2D), impostor.SpecularMap, context);
			}

			if (impostor.EmissionMap != null)
			{
				Reference("Emission Map", typeof(Texture2D), impostor.EmissionMap, context);
			}

			ImGui.PopID();
			DrawAtlas(impostor, context);
			ImGui.PopID();
		}

		#endregion

		#region PrivateMethods

		private static void Bake(Impostor impostor, EditorApplication editor)
		{
			try
			{
				editor.BakeImpostor(impostor);
			}
			catch (Exception e)
			{
				Debug.LogError($"[Impostor] {impostor.AssetPath ?? impostor.name} was not baked: {e.Message}");
			}
		}

		private static void DrawSummary(Impostor impostor)
		{
			int atlas = impostor.Frames * impostor.FrameSize;
			int maps = 2 + (impostor.SpecularMap != null ? 1 : 0) + (impostor.EmissionMap != null ? 1 : 0);
			// With every mip level: a third more.
			float megabytes = (float)atlas * atlas * 4f * maps * (4f / 3f) / (1024f * 1024f);
			ImGui.PushTextWrapPos(0f);
			ImGui.TextDisabled($"{impostor.Frames} x {impostor.Frames} views of {impostor.FrameSize} texels{(impostor.Hemisphere ? " from above" : "")}: " +
			                   $"{maps} atlases of {atlas} x {atlas} ({megabytes:0.#} MB uncompressed).");
			ImGui.TextDisabled($"{impostor.SourceTriangles:N0} triangles drawn as 12.");
			ImpostorSettings settings = impostor.Settings?.Validated();
			if (settings != null && (settings.Frames != impostor.Frames || settings.FrameSize != impostor.FrameSize || settings.Hemisphere != impostor.Hemisphere))
			{
				ImGui.TextColored(EditorStyle.Warning, "The settings changed since the bake: Bake Again applies them.");
			}

			ImGui.PopTextWrapPos();
		}

		// A file the bake wrote: shown as a reference (a click shows it in the project, a double click selects it), not to
		// be set here.
		private static void Reference(string label, Type type, object value, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			ImGui.PushID(label);
			object shown = value;
			ObjectField.Draw(type, ref shown, context);
			ImGui.PopID();
		}

		private static void DrawAtlas(Impostor impostor, InspectorContext context)
		{
			ImGui.SeparatorText("Atlas");
			if (shownAtlas == 2 && impostor.SpecularMap == null || shownAtlas == 3 && impostor.EmissionMap == null)
			{
				shownAtlas = 0;
			}

			FieldDrawer.Label("Show", null, context);
			if (EditorGui.BeginCombo("##atlas", AtlasNames[shownAtlas]))
			{
				for (int i = 0; i < AtlasNames.Length; i++)
				{
					if (AtlasOf(impostor, i) != null && EditorGui.Selectable(AtlasNames[i], i == shownAtlas))
					{
						shownAtlas = i;
						previewOf = null;
					}
				}

				EditorGui.EndCombo();
			}

			Texture2D atlas = AtlasOf(impostor, shownAtlas);
			if (atlas == null)
			{
				return;
			}

			if (preview == null || !ReferenceEquals(previewOf, atlas) || previewVersion != ImpostorAssets.Version)
			{
				preview?.Release();
				preview = MakePreview(atlas, impostor.ShapeMap);
				previewOf = atlas;
				previewVersion = ImpostorAssets.Version;
			}

			if (preview == null)
			{
				ImGui.TextDisabled("The atlas has no pixels to show.");
				return;
			}

			float side = Math.Max(32f, Math.Min(ImGui.GetContentRegionAvail().X, 512f));
			ImGui.Image(EditorGui.Texture(preview.Handle), new NVector2(side, side));
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip(shownAtlas == 1
					? "Shape: red and green the surface normal in the view's axes, blue the depth along the view;\nthe coverage is in the alpha (the texels outside the object are dimmed here)."
					: shownAtlas == 0
						? "Base: the colour the surface scatters; the alpha holds its smoothness (with a specular atlas)\nor the occlusion of its indirect light. Texels outside the object repeat its edge (dimmed here)."
						: shownAtlas == 2
							? "Specular: the surface's specular colour; the alpha holds the occlusion of its indirect light."
							: "Emission: the emitted light, as a share of the brightest (the material's Emissive intensity).");
			}
		}

		private static Texture2D AtlasOf(Impostor impostor, int index)
		{
			switch (index)
			{
				case 0: return impostor.BaseMap;
				case 1: return impostor.ShapeMap;
				case 2: return impostor.SpecularMap;
				default: return impostor.EmissionMap;
			}
		}

		// The atlas's colours at every step-th texel, top row first, opaque; where the shape atlas says a texel is
		// outside the object (coverage below a half) the colour is dimmed, so the views show as the object's outlines.
		private static Texture2D MakePreview(Texture2D atlas, Texture2D shape)
		{
			byte[] source = atlas.GetRawPixels();
			int size = atlas.width;
			if (source == null || size <= 0 || atlas.height != size || source.Length < size * size * 4)
			{
				return null;
			}

			byte[] coverage = shape != null && shape.width == size && shape.height == size ? shape.GetRawPixels() : null;
			if (coverage != null && coverage.Length < size * size * 4)
			{
				coverage = null;
			}

			int step = Math.Max(1, (size + 511) / 512);
			int side = size / step;
			var pixels = new byte[side * side * 4];
			for (int y = 0; y < side; y++)
			{
				// The preview's top row is the atlas's top row: its last when it stores the bottom row first.
				int atlasRow = atlas.bottomUp ? size - 1 - y * step : y * step;
				int shapeRow = shape != null && shape.bottomUp ? size - 1 - y * step : y * step;
				for (int x = 0; x < side; x++)
				{
					int from = (atlasRow * size + x * step) * 4;
					int to = (y * side + x) * 4;
					bool inside = coverage == null || coverage[(shapeRow * size + x * step) * 4 + 3] >= 128;
					for (int channel = 0; channel < 3; channel++)
					{
						int value = source[from + channel];
						pixels[to + channel] = (byte)(inside ? value : 26 + value / 16);
					}

					pixels[to + 3] = 255;
				}
			}

			var texture = new Texture2D(side, side, pixels, atlas.name + " (preview)");
			texture.SetSamplerState(FilterMode.Bilinear, TextureWrap.Clamp, 1);
			return texture;
		}

		#endregion
	}
}
