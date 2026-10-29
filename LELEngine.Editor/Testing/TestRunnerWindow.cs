using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Hexa.NET.ImGui;
using LELEngine.Testing;
using NVector2 = System.Numerics.Vector2;
using NVector4 = System.Numerics.Vector4;

namespace LELEngine.Editor
{
	/// <summary>
	///     Window > Test Runner: the project's unit tests (scripts in Tests folders) as a tree (namespace, class, test,
	///     cases) with their last outcome and time; runs all, the selection or the failed ones (in edit mode, a little per
	///     frame, the editor stays responsive), Debug runs with a debugger attached (asks for one first) and stops at the
	///     test's start; Break on Failure stops it where a test fails. The details show a result's message, its call
	///     stack (click: the code around the line; double click: the script editor) and the test's output and log.
	/// </summary>
	internal sealed class TestRunnerWindow : IEditorPanel
	{
		#region PublicFields

		public const string Title = "Test Runner";

		/// <summary>The window is shown (Window menu, close button; saved with the editor's windows).</summary>
		public bool Open;

		string IEditorPanel.PanelName => Title;
		string IEditorPanel.ImGuiName => ImGuiId;
		bool IEditorPanel.PanelOpen { get => Open; set => Open = value; }
		NVector2 IEditorPanel.DefaultCaptureSize => new NVector2(900, 700);
		void IEditorPanel.Focus() => Show();

		#endregion

		#region PrivateFields

		private const string ImGuiId = Title + "###testrunner";
		private const float SplitterHeight = 6f;

		private readonly EditorApplication editor;

		// The selected node (full name: it survives reloads; "" = the assembly).
		private string selected;

		// Open / closed rows the user chose (others: groups open, methods with cases closed).
		private readonly Dictionary<string, bool> expanded = new Dictionary<string, bool>(StringComparer.Ordinal);
		private string search = "";
		private string category;
		private bool showPassed = true;
		private bool showFailed = true;
		private bool showSkipped = true;
		private bool showNotRun = true;
		private float detailsHeight = 240f;
		private List<string> categories = new List<string>();
		private int seenVersion = -1;

		// Frames of the shown result whose code the user opened or closed (by index) and the result they belong to.
		private readonly HashSet<int> toggledFrames = new HashSet<int>();
		private TestResult framesOf;

		// Debug: the tests waiting for a debugger to attach (full names), the popup asking for one.
		private List<string> pendingDebug;
		private bool openDebugPopup;

		// A row to scroll to when it is drawn (the running test once it starts, a failure picked in the details).
		private string scrollTo;
		private string followed;

		private static readonly string ProcessName = Process.GetCurrentProcess().ProcessName;

		#endregion

		#region Constructors

