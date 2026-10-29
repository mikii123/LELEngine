using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using LELEngine.Testing;

namespace LELEngine.Editor
{
	/// <summary>
	///     The editor's side of the project's unit tests (scripts in Tests folders, Game.Tests.dll): the tests found in
	///     the loaded assembly (<see cref="Root" />), the last result of every test by its full name (kept across script
	///     reloads and runs), and the run in progress, ticked once per editor frame in edit mode (play mode aborts it).
	///     <see cref="ScriptDomain" /> hands the assembly over after every load and takes every test type back before it
	///     unloads (<see cref="OnTestsUnloading" />). The Test Runner window and batch mode (-runTests) drive it.
	/// </summary>
	internal sealed class EditorTests
	{
		#region PublicFields

		/// <summary>The tests of the loaded assembly (null: none loaded).</summary>
		public TestNode Root { get; private set; }

		/// <summary>Bumped when the tests are loaded or unloaded and when a run starts or ends (views rebuild).</summary>
		public int Version { get; private set; }

		/// <summary>The current or last run (null after a reload).</summary>
		public TestRun Run { get; private set; }

		public bool IsRunning => Run != null && !Run.IsDone;

		/// <summary>Last result per test (and per class whose set up or tear down failed), by full name.</summary>
		public IReadOnlyDictionary<string, TestResult> Results => results;

		/// <summary>"12 passed, 1 failed in 0.40 s" of the last finished run, or null.</summary>
		public string LastSummary { get; private set; }

		/// <summary>A test (or a class's set up / tear down) failed in the last finished run.</summary>
		public bool LastRunFailed { get; private set; }

		public readonly TestRunOptions Options = new TestRunOptions { BreakOnFailure = true };

		/// <summary>A run ended (finished, stopped or aborted).</summary>
		public event Action<TestRun> RunFinished;

		#endregion

		#region PrivateFields

		private readonly EditorApplication editor;
		private readonly Dictionary<string, TestResult> results = new Dictionary<string, TestResult>(StringComparer.Ordinal);

		// The current run's tests (queued ones show as such).
		private readonly HashSet<TestNode> runTests = new HashSet<TestNode>();

		#endregion

		#region Constructors

		public EditorTests(EditorApplication editor)
		{
			this.editor = editor;
		}

		#endregion

		#region PublicMethods

		/// <summary>The test assembly loaded (null: none, or it does not compile).</summary>
		public void OnTestsLoaded(Assembly assembly, byte[] symbols)
		{
			Root = null;
			if (assembly != null)
			{
				try
				{
					Root = TestDiscovery.Discover(assembly, symbols);
				}
				catch (Exception e)
				{
					Debug.LogError("[Tests] Cannot read the tests: " + e.Message);
				}
			}

			Version++;
		}

		/// <summary>The test assembly is about to unload: a run in progress is aborted, every node goes.</summary>
		public void OnTestsUnloading()
		{
			if (IsRunning)
			{
				Run.Abort();
				Debug.LogWarning("[Tests] The test run was aborted: the scripts reload.");
				Finish();
			}

			Run = null;
			Root = null;
			runTests.Clear();
			Version++;
		}

		/// <summary>The project closed: results go too.</summary>
		public void Reset()
		{
			OnTestsUnloading();
			results.Clear();
			LastSummary = null;
			LastRunFailed = false;
		}

		/// <summary>Why tests cannot run now (null: they can).</summary>
		public string CannotRunReason()
		{
			if (editor.Project == null)
			{
				return "No project is open.";
			}

			if (editor.PlayState != PlayState.Edit)
			{
				return "Tests run in edit mode: stop the game first.";
			}

			if (IsRunning)
			{
				return "Tests are running.";
			}

			if (editor.Scripts != null && editor.Scripts.IsBuilding)
			{
				return "Scripts are compiling.";
			}

			if (Root == null)
			{
				return editor.Scripts != null && editor.Scripts.TestDiagnostics.Any(d => d.IsError) ? "The tests do not compile." : "No tests are loaded.";
			}

			return null;
		}

		/// <summary>Starts running the tests (each node's tests: <see cref="TestNode.CollectTests" />); false when it cannot.</summary>
		public bool Start(IEnumerable<TestNode> nodes, TestRunOptions options = null)
		{
			string reason = CannotRunReason();
			if (reason != null)
			{
				Debug.LogWarning("[Tests] " + reason);
				return false;
			}

			// In tree order, each test once (a class and one of its tests both chosen).
			var chosen = new HashSet<TestNode>();
			foreach (TestNode node in nodes)
			{
				chosen.UnionWith(node.CollectTests());
			}

			List<TestNode> tests = Root.Descendants().Where(chosen.Contains).ToList();
			if (tests.Count == 0)
			{
				return false;
			}

			TestRunOptions settings = options ?? Options;
			Run = new TestRun(tests, new TestRunOptions
			{
				BreakOnFailure = settings.BreakOnFailure,
				BreakAtTestStart = settings.BreakAtTestStart,
				StopOnFirstFailure = settings.StopOnFirstFailure,
				FrameBudgetMilliseconds = editor.HasUI ? settings.FrameBudgetMilliseconds : 250
			});
			Run.TestFinished += (node, result) => results[node.FullName] = result;
			runTests.Clear();
			runTests.UnionWith(tests);
			LastSummary = null;
			Version++;
			Debug.Log($"[Tests] Running {tests.Count} test{(tests.Count == 1 ? "" : "s")}" + (Run.Options.BreakAtTestStart || Run.Options.BreakOnFailure && System.Diagnostics.Debugger.IsAttached ? " (debugger attached)" : ""));
			return true;
		}

