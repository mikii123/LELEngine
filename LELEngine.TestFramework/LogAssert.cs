using System.Text.RegularExpressions;

namespace LELEngine.Testing
{
	/// <summary>
	///     Unity's LogAssert. An error or exception logged while a test runs (<c>Debug.LogError</c>,
	///     <c>Debug.LogException</c>, an exception a component callback threw) fails the test, unless it was
	///     expected with <see cref="Expect(LogType, string)" />; an expected message that never comes fails it too.
	/// </summary>
	public static class LogAssert
	{
		/// <summary>Errors and exceptions logged by the current test do not fail it (reset before every test).</summary>
		public static bool ignoreFailingMessages
		{
			get => TestRun.CurrentExecution?.IgnoreFailingMessages ?? false;
			set
			{
				TestExecution execution = TestRun.RequireExecution();
				execution.IgnoreFailingMessages = value;
			}
		}

		/// <summary>The next message of this type whose text is exactly <paramref name="message" /> is expected (consumed when it comes).</summary>
		public static void Expect(LogType type, string message)
		{
			TestRun.RequireExecution().Expect(type, message, null);
		}

		/// <summary>The next message of this type whose text matches <paramref name="message" /> is expected.</summary>
		public static void Expect(LogType type, Regex message)
		{
			TestRun.RequireExecution().Expect(type, null, message);
		}

		/// <summary>Fails now when an error or exception was logged that nothing expected (the test's end checks it anyway).</summary>
		public static void NoUnexpectedReceived()
		{
			string unexpected = TestRun.RequireExecution().TakeUnexpected();
			if (unexpected != null)
			{
				Assert.Failed(unexpected);
			}
		}
	}
}
