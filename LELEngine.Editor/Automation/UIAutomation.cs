using System.Threading.Tasks;
using Hexa.NET.ImGui;

namespace LELEngine.Editor.Automation
{
	/// <summary>
	///     Invoking a UI element without the mouse: a request (element, action) waits for the element to be drawn in the next
	///     frames, and the editor's controls (<see cref="EditorGui" /> wrappers, <see cref="EditorMenu" />) act on it as if
	///     clicked, wherever the element is and whatever covers it:
	///     <list type="bullet">
	///         <item>buttons, selectables, menu items, checkboxes, combos, headers, text fields: ImGui's own activation of the
	///         item (what keyboard navigation does: the widget reports a press, a text field starts editing) for that call;</item>
	///         <item><see cref="EditorGui.IsItemClicked" /> and the double-click checks report the click for the item (rows whose
	///         code reads clicks, as the hierarchy's);</item>
	///         <item><see cref="EditorGui.BeginPopupContextItem" /> opens the item's context menu;</item>
	///         <item>tabs are selected (<see cref="ImGuiTabItemFlags.SetSelected" />);</item>
	///         <item>value fields (text, numbers, vectors, colors, checkboxes) take a value: the wrapper parses the text, writes
	///         it as the widget would and reports an edit that ended (the widget returns true, the item reads as edited and
	///         deactivated after an edit), so the code around it applies it with its undo.</item>
	///     </list>
	///     A request lasts a few frames: unanswered (the element was not drawn, or nothing reacted) it fails.
	/// </summary>
	internal static unsafe class UIAutomation
	{
		#region PublicFields

		public enum Action
		{
			Click,
			DoubleClick,
			ContextMenu,
			SetValue
		}

		/// <summary>Why the last request failed when its element refused it (a value it cannot take), else null.</summary>
		public static string LastError { get; private set; }

		#endregion

		#region PrivateFields

		private sealed class Request
		{
			public nint Context;
			public uint Window;
			public uint Item;
			public string Name;
			public Action Action;
			public int Button;
			public string Text;
			public bool Done;
			public bool Refused;
			public bool Armed;
			public int FramesLeft;
			public TaskCompletionSource<bool> Result;
		}

		private const int RequestFrames = 4;

		private static Request current;

		// The navigation state an activation replaced (put back right after the widget).
		private static uint savedActivateId;
		private static uint savedDownId;
		private static uint savedPressedId;
		private static ImGuiActivateFlags savedFlags;
		private static ImGuiInputSource savedSource;

