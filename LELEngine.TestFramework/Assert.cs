using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using OpenTK.Mathematics;

namespace LELEngine.Testing
{
	public delegate void TestDelegate();

	public delegate Task AsyncTestDelegate();

	/// <summary>A failed assertion: the test fails with its message.</summary>
	public class AssertionException : Exception
	{
		public AssertionException(string message)
			: base(message)
		{
		}
	}

	/// <summary><see cref="Assert.Ignore" />: the test ends as skipped.</summary>
	public class IgnoreException : Exception
	{
		public IgnoreException(string message)
			: base(message)
		{
		}
	}

	/// <summary><see cref="Assert.Inconclusive" />: the test ends without a verdict.</summary>
	public class InconclusiveException : Exception
	{
		public InconclusiveException(string message)
			: base(message)
		{
		}
	}

	/// <summary><see cref="Assert.Pass" />: the test ends as passed.</summary>
	public class SuccessException : Exception
	{
		public SuccessException(string message)
			: base(message)
		{
		}
	}

	/// <summary>
	///     NUnit's classic asserts. Failures say what was expected and what was found; a destroyed engine object counts as
	///     null (as in the scripts, see the null-check weaver). <see cref="ThrowsAsync{T}" /> and the other async forms
	///     return tasks: await them.
	/// </summary>
	public static class Assert
	{
		#region PublicMethods

		public static void Pass(string message = null)
		{
			throw new SuccessException(message ?? "");
		}

		public static void Fail(string message = null)
		{
			Failed(message ?? "Assert.Fail");
		}

		public static void Ignore(string message = null)
		{
			throw new IgnoreException(message ?? "");
		}

		public static void Inconclusive(string message = null)
		{
			throw new InconclusiveException(message ?? "");
		}

		public static void That(bool condition, string message = null)
		{
			IsTrue(condition, message);
		}

		public static void IsTrue(bool condition, string message = null)
		{
			if (!condition)
			{
				Failed(message, "True", "False");
			}
		}

		public static void True(bool condition, string message = null)
		{
			IsTrue(condition, message);
		}

		public static void IsFalse(bool condition, string message = null)
		{
			if (condition)
			{
				Failed(message, "False", "True");
			}
		}

		public static void False(bool condition, string message = null)
		{
			IsFalse(condition, message);
		}

		/// <summary>Null, or a destroyed engine object.</summary>
		public static void IsNull(object value, string message = null)
		{
			if (!TestFormat.IsNull(value))
			{
				Failed(message, "null", TestFormat.Value(value));
			}
		}

		public static void Null(object value, string message = null)
		{
			IsNull(value, message);
		}

		/// <summary>Neither null nor a destroyed engine object.</summary>
		public static void IsNotNull(object value, string message = null)
		{
			if (TestFormat.IsNull(value))
			{
				Failed(message, "not null", TestFormat.Value(value));
			}
		}

		public static void NotNull(object value, string message = null)
		{
			IsNotNull(value, message);
		}

		/// <summary>
		///     Equal values: numbers of any type by value, strings ordinally, sequences element by element, dictionaries
		///     by key, anything else by Equals.
		/// </summary>
		public static void AreEqual(object expected, object actual, string message = null)
		{
			if (!TestEquality.AreEqual(expected, actual, out string difference))
			{
				Failed(message, difference ?? TestFormat.ExpectedButWas(TestFormat.Value(expected), TestFormat.Value(actual)));
			}
		}

		/// <summary>Numbers within <paramref name="delta" /> of each other.</summary>
		public static void AreEqual(double expected, double actual, double delta, string message = null)
		{
			bool equal = double.IsNaN(expected) ? double.IsNaN(actual) : double.IsInfinity(expected) ? expected.Equals(actual) : Math.Abs(expected - actual) <= delta;
			if (!equal)
			{
				Failed(message, TestFormat.ExpectedButWas(TestFormat.Value(expected) + " +/- " + TestFormat.Value(delta), TestFormat.Value(actual)) + "\n  Off by:   " + TestFormat.Value(actual - expected));
			}
		}

