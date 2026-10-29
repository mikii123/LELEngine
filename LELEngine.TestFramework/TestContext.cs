using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LELEngine.Testing
{
	/// <summary>The running test as <see cref="TestContext.CurrentTest" /> describes it.</summary>
	public sealed class TestInfo
	{
		/// <summary>"Method" or "Method(1, 2)" (a case).</summary>
		public string Name { get; internal set; }

		/// <summary>"Namespace.Class.Method(1, 2)".</summary>
		public string FullName { get; internal set; }

		public string ClassName { get; internal set; }
		public string MethodName { get; internal set; }
		public object[] Arguments { get; internal set; }
		public IReadOnlyList<string> Categories { get; internal set; }
		public string Description { get; internal set; }
	}

	/// <summary>
	///     What a running test can ask the runner: its description, output kept with its result, and waits that let the
	///     editor run frames (tests run on the editor's main thread; an async test continues where it awaited in a later
	///     frame, so scenes, loads and timers advance in between).
	/// </summary>
	public static class TestContext
	{
		/// <summary>The running test (null outside a test).</summary>
		public static TestInfo CurrentTest => TestRun.CurrentExecution?.Info;

		/// <summary>Frames the runner ticked since the current test started.</summary>
		public static int FrameCount
		{
			get
			{
				TestExecution execution = TestRun.CurrentExecution;
				return execution == null ? 0 : execution.Run.Frame - execution.StartFrame;
			}
		}

		/// <summary>
		///     An empty folder for files the test writes, made on first use and deleted when the test ends (with
		///     everything in it).
		/// </summary>
		public static string TemporaryDirectory => TestRun.RequireExecution().TemporaryDirectory;

		/// <summary>A line of the test's output (shown with its result, not in the console).</summary>
		public static void WriteLine(object value)
		{
			Write((value?.ToString() ?? "null") + "\n");
		}

		public static void WriteLine()
		{
			Write("\n");
		}

		public static void WriteLine(string format, params object[] args)
		{
			Write(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args) + "\n");
		}

		public static void Write(object value)
		{
			TestExecution execution = TestRun.CurrentExecution;
			if (execution == null)
			{
				Debug.Log(value);
				return;
			}

			execution.WriteOutput(value?.ToString() ?? "null");
		}

		/// <summary><c>await TestContext.NextFrame();</c> continues in the editor's next frame.</summary>
		public static FrameAwaitable NextFrame()
		{
			return new FrameAwaitable(TestRun.RequireActive());
		}

		/// <summary>Continues after <paramref name="count" /> frames.</summary>
		public static async Task Frames(int count)
		{
			for (int i = 0; i < count; i++)
			{
				await NextFrame();
			}
		}

		/// <summary>Continues after <paramref name="seconds" /> of real time (frames run meanwhile).</summary>
		public static async Task WaitForSeconds(double seconds)
		{
			var clock = Stopwatch.StartNew();
			do
			{
				await NextFrame();
			}
			while (clock.Elapsed.TotalSeconds < seconds);
		}

		/// <summary>
		///     Continues in the first frame the condition holds (checked once per frame, first right away); fails the
		///     test when it still does not after <paramref name="timeoutSeconds" />.
		/// </summary>
		public static async Task WaitUntil(Func<bool> condition, double timeoutSeconds = 10, string message = null)
		{
			var clock = Stopwatch.StartNew();
			while (!condition())
			{
				if (clock.Elapsed.TotalSeconds > timeoutSeconds)
				{
					Assert.Failed(message, $"  Condition not met within {timeoutSeconds:0.###} s");
				}

				await NextFrame();
			}
		}
	}

	/// <summary><c>await TestContext.NextFrame()</c>.</summary>
	public readonly struct FrameAwaitable
	{
		private readonly TestRun run;

		internal FrameAwaitable(TestRun run)
		{
			this.run = run;
		}

		public FrameAwaiter GetAwaiter()
		{
			return new FrameAwaiter(run);
		}
	}

	public readonly struct FrameAwaiter : ICriticalNotifyCompletion
	{
		private readonly TestRun run;

		internal FrameAwaiter(TestRun run)
		{
			this.run = run;
		}

		public bool IsCompleted => false;

		public void GetResult()
		{
		}

		public void OnCompleted(Action continuation)
		{
			run.ScheduleNextFrame(continuation);
		}

		public void UnsafeOnCompleted(Action continuation)
		{
			run.ScheduleNextFrame(continuation);
		}
	}
}
