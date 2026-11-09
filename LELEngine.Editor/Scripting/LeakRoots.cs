using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Diagnostics.Runtime;

namespace LELEngine.Editor
{
	/// <summary>
	///     What the GC itself says keeps an unloaded script assembly alive. A snapshot of the editor's own process (ClrMD
	///     over the runtime's DAC; <c>PssCaptureSnapshot</c> pauses the process only while it copies) is searched from
	///     every GC root to the objects of the old scripts' types (List&lt;OldType&gt; and OldType[] too) and to their
	///     loader allocator (which their types, methods and delegates keep alive). These are the roots
	///     <see cref="LeakTracer" /> cannot see: thread stacks (with the method holding the reference), GC handles by kind
	///     (strong, pinned, ref-counted: native callbacks, COM), the finalizer queue, and the static fields of every
	///     library. Runs on a worker thread: it reads the copy while the editor goes on.
	/// </summary>
	internal static class LeakRoots
	{
		#region PublicFields

		/// <summary>The outcome of a search.</summary>
		internal sealed class Report
		{
			/// <summary>"root: Object.field -> Object[i] -> ... -> old object", the shortest path of each root, shortest first.</summary>
			public readonly List<string> Paths = new List<string>();

			/// <summary>GC roots that reach the old scripts.</summary>
			public int Roots;

			/// <summary>Places they are in (a method's locals, a handle kind, the finalizer queue): one path each, more than <see cref="Paths" /> when capped.</summary>
			public int Places;

			/// <summary>Objects of the old scripts in the snapshot, counted only when no root reaches one (else -1).</summary>
			public int OldObjects = -1;

			/// <summary>Why nothing was searched (null when the search ran).</summary>
			public string Error;

			/// <summary>The search ran out of time and reports what it found until then.</summary>
			public bool Stopped;

			public double SnapshotMilliseconds;

			/// <summary>Frames the editor drew while the snapshot was taken (0 for a long snapshot: the process stood still).</summary>
			public long SnapshotFrames;

			public double Milliseconds;
		}

		#endregion

		#region PrivateFields

		/// <summary>Steps of a path shown at most (its start and its end; what holds the scene matters, not the scene's own links).</summary>
		private const int MaxSteps = 16;

		// Namespaces in type names ("System.Collections.Generic.List<LELEngine.Light>" -> "List<Light>").
		private static readonly Regex Namespaces = new Regex(@"\b[A-Za-z_][A-Za-z0-9_]*\.", RegexOptions.Compiled);

		/// <summary>A place roots hold the old scripts from: its shortest path and every kind of root there.</summary>
		private sealed class Place
		{
			public readonly List<string> Roots = new List<string>();
			public List<ulong> Chain;
		}

		/// <summary>
		///     "LightBuffer.giSeen" for the address of a static field's slot. The runtime keeps the static references of
		///     the types of a non-collectible assembly in object arrays a handle holds: a path from such a handle starts at
		///     a slot of the array, which these names turn into the field. Built on first use from every type loaded in
		///     the process (a few seconds at most; a capped time).
		/// </summary>
		private sealed class StaticNames
		{
			private readonly ClrRuntime runtime;
			private readonly int budgetMs;
			private Dictionary<ulong, string> slots;

			public StaticNames(ClrRuntime runtime, int budgetMs)
			{
				this.runtime = runtime;
				this.budgetMs = budgetMs;
			}

			public string Find(ulong slot)
			{
				if (slots == null)
				{
					Build();
				}

				return slots.TryGetValue(slot, out string name) ? name : null;
			}

