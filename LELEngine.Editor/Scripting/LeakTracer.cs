using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using LELEngine.UIElements;

namespace LELEngine.Editor
{
	/// <summary>
	///     Finds what keeps an unloaded script assembly alive: a breadth-first walk over the objects reachable from the
	///     static fields of the engine and the assemblies built on it (the editor; the closed generic base classes of
	///     their types included: pools such as <c>EventBase&lt;PointerMoveEvent&gt;.pool</c>), from extra roots (the
	///     editor instance, through which the libraries' objects are reached) and from the runtime's process-wide hooks
	///     (AppContext events and data, assembly resolve events, timers, queued thread pool work, the main thread's async
	///     locals). The walk stops at every object, type, member or delegate of the old assembly; a reverse walk from
	///     those then gives every root that reaches one its own shortest path. Thread stacks, GC handles, native
	///     callbacks and the libraries' own static fields are not visible to it (reading a library's static field could
	///     run a static constructor that is not ready to run). Main thread: reading an engine class's static field may
	///     run its static constructor.
	/// </summary>
	internal static class LeakTracer
	{
		#region PublicFields

		/// <summary>The outcome of a trace.</summary>
		internal sealed class Report
		{
			/// <summary>"Root.field[i] (Label).field -> what": the shortest path of each root that reaches the old scripts, shortest first.</summary>
			public readonly List<string> Paths = new List<string>();

			/// <summary>Objects, types, members and delegates of the old scripts reached (the walk stops at each).</summary>
			public int Found;

			/// <summary>Roots that reach the old scripts (more than <see cref="Paths" /> when the report was capped).</summary>
			public int Roots;

			public int Visited;
			public bool Stopped;
			public double Milliseconds;
		}

		#endregion

		#region PublicMethods

		/// <summary>Walks the roots and reports the paths to the assemblies of <paramref name="old" /> (at most <paramref name="maxPaths" />).</summary>
		public static Report Trace(AssemblyLoadContext old, IEnumerable<KeyValuePair<string, object>> extraRoots, int maxPaths = 8, double budgetMs = 3000)
		{
			var walker = new Walker(old, maxPaths, budgetMs);
			walker.AddStaticRoots();
			foreach (KeyValuePair<string, object> root in extraRoots)
			{
				walker.AddRoot(root.Key, root.Value);
			}

			walker.AddRuntimeRoots();
			walker.Run();
			return walker.Result;
		}

		#endregion

		#region NestedTypes

		private sealed class Walker
		{
			#region PublicFields

			public readonly Report Result = new Report();

			#endregion

			#region PrivateFields

			private enum Edge : byte
			{
				Root,
				Field,
				Index,
				Key,
				Value,
				Target
			}

			/// <summary>One object reached: how (the edge from its parent in the walk) and from which root.</summary>
			private struct Node
			{
				public object Value;
				public int Parent;
				public int Root;
				public Edge Kind;
				public int Index;

				/// <summary>The field (Field) or the dictionary key (Key, Value).</summary>
				public object Label;
			}

			/// <summary>A reference to an object reached before (the walk keeps one parent per object; these are the others).</summary>
			private struct Link
			{
				public int From;
				public int To;
				public Edge Kind;
				public int Index;
				public object Label;
			}

			private const int MaxNodes = 8_000_000;
			private const int MaxLinks = 16_000_000;

			// Runtime classes holding process-wide hooks scripts subscribe to or fill (looked up by name: they are internal).
			private static readonly string[] RuntimeRootTypes =
			{
				"System.AppContext",
				"System.AppDomain",
				"System.Runtime.Loader.AssemblyLoadContext",
				"System.Threading.Tasks.TaskScheduler",
				"System.Threading.TimerQueue",
				"System.Threading.ThreadPool",
				"System.Threading.ThreadPoolWorkQueue",
				"System.Threading.ThreadPoolWorkQueue+WorkStealingQueueList"
			};

