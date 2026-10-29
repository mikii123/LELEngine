using System;
using System.Collections.Generic;

namespace LELEngine.Testing
{
	/// <summary>
	///     A test: a method without parameters of a class in a Tests folder (any access, instance or static), returning
	///     void, a value (with <see cref="ExpectedResult" />), <see cref="System.Threading.Tasks.Task" /> or
	///     ValueTask. An async test continues across editor frames (<see cref="TestContext.NextFrame" />).
	/// </summary>
	[AttributeUsage(AttributeTargets.Method, Inherited = true)]
	public sealed class TestAttribute : Attribute
	{
		private object expectedResult;

		public string Description { get; set; }

		/// <summary>The value the method must return (compared as <see cref="Assert.AreEqual(object, object, string)" /> does).</summary>
		public object ExpectedResult
		{
			get => expectedResult;
			set
			{
				expectedResult = value;
				HasExpectedResult = true;
			}
		}

		public bool HasExpectedResult { get; private set; }
	}

	/// <summary>
	///     A test with arguments: one case per attribute. Arguments convert to the parameter types where they can (an int
	///     literal for a float or long parameter, a string for an enum).
	/// </summary>
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
	public sealed class TestCaseAttribute : Attribute
	{
		private object expectedResult;

		public TestCaseAttribute(params object[] arguments)
		{
			// [TestCase(null)] passes a null array: one null argument.
			Arguments = arguments ?? new object[] { null };
		}

		public object[] Arguments { get; }

		/// <summary>The case's name in place of "Method(arguments)".</summary>
		public string TestName { get; set; }

		public string Description { get; set; }

		/// <summary>Categories of this case, comma separated.</summary>
		public string Category { get; set; }

		/// <summary>Skips the case with this reason.</summary>
		public string Ignore { get; set; }

		/// <summary>Runs only when the case itself is run, not with its method, class or "Run All".</summary>
		public bool Explicit { get; set; }

		public object ExpectedResult
		{
			get => expectedResult;
			set
			{
				expectedResult = value;
				HasExpectedResult = true;
			}
		}

		public bool HasExpectedResult { get; private set; }
	}

	/// <summary>
	///     Cases from a static field, property or method without parameters (of the test's class, or of
	///     <see cref="SourceType" />) returning a sequence: each item is an object[] of arguments, a
	///     <see cref="TestCaseData" />, or a single argument.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
	public sealed class TestCaseSourceAttribute : Attribute
	{
		public TestCaseSourceAttribute(string sourceName)
		{
			SourceName = sourceName;
		}

		public TestCaseSourceAttribute(Type sourceType, string sourceName)
		{
			SourceType = sourceType;
			SourceName = sourceName;
		}

		public Type SourceType { get; }
		public string SourceName { get; }

		/// <summary>Categories of every case of the source, comma separated.</summary>
		public string Category { get; set; }
	}

	/// <summary>Runs before every test of its class (base classes' first).</summary>
	[AttributeUsage(AttributeTargets.Method, Inherited = true)]
	public sealed class SetUpAttribute : Attribute
	{
	}

	/// <summary>Runs after every test of its class, also after a failure (the class's own first, then base classes').</summary>
	[AttributeUsage(AttributeTargets.Method, Inherited = true)]
	public sealed class TearDownAttribute : Attribute
	{
	}

	/// <summary>Runs once before the first test of its class that runs.</summary>
	[AttributeUsage(AttributeTargets.Method, Inherited = true)]
	public sealed class OneTimeSetUpAttribute : Attribute
	{
	}

	/// <summary>Runs once after the last test of its class that ran.</summary>
	[AttributeUsage(AttributeTargets.Method, Inherited = true)]
	public sealed class OneTimeTearDownAttribute : Attribute
	{
	}

	/// <summary>
	///     Marks a class of tests (optional: any class with a test method is one). One instance runs all of its tests,
	///     made with its constructor without parameters.
	/// </summary>
	[AttributeUsage(AttributeTargets.Class, Inherited = true)]
	public sealed class TestFixtureAttribute : Attribute
	{
		public string Description { get; set; }

		/// <summary>Categories of every test of the class, comma separated.</summary>
		public string Category { get; set; }

		/// <summary>Skips every test of the class with this reason.</summary>
		public string Ignore { get; set; }
	}

	/// <summary>Skips the test (or every test of the class) with a reason; it shows as skipped.</summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
	public sealed class IgnoreAttribute : Attribute
	{
		public IgnoreAttribute(string reason)
		{
			Reason = reason;
		}

		public string Reason { get; }
	}

	/// <summary>A category of the test (or of every test of the class): the Test Runner and -testCategory filter by it.</summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
	public sealed class CategoryAttribute : Attribute
	{
		public CategoryAttribute(string name)
		{
			Name = name;
		}

		public string Name { get; }
	}

	/// <summary>
	///     Fails an async test (or set up / tear down) that runs longer (milliseconds; default 60 000, 0 = none).
	///     Synchronous code cannot be stopped: it blocks the editor until it returns.
	/// </summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
	public sealed class TimeoutAttribute : Attribute
	{
		public TimeoutAttribute(int milliseconds)
		{
			Milliseconds = milliseconds;
		}

		public int Milliseconds { get; }
	}

	/// <summary>Runs only when the test (or the class) itself is run, not with a parent or "Run All".</summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
	public sealed class ExplicitAttribute : Attribute
	{
		public ExplicitAttribute(string reason = null)
		{
			Reason = reason;
		}

		public string Reason { get; }
	}

	/// <summary>A description shown with the test in the Test Runner.</summary>
	[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
	public sealed class DescriptionAttribute : Attribute
	{
		public DescriptionAttribute(string description)
		{
			Description = description;
		}

		public string Description { get; }
	}

	/// <summary>A case of a <see cref="TestCaseSourceAttribute" />: arguments plus what a <see cref="TestCaseAttribute" /> can say.</summary>
	public sealed class TestCaseData
	{
		public TestCaseData(params object[] arguments)
		{
			Arguments = arguments ?? new object[] { null };
		}

		public object[] Arguments { get; }
		public object ExpectedResult { get; private set; }
		public bool HasExpectedResult { get; private set; }
		public string TestName { get; private set; }
		public string Description { get; private set; }
		public List<string> Categories { get; } = new List<string>();
		public string IgnoreReason { get; private set; }
		public bool IsExplicit { get; private set; }

		public TestCaseData Returns(object result)
		{
			ExpectedResult = result;
			HasExpectedResult = true;
			return this;
		}

		public TestCaseData SetName(string name)
		{
			TestName = name;
			return this;
		}

		public TestCaseData SetDescription(string description)
		{
			Description = description;
			return this;
		}

		public TestCaseData SetCategory(string category)
		{
			Categories.Add(category);
			return this;
		}

		public TestCaseData Ignore(string reason)
		{
			IgnoreReason = reason;
			return this;
		}

		public TestCaseData Explicit()
		{
			IsExplicit = true;
			return this;
		}
	}
}
