using System;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;

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

		#endregion

		#region Constructors

		public GameInputHost(NativeWindow window, Func<bool> active)
		{
			this.window = window;
			this.active = active;
		}

		#endregion

		#region PublicMethods

		public MouseState MouseState => window.MouseState;

		public CursorState CursorState
		{
			get => window.CursorState;
			set => window.CursorState = active() || value == CursorState.Normal ? value : CursorState.Normal;
		}

		public Vector2i ClientSize => window.ClientSize;

		public bool IsKeyDown(Keys key)
		{
			return active() && window.IsKeyDown(key);
		}

		public bool IsMouseButtonDown(MouseButton button)
		{
			return active() && window.IsMouseButtonDown(button);
		}

		#endregion
	}
}
