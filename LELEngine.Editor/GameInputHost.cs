using System;
using System.Collections.Generic;
using LELEngine.Mathematics;
using LELEngine.Windowing;

namespace LELEngine.Editor
{
	/// <summary>
	///     The game's view of the input inside the editor: the window's keyboard and mouse while the game plays and
	///     the game view has focus, nothing otherwise (so typing in the inspector never moves the player).
	/// </summary>
	internal sealed class GameInputHost : IInputHost
	{
		#region PrivateFields

		private readonly NativeWindow window;
		private readonly Func<bool> active;
		private readonly Func<Vector2> imageOrigin;

		// Keys held by automation (the CLI's "key" in play mode): down for the game whatever has focus.
		private readonly HashSet<Keys> heldKeys = new HashSet<Keys>();

		#endregion

		#region Constructors

		/// <param name="imageOrigin">Where the game view's image starts in the window (the game's pixel 0, 0).</param>
		public GameInputHost(NativeWindow window, Func<bool> active, Func<Vector2> imageOrigin)
		{
			this.window = window;
			this.active = active;
			this.imageOrigin = imageOrigin;
		}

		#endregion

		#region PublicMethods

		public MouseState MouseState => window.MouseState;

		public CursorState CursorState
		{
			get => window.CursorState;
			set => window.CursorState = active() || value == CursorState.Normal ? value : CursorState.Normal;
		}

		/// <summary>Size of the game view image (the game's "window"); set by the game view panel every frame.</summary>
		public Vector2i ViewSize { get; set; }

		public Vector2i ClientSize => ViewSize.X > 0 && ViewSize.Y > 0 ? ViewSize : window.ClientSize;

		public bool IsKeyDown(Keys key)
		{
			return heldKeys.Contains(key) || (active() && window.IsKeyDown(key));
		}

		/// <summary>Holds or releases a key for the game, as if pressed on the keyboard with the game view focused.</summary>
		public void SetKeyHeld(Keys key, bool held)
		{
			if (held)
			{
				heldKeys.Add(key);
			}
			else
			{
				heldKeys.Remove(key);
			}
		}

		public bool IsMouseButtonDown(MouseButton button)
		{
			return active() && window.IsMouseButtonDown(button);
		}

		/// <summary>The editor's cursor moves only while the game has the input (a game never takes it from the editor's panels).</summary>
		public void WarpCursor(Vector2 position)
		{
			if (active())
			{
				window.MousePosition = position + imageOrigin();
			}
		}

		#endregion
	}
}