			private readonly AssemblyLoadContext old;
			private readonly HashSet<Assembly> oldAssemblies = new HashSet<Assembly>();
			private readonly int maxPaths;
			private readonly double budgetMs;
			private readonly Stopwatch watch = Stopwatch.StartNew();
			private readonly List<Node> nodes = new List<Node>(1 << 16);
			private readonly List<Link> links = new List<Link>();
			private readonly Dictionary<object, int> nodeOf = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
			private readonly List<string> rootNames = new List<string>();
			private readonly List<int> rootNodes = new List<int>();

			// Objects of the old scripts reached (node -> what it is); the walk does not go past them.
			private readonly Dictionary<int, string> oldNodes = new Dictionary<int, string>();

			private readonly Dictionary<Type, bool> oldTypes = new Dictionary<Type, bool>();
			private readonly Dictionary<Type, FieldInfo[]> fields = new Dictionary<Type, FieldInfo[]>();
			private readonly Dictionary<Type, bool> structReferences = new Dictionary<Type, bool>();

			#endregion

			#region Constructors

			public Walker(AssemblyLoadContext old, int maxPaths, double budgetMs)
			{
				this.old = old;
				this.maxPaths = maxPaths;
				this.budgetMs = budgetMs;
				foreach (Assembly assembly in old.Assemblies)
				{
					oldAssemblies.Add(assembly);
				}
			}

			#endregion

			#region PublicMethods

			/// <summary>The static fields of the engine and the assemblies built on it (the editor), not of the scripts.</summary>
			public void AddStaticRoots()
			{
				var types = new List<Type>();
				var closedBases = new HashSet<Type>();
				foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (!IsEngineAssembly(assembly))
					{
						continue;
					}

					foreach (Type type in TypesOf(assembly))
					{
						if (type == null || type.ContainsGenericParameters)
						{
							// Open generics keep their statics per closed type: found below through the classes deriving from them.
							continue;
						}

						types.Add(type);
						for (Type parent = type.BaseType; parent != null; parent = parent.BaseType)
						{
							if (parent.IsGenericType && !parent.ContainsGenericParameters && IsEngineAssembly(parent.Assembly))
							{
								closedBases.Add(parent);
							}
						}
					}
				}

				types.AddRange(closedBases);
				foreach (Type type in types)
				{
					AddStatics(type);
				}
			}

			/// <summary>A named root: its node is its own even when another root reached the object first (both are reported).</summary>
			public void AddRoot(string name, object value)
			{
				if (value == null || value is string || value is Pointer)
				{
					return;
				}

				if (value.GetType().IsValueType && !HoldsReferences(value.GetType()))
				{
					return;
				}

				rootNames.Add(name);
				rootNodes.Add(nodes.Count);
				nodeOf.TryAdd(value, nodes.Count);
				nodes.Add(new Node { Value = value, Parent = -1, Root = rootNames.Count - 1, Kind = Edge.Root });
			}

			/// <summary>Process-wide hooks of the runtime that outlive any script: events, data store, timers, queued work.</summary>
			public void AddRuntimeRoots()
			{
				Assembly core = typeof(object).Assembly;
				foreach (string name in RuntimeRootTypes)
				{
					Type type = core.GetType(name, false);
					if (type != null)
					{
						AddStatics(type);
					}
				}

				AddStatics(typeof(Console));
				AddRoot("AssemblyLoadContext.Default", AssemblyLoadContext.Default);
				// Async locals and the synchronization context of the main thread.
				AddRoot("main thread", Thread.CurrentThread);
			}

			/// <summary>Breadth first over everything reachable, then the paths from each root to the nearest old object.</summary>
			public void Run()
			{
				for (int i = 0; i < nodes.Count; i++)
				{
					if ((i & 1023) == 0 && (watch.Elapsed.TotalMilliseconds > budgetMs || nodes.Count > MaxNodes))
					{
						Result.Stopped = true;
						break;
					}

					Visit(i);
				}

				Result.Visited = nodes.Count;
				Result.Found = oldNodes.Count;
				BuildPaths();
				Result.Milliseconds = watch.Elapsed.TotalMilliseconds;
			}

			#endregion

			#region PrivateMethods