		public TestRunnerWindow(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		public void Show()
		{
			Open = true;
			editor.RequestWindowFocus(ImGuiId);
		}

		/// <summary>The project closed.</summary>
		public void Reset()
		{
			selected = null;
			expanded.Clear();
			pendingDebug = null;
			framesOf = null;
		}

		public void Draw()
		{
			if (!Open)
			{
				return;
			}

			ImGui.SetNextWindowSize(new NVector2(820, 640), ImGuiCond.FirstUseEver);
			if (ImGui.Begin(ImGuiId, ref Open))
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

		#endregion

		#region PrivateMethods

		private void DrawContent()
		{
			EditorTests tests = editor.Tests;
			if (seenVersion != tests.Version)
			{
				seenVersion = tests.Version;
				categories = tests.Root == null ? new List<string>() : tests.Root.CollectTests().SelectMany(t => t.Categories).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
				if (category != null && !categories.Contains(category, StringComparer.OrdinalIgnoreCase))
				{
					category = null;
				}
			}

			// A debug run waits for the debugger.
			if (pendingDebug != null && Debugger.IsAttached && tests.CannotRunReason() == null)
			{
				List<string> names = pendingDebug;
				pendingDebug = null;
				StartDebug(names);
			}

			DrawToolbar(tests);
			DrawStatus(tests);
			ImGui.Separator();
			if (tests.Root == null)
			{
				DrawEmpty();
				DrawDebugPopup();
				return;
			}

			float available = ImGui.GetContentRegionAvail().Y;
			detailsHeight = Math.Clamp(detailsHeight, 60f, Math.Max(60f, available - 80f));
			float treeHeight = Math.Max(40f, available - detailsHeight - SplitterHeight);
			if (ImGui.BeginChild("##tests", new NVector2(0, treeHeight), ImGuiChildFlags.Borders))
			{
				DrawTree(tests);
			}

			ImGui.EndChild();
			DrawSplitter();
			if (ImGui.BeginChild("##details", new NVector2(0, 0), ImGuiChildFlags.Borders))
			{
				DrawDetails(tests);
			}

			ImGui.EndChild();
			DrawDebugPopup();
		}

		// ---------------------------------------------------------------- toolbar

		private void DrawToolbar(EditorTests tests)
		{
			string cannotRun = tests.CannotRunReason();
			bool canRun = cannotRun == null;
			TestNode selection = SelectedNode(tests);

			ImGui.BeginDisabled(!canRun);
			if (EditorGui.Button("Run All"))
			{
				tests.Start(new[] { tests.Root });
			}

			Tooltip(canRun ? "Runs every test (not the [Explicit] ones)" : cannotRun);
			ImGui.SameLine();
			ImGui.BeginDisabled(selection == null);
			if (EditorGui.Button("Run Selected"))
			{
				tests.Start(new[] { selection });
			}

			Tooltip(selection == null ? "Select a test, class or namespace" : "Runs " + selection.Name);
			ImGui.SameLine();
			if (EditorGui.Button("Debug Selected"))
			{
				DebugRun(new[] { selection });
			}

			Tooltip("Runs the selection with a debugger attached, stopping right before each test method (step into it)");
			ImGui.EndDisabled();
			ImGui.SameLine();
			List<TestNode> failed = canRun ? FailedTests(tests) : new List<TestNode>();
			ImGui.BeginDisabled(failed.Count == 0);
			if (EditorGui.Button("Run Failed"))
			{
				tests.Start(failed);
			}

			Tooltip(failed.Count == 0 ? "No test failed in its last run" : "Runs the " + failed.Count + " tests that failed");
			ImGui.EndDisabled();
			ImGui.EndDisabled();

			ImGui.SameLine();
			ImGui.BeginDisabled(!tests.IsRunning);
			bool stopping = tests.IsRunning && tests.Run.IsStopping;
			if (EditorGui.Button(stopping ? "Abort" : "Stop"))
			{
				if (stopping)
				{
					tests.Abort();
				}
				else
				{
					tests.Stop();
				}
			}

			Tooltip(stopping ? "Ends the run now (the test waiting does not finish, no tear down runs)" : "Ends the run after the current test");
			ImGui.EndDisabled();

			ImGui.SameLine();
			if (EditorGui.Button("Options"))
			{
				ImGui.OpenPopup("##testoptions");
			}

			if (ImGui.BeginPopup("##testoptions"))
			{
				EditorGui.Checkbox("Break on Failure", ref tests.Options.BreakOnFailure);
				Tooltip("With a debugger attached: stop where a test fails (the failing assert, the throw of an exception, an unexpected error being logged)");
				EditorGui.Checkbox("Stop on First Failure", ref tests.Options.StopOnFirstFailure);
				ImGui.Separator();
				if (EditorMenu.Item("Expand All"))
				{
					SetExpanded(tests.Root, true);
				}

				if (EditorMenu.Item("Collapse All"))
				{
					SetExpanded(tests.Root, false);
				}

				if (EditorMenu.Item("Open Script Project", "", false, editor.Scripts != null))
				{
					editor.Scripts.OpenInIde();
				}

				ImGui.EndPopup();
			}

			if (Debugger.IsAttached)
			{
				ImGui.SameLine();
				ImGui.TextColored(EditorStyle.Accent, "Debugger attached");
			}

			// Second line: search, outcome filters with counts, category.
			int passed = 0, failedCount = 0, skipped = 0, notRun = 0;
			if (tests.Root != null)
			{
				foreach (TestNode test in tests.Root.CollectTests())
				{
					switch (tests.OutcomeOf(test))
					{
						case TestOutcome.Passed:
							passed++;
							break;
						case TestOutcome.Failed:
							failedCount++;
							break;
						case TestOutcome.Skipped:
						case TestOutcome.Inconclusive:
							skipped++;
							break;
						default:
							notRun++;
							break;
					}
				}
			}

			ImGui.SetNextItemWidth(Math.Max(120f, ImGui.GetContentRegionAvail().X * 0.35f));
			EditorGui.InputTextWithHint("##search", "Search tests", ref search, 256);
			ImGui.SameLine();
			FilterToggle("Passed", passed, TestOutcome.Passed, ref showPassed);
			ImGui.SameLine();
			FilterToggle("Failed", failedCount, TestOutcome.Failed, ref showFailed);
			ImGui.SameLine();
			FilterToggle("Skipped", skipped, TestOutcome.Skipped, ref showSkipped);
			ImGui.SameLine();
			FilterToggle("Not Run", notRun, TestOutcome.NotRun, ref showNotRun);
			if (categories.Count > 0)
			{
				ImGui.SameLine();
				ImGui.SetNextItemWidth(140f);
				if (EditorGui.BeginCombo("##category", category ?? "All categories"))
				{
					if (EditorGui.Selectable("All categories", category == null))
					{
						category = null;
					}

					foreach (string name in categories)
					{
						if (EditorGui.Selectable(name, string.Equals(category, name, StringComparison.OrdinalIgnoreCase)))
						{
							category = name;
						}
					}

					EditorGui.EndCombo();
				}
			}
		}

		/// <summary>A toggle button with the outcome's icon and count: shows or hides tests with that outcome.</summary>
		private static void FilterToggle(string label, int count, TestOutcome outcome, ref bool shown)
		{
			float size = ImGui.GetFrameHeight();
			ImGui.PushStyleColor(ImGuiCol.Button, shown ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] * new NVector4(1f, 1f, 1f, 0.55f) : ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBg]);
			NVector2 start = ImGui.GetCursorScreenPos();
			bool clicked = EditorGui.Button("    " + count + "##" + label);
			ImGui.PopStyleColor();
			StatusIcon(ImGui.GetWindowDrawList(), start + new NVector2(size * 0.5f + 2f, size * 0.5f), size * 0.3f, outcome, false);
			// Automation names it by its outcome ("filter:Failed"), not by its count.
			TestAnchors.RecordElement("filter", label, 0, shown ? "shown" : "hidden");
			if (clicked)
			{
				shown = !shown;
			}

			Tooltip((shown ? "Hide " : "Show ") + label.ToLowerInvariant() + " tests");
		}

		private void DrawStatus(EditorTests tests)
		{
			ScriptDomain scripts = editor.Scripts;
			if (tests.IsRunning)
			{
				TestRun run = tests.Run;
				float fraction = run.Tests.Count == 0 ? 1f : (float)run.Completed / run.Tests.Count;
				string current = run.Current != null ? run.Current.FullName : "";
				ImGui.ProgressBar(fraction, new NVector2(-1, 0), $"{run.Completed} / {run.Tests.Count}  {current}");
				return;
			}

			if (scripts != null && scripts.IsBuilding)
			{
				ImGui.TextDisabled(scripts.StatusText);
				return;
			}

			if (tests.Root != null)
			{
				int count = tests.Root.CollectTests().Count;
				ImGui.TextDisabled($"{tests.Root.Name}: {count} test{(count == 1 ? "" : "s")}");
				if (tests.LastSummary != null)
				{
					ImGui.SameLine();
					ImGui.TextDisabled("·");
					ImGui.SameLine();
					ColoredText(tests.LastRunFailed ? EditorStyle.Get(EditorColor.Error) : EditorStyle.Get(EditorColor.Added), "Last run: " + tests.LastSummary);
				}
			}
		}

