using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace LELEngine.InputSystem
{
	/// <summary>
	///     The input system's entry point (Rewired's ReInput): players, controllers and the remapping listener. It
	///     starts on the first frame with the configuration named in Project Settings > Input (or one passed to
	///     <see cref="Initialize" />) and updates once per frame before the scenes run (<see cref="Input.BeginFrame" />):
	///     devices are polled (gamepads connect and disconnect), then every player's actions are read.
	/// </summary>
	public static class InputManager
	{
		#region PublicFields

		/// <summary>The configuration in use (null until the input system starts, or when the project has none).</summary>
		public static InputConfiguration Configuration { get; private set; }

		public static bool IsReady => Configuration != null;

		public static IReadOnlyList<InputPlayer> Players => players;

		public static KeyboardController Keyboard { get; } = new KeyboardController();
		public static MouseController Mouse { get; } = new MouseController();

		/// <summary>Connected gamepads (devices and virtual ones).</summary>
		public static IReadOnlyList<GamepadController> Gamepads => gamepads;

		/// <summary>Remapping: listens for the next element pressed and binds it to an action (a controls menu).</summary>
		public static InputMapper Mapper { get; } = new InputMapper();

		/// <summary>Where <see cref="SaveUserData" /> writes the players' remapped maps (the editor uses the project's Library).</summary>
		public static string UserDataPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LELEngine", AppName(), "InputUserData.json");

		public static event Action<Controller> ControllerConnected;
		public static event Action<Controller> ControllerDisconnected;

		#endregion

		#region PrivateFields

		private static readonly List<InputPlayer> players = new List<InputPlayer>();
		private static readonly List<GamepadController> gamepads = new List<GamepadController>();
		private static readonly HashSet<string> warnedActions = new HashSet<string>(StringComparer.Ordinal);

		// The player each device was last assigned to (by hardware id): a reconnected gamepad goes back to it.
		private static readonly Dictionary<string, int> previousOwners = new Dictionary<string, int>(StringComparer.Ordinal);
		private static bool initializeTried;
		private static int nextGamepadId;
		private static double time;
		private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
		private const int JoystickSlots = 16;

		#endregion

		#region PublicMethods

		/// <summary>
		///     Starts the input system with a configuration: the players it defines with their starting maps (or the
		///     user's saved maps), the keyboard and mouse given to the players that start with them, connected gamepads
		///     assigned. Called on the first frame with the project's configuration; call it to use another one.
		/// </summary>
		public static void Initialize(InputConfiguration configuration)
		{
			Shutdown();
			initializeTried = true;
			Configuration = configuration;
			if (configuration == null)
			{
				return;
			}

			foreach (InputPlayerDefinition definition in configuration.Players)
			{
				var player = new InputPlayer(configuration, definition);
				if (definition.AssignKeyboardOnStart)
				{
					player.AddController(Keyboard);
				}

				if (definition.AssignMouseOnStart)
				{
					player.AddController(Mouse);
				}

				players.Add(player);
			}

			if (configuration.Settings.LoadUserData)
			{
				LoadUserData();
			}

			PollDevices();
			Debug.Log($"[Input] {configuration.name}: {configuration.Actions.Count} actions, {players.Count} players, {gamepads.Count} gamepads");
		}

		/// <summary>Stops the input system (the editor leaving play mode); the next frame starts it again.</summary>
		public static void Shutdown()
		{
			Mapper.Stop();
			foreach (InputPlayer player in players)
			{
				foreach (Controller controller in new List<Controller>(player.Controllers))
				{
					player.RemoveController(controller);
				}
			}

			// Virtual gamepads belong to the run that made them (a new play session makes its own).
			players.Clear();
			gamepads.Clear();
			Keyboard.Owner = null;
			Mouse.Owner = null;
			Configuration = null;
			initializeTried = false;
			warnedActions.Clear();
		}

		public static InputPlayer GetPlayer(int id)
		{
			EnsureInitialized();
			return players.Find(p => p.Id == id);
		}

		public static InputPlayer GetPlayer(string name)
		{
			EnsureInitialized();
			return players.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>A gamepad driven by code (<see cref="GamepadController.SetButton" />), assigned like a device that connected.</summary>
		public static GamepadController CreateVirtualGamepad(string name)
		{
			EnsureInitialized();
			var gamepad = new GamepadController(nextGamepadId++, -1, name, "virtual:" + name);
			gamepads.Add(gamepad);
			OnConnected(gamepad);
			return gamepad;
		}

		public static void RemoveVirtualGamepad(GamepadController gamepad)
		{
			if (gamepad != null && gamepad.IsVirtual && gamepads.Remove(gamepad))
			{
				OnDisconnected(gamepad);
			}
		}

		/// <summary>Writes every player's controller maps (as remapped) to <see cref="UserDataPath" />.</summary>
		public static void SaveUserData()
		{
			if (Configuration == null)
			{
				return;
			}

			var data = new InputUserData();
			foreach (InputPlayer player in players)
			{
				var saved = new InputUserData.PlayerMaps { PlayerId = player.Id };
				foreach (PlayerMap map in player.Maps)
				{
					saved.Maps.Add(map.Map.Clone());
					saved.Enabled.Add(map.Enabled);
				}

				data.Players.Add(saved);
			}

			try
			{
				Directory.CreateDirectory(Path.GetDirectoryName(UserDataPath) ?? ".");
				Documents.WriteObjectFile(UserDataPath, data);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Debug.LogWarning("[Input] Cannot save the user's input maps: " + e.Message);
			}
		}

		/// <summary>Reads the players' saved maps (replacing the loaded ones of the same type and category).</summary>
		public static void LoadUserData()
		{
			if (Configuration == null || !File.Exists(UserDataPath))
			{
				return;
			}

			try
			{
				InputUserData data = Documents.ReadObjectFile<InputUserData>(UserDataPath);
				foreach (InputUserData.PlayerMaps saved in data?.Players ?? new List<InputUserData.PlayerMaps>())
				{
					InputPlayer player = players.Find(p => p.Id == saved.PlayerId);
					if (player == null)
					{
						continue;
					}

					var loaded = new List<PlayerMap>();
					for (int i = 0; i < saved.Maps.Count; i++)
					{
						// Bindings of actions the configuration no longer has are dropped.
						ControllerMap map = saved.Maps[i];
						map.Bindings.RemoveAll(b => Configuration.FindAction(b.ActionId) == null);
						loaded.Add(new PlayerMap { Map = map, Enabled = i < saved.Enabled.Count && saved.Enabled[i] });
					}

					player.ReplaceMaps(loaded);
				}
			}
			catch (Exception e)
			{
				Debug.LogWarning("[Input] Cannot read the user's input maps (" + e.Message + "); using the defaults");
			}
		}

		/// <summary>Deletes the saved maps and gives every player the configuration's maps again.</summary>
		public static void ClearUserData()
		{
			try
			{
				if (File.Exists(UserDataPath))
				{
					File.Delete(UserDataPath);
				}
			}
			catch (IOException e)
			{
				Debug.LogWarning("[Input] Cannot delete the user's input maps: " + e.Message);
			}

			if (Configuration == null)
			{
				return;
			}

			foreach (InputPlayer player in players)
			{
				InputPlayerDefinition definition = Configuration.Players.Find(d => d.Id == player.Id);
				foreach (StartingMap start in definition?.StartingMaps ?? new List<StartingMap>())
				{
					player.LoadMap(start.Type, start.CategoryId, start.LayoutId, start.Enabled);
				}
			}
		}

		#endregion

		#region InternalMethods

		/// <summary>One frame: devices polled, actions read (from <see cref="Input.BeginFrame" />).</summary>
		internal static void Update()
		{
			EnsureInitialized();
			if (Configuration == null)
			{
				return;
			}

			// Real time: presses are timed as the player made them, whatever the game's time scale.
			double now = clock.Elapsed.TotalSeconds;
			float deltaTime = (float)Math.Min(0.25, now - time);
			time = now;
			PollDevices();
			Keyboard.Poll(time);
			Mouse.Poll(time);
			foreach (GamepadController gamepad in gamepads)
			{
				gamepad.Poll(time);
			}

			Mapper.Update();
			foreach (InputPlayer player in players)
			{
				player.Update(time, deltaTime);
			}
		}

		internal static void WarnUnknownAction(string action)
		{
			if (Configuration != null && warnedActions.Add(action ?? ""))
			{
				Debug.LogWarning($"[Input] {Configuration.name} has no action '{action}'");
			}
		}

		#endregion

		#region PrivateMethods

		private static void EnsureInitialized()
		{
			if (initializeTried)
			{
				return;
			}

			initializeTried = true;
			string path = ProjectSettings.Current.Input.Configuration;
			if (string.IsNullOrEmpty(path))
			{
				return;
			}

			if (ScriptableObjectAssets.Load(path) is InputConfiguration configuration)
			{
				Initialize(configuration);
			}
			else
			{
				Debug.LogWarning("[Input] Project Settings > Input names " + path + ", which is not an input configuration");
			}
		}

		/// <summary>Notices gamepads connecting and disconnecting (GLFW's slots).</summary>
		private static void PollDevices()
		{
			for (int slot = 0; slot < JoystickSlots; slot++)
			{
				bool present;
				try
				{
					present = GLFW.JoystickPresent(slot) && GLFW.JoystickIsGamepad(slot);
				}
				catch (Exception)
				{
					// GLFW not initialized (a host without a window).
					return;
				}

				GamepadController known = gamepads.Find(g => g.JoystickSlot == slot);
				if (present && known == null)
				{
					string name = GLFW.GetGamepadName(slot) ?? GLFW.GetJoystickName(slot) ?? "Gamepad";
					string hardwareId = (GLFW.GetJoystickGUID(slot) ?? "") + ":" + name;
					var gamepad = new GamepadController(nextGamepadId++, slot, name, hardwareId);
					gamepads.Add(gamepad);
					OnConnected(gamepad);
				}
				else if (!present && known != null)
				{
					gamepads.Remove(known);
					OnDisconnected(known);
				}
			}
		}

		private static void OnConnected(GamepadController gamepad)
		{
			Debug.Log("[Input] Gamepad connected: " + gamepad.Name);
			if (Configuration != null && Configuration.Settings.AutoAssignGamepads)
			{
				Assign(gamepad);
			}

			ControllerConnected?.Invoke(gamepad);
		}

		private static void OnDisconnected(GamepadController gamepad)
		{
			Debug.Log("[Input] Gamepad disconnected: " + gamepad.Name);
			gamepad.IsConnected = false;
			if (gamepad.Owner != null)
			{
				previousOwners[gamepad.HardwareId] = gamepad.Owner.Id;
				gamepad.Owner.RemoveController(gamepad);
			}

			ControllerDisconnected?.Invoke(gamepad);
		}

		/// <summary>Gives a gamepad to its previous player, else to the player with the fewest that wants gamepads and has room.</summary>
		private static void Assign(GamepadController gamepad)
		{
			InputManagerSettings settings = Configuration.Settings;
			bool Wants(InputPlayer player)
			{
				InputPlayerDefinition definition = Configuration.Players.Find(d => d.Id == player.Id);
				return definition != null && definition.AssignGamepads && player.GamepadCount < settings.MaxGamepadsPerPlayer;
			}

			if (settings.ReassignToPreviousOwner && previousOwners.TryGetValue(gamepad.HardwareId, out int ownerId))
			{
				InputPlayer previous = players.Find(p => p.Id == ownerId);
				if (previous != null && Wants(previous))
				{
					previous.AddController(gamepad);
					return;
				}
			}

			InputPlayer best = null;
			foreach (InputPlayer player in players)
			{
				if (!Wants(player))
				{
					continue;
				}

				if (!settings.DistributeGamepadsEvenly)
				{
					best = player;
					break;
				}

				if (best == null || player.GamepadCount < best.GamepadCount)
				{
					best = player;
				}
			}

			best?.AddController(gamepad);
		}

		private static string AppName()
		{
			try
			{
				string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath);
				return string.IsNullOrEmpty(name) ? "Game" : name;
			}
			catch (Exception)
			{
				return "Game";
			}
		}

		#endregion
	}

	/// <summary>Project Settings > Input.</summary>
	[Serializable]
	public sealed class InputProjectSettings
	{
		/// <summary>Asset path of the <see cref="InputConfiguration" /> the game uses ("" : no input system).</summary>
		public string Configuration = "";
	}

	/// <summary>The players' remapped maps as saved in the user's data folder.</summary>
	[Serializable]
	public sealed class InputUserData
	{
		public List<PlayerMaps> Players = new List<PlayerMaps>();

		[Serializable]
		public sealed class PlayerMaps
		{
			public int PlayerId;
			public List<ControllerMap> Maps = new List<ControllerMap>();
			public List<bool> Enabled = new List<bool>();
		}
	}
}
