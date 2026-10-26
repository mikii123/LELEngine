using System;
using System.IO;
using Hexa.NET.ImGui;
using NVector2 = System.Numerics.Vector2;

namespace LELEngine.Editor
{
	/// <summary>
	///     The inspector of a ScriptableObject asset (.asset): its class (with a button opening the script) and its
	///     serialized fields. Edits are undoable, saved to the asset's file when they end, and run OnValidate.
	/// </summary>
	internal static class ScriptableObjectInspector
	{
		#region PublicMethods

		public static void Draw(string assetPath, InspectorContext context)
		{
			ScriptableObject asset = ScriptableObjectAssets.Load(assetPath);
			if (asset == null)
			{
				string typeName = ScriptableObjectAssets.GetTypeName(assetPath);
				ImGui.TextColored(EditorStyle.Warning, "Missing script: " + (typeName ?? "unknown class"));
				ImGui.PushTextWrapPos(0f);
				ImGui.TextDisabled("The class of this asset was not found (renamed, deleted, or the scripts do not compile). Its data stays in the file.");
				ImGui.PopTextWrapPos();
				return;
			}

			Type type = asset.GetType();
			FieldDrawer.Label("Script", null, context);
			string script = ScriptFiles.Find(type);
			ImGui.BeginDisabled(script == null);
			if (EditorGui.Button(type.Name + (script != null ? "##script" : " (no script file)##script"), new NVector2(-1f, 0f)) && script != null)
			{
				ScriptFiles.Open(type);
			}

			ImGui.EndDisabled();
			if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(type.FullName + (script != null ? "\nOpen " + script : ""));
			}

			ImGui.Separator();
			context.UndoTarget = asset;
			if (FieldDrawer.DrawFields(asset, context))
			{
				asset.Validate();
				context.Editor.NotifySceneChanged();
			}
		}

		#endregion
	}
}
