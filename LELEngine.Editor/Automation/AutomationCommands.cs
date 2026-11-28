using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine.Editor.Automation
{
	/// <summary>The answer to an automation command: text for the caller, files it wrote (screenshots).</summary>
	internal sealed class CommandResult
	{
		public bool Ok = true;
		public string Output = "";
		public string Error;
		public List<string> Files = new List<string>();

		public static CommandResult Fail(string error)
		{
			return new CommandResult { Ok = false, Error = error };
		}
	}

	/// <summary>
	///     The automation server's commands (LELEngine.Editor.Console -command "..."), one line each, words split at spaces
	///     ("double quotes" keep spaces). They are <see cref="EditorAutomation" />'s operations; <see cref="Help" /> lists
	///     them. Each runs between editor frames; a command over several frames (input, waits) answers when it is done.
	/// </summary>
	internal static class AutomationCommands
	{
		#region PublicFields

		public const string Help =
			"LELEngine editor automation commands (words split at spaces, \"quotes\" keep spaces; window names as 'windows' lists them):\n" +
			"  help                                   this text\n" +
			"  status                                 project, scene, play mode, busy (scripts / assets / loads), UI or batch\n" +
			"  windows                                every window: open, on screen, place and size\n" +
			"  screenshot <window> [WxH] [file.png] [--layout] [--frames N]\n" +
			"                                         PNG of that window only (never another window over it); no size: its size on\n" +
			"                                         screen, as the last frame drew it (with the menus and popups it opened);\n" +
			"                                         another size, --layout or batch mode: laid out again off screen at that size.\n" +
			"                                         Views render N frames (20) for a new size. Default file:\n" +
			"                                         <project>/Library/Screenshots/. Prints the named items (anchors) with their\n" +
			"                                         rectangles in the image\n" +
			"  elements <window>                      the UI elements the window drew, with values and rectangles: kind:path,\n" +
			"                                         the path as the window shows it, inspector fields under their component:\n" +
			"                                         header:Mover, input:Mover/Speed = 5, color:Mover/Tint = 1, 0, 0, 1,\n" +
			"                                         combo:Mover/Mode = Fast, input:Mover/Waypoints/0 = 1, 2, 3,\n" +
			"                                         button:Add Component, menu:File/Save Scene, hierarchy:<object>, ...\n" +
			"                                         An element is named by its whole name, its path or the path's end:\n" +
			"                                         \"input:Mover/Speed\", \"Mover/Speed\" or \"Speed\" (when only one is drawn)\n" +
			"  open|close|focus <window>              (open also takes an editor script window's class name)\n" +
			"  click <element> [--window W] [--ctrl] [--shift]\n" +
			"                                         runs the element's action as a click would (no mouse: covered or not;\n" +
			"                                         a window behind another tab comes forward); --window when names repeat;\n" +
			"                                         --ctrl / --shift: the click holds the key (hierarchy rows: add / range)\n" +
			"  double-click <element> [--window W]    project files, console messages, object fields, ...\n" +
			"  context <element> [--window W]         opens its right-click menu; then: click \"menu:Duplicate\"\n" +
			"  input <element> <value> [--window W]   sets a value field as an edit of it (undo in the inspector): text,\n" +
			"                                         numbers, vectors \"1, 2, 3\", colors \"#FF8800\" / \"1, 0.5, 0\", checkboxes\n" +
			"                                         true / false, combos (enums, layers, shaders) by their option's text\n" +
			"  scroll <window> <pixels|top|bottom>    the window's scrolling part (its list)\n" +
			"  key <keys> [--hold S] [--window W | --editor]\n" +
			"                                         Enter, F2, Ctrl+S, Ctrl+Shift+Z, Q, Space, 1, UpArrow (ImGui key names),\n" +
			"                                         held S seconds (else a frame). Play mode: the game's keyboard (as with the\n" +
			"                                         game view focused; a paused game reads nothing); edit mode, --window or\n" +
			"                                         --editor: the editor's focused window\n" +
			"  type <text> [--window W | --editor]    characters: play mode the game UI's focused text field, else the\n" +
			"                                         editor's active text field\n" +
			"  actions [filter]                       every editor action by its menu path (enabled, ticked, shortcut), the\n" +
			"                                         editor scripts' [MenuItem]s too (menus: the same)\n" +
			"  action <path>                          runs the action itself, no menu or window, nothing asked:\n" +
			"                                         \"GameObject/Create Empty\", \"Window/Reset Layout\" (menu: the same); one that\n" +
			"                                         needs an answer fails and says why\n" +
			"  reserialize                            Force Reserialize All Assets; answers when it has ended, with its summary\n" +
			"  hierarchy                              the open scene's objects (ids, components)\n" +
			"  select <object>|none [--add]           object: Parent/Child, a unique name, or #id; --add: it joins the\n" +
			"                                         objects already selected (status lists them)\n" +
			"  select-asset <assetPath> [--add]       --add: it joins the files already selected (the inspector edits\n" +
			"                                         files of one kind together)\n" +
			"  delete-asset <assetPath>               a file (with its meta) or a folder, to the Recycle Bin on Windows\n" +
			"  reparent <object> <parent|none> [index] (instead of dragging in the hierarchy); index: its place among the\n" +
			"                                         new siblings, else after the last\n" +
			"  reorder <object> <index|first|last>    its place among its siblings (the scene's roots for a root)\n" +
			"  rename <object> <name>\n" +
			"  inspect <object>                       components and their fields\n" +
			"  get <object> <Component.field>\n" +
			"  set <object> <Component.field> <value> one undo step; values: 1.5, true, EnumName, 1,2,3, null, new:Class\n" +
			"  static <object> on|off [--children|--self]\n" +
			"                                         the object's Static flag; one with children needs --children (they change\n" +
			"                                         too) or --self (this object only): the question the inspector asks\n" +
			"  layer <object> <name|0..31> [--children|--self]\n" +
			"                                         the object's layer, with its children as for static; one undo step each\n" +
			"  add-component <object> <Type>\n" +
			"  impostor create <prefab or model>      an impostor asset of the file next to it (\"Name Impostor.asset\"), baked\n" +
			"  impostor bake <asset>|all              bakes it again into its files (atlases, proxy model, material)\n" +
			"  impostor add-level <object> <asset>    the baked impostor as the last level of the object's LOD group (an\n" +
			"                                         object without a group gets one); one undo step\n" +
			"  scene open <path> [--save|--discard] | scene new [--save|--discard] | scene save [path]\n" +
			"                                         unsaved changes (scene, prefab) without --save / --discard: it fails\n" +
			"  play | stop | pause | step | undo | redo\n" +
			"  refresh                                import changed files now, also those the watchers have not reported yet\n" +
			"                                         (every command first imports what changed since the last one)\n" +
			"  console [N] [--errors] [--clear]       the last N (30) messages, each with its call stack\n" +
			"  sessions [N] [--errors] [--remote [--machine M] [--scene S]]\n" +
			"                                         recorded play sessions, newest first (N: 20), a line each: start, id,\n" +
			"                                         scene, length, frames, frame times, hitches, errors; --errors: with errors\n" +
			"                                         or a crash; --remote: the play session server's (every editor of the team,\n" +
			"                                         Preferences > Play Sessions > Server), --machine / --scene filter them\n" +
			"  session <id|folder|latest> [--remote]  one session: its line, files and whole session.json (where it ran, how it\n" +
			"                                         ended, the summary); local: its folder (frames.csv, events.jsonl)\n" +
			"  session fetch <id>                     a server session's files, checked and unpacked, into\n" +
			"                                         <project>/Library/RemoteSessions/<id>/ (session.json, events.jsonl,\n" +
			"                                         frames.csv: read them as a local session's)\n" +
			"  session upload <id|folder|latest|all> [--force]\n" +
			"                                         sends local sessions to the server now (not twice unless --force)\n" +
			"  wait <frames> | wait idle [seconds]    idle: scripts built, assets refreshed, loads done\n" +
			"  exec <Namespace.Type.Method> [args]    a static method of the scripts / engine / editor (Task allowed)\n" +
			"  quit [--save|--discard]                ends the editor; unsaved changes without --save / --discard: it fails\n" +
			"Commands run one at a time, in the order they come (from any number of clients), each once the one before\n" +
			"has answered. None opens a window or asks; the ones acting on windows (click, input, key, type, context,\n" +
			"scroll, focus) are the only ones that touch the UI.\n";

		#endregion

		#region PublicMethods

		public static async Task<CommandResult> Execute(string line)
		{
			List<string> words = Split(line);
			if (words.Count == 0)
			{
				return CommandResult.Fail("Empty command (try: help)");
			}

			var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			var args = new List<string>();
			for (int i = 1; i < words.Count; i++)
			{
				string word = words[i];
				if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2)
				{
					string name = word.Substring(2);
					bool takesValue = name is "frames" or "window" or "count" or "timeout" or "hold" or "machine" or "scene";
					options[name] = takesValue && i + 1 < words.Count ? words[++i] : "";
				}
				else
				{
					args.Add(word);
				}
			}

			string command = words[0].ToLowerInvariant();
			try
			{
				// A command is the automation coming back to the editor: what changed on disk meanwhile imports first, once (an
				// automated editor is never in front; EditorApplication.ImportsPaused). refresh imports itself; quit and help need
				// nothing.
				if (command is not ("quit" or "help" or "refresh"))
				{
					EditorAutomation.Editor.ImportPendingChanges();
				}

				CommandResult result = await Run(command, args, options);
				// A command that does something may have changed what the views show through code that tells nobody (exec, an
				// action, a script's button): they draw again, as after the user's own click. Commands that only read leave an
				// idle editor idle.
				if (command is not ("help" or "status" or "windows" or "screenshot" or "elements" or "anchors" or "actions" or "menus" or "hierarchy" or "inspect" or "get" or "console" or "wait" or "refresh" or "quit" or "sessions" or "session"))
				{
					EditorAutomation.Editor.NotifySceneChanged();
				}

				return result;
			}
			catch (Exception e) when (e is ArgumentException or InvalidOperationException or FormatException or IOException or NotSupportedException or UnauthorizedAccessException)
			{
				return CommandResult.Fail(e.Message);
			}
		}

		#endregion

		#region PrivateMethods

		private static async Task<CommandResult> Run(string command, List<string> args, Dictionary<string, string> options)
		{
			var result = new CommandResult();
			var text = new StringBuilder();
			switch (command)
			{
				case "help":
					text.Append(Help);
					break;

				case "status":
					text.Append(Status());
					break;

				case "windows":
					foreach (EditorWindowInfo window in EditorAutomation.GetWindows())
					{
						text.Append(window).Append('\n');
					}

					break;

				case "screenshot":
				{
					Need(args, 1, "screenshot <window> [WxH] [file.png]");
					// The words that are not a size or a .png file name the window ("Probe Window" works unquoted too).
					int width = 0;
					int height = 0;
					string file = null;
					var windowWords = new List<string>();
					foreach (string arg in args)
					{
						if (TryParseSize(arg, out int w, out int h))
						{
							width = w;
							height = h;
						}
						else if (arg.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
						{
							file = arg;
						}
						else
						{
							windowWords.Add(arg);
						}
					}

					int frames = options.TryGetValue("frames", out string framesText) ? int.Parse(framesText, CultureInfo.InvariantCulture) : WindowCapture.DefaultViewFrames;
					EditorScreenshot shot = EditorAutomation.GetScreenshot(Joined(windowWords), width, height, options.ContainsKey("layout"), frames);
					file = Path.GetFullPath(file ?? DefaultScreenshotPath(shot.Window));
					shot.SavePng(file);
					result.Files.Add(file);
					text.Append($"{file}\n{shot.Window} {shot.Width}x{shot.Height} ({(shot.Live ? "as the last frame drew it" : "laid out off screen")}), {shot.Anchors.Count} anchors\n");
					AppendAnchors(text, shot.Anchors);
					break;
				}

				case "elements":
				case "anchors":
					Need(args, 1, "elements <window>");
					AppendAnchors(text, EditorAutomation.GetElements(Joined(args)));
					break;

				case "open":
					Need(args, 1, "open <window>");
					EditorAutomation.OpenWindow(Joined(args));
					await EditorAutomation.NextFrame(2);
					text.Append(EditorAutomation.GetWindow(Joined(args))?.ToString() ?? "opened").Append('\n');
					break;

				case "close":
					Need(args, 1, "close <window>");
					EditorAutomation.CloseWindow(Joined(args));
					await EditorAutomation.NextFrame();
					break;

				case "focus":
					Need(args, 1, "focus <window>");
					EditorAutomation.FocusWindow(Joined(args));
					await EditorAutomation.NextFrame(2);
					break;

				case "click":
					Need(args, 1, "click <element> [--window W] [--ctrl] [--shift]");
					await EditorAutomation.Click(Joined(args), options.GetValueOrDefault("window"), options.ContainsKey("ctrl"), options.ContainsKey("shift"));
					text.Append("Clicked ").Append(Joined(args)).Append('\n');
					break;

				case "double-click":
					Need(args, 1, "double-click <element> [--window W]");
					await EditorAutomation.DoubleClick(Joined(args), options.GetValueOrDefault("window"));
					text.Append("Double-clicked ").Append(Joined(args)).Append('\n');
					break;

				case "context":
					Need(args, 1, "context <element> [--window W]");
					await EditorAutomation.OpenContextMenu(Joined(args), options.GetValueOrDefault("window"));
					text.Append("Context menu of ").Append(Joined(args)).Append(" open; its items are elements \"menu:...\"\n");
					break;

				case "input":
					Need(args, 2, "input <element> <value> [--window W]");
					await EditorAutomation.Input(args[0], string.Join(" ", args.Skip(1)), options.GetValueOrDefault("window"));
					text.Append("Set ").Append(args[0]).Append('\n');
					break;

				case "scroll":
				{
					Need(args, 2, "scroll <window> <pixels|top|bottom>");
					string amount = args[^1].ToLowerInvariant();
					float pixels = amount == "top" ? float.NegativeInfinity : amount == "bottom" ? float.PositiveInfinity : float.Parse(amount, CultureInfo.InvariantCulture);
					await EditorAutomation.Scroll(string.Join(" ", args.Take(args.Count - 1)), pixels);
					break;
				}

				case "reparent":
					Need(args, 2, "reparent <object> <parent|none> [index]");
					EditorAutomation.Reparent(args[0], args[1], args.Count > 2 ? (int?)int.Parse(args[2], CultureInfo.InvariantCulture) : null);
					await EditorAutomation.NextFrame(2);
					break;

				case "reorder":
				{
					Need(args, 2, "reorder <object> <index|first|last>");
					string place = args[1].ToLowerInvariant();
					int index = place == "first" ? 0 : place == "last" ? int.MaxValue : int.Parse(place, CultureInfo.InvariantCulture);
					EditorAutomation.Reorder(args[0], index);
					await EditorAutomation.NextFrame(2);
					break;
				}

				case "rename":
					Need(args, 2, "rename <object> <name>");
					EditorAutomation.Rename(args[0], string.Join(" ", args.Skip(1)));
					await EditorAutomation.NextFrame(2);
					break;

				case "key":
				{
					Need(args, 1, "key <keys> [--hold S] [--window W | --editor]");
					string window = options.GetValueOrDefault("window");
					bool toEditor = options.ContainsKey("editor");
					double hold = options.TryGetValue("hold", out string holdText) ? double.Parse(holdText, CultureInfo.InvariantCulture) : 0;
					bool game = EditorAutomation.InputGoesToGame(window, toEditor);
					await EditorAutomation.Key(Joined(args), window, hold, toEditor);
					text.Append("Pressed ").Append(Joined(args)).Append(game ? " in the game" : " in the editor").Append('\n');
					break;
				}

				case "type":
				{
					Need(args, 1, "type <text> [--window W | --editor]");
					string window = options.GetValueOrDefault("window");
					bool toEditor = options.ContainsKey("editor");
					bool game = EditorAutomation.InputGoesToGame(window, toEditor);
					await EditorAutomation.TypeText(Joined(args), window, toEditor);
					text.Append("Typed into ").Append(game ? "the game" : "the editor").Append('\n');
					break;
				}

				case "actions":
				case "menus":
				{
					string filter = args.Count > 0 ? Joined(args) : null;
					foreach (MenuItemInfo item in EditorAutomation.GetActions())
					{
						if (filter == null || item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
						{
							text.Append(item).Append('\n');
						}
					}

					break;
				}

				case "action":
				case "menu":
					Need(args, 1, "action <path>");
					if (!EditorAutomation.RunAction(Joined(args)))
					{
						return CommandResult.Fail($"No action '{Joined(args)}' (list them: actions)");
					}

					await EditorAutomation.NextFrame(2);
					text.Append("Ran ").Append(Joined(args)).Append('\n');
					break;

				case "reserialize":
					text.Append(await EditorAutomation.ReserializeAllAssets()).Append('\n');
					break;

				case "hierarchy":
					text.Append(EditorAutomation.DescribeHierarchy());
					break;

				case "select":
					Need(args, 1, "select <object>|none [--add]");
					EditorAutomation.Select(Joined(args), options.ContainsKey("add"));
					await EditorAutomation.NextFrame(2);
					break;

				case "select-asset":
					Need(args, 1, "select-asset <assetPath> [--add]");
					EditorAutomation.SelectAsset(Joined(args), options.ContainsKey("add"));
					await EditorAutomation.NextFrame(2);
					break;

				case "delete-asset":
					Need(args, 1, "delete-asset <assetPath>");
					EditorAutomation.DeleteAsset(Joined(args));
					await EditorAutomation.NextFrame(2);
					break;

				case "inspect":
					Need(args, 1, "inspect <object>");
					text.Append(EditorAutomation.DescribeObject(Joined(args)));
					break;

				case "get":
					Need(args, 2, "get <object> <Component.field>");
					text.Append(EditorAutomation.GetField(args[0], args[1])).Append('\n');
					break;

				case "set":
					Need(args, 3, "set <object> <Component.field> <value>");
					EditorAutomation.SetField(args[0], args[1], string.Join(" ", args.Skip(2)));
					await EditorAutomation.NextFrame(2);
					text.Append(args[0]).Append(' ').Append(args[1]).Append(" = ").Append(EditorAutomation.GetField(args[0], args[1])).Append('\n');
					break;

				case "static":
				{
					Need(args, 2, "static <object> on|off [--children|--self]");
					bool value = args[1].ToLowerInvariant() switch
					{
						"on" or "true" or "1" => true,
						"off" or "false" or "0" => false,
						_ => throw new ArgumentException("static <object> on|off [--children|--self]")
					};
					int changed = EditorAutomation.SetStatic(args[0], value, ChildrenOption(options));
					await EditorAutomation.NextFrame(2);
					text.Append(changed == 0 ? "Nothing to change" : $"{changed} object{(changed == 1 ? "" : "s")} {(value ? "static" : "not static")} now").Append('\n');
					break;
				}

				case "layer":
				{
					Need(args, 2, "layer <object> <layer name|0..31> [--children|--self]");
					int changed = EditorAutomation.SetLayer(args[0], string.Join(" ", args.Skip(1)), ChildrenOption(options));
					await EditorAutomation.NextFrame(2);
					text.Append(changed == 0 ? "Nothing to change" : $"{changed} object{(changed == 1 ? "" : "s")} on layer {string.Join(" ", args.Skip(1))} now").Append('\n');
					break;
				}

				case "add-component":
				{
					Need(args, 2, "add-component <object> <Type>");
					Behaviour added = EditorAutomation.AddComponent(args[0], args[1]);
					await EditorAutomation.NextFrame(2);
					text.Append(added != null ? "Added " + added.GetType().Name : "Not added").Append('\n');
					break;
				}

				case "impostor":
				{
					const string usage = "impostor create <prefab or model> | impostor bake <asset>|all | impostor add-level <object> <asset>";
					Need(args, 1, usage);
					switch (args[0].ToLowerInvariant())
					{
						case "create":
							Need(args, 2, "impostor create <prefab or model>");
							text.Append("Created ").Append(EditorAutomation.CreateImpostor(string.Join(" ", args.Skip(1)))).Append('\n');
							break;
						case "bake":
							Need(args, 2, "impostor bake <asset>|all");
							if (args.Count == 2 && string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase))
							{
								text.Append("Baked ").Append(EditorAutomation.BakeAllImpostors()).Append(" impostor assets\n");
							}
							else
							{
								string asset = string.Join(" ", args.Skip(1));
								text.Append("Baked ").Append(asset).Append(": ").Append(EditorAutomation.BakeImpostor(asset)).Append('\n');
							}

							break;
						case "add-level":
						{
							Need(args, 3, "impostor add-level <object> <asset>");
							GameObject child = EditorAutomation.AddImpostorLevel(args[1], string.Join(" ", args.Skip(2)));
							text.Append(child != null ? "Added " + child.Name + " #" + child.Id : "Not added").Append('\n');
							break;
						}
						default:
							throw new ArgumentException(usage);
					}

					await EditorAutomation.NextFrame(2);
					break;
				}

				case "scene":
					Need(args, 1, "scene open <path> [--save | --discard] | scene save [path] | scene new [--save | --discard]");
					switch (args[0].ToLowerInvariant())
					{
						case "open":
							Need(args, 2, "scene open <path> [--save | --discard]");
							EditorAutomation.OpenScene(string.Join(" ", args.Skip(1)), Unsaved(options));
							break;
						case "save":
							EditorAutomation.SaveScene(args.Count > 1 ? string.Join(" ", args.Skip(1)) : null);
							break;
						case "new":
							EditorAutomation.NewScene(Unsaved(options));
							break;
						default:
							throw new ArgumentException("scene open <path> [--save | --discard] | scene save [path] | scene new [--save | --discard]");
					}

					await EditorAutomation.NextFrame(3);
					text.Append("Scene ").Append(EditorAutomation.ScenePath ?? "(unsaved)").Append(EditorAutomation.IsSceneDirty ? " *" : "").Append('\n');
					break;

				case "play":
					EditorAutomation.Play();
					// Asked for while the scripts compile, play starts after their build, and not at all with errors.
					await EditorAutomation.WaitUntil(() => !EditorAutomation.IsWaitingToPlay, 600);
					await EditorAutomation.NextFrame(2);
					if (!EditorAutomation.IsPlaying && EditorAutomation.HasScriptErrors)
					{
						return CommandResult.Fail("Play mode did not start: the scripts have compile errors (see: console)");
					}

					break;

				case "stop":
					EditorAutomation.Stop();
					await EditorAutomation.NextFrame(2);
					break;

				case "pause":
					EditorAutomation.Pause();
					await EditorAutomation.NextFrame();
					break;

				case "step":
					EditorAutomation.Step();
					await EditorAutomation.NextFrame();
					break;

				case "undo":
					EditorAutomation.Undo();
					await EditorAutomation.NextFrame(2);
					break;

				case "redo":
					EditorAutomation.Redo();
					await EditorAutomation.NextFrame(2);
					break;

				case "refresh":
					EditorAutomation.RefreshAssets();
					await EditorAutomation.NextFrame(2);
					break;

				case "console":
				{
					if (options.ContainsKey("clear"))
					{
						EditorAutomation.ClearConsole();
						break;
					}

					int count = args.Count > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 30;
					foreach (ConsoleMessage message in EditorAutomation.GetConsole(count, options.ContainsKey("errors")))
					{
						text.Append(message).Append('\n');
					}

					break;
				}

				case "wait":
					Need(args, 1, "wait <frames> | wait idle [seconds]");
					if (string.Equals(args[0], "idle", StringComparison.OrdinalIgnoreCase))
					{
						double seconds = args.Count > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 120;
						if (!await EditorAutomation.WaitForIdle(seconds))
						{
							return CommandResult.Fail($"Still busy after {seconds:0} s ({Status().Trim()})");
						}

						text.Append(Status());
					}
					else
					{
						await EditorAutomation.NextFrame(int.Parse(args[0], CultureInfo.InvariantCulture));
					}

					break;

				case "sessions":
				{
					int count = args.Count > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 20;
					bool errors = options.ContainsKey("errors");
					if (options.ContainsKey("remote"))
					{
						options.TryGetValue("machine", out string machine);
						options.TryGetValue("scene", out string scene);
						(bool ok, string listing) = await Task.Run(() => PlaySessionServer.Instance.List(count, machine, scene, errors));
						if (!ok)
						{
							return CommandResult.Fail(listing);
						}

						text.Append(listing);
					}
					else
					{
						PlaySessionRecorder recorder = EditorAutomation.Editor.Sessions;
						string root = recorder.Root ?? throw new InvalidOperationException("No project is open.");
						List<PlaySessionListing> sessions = await Task.Run(() => PlaySessionStore.List(root, recorder.InUse));
						sessions = sessions.FindAll(s => !errors || s.Errors + s.Exceptions > 0 || s.Interrupted);
						text.Append($"{sessions.Count} session{(sessions.Count == 1 ? "" : "s")} in {root} (newest first; 'session <id|folder|latest>' for one):\n");
						foreach (PlaySessionListing session in sessions.Take(count))
						{
							text.Append(LocalRow(session)).Append('\n');
						}
					}

					break;
				}

				case "session":
				{
					Need(args, 1, "session <id|folder|latest> [--remote] | session fetch <id> | session upload <id|folder|latest|all> [--force]");
					string what = args[0].ToLowerInvariant();
					if (what == "fetch")
					{
						Need(args, 2, "session fetch <id>");
						string library = EditorAutomation.Editor.Project?.LibraryPath ?? throw new InvalidOperationException("No project is open.");
						(bool ok, string fetched) = await Task.Run(() => PlaySessionServer.Instance.Fetch(args[1], library));
						if (!ok)
						{
							return CommandResult.Fail(fetched);
						}

						text.Append(fetched);
						break;
					}

					if (what == "upload")
					{
						Need(args, 2, "session upload <id|folder|latest|all> [--force]");
						PlaySessionRecorder recorder = EditorAutomation.Editor.Sessions;
						string root = recorder.Root ?? throw new InvalidOperationException("No project is open.");
						bool force = options.ContainsKey("force");
						List<PlaySessionListing> sessions = await Task.Run(() => PlaySessionStore.List(root, recorder.InUse));
						List<PlaySessionListing> chosen = string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase)
							? sessions.FindAll(s => !s.Recording)
							: new List<PlaySessionListing> { FindLocal(sessions, args[1]) };
						bool allOk = true;
						foreach (PlaySessionListing session in chosen)
						{
							(bool ok, string message) = await Task.Run(() => PlaySessionServer.Instance.Send(session.Folder, force));
							allOk &= ok;
							text.Append(ok ? "" : "FAILED ").Append(session.Name).Append(": ").Append(message).Append('\n');
						}

						if (!allOk)
						{
							return CommandResult.Fail(text.ToString().TrimEnd());
						}

						break;
					}

					if (options.ContainsKey("remote"))
					{
						(bool ok, string described) = await Task.Run(() => PlaySessionServer.Instance.Describe(args[0]));
						if (!ok)
						{
							return CommandResult.Fail(described);
						}

						text.Append(described);
						break;
					}

					{
						PlaySessionRecorder recorder = EditorAutomation.Editor.Sessions;
						string root = recorder.Root ?? throw new InvalidOperationException("No project is open.");
						List<PlaySessionListing> sessions = await Task.Run(() => PlaySessionStore.List(root, recorder.InUse));
						PlaySessionListing session = FindLocal(sessions, args[0]);
						text.Append(LocalRow(session)).Append('\n');
						text.Append("folder: ").Append(session.Folder).Append('\n');
						foreach (string file in Directory.GetFiles(session.Folder))
						{
							text.Append($"  {Path.GetFileName(file)}  {new FileInfo(file).Length} bytes\n");
						}

						string header = Path.Combine(session.Folder, PlaySessionWriter.HeaderFile);
						if (File.Exists(header))
						{
							text.Append("session.json:\n").Append(File.ReadAllText(header)).Append('\n');
						}
					}

					break;
				}

				case "exec":
				{
					Need(args, 1, "exec <Namespace.Type.Method> [args]");
					int code = await EditorAutomation.ExecuteMethod(args[0], args.Skip(1).ToArray());
					if (code != 0)
					{
						return CommandResult.Fail($"{args[0]} returned {code}");
					}

					text.Append(args[0]).Append(" done\n");
					break;
				}

				case "quit":
					EditorAutomation.Quit(Unsaved(options));
					text.Append("Quitting\n");
					break;

				default:
					return CommandResult.Fail($"Unknown command '{command}' (try: help)");
			}

			result.Output = text.ToString();
			return result;
		}

		/// <summary>A local session by its id, its folder's name (or the start of it) or "latest" (the newest one).</summary>
		private static PlaySessionListing FindLocal(List<PlaySessionListing> sessions, string which)
		{
			if (sessions.Count == 0)
			{
				throw new InvalidOperationException("This project has no recorded play sessions.");
			}

			if (string.Equals(which, "latest", StringComparison.OrdinalIgnoreCase))
			{
				return sessions[0];
			}

			PlaySessionListing found = sessions.Find(s => string.Equals(s.Id, which, StringComparison.OrdinalIgnoreCase))
				?? sessions.Find(s => string.Equals(s.Name, which, StringComparison.OrdinalIgnoreCase));
			if (found != null)
			{
				return found;
			}

			List<PlaySessionListing> starting = sessions.FindAll(s => s.Name.StartsWith(which, StringComparison.OrdinalIgnoreCase) || (s.Id?.StartsWith(which, StringComparison.OrdinalIgnoreCase) ?? false));
			return starting.Count switch
			{
				1 => starting[0],
				0 => throw new ArgumentException($"No session '{which}' (see: sessions)"),
				_ => throw new ArgumentException($"'{which}' names {starting.Count} sessions: " + string.Join(", ", starting.Take(5).Select(s => s.Name)))
			};
		}

		/// <summary>A local session as one line, the shape of a remote one ('sessions --remote').</summary>
		private static string LocalRow(PlaySessionListing s)
		{
			string ended = s.Recording ? "recording" : s.Interrupted ? "INTERRUPTED: " + s.EndReason : s.EndReason;
			return string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss}  {1}  {2}  {3:0.0} s  {4} frames  avg {5:0.0} ms  worst {6:0} ms  {7} hitches  {8} errors  {9} warnings  {10}  [{11}]{12}",
				s.Started, s.Id, s.Scene, s.Seconds, s.Frames, s.AverageFrameMs, s.WorstFrameMs, s.Hitches, s.Errors + s.Exceptions, s.Warnings, ended, s.Name,
				s.SentTo != null ? "  sent" : "");
		}

		/// <summary>--save / --discard: what a command that would lose unsaved changes does with them (neither: it refuses).</summary>
		private static UnsavedChanges Unsaved(Dictionary<string, string> options)
		{
			if (options.ContainsKey("save") && options.ContainsKey("discard"))
			{
				throw new ArgumentException("--save or --discard, not both");
			}

			return options.ContainsKey("save") ? UnsavedChanges.Save : options.ContainsKey("discard") ? UnsavedChanges.Discard : UnsavedChanges.Refuse;
		}

		// --children: the object's children change too; --self: the object alone; neither: no answer (an object with
		// children then refuses, as the inspector would ask).
		private static bool? ChildrenOption(Dictionary<string, string> options)
		{
			if (options.ContainsKey("children") && options.ContainsKey("self"))
			{
				throw new ArgumentException("--children or --self, not both");
			}

			return options.ContainsKey("children") ? true : options.ContainsKey("self") ? false : (bool?)null;
		}

		private static string Status()
		{
			EditorApplication editor = EditorAutomation.Editor;
			var text = new StringBuilder();
			text.Append("Project ").Append(editor.Project?.Name ?? "(none)").Append(" (").Append(editor.Project?.RootPath).Append(")\n");
			text.Append("Mode ").Append(editor.HasUI ? "editor window" : "batch (no UI)").Append(", frame ").Append(EditorAutomation.Frame).Append('\n');
			text.Append("Scene ").Append(editor.Scene?.Path ?? "(unsaved)").Append(editor.SceneDirty ? " (unsaved changes)" : "").Append('\n');
			text.Append("Play ").Append(editor.PlayState).Append('\n');
			text.Append("Selection ").Append(editor.Selected != null ? editor.Selected.Name + " #" + editor.Selected.Id : editor.SelectedAsset ?? "none");
			if (editor.SelectedObjects.Count > 1)
			{
				// The active object first, then the others selected with it.
				text.Append(" and ").Append(editor.SelectedObjects.Count - 1).Append(" more: ").Append(string.Join(", ", editor.SelectedObjects.Where(go => go != editor.Selected).Take(20).Select(go => go.Name + " #" + go.Id)));
				if (editor.SelectedObjects.Count > 21)
				{
					text.Append(", ...");
				}
			}
			else if (editor.SelectedAssets.Count > 1)
			{
				// The active file first, then the others selected with it.
				text.Append(" and ").Append(editor.SelectedAssets.Count - 1).Append(" more: ").Append(string.Join(", ", editor.SelectedAssets.Where(path => path != editor.SelectedAsset).Take(20)));
				if (editor.SelectedAssets.Count > 21)
				{
					text.Append(", ...");
				}
			}

			text.Append('\n');
			text.Append("Scripts ").Append(editor.Scripts == null ? "none" : editor.Scripts.IsBuilding ? "building" : editor.Scripts.HasErrors ? "errors" : "ok").Append('\n');
			text.Append("Busy ").Append(editor.IsBusy ? "yes" : "no").Append('\n');
			return text.ToString();
		}

		private static void AppendAnchors(StringBuilder text, IReadOnlyList<UIAnchor> anchors)
		{
			const int max = 400;
			foreach (UIAnchor anchor in anchors.Take(max))
			{
				text.Append("  ").Append(anchor).Append('\n');
			}

			if (anchors.Count > max)
			{
				text.Append($"  ... {anchors.Count - max} more\n");
			}
		}

		private static string DefaultScreenshotPath(string window)
		{
			string name = new string(window.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
			return Path.Combine(EditorAutomation.ProjectPath ?? Environment.CurrentDirectory, "Library", "Screenshots", $"{name}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
		}

		private static bool TryParseSize(string text, out int width, out int height)
		{
			width = height = 0;
			string[] parts = text.ToLowerInvariant().Split('x');
			return parts.Length == 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out width) && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
		}

		private static void Need(List<string> args, int count, string usage)
		{
			if (args.Count < count)
			{
				throw new ArgumentException("Usage: " + usage);
			}
		}

		private static string Joined(List<string> args)
		{
			return string.Join(" ", args);
		}

		/// <summary>Words of a command line: spaces separate, "double quotes" keep spaces (\" is a quote inside them).</summary>
		internal static List<string> Split(string line)
		{
			var words = new List<string>();
			var word = new StringBuilder();
			bool quoted = false;
			bool any = false;
			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (quoted && c == '\\' && i + 1 < line.Length && line[i + 1] == '"')
				{
					word.Append('"');
					i++;
				}
				else if (c == '"')
				{
					quoted = !quoted;
					any = true;
				}
				else if (char.IsWhiteSpace(c) && !quoted)
				{
					if (word.Length > 0 || any)
					{
						words.Add(word.ToString());
						word.Clear();
						any = false;
					}
				}
				else
				{
					word.Append(c);
				}
			}

			if (word.Length > 0 || any)
			{
				words.Add(word.ToString());
			}

			return words;
		}

		#endregion
	}
}