			private void AddStatics(Type type)
			{
				FieldInfo[] statics;
				try
				{
					statics = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				}
				catch (Exception)
				{
					return;
				}

				foreach (FieldInfo field in statics)
				{
					if (field.IsLiteral || !CanHoldReference(field.FieldType))
					{
						continue;
					}

					object value;
					try
					{
						value = field.GetValue(null);
					}
					catch (Exception)
					{
						continue;
					}

					bool threadStatic = field.IsDefined(typeof(ThreadStaticAttribute), false);
					AddRoot(Name(type) + "." + FieldName(field) + (threadStatic ? " (main thread)" : ""), value);
				}
			}

			private void Enqueue(object value, int parent, Edge kind, int index, object label)
			{
				if (value == null || value is string || value is Pointer)
				{
					return;
				}

				Type type = value.GetType();
				if (type.IsValueType)
				{
					// A boxed struct: a copy, walked for the references it holds (it cannot hold itself).
					if (!HoldsReferences(type))
					{
						return;
					}
				}
				else if (nodeOf.TryGetValue(value, out int reached))
				{
					if (links.Count < MaxLinks)
					{
						links.Add(new Link { From = parent, To = reached, Kind = kind, Index = index, Label = label });
					}

					return;
				}
				else
				{
					nodeOf[value] = nodes.Count;
				}

				nodes.Add(new Node { Value = value, Parent = parent, Root = nodes[parent].Root, Kind = kind, Index = index, Label = label });
			}

			private void Visit(int index)
			{
				object value = nodes[index].Value;
				string what = DescribeOld(value);
				if (what != null)
				{
					oldNodes[index] = what;
					return;
				}

				if (value is Type || value is MemberInfo || value is Assembly || value is Module || value is ParameterInfo)
				{
					// Reflection objects of the engine and the framework: their caches are not followed.
					return;
				}

				try
				{
					Expand(index, value);
				}
				catch (Exception)
				{
					// A getter threw or a collection changed: this object's references stay unknown.
				}
			}

			private void Expand(int index, object value)
			{
				switch (value)
				{
					case Delegate handler:
						ExpandDelegate(index, handler);
						return;
					case Array array:
						ExpandArray(index, array);
						return;
				}

				Type type = value.GetType();
				if (IsPlainCollection(type))
				{
					// The framework's ordinary collections read by key / index (the concurrent ones only through their fields:
					// enumerating a ConcurrentQueue keeps the items it dequeues afterwards).
					if (value is IDictionary dictionary)
					{
						foreach (DictionaryEntry entry in dictionary)
						{
							Enqueue(entry.Key, index, Edge.Key, 0, entry.Key);
							Enqueue(entry.Value, index, Edge.Value, 0, entry.Key);
						}

						return;
					}

					if (value is IList list)
					{
						for (int i = 0; i < list.Count; i++)
						{
							Enqueue(list[i], index, Edge.Index, i, null);
						}

						return;
					}
				}

				foreach (FieldInfo field in FieldsOf(type))
				{
					Enqueue(field.GetValue(value), index, Edge.Field, 0, field);
				}
			}

			/// <summary>A delegate with a handler of the old scripts is where the path ends; the others lead on to their targets.</summary>
			private void ExpandDelegate(int index, Delegate handler)
			{
				Delegate[] handlers = handler.GetInvocationList();
				var oldHandlers = new List<string>();
				foreach (Delegate single in handlers)
				{
					try
					{
						MethodInfo method = single.Method;
						if (IsOld(method.DeclaringType))
						{
							oldHandlers.Add(MethodName(method));
						}
					}
					catch (Exception)
					{
						// A delegate without metadata (dynamic method): only its target is followed.
					}
				}

				if (oldHandlers.Count > 0)
				{
					oldNodes[index] = "handler " + string.Join(", ", oldHandlers);
					return;
				}

				for (int i = 0; i < handlers.Length; i++)
				{
					Enqueue(handlers[i].Target, index, Edge.Target, handlers.Length > 1 ? i : -1, null);
				}
			}