		// ---------------------------------------------------------------- empty states

		private void DrawEmpty()
		{
			ScriptDomain scripts = editor.Scripts;
			IReadOnlyList<BuildDiagnostic> diagnostics = scripts?.TestDiagnostics ?? Array.Empty<BuildDiagnostic>();
			List<BuildDiagnostic> errors = diagnostics.Where(d => d.IsError).ToList();
			if (errors.Count > 0)
			{
				ImGui.TextColored(EditorStyle.Get(EditorColor.Error), $"The tests do not compile ({errors.Count} error{(errors.Count == 1 ? "" : "s")}); the game scripts are not affected.");
				ImGui.Spacing();
				for (int i = 0; i < errors.Count; i++)
				{
					BuildDiagnostic error = errors[i];
					ImGui.PushID(i);
					string location = error.File != null ? Path.GetFileName(error.File) + ":" + error.Line : "";
					ImGui.PushTextWrapPos(0f);
					if (EditorGui.Selectable(location + "  " + error.Code + ": " + error.Message + "##error", false, ImGuiSelectableFlags.AllowDoubleClick) && error.File != null)
					{
						ExternalScriptEditor.Open(error.File, error.Line, error.Column);
					}

					ImGui.PopTextWrapPos();
					Tooltip(error.File != null ? error.File + ":" + error.Line + "\nClick: open in the script editor" : null);
					ImGui.PopID();
				}

				return;
			}

			if (scripts != null && (scripts.IsBuilding || scripts.HasTests))
			{
				ImGui.TextDisabled(scripts.IsBuilding ? "Compiling..." : "The tests are not loaded yet.");
				return;
			}

			ImGui.PushTextWrapPos(0f);
			ImGui.TextUnformatted("No tests in this project.");
			ImGui.TextDisabled("Tests are scripts in a folder named Tests anywhere under Assets. They compile into Game.Tests.dll " +
				"against the game's scripts and the engine (using LELEngine.Testing: [Test], [TestCase], [SetUp], Assert, LogAssert, " +
				"async tests awaiting TestContext.NextFrame()), run here in edit mode, and never reach a game build.");
			ImGui.PopTextWrapPos();
			ImGui.Spacing();
			ImGui.BeginDisabled(editor.Project == null);
			if (EditorGui.Button("Create Test Script"))
			{
				CreateTestScript();
			}

			ImGui.EndDisabled();
		}

		private void CreateTestScript()
		{
			try
			{
				string folder = Path.Combine(editor.Project.AssetsPath, ProjectGenerator.TestsFolderName);
				Directory.CreateDirectory(folder);
				string name = "NewTests";
				for (int n = 2; File.Exists(Path.Combine(folder, name + ".cs")); n++)
				{
					name = "NewTests" + n;
				}

				string file = Path.Combine(folder, name + ".cs");
				File.WriteAllText(file, TestScriptTemplate(name));
				Debug.Log("[Tests] Created " + AssetDatabase.ToAssetPath(file));
				ExternalScriptEditor.Open(file, 1);
			}
			catch (Exception e)
			{
				Debug.LogError("[Tests] Cannot create the test script: " + e.Message);
			}
		}

		/// <summary>The script "Create Test Script" writes (the project panel's too).</summary>
		public static string TestScriptTemplate(string className)
		{
			return "using System.Threading.Tasks;\nusing LELEngine;\nusing LELEngine.Testing;\n\n" +
				"public class " + className + "\n{\n" +
				"\t[Test]\n\tpublic void AddsUp()\n\t{\n\t\tAssert.AreEqual(4, 2 + 2);\n\t}\n\n" +
				"\t[TestCase(1, 2, ExpectedResult = 3)]\n\t[TestCase(-1, 1, ExpectedResult = 0)]\n\tpublic int Sum(int a, int b)\n\t{\n\t\treturn a + b;\n\t}\n\n" +
				"\t[Test]\n\tpublic async Task WaitsAFrame()\n\t{\n\t\tint frame = TestContext.FrameCount;\n\t\tawait TestContext.NextFrame();\n\t\tAssert.Greater(TestContext.FrameCount, frame);\n\t}\n}\n";
		}

		// ---------------------------------------------------------------- tree

		private void DrawTree(EditorTests tests)
		{
			var visible = new Dictionary<TestNode, bool>();
			if (!IsVisible(tests, tests.Root, visible))
			{
				ImGui.TextDisabled("No test matches the filters.");
				return;
			}

			// Follow the running test: scrolled to once when it starts.
			string current = tests.IsRunning ? tests.Run.Current?.FullName : null;
			if (current != null && current != followed)
			{
				followed = current;
				scrollTo = current;
			}

			ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new NVector2(ImGui.GetStyle().ItemSpacing.X, 1f));
			DrawNode(tests, tests.Root, 0, visible);
			ImGui.PopStyleVar();
		}