		/// <summary>The item a click was reported for in this frame (a following double-click check of the same item reports too).</summary>
		private static uint clickedItem;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Asks for the element to be invoked in the next frames (<paramref name="text" />: the value for
		///     <see cref="Action.SetValue" />); true when it reacted (false: see <see cref="LastError" />).
		/// </summary>
		public static Task<bool> Invoke(ImGuiController owner, uint window, uint item, string name, Action action, int button, string text = null)
		{
			current?.Result.TrySetResult(false);
			LastError = null;
			current = new Request
			{
				Context = (nint)owner.Context.Handle,
				Window = window,
				Item = item,
				Name = name,
				Action = action,
				Button = button,
				Text = text,
				FramesLeft = RequestFrames,
				Result = new TaskCompletionSource<bool>()
			};
			return current.Result.Task;
		}

		/// <summary>Once per tick (between frames): a request answered ends, one unanswered for long enough fails.</summary>
		public static void Tick()
		{
			clickedItem = 0;
			if (current == null)
			{
				return;
			}

			if (current.Refused)
			{
				Request refused = current;
				current = null;
				refused.Result.TrySetResult(false);
			}
			else if (current.Done)
			{
				Request done = current;
				current = null;
				done.Result.TrySetResult(true);
			}
			else if (--current.FramesLeft <= 0)
			{
				Request failed = current;
				current = null;
				failed.Result.TrySetResult(false);
			}
			else
			{
				current.Armed = false;
			}
		}

		/// <summary>
		///     Before a widget with this label: when it is the element a click is asked for, the widget is activated for this call
		///     (true: call <see cref="EndActivation" /> after it). <paramref name="activate" /> false: only the click checks report
		///     it (tree rows that open on their arrow: an activation would fold them).
		/// </summary>
		public static bool BeginActivation(string label, bool activate = true)
		{
			if (current == null || current.Done || current.Action != Action.Click || current.Button != 0)
			{
				return false;
			}

			uint id = ImGui.GetID(label);
			if (!IsCurrent(id))
			{
				return false;
			}

			current.Done = true;
			clickedItem = id;
			if (!activate)
			{
				return false;
			}

			ImGuiContextPtr context = ImGui.GetCurrentContext();
			savedActivateId = context.NavActivateId;
			savedDownId = context.NavActivateDownId;
			savedPressedId = context.NavActivatePressedId;
			savedFlags = context.NavActivateFlags;
			savedSource = context.NavInputSource;
			context.NavActivateId = id;
			context.NavActivateDownId = id;
			context.NavActivatePressedId = id;
			// Text fields start editing with their text selected (typing replaces it), as when tabbed into.
			context.NavActivateFlags = ImGuiActivateFlags.PreferInput | ImGuiActivateFlags.FromTabbing;
			context.NavInputSource = ImGuiInputSource.Keyboard;
			return true;
		}

		public static void EndActivation(bool activated)
		{
			if (!activated)
			{
				return;
			}

			ImGuiContextPtr context = ImGui.GetCurrentContext();
			context.NavActivateId = savedActivateId;
			context.NavActivateDownId = savedDownId;
			context.NavActivatePressedId = savedPressedId;
			context.NavActivateFlags = savedFlags;
			context.NavInputSource = savedSource;
		}

		/// <summary>A click check of the last item (<paramref name="button" />): true when a click (or double click) of it is asked for.</summary>
		public static bool ItemClicked(uint item, int button)
		{
			if (item != 0 && item == clickedItem && button == 0)
			{
				return true;
			}

			if (current == null || current.Done || !IsCurrent(item))
			{
				return false;
			}

			bool clicked = (current.Action == Action.Click && current.Button == button) || (current.Action == Action.DoubleClick && button == 0);
			if (clicked)
			{
				// A double click answers when its double-click check runs (or right here when the code reads clicks only).
				current.Done = current.Action == Action.Click || current.Done;
				clickedItem = item;
				if (current.Action == Action.DoubleClick)
				{
					current.Armed = true;
				}
			}

			return clicked;
		}

		/// <summary>
		///     Before a value widget with this id: the text automation sets as its value, or null. The wrapper parses it,
		///     writes the value after the widget and calls <see cref="ValueApplied" />.
		/// </summary>
		public static string ValueRequested(uint id)
		{
			return current != null && !current.Done && !current.Refused && current.Action == Action.SetValue && IsCurrent(id) ? current.Text : null;
		}

		/// <summary>
		///     Right after the value widget whose value was set: <paramref name="error" /> null answers the request, else it
		///     fails with that message. <paramref name="changed" />: the value differs, and the widget reports an edit that
		///     ended (edited, deactivated after an edit), as after typing and Enter.
		/// </summary>
		public static void ValueApplied(bool changed, string error = null)
		{
			if (current == null)
			{
				return;
			}

			if (error != null)
			{
				current.Refused = true;
				LastError = $"'{current.Name}' cannot take '{current.Text}': {error}";
				return;
			}

			current.Done = true;
			if (!changed)
			{
				return;
			}

			ImGuiContextPtr context = ImGui.GetCurrentContext();
			ref ImGuiLastItemData last = ref context.LastItemData;
			last.StatusFlags |= ImGuiItemStatusFlags.Edited | ImGuiItemStatusFlags.HasDeactivated | ImGuiItemStatusFlags.Deactivated;
			ref ImGuiDeactivatedItemData deactivated = ref context.DeactivatedItemData;
			deactivated.ID = last.ID;
			deactivated.ElapseFrame = context.FrameCount;
			deactivated.HasBeenEditedBefore = 1;
			deactivated.IsAlive = 1;
		}

		/// <summary>A click of this item was reported in this frame (by its activation or a click check).</summary>
		public static bool WasClicked(uint item)
		{
			return item != 0 && item == clickedItem;
		}

		/// <summary>A double-click check of the last item.</summary>
		public static bool ItemDoubleClicked(uint item)
		{
			if (current == null || current.Done || current.Action != Action.DoubleClick || !IsCurrent(item))
			{
				return false;
			}

			current.Done = true;
			clickedItem = item;
			return true;
		}

		/// <summary>The context menu of the last item is asked for (by its id, or the element was just recorded in this window).</summary>
		public static bool ContextRequested(uint item)
		{
			if (current == null || current.Done || current.Action != Action.ContextMenu)
			{
				return false;
			}

			if (IsCurrent(item) || (current.Armed && (nint)ImGui.GetCurrentContext().Handle == current.Context && TestAnchors.CurrentWindowId() == current.Window))
			{
				current.Done = true;
				return true;
			}

			return false;
		}

		/// <summary>A tab whose element name is asked to be clicked: it is selected.</summary>
		public static bool TabRequested(string name)
		{
			if (current == null || current.Done || current.Action != Action.Click || (nint)ImGui.GetCurrentContext().Handle != current.Context
				|| TestAnchors.CurrentWindowId() != current.Window || !string.Equals(name, current.Name, System.StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			current.Done = true;
			return true;
		}

		/// <summary>An anchor was recorded: a context menu asked for it opens at the window's next context menu check.</summary>
		public static void Recorded(string name, TestAnchors.Entry entry)
		{
			if (current != null && !current.Done && current.Action == Action.ContextMenu && entry.Window == current.Window
				&& string.Equals(name, current.Name, System.StringComparison.OrdinalIgnoreCase))
			{
				current.Armed = true;
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsCurrent(uint item)
		{
			return item != 0 && item == current.Item && (nint)ImGui.GetCurrentContext().Handle == current.Context && TestAnchors.CurrentWindowId() == current.Window;
		}

		#endregion
	}
}