			private void ExpandArray(int index, Array array)
			{
				if (!CanHoldReference(array.GetType().GetElementType()))
				{
					return;
				}

				if (array.Rank == 1)
				{
					if (array is object[] objects)
					{
						for (int i = 0; i < objects.Length; i++)
						{
							Enqueue(objects[i], index, Edge.Index, i, null);
						}

						return;
					}

					// Structs holding references (dictionary entries): boxed copies.
					for (int i = 0; i < array.Length; i++)
					{
						Enqueue(array.GetValue(i), index, Edge.Index, i, null);
					}

					return;
				}

				int n = 0;
				foreach (object item in array)
				{
					Enqueue(item, index, Edge.Index, n++, null);
				}
			}

			/// <summary>
			///     Breadth first backwards from the old objects over every reference seen (the walk's parent links and the
			///     others): each object learns its next step on a shortest way down to one; every root that has a way gets a path.
			/// </summary>
			private void BuildPaths()
			{
				int count = nodes.Count;
				var next = new int[count];
				var nextLink = new int[count];
				var reached = new bool[count];
				var linksTo = new int[count];
				var nextLinkTo = new int[links.Count];
				Array.Fill(next, -1);
				Array.Fill(nextLink, -1);
				Array.Fill(linksTo, -1);
				for (int l = 0; l < links.Count; l++)
				{
					int to = links[l].To;
					nextLinkTo[l] = linksTo[to];
					linksTo[to] = l;
				}

				var queue = new Queue<int>();
				foreach (int node in oldNodes.Keys)
				{
					reached[node] = true;
					queue.Enqueue(node);
				}

				while (queue.Count > 0)
				{
					int target = queue.Dequeue();
					int parent = nodes[target].Parent;
					if (parent >= 0 && !reached[parent])
					{
						reached[parent] = true;
						next[parent] = target;
						queue.Enqueue(parent);
					}

					for (int l = linksTo[target]; l >= 0; l = nextLinkTo[l])
					{
						int from = links[l].From;
						if (!reached[from])
						{
							reached[from] = true;
							next[from] = target;
							nextLink[from] = l;
							queue.Enqueue(from);
						}
					}
				}

				var found = new List<(int steps, string path)>();
				foreach (int root in rootNodes)
				{
					if (reached[root])
					{
						found.Add(FormatPath(root, next, nextLink));
					}
				}

				found.Sort((a, b) => a.steps != b.steps ? a.steps.CompareTo(b.steps) : string.CompareOrdinal(a.path, b.path));
				Result.Roots = found.Count;
				for (int i = 0; i < found.Count && i < maxPaths; i++)
				{
					Result.Paths.Add(found[i].path);
				}
			}

			private (int steps, string path) FormatPath(int root, int[] next, int[] nextLink)
			{
				var text = new StringBuilder(rootNames[nodes[root].Root]);
				int steps = 0;
				int current = root;
				while (!oldNodes.ContainsKey(current))
				{
					int to = next[current];
					int link = nextLink[current];
					if (link >= 0)
					{
						AppendEdge(text, links[link].Kind, links[link].Index, links[link].Label);
					}
					else
					{
						AppendEdge(text, nodes[to].Kind, nodes[to].Index, nodes[to].Label);
					}

					// The last object is what the end of the path describes; the others get a label when they have a name.
					string label = oldNodes.ContainsKey(to) ? null : Label(nodes[to].Value);
					if (label != null)
					{
						text.Append(" (").Append(label).Append(')');
					}

					current = to;
					steps++;
				}

				// ASCII arrow: logs and the editor console's font.
				text.Append(" -> ").Append(oldNodes[current]);
				return (steps, text.ToString());
			}

			private static void AppendEdge(StringBuilder text, Edge kind, int index, object label)
			{
				switch (kind)
				{
					case Edge.Field:
						text.Append('.').Append(FieldName((FieldInfo)label));
						break;
					case Edge.Index:
						text.Append('[').Append(index).Append(']');
						break;
					case Edge.Key:
						text.Append('{').Append(FormatKey(label)).Append('}');
						break;
					case Edge.Value:
						text.Append('[').Append(FormatKey(label)).Append(']');
						break;
					case Edge.Target:
						text.Append(index >= 0 ? "[" + index + "]" : "").Append(".Target");
						break;
				}
			}

