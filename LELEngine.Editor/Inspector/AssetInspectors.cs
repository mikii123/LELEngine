using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.Serialization;
using LELEngine.Shaders;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector of a selected project file: a material editor, texture import settings, shader properties,
	///     model import settings, physics material values, scene / script / UI document actions, or the plain file info.
	/// </summary>
	internal static class AssetInspectors
	{
		#region PublicMethods

		public static void Draw(string assetPath, InspectorContext context)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			if (!File.Exists(absolute))
			{
				ImGui.TextDisabled("The file no longer exists: " + assetPath);
				return;
			}

			Type type = AssetDatabase.GetAssetType(assetPath);
			string kind = Kind(assetPath, type);
			ImGui.TextUnformatted(Path.GetFileName(assetPath));
			ImGui.SameLine();
			ImGui.TextDisabled("(" + kind + ")");
			ImGui.TextDisabled(assetPath);
			string guid = AssetDatabase.GetGuid(assetPath);
			if (guid != null)
			{
				ImGui.TextDisabled("GUID " + guid);
			}

			// Engine shaders and built-in assets (Engine/...): shown, never edited.
			bool builtin = AssetDatabase.IsEngineResource(assetPath);
			if (builtin)
			{
				ImGui.TextDisabled("Built-in, read-only: part of the engine, in every project and game");
			}

			ImGui.Separator();
			ImGui.Spacing();
			ImGui.BeginDisabled(builtin);

			if (type == typeof(Material))
			{
				Material material = InternalStorage.GetOrCreateMaterial(assetPath);
				MaterialSection.Draw(material, context, "material:" + material.name, false);
			}
			else if (TextureAssets.IsImageFile(assetPath))
			{
				TextureImporterInspector.Draw(assetPath, context);
			}
			else if (type == typeof(ShaderProgram))
			{
				DrawShader(assetPath, absolute);
			}
			else if (type == typeof(Model))
			{
				ModelImporterInspector.Draw(assetPath, context);
			}
			else if (type == typeof(PhysicsMaterial2D))
			{
				PhysicsMaterial2D material = PhysicsMaterial2D.Load(assetPath);
				context.UndoTarget = material;
				if (FieldDrawer.DrawFields(material, context))
				{
					context.Editor.NotifySceneChanged();
				}
			}
			else if (type == typeof(ScriptableObject) && EditorIcons.KindOfFile(assetPath) == IconKind.InputConfiguration)
			{
				DrawInputConfiguration(assetPath, context);
			}
			else if (type == typeof(ScriptableObject))
			{
				ScriptableObjectInspector.Draw(assetPath, context);
			}
			else if (type == typeof(Prefab))
			{
				PrefabAssetInspector.Draw(assetPath, context);
			}
			else if (assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase))
			{
				if (ImGui.Button("Open Scene", new NVector2(-1, 0)))
				{
					context.Editor.OpenScene(assetPath);
				}

				DrawFileInfo(absolute);
			}
			else if (type == typeof(LELEngine.VFX.VfxGraph))
			{
				if (ImGui.Button("Open in VFX Graph", new NVector2(-1, 0)))
				{
					context.Editor.OpenVfxGraph(assetPath);
				}

				DrawVfxGraph(assetPath);
				DrawFileInfo(absolute);
			}
			else if (type == typeof(LELEngine.Audio.AudioProject))
			{
				Audio.AudioInspectors.DrawProject(assetPath, context);
			}
			else if (type == typeof(LELEngine.Audio.AudioClip))
			{
				Audio.AudioInspectors.DrawClip(assetPath, context);
			}
			else if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				if (ImGui.Button("Open Script", new NVector2(-1, 0)))
				{
					OpenExternally(absolute);
				}

				if (ImGui.Button("Open Script Project", new NVector2(-1, 0)) && context.Editor.Scripts != null)
				{
					context.Editor.Scripts.OpenInIde();
				}

				DrawFileInfo(absolute);
			}
			else if (assetPath.EndsWith(".uxml", StringComparison.OrdinalIgnoreCase) || assetPath.EndsWith(".uss", StringComparison.OrdinalIgnoreCase))
			{
				if (ImGui.Button("Open in UI Builder", new NVector2(-1, 0)))
				{
					context.Editor.OpenUIBuilder(assetPath);
				}

				DrawFileInfo(absolute);
			}
			else
			{
				if (ImGui.Button("Open", new NVector2(-1, 0)))
				{
					OpenExternally(absolute);
				}

				DrawFileInfo(absolute);
			}

			ImGui.EndDisabled();
		}

		/// <summary>Selection left the asset: inspectors drop their working state.</summary>
		public static void Deselected()
		{
			TextureImporterInspector.Reset();
			ModelImporterInspector.Reset();
		}

		#endregion

		#region PrivateMethods

		private static string Kind(string assetPath, Type type)
		{
			if (type == typeof(ShaderProgram)) return "Shader";
			if (type == typeof(Prefab)) return Prefab.Load(assetPath)?.IsVariant == true ? "Prefab Variant" : "Prefab";
			if (type == typeof(LELEngine.VFX.VfxGraph)) return "Visual Effect Graph";
			if (type == typeof(LELEngine.Audio.AudioProject)) return "Audio Project";
			if (type == typeof(LELEngine.Audio.AudioClip)) return "Audio Clip";
			if (type == typeof(ScriptableObject)) return (ScriptableObjectAssets.GetStoredType(assetPath)?.Name ?? "missing script") + " asset";
			if (TextureAssets.IsImageFile(assetPath))
			{
				var importer = AssetDatabase.GetMeta(assetPath).importer as TextureImporter ?? TextureImporter.CreateDefault(assetPath);
				switch (importer.shape)
				{
					case TextureImporterShape.Cube: return "Cubemap";
					case TextureImporterShape.Texture3D: return "Texture 3D";
					default: return importer.textureType == TextureImporterType.Sprite ? "Sprite" : "Texture 2D";
				}
			}

			if (type != null) return type.Name;
			if (assetPath.EndsWith(SceneSerializer.Extension, StringComparison.OrdinalIgnoreCase)) return "Scene";
			if (assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return "C# Script";
			return Path.GetExtension(assetPath).TrimStart('.').ToUpperInvariant() + " file";
		}

		private static void DrawShader(string assetPath, string absolute)
		{
			List<ShaderProperty> properties;
			try
			{
				properties = ShaderProgram.ReadProperties(absolute);
			}
			catch (Exception e)
			{
				properties = new List<ShaderProperty>();
				ImGui.TextColored(EditorStyle.Error, e.Message);
			}

			if (ImGui.Button("Open", new NVector2(-1, 0)))
			{
				OpenExternally(absolute);
			}

			ImGui.Spacing();
			ImGui.SeparatorText("Properties");
			if (properties.Count == 0)
			{
				ImGui.TextDisabled("No Properties block: materials show the shader's uniforms instead.");
			}

			foreach (ShaderProperty property in properties)
			{
				string type = property.Type == ShaderPropertyType.Range ? $"Range({property.RangeMin:0.##}, {property.RangeMax:0.##})" : property.Type.ToString();
				ImGui.BulletText(property.Name + "  (" + type + ")  \"" + property.DisplayName + "\"");
			}

			ShaderProgram program = InternalStorage.GetShader(assetPath);
			if (program != null)
			{
				ImGui.Spacing();
				ImGui.TextDisabled(program.IsLinked ? "Compiled" + (program.SupportsInstancing ? ", GPU-driven capable" : "") : "Does not compile (see the console)");
			}

			DrawFileInfo(absolute);
		}

		private static void DrawVfxGraph(string assetPath)
		{
			try
			{
				LELEngine.VFX.VfxGraph graph = LELEngine.VFX.VfxGraph.Load(assetPath);
				int contexts = graph.GetContexts().Count;
				ImGui.Text($"{contexts} contexts, {graph.Nodes.Count - contexts} blocks and operators, {graph.Properties.Count} properties");
				foreach (LELEngine.VFX.VfxProperty property in graph.Properties)
				{
					if (property.Exposed)
					{
						ImGui.BulletText(property.Name + "  (" + property.Value.Type + ")  " + property.Value);
					}
				}

				LELEngine.VFX.VfxProgramSet programs = LELEngine.VFX.VfxRuntime.PeekPrograms(graph);
				if (programs != null)
				{
					ImGui.TextDisabled(programs.HasErrors ? "Does not compile (open the graph for the messages)" : "Compiled: " + (programs.Compiled?.Systems.Count ?? 0) + " systems");
				}
			}
			catch (Exception e)
			{
				ImGui.TextColored(EditorStyle.Error, "Cannot load: " + e.Message);
			}
		}

		/// <summary>An input configuration: what it holds and a button to the Input Manager (its editor).</summary>
		private static void DrawInputConfiguration(string assetPath, InspectorContext context)
		{
			if (ImGui.Button("Open in Input Manager", new NVector2(-1, 0)))
			{
				context.Editor.OpenInputManager(assetPath);
			}

			if (ScriptableObjectAssets.Load(assetPath) is LELEngine.InputSystem.InputConfiguration configuration)
			{
				ImGui.TextDisabled($"{configuration.Actions.Count} actions, {configuration.Players.Count} players, {configuration.Maps.Count} controller maps, {configuration.Behaviors.Count} input behaviors");
				bool used = string.Equals(ProjectSettings.Current.Input.Configuration, assetPath, StringComparison.OrdinalIgnoreCase);
				ImGui.TextDisabled(used ? "The project's input configuration (Project Settings > Input)." : "Not the project's input configuration.");
			}

			DrawFileInfo(AssetDatabase.ToAbsolute(assetPath));
		}

		public static void DrawFileInfo(string absolute)
		{
			try
			{
				var info = new FileInfo(absolute);
				ImGui.Spacing();
				ImGui.TextDisabled((info.Length >= 1024 * 1024 ? $"{info.Length / (1024f * 1024f):0.0} MB" : $"{info.Length / 1024f:0.0} KB") + ", modified " + info.LastWriteTime.ToString("g"));
			}
			catch (Exception)
			{
				// No info.
			}
		}

		private static void OpenExternally(string absolute)
		{
			if (absolute.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
			{
				ExternalScriptEditor.Open(absolute, 1);
				return;
			}

			try
			{
				Process.Start(new ProcessStartInfo(absolute) { UseShellExecute = true });
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Editor] Cannot open " + absolute + ": " + e.Message);
			}
		}

		#endregion
	}
}
