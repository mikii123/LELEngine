using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Hexa.NET.ImGui;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Keyboard input for the game in play mode, as the window's keys reach it with the game view focused: the key held
	///     for the game's polls (<see cref="GameInputHost" />, the input actions), its down / up events (GetKeyDown /
	///     GetKeyUp, the game's UI), characters for the UI's text fields. Key names as <see cref="EditorInput" />'s (ImGui's).
	///     A paused game reads none of it, as with the keyboard.
	/// </summary>
	internal static class GameKeyboard
	{
		#region PrivateFields

		// ImGui's key -> the window's key (ImGuiController.MapKey inverted).
		private static Dictionary<ImGuiKey, Keys> windowKeys;

		#endregion

		#region PublicMethods

		/// <summary>"Q", "Space", "Ctrl+S": modifiers down, the key down, held <paramref name="holdSeconds" /> (at least a frame), all up.</summary>
		public static async Task Press(GameInputHost host, string keys, double holdSeconds = 0)
		{
			(ImGuiKey[] modifierKeys, ImGuiKey mainKey) = EditorInput.ParseChord(keys);
			Keys key = WindowKey(mainKey);
			var modifiers = new Keys[modifierKeys.Length];
			KeyModifiers held = 0;
			for (int i = 0; i < modifiers.Length; i++)
			{
				modifiers[i] = WindowKey(modifierKeys[i]);
				Set(host, modifiers[i], true, held);
				held |= ModifierFlag(modifierKeys[i]);
			}

			Set(host, key, true, held);
			try
			{
				await EditorInput.Hold(holdSeconds);
			}
			finally
			{
				Set(host, key, false, held);
				for (int i = modifiers.Length - 1; i >= 0; i--)
				{
					held &= ~ModifierFlag(modifierKeys[i]);
					Set(host, modifiers[i], false, held);
				}
			}

			await EditorAutomation.NextFrame(2);
		}

		/// <summary>Characters for the game UI's focused text field.</summary>
		public static async Task Text(string text)
		{
			foreach (char c in text)
			{
				Input.Input_TextInput(new TextInputEventArgs(c));
			}

			await EditorAutomation.NextFrame(2);
		}

		#endregion

		#region PrivateMethods

		private static void Set(GameInputHost host, Keys key, bool down, KeyModifiers modifiers)
		{
			host.SetKeyHeld(key, down);
			var e = new KeyboardKeyEventArgs(key, 0, modifiers, false);
			if (down)
			{
				Input.Input_KeyDown(e);
			}
			else
			{
				Input.Input_KeyUp(e);
			}
		}

		private static Keys WindowKey(ImGuiKey key)
		{
			switch (key)
			{
				case ImGuiKey.ModCtrl: return Keys.LeftControl;
				case ImGuiKey.ModShift: return Keys.LeftShift;
				case ImGuiKey.ModAlt: return Keys.LeftAlt;
				case ImGuiKey.ModSuper: return Keys.LeftSuper;
			}

			if (windowKeys == null)
			{
				windowKeys = new Dictionary<ImGuiKey, Keys>();
				foreach (Keys windowKey in Enum.GetValues<Keys>())
				{
					ImGuiKey mapped = ImGuiController.MapKey(windowKey);
					if (mapped != ImGuiKey.None)
					{
						windowKeys.TryAdd(mapped, windowKey);
					}
				}
			}

			return windowKeys.TryGetValue(key, out Keys found) ? found : throw new ArgumentException($"The game's keyboard has no key {key}.");
		}

		private static KeyModifiers ModifierFlag(ImGuiKey modifier)
		{
			switch (modifier)
			{
				case ImGuiKey.ModCtrl: return KeyModifiers.Control;
				case ImGuiKey.ModShift: return KeyModifiers.Shift;
				case ImGuiKey.ModAlt: return KeyModifiers.Alt;
				case ImGuiKey.ModSuper: return KeyModifiers.Super;
				default: return 0;
			}
		}

		#endregion
	}
}