			/// <summary>What an object of the old scripts is ("AsyncProbe on 'Async'", "Type WeaponConfig"); null for anything else.</summary>
			private string DescribeOld(object value)
			{
				switch (value)
				{
					case Type type:
						return IsOld(type) ? "Type " + Name(type) : null;
					case MemberInfo member:
						return IsOld(member.DeclaringType) ? member.MemberType + " " + Name(member.DeclaringType) + "." + member.Name : null;
					case Assembly assembly:
						return oldAssemblies.Contains(assembly) ? "Assembly " + assembly.GetName().Name : null;
					case Module module:
						return oldAssemblies.Contains(module.Assembly) ? "Module " + module.Name : null;
					case AssemblyLoadContext context:
						return ReferenceEquals(context, old) ? "the old load context itself" : null;
				}

				Type objectType = value.GetType();
				if (!IsOld(objectType))
				{
					return null;
				}

				try
				{
					switch (value)
					{
						case Behaviour behaviour:
							return Name(objectType) + " on '" + behaviour.gameObject?.Name + "'";
						case ScriptableObject asset:
							return Name(objectType) + " '" + asset.name + "'";
					}
				}
				catch (Exception)
				{
					// A destroyed object: its type says enough.
				}

				return Name(objectType);
			}

			/// <summary>Defined in an old script assembly, or built from such types (List&lt;OldType&gt;, OldType[]).</summary>
			private bool IsOld(Type type)
			{
				if (type == null || !type.IsCollectible)
				{
					return false;
				}

				if (oldTypes.TryGetValue(type, out bool known))
				{
					return known;
				}

				bool result = oldAssemblies.Contains(type.Assembly);
				if (!result && type.HasElementType)
				{
					result = IsOld(type.GetElementType());
				}

				if (!result && type.IsGenericType)
				{
					foreach (Type argument in type.GetGenericArguments())
					{
						if (IsOld(argument))
						{
							result = true;
							break;
						}
					}
				}

				oldTypes[type] = result;
				return result;
			}

			/// <summary>The instance fields (base classes included) that can hold a reference.</summary>
			private FieldInfo[] FieldsOf(Type type)
			{
				if (fields.TryGetValue(type, out FieldInfo[] known))
				{
					return known;
				}

				var list = new List<FieldInfo>();
				for (Type level = type; level != null && level != typeof(object); level = level.BaseType)
				{
					foreach (FieldInfo field in level.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						if (CanHoldReference(field.FieldType))
						{
							list.Add(field);
						}
					}
				}

				known = list.ToArray();
				fields[type] = known;
				return known;
			}

			private bool CanHoldReference(Type type)
			{
				if (type == null || type.IsPointer || type.IsByRef || type.IsFunctionPointer)
				{
					return false;
				}

				return !type.IsValueType || HoldsReferences(type);
			}

			private bool HoldsReferences(Type structType)
			{
				if (structType.IsPrimitive || structType.IsEnum || structType == typeof(decimal))
				{
					return false;
				}

				if (structReferences.TryGetValue(structType, out bool known))
				{
					return known;
				}

				// Guard against recursion through generic structs while this one is examined.
				structReferences[structType] = false;
				bool result = false;
				foreach (FieldInfo field in structType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (CanHoldReference(field.FieldType))
					{
						result = true;
						break;
					}
				}

				structReferences[structType] = result;
				return result;
			}

			/// <summary>The engine, or an assembly of the editor's process built on it (not the scripts: they are collectible).</summary>
			private static bool IsEngineAssembly(Assembly assembly)
			{
				if (assembly.IsDynamic || assembly.IsCollectible || AssemblyLoadContext.GetLoadContext(assembly) != AssemblyLoadContext.Default)
				{
					return false;
				}

				Assembly engine = typeof(GameObject).Assembly;
				if (assembly == engine)
				{
					return true;
				}

				string engineName = engine.GetName().Name;
				foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
				{
					if (reference.Name == engineName)
					{
						return true;
					}
				}

				return false;
			}