		public static void AreNotEqual(object expected, object actual, string message = null)
		{
			if (TestEquality.AreEqual(expected, actual, out _))
			{
				Failed(message, "not equal to " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void AreSame(object expected, object actual, string message = null)
		{
			if (!ReferenceEquals(expected, actual))
			{
				Failed(message, "same as " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void AreNotSame(object expected, object actual, string message = null)
		{
			if (ReferenceEquals(expected, actual))
			{
				Failed(message, "not same as " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		/// <summary><paramref name="arg1" /> &gt; <paramref name="arg2" />.</summary>
		public static void Greater(IComparable arg1, IComparable arg2, string message = null)
		{
			if (TestEquality.Compare(arg1, arg2) <= 0)
			{
				Failed(message, "greater than " + TestFormat.Value(arg2), TestFormat.Value(arg1));
			}
		}

		public static void GreaterOrEqual(IComparable arg1, IComparable arg2, string message = null)
		{
			if (TestEquality.Compare(arg1, arg2) < 0)
			{
				Failed(message, "greater than or equal to " + TestFormat.Value(arg2), TestFormat.Value(arg1));
			}
		}

		/// <summary><paramref name="arg1" /> &lt; <paramref name="arg2" />.</summary>
		public static void Less(IComparable arg1, IComparable arg2, string message = null)
		{
			if (TestEquality.Compare(arg1, arg2) >= 0)
			{
				Failed(message, "less than " + TestFormat.Value(arg2), TestFormat.Value(arg1));
			}
		}

		public static void LessOrEqual(IComparable arg1, IComparable arg2, string message = null)
		{
			if (TestEquality.Compare(arg1, arg2) > 0)
			{
				Failed(message, "less than or equal to " + TestFormat.Value(arg2), TestFormat.Value(arg1));
			}
		}

		public static void IsNaN(double value, string message = null)
		{
			if (!double.IsNaN(value))
			{
				Failed(message, "NaN", TestFormat.Value(value));
			}
		}

		/// <summary>An empty string or sequence.</summary>
		public static void IsEmpty(IEnumerable value, string message = null)
		{
			if (value == null || value.Cast<object>().Any())
			{
				Failed(message, "<empty>", TestFormat.Value(value));
			}
		}

		public static void IsNotEmpty(IEnumerable value, string message = null)
		{
			if (value == null || !value.Cast<object>().Any())
			{
				Failed(message, "not <empty>", TestFormat.Value(value));
			}
		}

		/// <summary>The sequence holds an item equal to <paramref name="expected" />.</summary>
		public static void Contains(object expected, IEnumerable actual, string message = null)
		{
			if (actual == null || !actual.Cast<object>().Any(item => TestEquality.AreEqual(expected, item, out _)))
			{
				Failed(message, "collection containing " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		/// <summary>The value is a <typeparamref name="T" /> (or derives from it); returns it as one.</summary>
		public static T IsInstanceOf<T>(object value, string message = null)
		{
			if (!(value is T typed))
			{
				Failed(message, "instance of <" + TestFormat.TypeName(typeof(T)) + ">", value == null ? "null" : "<" + TestFormat.TypeName(value.GetType()) + ">");
				return default;
			}

			return typed;
		}

		public static void IsNotInstanceOf<T>(object value, string message = null)
		{
			if (value is T)
			{
				Failed(message, "not instance of <" + TestFormat.TypeName(typeof(T)) + ">", "<" + TestFormat.TypeName(value.GetType()) + ">");
			}
		}

		/// <summary>The code throws exactly a <typeparamref name="T" /> (not a subclass; see <see cref="Catch{T}" />); returns it.</summary>
		public static T Throws<T>(TestDelegate code, string message = null) where T : Exception
		{
			return (T)Throws(typeof(T), code, message);
		}

		public static Exception Throws(Type expectedType, TestDelegate code, string message = null)
		{
			Exception thrown = Run(code);
			CheckThrown(thrown, expectedType, true, message);
			return thrown;
		}

		/// <summary>The code throws a <typeparamref name="T" /> or a subclass of it; returns it.</summary>
		public static T Catch<T>(TestDelegate code, string message = null) where T : Exception
		{
			Exception thrown = Run(code);
			CheckThrown(thrown, typeof(T), false, message);
			return (T)thrown;
		}

		public static Exception Catch(TestDelegate code, string message = null)
		{
			return Catch<Exception>(code, message);
		}

		public static void DoesNotThrow(TestDelegate code, string message = null)
		{
			Exception thrown = Run(code);
			if (thrown != null)
			{
				Failed(message, TestFormat.ExpectedButWas("No exception", TestFormat.Exception(thrown)));
			}
		}

		/// <summary>The task fails with exactly a <typeparamref name="T" />: <c>await Assert.ThrowsAsync&lt;T&gt;(...)</c>.</summary>
		public static async Task<T> ThrowsAsync<T>(AsyncTestDelegate code, string message = null) where T : Exception
		{
			Exception thrown = await RunAsync(code);
			CheckThrown(thrown, typeof(T), true, message);
			return (T)thrown;
		}

		/// <summary>The task fails with a <typeparamref name="T" /> or a subclass of it.</summary>
		public static async Task<T> CatchAsync<T>(AsyncTestDelegate code, string message = null) where T : Exception
		{
			Exception thrown = await RunAsync(code);
			CheckThrown(thrown, typeof(T), false, message);
			return (T)thrown;
		}

		public static async Task DoesNotThrowAsync(AsyncTestDelegate code, string message = null)
		{
			Exception thrown = await RunAsync(code);
			if (thrown != null)
			{
				Failed(message, TestFormat.ExpectedButWas("No exception", TestFormat.Exception(thrown)));
			}
		}

		public static void AreApproximatelyEqual(float expected, float actual, float tolerance = 1e-5f, string message = null)
		{
			if (!(Math.Abs(expected - actual) <= tolerance))
			{
				Failed(message, TestFormat.ExpectedButWas(TestFormat.Value(expected) + " +/- " + TestFormat.Value(tolerance), TestFormat.Value(actual)));
			}
		}

		/// <summary>Every component within <paramref name="tolerance" />.</summary>
		public static void AreApproximatelyEqual(Vector2 expected, Vector2 actual, float tolerance = 1e-5f, string message = null)
		{
			CheckComponents(new[] { expected.X, expected.Y }, new[] { actual.X, actual.Y }, tolerance, message, TestFormat.Value(expected), TestFormat.Value(actual));
		}

		public static void AreApproximatelyEqual(Vector3 expected, Vector3 actual, float tolerance = 1e-5f, string message = null)
		{
			CheckComponents(new[] { expected.X, expected.Y, expected.Z }, new[] { actual.X, actual.Y, actual.Z }, tolerance, message, TestFormat.Value(expected), TestFormat.Value(actual));
		}

		public static void AreApproximatelyEqual(Vector4 expected, Vector4 actual, float tolerance = 1e-5f, string message = null)
		{
			CheckComponents(new[] { expected.X, expected.Y, expected.Z, expected.W }, new[] { actual.X, actual.Y, actual.Z, actual.W }, tolerance, message, TestFormat.Value(expected), TestFormat.Value(actual));
		}

		public static void AreApproximatelyEqual(Color4 expected, Color4 actual, float tolerance = 1e-5f, string message = null)
		{
			CheckComponents(new[] { expected.R, expected.G, expected.B, expected.A }, new[] { actual.R, actual.G, actual.B, actual.A }, tolerance, message, TestFormat.Value(expected), TestFormat.Value(actual));
		}

		/// <summary>The same rotation (q and -q are one rotation) within <paramref name="tolerance" /> per component.</summary>
		public static void AreApproximatelyEqual(Quaternion expected, Quaternion actual, float tolerance = 1e-5f, string message = null)
		{
			float sign = expected.X * actual.X + expected.Y * actual.Y + expected.Z * actual.Z + expected.W * actual.W < 0f ? -1f : 1f;
			CheckComponents(new[] { expected.X, expected.Y, expected.Z, expected.W }, new[] { actual.X * sign, actual.Y * sign, actual.Z * sign, actual.W * sign }, tolerance, message, TestFormat.Value(expected), TestFormat.Value(actual));
		}

		#endregion

		#region InternalMethods

		/// <summary>Every failed assertion goes through here: the run may stop the debugger first (Break on Failure).</summary>
		internal static void Failed(string message)
		{
			TestRun.OnAssertionFailing(message);
			throw new AssertionException(message);
		}

		internal static void Failed(string userMessage, string expected, string actual)
		{
			Failed(userMessage, TestFormat.ExpectedButWas(expected, actual));
		}

		internal static void Failed(string userMessage, string details)
		{
			Failed(string.IsNullOrEmpty(userMessage) ? details : userMessage + "\n" + details);
		}

		#endregion

		#region PrivateMethods

		private static Exception Run(TestDelegate code)
		{
			try
			{
				code();
				return null;
			}
			catch (Exception e)
			{
				return e;
			}
		}

		private static async Task<Exception> RunAsync(AsyncTestDelegate code)
		{
			try
			{
				await code();
				return null;
			}
			catch (Exception e)
			{
				return e;
			}
		}

		private static void CheckThrown(Exception thrown, Type expectedType, bool exact, string message)
		{
			string expected = "<" + TestFormat.TypeName(expectedType) + ">" + (exact ? "" : " or a subclass");
			if (thrown == null)
			{
				Failed(message, expected, "no exception");
			}
			else if (exact ? thrown.GetType() != expectedType : !expectedType.IsInstanceOfType(thrown))
			{
				Failed(message, expected, TestFormat.Exception(thrown));
			}
		}

		private static void CheckComponents(float[] expected, float[] actual, float tolerance, string message, string expectedText, string actualText)
		{
			for (int i = 0; i < expected.Length; i++)
			{
				if (!(Math.Abs(expected[i] - actual[i]) <= tolerance))
				{
					Failed(message, TestFormat.ExpectedButWas(expectedText + " +/- " + TestFormat.Value(tolerance), actualText) + "\n  Component " + i + " is off by " + TestFormat.Value(actual[i] - expected[i]));
				}
			}
		}

		#endregion
	}

	/// <summary>Asserts about whole sequences.</summary>
	public static class CollectionAssert
	{
		/// <summary>The same items in the same order.</summary>
		public static void AreEqual(IEnumerable expected, IEnumerable actual, string message = null)
		{
			Assert.AreEqual(expected, actual, message);
		}

		/// <summary>The same items, any order (each counted).</summary>
		public static void AreEquivalent(IEnumerable expected, IEnumerable actual, string message = null)
		{
			List<object> missing = expected?.Cast<object>().ToList() ?? new List<object>();
			var extra = new List<object>();
			foreach (object item in actual ?? Array.Empty<object>())
			{
				int index = missing.FindIndex(e => TestEquality.AreEqual(e, item, out _));
				if (index >= 0)
				{
					missing.RemoveAt(index);
				}
				else
				{
					extra.Add(item);
				}
			}

			if (missing.Count > 0 || extra.Count > 0 || expected == null != (actual == null))
			{
				Assert.Failed(message, "Expected: equivalent to " + TestFormat.Value(expected) + "\n  But was:  " + TestFormat.Value(actual) +
					(missing.Count > 0 ? "\n  Missing:  " + TestFormat.Value(missing) : "") + (extra.Count > 0 ? "\n  Extra:    " + TestFormat.Value(extra) : ""));
			}
		}

		public static void Contains(IEnumerable collection, object item, string message = null)
		{
			Assert.Contains(item, collection, message);
		}

		public static void DoesNotContain(IEnumerable collection, object item, string message = null)
		{
			if (collection != null && collection.Cast<object>().Any(e => TestEquality.AreEqual(item, e, out _)))
			{
				Assert.Failed(message, "not collection containing " + TestFormat.Value(item), TestFormat.Value(collection));
			}
		}

		public static void IsEmpty(IEnumerable collection, string message = null)
		{
			Assert.IsEmpty(collection, message);
		}

		public static void IsNotEmpty(IEnumerable collection, string message = null)
		{
			Assert.IsNotEmpty(collection, message);
		}

		public static void AllItemsAreNotNull(IEnumerable collection, string message = null)
		{
			int index = 0;
			foreach (object item in collection ?? Array.Empty<object>())
			{
				if (TestFormat.IsNull(item))
				{
					Assert.Failed(message, "all items not null", TestFormat.Value(collection) + " (index " + index + " is null)");
				}

				index++;
			}
		}

		public static void AllItemsAreUnique(IEnumerable collection, string message = null)
		{
			var seen = new List<object>();
			foreach (object item in collection ?? Array.Empty<object>())
			{
				if (seen.Any(e => TestEquality.AreEqual(e, item, out _)))
				{
					Assert.Failed(message, "all items unique", TestFormat.Value(collection) + " (" + TestFormat.Value(item) + " repeats)");
				}

				seen.Add(item);
			}
		}

		/// <summary>The items are in ascending order (by their <see cref="IComparable" />).</summary>
		public static void IsOrdered(IEnumerable collection, string message = null)
		{
			object previous = null;
			int index = 0;
			foreach (object item in collection ?? Array.Empty<object>())
			{
				if (index > 0 && TestEquality.Compare((IComparable)previous, (IComparable)item) > 0)
				{
					Assert.Failed(message, "collection ordered", TestFormat.Value(collection) + " (index " + index + " is smaller than the one before)");
				}

				previous = item;
				index++;
			}
		}
	}

	/// <summary>Asserts about strings (ordinal comparisons).</summary>
	public static class StringAssert
	{
		public static void Contains(string expected, string actual, string message = null)
		{
			if (actual == null || !actual.Contains(expected, StringComparison.Ordinal))
			{
				Assert.Failed(message, "string containing " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void DoesNotContain(string expected, string actual, string message = null)
		{
			if (actual != null && actual.Contains(expected, StringComparison.Ordinal))
			{
				Assert.Failed(message, "string not containing " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void StartsWith(string expected, string actual, string message = null)
		{
			if (actual == null || !actual.StartsWith(expected, StringComparison.Ordinal))
			{
				Assert.Failed(message, "string starting with " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void EndsWith(string expected, string actual, string message = null)
		{
			if (actual == null || !actual.EndsWith(expected, StringComparison.Ordinal))
			{
				Assert.Failed(message, "string ending with " + TestFormat.Value(expected), TestFormat.Value(actual));
			}
		}

		public static void AreEqualIgnoringCase(string expected, string actual, string message = null)
		{
			if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
			{
				Assert.Failed(message, TestFormat.Value(expected) + ", ignoring case", TestFormat.Value(actual));
			}
		}

		public static void IsMatch(string pattern, string actual, string message = null)
		{
			if (actual == null || !Regex.IsMatch(actual, pattern))
			{
				Assert.Failed(message, "string matching " + TestFormat.Value(pattern), TestFormat.Value(actual));
			}
		}
	}

	/// <summary>Equality and order as the asserts use them.</summary>
	internal static class TestEquality
	{
		public static bool AreEqual(object expected, object actual, out string difference)
		{
			difference = null;
			if (TestFormat.IsNull(expected) || TestFormat.IsNull(actual))
			{
				return TestFormat.IsNull(expected) && TestFormat.IsNull(actual) && (expected == null || actual == null || ReferenceEquals(expected, actual) || expected is EngineObject && actual is EngineObject);
			}

			if (IsNumber(expected) && IsNumber(actual))
			{
				return NumbersEqual(expected, actual);
			}

			if (expected is string || actual is string)
			{
				if (expected is string expectedText && actual is string actualText)
				{
					if (string.Equals(expectedText, actualText, StringComparison.Ordinal))
					{
						return true;
					}

					difference = StringDifference(expectedText, actualText);
				}

				return false;
			}

			if (expected is IDictionary expectedDictionary && actual is IDictionary actualDictionary)
			{
				return DictionariesEqual(expectedDictionary, actualDictionary, out difference);
			}

			if (expected is IEnumerable expectedItems && actual is IEnumerable actualItems && !(expected is IComparable && expected.Equals(actual)))
			{
				return SequencesEqual(expected, actual, expectedItems, actualItems, out difference);
			}

			return expected.Equals(actual);
		}

		/// <summary>Order of two values: numbers of any type by value, else the first value's CompareTo.</summary>
		public static int Compare(IComparable a, IComparable b)
		{
			if (a == null || b == null)
			{
				throw new ArgumentException("Cannot compare with null.");
			}

			if (IsNumber(a) && IsNumber(b))
			{
				if (IsFloating(a) || IsFloating(b))
				{
					return Convert.ToDouble(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDouble(b, CultureInfo.InvariantCulture));
				}

				return Convert.ToDecimal(a, CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(b, CultureInfo.InvariantCulture));
			}

			return a.CompareTo(b);
		}

		private static bool IsNumber(object value)
		{
			return value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong
				|| value is float || value is double || value is decimal;
		}

		private static bool IsFloating(object value)
		{
			return value is float || value is double;
		}

		private static bool NumbersEqual(object expected, object actual)
		{
			if (IsFloating(expected) || IsFloating(actual))
			{
				double a = Convert.ToDouble(expected, CultureInfo.InvariantCulture);
				double b = Convert.ToDouble(actual, CultureInfo.InvariantCulture);
				return a.Equals(b);
			}

			return Convert.ToDecimal(expected, CultureInfo.InvariantCulture) == Convert.ToDecimal(actual, CultureInfo.InvariantCulture);
		}

		private static bool SequencesEqual(object expected, object actual, IEnumerable expectedItems, IEnumerable actualItems, out string difference)
		{
			difference = null;
			List<object> a = expectedItems.Cast<object>().ToList();
			List<object> b = actualItems.Cast<object>().ToList();
			int count = Math.Min(a.Count, b.Count);
			for (int i = 0; i < count; i++)
			{
				if (!AreEqual(a[i], b[i], out _))
				{
					difference = "Expected and actual differ at index [" + i + "]\n" + TestFormat.ExpectedButWas(TestFormat.Value(a[i]), TestFormat.Value(b[i])) +
						"\n  Expected sequence: " + TestFormat.Value(expected) + "\n  Actual sequence:   " + TestFormat.Value(actual);
					return false;
				}
			}

			if (a.Count != b.Count)
			{
				difference = "Expected is " + TestFormat.Describe(expected, a.Count) + ", actual is " + TestFormat.Describe(actual, b.Count) + "\n" +
					(a.Count > b.Count ? "  Missing from index [" + count + "]: " + TestFormat.Value(a.Skip(count).ToList()) : "  Extra from index [" + count + "]: " + TestFormat.Value(b.Skip(count).ToList())) +
					"\n  Expected sequence: " + TestFormat.Value(expected) + "\n  Actual sequence:   " + TestFormat.Value(actual);
				return false;
			}

			return true;
		}

		private static bool DictionariesEqual(IDictionary expected, IDictionary actual, out string difference)
		{
			difference = null;
			foreach (DictionaryEntry entry in expected)
			{
				if (!actual.Contains(entry.Key))
				{
					difference = "Key " + TestFormat.Value(entry.Key) + " is missing\n  Expected: " + TestFormat.Value(expected) + "\n  But was:  " + TestFormat.Value(actual);
					return false;
				}

				if (!AreEqual(entry.Value, actual[entry.Key], out _))
				{
					difference = "Values of key " + TestFormat.Value(entry.Key) + " differ\n" + TestFormat.ExpectedButWas(TestFormat.Value(entry.Value), TestFormat.Value(actual[entry.Key]));
					return false;
				}
			}

			foreach (object key in actual.Keys)
			{
				if (!expected.Contains(key))
				{
					difference = "Extra key " + TestFormat.Value(key) + "\n  Expected: " + TestFormat.Value(expected) + "\n  But was:  " + TestFormat.Value(actual);
					return false;
				}
			}

			return true;
		}

		private static string StringDifference(string expected, string actual)
		{
			int index = 0;
			while (index < expected.Length && index < actual.Length && expected[index] == actual[index])
			{
				index++;
			}

			string lengths = expected.Length == actual.Length ? "Strings are both " + expected.Length + " long" : "Expected is " + expected.Length + " long, actual is " + actual.Length;
			return lengths + ", they differ at index " + index + "\n" + TestFormat.ExpectedButWas(TestFormat.Value(expected), TestFormat.Value(actual)) + "\n  " + new string('-', 10 + Math.Min(index, 60)) + "^";
		}
	}

	/// <summary>How values read in failure messages (NUnit's style: "text", 1.5f, &lt; 1, 2 &gt;).</summary>
	internal static class TestFormat
	{
		private const int MaxItems = 12;

		/// <summary>Null, or an engine object destroyed (it reads as null in the scripts too).</summary>
		public static bool IsNull(object value)
		{
			return value == null || value is EngineObject engineObject && engineObject.IsDestroyed;
		}

		public static string ExpectedButWas(string expected, string actual)
		{
			return "  Expected: " + expected + "\n  But was:  " + actual;
		}

		public static string Value(object value)
		{
			switch (value)
			{
				case null:
					return "null";
				case EngineObject engineObject when engineObject.IsDestroyed:
					return "<destroyed " + engineObject + ">";
				case string text:
					return "\"" + text + "\"";
				case char c:
					return "'" + c + "'";
				case float f:
					return f.ToString("R", CultureInfo.InvariantCulture) + "f";
				case double d:
					return d.ToString("R", CultureInfo.InvariantCulture) + "d";
				case decimal m:
					return m.ToString(CultureInfo.InvariantCulture) + "m";
				case bool b:
					return b ? "True" : "False";
				case Type type:
					return "<" + TypeName(type) + ">";
				case IDictionary dictionary:
				{
					var builder = new StringBuilder("[ ");
					int n = 0;
					foreach (DictionaryEntry entry in dictionary)
					{
						if (n > 0) builder.Append(", ");
						if (n++ == MaxItems)
						{
							builder.Append("...");
							break;
						}

						builder.Append(Value(entry.Key)).Append(": ").Append(Value(entry.Value));
					}

					return builder.Append(" ]").ToString();
				}
				case IEnumerable items:
				{
					var builder = new StringBuilder("< ");
					int n = 0;
					foreach (object item in items)
					{
						if (n > 0) builder.Append(", ");
						if (n++ == MaxItems)
						{
							builder.Append("...");
							break;
						}

						builder.Append(Value(item));
					}

					return builder.Append(" >").ToString();
				}
				case IFormattable formattable:
					return formattable.ToString(null, CultureInfo.InvariantCulture);
				default:
					return value.ToString();
			}
		}

		public static string Describe(object sequence, int count)
		{
			return "<" + TypeName(sequence.GetType()) + "> with " + count + (count == 1 ? " item" : " items");
		}

		public static string Exception(Exception e)
		{
			return "<" + TypeName(e.GetType()) + ">: " + e.Message;
		}

		public static string TypeName(Type type)
		{
			if (type.IsArray)
			{
				return TypeName(type.GetElementType()) + "[]";
			}

			if (!type.IsGenericType)
			{
				return type.FullName ?? type.Name;
			}

			string name = (type.FullName ?? type.Name).Split('`')[0];
			return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
		}
	}
}
