using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LELEngine.Testing
{
	public enum TestOutcome
	{
		NotRun,
		Running,
		Passed,
		Failed,
		Skipped,
		Inconclusive
	}

	/// <summary>A line of a test's output: a log message while it ran, or <see cref="TestContext.WriteLine(object)" />.</summary>
	public sealed class TestLogEntry
	{
		public LogType Type;
		public string Text;
		public string StackTrace;

		/// <summary>Written with <see cref="TestContext.Write" /> (not a log message).</summary>
		public bool IsOutput;

		/// <summary>An error a <see cref="LogAssert.Expect(LogType, string)" /> expected.</summary>
		public bool Expected;
	}

	/// <summary>The outcome of a test (or a class's set up / tear down problem, under the class's full name).</summary>
	public sealed class TestResult
	{
		public string FullName;
		public TestOutcome Outcome;
		public double Seconds;

		/// <summary>Frames the test spanned (0 for a test that ran within one frame).</summary>
		public int Frames;

		public string Message;
		public string StackTrace;
		public readonly List<TestLogEntry> Output = new List<TestLogEntry>();
		public DateTime Finished;
	}

	public sealed class TestRunOptions
	{
		/// <summary>
		///     With a debugger attached: stop in the debugger where a test fails (the failing assert; the throw of an
		///     exception escaping synchronous test code; an unexpected error being logged).
		/// </summary>
		public bool BreakOnFailure;

		/// <summary>With a debugger attached: stop right before each test method is called (step into it from there).</summary>
		public bool BreakAtTestStart;

		/// <summary>How long a frame may run tests before the runner lets the editor draw (milliseconds).</summary>
		public double FrameBudgetMilliseconds = 12;

		public bool StopOnFirstFailure;
	}

	/// <summary>
	///     Runs tests on the calling (main) thread, a little per frame: the host calls <see cref="Tick" /> once per frame.
	///     Synchronous tests run back to back until the frame's budget is spent; async ones continue where they await in
	///     the same or a later frame (<see cref="TestContext.NextFrame" />, Task.Delay, loads): their continuations run
	///     inside <see cref="Tick" /> on the runner's synchronization context. One instance of a class runs its tests:
	///     [OneTimeSetUp], then per test [SetUp], the test, [TearDown], then [OneTimeTearDown]. Errors and exceptions
	///     logged while a test runs fail it unless expected (<see cref="LogAssert" />). One run at a time.
	/// </summary>
	public sealed class TestRun
	{
		#region PublicFields

		public IReadOnlyList<TestNode> Tests => tests;

		/// <summary>Results by full name, filled as tests finish (also the classes whose set up or tear down failed).</summary>
		public IReadOnlyDictionary<string, TestResult> Results => results;

		/// <summary>The test running now (null between tests).</summary>
		public TestNode Current { get; private set; }

		public int Completed { get; private set; }
		public int Passed { get; private set; }
		public int Failed { get; private set; }
		public int Skipped { get; private set; }
		public int Inconclusive { get; private set; }
		public bool IsDone { get; private set; }

		/// <summary><see cref="Stop" /> or <see cref="Abort" /> ended it before every test ran.</summary>
		public bool WasStopped { get; private set; }

		/// <summary>A stop was asked for: the run ends after the current test.</summary>
		public bool IsStopping => stopRequested && !IsDone;

		public double Seconds => clock.Elapsed.TotalSeconds;

		/// <summary>Ticks so far (frames).</summary>
		public int Frame { get; private set; }

		public TestRunOptions Options { get; }

		public event Action<TestNode> TestStarted;
		public event Action<TestNode, TestResult> TestFinished;

		#endregion

		#region InternalFields

		internal static TestExecution CurrentExecution => active?.execution;

		#endregion

		#region PrivateFields

		// The run executing now (static: awaits on other threads find it), cleared when it ends.
		private static TestRun active;

		private readonly List<TestNode> tests;
		private readonly Dictionary<string, TestResult> results = new Dictionary<string, TestResult>(StringComparer.Ordinal);
		private readonly RunContext context = new RunContext();
		private readonly Stopwatch clock = new Stopwatch();
		private Task task;
		private TestExecution execution;
		private bool stopRequested;
		private long tickStart;

		#endregion

		#region Constructors

		public TestRun(IEnumerable<TestNode> tests, TestRunOptions options = null)
		{
			this.tests = tests.Where(t => t.IsTest).Distinct().ToList();
			Options = options ?? new TestRunOptions();
		}

		#endregion

		#region PublicMethods

		/// <summary>Runs tests for this frame (main thread, once per frame, until <see cref="IsDone" />).</summary>
		public void Tick()
		{
			if (IsDone)
			{
				return;
			}

			if (active != null && active != this)
			{
				throw new InvalidOperationException("Another test run is in progress.");
			}

			active = this;
			Frame++;
			tickStart = Stopwatch.GetTimestamp();
			SynchronizationContext previous = SynchronizationContext.Current;
			SynchronizationContext.SetSynchronizationContext(context);
			try
			{
				if (task == null)
				{
					clock.Start();
					Debug.MessageLogged += OnMessageLogged;
					task = RunAllAsync();
				}
				else
				{
					context.BeginFrame();
				}

				context.RunReady(IsOverBudget);
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previous);
			}

			if (task.IsCompleted)
			{
				if (task.IsFaulted)
				{
					Debug.LogException(task.Exception?.GetBaseException());
				}

				End();
			}
		}

		/// <summary>Ends the run after the current test (its tear downs and its class's [OneTimeTearDown] still run).</summary>
		public void Stop()
		{
			stopRequested = true;
		}

		/// <summary>
		///     Ends the run now, wherever its tests are waiting (no tear down runs; what they awaited never continues). For
		///     a stuck test, and before the test assembly unloads.
		/// </summary>
		public void Abort()
		{
			if (IsDone)
			{
				return;
			}

			stopRequested = true;
			if (Current != null && results.TryGetValue(Current.FullName, out TestResult result) && result.Outcome == TestOutcome.Running)
			{
				result.Outcome = TestOutcome.NotRun;
				result.Message = "The run was aborted while this test ran.";
				result.Finished = DateTime.Now;
				execution?.Cleanup();
			}

			End();
		}

		#endregion

		#region InternalMethods

		internal static TestRun RequireActive()
		{
			return active ?? throw new InvalidOperationException("No test is running (TestContext waits work inside tests the runner runs).");
		}

		internal static TestExecution RequireExecution()
		{
			return CurrentExecution ?? throw new InvalidOperationException("No test is running.");
		}

		internal void ScheduleNextFrame(Action continuation)
		{
			context.ScheduleNextFrame(continuation);
		}

		/// <summary>An assert is about to fail: Break on Failure stops the debugger here (the test's frame is just below).</summary>
		internal static void OnAssertionFailing(string message)
		{
			TestRun run = active;
			if (run != null && run.Options.BreakOnFailure && Debugger.IsAttached)
			{
				// The failing assert is one frame down the call stack: step out (Shift+F11) to reach the test.
				Debugger.Break();
			}
		}

		#endregion

		#region PrivateMethods

		private bool IsOverBudget()
		{
			return Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds > Options.FrameBudgetMilliseconds;
		}

		private void End()
		{
			bool unfinished = Completed < tests.Count;
			IsDone = true;
			WasStopped = stopRequested && unfinished;
			Current = null;
			execution = null;
			clock.Stop();
			context.Close();
			Debug.MessageLogged -= OnMessageLogged;
			task = null;
			if (active == this)
			{
				active = null;
			}
		}

		private async Task RunAllAsync()
		{
			int index = 0;
			while (index < tests.Count && !stopRequested)
			{
				// Consecutive tests of one class share its instance and one-time set up.
				TestNode fixture = tests[index].Fixture;
				int end = index;
				while (end < tests.Count && tests[end].Fixture == fixture)
				{
					end++;
				}

				await RunFixtureAsync(fixture, tests.GetRange(index, end - index));
				index = end;
			}
		}

		private async Task RunFixtureAsync(TestNode fixture, List<TestNode> fixtureTests)
		{
			object instance = null;
			bool created = false;
			string failure = null;
			string failureStack = null;
			TestOutcome failureOutcome = TestOutcome.Failed;
			bool anyRunnable = fixtureTests.Any(t => t.IgnoreReason == null && t.Error == null);
			if (anyRunnable && fixture.Error == null)
			{
				try
				{
					instance = fixture.FixtureType.IsAbstract ? null : Activator.CreateInstance(fixture.FixtureType, true);
					created = true;
				}
				catch (Exception e)
				{
					Exception cause = Unwrap(e);
					failure = $"Cannot create {fixture.Name}: {cause.GetType().Name}: {cause.Message}";
					failureStack = FilterStack(cause);
				}

				foreach (MethodInfo method in failure == null ? fixture.OneTimeSetUps : new List<MethodInfo>())
				{
					try
					{
						await InvokeAsync(method, instance, null, fixture.TimeoutMilliseconds);
					}
					catch (Exception e)
					{
						Exception cause = Unwrap(e);
						failureOutcome = cause is IgnoreException ? TestOutcome.Skipped : cause is InconclusiveException ? TestOutcome.Inconclusive : TestOutcome.Failed;
						failure = failureOutcome == TestOutcome.Failed ? $"[OneTimeSetUp] {method.Name} failed: {Describe(cause)}" : cause.Message;
						failureStack = FilterStack(cause);
						break;
					}
				}
			}

			foreach (TestNode test in fixtureTests)
			{
				if (stopRequested)
				{
					break;
				}

				if (failure != null && test.IgnoreReason == null && test.Error == null)
				{
					Record(test, new TestResult { FullName = test.FullName, Outcome = failureOutcome, Message = failure, StackTrace = failureStack, Finished = DateTime.Now });
				}
				else
				{
					await RunTestAsync(test, instance);
				}

				if (Options.StopOnFirstFailure && results.TryGetValue(test.FullName, out TestResult result) && result.Outcome == TestOutcome.Failed)
				{
					stopRequested = true;
				}

				if (IsOverBudget())
				{
					// Let the editor draw a frame.
					await new FrameAwaitable(this);
				}
			}

			// [OneTimeTearDown] runs when the instance was made, also after a failed [OneTimeSetUp] (as NUnit).
			if (!created)
			{
				return;
			}

			var problems = new StringBuilder();
			foreach (MethodInfo method in fixture.OneTimeTearDowns)
			{
				try
				{
					await InvokeAsync(method, instance, null, fixture.TimeoutMilliseconds);
				}
				catch (Exception e)
				{
					problems.Append($"[OneTimeTearDown] {method.Name} failed: {Describe(Unwrap(e))}\n");
				}
			}

			if (instance is IDisposable disposable)
			{
				try
				{
					disposable.Dispose();
				}
				catch (Exception e)
				{
					problems.Append($"{fixture.Name}.Dispose failed: {Describe(e)}\n");
				}
			}

			if (problems.Length > 0)
			{
				results[fixture.FullName] = new TestResult { FullName = fixture.FullName, Outcome = TestOutcome.Failed, Message = problems.ToString().TrimEnd(), Finished = DateTime.Now };
			}
		}

		private async Task RunTestAsync(TestNode test, object instance)
		{
			var result = new TestResult { FullName = test.FullName, Outcome = TestOutcome.Running };
			results[test.FullName] = result;
			Current = test;
			execution = new TestExecution(this, test, result);
			TestStarted?.Invoke(test);
			long start = Stopwatch.GetTimestamp();
			if (test.IgnoreReason != null)
			{
				result.Outcome = TestOutcome.Skipped;
				result.Message = test.IgnoreReason;
			}
			else if (test.Error != null)
			{
				result.Outcome = TestOutcome.Failed;
				result.Message = test.Error;
			}
			else
			{
				Exception failure = null;
				TestNode fixture = test.Fixture;
				try
				{
					foreach (MethodInfo method in fixture.SetUps)
					{
						await InvokeAsync(method, instance, null, test.TimeoutMilliseconds);
					}

					if (Options.BreakAtTestStart && Debugger.IsAttached)
					{
						// The test method is the next call (InvokeAsync -> MethodInfo.Invoke): step into it (F11), or set a
						// breakpoint in it and continue.
						Debugger.Break();
					}

					object value = await InvokeAsync(test.Method, instance, test.Arguments, test.TimeoutMilliseconds);
					if (test.HasExpectedResult)
					{
						Assert.AreEqual(test.ExpectedResult, value, "The test's return value is not its ExpectedResult.");
					}
				}
				catch (Exception e)
				{
					failure = Unwrap(e);
				}

				var tearDownProblems = new StringBuilder();
				foreach (MethodInfo method in fixture.TearDowns)
				{
					try
					{
						await InvokeAsync(method, instance, null, test.TimeoutMilliseconds);
					}
					catch (Exception e)
					{
						tearDownProblems.Append($"[TearDown] {method.Name} failed: {Describe(Unwrap(e))}\n");
					}
				}

				Classify(result, failure, tearDownProblems.ToString().TrimEnd(), execution.MissingExpectations(), execution.TakeUnexpected());
			}

			result.Seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
			result.Frames = Frame - execution.StartFrame;
			result.Finished = DateTime.Now;
			execution.Cleanup();
			execution = null;
			Current = null;
			Record(test, result);
		}

		private void Record(TestNode test, TestResult result)
		{
			results[test.FullName] = result;
			Completed++;
			switch (result.Outcome)
			{
				case TestOutcome.Passed:
					Passed++;
					break;
				case TestOutcome.Failed:
					Failed++;
					break;
				case TestOutcome.Skipped:
					Skipped++;
					break;
				case TestOutcome.Inconclusive:
					Inconclusive++;
					break;
			}

			TestFinished?.Invoke(test, result);
		}

		private static void Classify(TestResult result, Exception failure, string tearDown, string missing, string unexpected)
		{
			switch (failure)
			{
				case null:
				case SuccessException _:
					result.Outcome = TestOutcome.Passed;
					result.Message = failure?.Message is { Length: > 0 } passMessage ? passMessage : null;
					break;
				case IgnoreException ignore:
					result.Outcome = TestOutcome.Skipped;
					result.Message = ignore.Message;
					break;
				case InconclusiveException inconclusive:
					result.Outcome = TestOutcome.Inconclusive;
					result.Message = inconclusive.Message;
					break;
				case AssertionException assertion:
					result.Outcome = TestOutcome.Failed;
					result.Message = assertion.Message;
					result.StackTrace = FilterStack(assertion);
					break;
				case TestTimeoutException timeout:
					result.Outcome = TestOutcome.Failed;
					result.Message = timeout.Message;
					break;
				default:
					result.Outcome = TestOutcome.Failed;
					result.Message = Describe(failure);
					result.StackTrace = FilterStack(failure);
					break;
			}

			// Log and tear down problems fail a test that would pass, and are added to any other failure.
			foreach (string problem in new[] { tearDown, unexpected, missing })
			{
				if (string.IsNullOrEmpty(problem) || result.Outcome == TestOutcome.Skipped)
				{
					continue;
				}

				if (result.Outcome != TestOutcome.Failed)
				{
					result.Outcome = TestOutcome.Failed;
					result.Message = problem;
				}
				else
				{
					result.Message += "\n" + problem;
				}
			}
		}

		/// <summary>
		///     Calls a test, set up or tear down method and waits for it when it returns a task (Task, Task&lt;T&gt;,
		///     ValueTask, ValueTask&lt;T&gt;), at most <paramref name="timeoutMilliseconds" />; returns its value.
		/// </summary>
		private async Task<object> InvokeAsync(MethodInfo method, object instance, object[] arguments, int timeoutMilliseconds)
		{
			object value;
			try
			{
				value = method.Invoke(method.IsStatic ? null : instance, BindingFlags.DoNotWrapExceptions, null, arguments, CultureInfo.InvariantCulture);
			}
			catch (Exception e) when (BreakOnException(e))
			{
				// Never taken: the filter stops the debugger while the throwing frame is still on the stack.
				throw;
			}

			Task awaited = AsTask(value, method.ReturnType);
			if (awaited == null)
			{
				return value;
			}

			if (!awaited.IsCompleted && timeoutMilliseconds > 0)
			{
				using var cancel = new CancellationTokenSource();
				Task first = await Task.WhenAny(awaited, Task.Delay(timeoutMilliseconds, cancel.Token));
				cancel.Cancel();
				if (first != awaited)
				{
					throw new TestTimeoutException($"{method.Name} did not finish within its Timeout of {timeoutMilliseconds} ms.");
				}
			}

			await awaited;
			Type returnType = method.ReturnType;
			bool hasValue = returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>));
			return hasValue ? awaited.GetType().GetProperty("Result")?.GetValue(awaited) : null;
		}

		private bool BreakOnException(Exception e)
		{
			if (Options.BreakOnFailure && Debugger.IsAttached && !(e is AssertionException || e is IgnoreException || e is InconclusiveException || e is SuccessException))
			{
				// The exception escaping the test: the call stack still shows where it was thrown.
				Debugger.Break();
			}

			return false;
		}

		private static Task AsTask(object value, Type returnType)
		{
			switch (value)
			{
				case null:
					return null;
				case Task task:
					return task;
				case ValueTask valueTask:
					return valueTask.AsTask();
			}

			if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
			{
				return (Task)returnType.GetMethod("AsTask")?.Invoke(value, null);
			}

			return null;
		}

		private void OnMessageLogged(string message, string stackTrace, LogType type, object logContext)
		{
			execution?.OnLog(message, stackTrace, type);
		}

		private static Exception Unwrap(Exception e)
		{
			while ((e is TargetInvocationException || e is AggregateException && ((AggregateException)e).InnerExceptions.Count == 1) && e.InnerException != null)
			{
				e = e.InnerException;
			}

			return e;
		}

		private static string Describe(Exception e)
		{
			string text = e.GetType().Name + ": " + e.Message;
			for (Exception cause = e.InnerException; cause != null; cause = cause.InnerException)
			{
				text += "\n--- Caused by " + cause.GetType().Name + ": " + cause.Message;
			}

			return text;
		}

		/// <summary>The exception's call stack without the runner's and the asserts' own frames (and their causes').</summary>
		private static string FilterStack(Exception e)
		{
			var builder = new StringBuilder();
			for (Exception current = e; current != null; current = current.InnerException)
			{
				if (current != e)
				{
					builder.Append("--- Caused by ").Append(current.GetType().Name).Append(": ").Append(current.Message).Append('\n');
				}

				foreach (string line in (current.StackTrace ?? "").Split('\n'))
				{
					string text = line.TrimEnd('\r');
					string trimmed = text.TrimStart();
					if (trimmed.Length == 0 || trimmed.StartsWith("at LELEngine.Testing.", StringComparison.Ordinal) || trimmed.StartsWith("at System.Runtime.", StringComparison.Ordinal)
						|| trimmed.StartsWith("at System.Reflection.", StringComparison.Ordinal) || trimmed.StartsWith("at System.Threading.", StringComparison.Ordinal)
						|| trimmed.StartsWith("--- End of stack trace", StringComparison.Ordinal))
					{
						continue;
					}

					builder.Append(text).Append('\n');
				}
			}

			return builder.Length > 0 ? builder.ToString().TrimEnd() : null;
		}

		#endregion

		#region NestedTypes

		/// <summary>
		///     Where the run's code continues: posted continuations run in the current tick (while its budget lasts), the
		///     next-frame ones in the next tick. Closed when the run ends: what still comes is dropped.
		/// </summary>
		private sealed class RunContext : SynchronizationContext
		{
			private readonly ConcurrentQueue<(SendOrPostCallback callback, object state)> ready = new ConcurrentQueue<(SendOrPostCallback, object)>();
			private readonly ConcurrentQueue<Action> nextFrame = new ConcurrentQueue<Action>();
			private volatile bool closed;

			public override void Post(SendOrPostCallback callback, object state)
			{
				if (!closed)
				{
					ready.Enqueue((callback, state));
				}
			}

			public override void Send(SendOrPostCallback callback, object state)
			{
				callback(state);
			}

			public override SynchronizationContext CreateCopy()
			{
				return this;
			}

			public void ScheduleNextFrame(Action continuation)
			{
				if (!closed)
				{
					nextFrame.Enqueue(continuation);
				}
			}

			/// <summary>The continuations waiting for a new frame become ready (those scheduled from now on wait for the next).</summary>
			public void BeginFrame()
			{
				int count = nextFrame.Count;
				for (int i = 0; i < count && nextFrame.TryDequeue(out Action continuation); i++)
				{
					ready.Enqueue((state => ((Action)state)(), continuation));
				}
			}

			public void RunReady(Func<bool> overBudget)
			{
				while (!closed && !overBudget() && ready.TryDequeue(out (SendOrPostCallback callback, object state) item))
				{
					try
					{
						item.callback(item.state);
					}
					catch (Exception e)
					{
						Debug.LogException(e);
					}
				}
			}

			public void Close()
			{
				closed = true;
				ready.Clear();
				nextFrame.Clear();
			}
		}

		#endregion
	}

	/// <summary>An async step ran past its [Timeout].</summary>
	internal sealed class TestTimeoutException : Exception
	{
		public TestTimeoutException(string message)
			: base(message)
		{
		}
	}

	/// <summary>One test while it runs: its output, the log messages it expects and the unexpected ones it got.</summary>
	internal sealed class TestExecution
	{
		private readonly object sync = new object();
		private readonly List<(LogType type, string text, Regex pattern)> expected = new List<(LogType, string, Regex)>();
		private readonly List<string> unexpected = new List<string>();
		private string temporaryDirectory;

		public TestExecution(TestRun run, TestNode node, TestResult result)
		{
			Run = run;
			Result = result;
			StartFrame = run.Frame;
			Info = new TestInfo
			{
				Name = node.Name,
				FullName = node.FullName,
				ClassName = node.FixtureType?.FullName,
				MethodName = node.Method?.Name,
				Arguments = node.Arguments,
				Categories = node.Categories,
				Description = node.Description
			};
		}

		public TestRun Run { get; }
		public TestResult Result { get; }
		public TestInfo Info { get; }
		public int StartFrame { get; }
		public bool IgnoreFailingMessages { get; set; }

		public string TemporaryDirectory
		{
			get
			{
				if (temporaryDirectory == null)
				{
					temporaryDirectory = Path.Combine(Path.GetTempPath(), "LELEngine", "Tests", Guid.NewGuid().ToString("N"));
					Directory.CreateDirectory(temporaryDirectory);
				}

				return temporaryDirectory;
			}
		}

		public void Expect(LogType type, string text, Regex pattern)
		{
			lock (sync)
			{
				expected.Add((type, text, pattern));
			}
		}

		public void WriteOutput(string text)
		{
			lock (sync)
			{
				// Consecutive writes without a newline join one entry.
				TestLogEntry last = Result.Output.Count > 0 ? Result.Output[^1] : null;
				if (last != null && last.IsOutput && !last.Text.EndsWith("\n", StringComparison.Ordinal))
				{
					last.Text += text;
				}
				else
				{
					Result.Output.Add(new TestLogEntry { Type = LogType.Log, Text = text, IsOutput = true });
				}
			}
		}

		public void OnLog(string message, string stackTrace, LogType type)
		{
			bool breakHere = false;
			lock (sync)
			{
				var entry = new TestLogEntry { Type = type, Text = message ?? "Null", StackTrace = stackTrace };
				Result.Output.Add(entry);
				int match = expected.FindIndex(e => e.type == type && (e.pattern != null ? e.pattern.IsMatch(entry.Text) : entry.Text == e.text));
				if (match >= 0)
				{
					expected.RemoveAt(match);
					entry.Expected = true;
				}
				else if ((type == LogType.Error || type == LogType.Exception) && !IgnoreFailingMessages)
				{
					unexpected.Add($"Unhandled log message: [{type}] {entry.Text}. Use LogAssert.Expect if it is expected.");
					breakHere = Run.Options.BreakOnFailure;
				}
			}

			if (breakHere && Debugger.IsAttached)
			{
				// What logged the error is down the call stack.
				Debugger.Break();
			}
		}

		/// <summary>The unexpected errors so far as one message (null: none); they are reported once.</summary>
		public string TakeUnexpected()
		{
			lock (sync)
			{
				if (unexpected.Count == 0)
				{
					return null;
				}

				string text = string.Join("\n", unexpected);
				unexpected.Clear();
				return text;
			}
		}

		public string MissingExpectations()
		{
			lock (sync)
			{
				return expected.Count == 0 ? null : string.Join("\n", expected.Select(e => $"Expected log message did not appear: [{e.type}] {(e.pattern != null ? "Regex " + e.pattern : e.text)}"));
			}
		}

		public void Cleanup()
		{
			if (temporaryDirectory == null)
			{
				return;
			}

			try
			{
				Directory.Delete(temporaryDirectory, true);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				// A file still open: it stays in the temp folder.
			}

			temporaryDirectory = null;
		}
	}
}