			private static Type[] TypesOf(Assembly assembly)
			{
				try
				{
					return assembly.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					return e.Types;
				}
				catch (Exception)
				{
					return Array.Empty<Type>();
				}
			}

			private static bool IsPlainCollection(Type type)
			{
				string space = type.Namespace;
				return !type.IsValueType && (space == "System.Collections.Generic" || space == "System.Collections.ObjectModel" || space == "System.Collections");
			}

			/// <summary>A name for an object on the way ("GameObject 'Player'", "UIRenderPass"); null for collections (the index says enough).</summary>
			private static string Label(object value)
			{
				try
				{
					switch (value)
					{
						case GameObject go:
							return "GameObject '" + go.Name + "'";
						case Behaviour behaviour:
							return Name(behaviour.GetType()) + " on '" + behaviour.gameObject?.Name + "'";
						case Scene scene:
							return "Scene '" + scene.Name + "'";
						case ScriptableObject asset:
							return Name(asset.GetType()) + " '" + asset.name + "'";
						case VisualElement element:
							return Name(element.GetType()) + (string.IsNullOrEmpty(element.name) ? "" : " '" + element.name + "'");
					}
				}
				catch (Exception)
				{
					// A destroyed object: its type is shown.
				}

				Type type = value.GetType();
				return type.IsArray || type.IsValueType || IsPlainCollection(type) ? null : Name(type);
			}

			private static string FormatKey(object key)
			{
				switch (key)
				{
					case null:
						return "null";
					case string text:
						return "\"" + (text.Length > 40 ? text.Substring(0, 40) + "..." : text) + "\"";
					case Type type:
						return Name(type);
				}

				Type keyType = key.GetType();
				if (keyType.IsPrimitive || keyType.IsEnum || key is Guid || key is decimal)
				{
					return Convert.ToString(key, CultureInfo.InvariantCulture);
				}

				return Label(key) ?? Name(keyType);
			}

			private static string FieldName(FieldInfo field)
			{
				string name = field.Name;
				// Auto-property backing fields: "<Name>k__BackingField".
				int end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
				return name.StartsWith("<", StringComparison.Ordinal) && end > 1 ? name.Substring(1, end - 1) : name;
			}

			private static string MethodName(MethodInfo method)
			{
				string name = method.Name;
				if (name.StartsWith("<", StringComparison.Ordinal))
				{
					// Lambdas and local functions: "<Start>b__3_0".
					int end = name.IndexOf('>');
					name = end > 1 ? "lambda in " + name.Substring(1, end - 1) : name;
				}

				// A lambda lives in a compiler-generated class (its captures or the cache of lambdas): named by the class it is written in.
				Type owner = method.DeclaringType;
				while (owner != null && owner.Name.StartsWith("<", StringComparison.Ordinal) && owner.DeclaringType != null)
				{
					owner = owner.DeclaringType;
				}

				return Name(owner) + "." + name;
			}

			/// <summary>A short readable type name: generic arguments, closures and state machines by their method.</summary>
			private static string Name(Type type)
			{
				if (type == null)
				{
					return "?";
				}

				if (type.IsArray)
				{
					return Name(type.GetElementType()) + "[]";
				}

				string name = type.Name;
				if (name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType != null)
				{
					// Compiler-generated: lambda caches, closures, state machines of async methods and iterators.
					string owner = Name(type.DeclaringType);
					if (name == "<>c")
					{
						return "lambdas of " + owner;
					}

					if (name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal))
					{
						return "closure in " + owner;
					}

					int end = name.IndexOf('>');
					return end > 1 ? "state machine of " + owner + "." + name.Substring(1, end - 1) : owner + "." + name;
				}

				int tick = name.IndexOf('`');
				if (tick >= 0)
				{
					name = name.Substring(0, tick);
				}

				if (type.IsGenericType && !type.IsNested)
				{
					name += "<" + string.Join(", ", Array.ConvertAll(type.GetGenericArguments(), Name)) + ">";
				}

				return type.IsNested && !type.IsGenericParameter ? Name(type.DeclaringType) + "." + name : name;
			}

			#endregion
		}

		#endregion
	}
}