			private void Build()
			{
				slots = new Dictionary<ulong, string>();
				var watch = Stopwatch.StartNew();
				ClrAppDomain domain = runtime.AppDomains.Length > 0 ? runtime.AppDomains[0] : runtime.SystemDomain;
				foreach (ClrModule module in runtime.EnumerateModules())
				{
					foreach (var entry in module.EnumerateTypeDefToMethodTableMap())
					{
						if (watch.ElapsedMilliseconds > budgetMs)
						{
							return;
						}

						ClrType type = runtime.GetTypeByMethodTable(entry.Item1);
						if (type == null || type.IsCollectible)
						{
							continue;
						}

						foreach (ClrStaticField field in type.StaticFields)
						{
							try
							{
								ulong address = field.GetAddress(domain);
								if (address != 0)
								{
									slots[address] = Short(type.Name) + "." + FieldName(field.Name);
								}
							}
							catch (Exception)
							{
								// A static the copy cannot place: left unnamed.
							}
						}
					}
				}
			}
		}

		#endregion

		#region PublicMethods

		/// <param name="oldTypes">The method table (<c>RuntimeTypeHandle.Value</c>) of a type of each old assembly: they name the old loader allocators.</param>
		/// <param name="captured">Called once the snapshot is taken (or could not be): the copy no longer changes, so other work may go on.</param>
		/// <param name="maxPaths">Roots reported with their path at most (the others are counted).</param>
		/// <param name="budgetMs">Time for the whole search (the paths found until then are reported).</param>
		public static Report Find(IReadOnlyList<IntPtr> oldTypes, Action captured = null, int maxPaths = 10, int budgetMs = 60000)
		{
			var report = new Report();
			var watch = Stopwatch.StartNew();
			bool told = false;

			void Captured()
			{
				if (!told)
				{
					told = true;
					captured?.Invoke();
				}
			}

			try
			{
				long frames = Time.frameCount;
				using DataTarget target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
				report.SnapshotMilliseconds = watch.Elapsed.TotalMilliseconds;
				report.SnapshotFrames = Time.frameCount - frames;
				Captured();
				if (target.ClrVersions.Length == 0)
				{
					report.Error = "no .NET runtime in the snapshot";
					return report;
				}

				using ClrRuntime runtime = target.ClrVersions[0].CreateRuntime();
				ClrHeap heap = runtime.Heap;
				if (!heap.CanWalkHeap)
				{
					report.Error = "the snapshot was taken during a GC (its heap cannot be walked)";
					return report;
				}

				// The old loader allocators: their handles (in every collectible type of them) and their managed objects.
				var allocators = new HashSet<ulong>();
				var allocatorObjects = new HashSet<ulong>();
				foreach (IntPtr handle in oldTypes)
				{
					ClrType type = runtime.GetTypeByMethodTable((ulong)handle);
					if (type == null || !type.IsCollectible || type.LoaderAllocatorHandle == 0)
					{
						continue;
					}

					allocators.Add(type.LoaderAllocatorHandle);
					ulong allocator = target.DataReader.ReadPointer(type.LoaderAllocatorHandle);
					if (allocator != 0)
					{
						allocatorObjects.Add(allocator);
					}
				}

				if (allocators.Count == 0)
				{
					report.Error = "no type of the old scripts in the snapshot";
					return report;
				}

				bool IsOld(ClrObject value)
				{
					return allocatorObjects.Contains(value.Address) || (value.Type is { IsCollectible: true } type && allocators.Contains(type.LoaderAllocatorHandle));
				}

				// What the old loader allocators hold themselves (the old scripts' own static fields): roots there go with them.
				HashSet<ulong> owned = OwnedBy(heap, allocatorObjects);

				// One line per place a root holds (its object and the next step: one static field, one local's object), with
				// the shortest path; every kind of root at that place is named on the line.
				var places = new Dictionary<(ulong, ulong), Place>();
				var threads = new Dictionary<int, string>();
				int left = Math.Max(1000, budgetMs - (int)watch.ElapsedMilliseconds);
				using (var cancel = new CancellationTokenSource(left))
				{
					var search = new GCRoot(heap, IsOld);
					try
					{
						foreach ((ClrRoot root, GCRoot.ChainLink path) in search.EnumerateRootPaths(cancel.Token))
						{
							if (!HoldsAlive(root) || owned.Contains(root.Object.Address))
							{
								continue;
							}

							report.Roots++;
							var chain = new List<ulong>();
							for (GCRoot.ChainLink link = path; link != null; link = link.Next)
							{
								chain.Add(link.Object);
							}

							(ulong, ulong) key = (chain[0], chain.Count > 1 ? chain[1] : 0);
							if (!places.TryGetValue(key, out Place place))
							{
								place = new Place { Chain = chain };
								places[key] = place;
							}
							else if (chain.Count < place.Chain.Count)
							{
								place.Chain = chain;
							}

							string from = DescribeRoot(root, threads);
							if (!place.Roots.Contains(from))
							{
								place.Roots.Add(from);
							}
						}
					}
					catch (OperationCanceledException)
					{
						report.Stopped = true;
					}
				}

				var found = new List<Place>(places.Values);
				report.Places = found.Count;
				found.Sort((a, b) => a.Chain.Count.CompareTo(b.Chain.Count));
				var statics = new StaticNames(runtime, Math.Max(500, budgetMs - (int)watch.ElapsedMilliseconds));
				for (int i = 0; i < found.Count && i < maxPaths; i++)
				{
					Place place = found[i];
					string path = FormatPath(runtime, heap, place.Chain, allocatorObjects, statics, out bool fromStatic);
					// A path from a static field starts with it: the handle holding the runtime's statics array (and a stack
					// that may point at the array) are how the runtime keeps statics, not news.
					string roots = place.Roots[0] + (place.Roots.Count > 1 ? " (also " + string.Join("; ", place.Roots.GetRange(1, place.Roots.Count - 1)) + ")" : "");
					report.Paths.Add(fromStatic ? path : roots + ": " + path);
				}

				if (report.Roots == 0)
				{
					// Nothing holds them: garbage the next GC takes, or gone already.
					report.OldObjects = 0;
					foreach (ClrObject value in heap.EnumerateObjects())
					{
						if (IsOld(value))
						{
							report.OldObjects++;
						}
					}
				}
			}
			catch (Exception e)
			{
				report.Error = e.GetType().Name + ": " + e.Message;
			}
			finally
			{
				Captured();
			}

			report.Milliseconds = watch.Elapsed.TotalMilliseconds;
			return report;
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     A root that keeps its object alive: strong, pinned, sized and dependent handles, ref-counted ones with refs,
		///     stacks, the finalizer queue, statics. Weak handles are not (ClrMD lists kinds it does not know, such as the
		///     runtime's weak interior pointers, as roots).
		/// </summary>
		private static bool HoldsAlive(ClrRoot root)
		{
			if (!(root is ClrHandle handle))
			{
				return true;
			}

			switch (handle.HandleKind)
			{
				case ClrHandleKind.Strong:
				case ClrHandleKind.Pinned:
				case ClrHandleKind.AsyncPinned:
				case ClrHandleKind.SizedRef:
				case ClrHandleKind.Dependent:
					return true;
				case ClrHandleKind.RefCounted:
					return handle.ReferenceCount > 0;
				default:
					return false;
			}
		}

		/// <summary>
		///     Objects the old loader allocators hold within a few steps (their handle table: the old scripts' static fields):
		///     a root there is the old scripts' own and goes with them.
		/// </summary>
		private static HashSet<ulong> OwnedBy(ClrHeap heap, HashSet<ulong> allocatorObjects)
		{
			var owned = new HashSet<ulong>();
			var level = new List<ulong>(allocatorObjects);
			for (int depth = 0; depth < 3 && level.Count > 0 && owned.Count < 100_000; depth++)
			{
				var next = new List<ulong>();
				foreach (ulong address in level)
				{
					foreach (ClrObject reference in heap.GetObject(address).EnumerateReferences(false, false))
					{
						if (owned.Add(reference.Address))
						{
							next.Add(reference.Address);
						}
					}
				}

				level = next;
			}

			return owned;
		}

		/// <summary>Where a root is: "stack of thread 12 (started in X.Run), in Y.Method", "strong GC handle", "finalizer queue".</summary>
		private static string DescribeRoot(ClrRoot root, Dictionary<int, string> threads)
		{
			switch (root)
			{
				case ClrStackRoot stack:
				{
					ClrStackFrame frame = stack.StackFrame;
					string method = frame?.Method != null ? MethodName(frame.Method) : frame?.FrameName ?? "a frame without a method";
					return "stack of " + ThreadName(frame?.Thread, threads) + ", in " + method;
				}
				case ClrHandle handle:
					switch (handle.HandleKind)
					{
						case ClrHandleKind.Strong:
							return "strong GC handle (GCHandle.Alloc: a native callback's target, a library's static)";
						case ClrHandleKind.Pinned:
							return "pinned GC handle (pinned memory, or a type's static fields)";
						case ClrHandleKind.RefCounted:
							return "ref-counted handle (an object native code holds through COM, " + handle.ReferenceCount + " refs)";
						case ClrHandleKind.Dependent:
							return "dependent handle (ConditionalWeakTable) of " + Short(handle.Object.Type?.Name);
						default:
							return handle.HandleKind + " GC handle";
					}
			}

			switch (root.RootKind)
			{
				case ClrRootKind.FinalizerQueue:
					return "finalizer queue (an object waiting for its finalizer)";
				case ClrRootKind.StaticVar:
					return "static field";
				case ClrRootKind.ThreadStaticVar:
					return "thread-static field";
				default:
					return root.RootKind.ToString();
			}
		}

		/// <summary>"thread 12 (started in AudioOutput.Run)"; the finalizer thread by name.</summary>
		private static string ThreadName(ClrThread thread, Dictionary<int, string> names)
		{
			if (thread == null)
			{
				return "a thread";
			}

			if (names.TryGetValue(thread.ManagedThreadId, out string known))
			{
				return known;
			}

			string start = null;
			try
			{
				// Innermost frame first: the last one with a method is where the thread started.
				foreach (ClrStackFrame frame in thread.EnumerateStackTrace(false))
				{
					if (frame.Method != null)
					{
						start = MethodName(frame.Method);
					}
				}
			}
			catch (Exception)
			{
				// No stack walk: the thread's id alone.
			}

			string text = "thread " + thread.ManagedThreadId + (thread.IsFinalizer ? " (the finalizer thread)" : start != null ? " (started in " + start + ")" : "");
			names[thread.ManagedThreadId] = text;
			return text;
		}

		/// <summary>
		///     "Root (label).field -> Next[3] -> ... -> old object", long paths cut in the middle; a path from a slot of the
		///     runtime's statics array starts with the field ("static LightBuffer.giSeen -> ...").
		/// </summary>
		private static string FormatPath(ClrRuntime runtime, ClrHeap heap, List<ulong> chain, HashSet<ulong> allocatorObjects, StaticNames statics, out bool fromStatic)
		{
			var text = new StringBuilder();
			int count = chain.Count;
			int first = 0;
			fromStatic = false;
			string field = count > 1 ? StaticField(heap, chain[0], chain[1], statics) : null;
			if (field != null)
			{
				text.Append("static ").Append(field).Append(" -> ");
				first = 1;
				fromStatic = true;
			}

			for (int i = first; i < count; i++)
			{
				if (count > MaxSteps && i == MaxSteps - 4)
				{
					// Keep the start (what holds it) and the end (what of the old scripts it reaches).
					text.Append(" ... ").Append(count - MaxSteps + 1).Append(" steps ... -> ");
					i = count - 3;
				}

				ClrObject value = heap.GetObject(chain[i]);
				text.Append(allocatorObjects.Contains(chain[i]) ? "LoaderAllocator of the old scripts (held through a type, method or delegate of them)" : Describe(runtime, value));
				if (i + 1 < count)
				{
					text.Append(Edge(value, chain[i + 1])).Append(" -> ");
				}
			}

			return text.ToString();
		}

		/// <summary>The static field whose slot in the array <paramref name="array" /> holds <paramref name="to" /> (null: not a statics array).</summary>
		private static string StaticField(ClrHeap heap, ulong array, ulong to, StaticNames statics)
		{
			try
			{
				ClrObject holder = heap.GetObject(array);
				if (holder.Type == null || !holder.Type.IsArray)
				{
					return null;
				}

				foreach (ClrReference reference in holder.EnumerateReferencesWithFields(false, false))
				{
					if (reference.Object.Address == to && reference.IsArrayElement)
					{
						// The slot's address: the offset counts from the object's start (or from its data, after the method table).
						return statics.Find(array + (ulong)reference.Offset) ?? statics.Find(array + (ulong)IntPtr.Size + (ulong)reference.Offset);
					}
				}
			}
			catch (Exception)
			{
				// Not readable in the copy: the array's slot is shown.
			}

			return null;
		}

		/// <summary>The field or element of <paramref name="from" /> that holds <paramref name="to" />.</summary>
		private static string Edge(ClrObject from, ulong to)
		{
			try
			{
				foreach (ClrReference reference in from.EnumerateReferencesWithFields(false, true))
				{
					if (reference.Object.Address != to)
					{
						continue;
					}

					if (reference.IsDependentHandle)
					{
						return " (dependent handle)";
					}

					// A struct's field inside the field or element (a dictionary entry's key or value).
					string inner = reference.InnerField is { Field: not null } nested ? "." + FieldName(nested.Field.Name) : "";
					if (reference.IsArrayElement)
					{
						// Offsets count from the method table pointer; elements start after it and the length.
						int size = from.Type?.ComponentSize ?? 0;
						int index = size > 0 ? (reference.Offset - 2 * IntPtr.Size) / size : -1;
						return (index >= 0 ? "[" + index + "]" : "[]") + inner;
					}

					if (reference.IsField)
					{
						return "." + FieldName(reference.Field?.Name) + inner;
					}

					return "";
				}
			}
			catch (Exception)
			{
				// A damaged object in the copy: the step stays unnamed.
			}

			// Not a field: an object of a collectible type keeps its type's loader allocator alive.
			return " (its type's loader allocator)";
		}

		/// <summary>"GameObject 'Player'", "Light on 'Flash 3'", "Type SpaceStorm", "List&lt;Light&gt;".</summary>
		private static string Describe(ClrRuntime runtime, ClrObject value)
		{
			ClrType type = value.Type;
			if (type == null)
			{
				return "?";
			}

			string name = Short(type.Name);
			try
			{
				if (value.TryReadStringField("<Name>k__BackingField", 48, out string label) && label != null)
				{
					return name + " '" + label + "'";
				}

				if (value.TryReadObjectField("<gameObject>k__BackingField", out ClrObject owner) && !owner.IsNull &&
				    owner.TryReadStringField("<Name>k__BackingField", 48, out string ownerName) && ownerName != null)
				{
					return name + " on '" + ownerName + "'";
				}

				switch (type.Name)
				{
					case "System.Threading.Thread":
						return value.TryReadStringField("_name", 48, out string thread) && thread != null ? "Thread '" + thread + "'" : name;
					case "System.RuntimeType":
						if (value.TryReadField("m_handle", out nint handle))
						{
							ClrType described = runtime.GetTypeByMethodTable((ulong)handle);
							return "Type " + (described != null ? Short(described.Name) : "?");
						}

						break;
				}
			}
			catch (Exception)
			{
				// The type's name says enough.
			}

			return name;
		}

		private static string MethodName(ClrMethod method)
		{
			return Short(method.Type?.Name) + "." + method.Name;
		}

		/// <summary>Auto-property backing fields by their property's name.</summary>
		private static string FieldName(string name)
		{
			if (name == null)
			{
				return "?";
			}

			int end = name.IndexOf(">k__BackingField", StringComparison.Ordinal);
			return name.StartsWith("<", StringComparison.Ordinal) && end > 1 ? name.Substring(1, end - 1) : name;
		}

		private static string Short(string typeName)
		{
			return typeName == null ? "?" : Namespaces.Replace(typeName, "");
		}

		#endregion
	}
}
