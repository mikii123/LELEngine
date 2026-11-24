using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using LELEngine.Shaders;
using LELEngine.Graphics;
using LELEngine.Mathematics;
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
		private static LevelList shaderLevels;
		private static string shaderSearch = "";
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

			// The variant the material's keywords pick (compiled here on first use).
			ShaderProgram program = material.Program;
			if (program.Keywords.Count > 0)
			{
				ImGui.TextDisabled("Variant: " + string.Join(" ", program.Keywords));
				if (ImGui.IsItemHovered())
				{
					EditorGui.SetTooltip("The keywords of the material's toggles, keyword enums and set maps: a program of its own (game builds compile the ones their materials use).");
				}
			}

			if (!program.IsLinked)
			{
				ImGui.TextColored(EditorStyle.Error, program.Keywords.Count > 0 ? "This variant does not compile (see the console)." : "The shader does not compile (see the console).");
			}

			changed |= DrawRenderQueue(material, context);
			ImGui.Spacing();

			var shown = new HashSet<string>(StringComparer.Ordinal);
			if (shader.Properties.Count > 0)
			{
				int perRenderer = 0;
				foreach (ShaderProperty declared in shader.Properties)
				{
					shown.Add(declared.Name);
					perRenderer += declared.PerRendererData && !declared.HideInInspector ? 1 : 0;
					if (declared.HideInInspector || declared.PerRendererData || IsScaleOffsetOfTexture(shader, declared))
					{
						continue;
					}

					ImGui.PushID(declared.Name);
					changed |= DrawDeclared(material, declared, context);
					ShaderProperty scaleOffset = declared.IsTexture ? FindScaleOffset(shader, declared.Name) : null;
					if (scaleOffset != null && !scaleOffset.HideInInspector)
					{
						changed |= DrawScaleOffset(material, scaleOffset, context);
					}

					ImGui.PopID();
				}

				if (perRenderer > 0)
				{
					ImGui.TextDisabled(perRenderer == 1 ? "1 property is set per renderer ([PerRendererData])." : $"{perRenderer} properties are set per renderer ([PerRendererData]).");
				}
			}
			else
			{
				ImGui.TextDisabled("The shader declares no Properties block; its material uniforms:");
				List<KeyValuePair<string, UniformType>> uniforms = shader.GetMaterialUniforms();
				// The engine's conventional parameters first (Color, Emissive, Roughness, Metallic), then the rest by name.
				uniforms.Sort((a, b) =>
				{
					int ra = StandardRank(a.Key);
					int rb = StandardRank(b.Key);
					return ra != rb ? ra.CompareTo(rb) : string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
				});
				foreach (KeyValuePair<string, UniformType> uniform in uniforms)
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
				if (EditorGui.TreeNodeEx("Unused values (" + unused.Count + ")", ImGuiTreeNodeFlags.SpanAvailWidth))
				{
					foreach (MaterialProperty property in unused)
					{
						ImGui.TextDisabled(property.name + " (" + property.Type + ")");
					}

					if (EditorGui.Button("Remove unused values"))
					{
						context.Editor.Edits.Begin(material, context.UndoGroup);
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
			if (EditorGui.Button("Reset to shader defaults"))
			{
				context.Editor.Edits.Begin(material, context.UndoGroup);
				// Every material edited together, whatever values it had.
				var targets = new List<Material> { material };
				if (context.UndoGroup != null)
				{
					foreach (object other in context.UndoGroup)
					{
						if (other is Material otherMaterial)
						{
							targets.Add(otherMaterial);
						}
					}
				}

				foreach (Material target in targets)
				{
					foreach (string name in new List<string>(target.GetPropertyNames()))
					{
						target.RemoveProperty(name);
					}

					target.ResetToShaderDefaults(false);
				}

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

		/// <summary>
		///     Several materials edited together (selected in the project panel): drawn from <paramref name="primary" /> with
		///     "—" where their shaders, queues or values differ; what an edit changes in it (a value, a vector's or color's
		///     components one by one, a texture, the shader, the queue, values removed) is set on the others too, as one
		///     undo step, each saved.
		/// </summary>
		public static void DrawMultiple(Material primary, IReadOnlyList<Material> others, InspectorContext context)
		{
			Dictionary<string, (ValueTag tag, object value)> before = ValuesOf(primary);
			context.UndoGroup = others;
			context.Mixed = MixedOf(primary, before, others);
			try
			{
				Draw(primary, context);
			}
			finally
			{
				context.UndoGroup = null;
				context.Mixed = null;
				context.Path = "";
			}

			if (Propagate(primary, before, others, context.Touched))
			{
				MaterialChanged(context.Editor);
			}

			context.Touched.Clear();
		}

		#endregion

		#region MultipleMaterials

		// The paths of a material's own state among its values (no shader property is named so).
		private const string ShaderPath = "m_Shader";
		private const string RenderQueuePath = "m_RenderQueue";

		/// <summary>A material's shader, queue override and values by name (textures as the texture objects).</summary>
		private static Dictionary<string, (ValueTag tag, object value)> ValuesOf(Material material)
		{
			var values = new Dictionary<string, (ValueTag, object)>(StringComparer.Ordinal)
			{
				[ShaderPath] = (ValueTag.String, material.ShaderPath ?? ""),
				[RenderQueuePath] = (ValueTag.Int32, material.OverridesRenderQueue ? material.renderQueue : -1)
			};
			foreach (MaterialProperty property in material.Properties)
			{
				switch (property)
				{
					case FloatProperty f: values[property.name] = (ValueTag.Single, f.value); break;
					case IntProperty i: values[property.name] = (ValueTag.Int32, i.value); break;
					case VectorProperty v: values[property.name] = (ValueTag.Vector4, v.value); break;
					case ColorProperty c: values[property.name] = (ValueTag.Color4, c.value); break;
					case TextureProperty t: values[property.name] = (ValueTag.Asset, t.value); break;
				}
			}

			return values;
		}

		private static SerializedNode NodeOf(Dictionary<string, (ValueTag tag, object value)> values)
		{
			var node = new SerializedNode { TypeName = "MaterialValues" };
			foreach (KeyValuePair<string, (ValueTag tag, object value)> entry in values)
			{
				node.Properties.Add(new SerializedProperty { Name = entry.Key, Tag = entry.Value.tag, Value = entry.Value.value });
			}

			return node;
		}

		/// <summary>The values that differ between the materials (one has a value another has not: differs too).</summary>
		private static MixedValues MixedOf(Material primary, Dictionary<string, (ValueTag tag, object value)> mine, IReadOnlyList<Material> others)
		{
			var differing = new List<string>();
			var nodes = new List<SerializedNode>();
			foreach (Material other in others)
			{
				Dictionary<string, (ValueTag tag, object value)> theirs = ValuesOf(other);
				nodes.Add(NodeOf(theirs));
				foreach (KeyValuePair<string, (ValueTag tag, object value)> entry in mine)
				{
					if (!theirs.TryGetValue(entry.Key, out (ValueTag tag, object value) value) || !Equals(value.value, entry.Value.value))
					{
						differing.Add(entry.Key);
					}
				}

				foreach (string name in theirs.Keys)
				{
					if (!mine.ContainsKey(name))
					{
						differing.Add(name);
					}
				}
			}

			return MixedValues.FromPaths(differing, NodeOf(mine), nodes);
		}

		/// <summary>What the edit changed in <paramref name="primary" /> (against <paramref name="before" />) set on the others; true when one changed.</summary>
		private static bool Propagate(Material primary, Dictionary<string, (ValueTag tag, object value)> before, IReadOnlyList<Material> others, List<string> touched)
		{
			Dictionary<string, (ValueTag tag, object value)> after = ValuesOf(primary);
			var changed = new List<string>();
			foreach (KeyValuePair<string, (ValueTag tag, object value)> entry in after)
			{
				// Set while the materials differed (an option picked, a toggle clicked) goes to all, changed or not; a
				// vector or color only in the components that changed.
				bool set = touched.Contains(entry.Key) && entry.Value.tag != ValueTag.Vector4 && entry.Value.tag != ValueTag.Color4;
				if (set || !before.TryGetValue(entry.Key, out (ValueTag tag, object value) old) || !Equals(old.value, entry.Value.value))
				{
					changed.Add(entry.Key);
				}
			}

			var removed = new List<string>();
			foreach (string name in before.Keys)
			{
				if (!after.ContainsKey(name))
				{
					removed.Add(name);
				}
			}

			if (changed.Count == 0 && removed.Count == 0)
			{
				return false;
			}

			foreach (Material other in others)
			{
				// The shader first: a new one fills in its defaults, which the values below then replace.
				if (changed.Contains(ShaderPath) && primary.UsingShader != null)
				{
					other.shader = primary.UsingShader;
				}

				if (changed.Contains(RenderQueuePath))
				{
					other.renderQueue = (int)after[RenderQueuePath].value;
				}

				foreach (string name in changed)
				{
					MaterialProperty property = name == ShaderPath || name == RenderQueuePath ? null : primary.GetProperty(name);
					before.TryGetValue(name, out (ValueTag tag, object value) old);
					switch (property)
					{
						case FloatProperty f:
							other.SetFloat(name, f.value);
							break;
						case IntProperty i:
							other.SetInt(name, i.value);
							break;
						case VectorProperty v:
						{
							// Only the components the edit changed; the other material keeps the rest.
							Vector4 theirs = other.GetVector(name, v.value);
							Vector4 was = old.value is Vector4 previous ? previous : theirs;
							other.SetVector(name, new Vector4(
								was.X != v.value.X ? v.value.X : theirs.X, was.Y != v.value.Y ? v.value.Y : theirs.Y,
								was.Z != v.value.Z ? v.value.Z : theirs.Z, was.W != v.value.W ? v.value.W : theirs.W));
							break;
						}
						case ColorProperty c:
						{
							Color4 theirs = other.GetColor(name, c.value);
							Color4 was = old.value is Color4 previous ? previous : theirs;
							other.SetColor(name, new Color4(
								was.R != c.value.R ? c.value.R : theirs.R, was.G != c.value.G ? c.value.G : theirs.G,
								was.B != c.value.B ? c.value.B : theirs.B, was.A != c.value.A ? c.value.A : theirs.A));
							break;
						}
						case TextureProperty t:
							other.SetTexture(name, t.value);
							break;
					}
				}

				foreach (string name in removed)
				{
					other.RemoveProperty(name);
				}
			}

			return true;
		}

		#endregion

		#region PrivateMethods

		private static bool DrawShaderRow(Material material, InspectorContext context)
		{
			context.Path = ShaderPath;
			FieldDrawer.Label("Shader", null, context);
			string current = material.shader?.AssetPath ?? "None";
			bool changed = false;
			if (EditorGui.BeginCombo("##shader", context.IsMixed() ? EditorGui.MixedValue : current, ImGuiComboFlags.HeightLarge))
			{
				// The shaders by folder (levels), a search's matches flat.
				List<string> choices = ShaderChoices(context.Editor);
				LevelList.SearchField("##shader_search", ref shaderSearch);
				int chosen = shaderLevels.Draw(shaderSearch, i => string.Equals(choices[i], current, StringComparison.OrdinalIgnoreCase));
				if (chosen >= 0 && (context.IsMixed() || !string.Equals(choices[chosen], current, StringComparison.OrdinalIgnoreCase)))
				{
					context.Editor.Edits.Begin(material, context.UndoGroup);
					material.shader = InternalStorage.GetOrCreateShader(choices[chosen]);
					context.Editor.Edits.RequestCommit();
					context.Touch();
					changed = true;
					ImGui.CloseCurrentPopup();
				}

				EditorGui.EndCombo();
			}

			if (ImGui.BeginDragDropTarget())
			{
				if (!ImGui.AcceptDragDropPayload(ProjectPanel.DragType).IsNull && ProjectPanel.DraggedAsset != null && ProjectPanel.DraggedAsset.EndsWith(".shader", StringComparison.OrdinalIgnoreCase))
				{
					context.Editor.Edits.Begin(material, context.UndoGroup);
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
				EditorGui.SetTooltip("Declared by the shader in its Properties block (Blend, ZWrite, ZTest, Cull, Queue).");
			}

			context.Path = RenderQueuePath;
			FieldDrawer.Label("Render Queue", null, context);
			int queue = material.renderQueue;
			ImGui.SetNextItemWidth(Math.Max(60f, ImGui.GetContentRegionAvail().X - 110f));
			bool changed = EditorGui.InputInt("##queue", ref queue, 10, 100);
			if (context.IsMixed() && !ImGui.IsItemActive())
			{
				EditorGui.MixedOverlay();
			}

			context.Track(changed);
			if (changed)
			{
				// Back on the shader's own queue: no override.
				material.renderQueue = queue == state.Queue ? -1 : Math.Max(0, queue);
			}

			ImGui.SameLine();
			if (material.OverridesRenderQueue)
			{
				if (EditorGui.SmallButton("Shader's"))
				{
					context.Editor.Edits.Begin(material, context.UndoGroup);
					material.renderQueue = -1;
					context.Editor.Edits.RequestCommit();
					changed = true;
				}

				if (ImGui.IsItemHovered())
				{
					EditorGui.SetTooltip("Follow the shader's queue (" + state.Queue + ") again.");
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
			shaderLevels = LevelList.OfPaths(shaderChoices);
			return shaderChoices;
		}

		private static bool DrawDeclared(Material material, ShaderProperty declared, InspectorContext context)
		{
			string name = declared.Name;
			string label = string.IsNullOrEmpty(declared.DisplayName) ? name : declared.DisplayName;
			if (declared.IsKeywordEnum)
			{
				return DrawKeywordEnum(material, declared, label, context);
			}

			if (declared.IsToggle)
			{
				return DrawKeywordToggle(material, declared, label, context);
			}

			// A material without a value (saved before the shader declared it) draws with the declared default, the standard
			// parameters with the engine's (Material.ResetStandardUniforms): show what it draws with.
			Vector4 fallback = !Material.IsStandardUniform(name) ? declared.DefaultValue : name == "Color" ? Vector4.One : Vector4.Zero;
			switch (declared.Type)
			{
				case ShaderPropertyType.Color:
					return DrawColor(material, name, label, declared.Hdr, context, new Color4(fallback.X, fallback.Y, fallback.Z, fallback.W));
				case ShaderPropertyType.Range:
					return DrawRange(material, name, label, declared.RangeMin, declared.RangeMax, context, fallback.X);
				case ShaderPropertyType.Float:
					return DrawFloat(material, name, label, context, fallback.X);
				case ShaderPropertyType.Int:
					return DrawInt(material, name, label, context, (int)fallback.X);
				case ShaderPropertyType.Vector:
					return DrawVector(material, name, label, 4, context, fallback);
				case ShaderPropertyType.Cubemap:
					return DrawTexture(material, name, label, typeof(Cubemap), context);
				case ShaderPropertyType.Texture3D:
					return DrawTexture(material, name, label, typeof(Texture3D), context);
				default:
					bool changed = DrawTexture(material, name, label, typeof(Texture2D), context);
					if (declared.IsMapKeyword && ImGui.IsItemHovered())
					{
						EditorGui.SetTooltip("Keyword " + declared.Keywords[0] + " while a texture is set");
					}

					return changed;
			}
		}

		private static bool DrawUniform(Material material, string name, UniformType type, InspectorContext context)
		{
			string label = Serialization.SerializationUtility.NicifyName(name);
			switch (type)
			{
				case UniformType.Vector4:
					return IsColorName(name) ? DrawColor(material, name, label, IsHdrName(name), context) : DrawVector(material, name, label, 4, context);
				case UniformType.Vector3:
					return IsColorName(name) ? DrawColor(material, name, label, IsHdrName(name), context) : DrawVector(material, name, label, 3, context);
				case UniformType.Vector2:
					return DrawVector(material, name, label, 2, context);
				case UniformType.Float:
					return DrawFloat(material, name, label, context);
				case UniformType.Int:
				case UniformType.UInt:
					return DrawInt(material, name, label, context);
				case UniformType.Bool:
					return DrawBool(material, name, label, context);
				case UniformType.Sampler2D:
					return DrawTexture(material, name, label, typeof(Texture2D), context);
				case UniformType.SamplerCube:
					return DrawTexture(material, name, label, typeof(Cubemap), context);
				case UniformType.Sampler3D:
					return DrawTexture(material, name, label, typeof(Texture3D), context);
				default:
					FieldDrawer.Label(label, null, context);
					ImGui.TextDisabled(type.ToString());
					return false;
			}
		}

		private static bool DrawColor(Material material, string name, string label, bool hdr, InspectorContext context, Color4? fallback = null)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			Color4 color = material.GetColor(name, fallback ?? Color4.White);
			NVector4 n = new NVector4(color.R, color.G, color.B, color.A);
			ImGuiColorEditFlags flags = ImGuiColorEditFlags.Float | ImGuiColorEditFlags.AlphaPreviewHalf;
			if (hdr)
			{
				flags |= ImGuiColorEditFlags.Hdr;
			}

			bool changed = EditorGui.ColorEdit4("##v", ref n, flags);
			if (context.IsMixed() && !ImGui.IsItemActive())
			{
				EditorGui.MixedOverlay();
			}

			context.Track(changed);
			if (changed)
			{
				material.SetColor(name, new Color4(n.X, n.Y, n.Z, n.W));
			}

			return changed;
		}

		private static bool DrawRange(Material material, string name, string label, float min, float max, InspectorContext context, float fallback = 0f)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			float value = material.GetFloat(name, fallback);
			bool changed = EditorGui.SliderFloat("##v", ref value, min, max);
			if (context.IsMixed() && !ImGui.IsItemActive())
			{
				EditorGui.MixedOverlay();
			}

			context.Track(changed);
			if (changed)
			{
				material.SetFloat(name, value);
			}

			return changed;
		}

		private static bool DrawFloat(Material material, string name, string label, InspectorContext context, float fallback = 0f)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			float value = material.GetFloat(name, fallback);
			bool changed = EditorGui.DragFloat("##v", ref value, Math.Max(0.005f, Math.Abs(value) * 0.01f), 0f, 0f, context.IsMixed() ? EditorGui.MixedValue : "%.3f");
			context.Track(changed);
			if (changed)
			{
				material.SetFloat(name, value);
			}

			return changed;
		}

		private static bool DrawInt(Material material, string name, string label, InspectorContext context, int fallback = 0)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			int value = material.GetInt(name, fallback);
			bool changed = EditorGui.DragInt("##v", ref value, 0.2f, 0, 0, context.IsMixed() ? EditorGui.MixedValue : "%d");
			context.Track(changed);
			if (changed)
			{
				material.SetInt(name, value);
			}

			return changed;
		}

		// [KeywordEnum]: a combo of the options; the value is the option's index (it picks the variant).
		private static bool DrawKeywordEnum(Material material, ShaderProperty declared, string label, InspectorContext context)
		{
			context.Path = declared.Name;
			FieldDrawer.Label(label, null, context);
			int current = declared.OptionIndex(material.GetFloat(declared.Name, declared.DefaultValue.X));
			bool mixed = context.IsMixed();
			bool changed = false;
			if (EditorGui.BeginCombo("##v", mixed ? EditorGui.MixedValue : declared.EnumOptions[current]))
			{
				for (int i = 0; i < declared.EnumOptions.Length; i++)
				{
					if (EditorGui.Selectable(declared.EnumOptions[i], !mixed && i == current) && (mixed || i != current))
					{
						context.BeginEdit();
						SetKeywordValue(material, declared, i);
						context.EndEdit();
						context.Touch();
						changed = true;
					}

					TestAnchors.RecordItem("materialoption:" + declared.Name + "/" + i);
					if (ImGui.IsItemHovered())
					{
						EditorGui.SetTooltip("Keyword " + declared.Keywords[i]);
					}
				}

				EditorGui.EndCombo();
			}
			else
			{
				TestAnchors.RecordItem("materialfield:" + declared.Name);
			}

			return changed;
		}

		// [Toggle]: a checkbox (1 / 0) that switches the keyword.
		private static bool DrawKeywordToggle(Material material, ShaderProperty declared, string label, InspectorContext context)
		{
			context.Path = declared.Name;
			FieldDrawer.Label(label, null, context);
			bool mixed = context.IsMixed();
			bool value = !mixed && material.GetFloat(declared.Name, declared.DefaultValue.X) != 0f;
			ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
			bool changed = EditorGui.Checkbox("##v", ref value);
			ImGui.PopItemFlag();
			TestAnchors.RecordItem("materialfield:" + declared.Name);
			if (ImGui.IsItemHovered())
			{
				EditorGui.SetTooltip("Keyword " + declared.Keywords[0]);
			}

			context.Track(changed);
			if (changed)
			{
				SetKeywordValue(material, declared, value ? 1 : 0);
			}

			return changed;
		}

		private static void SetKeywordValue(Material material, ShaderProperty declared, int value)
		{
			if (declared.Type == ShaderPropertyType.Int)
			{
				material.SetInt(declared.Name, value);
			}
			else
			{
				material.SetFloat(declared.Name, value);
			}
		}

		private static bool DrawBool(Material material, string name, string label, InspectorContext context)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			bool mixed = context.IsMixed();
			bool value = !mixed && material.GetInt(name) != 0;
			ImGui.PushItemFlag((ImGuiItemFlags)ImGuiItemFlagsPrivate.MixedValue, mixed);
			bool changed = EditorGui.Checkbox("##v", ref value);
			ImGui.PopItemFlag();
			context.Track(changed);
			if (changed)
			{
				material.SetInt(name, value ? 1 : 0);
			}

			return changed;
		}

		private static bool DrawVector(Material material, string name, string label, int components, InspectorContext context, Vector4 fallback = default)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			Vector4 value = material.GetVector(name, fallback);
			// The components that differ between materials edited together show "—".
			Span<float> n = stackalloc float[] { value.X, value.Y, value.Z, value.W };
			bool changed = EditorGui.DragFloats("##v", n.Slice(0, Math.Clamp(components, 2, 4)), context.MixedComponents(4), 0.05f);
			value = new Vector4(n[0], n[1], n[2], n[3]);

			context.Track(changed);
			if (changed)
			{
				material.SetVector(name, value);
			}

			return changed;
		}

		// Unity's texture Tiling and Offset: a Vector property named after a texture plus "_ST" (xy tiling, zw offset) is
		// drawn under that texture as two rows instead of on its own.
		private static ShaderProperty FindScaleOffset(ShaderProgram shader, string textureName)
		{
			ShaderProperty property = shader.FindProperty(textureName + "_ST");
			return property != null && property.Type == ShaderPropertyType.Vector ? property : null;
		}

		private static bool IsScaleOffsetOfTexture(ShaderProgram shader, ShaderProperty declared)
		{
			if (declared.Type != ShaderPropertyType.Vector || !declared.Name.EndsWith("_ST", StringComparison.Ordinal))
			{
				return false;
			}

			ShaderProperty texture = shader.FindProperty(declared.Name.Substring(0, declared.Name.Length - 3));
			return texture != null && texture.IsTexture;
		}

		private static bool DrawScaleOffset(Material material, ShaderProperty declared, InspectorContext context)
		{
			Vector4 value = material.GetVector(declared.Name, declared.DefaultValue);
			Span<float> tiling = stackalloc float[] { value.X, value.Y };
			Span<float> offset = stackalloc float[] { value.Z, value.W };
			context.Path = declared.Name;
			bool[] mixed = context.MixedComponents(4);
			ImGui.PushID(declared.Name);
			FieldDrawer.Label("Tiling", null, context);
			bool tilingChanged = EditorGui.DragFloats("##tiling", tiling, mixed, 0.01f);
			context.Track(tilingChanged);
			FieldDrawer.Label("Offset", null, context);
			bool offsetChanged = EditorGui.DragFloats("##offset", offset, mixed != null ? new[] { mixed[2], mixed[3] } : null, 0.01f);
			context.Track(offsetChanged);
			ImGui.PopID();
			if (tilingChanged || offsetChanged)
			{
				material.SetVector(declared.Name, new Vector4(tiling[0], tiling[1], offset[0], offset[1]));
				return true;
			}

			return false;
		}

		private static bool DrawTexture(Material material, string name, string label, Type textureType, InspectorContext context)
		{
			context.Path = name;
			FieldDrawer.Label(label, null, context);
			object value = material.GetTexture(name);
			Func<string, bool> filter = path => ShapeMatches(path, textureType);
			// Materials edited together: the field shows "—" where their textures differ.
			bool changed = context.Mixed != null ? ObjectField.Draw(textureType, ref value, context) : AssetField.Draw(textureType, ref value, filter);
			context.Track(changed);
			if (changed)
			{
				context.Editor.Edits.Begin(material, context.UndoGroup);
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
