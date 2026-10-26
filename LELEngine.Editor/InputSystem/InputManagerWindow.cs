using System;
using System.Collections.Generic;
using System.IO;
using Hexa.NET.ImGui;
using LELEngine.InputSystem;
using OpenTK.Windowing.GraphicsLibraryFramework;
using InputAction = LELEngine.InputSystem.InputAction;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Window > Input Manager (Rewired's editor): the pages of an <see cref="InputConfiguration" />: settings, players
	///     with their starting maps, actions, action / map categories, layouts, input behaviors, the keyboard, mouse and
	///     gamepad maps (bindings per action; Listen binds the next key, button or stick pushed), and Debug (play mode:
	///     every player's actions live; connected gamepads always). Edits are undo steps and save the asset when they end.
	/// </summary>
	internal sealed class InputManagerWindow : IEditorPanel
	{
		#region PublicFields

		public bool Open;

		string IEditorPanel.PanelName => "Input Manager";
		string IEditorPanel.ImGuiName => "Input Manager";
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(1000, 700);
		void IEditorPanel.Focus() => EditorApplication.Instance.OpenInputManager(null);

		/// <summary>The configuration shown (the project's by default).</summary>
		public string AssetPath => assetPath;

		#endregion

		#region PrivateFields

		private enum Page
		{
			Settings,
			Players,
			Actions,
			ActionCategories,
			MapCategories,
			Layouts,
			Behaviors,
			KeyboardMaps,
			MouseMaps,
			GamepadMaps,
			Debug
		}

		private static readonly (Page page, string label)[] Pages =
		{
			(Page.Settings, "Settings"),
			(Page.Players, "Players"),
			(Page.Actions, "Actions"),
			(Page.ActionCategories, "Action Categories"),
			(Page.MapCategories, "Map Categories"),
			(Page.Layouts, "Layouts"),
			(Page.Behaviors, "Input Behaviors"),
			(Page.KeyboardMaps, "Keyboard Maps"),
			(Page.MouseMaps, "Mouse Maps"),
			(Page.GamepadMaps, "Gamepad Maps"),
			(Page.Debug, "Debug")
		};

		/// <summary>A binding waiting for the next key, button or axis (Listen).</summary>
		private sealed class Listening
		{
			public ControllerType Type;
			public ControllerMap Map;
			public InputAction Action;

			/// <summary>The binding replaced (null: a new one is added).</summary>
			public ActionElementMap Binding;

			public AxisContribution Contribution;
			public bool WaitingForRelease = true;
			public double Started;
		}

		private readonly EditorApplication editor;
		private readonly InspectorContext context;
		private string assetPath;
		private Page page = Page.Actions;
		private int selectedPlayer;
		private int selectedBehavior;
		private readonly int[] mapCategory = new int[3];
		private readonly int[] mapLayout = new int[3];
		private string actionFilter = "";
		private string keyFilter = "";
		private Listening listening;
		private Page? requestedPage;

		#endregion

		#region Constructors

		public InputManagerWindow(EditorApplication editor)
		{
			this.editor = editor;
			context = new InspectorContext { Editor = editor };
		}

		#endregion

		#region PublicMethods

		/// <summary>Opens the window on a configuration (null: the project's).</summary>
		public void Show(string configurationPath)
		{
			assetPath = configurationPath ?? (string.IsNullOrEmpty(ProjectSettings.Current.Input.Configuration) ? assetPath : ProjectSettings.Current.Input.Configuration);
			Open = true;
			ImGui.SetWindowFocus("Input Manager");
		}

		/// <summary>Shows a page (test hooks: inputmanager=N;Page).</summary>
		public void ShowPage(string pageName)
		{
			if (Enum.TryParse(pageName, true, out Page parsed))
			{
				requestedPage = parsed;
			}
		}

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(1120f, 640f), ImGuiCond.FirstUseEver);
			if (ImGui.Begin("Input Manager", ref Open))
			{
				try
				{
					DrawContent();
				}
				catch (Exception e)
				{
					Debug.LogException(e);
				}
			}

			ImGui.End();
		}

		/// <summary>An asset moved (rename): the window follows it.</summary>
		public void AssetMoved(string fromPath, string toPath)
		{
			assetPath = AssetDatabase.MovedPath(assetPath, fromPath, toPath) ?? assetPath;
		}

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			if (editor.Project == null)
			{
				return;
			}

			assetPath ??= ProjectSettings.Current.Input.Configuration;
			InputConfiguration configuration = !string.IsNullOrEmpty(assetPath) && File.Exists(AssetDatabase.ToAbsolute(assetPath)) ? ScriptableObjectAssets.Load(assetPath) as InputConfiguration : null;
			DrawHeader(configuration);
			if (configuration == null)
			{
				return;
			}

			context.UndoTarget = configuration;
			context.Scene = editor.Scene;
			context.LabelWidth = 220f;
			if (ImGui.BeginTable("##input", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("pages", ImGuiTableColumnFlags.WidthFixed, 170f);
				ImGui.TableSetupColumn("page", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach ((Page item, string label) in Pages)
				{
					if (EditorGui.Selectable(label, page == item) || requestedPage == item)
					{
						page = item;
						listening = null;
					}

					TestAnchors.RecordItem("input:" + item);
				}

				requestedPage = null;
				ImGui.TableSetColumnIndex(1);
				if (ImGui.BeginChild("##page"))
				{
					DrawPage(configuration);
				}

				ImGui.EndChild();
				ImGui.EndTable();
			}
		}

		/// <summary>The configuration shown, whether the project uses it, and creating one.</summary>
		private void DrawHeader(InputConfiguration configuration)
		{
			object value = configuration;
			ImGui.TextUnformatted("Configuration");
			ImGui.SameLine(130f);
			ImGui.SetNextItemWidth(320f);
			if (AssetField.Draw(typeof(InputConfiguration), ref value))
			{
				assetPath = (value as InputConfiguration)?.AssetPath;
				listening = null;
			}

			ImGui.SameLine();
			if (configuration == null)
			{
				if (EditorGui.Button("Create"))
				{
					CreateConfiguration();
				}

				ImGui.Spacing();
				ImGui.TextDisabled("No input configuration: create one, or pick one above.");
				return;
			}

			bool used = string.Equals(ProjectSettings.Current.Input.Configuration, assetPath, StringComparison.OrdinalIgnoreCase);
			if (used)
			{
				ImGui.TextDisabled("(the project's configuration)");
			}
			else if (EditorGui.Button("Use in Project"))
			{
				editor.Edits.Begin(ProjectSettings.Current);
				ProjectSettings.Current.Input.Configuration = assetPath;
				editor.Edits.RequestCommit();
			}

			ImGui.Separator();
		}

		/// <summary>A new configuration (Assets/Settings/Input.asset, a free name) used by the project when it has none.</summary>
		private void CreateConfiguration()
		{
			string folder = "Settings";
			string path = folder + "/Input" + ScriptableObjectAssets.Extension;
			for (int i = 1; File.Exists(AssetDatabase.ToAbsolute(path)); i++)
			{
				path = folder + "/Input " + i + ScriptableObjectAssets.Extension;
			}

			Directory.CreateDirectory(AssetDatabase.ToAbsolute(folder));
			var created = ScriptableObject.CreateInstance<InputConfiguration>();
			created.Reset();
			ScriptableObjectAssets.Save(created, path);
			AssetDatabase.Refresh(true);
			editor.NotifyAssetsChanged();
			assetPath = path;
			if (string.IsNullOrEmpty(ProjectSettings.Current.Input.Configuration))
			{
				editor.Edits.Begin(ProjectSettings.Current);
				ProjectSettings.Current.Input.Configuration = path;
				editor.Edits.RequestCommit();
			}
		}

		private void DrawPage(InputConfiguration configuration)
		{
			switch (page)
			{
				case Page.Settings:
					ImGui.TextDisabled("How controllers are assigned to players and how players' remapping is kept.");
					ImGui.Spacing();
					FieldDrawer.DrawFields(configuration.Settings, context);
					break;
				case Page.Players:
					DrawPlayers(configuration);
					break;
				case Page.Actions:
					DrawActions(configuration);
					break;
				case Page.ActionCategories:
					DrawCategories(configuration, configuration.ActionCategories, "action category", "Groups of actions (a remapping screen shows them together).", c => configuration.Actions.Exists(a => a.CategoryId == c.Id));
					break;
				case Page.MapCategories:
					DrawCategories(configuration, configuration.MapCategories, "map category", "What the game is doing (Gameplay, Menu): each has its own maps, players enable maps by category.", c => configuration.Maps.Exists(m => m.CategoryId == c.Id));
					break;
				case Page.Layouts:
					DrawCategories(configuration, configuration.Layouts, "layout", "Alternative bindings of a map category (Default, Left Handed): a player has one layout of a category loaded.", c => configuration.Maps.Exists(m => m.LayoutId == c.Id));
					break;
				case Page.Behaviors:
					DrawBehaviors(configuration);
					break;
				case Page.KeyboardMaps:
					DrawMaps(configuration, ControllerType.Keyboard);
					break;
				case Page.MouseMaps:
					DrawMaps(configuration, ControllerType.Mouse);
					break;
				case Page.GamepadMaps:
					DrawMaps(configuration, ControllerType.Gamepad);
					break;
				case Page.Debug:
					DrawDebug(configuration);
					break;
			}
		}

		// ---------------------------------------------------------------- edits

		/// <summary>Before a change made by code (a button, a list operation): the undo step's "before" state.</summary>
		private void BeginEdit()
		{
			context.BeginEdit();
		}

		/// <summary>After it: the undo step ends with this frame (and the asset is saved).</summary>
		private void EndEdit()
		{
			context.EndEdit();
		}

		private bool EditText(string id, ref string value, float width)
		{
			string text = value ?? "";
			ImGui.SetNextItemWidth(width);
			bool changed = EditorGui.InputText(id, ref text, 128);
			context.Track(changed);
			if (changed)
			{
				value = text;
			}

			return changed;
		}

		private bool EditCheckbox(string id, ref bool value)
		{
			bool local = value;
			bool changed = EditorGui.Checkbox(id, ref local);
			context.Track(changed);
			if (changed)
			{
				value = local;
			}

			return changed;
		}

		/// <summary>A combo of named items with ids; true when another was picked.</summary>
		private bool EditCombo<T>(string id, ref int value, IList<T> items, Func<T, int> itemId, Func<T, string> itemName, float width)
		{
			T current = default;
			foreach (T item in items)
			{
				if (itemId(item) == value)
				{
					current = item;
				}
			}

			bool changed = false;
			ImGui.SetNextItemWidth(width);
			if (EditorGui.BeginCombo(id, current != null ? itemName(current) : "(none)"))
			{
				foreach (T item in items)
				{
					if (EditorGui.Selectable(itemName(item) + "##" + itemId(item), itemId(item) == value))
					{
						BeginEdit();
						value = itemId(item);
						EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			return changed;
		}

		private bool EditEnum<T>(string id, ref T value, float width)
			where T : struct, Enum
		{
			bool changed = false;
			ImGui.SetNextItemWidth(width);
			if (EditorGui.BeginCombo(id, value.ToString()))
			{
				foreach (T item in Enum.GetValues<T>())
				{
					if (EditorGui.Selectable(item.ToString(), item.Equals(value)))
					{
						BeginEdit();
						value = item;
						EndEdit();
						changed = true;
					}
				}

				EditorGui.EndCombo();
			}

			return changed;
		}

		// ---------------------------------------------------------------- players

		private void DrawPlayers(InputConfiguration configuration)
		{
			ImGui.TextDisabled("The players of the game: what they start with. Scripts read InputManager.GetPlayer(id or name).");
			ImGui.Spacing();
			if (ImGui.BeginTable("##players", 2, ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, 180f);
				ImGui.TableSetupColumn("player", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach (InputPlayerDefinition player in configuration.Players)
				{
					if (EditorGui.Selectable(player.Name + "##player" + player.Id, player.Id == selectedPlayer))
					{
						selectedPlayer = player.Id;
					}
				}

				if (EditorGui.Button("Add Player"))
				{
					BeginEdit();
					int id = InputConfiguration.NextId(configuration.Players, p => p.Id);
					configuration.Players.Add(InputPlayerDefinition.CreateDefault(id, "Player" + id));
					EndEdit();
					selectedPlayer = id;
				}

				ImGui.TableSetColumnIndex(1);
				InputPlayerDefinition selected = configuration.Players.Find(p => p.Id == selectedPlayer) ?? (configuration.Players.Count > 0 ? configuration.Players[0] : null);
				if (selected != null)
				{
					selectedPlayer = selected.Id;
					DrawPlayer(configuration, selected);
				}

				ImGui.EndTable();
			}
		}

		private void DrawPlayer(InputConfiguration configuration, InputPlayerDefinition player)
		{
			ImGui.PushID("player" + player.Id);
			ImGui.TextDisabled("Id " + player.Id);
			FieldDrawer.DrawFields(player, context, filter: field => field.Name != nameof(InputPlayerDefinition.StartingMaps) && field.Name != nameof(InputPlayerDefinition.Id));
			ImGui.Spacing();
			ImGui.SeparatorText("Starting Maps");
			ImGui.TextDisabled("The controller maps the player has at start (a map category and layout per controller type).");
			StartingMap removed = null;
			for (int i = 0; i < player.StartingMaps.Count; i++)
			{
				StartingMap map = player.StartingMaps[i];
				ImGui.PushID(i);
				EditEnum("##type", ref map.Type, 110f);
				ImGui.SameLine();
				EditCombo("##category", ref map.CategoryId, configuration.MapCategories, c => c.Id, c => c.Name, 150f);
				ImGui.SameLine();
				EditCombo("##layout", ref map.LayoutId, configuration.Layouts, c => c.Id, c => c.Name, 150f);
				ImGui.SameLine();
				EditCheckbox("Enabled", ref map.Enabled);
				if (configuration.FindMap(map.Type, map.CategoryId, map.LayoutId) == null)
				{
					ImGui.SameLine();
					ImGui.TextColored(EditorStyle.Warning, "no such map");
				}

				ImGui.SameLine();
				if (EditorIcons.Button("remove", IconKind.Cross, "Remove"))
				{
					removed = map;
				}

				ImGui.PopID();
			}

			if (removed != null)
			{
				BeginEdit();
				player.StartingMaps.Remove(removed);
				EndEdit();
			}

			if (EditorGui.Button("Add Starting Map"))
			{
				BeginEdit();
				player.StartingMaps.Add(new StartingMap());
				EndEdit();
			}

			ImGui.Spacing();
			ImGui.Separator();
			ImGui.BeginDisabled(configuration.Players.Count <= 1);
			if (EditorGui.Button("Delete Player"))
			{
				BeginEdit();
				configuration.Players.Remove(player);
				EndEdit();
			}

			ImGui.EndDisabled();
			ImGui.PopID();
		}

		// ---------------------------------------------------------------- actions

		private void DrawActions(InputConfiguration configuration)
		{
			ImGui.TextDisabled("What players do; scripts read them by name: player.GetButtonDown(\"Fire\"), player.GetAxis(\"Move Horizontal\").");
			ImGui.Spacing();
			if (EditorGui.Button("Add Action"))
			{
				BeginEdit();
				int id = InputConfiguration.NextId(configuration.Actions, a => a.Id);
				configuration.Actions.Add(new InputAction { Id = id, Name = "Action" + id, CategoryId = configuration.ActionCategories.Count > 0 ? configuration.ActionCategories[0].Id : 0, BehaviorId = configuration.Behaviors.Count > 0 ? configuration.Behaviors[0].Id : 0 });
				EndEdit();
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(220f);
			EditorGui.InputTextWithHint("##filter", "Filter", ref actionFilter, 64);
			// Fixed columns, scrolled sideways in a narrow window (a stretched one would be squeezed to nothing).
			const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;
			if (!ImGui.BeginTable("##actions", 9, flags, ImGui.GetContentRegionAvail()))
			{
				return;
			}

			ImGui.TableSetupScrollFreeze(2, 1);
			ImGui.TableSetupColumn("Id", ImGuiTableColumnFlags.WidthFixed, 22f);
			ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 170f);
			ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 82f);
			ImGui.TableSetupColumn("Category", ImGuiTableColumnFlags.WidthFixed, 122f);
			ImGui.TableSetupColumn("Behavior", ImGuiTableColumnFlags.WidthFixed, 122f);
			ImGui.TableSetupColumn("Positive / Negative", ImGuiTableColumnFlags.WidthFixed, 170f);
			ImGui.TableSetupColumn("Descriptive Name", ImGuiTableColumnFlags.WidthFixed, 150f);
			ImGui.TableSetupColumn("Assignable", ImGuiTableColumnFlags.WidthFixed, 70f);
			ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30f);
			ImGui.TableHeadersRow();
			InputAction removed = null;
			foreach (InputAction action in configuration.Actions)
			{
				if (actionFilter.Length > 0 && action.Name.IndexOf(actionFilter, StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}

				ImGui.PushID(action.Id);
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextDisabled(action.Id.ToString());
				ImGui.TableNextColumn();
				EditText("##name", ref action.Name, -1f);
				TestAnchors.RecordItem("action:" + action.Name);
				ImGui.TableNextColumn();
				EditEnum("##type", ref action.Type, 80f);
				ImGui.TableNextColumn();
				EditCombo("##category", ref action.CategoryId, configuration.ActionCategories, c => c.Id, c => c.Name, 120f);
				ImGui.TableNextColumn();
				EditCombo("##behavior", ref action.BehaviorId, configuration.Behaviors, b => b.Id, b => b.Name, 120f);
				ImGui.TableNextColumn();
				if (action.Type == InputActionType.Axis)
				{
					EditText("##positive", ref action.PositiveName, 80f);
					ImGui.SameLine();
					EditText("##negative", ref action.NegativeName, 80f);
				}

				ImGui.TableNextColumn();
				EditText("##descriptive", ref action.DescriptiveName, 140f);
				ImGui.TableNextColumn();
				EditCheckbox("##assignable", ref action.UserAssignable);
				ImGui.TableNextColumn();
				if (EditorIcons.Button("remove", IconKind.Cross, "Delete the action and its bindings"))
				{
					removed = action;
				}

				ImGui.PopID();
			}

			ImGui.EndTable();
			if (removed != null)
			{
				BeginEdit();
				configuration.Actions.Remove(removed);
				foreach (ControllerMap map in configuration.Maps)
				{
					map.Bindings.RemoveAll(b => b.ActionId == removed.Id);
				}

				EndEdit();
			}
		}

		// ---------------------------------------------------------------- categories, layouts

		private void DrawCategories(InputConfiguration configuration, List<InputCategory> list, string kind, string description, Predicate<InputCategory> used)
		{
			ImGui.TextDisabled(description);
			ImGui.Spacing();
			if (EditorGui.Button("Add " + char.ToUpperInvariant(kind[0]) + kind.Substring(1)))
			{
				BeginEdit();
				int id = InputConfiguration.NextId(list, c => c.Id);
				list.Add(new InputCategory { Id = id, Name = "New " + kind });
				EndEdit();
			}

			if (!ImGui.BeginTable("##categories", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
			{
				return;
			}

			ImGui.TableSetupColumn("Id");
			ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
			ImGui.TableSetupColumn("Descriptive Name");
			ImGui.TableSetupColumn("Assignable");
			ImGui.TableSetupColumn("");
			ImGui.TableHeadersRow();
			InputCategory removed = null;
			foreach (InputCategory category in list)
			{
				ImGui.PushID(category.Id);
				ImGui.TableNextRow();
				ImGui.TableNextColumn();
				ImGui.TextDisabled(category.Id.ToString());
				ImGui.TableNextColumn();
				EditText("##name", ref category.Name, -1f);
				ImGui.TableNextColumn();
				EditText("##descriptive", ref category.DescriptiveName, 180f);
				ImGui.TableNextColumn();
				EditCheckbox("##assignable", ref category.UserAssignable);
				ImGui.TableNextColumn();
				bool inUse = used(category);
				ImGui.BeginDisabled(list.Count <= 1 || inUse);
				if (EditorIcons.Button("remove", IconKind.Cross, inUse ? "In use" : "Delete"))
				{
					removed = category;
				}

				ImGui.EndDisabled();
				ImGui.PopID();
			}

			ImGui.EndTable();
			if (removed != null)
			{
				BeginEdit();
				list.Remove(removed);
				EndEdit();
			}
		}

		// ---------------------------------------------------------------- behaviors

		private void DrawBehaviors(InputConfiguration configuration)
		{
			ImGui.TextDisabled("How actions' values are shaped: digital axis gravity, mouse sensitivity, double / short / long press times.");
			ImGui.Spacing();
			if (ImGui.BeginTable("##behaviors", 2, ImGuiTableFlags.BordersInnerV))
			{
				ImGui.TableSetupColumn("list", ImGuiTableColumnFlags.WidthFixed, 180f);
				ImGui.TableSetupColumn("behavior", ImGuiTableColumnFlags.WidthStretch);
				ImGui.TableNextRow();
				ImGui.TableSetColumnIndex(0);
				foreach (InputBehavior behavior in configuration.Behaviors)
				{
					if (EditorGui.Selectable(behavior.Name + "##behavior" + behavior.Id, behavior.Id == selectedBehavior))
					{
						selectedBehavior = behavior.Id;
					}
				}

				if (EditorGui.Button("Add Behavior"))
				{
					BeginEdit();
					int id = InputConfiguration.NextId(configuration.Behaviors, b => b.Id);
					configuration.Behaviors.Add(new InputBehavior { Id = id, Name = "Behavior" + id });
					EndEdit();
					selectedBehavior = id;
				}

				ImGui.TableSetColumnIndex(1);
				InputBehavior selected = configuration.Behaviors.Find(b => b.Id == selectedBehavior) ?? (configuration.Behaviors.Count > 0 ? configuration.Behaviors[0] : null);
				if (selected != null)
				{
					selectedBehavior = selected.Id;
					ImGui.PushID("behavior" + selected.Id);
					FieldDrawer.DrawFields(selected, context, filter: field => field.Name != nameof(InputBehavior.Id));
					bool inUse = configuration.Actions.Exists(a => a.BehaviorId == selected.Id);
					ImGui.Spacing();
					ImGui.BeginDisabled(configuration.Behaviors.Count <= 1 || inUse);
					if (EditorGui.Button("Delete Behavior"))
					{
						BeginEdit();
						configuration.Behaviors.Remove(selected);
						EndEdit();
					}

					ImGui.EndDisabled();
					if (inUse)
					{
						ImGui.SameLine();
						ImGui.TextDisabled("(actions use it)");
					}

					ImGui.PopID();
				}

				ImGui.EndTable();
			}
		}

		// ---------------------------------------------------------------- maps

		private void DrawMaps(InputConfiguration configuration, ControllerType type)
		{
			int t = (int)type;
			ImGui.TextDisabled(type switch
			{
				ControllerType.Keyboard => "Keys bound to actions (two keys make an axis: one positive, one negative). Listen, then press the key (with modifiers).",
				ControllerType.Mouse => "Mouse buttons, movement and wheel bound to actions.",
				_ => "The standard gamepad's buttons, sticks and triggers bound to actions (every gamepad is read through this layout)."
			});
			ImGui.Spacing();
			ImGui.TextUnformatted("Map Category");
			ImGui.SameLine();
			EditComboLocal("##category", ref mapCategory[t], configuration.MapCategories);
			ImGui.SameLine();
			ImGui.TextUnformatted("Layout");
			ImGui.SameLine();
			EditComboLocal("##layout", ref mapLayout[t], configuration.Layouts);
			ControllerMap map = configuration.FindMap(type, mapCategory[t], mapLayout[t]);
			ImGui.SameLine();
			if (map == null)
			{
				if (EditorGui.Button("Create Map"))
				{
					BeginEdit();
					configuration.Maps.Add(new ControllerMap { Id = InputConfiguration.NextId(configuration.Maps, m => m.Id), Type = type, CategoryId = mapCategory[t], LayoutId = mapLayout[t] });
					EndEdit();
				}

				ImGui.Spacing();
				ImGui.TextDisabled("No " + type + " map for this category and layout.");
				return;
			}

			if (EditorGui.Button("Delete Map"))
			{
				BeginEdit();
				configuration.Maps.Remove(map);
				EndEdit();
				return;
			}

			ImGui.Spacing();
			const ImGuiTableFlags flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;
			if (!ImGui.BeginTable("##bindings", 5, flags, ImGui.GetContentRegionAvail()))
			{
				return;
			}

			ImGui.TableSetupScrollFreeze(1, 1);
			ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 180f);
			ImGui.TableSetupColumn("Element", ImGuiTableColumnFlags.WidthFixed, 210f);
			ImGui.TableSetupColumn("Direction / Range", ImGuiTableColumnFlags.WidthFixed, type == ControllerType.Keyboard ? 150f : 270f);
			ImGui.TableSetupColumn(type == ControllerType.Keyboard ? "Modifiers" : "", ImGuiTableColumnFlags.WidthFixed, type == ControllerType.Keyboard ? 200f : 1f);
			ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 60f);
			ImGui.TableHeadersRow();
			ActionElementMap removed = null;
			foreach (InputAction action in configuration.Actions)
			{
				ImGui.PushID(action.Id);
				List<ActionElementMap> bindings = map.Bindings.FindAll(b => b.ActionId == action.Id);
				int rows = Math.Max(1, bindings.Count);
				for (int row = 0; row < rows; row++)
				{
					ActionElementMap binding = row < bindings.Count ? bindings[row] : null;
					ImGui.PushID(row);
					ImGui.TableNextRow();
					ImGui.TableNextColumn();
					if (row == 0)
					{
						ImGui.TextUnformatted(action.Name);
						ImGui.SameLine();
						ImGui.TextDisabled(action.Type == InputActionType.Axis ? "(axis)" : "");
					}

					ImGui.TableNextColumn();
					if (binding != null)
					{
						DrawElement(configuration, map, action, binding);
						ImGui.TableNextColumn();
						DrawDirection(action, map, binding);
						ImGui.TableNextColumn();
						if (type == ControllerType.Keyboard)
						{
							DrawModifiers(binding);
						}

						ImGui.TableNextColumn();
						if (EditorIcons.Button("remove", IconKind.Cross, "Remove the binding"))
						{
							removed = binding;
						}
					}
					else
					{
						// Nothing bound yet: Listen adds the first binding.
						DrawListenButton(map, action, null, AxisContribution.Positive);
						ImGui.TableNextColumn();
						ImGui.TableNextColumn();
						ImGui.TableNextColumn();
					}

					if (binding != null && row == rows - 1)
					{
						ImGui.SameLine();
						if (EditorIcons.Button("add", IconKind.Plus, "Add another binding"))
						{
							BeginEdit();
							map.Bindings.Add(new ActionElementMap { ActionId = action.Id, Element = ControllerTypes.Elements(type)[0], Contribution = action.Type == InputActionType.Axis && bindings.Count % 2 == 1 ? AxisContribution.Negative : AxisContribution.Positive });
							EndEdit();
						}
					}

					ImGui.PopID();
				}

				ImGui.PopID();
			}

			ImGui.EndTable();
			if (removed != null)
			{
				BeginEdit();
				map.Bindings.Remove(removed);
				EndEdit();
			}

			UpdateListening(map);
		}

		private static void EditComboLocal(string id, ref int value, List<InputCategory> items)
		{
			int selected = value;
			InputCategory current = items.Find(c => c.Id == selected) ?? (items.Count > 0 ? items[0] : null);
			value = current?.Id ?? 0;
			ImGui.SetNextItemWidth(150f);
			if (EditorGui.BeginCombo(id, current?.Name ?? "(none)"))
			{
				foreach (InputCategory item in items)
				{
					if (EditorGui.Selectable(item.Name + "##" + item.Id, item.Id == value))
					{
						value = item.Id;
					}
				}

				EditorGui.EndCombo();
			}
		}

		/// <summary>The bound element: a button that listens for the next input, and a picker of every element.</summary>
		private void DrawElement(InputConfiguration configuration, ControllerMap map, InputAction action, ActionElementMap binding)
		{
			if (listening != null && listening.Binding == binding)
			{
				DrawListeningLabel();
				return;
			}

			string name = ControllerTypes.ElementName(map.Type, binding.Element);
			if (EditorGui.Button(name + "##element", new NVector2(170f, 0f)))
			{
				StartListening(map, action, binding, binding.Contribution);
			}

			TestAnchors.RecordItem("binding:" + action.Name + "/" + name);
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Click, then press the " + (map.Type == ControllerType.Keyboard ? "key" : map.Type == ControllerType.Mouse ? "mouse button" : "button or push the stick") + " to bind (Esc cancels)");
			}

			ImGui.SameLine();
			ImGui.SetNextItemWidth(26f);
			if (EditorGui.BeginCombo("##pick", "", ImGuiComboFlags.NoPreview | ImGuiComboFlags.HeightLarge))
			{
				if (map.Type == ControllerType.Keyboard)
				{
					if (ImGui.IsWindowAppearing())
					{
						keyFilter = "";
						ImGui.SetKeyboardFocusHere();
					}

					EditorGui.InputTextWithHint("##keyfilter", "Filter", ref keyFilter, 32);
				}

				foreach (int element in ControllerTypes.Elements(map.Type))
				{
					string elementName = ControllerTypes.ElementName(map.Type, element);
					if (keyFilter.Length > 0 && map.Type == ControllerType.Keyboard && elementName.IndexOf(keyFilter, StringComparison.OrdinalIgnoreCase) < 0)
					{
						continue;
					}

					if (EditorGui.Selectable(elementName + "##" + element, element == binding.Element))
					{
						BeginEdit();
						SetElement(action, map.Type, binding, element, 1f);
						EndEdit();
					}
				}

				EditorGui.EndCombo();
			}
		}

		/// <summary>Keys and buttons: the direction they push an axis action. Axes: the part read, and inversion.</summary>
		private void DrawDirection(InputAction action, ControllerMap map, ActionElementMap binding)
		{
			if (ControllerTypes.IsAxis(map.Type, binding.Element))
			{
				EditEnum("##range", ref binding.Range, 90f);
				if (binding.Range != AxisRange.Full && action.Type == InputActionType.Axis)
				{
					ImGui.SameLine();
					EditEnum("##contribution", ref binding.Contribution, 90f);
				}

				ImGui.SameLine();
				EditCheckbox("Invert", ref binding.Invert);
			}
			else if (action.Type == InputActionType.Axis)
			{
				string positive = string.IsNullOrEmpty(action.PositiveName) ? "Positive" : action.PositiveName;
				string negative = string.IsNullOrEmpty(action.NegativeName) ? "Negative" : action.NegativeName;
				ImGui.SetNextItemWidth(140f);
				if (EditorGui.BeginCombo("##contribution", binding.Contribution == AxisContribution.Positive ? "+ " + positive : "- " + negative))
				{
					if (EditorGui.Selectable("+ " + positive, binding.Contribution == AxisContribution.Positive))
					{
						BeginEdit();
						binding.Contribution = AxisContribution.Positive;
						EndEdit();
					}

					if (EditorGui.Selectable("- " + negative, binding.Contribution == AxisContribution.Negative))
					{
						BeginEdit();
						binding.Contribution = AxisContribution.Negative;
						EndEdit();
					}

					EditorGui.EndCombo();
				}
			}
		}

		private void DrawModifiers(ActionElementMap binding)
		{
			foreach (ModifierKeys modifier in new[] { ModifierKeys.Control, ModifierKeys.Shift, ModifierKeys.Alt })
			{
				bool held = (binding.Modifiers & modifier) != 0;
				bool local = held;
				bool changed = EditorGui.Checkbox(modifier == ModifierKeys.Control ? "Ctrl" : modifier.ToString(), ref local);
				context.Track(changed);
				if (changed)
				{
					binding.Modifiers = local ? binding.Modifiers | modifier : binding.Modifiers & ~modifier;
				}

				ImGui.SameLine();
			}

			ImGui.NewLine();
		}

		// ---------------------------------------------------------------- listening

		private void DrawListenButton(ControllerMap map, InputAction action, ActionElementMap binding, AxisContribution contribution)
		{
			if (listening != null && listening.Binding == binding && listening.Action == action && binding == null)
			{
				DrawListeningLabel();
				return;
			}

			if (EditorGui.Button("Listen##listen", new NVector2(170f, 0f)))
			{
				StartListening(map, action, binding, contribution);
			}

			TestAnchors.RecordItem("listen:" + action.Name);
			ImGui.SameLine();
			if (EditorIcons.Button("addnew", IconKind.Plus, "Add a binding and pick its element"))
			{
				BeginEdit();
				map.Bindings.Add(new ActionElementMap { ActionId = action.Id, Element = ControllerTypes.Elements(map.Type)[0], Contribution = contribution });
				EndEdit();
			}
		}

		private void DrawListeningLabel()
		{
			float pulse = 0.55f + 0.45f * MathF.Sin((float)ImGui.GetTime() * 6f);
			ImGui.PushStyleColor(ImGuiCol.Button, new NVector4(0.85f * pulse, 0.45f * pulse, 0.15f, 1f));
			if (EditorGui.Button("Press...##listening", new NVector2(170f, 0f)))
			{
				listening = null;
			}

			ImGui.PopStyleColor();
			if (ImGui.IsItemHovered())
			{
				ImGui.SetTooltip("Waiting for input (Esc or a click here cancels)");
			}
		}

		private void StartListening(ControllerMap map, InputAction action, ActionElementMap binding, AxisContribution contribution)
		{
			listening = new Listening { Type = map.Type, Map = map, Action = action, Binding = binding, Contribution = contribution, Started = ImGui.GetTime() };
		}

		/// <summary>Detects the element pressed while listening (keyboard and mouse through ImGui, gamepads through GLFW).</summary>
		private void UpdateListening(ControllerMap map)
		{
			if (listening == null || listening.Map != map)
			{
				return;
			}

			if (ImGui.IsKeyPressed(ImGuiKey.Escape) || ImGui.GetTime() - listening.Started > 8.0)
			{
				listening = null;
				return;
			}

			bool anything = Detect(listening.Type, out int element, out float axisValue, out ModifierKeys modifiers);
			if (listening.WaitingForRelease)
			{
				// The click on the button (or a held key) must be let go first.
				listening.WaitingForRelease = anything || ImGui.IsMouseDown(ImGuiMouseButton.Left);
				return;
			}

			if (!anything)
			{
				return;
			}

			BeginEdit();
			ActionElementMap binding = listening.Binding;
			if (binding == null)
			{
				binding = new ActionElementMap { ActionId = listening.Action.Id, Contribution = listening.Contribution };
				map.Bindings.Add(binding);
			}

			SetElement(listening.Action, listening.Type, binding, element, axisValue);
			binding.Modifiers = listening.Type == ControllerType.Keyboard ? modifiers : ModifierKeys.None;
			EndEdit();
			listening = null;
		}

		/// <summary>A binding's new element: an axis gets the part pushed (or the whole axis for an axis action).</summary>
		private static void SetElement(InputAction action, ControllerType type, ActionElementMap binding, int element, float axisValue)
		{
			binding.Element = element;
			if (!ControllerTypes.IsAxis(type, element))
			{
				binding.Range = AxisRange.Full;
				binding.Invert = false;
				return;
			}

			bool trigger = type == ControllerType.Gamepad && (element == (int)GamepadElement.LeftTrigger || element == (int)GamepadElement.RightTrigger);
			if (action.Type == InputActionType.Axis && !trigger)
			{
				binding.Range = AxisRange.Full;
				binding.Invert = axisValue < 0f && binding.Contribution == AxisContribution.Positive;
			}
			else
			{
				binding.Range = axisValue >= 0f ? AxisRange.Positive : AxisRange.Negative;
			}
		}

		private static bool Detect(ControllerType type, out int element, out float axisValue, out ModifierKeys modifiers)
		{
			element = 0;
			axisValue = 1f;
			modifiers = ModifierKeys.None;
			ImGuiIOPtr io = ImGui.GetIO();
			switch (type)
			{
				case ControllerType.Keyboard:
					if (io.KeyCtrl) modifiers |= ModifierKeys.Control;
					if (io.KeyShift) modifiers |= ModifierKeys.Shift;
					if (io.KeyAlt) modifiers |= ModifierKeys.Alt;
					foreach (int code in ControllerTypes.Elements(ControllerType.Keyboard))
					{
						var key = (Keys)code;
						if (key == Keys.Escape || key == Keys.LeftShift || key == Keys.RightShift || key == Keys.LeftControl || key == Keys.RightControl || key == Keys.LeftAlt || key == Keys.RightAlt)
						{
							continue;
						}

						ImGuiKey imguiKey = ImGuiController.MapKey(key);
						if (imguiKey != ImGuiKey.None && ImGui.IsKeyDown(imguiKey))
						{
							element = code;
							return true;
						}
					}

					return false;
				case ControllerType.Mouse:
					for (int button = 0; button < 5; button++)
					{
						if (ImGui.IsMouseDown((ImGuiMouseButton)button))
						{
							element = (int)MouseElement.LeftButton + button;
							return true;
						}
					}

					if (Math.Abs(io.MouseWheel) > 0f)
					{
						element = (int)MouseElement.Wheel;
						axisValue = io.MouseWheel;
						return true;
					}

					return false;
				default:
					return EditorGamepads.Detect(out element, out axisValue);
			}
		}

		// ---------------------------------------------------------------- debug

		private void DrawDebug(InputConfiguration configuration)
		{
			if (!editor.IsPlaying || !InputManager.IsReady)
			{
				ImGui.TextDisabled("Play mode shows every player's actions here, live.");
			}
			else
			{
				if (InputManager.Configuration != configuration)
				{
					ImGui.TextColored(EditorStyle.Warning, "The game runs " + InputManager.Configuration.name + ", not this configuration.");
				}

				foreach (InputPlayer player in InputManager.Players)
				{
					if (!EditorGui.CollapsingHeader(player.DescriptiveName + "##debug" + player.Id, ImGuiTreeNodeFlags.DefaultOpen))
					{
						continue;
					}

					var controllers = new List<string>();
					foreach (Controller controller in player.Controllers)
					{
						controllers.Add(controller.Name);
					}

					ImGui.TextDisabled("Controllers: " + (controllers.Count > 0 ? string.Join(", ", controllers) : "none") + "   last active: " + (player.LastActiveController?.Name ?? "-"));
					var enabled = new List<string>();
					foreach (PlayerMap map in player.Maps)
					{
						string category = InputManager.Configuration.MapCategories.Find(c => c.Id == map.CategoryId)?.Name ?? map.CategoryId.ToString();
						enabled.Add(map.Type + "/" + category + (map.Enabled ? "" : " (off)"));
					}

					ImGui.TextDisabled("Maps: " + string.Join(", ", enabled));
					if (ImGui.BeginTable("##actions" + player.Id, 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
					{
						ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 200f);
						ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthFixed, 230f);
						ImGui.TableSetupColumn("State", ImGuiTableColumnFlags.WidthStretch);
						foreach (InputAction action in InputManager.Configuration.Actions)
						{
							float value = player.GetAxis(action.Id);
							bool pressed = player.GetButton(action.Id);
							ImGui.TableNextRow();
							ImGui.TableNextColumn();
							ImGui.TextUnformatted(action.Name);
							ImGui.TableNextColumn();
							ValueBar(value, action.Type == InputActionType.Axis);
							ImGui.TableNextColumn();
							if (pressed)
							{
								ImGui.TextColored(EditorIcons.Accept, "pressed " + player.GetButtonTimePressed(action.Name).ToString("0.0") + " s");
							}
						}

						ImGui.EndTable();
					}
				}
			}

			ImGui.Spacing();
			ImGui.SeparatorText("Gamepads");
			if (editor.IsPlaying && InputManager.IsReady)
			{
				// The input system's gamepads (virtual ones too) with their owners.
				if (InputManager.Gamepads.Count == 0)
				{
					ImGui.TextDisabled("None connected.");
				}

				foreach (GamepadController gamepad in InputManager.Gamepads)
				{
					ImGui.TextUnformatted(gamepad.Name + (gamepad.IsVirtual ? "  (virtual)" : "") + "  →  " + (gamepad.Owner?.Name ?? "no player"));
					var active = new List<string>();
					for (int element = 0; element < gamepad.ElementCount; element++)
					{
						float value = gamepad.GetValue(element);
						if (Math.Abs(value) > 0.01f)
						{
							active.Add(ControllerTypes.ElementName(ControllerType.Gamepad, element) + (ControllerTypes.IsAxis(ControllerType.Gamepad, element) ? " " + value.ToString("0.00") : ""));
						}
					}

					ImGui.TextDisabled(active.Count > 0 ? string.Join(", ", active) : "-");
				}
			}
			else
			{
				EditorGamepads.DrawConnected();
			}
		}

		/// <summary>An action's value: axes from the centre (-1 left, 1 right; the mouse can go past), buttons from the left.</summary>
		private static void ValueBar(float value, bool axis)
		{
			NVector2 size = new NVector2(160f, ImGui.GetFrameHeight());
			NVector2 min = ImGui.GetCursorScreenPos();
			// The bar and its number after it.
			ImGui.Dummy(size + new NVector2(50f, 0f));
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			draw.AddRectFilled(min, min + size, ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
			uint fill = ImGui.GetColorU32(ImGuiCol.PlotHistogram);
			float clamped = Math.Clamp(value, axis ? -1f : 0f, 1f);
			if (axis)
			{
				float center = min.X + size.X * 0.5f;
				float end = center + clamped * size.X * 0.5f;
				draw.AddRectFilled(new NVector2(Math.Min(center, end), min.Y), new NVector2(Math.Max(center, end), min.Y + size.Y), fill);
				draw.AddLine(new NVector2(center, min.Y), new NVector2(center, min.Y + size.Y), ImGui.GetColorU32(ImGuiCol.Border));
			}
			else
			{
				draw.AddRectFilled(min, new NVector2(min.X + clamped * size.X, min.Y + size.Y), fill);
			}

			string text = value.ToString("0.00");
			draw.AddText(min + new NVector2(size.X + 6f, (size.Y - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), text);
		}

		#endregion
	}

	/// <summary>Gamepads read in edit mode (the input system runs in play mode only): listening for bindings, the Debug page.</summary>
	internal static class EditorGamepads
	{
		#region PublicMethods

		/// <summary>A button pressed, or an axis pushed past 0.6, on any connected gamepad.</summary>
		public static unsafe bool Detect(out int element, out float axisValue)
		{
			element = 0;
			axisValue = 1f;
			for (int slot = 0; slot < 16; slot++)
			{
				if (!GLFW.JoystickPresent(slot) || !GLFW.JoystickIsGamepad(slot) || !GLFW.GetGamepadState(slot, out GamepadState state))
				{
					continue;
				}

				for (int button = 0; button < 15; button++)
				{
					if (state.Buttons[button] != 0)
					{
						element = (int)GamepadElement.ActionBottom + button;
						return true;
					}
				}

				// Sticks (y up positive), then triggers (0..1).
				float[] axes = { state.Axes[0], -state.Axes[1], state.Axes[2], -state.Axes[3], (state.Axes[4] + 1f) * 0.5f, (state.Axes[5] + 1f) * 0.5f };
				for (int axis = 0; axis < axes.Length; axis++)
				{
					if (Math.Abs(axes[axis]) > 0.6f)
					{
						element = axis;
						axisValue = axes[axis];
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>Every connected gamepad with its elements' values.</summary>
		public static unsafe void DrawConnected()
		{
			bool any = false;
			for (int slot = 0; slot < 16; slot++)
			{
				if (!GLFW.JoystickPresent(slot))
				{
					continue;
				}

				any = true;
				bool gamepad = GLFW.JoystickIsGamepad(slot);
				ImGui.TextUnformatted((GLFW.GetGamepadName(slot) ?? GLFW.GetJoystickName(slot) ?? "Joystick") + (gamepad ? "" : "  (no gamepad mapping: not read)"));
				if (!gamepad || !GLFW.GetGamepadState(slot, out GamepadState state))
				{
					continue;
				}

				var pressed = new List<string>();
				for (int button = 0; button < 15; button++)
				{
					if (state.Buttons[button] != 0)
					{
						pressed.Add(ControllerTypes.ElementName(ControllerType.Gamepad, (int)GamepadElement.ActionBottom + button));
					}
				}

				ImGui.TextDisabled($"Left stick {state.Axes[0]:0.00}, {-state.Axes[1]:0.00}   Right stick {state.Axes[2]:0.00}, {-state.Axes[3]:0.00}   Triggers {(state.Axes[4] + 1f) * 0.5f:0.00}, {(state.Axes[5] + 1f) * 0.5f:0.00}");
				ImGui.TextDisabled("Buttons: " + (pressed.Count > 0 ? string.Join(", ", pressed) : "-"));
			}

			if (!any)
			{
				ImGui.TextDisabled("None connected.");
			}
		}

		#endregion
	}
}
