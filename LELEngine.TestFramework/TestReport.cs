using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;

namespace LELEngine.Testing
{
	/// <summary>Results as files and text: JUnit XML (CI servers read it), a one-line summary.</summary>
	public static class TestReport
	{
		/// <summary>"12 passed, 1 failed, 2 skipped in 0.43 s".</summary>
		public static string Summary(TestRun run)
		{
			var parts = new List<string> { run.Passed + " passed" };
			if (run.Failed > 0) parts.Add(run.Failed + " failed");
			if (run.Skipped > 0) parts.Add(run.Skipped + " skipped");
			if (run.Inconclusive > 0) parts.Add(run.Inconclusive + " inconclusive");
			int notRun = run.Tests.Count - run.Completed;
			if (notRun > 0) parts.Add(notRun + " not run");
			return string.Join(", ", parts) + string.Format(CultureInfo.InvariantCulture, " in {0:0.00} s", run.Seconds);
		}

		/// <summary>
		///     Writes the run as JUnit XML: a testsuite per class, a testcase per test (failure / skipped, the output as
		///     system-out); class-level failures (set up, tear down) as a testcase named after the class.
		/// </summary>
		public static void WriteJUnit(TestRun run, string path, string name)
		{
			string folder = Path.GetDirectoryName(Path.GetFullPath(path));
			if (!string.IsNullOrEmpty(folder))
			{
				Directory.CreateDirectory(folder);
			}

			var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) };
			using XmlWriter xml = XmlWriter.Create(path, settings);
			xml.WriteStartDocument();
			xml.WriteStartElement("testsuites");
			xml.WriteAttributeString("name", name);
			xml.WriteAttributeString("tests", run.Tests.Count.ToString(CultureInfo.InvariantCulture));
			xml.WriteAttributeString("failures", run.Failed.ToString(CultureInfo.InvariantCulture));
			xml.WriteAttributeString("skipped", (run.Skipped + run.Inconclusive + run.Tests.Count - run.Completed).ToString(CultureInfo.InvariantCulture));
			xml.WriteAttributeString("time", Seconds(run.Seconds));
			foreach (IGrouping<TestNode, TestNode> fixture in run.Tests.GroupBy(t => t.Fixture))
			{
				List<TestResult> fixtureResults = fixture.Select(t => run.Results.TryGetValue(t.FullName, out TestResult r) ? r : null).ToList();
				run.Results.TryGetValue(fixture.Key.FullName, out TestResult fixtureProblem);
				xml.WriteStartElement("testsuite");
				xml.WriteAttributeString("name", fixture.Key.FullName);
				xml.WriteAttributeString("tests", (fixture.Count() + (fixtureProblem != null ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
				xml.WriteAttributeString("failures", (fixtureResults.Count(r => r?.Outcome == TestOutcome.Failed) + (fixtureProblem != null ? 1 : 0)).ToString(CultureInfo.InvariantCulture));
				xml.WriteAttributeString("skipped", fixtureResults.Count(r => r == null || r.Outcome != TestOutcome.Passed && r.Outcome != TestOutcome.Failed).ToString(CultureInfo.InvariantCulture));
				xml.WriteAttributeString("time", Seconds(fixtureResults.Sum(r => r?.Seconds ?? 0)));
				foreach (TestNode test in fixture)
				{
					run.Results.TryGetValue(test.FullName, out TestResult result);
					WriteCase(xml, fixture.Key.FullName, test.FullName.Substring(fixture.Key.FullName.Length + 1), result);
				}

				if (fixtureProblem != null)
				{
					WriteCase(xml, fixture.Key.FullName, fixture.Key.Name, fixtureProblem);
				}

				xml.WriteEndElement();
			}

			xml.WriteEndElement();
			xml.WriteEndDocument();
		}

		private static void WriteCase(XmlWriter xml, string className, string name, TestResult result)
		{
			xml.WriteStartElement("testcase");
			xml.WriteAttributeString("classname", className);
			xml.WriteAttributeString("name", name);
			xml.WriteAttributeString("time", Seconds(result?.Seconds ?? 0));
			switch (result?.Outcome)
			{
				case TestOutcome.Failed:
					xml.WriteStartElement("failure");
					xml.WriteAttributeString("message", result.Message ?? "");
					xml.WriteString(((result.Message ?? "") + "\n" + (result.StackTrace ?? "")).Trim());
					xml.WriteEndElement();
					break;
				case TestOutcome.Passed:
					break;
				default:
					xml.WriteStartElement("skipped");
					xml.WriteAttributeString("message", result == null ? "Not run" : result.Outcome == TestOutcome.Inconclusive ? "Inconclusive: " + result.Message : result.Message ?? "");
					xml.WriteEndElement();
					break;
			}

			if (result != null && result.Output.Count > 0)
			{
				xml.WriteStartElement("system-out");
				xml.WriteString(string.Join("\n", result.Output.Select(e => e.IsOutput ? e.Text.TrimEnd('\n') : "[" + e.Type + "] " + e.Text)));
				xml.WriteEndElement();
			}

			xml.WriteEndElement();
		}

		private static string Seconds(double seconds)
		{
			return seconds.ToString("0.000", CultureInfo.InvariantCulture);
		}
	}
}
