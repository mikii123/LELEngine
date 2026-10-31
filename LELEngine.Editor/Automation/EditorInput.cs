using System;
using System.Threading.Tasks;
using Hexa.NET.ImGui;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Keyboard input for an editor window's ImGui context (the focused window, the active text field), spread over frames.
	///     There is no mouse here: UI elements are invoked through their own actions (<see cref="UIAutomation" />).
	/// </summary>
	internal static class EditorInput
	{
		#region PublicMethods

		/// <summary>
		///     "Ctrl+Shift+S", "F2", "Enter": modifiers down, the key down then up, modifiers up. Held for
		///     <paramref name="holdSeconds" /> (at least a frame).
		/// </summary>
		public static async Task Keys(ImGuiController owner, string keys, double holdSeconds = 0)
		{
			(ImGuiKey[] mods, ImGuiKey key) = ParseChord(keys);
			Send(owner, io =>
			{
				SetModifiers(io, mods, true);
				io.AddKeyEvent(key, true);
			});
			await Hold(holdSeconds);
			Send(owner, io =>
			{
				io.AddKeyEvent(key, false);
				SetModifiers(io, mods, false);
			});
			await EditorAutomation.NextFrame(2);
		}

		/// <summary>Characters for the active text field.</summary>
		public static async Task Text(ImGuiController owner, string text)
		{
			Send(owner, io =>
			{
				foreach (char c in text)
				{
					io.AddInputCharacter(c);
				}
			});
			await EditorAutomation.NextFrame(2);
		}

		/// <summary>"Ctrl+Shift+S": the modifiers and the key (ImGui key names, Ctrl / Shift / Alt / Super modifiers).</summary>
		internal static (ImGuiKey[] modifiers, ImGuiKey key) ParseChord(string keys)
		{
			string[] parts = keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			if (parts.Length == 0)
			{
				throw new ArgumentException("No key given.");
			}

			return (ParseModifiers(string.Join("+", parts, 0, parts.Length - 1)), ParseKey(parts[^1]));
		}

		/// <summary>Waits a frame, then on until <paramref name="seconds" /> have passed.</summary>
		internal static async Task Hold(double seconds)
		{
			DateTime end = DateTime.UtcNow.AddSeconds(seconds);
			do
			{
				await EditorAutomation.NextFrame();
			}
			while (DateTime.UtcNow < end);
		}

		#endregion

		#region PrivateMethods

		/// <summary>Queues input in the owner's ImGui context (it is read at that context's next frame).</summary>
		private static void Send(ImGuiController owner, Action<ImGuiIOPtr> input)
		{
			ImGuiContextPtr previous = ImGui.GetCurrentContext();
			ImGui.SetCurrentContext(owner.Context);
			try
			{
				input(ImGui.GetIO());
			}
			finally
			{
				ImGui.SetCurrentContext(previous);
			}
		}

		private static void SetModifiers(ImGuiIOPtr io, ImGuiKey[] modifiers, bool down)
		{
			foreach (ImGuiKey modifier in modifiers)
			{
				io.AddKeyEvent(modifier, down);
			}
		}

		private static ImGuiKey[] ParseModifiers(string modifiers)
		{
			if (string.IsNullOrWhiteSpace(modifiers))
			{
				return Array.Empty<ImGuiKey>();
			}

			return Array.ConvertAll(modifiers.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), name => name.ToLowerInvariant() switch
			{
				"ctrl" or "control" => ImGuiKey.ModCtrl,
				"shift" => ImGuiKey.ModShift,
				"alt" => ImGuiKey.ModAlt,
				"super" or "win" or "cmd" => ImGuiKey.ModSuper,
				_ => throw new ArgumentException("Unknown modifier " + name + " (Ctrl, Shift, Alt, Super)")
			});
		}

		private static ImGuiKey ParseKey(string name)
		{
			switch (name.ToLowerInvariant())
			{
				case "del": return ImGuiKey.Delete;
				case "esc": return ImGuiKey.Escape;
				case "return": return ImGuiKey.Enter;
				case "up": return ImGuiKey.UpArrow;
				case "down": return ImGuiKey.DownArrow;
				case "left": return ImGuiKey.LeftArrow;
				case "right": return ImGuiKey.RightArrow;
				case "pgup": return ImGuiKey.PageUp;
				case "pgdn": return ImGuiKey.PageDown;
			}

			if (name.Length == 1 && char.IsDigit(name[0]))
			{
				return ImGuiKey.Key0 + (name[0] - '0');
			}

			if (Enum.TryParse(name, true, out ImGuiKey key) && key != ImGuiKey.None)
			{
				return key;
			}

			throw new ArgumentException("Unknown key " + name + " (ImGui key names: A, F2, Enter, Escape, Delete, Tab, Space, UpArrow, ...)");
		}

		#endregion
	}
}