		/// <summary>The node passes the filters itself (a test) or has a test below it that does (memoized per frame).</summary>
		private bool IsVisible(EditorTests tests, TestNode node, Dictionary<TestNode, bool> visible)
		{
			if (visible.TryGetValue(node, out bool shown))
			{
				return shown;
			}

			if (node.IsTest)
			{
				TestOutcome outcome = tests.OutcomeOf(node);
				bool outcomeShown = outcome switch
				{
					TestOutcome.Passed => showPassed,
					TestOutcome.Failed => showFailed,
					TestOutcome.Skipped => showSkipped,
					TestOutcome.Inconclusive => showSkipped,
					TestOutcome.Running => true,
					_ => showNotRun
				};
				shown = outcomeShown && EditorTests.Matches(node, search.Trim(), category);
			}
			else
			{
				shown = false;
				foreach (TestNode child in node.Children)
				{
					shown |= IsVisible(tests, child, visible);
				}
			}

			visible[node] = shown;
			return shown;
		}

		private bool IsExpanded(TestNode node)
		{
			if (expanded.TryGetValue(node.FullName, out bool open))
			{
				return open;
			}

			// Searching opens everything that matches.
			return node.Kind != TestNodeKind.Method || search.Trim().Length > 0;
		}

		private void SetExpanded(TestNode node, bool open)
		{
			foreach (TestNode n in node.Descendants())
			{
				if (!n.IsTest)
				{
					expanded[n.FullName] = open;
				}
			}
		}

