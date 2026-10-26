using System;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>Small icons of object fields, lists and drag previews, drawn with the draw list (the editor font has no symbol glyphs).</summary>
	internal enum IconKind
	{
		None,
		GameObject,
		Component,

		/// <summary>Four arrows from the centre (the move gizmo): transforms.</summary>
		Transform,

		/// <summary>The ScriptableObject's page with "&lt; &gt;" on it: components of the project's scripts.</summary>
		ScriptComponent,
		Asset,
		ScriptableObject,
		Picker,
		Grip,
		Cross,
		Plus,
		Dropdown,
		Lock,
		Unlock,

		/// <summary>A solid cube: prefab assets and prefab instance roots.</summary>
		Prefab,

		/// <summary>A solid cube with a marker: prefab variants.</summary>
		PrefabVariant,

		/// <summary>A "+" in a disc: objects and components added to a prefab instance.</summary>
		AddedBadge,

		/// <summary>A "-" in a disc: things a prefab instance removed.</summary>
		RemovedBadge,

		/// <summary>A right-pointing arrow head (open a prefab).</summary>
		Open,

		// File kinds of the project panel (EditorIcons.KindOfFile).

		Folder,
		FolderOpen,
		Scene,
		Script,
		Shader,
		Material,
		Texture,
		Model,
		AudioClip,
		AudioProject,
		VisualEffect,
		UIDocument,
		StyleSheet,
		Font,
		PhysicsMaterial,
		InputConfiguration,
		Text
	}

	internal static class EditorIcons
	{
		#region PublicFields

		public static readonly NVector4 Accept = new NVector4(0.35f, 0.82f, 0.45f, 1f);
		public static readonly NVector4 Reject = new NVector4(0.95f, 0.38f, 0.33f, 1f);

		#endregion

		#region PublicMethods

		/// <summary>The icon of a referenced object (null: none).</summary>
		public static IconKind KindOf(object value)
		{
			switch (value)
			{
				case GameObject _:
					return IconKind.GameObject;
				case Transform _:
					return IconKind.Transform;
				case Behaviour component:
					return IsScript(component) ? IconKind.ScriptComponent : IconKind.Component;
				case ScriptableObject _:
					return IconKind.ScriptableObject;
				case Prefab prefab:
					return prefab.IsVariant ? IconKind.PrefabVariant : IconKind.Prefab;
				case IAsset _:
					return IconKind.Asset;
				default:
					return IconKind.None;
			}
		}

		/// <summary>A component of the project's scripts (not the engine's, not a missing script's placeholder).</summary>
		public static bool IsScript(Behaviour component)
		{
			return component.GetType().Assembly != typeof(Behaviour).Assembly;
		}

		/// <summary>The icon of a project file by its type (prefabs, ScriptableObject assets and folders included).</summary>
		public static IconKind KindOfFile(string assetPath)
		{
			string extension = Path.GetExtension(assetPath ?? "").ToLowerInvariant();
			switch (extension)
			{
				case ".prefab":
					return IconKind.Prefab;
				case ".scene":
					return IconKind.Scene;
				case ".cs":
					return IconKind.Script;
				case ".shader":
				case ".glsl":
					return IconKind.Shader;
				case ".material":
					return IconKind.Material;
				case ".physicsmaterial2d":
					return IconKind.PhysicsMaterial;
				case ".vfx":
					return IconKind.VisualEffect;
				case ".lelaudio":
					return IconKind.AudioProject;
				case ".uxml":
					return IconKind.UIDocument;
				case ".uss":
					return IconKind.StyleSheet;
				case ".ttf":
				case ".otf":
					return IconKind.Font;
				case ".json":
				case ".txt":
				case ".md":
				case ".mtl":
				case ".xml":
				case ".csv":
					return IconKind.Text;
			}

			if (ScriptableObjectAssets.IsAssetFile(assetPath))
			{
				return ScriptableObjectAssets.GetTypeName(assetPath)?.StartsWith(typeof(InputSystem.InputConfiguration).FullName + ",", StringComparison.Ordinal) == true ? IconKind.InputConfiguration : IconKind.ScriptableObject;
			}

			if (TextureAssets.IsImageFile(assetPath))
			{
				return IconKind.Texture;
			}

			if (ModelAssets.IsModelFile(assetPath))
			{
				return IconKind.Model;
			}

			return LELEngine.Audio.AudioClip.IsAudioFile(assetPath) ? IconKind.AudioClip : IconKind.Asset;
		}

		/// <summary>The color a file kind's icon is drawn in (each kind its own hue, so a folder's files read at a glance).</summary>
		public static uint ColorOf(IconKind kind)
		{
			switch (kind)
			{
				case IconKind.Folder:
				case IconKind.FolderOpen:
					return Rgb(0.93f, 0.75f, 0.36f);
				case IconKind.Prefab:
				case IconKind.PrefabVariant:
					return ImGui.GetColorU32(EditorStyle.Prefab);
				case IconKind.Scene:
					return Rgb(0.86f, 0.88f, 0.92f);
				case IconKind.Script:
				case IconKind.ScriptComponent:
					return Rgb(0.45f, 0.82f, 0.48f);
				case IconKind.Shader:
					return Rgb(0.74f, 0.55f, 0.95f);
				case IconKind.Material:
					return Rgb(0.95f, 0.58f, 0.35f);
				case IconKind.Texture:
					return Rgb(0.38f, 0.8f, 0.78f);
				case IconKind.Model:
					return Rgb(0.5f, 0.7f, 0.98f);
				case IconKind.AudioClip:
				case IconKind.AudioProject:
					return Rgb(0.98f, 0.66f, 0.25f);
				case IconKind.VisualEffect:
					return Rgb(0.98f, 0.45f, 0.7f);
				case IconKind.UIDocument:
				case IconKind.StyleSheet:
					return Rgb(0.38f, 0.75f, 0.95f);
				case IconKind.Font:
					return Rgb(0.9f, 0.9f, 0.9f);
				case IconKind.PhysicsMaterial:
					return Rgb(0.62f, 0.86f, 0.4f);
				case IconKind.InputConfiguration:
					return Rgb(0.95f, 0.42f, 0.4f);
				case IconKind.ScriptableObject:
					return Rgb(0.55f, 0.68f, 0.95f);
				default:
					return ImGui.GetColorU32(ImGuiCol.Text, 0.7f);
			}
		}

		/// <summary>
		///     The color of a referenced object's icon (object fields, component headers): prefabs, scripts and
		///     ScriptableObjects in their kind's color, as the project panel shows their files; the rest in the text color.
		/// </summary>
		public static uint ReferenceColor(IconKind kind)
		{
			switch (kind)
			{
				case IconKind.Prefab:
				case IconKind.PrefabVariant:
				case IconKind.ScriptComponent:
				case IconKind.ScriptableObject:
					return ColorOf(kind);
				default:
					return ImGui.GetColorU32(ImGuiCol.Text, 0.85f);
			}
		}

		/// <summary>Draws an icon into the square at <paramref name="min" /> with side <paramref name="size" />.</summary>
		public static void Draw(ImDrawListPtr draw, NVector2 min, float size, IconKind kind, uint color)
		{
			NVector2 center = min + new NVector2(size * 0.5f);
			float s = size * 0.5f;
			float thickness = Math.Max(1f, size * 0.09f);
			if (DrawFileKind(draw, center, s, thickness, kind, color))
			{
				return;
			}

			switch (kind)
			{
				case IconKind.GameObject:
				{
					// An isometric cube.
					var top = center + new NVector2(0f, -s * 0.8f);
					var left = center + new NVector2(-s * 0.72f, -s * 0.38f);
					var right = center + new NVector2(s * 0.72f, -s * 0.38f);
					var middle = center + new NVector2(0f, 0.02f * s);
					var bottomLeft = center + new NVector2(-s * 0.72f, s * 0.42f);
					var bottomRight = center + new NVector2(s * 0.72f, s * 0.42f);
					var bottom = center + new NVector2(0f, s * 0.82f);
					draw.AddQuad(top, right, middle, left, color, thickness);
					draw.AddLine(left, bottomLeft, color, thickness);
					draw.AddLine(right, bottomRight, color, thickness);
					draw.AddLine(middle, bottom, color, thickness);
					draw.AddLine(bottomLeft, bottom, color, thickness);
					draw.AddLine(bottomRight, bottom, color, thickness);
					break;
				}
				case IconKind.Component:
					draw.AddCircle(center, s * 0.7f, color, 12, thickness);
					draw.AddCircleFilled(center, s * 0.28f, color, 10);
					for (int i = 0; i < 4; i++)
					{
						float angle = i * MathF.PI * 0.5f + MathF.PI * 0.25f;
						var direction = new NVector2(MathF.Cos(angle), MathF.Sin(angle));
						draw.AddLine(center + direction * s * 0.7f, center + direction * s * 0.95f, color, thickness * 1.6f);
					}

					break;
				case IconKind.Transform:
				{
					// A cross with an arrow head at each end and a dot in the middle.
					float reach = s * 0.95f;
					float head = s * 0.34f;
					draw.AddLine(center + new NVector2(-reach + head * 0.5f, 0f), center + new NVector2(reach - head * 0.5f, 0f), color, thickness);
					draw.AddLine(center + new NVector2(0f, -reach + head * 0.5f), center + new NVector2(0f, reach - head * 0.5f), color, thickness);
					for (int i = 0; i < 4; i++)
					{
						float angle = i * MathF.PI * 0.5f;
						var direction = new NVector2(MathF.Cos(angle), MathF.Sin(angle));
						var side = new NVector2(-direction.Y, direction.X);
						NVector2 tip = center + direction * reach;
						NVector2 back = center + direction * (reach - head);
						draw.AddTriangleFilled(tip, back + side * head * 0.75f, back - side * head * 0.75f, color);
					}

					draw.AddCircleFilled(center, s * 0.16f, color, 8);
					break;
				}
				case IconKind.Asset:
				case IconKind.ScriptableObject:
				case IconKind.Script:
				case IconKind.ScriptComponent:
				{
					// A page with a folded corner (lines of text for ScriptableObjects, "< >" for script files and their
					// components: the project panel and the inspector show them alike).
					float w = s * 0.72f;
					float h = s * 0.9f;
					float fold = s * 0.38f;
					var a = center + new NVector2(-w, -h);
					var b = center + new NVector2(w - fold, -h);
					var c = center + new NVector2(w, -h + fold);
					var d = center + new NVector2(w, h);
					var e = center + new NVector2(-w, h);
					draw.PathLineTo(a);
					draw.PathLineTo(b);
					draw.PathLineTo(c);
					draw.PathLineTo(d);
					draw.PathLineTo(e);
					draw.PathStroke(color, ImDrawFlags.Closed, thickness);
					draw.AddLine(b, b + new NVector2(0f, fold), color, thickness);
					draw.AddLine(b + new NVector2(0f, fold), c, color, thickness);
					if (kind == IconKind.ScriptableObject)
					{
						for (int i = 0; i < 3; i++)
						{
							float y = center.Y - h * 0.1f + i * h * 0.36f;
							draw.AddLine(new NVector2(center.X - w * 0.55f, y), new NVector2(center.X + w * 0.55f, y), color, thickness);
						}
					}
					else if (kind == IconKind.Script || kind == IconKind.ScriptComponent)
					{
						// "<" and ">" filling the page below the fold.
						float y = center.Y + h * 0.18f;
						float arm = h * 0.42f;
						draw.PathLineTo(new NVector2(center.X - w * 0.12f, y - arm));
						draw.PathLineTo(new NVector2(center.X - w * 0.62f, y));
						draw.PathLineTo(new NVector2(center.X - w * 0.12f, y + arm));
						draw.PathStroke(color, ImDrawFlags.None, thickness * 1.2f);
						draw.PathLineTo(new NVector2(center.X + w * 0.12f, y - arm));
						draw.PathLineTo(new NVector2(center.X + w * 0.62f, y));
						draw.PathLineTo(new NVector2(center.X + w * 0.12f, y + arm));
						draw.PathStroke(color, ImDrawFlags.None, thickness * 1.2f);
					}

					break;
				}
				case IconKind.Picker:
					draw.AddCircle(center, s * 0.62f, color, 14, thickness);
					draw.AddCircleFilled(center, s * 0.22f, color, 10);
					break;
				case IconKind.Grip:
					for (int i = -1; i <= 1; i++)
					{
						float y = center.Y + i * s * 0.42f;
						draw.AddLine(new NVector2(center.X - s * 0.55f, y), new NVector2(center.X + s * 0.55f, y), color, thickness);
					}

					break;
				case IconKind.Cross:
					draw.AddLine(center + new NVector2(-s * 0.45f, -s * 0.45f), center + new NVector2(s * 0.45f, s * 0.45f), color, thickness * 1.3f);
					draw.AddLine(center + new NVector2(-s * 0.45f, s * 0.45f), center + new NVector2(s * 0.45f, -s * 0.45f), color, thickness * 1.3f);
					break;
				case IconKind.Plus:
					draw.AddLine(center + new NVector2(-s * 0.55f, 0f), center + new NVector2(s * 0.55f, 0f), color, thickness * 1.3f);
					draw.AddLine(center + new NVector2(0f, -s * 0.55f), center + new NVector2(0f, s * 0.55f), color, thickness * 1.3f);
					break;
				case IconKind.Dropdown:
					draw.AddTriangleFilled(center + new NVector2(-s * 0.5f, -s * 0.22f), center + new NVector2(s * 0.5f, -s * 0.22f), center + new NVector2(0f, s * 0.36f), color);
					break;
				case IconKind.Lock:
				case IconKind.Unlock:
				{
					// A padlock: the body, and the shackle closed or swung open.
					draw.AddRectFilled(center + new NVector2(-s * 0.62f, -s * 0.05f), center + new NVector2(s * 0.62f, s * 0.8f), color, s * 0.12f);
					float shackleX = kind == IconKind.Lock ? 0f : s * 0.55f;
					draw.PathArcTo(center + new NVector2(shackleX, -s * 0.2f), s * 0.38f, MathF.PI, MathF.PI * 2f, 10);
					draw.PathStroke(color, ImDrawFlags.None, thickness * 1.4f);
					draw.AddLine(center + new NVector2(shackleX - s * 0.38f, -s * 0.2f), center + new NVector2(shackleX - s * 0.38f, -s * 0.02f), color, thickness * 1.4f);
					if (kind == IconKind.Lock)
					{
						draw.AddLine(center + new NVector2(s * 0.38f, -s * 0.2f), center + new NVector2(s * 0.38f, -s * 0.02f), color, thickness * 1.4f);
					}

					break;
				}
				case IconKind.Prefab:
				case IconKind.PrefabVariant:
				{
					// A solid isometric cube: the top lit, the sides shaded.
					var top = center + new NVector2(0f, -s * 0.85f);
					var left = center + new NVector2(-s * 0.78f, -s * 0.42f);
					var right = center + new NVector2(s * 0.78f, -s * 0.42f);
					var middle = center + new NVector2(0f, 0f);
					var bottomLeft = center + new NVector2(-s * 0.78f, s * 0.45f);
					var bottomRight = center + new NVector2(s * 0.78f, s * 0.45f);
					var bottom = center + new NVector2(0f, s * 0.88f);
					NVector4 c = ImGui.ColorConvertU32ToFloat4(color);
					draw.AddQuadFilled(top, right, middle, left, color);
					draw.AddQuadFilled(left, middle, bottom, bottomLeft, ImGui.ColorConvertFloat4ToU32(new NVector4(c.X * 0.62f, c.Y * 0.62f, c.Z * 0.62f, c.W)));
					draw.AddQuadFilled(middle, right, bottomRight, bottom, ImGui.ColorConvertFloat4ToU32(new NVector4(c.X * 0.8f, c.Y * 0.8f, c.Z * 0.8f, c.W)));
					if (kind == IconKind.PrefabVariant)
					{
						// A small dark diamond on the top face marks a variant.
						uint mark = ImGui.ColorConvertFloat4ToU32(new NVector4(c.X * 0.25f, c.Y * 0.25f, c.Z * 0.3f, c.W));
						var markCenter = center + new NVector2(0f, -s * 0.42f);
						float m = s * 0.2f;
						draw.AddQuadFilled(markCenter + new NVector2(0f, -m), markCenter + new NVector2(m * 1.6f, 0f), markCenter + new NVector2(0f, m), markCenter + new NVector2(-m * 1.6f, 0f), mark);
					}

					break;
				}
				case IconKind.AddedBadge:
				case IconKind.RemovedBadge:
				{
					draw.AddCircleFilled(center, s * 0.62f, color, 14);
					uint ink = ImGui.GetColorU32(new NVector4(0.08f, 0.08f, 0.09f, 1f));
					draw.AddLine(center + new NVector2(-s * 0.34f, 0f), center + new NVector2(s * 0.34f, 0f), ink, thickness * 1.5f);
					if (kind == IconKind.AddedBadge)
					{
						draw.AddLine(center + new NVector2(0f, -s * 0.34f), center + new NVector2(0f, s * 0.34f), ink, thickness * 1.5f);
					}

					break;
				}
				case IconKind.Open:
					draw.AddTriangleFilled(center + new NVector2(-s * 0.25f, -s * 0.45f), center + new NVector2(s * 0.35f, 0f), center + new NVector2(-s * 0.25f, s * 0.45f), color);
					break;
			}
		}

		// The project panel's file kinds; false for the other icons.
		private static bool DrawFileKind(ImDrawListPtr draw, NVector2 center, float s, float thickness, IconKind kind, uint color)
		{
			NVector4 c = ImGui.ColorConvertU32ToFloat4(color);
			uint dark = ImGui.ColorConvertFloat4ToU32(new NVector4(c.X * 0.55f, c.Y * 0.55f, c.Z * 0.55f, c.W));
			uint ink = ImGui.GetColorU32(new NVector4(0.1f, 0.1f, 0.12f, c.W));
			switch (kind)
			{
				case IconKind.Folder:
				case IconKind.FolderOpen:
				{
					// The tab, the back, and the front panel (tilted forward when open).
					var backMin = center + new NVector2(-s * 0.9f, -s * 0.5f);
					var backMax = center + new NVector2(s * 0.9f, s * 0.72f);
					draw.AddRectFilled(center + new NVector2(-s * 0.9f, -s * 0.72f), center + new NVector2(-s * 0.15f, -s * 0.4f), dark, s * 0.12f);
					draw.AddRectFilled(backMin, backMax, dark, s * 0.12f);
					if (kind == IconKind.FolderOpen)
					{
						draw.AddQuadFilled(center + new NVector2(-s * 0.62f, -s * 0.18f), center + new NVector2(s * 1.0f, -s * 0.18f), center + new NVector2(s * 0.8f, s * 0.72f), center + new NVector2(-s * 0.9f, s * 0.72f), color);
					}
					else
					{
						draw.AddRectFilled(center + new NVector2(-s * 0.9f, -s * 0.28f), backMax, color, s * 0.12f);
					}

					return true;
				}
				case IconKind.Scene:
				{
					// A clapperboard: the board and its striped, opened clapper.
					draw.AddRectFilled(center + new NVector2(-s * 0.85f, -s * 0.25f), center + new NVector2(s * 0.85f, s * 0.8f), color, s * 0.1f);
					var a = center + new NVector2(-s * 0.85f, -s * 0.42f);
					var b = center + new NVector2(s * 0.75f, -s * 0.85f);
					var bottomA = a + new NVector2(0f, s * 0.3f);
					var bottomB = b + new NVector2(0f, s * 0.3f);
					draw.AddQuadFilled(a, b, bottomB, bottomA, color);
					for (int i = 1; i <= 3; i++)
					{
						float t = i / 4f;
						NVector2 top = NVector2.Lerp(a, b, t);
						draw.AddLine(top, NVector2.Lerp(bottomA, bottomB, t - 0.1f), ink, thickness * 1.2f);
					}

					draw.AddLine(center + new NVector2(-s * 0.6f, s * 0.2f), center + new NVector2(s * 0.6f, s * 0.2f), ink, thickness);
					return true;
				}
				case IconKind.Shader:
				case IconKind.UIDocument:
				case IconKind.StyleSheet:
				case IconKind.Text:
				{
					// A page with a folded corner and what is written on it.
					float w = s * 0.72f;
					float h = s * 0.92f;
					float fold = s * 0.36f;
					draw.PathLineTo(center + new NVector2(-w, -h));
					draw.PathLineTo(center + new NVector2(w - fold, -h));
					draw.PathLineTo(center + new NVector2(w, -h + fold));
					draw.PathLineTo(center + new NVector2(w, h));
					draw.PathLineTo(center + new NVector2(-w, h));
					draw.PathFillConvex(dark);
					draw.AddTriangleFilled(center + new NVector2(w - fold, -h), center + new NVector2(w, -h + fold), center + new NVector2(w - fold, -h + fold), color);
					switch (kind)
					{
						case IconKind.Shader:
							// A wave.
							for (int i = 0; i <= 8; i++)
							{
								float t = i / 8f;
								draw.PathLineTo(center + new NVector2((t - 0.5f) * w * 1.5f, s * 0.2f - MathF.Sin(t * MathF.PI * 2f) * s * 0.3f));
							}

							draw.PathStroke(color, ImDrawFlags.None, thickness * 1.3f);
							break;
						case IconKind.UIDocument:
							// A window: its title bar and two panels.
							draw.AddRectFilled(center + new NVector2(-w * 0.7f, -s * 0.35f), center + new NVector2(w * 0.7f, -s * 0.12f), color);
							draw.AddRect(center + new NVector2(-w * 0.7f, -s * 0.35f), center + new NVector2(w * 0.7f, s * 0.65f), color, 0f, ImDrawFlags.None, thickness);
							draw.AddLine(center + new NVector2(0f, -s * 0.12f), center + new NVector2(0f, s * 0.65f), color, thickness);
							break;
						case IconKind.StyleSheet:
							// Swatches.
							draw.AddCircleFilled(center + new NVector2(-s * 0.3f, -s * 0.05f), s * 0.2f, color, 10);
							draw.AddCircleFilled(center + new NVector2(s * 0.3f, -s * 0.05f), s * 0.2f, Rgb(0.95f, 0.5f, 0.5f), 10);
							draw.AddCircleFilled(center + new NVector2(0f, s * 0.45f), s * 0.2f, Rgb(0.95f, 0.85f, 0.4f), 10);
							break;
						default:
							for (int i = 0; i < 3; i++)
							{
								float y = center.Y - s * 0.15f + i * s * 0.3f;
								draw.AddLine(new NVector2(center.X - w * 0.6f, y), new NVector2(center.X + w * 0.6f, y), color, thickness);
							}

							break;
					}

					return true;
				}
				case IconKind.Material:
					// A shaded sphere with a highlight.
					draw.AddCircleFilled(center, s * 0.85f, dark, 20);
					draw.AddCircleFilled(center + new NVector2(-s * 0.12f, -s * 0.12f), s * 0.66f, color, 20);
					draw.AddCircleFilled(center + new NVector2(-s * 0.35f, -s * 0.35f), s * 0.18f, ImGui.GetColorU32(new NVector4(1f, 1f, 1f, 0.8f * c.W)), 10);
					return true;
				case IconKind.Texture:
					// A picture: a frame, mountains and a sun.
					draw.AddRectFilled(center + new NVector2(-s * 0.9f, -s * 0.72f), center + new NVector2(s * 0.9f, s * 0.72f), dark, s * 0.1f);
					draw.AddTriangleFilled(center + new NVector2(-s * 0.75f, s * 0.6f), center + new NVector2(-s * 0.15f, -s * 0.15f), center + new NVector2(s * 0.45f, s * 0.6f), color);
					draw.AddTriangleFilled(center + new NVector2(0f, s * 0.6f), center + new NVector2(s * 0.38f, s * 0.12f), center + new NVector2(s * 0.78f, s * 0.6f), color);
					draw.AddCircleFilled(center + new NVector2(s * 0.45f, -s * 0.35f), s * 0.17f, Rgb(0.98f, 0.88f, 0.45f), 10);
					return true;
				case IconKind.Model:
				{
					// A wireframe pyramid.
					var apex = center + new NVector2(0f, -s * 0.88f);
					var left = center + new NVector2(-s * 0.85f, s * 0.55f);
					var right = center + new NVector2(s * 0.85f, s * 0.55f);
					var front = center + new NVector2(-s * 0.1f, s * 0.85f);
					draw.AddTriangleFilled(apex, left, front, dark);
					draw.AddTriangleFilled(apex, front, right, color);
					draw.AddLine(apex, left, color, thickness);
					draw.AddLine(apex, right, color, thickness);
					draw.AddLine(left, front, color, thickness);
					draw.AddLine(front, right, color, thickness);
					return true;
				}
				case IconKind.AudioClip:
					// A note.
					draw.AddCircleFilled(center + new NVector2(-s * 0.3f, s * 0.5f), s * 0.3f, color, 12);
					draw.AddLine(center + new NVector2(-s * 0.02f, s * 0.5f), center + new NVector2(-s * 0.02f, -s * 0.85f), color, thickness * 1.4f);
					draw.AddQuadFilled(center + new NVector2(-s * 0.02f, -s * 0.85f), center + new NVector2(s * 0.6f, -s * 0.55f), center + new NVector2(s * 0.6f, -s * 0.25f), center + new NVector2(-s * 0.02f, -s * 0.55f), color);
					return true;
				case IconKind.AudioProject:
					// A speaker and its sound.
					draw.AddQuadFilled(center + new NVector2(-s * 0.85f, -s * 0.28f), center + new NVector2(-s * 0.45f, -s * 0.28f), center + new NVector2(-s * 0.45f, s * 0.28f), center + new NVector2(-s * 0.85f, s * 0.28f), color);
					draw.AddQuadFilled(center + new NVector2(-s * 0.45f, -s * 0.28f), center + new NVector2(s * 0.05f, -s * 0.72f), center + new NVector2(s * 0.05f, s * 0.72f), center + new NVector2(-s * 0.45f, s * 0.28f), color);
					draw.PathArcTo(center + new NVector2(s * 0.05f, 0f), s * 0.42f, -0.9f, 0.9f, 8);
					draw.PathStroke(color, ImDrawFlags.None, thickness);
					draw.PathArcTo(center + new NVector2(s * 0.05f, 0f), s * 0.78f, -0.9f, 0.9f, 10);
					draw.PathStroke(color, ImDrawFlags.None, thickness);
					return true;
				case IconKind.VisualEffect:
				{
					// Sparkles: a four-pointed star and a small one.
					void Star(NVector2 at, float r)
					{
						draw.AddQuadFilled(at + new NVector2(0f, -r), at + new NVector2(r * 0.28f, 0f), at + new NVector2(0f, r), at + new NVector2(-r * 0.28f, 0f), color);
						draw.AddQuadFilled(at + new NVector2(-r, 0f), at + new NVector2(0f, -r * 0.28f), at + new NVector2(r, 0f), at + new NVector2(0f, r * 0.28f), color);
					}

					Star(center + new NVector2(-s * 0.15f, s * 0.12f), s * 0.78f);
					Star(center + new NVector2(s * 0.58f, -s * 0.55f), s * 0.35f);
					return true;
				}
				case IconKind.Font:
					// The letter A.
					draw.AddLine(center + new NVector2(-s * 0.7f, s * 0.8f), center + new NVector2(0f, -s * 0.85f), color, thickness * 1.6f);
					draw.AddLine(center + new NVector2(s * 0.7f, s * 0.8f), center + new NVector2(0f, -s * 0.85f), color, thickness * 1.6f);
					draw.AddLine(center + new NVector2(-s * 0.38f, s * 0.22f), center + new NVector2(s * 0.38f, s * 0.22f), color, thickness * 1.4f);
					return true;
				case IconKind.PhysicsMaterial:
					// A ball bouncing off the ground.
					draw.AddCircleFilled(center + new NVector2(s * 0.25f, -s * 0.25f), s * 0.42f, color, 14);
					draw.AddLine(center + new NVector2(-s * 0.9f, s * 0.75f), center + new NVector2(s * 0.9f, s * 0.75f), color, thickness);
					draw.PathArcTo(center + new NVector2(-s * 0.35f, s * 0.75f), s * 0.5f, MathF.PI * 1.05f, MathF.PI * 1.6f, 8);
					draw.PathStroke(dark, ImDrawFlags.None, thickness);
					return true;
				case IconKind.InputConfiguration:
				{
					// A gamepad: the body, the d-pad and two buttons.
					draw.AddRectFilled(center + new NVector2(-s * 0.9f, -s * 0.42f), center + new NVector2(s * 0.9f, s * 0.42f), color, s * 0.4f);
					draw.AddCircleFilled(center + new NVector2(-s * 0.55f, s * 0.42f), s * 0.3f, color, 10);
					draw.AddCircleFilled(center + new NVector2(s * 0.55f, s * 0.42f), s * 0.3f, color, 10);
					draw.AddRectFilled(center + new NVector2(-s * 0.62f, -s * 0.08f), center + new NVector2(-s * 0.22f, s * 0.08f), ink);
					draw.AddRectFilled(center + new NVector2(-s * 0.5f, -s * 0.2f), center + new NVector2(-s * 0.34f, s * 0.2f), ink);
					draw.AddCircleFilled(center + new NVector2(s * 0.32f, -s * 0.08f), s * 0.1f, ink, 8);
					draw.AddCircleFilled(center + new NVector2(s * 0.55f, s * 0.1f), s * 0.1f, ink, 8);
					return true;
				}
				default:
					return false;
			}
		}

		private static uint Rgb(float r, float g, float b)
		{
			return ImGui.ColorConvertFloat4ToU32(new NVector4(r, g, b, 1f));
		}

		/// <summary>A small square button showing an icon; true when clicked.</summary>
		public static bool Button(string id, IconKind kind, string tooltip, float size = 0f)
		{
			float height = ImGui.GetFrameHeight();
			size = size > 0f ? size : height;
			bool pressed = EditorGui.Button("##" + id, new NVector2(size, height));
			if (tooltip != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(tooltip);
			}

			NVector2 min = ImGui.GetItemRectMin();
			NVector2 max = ImGui.GetItemRectMax();
			float icon = Math.Min(max.X - min.X, max.Y - min.Y) * 0.62f;
			Draw(ImGui.GetWindowDrawList(), (min + max) * 0.5f - new NVector2(icon * 0.5f), icon, kind, ImGui.GetColorU32(ImGuiCol.Text));
			return pressed;
		}

		/// <summary>An outline around a drop target: green when the dragged object fits, red when it does not.</summary>
		public static void DropHighlight(NVector2 min, NVector2 max, bool fits)
		{
			ImGui.GetWindowDrawList().AddRect(min - new NVector2(1f), max + new NVector2(1f), ImGui.GetColorU32(fits ? Accept : Reject), ImGui.GetStyle().FrameRounding, ImDrawFlags.None, 2f);
		}

		#endregion
	}
}