		/// <summary>Called every editor frame (after the scene's update): advances the run.</summary>
		public void Update()
		{
			if (!IsRunning)
			{
				return;
			}

			if (editor.PlayState != PlayState.Edit)
			{
				Run.Abort();
				Debug.LogWarning("[Tests] The test run was aborted: play mode started.");
			}
			else
			{
				Run.Tick();
			}

			if (Run.IsDone)
			{
				Finish();
			}
		}

		/// <summary>The run ends after the current test.</summary>
		public void Stop()
		{
			Run?.Stop();
		}

		/// <summary>The run ends now (a test stuck waiting).</summary>
		public void Abort()
		{
			if (IsRunning)
			{
				Run.Abort();
				Finish();
			}
		}

		/// <summary>The test's last result (null: never run).</summary>
		public TestResult ResultOf(TestNode node)
		{
			return results.TryGetValue(node.FullName, out TestResult result) ? result : null;
		}

		/// <summary>
		///     The outcome a node shows: a test its result (Running while it runs, NotRun while queued); a group the worst
		///     of its tests (failed &gt; inconclusive &gt; passed with skips &gt; passed), Running while any runs.
		/// </summary>
		public TestOutcome OutcomeOf(TestNode node)
		{
			if (node.IsTest)
			{
				if (IsRunning && Run.Current == node)
				{
					return TestOutcome.Running;
				}

				TestResult result = ResultOf(node);
				return result == null || IsQueued(node) ? TestOutcome.NotRun : result.Outcome;
			}

			bool any = false;
			bool failed = false;
			bool inconclusive = false;
			bool passed = false;
			bool skipped = false;
			bool notRun = false;
			if (results.TryGetValue(node.FullName, out TestResult own) && own.Outcome == TestOutcome.Failed)
			{
				failed = true;
			}

			foreach (TestNode child in node.Children)
			{
				TestOutcome outcome = OutcomeOf(child);
				any = true;
				switch (outcome)
				{
					case TestOutcome.Running:
						return TestOutcome.Running;
					case TestOutcome.Failed:
						failed = true;
						break;
					case TestOutcome.Inconclusive:
						inconclusive = true;
						break;
					case TestOutcome.Passed:
						passed = true;
						break;
					case TestOutcome.Skipped:
						skipped = true;
						break;
					default:
						notRun = true;
						break;
				}
			}

			if (!any)
			{
				return failed ? TestOutcome.Failed : TestOutcome.NotRun;
			}

			return failed ? TestOutcome.Failed : inconclusive ? TestOutcome.Inconclusive : passed ? TestOutcome.Passed : skipped && !notRun ? TestOutcome.Skipped : TestOutcome.NotRun;
		}

		/// <summary>The test waits its turn in the current run.</summary>
		public bool IsQueued(TestNode node)
		{
			return IsRunning && Run.Current != node && !Run.Results.ContainsKey(node.FullName) && runTests.Contains(node);
		}

		/// <summary>
		///     Tests whose full name contains <paramref name="filter" /> (case-insensitive; null: all) and that have
		///     <paramref name="category" /> (null: any), as runnable roots (batch mode). [Explicit] tests only when the
		///     filter is their full name.
		/// </summary>
		public List<TestNode> Find(string filter, string category)
		{
			if (Root == null)
			{
				return new List<TestNode>();
			}

			List<TestNode> tests = Root.CollectTests().Where(t => Matches(t, filter, category)).ToList();
			TestNode named = string.IsNullOrEmpty(filter) ? null : Root.Find(filter.Trim());
			if (named != null && named.Kind != TestNodeKind.Root)
			{
				foreach (TestNode test in named.CollectTests())
				{
					if (!tests.Contains(test) && Matches(test, null, category))
					{
						tests.Add(test);
					}
				}
			}

			return tests;
		}

		public static bool Matches(TestNode test, string filter, string category)
		{
			return (string.IsNullOrEmpty(filter) || test.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase))
				&& (string.IsNullOrEmpty(category) || test.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)));
		}

		#endregion

		#region PrivateMethods

		private void Finish()
		{
			TestRun run = Run;
			foreach (KeyValuePair<string, TestResult> result in run.Results)
			{
				results[result.Key] = result.Value;
			}

			runTests.Clear();
			LastSummary = TestReport.Summary(run) + (run.WasStopped ? " (stopped)" : "");
			LastRunFailed = run.Results.Values.Any(r => r.Outcome == TestOutcome.Failed);
			Version++;
			string message = "[Tests] " + LastSummary;
			if (run.Failed > 0)
			{
				Debug.LogWarning(message + ": " + string.Join(", ", run.Tests.Where(t => run.Results.TryGetValue(t.FullName, out TestResult r) && r.Outcome == TestOutcome.Failed).Take(5).Select(t => t.Name)) + (run.Failed > 5 ? ", ..." : ""));
			}
			else
			{
				Debug.Log(message);
			}

			RunFinished?.Invoke(run);
		}

		#endregion
	}
}