		private void DrawNode(EditorTests tests, TestNode node, int depth, Dictionary<TestNode, bool> visible)
		{
			if (!visible[node])
			{
				return;
			}

			bool hasChildren = node.Children.Count > 0;
			bool open = hasChildren && IsExpanded(node);
			float lineHeight = ImGui.GetTextLineHeightWithSpacing();
			float indent = depth * lineHeight * 0.9f;
			NVector2 rowStart = ImGui.GetCursorScreenPos();
			float width = ImGui.GetContentRegionAvail().X;
			bool isSelected = selected == node.FullName;
			TestOutcome outcome = tests.OutcomeOf(node);
			TestResult result = node.IsTest ? tests.ResultOf(node) : null;

			ImGui.PushID(node.FullName);
			// The row is a selectable named after the node (automation: "item:Name"); its own text is drawn below, indented.
			ImGui.PushStyleColor(ImGuiCol.Text, new NVector4(0f, 0f, 0f, 0f));
			bool clicked = EditorGui.Selectable(DisplayName(node) + "##row", isSelected, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new NVector2(width, lineHeight - 1f));
			ImGui.PopStyleColor();
			bool hovered = ImGui.IsItemHovered();
			if (scrollTo == node.FullName)
			{
				ImGui.SetScrollHereY(0.5f);
				scrollTo = null;
			}

			if (clicked)
			{
				// The arrow toggles only under the mouse (automation clicks the row: it selects).
				float arrowRight = rowStart.X + indent + lineHeight;
				bool onArrow = hasChildren && hovered && ImGui.GetMousePos().X < arrowRight;
				if (onArrow || hasChildren && EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
				{
					expanded[node.FullName] = !open;
				}

				if (!onArrow)
				{
					Select(node);
					if (node.IsTest && EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
					{
						OpenSource(node);
					}
				}
			}

			DrawRowMenu(tests, node, result);
			if (hovered)
			{
				string tip = node.FullName;
				if (node.Description != null) tip += "\n" + node.Description;
				if (node.Categories.Count > 0) tip += "\nCategories: " + string.Join(", ", node.Categories);
				if (node.IgnoreReason != null) tip += "\nIgnored: " + node.IgnoreReason;
				if (node.IsExplicit) tip += "\n[Explicit]: runs only when run itself";
				if (node.Error != null) tip += "\nCannot run: " + node.Error;
				if (node.IsTest) tip += "\nDouble-click: open the test in the script editor";
				ImGui.SetTooltip((tip.Length > 0 ? tip : node.Name).Replace("%", "%%"));
			}

			// Arrow, icon, name, time.
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			float textY = rowStart.Y + (lineHeight - 1f - ImGui.GetTextLineHeight()) * 0.5f;
			float x = rowStart.X + indent;
			if (hasChildren)
			{
				DrawArrow(draw, new NVector2(x + lineHeight * 0.5f, textY + ImGui.GetTextLineHeight() * 0.5f), lineHeight * 0.2f, open, ImGui.GetColorU32(ImGuiCol.TextDisabled));
			}

			x += lineHeight;
			StatusIcon(draw, new NVector2(x + lineHeight * 0.45f, textY + ImGui.GetTextLineHeight() * 0.5f), lineHeight * 0.32f, tests.IsQueued(node) ? TestOutcome.NotRun : outcome, tests.IsQueued(node));
			x += lineHeight;
			uint textColor = ImGui.GetColorU32(node.IgnoreReason != null || node.IsExplicit ? ImGuiCol.TextDisabled : ImGuiCol.Text);
			string name = DisplayName(node);
			if (node.Kind != TestNodeKind.Root && !node.IsTest)
			{
				int count = node.CollectTests().Count;
				name += "  (" + count + ")";
			}

			draw.AddText(new NVector2(x, textY), textColor, name);
			if (result != null && result.Outcome != TestOutcome.Running && result.Outcome != TestOutcome.NotRun)
			{
				string time = FormatSeconds(result.Seconds);
				float timeWidth = ImGui.CalcTextSize(time).X;
				draw.AddText(new NVector2(rowStart.X + width - timeWidth - 4f, textY), ImGui.GetColorU32(ImGuiCol.TextDisabled), time);
			}

			ImGui.PopID();
			if (open)
			{
				foreach (TestNode child in node.Children)
				{
					DrawNode(tests, child, depth + 1, visible);
				}
			}
		}

		private void DrawRowMenu(EditorTests tests, TestNode node, TestResult result)
		{
			if (!EditorGui.BeginPopupContextItem("##rowmenu"))
			{
				return;
			}

			Select(node);
			bool canRun = tests.CannotRunReason() == null;
			if (EditorMenu.Item("Run", "", false, canRun))
			{
				tests.Start(new[] { node });
			}

			if (EditorMenu.Item("Debug", "", false, canRun))
			{
				DebugRun(new[] { node });
			}

			ImGui.Separator();
			if (EditorMenu.Item("Open in Script Editor", "", false, SourceOf(node).file != null))
			{
				OpenSource(node);
			}

			if (EditorMenu.Item("Copy Full Name"))
			{
				ImGui.SetClipboardText(node.FullName);
			}

			if (EditorMenu.Item("Copy Result", "", false, result?.Message != null))
			{
				ImGui.SetClipboardText(node.FullName + ": " + result.Outcome + "\n" + result.Message + (result.StackTrace != null ? "\n" + result.StackTrace : ""));
			}

			ImGui.EndPopup();
		}

		private void DrawSplitter()
		{
			NVector2 start = ImGui.GetCursorScreenPos();
			float width = ImGui.GetContentRegionAvail().X;
			EditorGui.InvisibleButton("##splitter", new NVector2(width, SplitterHeight));
			bool active = ImGui.IsItemActive();
			if (ImGui.IsItemHovered() || active)
			{
				ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNs);
			}

			if (active)
			{
				detailsHeight -= ImGui.GetIO().MouseDelta.Y;
			}

			float y = start.Y + SplitterHeight * 0.5f;
			ImGui.GetWindowDrawList().AddLine(new NVector2(start.X + width * 0.45f, y), new NVector2(start.X + width * 0.55f, y), ImGui.GetColorU32(active ? ImGuiCol.SeparatorActive : ImGuiCol.Separator), 2f);
		}

		// ---------------------------------------------------------------- details

		private void DrawDetails(EditorTests tests)
		{
			TestNode node = SelectedNode(tests);
			if (node == null)
			{
				ImGui.TextDisabled("Select a test to see its result.");
				return;
			}

			tests.Results.TryGetValue(node.FullName, out TestResult result);
			TestOutcome outcome = tests.OutcomeOf(node);

			// Title: icon, name (bold), outcome and time.
			float lineHeight = ImGui.GetTextLineHeight();
			NVector2 pos = ImGui.GetCursorScreenPos();
			StatusIcon(ImGui.GetWindowDrawList(), pos + new NVector2(lineHeight * 0.5f, lineHeight * 0.5f), lineHeight * 0.4f, outcome, tests.IsQueued(node));
			ImGui.SetCursorScreenPos(pos + new NVector2(lineHeight * 1.4f, 0f));
			bool bold = EditorFonts.PushBold();
			ImGui.TextUnformatted(DisplayName(node));
			EditorFonts.Pop(bold);
			ImGui.SameLine();
			ImGui.TextColored(OutcomeColor(outcome), OutcomeText(outcome, tests.IsQueued(node)));
			if (node.IsTest && result != null && outcome != TestOutcome.Running && outcome != TestOutcome.NotRun)
			{
				ImGui.SameLine();
				ImGui.TextDisabled(FormatSeconds(result.Seconds) + (result.Frames > 0 ? $", {result.Frames} frame{(result.Frames == 1 ? "" : "s")}" : "") + ", " + result.Finished.ToString("HH:mm:ss"));
			}

			DimText(node.Kind == TestNodeKind.Root ? "Test assembly " + node.Name : node.FullName);
			if (node.Description != null)
			{
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(node.Description);
				ImGui.PopTextWrapPos();
			}

			if (node.Categories.Count > 0)
			{
				DimText("Categories: " + string.Join(", ", node.Categories));
			}

			// Actions.
			bool canRun = tests.CannotRunReason() == null;
			ImGui.BeginDisabled(!canRun);
			if (EditorGui.SmallButton("Run"))
			{
				tests.Start(new[] { node });
			}

			ImGui.SameLine();
			if (EditorGui.SmallButton("Debug"))
			{
				DebugRun(new[] { node });
			}

			ImGui.EndDisabled();
			(string file, int line) = SourceOf(node);
			if (file != null)
			{
				ImGui.SameLine();
				if (EditorGui.SmallButton("Open Source"))
				{
					OpenSource(node);
				}
			}

			ImGui.Separator();
			if (!node.IsTest)
			{
				DrawGroupDetails(tests, node, result);
				return;
			}

			if (node.IgnoreReason != null && (result == null || result.Outcome == TestOutcome.Skipped))
			{
				ColoredText(EditorStyle.Get(EditorColor.Warning), "Ignored: " + node.IgnoreReason);
			}

			if (result == null || outcome == TestOutcome.NotRun && result.Outcome == TestOutcome.Running)
			{
				ImGui.TextDisabled(tests.IsQueued(node) ? "Waiting for its turn in the run." : "Not run yet.");
				return;
			}

			DrawResult(result);
		}

		private void DrawGroupDetails(EditorTests tests, TestNode node, TestResult own)
		{
			List<TestNode> all = node.CollectTests();
			int passed = all.Count(t => tests.OutcomeOf(t) == TestOutcome.Passed);
			int failed = all.Count(t => tests.OutcomeOf(t) == TestOutcome.Failed);
			int skipped = all.Count(t => tests.OutcomeOf(t) == TestOutcome.Skipped || tests.OutcomeOf(t) == TestOutcome.Inconclusive);
			int notRun = all.Count - passed - failed - skipped;
			ImGui.TextUnformatted($"{all.Count} test{(all.Count == 1 ? "" : "s")}: {passed} passed, {failed} failed, {skipped} skipped, {notRun} not run");
			if (own != null && own.Outcome == TestOutcome.Failed)
			{
				ImGui.Spacing();
				DrawResult(own);
			}

			// The failures below, one line each (click selects).
			List<TestNode> failures = all.Where(t => tests.OutcomeOf(t) == TestOutcome.Failed).ToList();
			if (failures.Count > 0)
			{
				ImGui.Spacing();
				ImGui.TextDisabled("Failed:");
				foreach (TestNode test in failures)
				{
					TestResult result = tests.ResultOf(test);
					string first = (result?.Message ?? "").Split('\n')[0];
					ImGui.PushID(test.FullName);
					if (EditorGui.Selectable(test.Name + "##failed"))
					{
						Select(test);
						for (TestNode parent = test.Parent; parent != null; parent = parent.Parent)
						{
							expanded[parent.FullName] = true;
						}

						scrollTo = test.FullName;
					}

					ImGui.SameLine();
					ColoredText(EditorStyle.Get(EditorColor.Error), first);
					ImGui.PopID();
				}
			}
		}

		private void DrawResult(TestResult result)
		{
			if (!ReferenceEquals(framesOf, result))
			{
				framesOf = result;
				toggledFrames.Clear();
			}

			if (!string.IsNullOrEmpty(result.Message))
			{
				NVector4 color = result.Outcome == TestOutcome.Failed ? EditorStyle.Get(EditorColor.Error) : result.Outcome == TestOutcome.Passed ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : EditorStyle.Get(EditorColor.Warning);
				bool mono = EditorFonts.PushMono(EditorPreferences.Current.Theme.CodeFontSize);
				ImGui.PushStyleColor(ImGuiCol.Text, color);
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(result.Message);
				ImGui.PopTextWrapPos();
				ImGui.PopStyleColor();
				EditorFonts.Pop(mono);
				if (EditorGui.BeginPopupContextItem("##messagemenu"))
				{
					if (EditorMenu.Item("Copy Message"))
					{
						ImGui.SetClipboardText(result.Message);
					}

					ImGui.EndPopup();
				}
			}

			if (result.StackTrace != null)
			{
				ImGui.Spacing();
				DrawFrames(ConsoleStackTrace.Parse(result.StackTrace));
			}

			if (result.Output.Count > 0)
			{
				ImGui.Spacing();
				if (EditorGui.CollapsingHeader("Output (" + result.Output.Count + ")", ImGuiTreeNodeFlags.DefaultOpen))
				{
					DrawOutput(result);
				}
			}
		}

		/// <summary>The call stack: project frames bright with their code shown, the rest dim; click toggles the code, double click opens the line.</summary>
		private void DrawFrames(IReadOnlyList<ConsoleFrame> frames)
		{
			int source = ConsoleStackTrace.SourceFrame(frames);
			float lineHeight = ImGui.GetTextLineHeight();
			ImDrawListPtr draw = ImGui.GetWindowDrawList();
			for (int i = 0; i < frames.Count; i++)
			{
				ConsoleFrame frame = frames[i];
				if (frame.Kind == ConsoleFrameKind.Note)
				{
					ImGui.PushStyleColor(ImGuiCol.Text, EditorStyle.Dim);
					ImGui.PushTextWrapPos(0f);
					ImGui.TextUnformatted(frame.Method);
					ImGui.PopTextWrapPos();
					ImGui.PopStyleColor();
					continue;
				}

				if (frame.Kind == ConsoleFrameKind.Library)
				{
					continue;
				}

				bool bright = frame.Kind == ConsoleFrameKind.Project;
				bool expanded = frame.HasSource && (i == source) != toggledFrames.Contains(i);
				ImGui.PushID(i);
				NVector2 pos = ImGui.GetCursorScreenPos();
				float width = ImGui.GetContentRegionAvail().X;
				if (EditorGui.Selectable("##frame", false, ImGuiSelectableFlags.AllowDoubleClick | ImGuiSelectableFlags.AllowOverlap, new NVector2(width, lineHeight)))
				{
					if (frame.HasSource && !toggledFrames.Remove(i))
					{
						toggledFrames.Add(i);
					}

					if (EditorGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && frame.HasSource)
					{
						ExternalScriptEditor.Open(frame.File, frame.Line);
					}
				}

				if (TestAnchors.Enabled)
				{
					TestAnchors.Record("testframe:" + i, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
				}

				Tooltip(frame.Method + (frame.File != null ? "\n" + frame.File + ":" + frame.Line : "") + (frame.HasSource ? "\nClick: show the code, double-click: open in the script editor" : ""));
				if (frame.HasSource)
				{
					DrawArrow(draw, new NVector2(pos.X + lineHeight * 0.5f, pos.Y + lineHeight * 0.5f), lineHeight * 0.22f, expanded, ImGui.GetColorU32(ImGuiCol.TextDisabled));
				}

				string method = ConsoleStackTrace.ShortMethod(frame.Method);
				var textPos = new NVector2(pos.X + lineHeight, pos.Y);
				draw.AddText(textPos, ImGui.GetColorU32(bright ? ImGuiCol.Text : ImGuiCol.TextDisabled), method);
				if (frame.DisplayFile != null)
				{
					NVector4 link = frame.HasSource ? EditorStyle.Get(EditorColor.Link) : EditorStyle.Dim;
					draw.AddText(textPos + new NVector2(ImGui.CalcTextSize(method).X + 12f, 0f), ImGui.ColorConvertFloat4ToU32(bright ? link : link * new NVector4(1f, 1f, 1f, 0.6f)), frame.DisplayFile + ":" + frame.Line);
				}

				if (expanded)
				{
					SourceSnippet.Draw(frame.File, frame.Line, Math.Max(1, EditorPreferences.Current.Console.SourceContextLines), lineHeight);
				}

				ImGui.PopID();
			}
		}

		private static void DrawOutput(TestResult result)
		{
			bool mono = EditorFonts.PushMono(EditorPreferences.Current.Theme.CodeFontSize);
			for (int i = 0; i < result.Output.Count; i++)
			{
				TestLogEntry entry = result.Output[i];
				NVector4 color = entry.Type switch
				{
					LogType.Error or LogType.Exception => EditorStyle.Get(EditorColor.Error),
					LogType.Warning => EditorStyle.Get(EditorColor.Warning),
					_ => ImGui.GetStyle().Colors[(int)ImGuiCol.Text]
				};
				if (entry.Expected)
				{
					color *= new NVector4(1f, 1f, 1f, 0.55f);
				}

				string prefix = entry.IsOutput ? "" : "[" + entry.Type + "] ";
				string text = prefix + entry.Text.TrimEnd('\n') + (entry.Expected ? "   (expected)" : "");
				ImGui.PushID(i);
				ImGui.PushStyleColor(ImGuiCol.Text, color);
				ImGui.PushTextWrapPos(0f);
				ImGui.TextUnformatted(text);
				ImGui.PopTextWrapPos();
				ImGui.PopStyleColor();
				if (entry.StackTrace != null)
				{
					// The log call's place in the scripts: double click opens it.
					IReadOnlyList<ConsoleFrame> frames = ConsoleStackTrace.Parse(entry.StackTrace);
					int source = ConsoleStackTrace.SourceFrame(frames);
					if (source >= 0)
					{
						Tooltip(frames[source].Method + "\n" + frames[source].File + ":" + frames[source].Line + "\nDouble-click: open in the script editor");

						if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
						{
							ExternalScriptEditor.Open(frames[source].File, frames[source].Line);
						}
					}
				}

				ImGui.PopID();
			}

			EditorFonts.Pop(mono);
		}

		// ---------------------------------------------------------------- debugging

		/// <summary>Runs the nodes with a debugger: at once when one is attached, else after asking for one.</summary>
		private void DebugRun(IEnumerable<TestNode> nodes)
		{
			List<string> names = nodes.Where(n => n != null).Select(n => n.FullName).ToList();
			if (names.Count == 0)
			{
				return;
			}

			if (Debugger.IsAttached)
			{
				StartDebug(names);
				return;
			}

			pendingDebug = names;
			openDebugPopup = true;
		}

		private void StartDebug(List<string> names)
		{
			EditorTests tests = editor.Tests;
			if (tests.Root == null)
			{
				return;
			}

			List<TestNode> nodes = names.Select(n => n.Length == 0 ? tests.Root : tests.Root.Find(n)).Where(n => n != null).ToList();
			tests.Start(nodes, new TestRunOptions
			{
				BreakAtTestStart = true,
				BreakOnFailure = tests.Options.BreakOnFailure,
				StopOnFirstFailure = tests.Options.StopOnFirstFailure,
				FrameBudgetMilliseconds = tests.Options.FrameBudgetMilliseconds
			});
		}

		private void DrawDebugPopup()
		{
			const string popup = "Attach a Debugger##testdebug";
			if (openDebugPopup)
			{
				openDebugPopup = false;
				ImGui.OpenPopup(popup);
			}

			// Over the Test Runner (a modal centres on the main window otherwise).
			ImGui.SetNextWindowPos(ImGui.GetWindowPos() + ImGui.GetWindowSize() * 0.5f, ImGuiCond.Appearing, new NVector2(0.5f, 0.5f));
			if (!ImGui.BeginPopupModal(popup, ImGuiWindowFlags.AlwaysAutoResize))
			{
				return;
			}

			if (pendingDebug == null)
			{
				// Started (the debugger attached) or cancelled.
				ImGui.CloseCurrentPopup();
				ImGui.EndPopup();
				return;
			}

			ImGui.PushTextWrapPos(500f);
			ImGui.TextUnformatted($"Attach your IDE's debugger to {ProcessName} (process {Environment.ProcessId}); the tests start as soon as it is attached and stop right before each test method.");
			ImGui.TextDisabled("Rider: Run > Attach to Process. Visual Studio: Debug > Attach to Process (Managed / .NET Core). VS Code: the .NET \"attach\" configuration. " +
				"Breakpoints in the tests and the game scripts work (their symbols are loaded with them).");
			ImGui.PopTextWrapPos();
			ImGui.Spacing();
			ImGui.TextColored(EditorStyle.Accent, "Waiting for a debugger...");
			ImGui.Spacing();
			if (OperatingSystem.IsWindows() && EditorGui.Button("Launch Debugger"))
			{
				// The system's just-in-time debugger dialog (Visual Studio, Rider when registered).
				Debugger.Launch();
			}

			ImGui.SameLine();
			if (editor.Scripts != null && EditorGui.Button("Open Script Project"))
			{
				editor.Scripts.OpenInIde();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Run Without Debugger"))
			{
				List<string> names = pendingDebug;
				pendingDebug = null;
				EditorTests tests = editor.Tests;
				if (tests.Root != null)
				{
					tests.Start(names.Select(n => n.Length == 0 ? tests.Root : tests.Root.Find(n)).Where(n => n != null));
				}

				ImGui.CloseCurrentPopup();
			}

			ImGui.SameLine();
			if (EditorGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape))
			{
				pendingDebug = null;
				ImGui.CloseCurrentPopup();
			}

			ImGui.EndPopup();
		}

		// ---------------------------------------------------------------- helpers

		private TestNode SelectedNode(EditorTests tests)
		{
			if (tests.Root == null || selected == null)
			{
				return null;
			}

			return selected.Length == 0 ? tests.Root : tests.Root.Find(selected);
		}

		private void Select(TestNode node)
		{
			selected = node.FullName;
		}

		private static List<TestNode> FailedTests(EditorTests tests)
		{
			return tests.Root == null ? new List<TestNode>() : tests.Root.Descendants().Where(n => n.IsTest && tests.ResultOf(n)?.Outcome == TestOutcome.Failed).ToList();
		}

		private static string DisplayName(TestNode node)
		{
			return node.Name;
		}

		/// <summary>The node's source: a test's method, a group's first test.</summary>
		private static (string file, int line) SourceOf(TestNode node)
		{
			if (node.SourceFile != null)
			{
				return (node.SourceFile, node.SourceLine);
			}

			TestNode first = node.Descendants().FirstOrDefault(n => n.SourceFile != null);
			return first != null ? (first.SourceFile, first.SourceLine) : (null, 0);
		}

		private static void OpenSource(TestNode node)
		{
			(string file, int line) = SourceOf(node);
			if (file != null)
			{
				ExternalScriptEditor.Open(file, line);
			}
		}

		private static string FormatSeconds(double seconds)
		{
			return seconds < 1 ? (seconds * 1000).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " ms" : seconds.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " s";
		}

		private static string OutcomeText(TestOutcome outcome, bool queued)
		{
			return queued ? "Queued" : outcome switch
			{
				TestOutcome.Passed => "Passed",
				TestOutcome.Failed => "Failed",
				TestOutcome.Skipped => "Skipped",
				TestOutcome.Inconclusive => "Inconclusive",
				TestOutcome.Running => "Running",
				_ => "Not run"
			};
		}

		private static NVector4 OutcomeColor(TestOutcome outcome)
		{
			return outcome switch
			{
				TestOutcome.Passed => EditorStyle.Get(EditorColor.Added),
				TestOutcome.Failed => EditorStyle.Get(EditorColor.Error),
				TestOutcome.Skipped => EditorStyle.Get(EditorColor.Warning),
				TestOutcome.Inconclusive => EditorStyle.Get(EditorColor.Link),
				TestOutcome.Running => EditorStyle.Accent,
				_ => EditorStyle.Dim
			};
		}

		/// <summary>The outcome's icon: a check, a cross, a dash, a question mark in a disc; a ring for not run, a spinning arc while running.</summary>
		private static void StatusIcon(ImDrawListPtr draw, NVector2 center, float radius, TestOutcome outcome, bool queued)
		{
			uint color = ImGui.ColorConvertFloat4ToU32(OutcomeColor(outcome));
			uint mark = ImGui.ColorConvertFloat4ToU32(new NVector4(1f, 1f, 1f, 0.95f));
			float r = radius;
			switch (outcome)
			{
				case TestOutcome.Passed:
					draw.AddCircleFilled(center, r, color, 16);
					draw.AddLine(center + new NVector2(-r * 0.5f, 0f), center + new NVector2(-r * 0.12f, r * 0.4f), mark, Math.Max(1.5f, r * 0.25f));
					draw.AddLine(center + new NVector2(-r * 0.12f, r * 0.4f), center + new NVector2(r * 0.5f, -r * 0.4f), mark, Math.Max(1.5f, r * 0.25f));
					break;
				case TestOutcome.Failed:
					draw.AddCircleFilled(center, r, color, 16);
					draw.AddLine(center + new NVector2(-r * 0.4f, -r * 0.4f), center + new NVector2(r * 0.4f, r * 0.4f), mark, Math.Max(1.5f, r * 0.25f));
					draw.AddLine(center + new NVector2(-r * 0.4f, r * 0.4f), center + new NVector2(r * 0.4f, -r * 0.4f), mark, Math.Max(1.5f, r * 0.25f));
					break;
				case TestOutcome.Skipped:
					draw.AddCircleFilled(center, r, color, 16);
					draw.AddLine(center + new NVector2(-r * 0.45f, 0f), center + new NVector2(r * 0.45f, 0f), mark, Math.Max(1.5f, r * 0.28f));
					break;
				case TestOutcome.Inconclusive:
					draw.AddCircleFilled(center, r, color, 16);
					draw.AddCircleFilled(center, r * 0.25f, mark, 8);
					break;
				case TestOutcome.Running:
				{
					float start = (float)(ImGui.GetTime() * 6.0 % (Math.PI * 2));
					draw.PathArcTo(center, r * 0.85f, start, start + MathF.PI * 1.4f, 16);
					draw.PathStroke(color, ImDrawFlags.None, Math.Max(1.5f, r * 0.3f));
					break;
				}
				default:
					draw.AddCircle(center, r * 0.85f, ImGui.GetColorU32(ImGuiCol.TextDisabled, queued ? 1f : 0.7f), 16, 1.2f);
					if (queued)
					{
						draw.AddCircleFilled(center, r * 0.3f, ImGui.GetColorU32(ImGuiCol.TextDisabled), 8);
					}

					break;
			}
		}

		private static void DrawArrow(ImDrawListPtr draw, NVector2 center, float size, bool open, uint color)
		{
			if (open)
			{
				draw.AddTriangleFilled(center + new NVector2(-size, -size * 0.5f), center + new NVector2(size, -size * 0.5f), center + new NVector2(0f, size * 0.7f), color);
			}
			else
			{
				draw.AddTriangleFilled(center + new NVector2(-size * 0.5f, -size), center + new NVector2(-size * 0.5f, size), center + new NVector2(size * 0.7f, 0f), color);
			}
		}

		/// <summary>A tooltip for the last item (the text as it is: ImGui would read % as a format).</summary>
		private static void Tooltip(string text)
		{
			if (text != null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
			{
				ImGui.SetTooltip(text.Replace("%", "%%"));
			}
		}

		/// <summary>Text of the tests (names, messages) in a color, unformatted.</summary>
		private static void ColoredText(NVector4 color, string text)
		{
			ImGui.PushStyleColor(ImGuiCol.Text, color);
			ImGui.TextUnformatted(text);
			ImGui.PopStyleColor();
		}

		private static void DimText(string text)
		{
			ColoredText(ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled], text);
		}

		#endregion
	}
}
