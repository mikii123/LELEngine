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
			"  click <element> [--window W]           runs the element's action as a click would (no mouse: covered or not;\n" +
			"                                         a window behind another tab comes forward); --window when names repeat\n" +
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
			"  menus [filter]                         every main menu item with its path (enabled, ticked, shortcut)\n" +
			"  menu <path>                            run a main menu item: \"GameObject/Create Empty\", \"Window/Reset Layout\"\n" +
			"  hierarchy                              the open scene's objects (ids, components)\n" +
			"  select <object>|none                   object: Parent/Child, a unique name, or #id\n" +
			"  select-asset <assetPath>\n" +
			"  reparent <object> <parent|none>        (instead of dragging in the hierarchy)\n" +
			"  rename <object> <name>\n" +
			"  inspect <object>                       components and their fields\n" +
			"  get <object> <Component.field>\n" +
			"  set <object> <Component.field> <value> one undo step; values: 1.5, true, EnumName, 1,2,3, null, new:Class\n" +
			"  add-component <object> <Type>\n" +
			"  scene open <path> | scene save | scene new\n" +
			"  play | stop | pause | step | undo | redo\n" +
			"  refresh                                read changed asset files now\n" +
			"  console [N] [--errors] [--clear]       the last N (30) messages\n" +
			"  wait <frames> | wait idle [seconds]    idle: scripts built, assets refreshed, loads done\n" +
			"  exec <Namespace.Type.Method> [args]    a static method of the scripts / engine / editor (Task allowed)\n" +
			"  quit [--discard]                       batch mode: end; the editor's window: close it (asks about unsaved changes\n" +
			"                                         unless --discard)\n";

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
					bool takesValue = name is "frames" or "window" or "count" or "timeout" or "hold";
					options[name] = takesValue && i + 1 < words.Count ? words[++i] : "";
				}
				else
				{
					args.Add(word);
				}
			}

			try
			{
				return await Run(words[0].ToLowerInvariant(), args, options);
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
					Need(args, 1, "click <element> [--window W]");
					await EditorAutomation.Click(Joined(args), options.GetValueOrDefault("window"));
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
					Need(args, 2, "reparent <object> <parent|none>");
					EditorAutomation.Reparent(args[0], args[1]);
					await EditorAutomation.NextFrame(2);
					break;

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

				case "menus":
				{
					string filter = args.Count > 0 ? Joined(args) : null;
					foreach (MenuItemInfo item in EditorAutomation.GetMenuItems())
					{
						if (filter == null || item.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
						{
							text.Append(item).Append('\n');
						}
					}

					break;
				}

				case "menu":
					Need(args, 1, "menu <path>");
					if (!EditorAutomation.ExecuteMenuItem(Joined(args)))
					{
						return CommandResult.Fail($"No enabled menu item '{Joined(args)}' (list them: menus)");
					}

					await EditorAutomation.NextFrame(2);
					text.Append("Ran ").Append(Joined(args)).Append('\n');
					break;

				case "hierarchy":
					text.Append(EditorAutomation.DescribeHierarchy());
					break;

				case "select":
					Need(args, 1, "select <object>|none");
					EditorAutomation.Select(Joined(args));
					await EditorAutomation.NextFrame(2);
					break;

				case "select-asset":
					Need(args, 1, "select-asset <assetPath>");
					EditorAutomation.SelectAsset(Joined(args));
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

				case "add-component":
				{
					Need(args, 2, "add-component <object> <Type>");
					Behaviour added = EditorAutomation.AddComponent(args[0], args[1]);
					await EditorAutomation.NextFrame(2);
					text.Append(added != null ? "Added " + added.GetType().Name : "Not added").Append('\n');
					break;
				}

				case "scene":
					Need(args, 1, "scene open <path> | scene save | scene new");
					switch (args[0].ToLowerInvariant())
					{
						case "open":
							Need(args, 2, "scene open <path>");
							EditorAutomation.OpenScene(string.Join(" ", args.Skip(1)));
							break;
						case "save":
							EditorAutomation.SaveScene();
							break;
						case "new":
							EditorAutomation.Editor.NewScene();
							break;
						default:
							throw new ArgumentException("scene open <path> | scene save | scene new");
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
					EditorAutomation.Quit(options.ContainsKey("discard"));
					text.Append("Quitting\n");
					break;

				default:
					return CommandResult.Fail($"Unknown command '{command}' (try: help)");
			}

			result.Output = text.ToString();
			return result;
		}

		private static string Status()
		{
			EditorApplication editor = EditorAutomation.Editor;
			var text = new StringBuilder();
			text.Append("Project ").Append(editor.Project?.Name ?? "(none)").Append(" (").Append(editor.Project?.RootPath).Append(")\n");
			text.Append("Mode ").Append(editor.HasUI ? "editor window" : "batch (no UI)").Append(", frame ").Append(EditorAutomation.Frame).Append('\n');
			text.Append("Scene ").Append(editor.Scene?.Path ?? "(unsaved)").Append(editor.SceneDirty ? " (unsaved changes)" : "").Append('\n');
			text.Append("Play ").Append(editor.PlayState).Append('\n');
			text.Append("Selection ").Append(editor.Selected != null ? editor.Selected.Name + " #" + editor.Selected.Id : editor.SelectedAsset ?? "none").Append('\n');
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
