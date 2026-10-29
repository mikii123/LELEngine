using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LELEngine.Testing
{
	public enum TestNodeKind
	{
		/// <summary>The test assembly.</summary>
		Root,
		Namespace,

		/// <summary>A class with tests.</summary>
		Fixture,

		/// <summary>A test method (a test itself, or the parent of its cases).</summary>
		Method,

		/// <summary>One case of a parameterized test.</summary>
		Case
	}

	/// <summary>
	///     A node of the discovered tests: assembly, namespace, class, method, case. Leaves are the tests
	///     (<see cref="IsTest" />). Nodes hold the test assembly's types: drop them before its context unloads.
	/// </summary>
	public sealed class TestNode
	{
		internal TestNode(TestNodeKind kind, string name, string fullName, TestNode parent)
		{
			Kind = kind;
			Name = name;
			FullName = fullName;
			Parent = parent;
			parent?.Children.Add(this);
		}

		public TestNodeKind Kind { get; }

		/// <summary>As the tree shows it: "Inventory", "AddsItem", "AddsItem(3, \"gold\")".</summary>
		public string Name { get; }

		/// <summary>Unique: "Namespace.Class.Method(args)"; results are kept by it across script reloads.</summary>
		public string FullName { get; }

		public TestNode Parent { get; }
		public List<TestNode> Children { get; } = new List<TestNode>();
		public Type FixtureType { get; internal set; }
		public MethodInfo Method { get; internal set; }
		public object[] Arguments { get; internal set; }
		public object ExpectedResult { get; internal set; }
		public bool HasExpectedResult { get; internal set; }

		/// <summary>Categories of the test, its class's included.</summary>
		public List<string> Categories { get; } = new List<string>();

		/// <summary>Skipped with this reason (its own or its class's [Ignore]).</summary>
		public string IgnoreReason { get; internal set; }

		/// <summary>[Explicit] here: this node runs only when it (or something below it) is run itself.</summary>
		public bool IsExplicit { get; internal set; }

		public string Description { get; internal set; }

		/// <summary>Limit for the test's async steps (0: none).</summary>
		public int TimeoutMilliseconds { get; internal set; }

		/// <summary>Why the test cannot run (a bad signature, a source that threw); it fails with this.</summary>
		public string Error { get; internal set; }

		/// <summary>Source file and line of the method (from the assembly's symbols), or null.</summary>
		public string SourceFile { get; internal set; }

		public int SourceLine { get; internal set; }

		// Fixtures: their set up and tear down methods, in running order.
		internal List<MethodInfo> SetUps { get; } = new List<MethodInfo>();
		internal List<MethodInfo> TearDowns { get; } = new List<MethodInfo>();
		internal List<MethodInfo> OneTimeSetUps { get; } = new List<MethodInfo>();
		internal List<MethodInfo> OneTimeTearDowns { get; } = new List<MethodInfo>();

		public bool IsTest => Kind == TestNodeKind.Case || Kind == TestNodeKind.Method && Children.Count == 0;

		/// <summary>The class node of a method or case (itself for a class).</summary>
		public TestNode Fixture
		{
			get
			{
				TestNode node = this;
				while (node != null && node.Kind != TestNodeKind.Fixture)
				{
					node = node.Parent;
				}

				return node;
			}
		}

		/// <summary>
		///     The tests that run when this node is run: every test below it, except those under an [Explicit] node other
		///     than this one and its parents.
		/// </summary>
		public List<TestNode> CollectTests()
		{
			var tests = new List<TestNode>();
			Collect(this, tests);
			return tests;
		}

		/// <summary>Every node below this one (this first), depth first.</summary>
		public IEnumerable<TestNode> Descendants()
		{
			yield return this;
			foreach (TestNode child in Children)
			{
				foreach (TestNode node in child.Descendants())
				{
					yield return node;
				}
			}
		}

		public TestNode Find(string fullName)
		{
			return Descendants().FirstOrDefault(n => n.FullName == fullName);
		}

		public override string ToString()
		{
			return FullName;
		}

		private static void Collect(TestNode node, List<TestNode> tests)
		{
			if (node.IsTest)
			{
				tests.Add(node);
				return;
			}

			foreach (TestNode child in node.Children)
			{
				if (!child.IsExplicit)
				{
					Collect(child, tests);
				}
			}
		}
	}

	/// <summary>Finds the tests of an assembly (see <see cref="TestAttribute" />).</summary>
	public static class TestDiscovery
	{
		#region PrivateFields

		private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
		private const int DefaultTimeout = 60000;

		#endregion

		#region PublicMethods

		/// <param name="assembly">The test assembly (Game.Tests.dll).</param>
		/// <param name="symbols">The assembly's portable PDB (source lines of the tests), or null.</param>
		public static TestNode Discover(Assembly assembly, byte[] symbols)
		{
			var root = new TestNode(TestNodeKind.Root, assembly.GetName().Name, "", null);
			Type[] types;
			try
			{
				types = assembly.GetTypes();
			}
			catch (ReflectionTypeLoadException e)
			{
				types = e.Types.Where(t => t != null).ToArray();
			}

			using var locator = new SourceLocator(assembly, symbols);
			var namespaces = new Dictionary<string, TestNode>(StringComparer.Ordinal);
			foreach (Type type in types.Where(IsFixtureCandidate).OrderBy(t => t.FullName, StringComparer.Ordinal))
			{
				List<MethodInfo> methods = TestMethods(type);
				if (methods.Count == 0)
				{
					continue;
				}

				string ns = type.Namespace ?? "";
				TestNode parent = root;
				if (ns.Length > 0 && !namespaces.TryGetValue(ns, out parent))
				{
					parent = new TestNode(TestNodeKind.Namespace, ns, ns, root);
					namespaces[ns] = parent;
				}

				AddFixture(type, methods, parent, locator);
			}

			// Namespaces and classes by name; tests in their source order.
			Sort(root);
			return root;
		}

		#endregion

		#region PrivateMethods

		private static bool IsFixtureCandidate(Type type)
		{
			return type.IsClass && !type.ContainsGenericParameters && (!type.IsAbstract || type.IsSealed) && !typeof(Delegate).IsAssignableFrom(type)
				&& !type.IsDefined(typeof(CompilerGeneratedAttribute), false);
		}

		/// <summary>Test methods of the class and its base classes (an override counts once).</summary>
		private static List<MethodInfo> TestMethods(Type type)
		{
			var methods = new List<MethodInfo>();
			var seen = new HashSet<string>(StringComparer.Ordinal);
			for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
			{
				foreach (MethodInfo method in t.GetMethods(Declared))
				{
					if (!IsTestMethod(method))
					{
						continue;
					}

					// The most derived declaration wins (base classes come later).
					MethodInfo definition = method.GetBaseDefinition();
					if (seen.Add(definition.DeclaringType + "." + definition.Name + "(" + string.Join(",", method.GetParameters().Select(p => p.ParameterType.FullName)) + ")"))
					{
						methods.Add(method);
					}
				}
			}

			return methods;
		}

		private static bool IsTestMethod(MethodInfo method)
		{
			return method.IsDefined(typeof(TestAttribute), true) || method.IsDefined(typeof(TestCaseAttribute), true) || method.IsDefined(typeof(TestCaseSourceAttribute), true);
		}

		private static void AddFixture(Type type, List<MethodInfo> methods, TestNode parent, SourceLocator locator)
		{
			string fixtureName = DisplayName(type);
			var fixture = new TestNode(TestNodeKind.Fixture, fixtureName, (type.Namespace != null ? type.Namespace + "." : "") + fixtureName, parent) { FixtureType = type };
			var fixtureAttribute = type.GetCustomAttribute<TestFixtureAttribute>(true);
			fixture.Description = type.GetCustomAttribute<DescriptionAttribute>(true)?.Description ?? fixtureAttribute?.Description;
			fixture.IgnoreReason = type.GetCustomAttribute<IgnoreAttribute>(true)?.Reason ?? fixtureAttribute?.Ignore;
			fixture.IsExplicit = type.IsDefined(typeof(ExplicitAttribute), true);
			fixture.TimeoutMilliseconds = type.GetCustomAttribute<TimeoutAttribute>(true)?.Milliseconds ?? DefaultTimeout;
			AddCategories(fixture.Categories, type.GetCustomAttributes<CategoryAttribute>(true).Select(c => c.Name));
			AddCategories(fixture.Categories, Split(fixtureAttribute?.Category));

			bool isStatic = type.IsAbstract && type.IsSealed;
			if (!isStatic && type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) == null)
			{
				fixture.Error = $"{fixtureName} has no constructor without parameters: the runner cannot make it.";
			}

			// Base classes' set ups first, their tear downs last.
			var hierarchy = new List<Type>();
			for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
			{
				hierarchy.Insert(0, t);
			}

			foreach (Type t in hierarchy)
			{
				foreach (MethodInfo method in t.GetMethods(Declared).OrderBy(m => m.MetadataToken))
				{
					CheckLifecycle(fixture, method, typeof(SetUpAttribute), fixture.SetUps, false);
					CheckLifecycle(fixture, method, typeof(OneTimeSetUpAttribute), fixture.OneTimeSetUps, false);
					CheckLifecycle(fixture, method, typeof(TearDownAttribute), fixture.TearDowns, true);
					CheckLifecycle(fixture, method, typeof(OneTimeTearDownAttribute), fixture.OneTimeTearDowns, true);
				}
			}

			foreach (MethodInfo method in methods)
			{
				AddMethod(fixture, method, locator);
			}

			fixture.SourceFile = fixture.Children.Select(c => c.SourceFile).FirstOrDefault(f => f != null);
			fixture.SourceLine = fixture.Children.Where(c => c.SourceFile == fixture.SourceFile).Select(c => c.SourceLine).DefaultIfEmpty(0).Min();
		}

		private static void CheckLifecycle(TestNode fixture, MethodInfo method, Type attribute, List<MethodInfo> list, bool reverse)
		{
			if (!method.IsDefined(attribute, true))
			{
				return;
			}

			// An override declared lower in the hierarchy replaces the base declaration.
			list.RemoveAll(m => m.GetBaseDefinition() == method.GetBaseDefinition());
			if (method.GetParameters().Length != 0 || method.ContainsGenericParameters)
			{
				fixture.Error ??= $"[{attribute.Name.Replace("Attribute", "")}] {method.Name} must have no parameters.";
				return;
			}

			if (IsAsyncVoid(method))
			{
				fixture.Error ??= $"[{attribute.Name.Replace("Attribute", "")}] {method.Name} is async void: the runner cannot wait for it (return Task).";
				return;
			}

			if (reverse)
			{
				list.Insert(0, method);
			}
			else
			{
				list.Add(method);
			}
		}

		private static void AddMethod(TestNode fixture, MethodInfo method, SourceLocator locator)
		{
			var node = new TestNode(TestNodeKind.Method, method.Name, fixture.FullName + "." + method.Name, fixture)
			{
				FixtureType = fixture.FixtureType,
				Method = method,
				Arguments = Array.Empty<object>(),
				Description = method.GetCustomAttribute<DescriptionAttribute>(true)?.Description,
				IgnoreReason = fixture.IgnoreReason ?? method.GetCustomAttribute<IgnoreAttribute>(true)?.Reason,
				IsExplicit = method.IsDefined(typeof(ExplicitAttribute), true),
				TimeoutMilliseconds = method.GetCustomAttribute<TimeoutAttribute>(true)?.Milliseconds ?? fixture.TimeoutMilliseconds,
				Error = fixture.Error
			};
			(node.SourceFile, node.SourceLine) = locator.Find(method);
			AddCategories(node.Categories, fixture.Categories);
			AddCategories(node.Categories, method.GetCustomAttributes<CategoryAttribute>(true).Select(c => c.Name));

			var test = method.GetCustomAttribute<TestAttribute>(true);
			node.Description ??= test?.Description;
			if (test != null && test.HasExpectedResult)
			{
				node.ExpectedResult = test.ExpectedResult;
				node.HasExpectedResult = true;
			}

			if (node.Error == null)
			{
				if (method.ContainsGenericParameters)
				{
					node.Error = "Generic test methods are not supported.";
				}
				else if (IsAsyncVoid(method))
				{
					node.Error = "async void tests cannot be awaited: return Task.";
				}
			}

			var cases = new List<CaseInfo>();
			foreach (TestCaseAttribute attribute in method.GetCustomAttributes<TestCaseAttribute>(true))
			{
				cases.Add(new CaseInfo
				{
					Arguments = attribute.Arguments,
					Name = attribute.TestName,
					Description = attribute.Description,
					Categories = Split(attribute.Category).ToList(),
					Ignore = attribute.Ignore,
					Explicit = attribute.Explicit,
					HasExpectedResult = attribute.HasExpectedResult,
					ExpectedResult = attribute.ExpectedResult
				});
			}

			foreach (TestCaseSourceAttribute attribute in method.GetCustomAttributes<TestCaseSourceAttribute>(true))
			{
				try
				{
					foreach (object item in ReadSource(attribute.SourceType ?? fixture.FixtureType, attribute.SourceName))
					{
						CaseInfo info = item switch
						{
							TestCaseData data => new CaseInfo
							{
								Arguments = data.Arguments, Name = data.TestName, Description = data.Description, Categories = data.Categories.ToList(),
								Ignore = data.IgnoreReason, Explicit = data.IsExplicit, HasExpectedResult = data.HasExpectedResult, ExpectedResult = data.ExpectedResult
							},
							object[] arguments when method.GetParameters().Length != 1 || method.GetParameters()[0].ParameterType != typeof(object[]) => new CaseInfo { Arguments = arguments },
							_ => new CaseInfo { Arguments = new[] { item } }
						};
						info.Categories ??= new List<string>();
						info.Categories.AddRange(Split(attribute.Category));
						cases.Add(info);
					}
				}
				catch (Exception e)
				{
					Exception cause = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
					new TestNode(TestNodeKind.Case, $"{attribute.SourceName} (source failed)", node.FullName + "{" + attribute.SourceName + "}", node)
					{
						FixtureType = fixture.FixtureType, Method = method, Arguments = Array.Empty<object>(), SourceFile = node.SourceFile, SourceLine = node.SourceLine,
						Error = $"[TestCaseSource(\"{attribute.SourceName}\")] failed: {cause.GetType().Name}: {cause.Message}"
					};
				}
			}

			if (cases.Count == 0)
			{
				if (node.Error == null && method.GetParameters().Any(p => !p.HasDefaultValue && !p.IsDefined(typeof(ParamArrayAttribute))))
				{
					node.Error = "A test with parameters needs [TestCase(...)] or [TestCaseSource(...)] for its arguments.";
				}

				if (node.Error == null)
				{
					node.Arguments = ConvertArguments(method, Array.Empty<object>(), out string error);
					node.Error = error;
				}

				return;
			}

			var names = new HashSet<string>(StringComparer.Ordinal);
			foreach (CaseInfo info in cases)
			{
				string name = info.Name ?? method.Name + "(" + string.Join(", ", info.Arguments.Select(CaseArgument)) + ")";
				// Two cases with the same text (rare: equal arguments) stay apart.
				string unique = name;
				for (int n = 2; !names.Add(unique); n++)
				{
					unique = name + " #" + n;
				}

				var testCase = new TestNode(TestNodeKind.Case, unique, fixture.FullName + "." + unique, node)
				{
					FixtureType = fixture.FixtureType,
					Method = method,
					Description = info.Description ?? node.Description,
					IgnoreReason = node.IgnoreReason ?? info.Ignore,
					IsExplicit = info.Explicit,
					TimeoutMilliseconds = node.TimeoutMilliseconds,
					SourceFile = node.SourceFile,
					SourceLine = node.SourceLine,
					HasExpectedResult = info.HasExpectedResult || node.HasExpectedResult,
					ExpectedResult = info.HasExpectedResult ? info.ExpectedResult : node.ExpectedResult,
					Error = node.Error
				};
				AddCategories(testCase.Categories, node.Categories);
				AddCategories(testCase.Categories, info.Categories);
				if (testCase.Error == null)
				{
					testCase.Arguments = ConvertArguments(method, info.Arguments, out string error);
					testCase.Error = error;
				}

				testCase.Arguments ??= info.Arguments;
			}
		}

		private static IEnumerable ReadSource(Type type, string name)
		{
			const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
			object value;
			FieldInfo field = type.GetField(name, flags);
			PropertyInfo property = field == null ? type.GetProperty(name, flags) : null;
			MethodInfo method = field == null && property == null ? type.GetMethod(name, flags, null, Type.EmptyTypes, null) : null;
			if (field != null)
			{
				value = field.GetValue(null);
			}
			else if (property != null)
			{
				value = property.GetValue(null);
			}
			else if (method != null)
			{
				value = method.Invoke(null, null);
			}
			else
			{
				throw new MissingMemberException($"{type.Name} has no static field, property or method without parameters named {name}.");
			}

			return value as IEnumerable ?? throw new InvalidCastException($"{type.Name}.{name} is not a sequence.");
		}

		/// <summary>The case's arguments for the method's parameters: converted, defaults filled in, a params array packed.</summary>
		private static object[] ConvertArguments(MethodInfo method, object[] arguments, out string error)
		{
			error = null;
			ParameterInfo[] parameters = method.GetParameters();
			var result = new object[parameters.Length];
			bool packs = parameters.Length > 0 && parameters[^1].IsDefined(typeof(ParamArrayAttribute)) &&
				!(arguments.Length == parameters.Length && (arguments[^1] == null || parameters[^1].ParameterType.IsInstanceOfType(arguments[^1])));
			int fixedCount = packs ? parameters.Length - 1 : parameters.Length;
			if (arguments.Length > fixedCount && !packs)
			{
				error = $"{method.Name} takes {parameters.Length} argument{(parameters.Length == 1 ? "" : "s")}, the case gives {arguments.Length}.";
				return null;
			}

			try
			{
				for (int i = 0; i < fixedCount; i++)
				{
					if (i < arguments.Length)
					{
						result[i] = ConvertArgument(arguments[i], parameters[i].ParameterType);
					}
					else if (parameters[i].HasDefaultValue)
					{
						result[i] = parameters[i].DefaultValue;
					}
					else
					{
						error = $"{method.Name} takes {parameters.Length} argument{(parameters.Length == 1 ? "" : "s")}, the case gives {arguments.Length}.";
						return null;
					}
				}

				if (packs)
				{
					Type element = parameters[^1].ParameterType.GetElementType();
					int count = Math.Max(0, arguments.Length - fixedCount);
					var array = Array.CreateInstance(element, count);
					for (int i = 0; i < count; i++)
					{
						array.SetValue(ConvertArgument(arguments[fixedCount + i], element), i);
					}

					result[^1] = array;
				}
			}
			catch (Exception e)
			{
				error = "Arguments do not fit the parameters: " + e.Message;
				return null;
			}

			return result;
		}

		private static object ConvertArgument(object value, Type target)
		{
			if (value == null)
			{
				if (target.IsValueType && Nullable.GetUnderlyingType(target) == null)
				{
					throw new ArgumentException($"null for a {target.Name} parameter");
				}

				return null;
			}

			if (target.IsInstanceOfType(value))
			{
				return value;
			}

			Type type = Nullable.GetUnderlyingType(target) ?? target;
			if (type.IsEnum)
			{
				return value is string name ? Enum.Parse(type, name) : Enum.ToObject(type, value);
			}

			if (type == typeof(TimeSpan) && value is string span)
			{
				return TimeSpan.Parse(span, CultureInfo.InvariantCulture);
			}

			if (type == typeof(DateTime) && value is string date)
			{
				return DateTime.Parse(date, CultureInfo.InvariantCulture);
			}

			if (type == typeof(decimal) && value is string number)
			{
				return decimal.Parse(number, CultureInfo.InvariantCulture);
			}

			if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(type))
			{
				return Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
			}

			throw new ArgumentException($"{TestFormat.Value(value)} for a {type.Name} parameter");
		}

		private static string CaseArgument(object value)
		{
			string text = TestFormat.Value(value);
			return text.Length > 40 ? text.Substring(0, 37) + "..." : text;
		}

		private static bool IsAsyncVoid(MethodInfo method)
		{
			return method.ReturnType == typeof(void) && method.IsDefined(typeof(AsyncStateMachineAttribute), false);
		}

		private static void AddCategories(List<string> list, IEnumerable<string> categories)
		{
			foreach (string category in categories)
			{
				if (!string.IsNullOrWhiteSpace(category) && !list.Contains(category.Trim()))
				{
					list.Add(category.Trim());
				}
			}
		}

		private static IEnumerable<string> Split(string categories)
		{
			return string.IsNullOrEmpty(categories) ? Array.Empty<string>() : categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		}

		/// <summary>A nested class as Outer.Inner.</summary>
		private static string DisplayName(Type type)
		{
			return type.DeclaringType != null ? DisplayName(type.DeclaringType) + "." + type.Name : type.Name;
		}

		private static void Sort(TestNode node)
		{
			if (node.Kind == TestNodeKind.Root || node.Kind == TestNodeKind.Namespace)
			{
				// Namespaces before classes, each by name.
				node.Children.Sort((a, b) => a.Kind != b.Kind ? (a.Kind == TestNodeKind.Namespace ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
			}
			else if (node.Kind == TestNodeKind.Fixture)
			{
				// Source order (the file, then the line); methods of other files (base classes) after.
				List<TestNode> ordered = node.Children
					.OrderBy(c => c.SourceFile == node.SourceFile ? 0 : 1)
					.ThenBy(c => c.SourceFile ?? "", StringComparer.OrdinalIgnoreCase)
					.ThenBy(c => c.SourceLine)
					.ThenBy(c => c.Method?.MetadataToken ?? 0)
					.ToList();
				node.Children.Clear();
				node.Children.AddRange(ordered);
			}

			foreach (TestNode child in node.Children)
			{
				Sort(child);
			}
		}

		#endregion

		#region NestedTypes

		private sealed class CaseInfo
		{
			public object[] Arguments;
			public string Name;
			public string Description;
			public List<string> Categories;
			public string Ignore;
			public bool Explicit;
			public bool HasExpectedResult;
			public object ExpectedResult;
		}

		/// <summary>The first source line of a method from the assembly's portable PDB (async and iterator methods: their state machine's).</summary>
		private sealed class SourceLocator : IDisposable
		{
			private readonly Assembly assembly;
			private readonly MetadataReaderProvider provider;
			private readonly MetadataReader reader;

			public SourceLocator(Assembly assembly, byte[] symbols)
			{
				this.assembly = assembly;
				if (symbols == null)
				{
					return;
				}

				try
				{
					provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(symbols));
					reader = provider.GetMetadataReader();
				}
				catch (Exception)
				{
					// Not a portable PDB: no source lines.
					provider?.Dispose();
					provider = null;
					reader = null;
				}
			}

			public (string file, int line) Find(MethodInfo method)
			{
				if (reader == null || method.Module.Assembly != assembly)
				{
					return (null, 0);
				}

				try
				{
					MethodBase body = method;
					var stateMachine = method.GetCustomAttribute<StateMachineAttribute>(false);
					if (stateMachine != null)
					{
						body = stateMachine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ?? body;
					}

					MethodDebugInformation information = reader.GetMethodDebugInformation(MetadataTokens.MethodDefinitionHandle(body.MetadataToken & 0xFFFFFF));
					foreach (SequencePoint point in information.GetSequencePoints())
					{
						if (!point.IsHidden)
						{
							return (reader.GetString(reader.GetDocument(point.Document).Name), point.StartLine);
						}
					}
				}
				catch (Exception)
				{
					// Symbols that do not match the assembly: no line.
				}

				return (null, 0);
			}

			public void Dispose()
			{
				provider?.Dispose();
			}
		}

		#endregion
	}
}
