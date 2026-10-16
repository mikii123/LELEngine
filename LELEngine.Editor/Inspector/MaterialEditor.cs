using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Unity-style material inspector: the shader (a combo of the project's and the engine's shaders) and one row
	///     per material parameter the shader declares in its Properties block: colors with a picker (HDR for emission),
	///     ranges as sliders, textures as object fields with a thumbnail, picker and drop target. Shaders without a
	///     Properties block show their material uniforms by GLSL type instead. Edits apply live (the material is the
	///     shared asset instance), are undoable and are saved to the .material file when the edit ends.
	/// </summary>
	internal static class MaterialEditor
	{
		#region PrivateFields

		private static List<string> shaderChoices;
		private static int shaderChoicesVersion = -1;

		#endregion

		#region PublicMethods

		public static bool Draw(Material material, InspectorContext context)
		{
			context.UndoTarget = material;
			bool changed = false;

			changed |= DrawShaderRow(material, context);
			ImGui.Spacing();

			ShaderProgram shader = material.shader;
			if (shader == null)
			{
				ImGui.TextColored(EditorStyle.Warning, "No shader: pick one above.");
				return changed;
			}

			if (!shader.IsLinked)
			{
				ImGui.TextColored(EditorStyle.Error, "The shader does not compile (see the console).");
			}

			changed |= DrawRenderQueue(material, context);
			ImGui.Spacing();

			var shown = new HashSet<string>(StringComparer.Ordinal);
			if (shader.Properties.Count > 0)
			{
				foreach (ShaderProperty declared in shader.Properties)
				{
					shown.Add(declared.Name);
					if (declared.HideInInspector)
					{
						continue;
					}

					ImGui.PushID(declared.Name);
					changed |= DrawDeclared(material, declared, context);
					ImGui.PopID();
				}
			}
			else
			{
				ImGui.TextDisabled("The shader declares no Properties block; its material uniforms:");
				List<KeyValuePair<string, ActiveUniformType>> uniforms = shader.GetMaterialUniforms();
				// The engine's conventional parameters first (Color, Emissive, Roughness, Metallic), then the rest by name.
				uniforms.Sort((a, b) =>
				{
					int ra = StandardRank(a.Key);
					int rb = StandardRank(b.Key);
					return ra != rb ? ra.CompareTo(rb) : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
				});
				foreach (KeyValuePair<string, ActiveUniformType> uniform in uniforms)
				{
					shown.Add(uniform.Key);
					ImGui.PushID(uniform.Key);
					changed |= DrawUniform(material, uniform.Key, uniform.Value, context);
					ImGui.PopID();
				}
			}

			// Values the shader does not use (left over from another shader).
			var unused = new List<MaterialProperty>();
			foreach (MaterialProperty property in material.Properties)
			{
				if (!shown.Contains(property.name))
				{
					unused.Add(property);
				}
			}

			if (unused.Count > 0)
			{
				ImGui.Spacing();
				if (ImGui.TreeNodeEx("Unused values (" + unused.Count + ")", ImGuiTreeNodeFlags.SpanAvailWidth))
				{
					foreach (MaterialProperty property in unused)
					{
						ImGui.TextDisabled(property.name + " (" + property.Type + ")");
					}

					if (ImGui.Button("Remove unused values"))
					{
						context.Editor.Edits.Begin(material);
						foreach (MaterialProperty property in unused)
						{
							material.RemoveProperty(property.name);
						}

						context.Editor.Edits.RequestCommit();
						changed = true;
					}

					ImGui.TreePop();
				}
			}

			ImGui.Spacing();
			if (ImGui.Button("Reset to shader defaults"))
			{
				context.Editor.Edits.Begin(material);
				foreach (string name in new List<string>(material.GetPropertyNames()))
				{
					material.RemoveProperty(name);
				}

				material.ResetToShaderDefaults(false);
				context.Editor.Edits.RequestCommit();
				changed = true;
			}

			if (changed)
			{
				MaterialChanged(context.Editor);
			}

			return changed;
		}

		/// <summary>The views re-render and the GI re-captures surfaces after a material edit.</summary>
		public static void MaterialChanged(EditorApplication editor)
		{
			editor.NotifySceneChanged();
			Lighting.GI.InvalidateStatic();
		}

		#endregion

		#region PrivateMethods

		private static bool DrawShaderRow(Material material, InspectorContext context)
		{
			FieldDrawer.Label("Shader", null, context);
			string current = material.shader?.AssetPath ?? "None";
			bool changed = false;
			if (ImGui.BeginCombo("##shader", current))
			{
				foreach (string choice in ShaderChoices(context.Editor))
				{
					bool selected = string.Equals(choice, current, StringComparison.OrdinalIgnoreCase);
					if (ImGui.Selectable(choice, selected) && !selected)
					{
						context.Editor.Edits.Begin(material);
						material.shader = InternalStorage.GetOrCreateShader(choice);
						context.Editor.Edits.RequestCommit();
						changed = true;
					}
				}

				ImGui.EndCombo();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && ProjectPanel.DraggedAsset != null && ProjectPanel.DraggedAsset.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
				{
					context.Editor.Edits.Begin(material);
					material.shader = InternalStorage.GetOrCreateShader(ProjectPanel.DraggedAsset);
					context.Editor.Edits.RequestCommit();
					changed = true;
				}

				ImGui.EndDragDropTarget();
			}

			return changed;
		}

		/// <summary>
		///     The shader's render state (queue, blending, depth, cull) and the material's queue: the shader's by default,
		///     editable per material (a queue above 2500 draws the material in the transparent pass).
		/// </summary>
		private static bool DrawRenderQueue(Material material, InspectorContext context)
		{
			ShaderRenderState state = material.RenderState;
			ImGui.TextDisabled(state.Describe());
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Declared by the shader in its Properties block (Blend, ZWrite, ZTest, Cull, Queue).");
			}

			FieldDrawer.Label("Render Queue", null, context);
			int queue = material.renderQueue;
			ImGui.SetNextItemWidth(Math.Max(60f, ImGui.GetContentRegionAvail().X - 110f));
			bool changed = ImGui.InputInt("##queue", ref queue, 10, 100);
			context.Track(changed);
			if (changed)
			{
				// Back on the shader's own queue: no override.
				material.renderQueue = queue == state.Queue ? -1 : Math.Max(0, queue);
			}

			ImGui.SameLine();
			if (material.OverridesRenderQueue)
			{
				if (ImGui.SmallButton("Shader's"))
				{
					context.Editor.Edits.Begin(material);
					material.renderQueue = -1;
					context.Editor.Edits.RequestCommit();
					changed = true;
				}

				if (ImGui.IsItemHovered())
				{
					ImGui.SetTooltip("Follow the shader's queue (" + state.Queue + ") again.");
				}
			}
			else
			{
				ImGui.TextDisabled("(from shader)");
			}

			return changed;
		}

		private static List<string> ShaderChoices(EditorApplication editor)
		{
			if (shaderChoices != null && shaderChoicesVersion == editor.AssetsVersion)
			{
				return shaderChoices;
			}

			shaderChoicesVersion = editor.AssetsVersion;
			shaderChoices = AssetDatabase.FindAssets(typeof(ShaderProgram));
			shaderChoices.AddRange(ObjectPicker.EngineShaders());
			return shaderChoices;
		}

		private static bool DrawDeclared(Material material, ShaderProperty declared, InspectorContext context)
		{
			string name = declared.Name;
			string label = string.IsNullOrEmpty(declared.DisplayName) ? name : declared.DisplayName;
			switch (declared.Type)
			{
				case ShaderPropertyType.Color:
					return DrawColor(material, name, label, declared.Hdr, context);
				case ShaderPropertyType.Range:
					return DrawRange(material, name, label, declared.RangeMin, declared.RangeMax, context);
				case ShaderPropertyType.Float:
					return DrawFloat(material, name, label, context);
				case ShaderPropertyType.Int:
					return DrawInt(material, name, label, context);
				case ShaderPropertyType.Vector:
					return DrawVector(material, name, label, 4, context);
				case ShaderPropertyType.Cubemap:
					return DrawTexture(material, name, label, typeof(Cubemap), context);
				case ShaderPropertyType.Texture3D:
					return DrawTexture(material, name, label, typeof(Texture3D), context);
				default:
					return DrawTexture(material, name, label, typeof(Texture2D), context);
			}
		}

		private static bool DrawUniform(Material material, string name, ActiveUniformType type, InspectorContext context)
		{
			string label = Serialization.SerializationUtility.NicifyName(name);
			switch (type)
			{
				case ActiveUniformType.FloatVec4:
					return IsColorName(name) ? DrawColor(material, name, label, IsHdrName(name), context) : DrawVector(material, name, label, 4, context);
				case ActiveUniformType.FloatVec3:
					return IsColorName(name) ? DrawColor(material, name, label, IsHdrName(name), context) : DrawVector(material, name, label, 3, context);
				case ActiveUniformType.FloatVec2:
					return DrawVector(material, name, label, 2, context);
				case ActiveUniformType.Float:
					return DrawFloat(material, name, label, context);
				case ActiveUniformType.Int:
				case ActiveUniformType.UnsignedInt:
					return DrawInt(material, name, label, context);
				case ActiveUniformType.Bool:
					return DrawBool(material, name, label, context);
				case ActiveUniformType.Sampler2D:
					return DrawTexture(material, name, label, typeof(Texture2D), context);
				case ActiveUniformType.SamplerCube:
					return DrawTexture(material, name, label, typeof(Cubemap), context);
				case ActiveUniformType.Sampler3D:
					return DrawTexture(material, name, label, typeof(Texture3D), context);
				default:
					FieldDrawer.Label(label, null, context);
					ImGui.TextDisabled(type.ToString());
					return false;
			}
		}

		private static bool DrawColor(Material material, string name, string label, bool hdr, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			Color4 color = material.GetColor(name, Color4.White);
			NVector4 n = new NVector4(color.R, color.G, color.B, color.A);
			ImGuiColorEditFlags flags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaPreviewHalf;
			if (hdr)
			{
				flags |= ImGuiColorEditFlags.Hdr;
			}

			bool changed = ImGui.ColorEdit4("##v", ref n, flags);
			context.Track(changed);
			if (changed)
			{
				material.SetColor(name, new Color4(n.X, n.Y, n.Z, n.W));
			}

			return changed;
		}

		private static bool DrawRange(Material material, string name, string label, float min, float max, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			float value = material.GetFloat(name);
			bool changed = ImGui.SliderFloat("##v", ref value, min, max);
			context.Track(changed);
			if (changed)
			{
				material.SetFloat(name, value);
			}

			return changed;
		}

		private static bool DrawFloat(Material material, string name, string label, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			float value = material.GetFloat(name);
			bool changed = ImGui.DragFloat("##v", ref value, Math.Max(0.005f, Math.Abs(value) * 0.01f));
			context.Track(changed);
			if (changed)
			{
				material.SetFloat(name, value);
			}

			return changed;
		}

		private static bool DrawInt(Material material, string name, string label, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			int value = material.GetInt(name);
			bool changed = ImGui.DragInt("##v", ref value, 0.2f);
			context.Track(changed);
			if (changed)
			{
				material.SetInt(name, value);
			}

			return changed;
		}

		private static bool DrawBool(Material material, string name, string label, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			bool value = material.GetInt(name) != 0;
			bool changed = ImGui.Checkbox("##v", ref value);
			context.Track(changed);
			if (changed)
			{
				material.SetInt(name, value ? 1 : 0);
			}

			return changed;
		}

		private static bool DrawVector(Material material, string name, string label, int components, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			Vector4 value = material.GetVector(name);
			bool changed;
			if (components == 2)
			{
				NVector2 n = new NVector2(value.X, value.Y);
				changed = ImGui.DragFloat2("##v", ref n, 0.05f);
				value = new Vector4(n.X, n.Y, value.Z, value.W);
			}
			else if (components == 3)
			{
				NVector3 n = new NVector3(value.X, value.Y, value.Z);
				changed = ImGui.DragFloat3("##v", ref n, 0.05f);
				value = new Vector4(n.X, n.Y, n.Z, value.W);
			}
			else
			{
				NVector4 n = new NVector4(value.X, value.Y, value.Z, value.W);
				changed = ImGui.DragFloat4("##v", ref n, 0.05f);
				value = new Vector4(n.X, n.Y, n.Z, n.W);
			}

			context.Track(changed);
			if (changed)
			{
				material.SetVector(name, value);
			}

			return changed;
		}

		private static bool DrawTexture(Material material, string name, string label, Type textureType, InspectorContext context)
		{
			FieldDrawer.Label(label, null, context);
			object value = material.GetTexture(name);
			Func<string, bool> filter = path => ShapeMatches(path, textureType);
			bool changed = AssetField.Draw(textureType, ref value, filter);
			context.Track(changed);
			if (changed)
			{
				context.Editor.Edits.Begin(material);
				material.SetTexture(name, value as Texture);
				context.Editor.Edits.RequestCommit();
			}

			return changed;
		}

		/// <summary>An image file imports as the texture kind a sampler wants (its import settings decide).</summary>
		public static bool ShapeMatches(string assetPath, Type textureType)
		{
			var importer = AssetDatabase.GetMeta(assetPath).importer as TextureImporter;
			TextureImporterShape shape = importer?.shape ?? TextureImporter.CreateDefault(assetPath).shape;
			if (textureType == typeof(Cubemap)) return shape == TextureImporterShape.Cube;
			if (textureType == typeof(Texture3D)) return shape == TextureImporterShape.Texture3D;
			if (textureType == typeof(Texture2D) || textureType == typeof(Sprite)) return shape == TextureImporterShape.Texture2D;
			return true;
		}

		private static readonly string[] StandardOrder = { "Color", "Emissive", "Roughness", "Metallic", "DiffuseMap", "AlbedoMap", "MainTex", "NormalMap", "SpecularMap", "MetalRoughMap" };

		private static int StandardRank(string name)
		{
			int index = Array.IndexOf(StandardOrder, name);
			return index >= 0 ? index : StandardOrder.Length;
		}

		private static bool IsColorName(string name)
		{
			string n = name.ToLowerInvariant();
			return n.Contains("color") || n.Contains("colour") || n.Contains("emissive") || n.Contains("emission") || n.Contains("tint") || n.Contains("albedo");
		}

		private static bool IsHdrName(string name)
		{
			string n = name.ToLowerInvariant();
			return n.Contains("emissive") || n.Contains("emission");
		}

		#endregion
	}
}
