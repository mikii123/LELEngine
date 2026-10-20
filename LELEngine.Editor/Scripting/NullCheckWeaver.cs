using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace LELEngine.Editor
{
	/// <summary>
	///     Makes destroyed engine objects null for the project's scripts: rewrites every null check of the compiled game
	///     and editor scripts (Game.dll, Game.Editor.dll) whose value can be an <see cref="EngineObject" /> into a call of
	///     the engine's checks.
	///
	///     C# compiles <c>x?.M()</c>, <c>x ?? y</c>, <c>x ??= y</c>, <c>if (x != null)</c>, <c>x is null</c>,
	///     <c>x is { }</c> and <c>x is T t</c> into brtrue / brfalse on the reference, and a null comparison used as a value
	///     into <c>ceq</c> (or <c>cgt.un</c>) against <c>ldnull</c>. A forward analysis of each method types every stack
	///     slot (optimized code keeps values on the stack across statements and joins): a test of a value that can hold an
	///     engine object becomes <see cref="EngineObject.IsAlive(EngineObject)" /> (classes deriving from EngineObject) or
	///     <see cref="EngineObject.IsAliveObject" /> (object, interfaces, generic parameters, types out of reach: other
	///     objects pass it as before); a comparison with the null constant becomes
	///     <see cref="EngineObject.NullCheckEquals" /> / <see cref="EngineObject.NullCheckNotEquals" />. Values of other
	///     types (strings, delegates, arrays, other classes, value types) are left alone. <c>ReferenceEquals(x, null)</c>
	///     compiles to the same <c>ldnull; ceq</c> and is rewritten too; <see cref="EngineObject.IsNullReference" /> (a
	///     call) is the plain reference test.
	///
	///     The rewritten assembly carries <see cref="NullChecksWovenAttribute" />, so weaving twice does nothing. The
	///     portable PDB is rewritten with it (breakpoints and stepping keep working).
	/// </summary>
	internal static class NullCheckWeaver
	{
		#region PublicMethods

		internal sealed class Result
		{
			/// <summary>The assembly was rewritten now (false: woven before, or nothing to do).</summary>
			public bool Woven;

			/// <summary>Null checks rewritten.</summary>
			public int Checks;

			/// <summary>Methods left as they were because their stack could not be followed.</summary>
			public int SkippedMethods;

			public double Milliseconds;
		}

		/// <summary>
		///     Rewrites <paramref name="dll" /> (and its .pdb) in place. <paramref name="searchFolders" /> resolve the
		///     referenced assemblies (the engine's folder and the script output); the runtime's folder is added.
		/// </summary>
		public static Result WeaveFile(string dll, IEnumerable<string> searchFolders)
		{
			var result = new Result();
			var watch = Stopwatch.StartNew();
			string pdb = Path.ChangeExtension(dll, ".pdb");
			bool symbols = File.Exists(pdb);

			using (var resolver = new DefaultAssemblyResolver())
			{
				foreach (string folder in searchFolders.Append(RuntimeEnvironment.GetRuntimeDirectory()).Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
				{
					resolver.AddSearchDirectory(folder);
				}

				var reading = new ReaderParameters
				{
					AssemblyResolver = resolver,
					ReadingMode = ReadingMode.Immediate,
					ReadSymbols = symbols,
					SymbolReaderProvider = symbols ? new PortablePdbReaderProvider() : null,
					SymbolStream = symbols ? new MemoryStream(File.ReadAllBytes(pdb)) : null
				};

				using (ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(File.ReadAllBytes(dll)), reading))
				{
					if (IsWoven(module))
					{
						return result;
					}

					var context = Context.Create(module);
					if (context == null)
					{
						// The scripts never reference the engine: no engine object can reach them.
						return result;
					}

					foreach (TypeDefinition type in module.GetTypes())
					{
						foreach (MethodDefinition method in type.Methods)
						{
							if (!method.HasBody)
							{
								continue;
							}

							int checks = WeaveMethod(method, context);
							if (checks < 0)
							{
								result.SkippedMethods++;
							}
							else
							{
								result.Checks += checks;
							}
						}
					}

					var marker = new CustomAttribute(context.MarkerConstructor);
					marker.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Int32, result.Checks));
					module.Assembly.CustomAttributes.Add(marker);

					var assembly = new MemoryStream();
					var symbolStream = symbols ? new MemoryStream() : null;
					module.Write(assembly, new WriterParameters
					{
						WriteSymbols = symbols,
						SymbolWriterProvider = symbols ? new PortablePdbWriterProvider() : null,
						SymbolStream = symbolStream
					});

					File.WriteAllBytes(dll, assembly.ToArray());
					if (symbols)
					{
						File.WriteAllBytes(pdb, symbolStream.ToArray());
					}
				}
			}

			result.Woven = true;
			result.Milliseconds = watch.Elapsed.TotalMilliseconds;
			return result;
		}

		#endregion

		#region PrivateTypes

		/// <summary>What a stack slot holds, as far as null checks go (a small lattice, see <see cref="Merge" />).</summary>
		private enum Kind : byte
		{
			/// <summary>The null constant.</summary>
			Null,

			/// <summary>Cannot be an engine object (value types, strings, delegates, arrays, other classes).</summary>
			Plain,

			/// <summary>Statically an engine object.</summary>
			Engine,

			/// <summary>A reference that may be an engine object (object, interfaces, generics, unresolved types).</summary>
			Maybe
		}

		private sealed class Context
		{
			public MethodReference IsAlive;
			public MethodReference IsAliveObject;
			public MethodReference NullCheckEquals;
			public MethodReference NullCheckNotEquals;
			public MethodReference MarkerConstructor;
			public readonly Dictionary<string, Kind> TypeKinds = new Dictionary<string, Kind>();

			public static Context Create(ModuleDefinition module)
			{
				AssemblyNameReference engineName = module.AssemblyReferences.FirstOrDefault(r => r.Name == "LELEngine");
				AssemblyDefinition engine = engineName != null ? module.AssemblyResolver.Resolve(engineName) : null;
				TypeDefinition engineObject = engine?.MainModule.GetType("LELEngine.EngineObject");
				TypeDefinition marker = engine?.MainModule.GetType("LELEngine.NullChecksWovenAttribute");
				if (engineObject == null || marker == null)
				{
					return null;
				}

				MethodReference Import(string name, params string[] parameters)
				{
					return module.ImportReference(engineObject.Methods.Single(m => m.Name == name && m.IsStatic
						&& m.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)));
				}

				return new Context
				{
					IsAlive = Import(nameof(EngineObject.IsAlive), "LELEngine.EngineObject"),
					IsAliveObject = Import(nameof(EngineObject.IsAliveObject), "System.Object"),
					NullCheckEquals = Import(nameof(EngineObject.NullCheckEquals), "System.Object", "System.Object"),
					NullCheckNotEquals = Import(nameof(EngineObject.NullCheckNotEquals), "System.Object", "System.Object"),
					MarkerConstructor = module.ImportReference(marker.Methods.Single(m => m.IsConstructor && !m.IsStatic))
				};
			}
		}

		#endregion

		#region PrivateMethods

		private static bool IsWoven(ModuleDefinition module)
		{
			return module.Assembly.CustomAttributes.Any(a => a.AttributeType.FullName == "LELEngine.NullChecksWovenAttribute");
		}

		/// <summary>Rewrites the method's null checks; returns how many, or -1 when its stack could not be followed.</summary>
		private static int WeaveMethod(MethodDefinition method, Context context)
		{
			MethodBody body = method.Body;
			body.SimplifyMacros();
			Dictionary<Instruction, Kind[]> stacks = Analyze(method, context);
			if (stacks == null)
			{
				body.OptimizeMacros();
				return -1;
			}

			ILProcessor il = body.GetILProcessor();
			int count = 0;
			foreach (Instruction instruction in body.Instructions.ToArray())
			{
				if (!stacks.TryGetValue(instruction, out Kind[] stack))
				{
					// Unreachable.
					continue;
				}

				Code code = instruction.OpCode.Code;
				if (code == Code.Brtrue || code == Code.Brfalse)
				{
					Kind tested = stack[stack.Length - 1];
					if (tested != Kind.Engine && tested != Kind.Maybe)
					{
						continue;
					}

					// The branch becomes the call and a new branch follows it: jumps to the old instruction land on the call.
					Instruction branch = Instruction.Create(instruction.OpCode, (Instruction)instruction.Operand);
					instruction.OpCode = OpCodes.Call;
					instruction.Operand = tested == Kind.Engine ? context.IsAlive : context.IsAliveObject;
					il.InsertAfter(instruction, branch);
					count++;
				}
				else if (code == Code.Ceq || code == Code.Cgt_Un)
				{
					Kind first = stack[stack.Length - 2];
					Kind second = stack[stack.Length - 1];
					bool firstIsNull = first == Kind.Null && (second == Kind.Engine || second == Kind.Maybe);
					bool secondIsNull = second == Kind.Null && (first == Kind.Engine || first == Kind.Maybe);
					// x != null as a value is x; ldnull; cgt.un (the null constant second).
					if (code == Code.Ceq ? firstIsNull || secondIsNull : secondIsNull)
					{
						instruction.OpCode = OpCodes.Call;
						instruction.Operand = code == Code.Ceq ? context.NullCheckEquals : context.NullCheckNotEquals;
						count++;
					}
				}
			}

			body.OptimizeMacros();
			return count;
		}

		/// <summary>
		///     The kinds on the stack before every reachable instruction (forward data flow over the method's branches and
		///     exception handlers); null when the stack cannot be followed.
		/// </summary>
		private static Dictionary<Instruction, Kind[]> Analyze(MethodDefinition method, Context context)
		{
			MethodBody body = method.Body;
			var stacks = new Dictionary<Instruction, Kind[]>();
			var work = new Stack<Instruction>();

			bool Flow(Instruction target, Kind[] stack)
			{
				if (!stacks.TryGetValue(target, out Kind[] known))
				{
					stacks[target] = stack;
					work.Push(target);
					return true;
				}

				if (known.Length != stack.Length)
				{
					return false;
				}

				Kind[] merged = null;
				for (int i = 0; i < stack.Length; i++)
				{
					Kind kind = Merge(known[i], stack[i]);
					if (kind != known[i])
					{
						merged ??= (Kind[])known.Clone();
						merged[i] = kind;
					}
				}

				if (merged != null)
				{
					stacks[target] = merged;
					work.Push(target);
				}

				return true;
			}

			if (body.Instructions.Count == 0)
			{
				return stacks;
			}

			Flow(body.Instructions[0], Array.Empty<Kind>());
			foreach (ExceptionHandler handler in body.ExceptionHandlers)
			{
				// Catch and filter blocks start with the exception (never an engine object); finally / fault start empty.
				bool exception = handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter;
				Flow(handler.HandlerStart, exception ? new[] { Kind.Plain } : Array.Empty<Kind>());
				if (handler.FilterStart != null)
				{
					Flow(handler.FilterStart, new[] { Kind.Plain });
				}
			}

			while (work.Count > 0)
			{
				Instruction instruction = work.Pop();
				Kind[] stack = stacks[instruction];
				if (!StackEffect(instruction, method, out int pops, out int pushes) || pops > stack.Length)
				{
					return null;
				}

				var next = new Kind[stack.Length - pops + pushes];
				Array.Copy(stack, next, stack.Length - pops);
				if (pushes > 0)
				{
					if (instruction.OpCode.Code == Code.Dup)
					{
						next[next.Length - 1] = next[next.Length - 2] = stack[stack.Length - 1];
					}
					else
					{
						next[next.Length - 1] = PushedKind(instruction, context);
						if (pushes > 1)
						{
							return null;
						}
					}
				}

				switch (instruction.OpCode.FlowControl)
				{
					case FlowControl.Branch:
						// leave empties the stack.
						if (!Flow((Instruction)instruction.Operand, instruction.OpCode.Code == Code.Leave ? Array.Empty<Kind>() : next))
						{
							return null;
						}

						break;
					case FlowControl.Cond_Branch:
						IEnumerable<Instruction> targets = instruction.Operand is Instruction[] cases ? cases : new[] { (Instruction)instruction.Operand };
						foreach (Instruction target in targets)
						{
							if (!Flow(target, next))
							{
								return null;
							}
						}

						if (instruction.Next != null && !Flow(instruction.Next, next))
						{
							return null;
						}

						break;
					case FlowControl.Return:
					case FlowControl.Throw:
						break;
					default:
						if (instruction.Next != null && !Flow(instruction.Next, next))
						{
							return null;
						}

						break;
				}
			}

			return stacks;
		}

		/// <summary>The kind both paths agree on at a join.</summary>
		private static Kind Merge(Kind a, Kind b)
		{
			if (a == b)
			{
				return a;
			}

			if (a == Kind.Null)
			{
				return b;
			}

			if (b == Kind.Null)
			{
				return a;
			}

			// Engine with Maybe, or a reference of another class: their common type may hold an engine object.
			return Kind.Maybe;
		}

		private static bool StackEffect(Instruction instruction, MethodDefinition method, out int pops, out int pushes)
		{
			OpCode op = instruction.OpCode;
			pops = 0;
			pushes = 0;
			switch (op.StackBehaviourPush)
			{
				case StackBehaviour.Push0:
					break;
				case StackBehaviour.Push1_push1:
					pushes = 2;
					break;
				case StackBehaviour.Varpush:
					if (instruction.Operand is MethodReference called)
					{
						pushes = called.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
					}
					else if (instruction.Operand is CallSite site)
					{
						pushes = site.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
					}
					else
					{
						return false;
					}

					break;
				default:
					pushes = 1;
					break;
			}

			switch (op.StackBehaviourPop)
			{
				case StackBehaviour.Pop0:
					break;
				case StackBehaviour.Pop1:
				case StackBehaviour.Popi:
				case StackBehaviour.Popref:
					pops = 1;
					break;
				case StackBehaviour.Pop1_pop1:
				case StackBehaviour.Popi_pop1:
				case StackBehaviour.Popi_popi:
				case StackBehaviour.Popi_popi8:
				case StackBehaviour.Popi_popr4:
				case StackBehaviour.Popi_popr8:
				case StackBehaviour.Popref_pop1:
				case StackBehaviour.Popref_popi:
					pops = 2;
					break;
				case StackBehaviour.Popi_popi_popi:
				case StackBehaviour.Popref_popi_popi:
				case StackBehaviour.Popref_popi_popi8:
				case StackBehaviour.Popref_popi_popr4:
				case StackBehaviour.Popref_popi_popr8:
				case StackBehaviour.Popref_popi_popref:
					pops = 3;
					break;
				case StackBehaviour.Varpop:
					if (instruction.Operand is MethodReference callee)
					{
						pops = callee.Parameters.Count + (callee.HasThis && !callee.ExplicitThis && op.Code != Code.Newobj ? 1 : 0);
					}
					else if (instruction.Operand is CallSite site)
					{
						pops = site.Parameters.Count + (site.HasThis && !site.ExplicitThis ? 1 : 0) + 1;
					}
					else if (op.Code == Code.Ret)
					{
						pops = method.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
					}
					else
					{
						return false;
					}

					break;
				case StackBehaviour.PopAll:
					// leave: the whole stack (the target starts empty).
					pops = 0;
					break;
				default:
					return false;
			}

			return true;
		}

		/// <summary>What the instruction pushes (one value; dup is handled by the caller).</summary>
		private static Kind PushedKind(Instruction instruction, Context context)
		{
			switch (instruction.OpCode.Code)
			{
				case Code.Ldnull:
					return Kind.Null;
				case Code.Ldloc:
					return KindOf(((VariableDefinition)instruction.Operand).VariableType, context);
				case Code.Ldarg:
					return KindOf(((ParameterDefinition)instruction.Operand).ParameterType, context);
				case Code.Ldfld:
				case Code.Ldsfld:
				{
					var field = (FieldReference)instruction.Operand;
					return KindOf(Substitute(field.FieldType, field.DeclaringType, null), context);
				}
				case Code.Call:
				case Code.Callvirt:
				{
					var called = (MethodReference)instruction.Operand;
					return KindOf(Substitute(called.ReturnType, called.DeclaringType, called), context);
				}
				case Code.Calli:
					return KindOf(((CallSite)instruction.Operand).ReturnType, context);
				case Code.Isinst:
				case Code.Castclass:
				case Code.Unbox_Any:
				case Code.Ldelem_Any:
					return KindOf((TypeReference)instruction.Operand, context);
				case Code.Box:
				{
					// A boxed value type is never null; a boxed generic parameter may be an engine object.
					var boxed = (TypeReference)instruction.Operand;
					return boxed is GenericParameter || !boxed.IsValueType ? Kind.Maybe : Kind.Plain;
				}
				case Code.Ldelem_Ref:
				case Code.Ldind_Ref:
					return Kind.Maybe;
				default:
					// newobj (never null), numbers, strings, tokens, addresses, arithmetic.
					return Kind.Plain;
			}
		}

		/// <summary>A generic parameter of the called method or its declaring type replaced by the instance's argument.</summary>
		private static TypeReference Substitute(TypeReference type, TypeReference declaringType, MethodReference method)
		{
			if (type is not GenericParameter parameter)
			{
				return type;
			}

			if (parameter.Type == GenericParameterType.Method && method is GenericInstanceMethod genericMethod && parameter.Position < genericMethod.GenericArguments.Count)
			{
				return genericMethod.GenericArguments[parameter.Position];
			}

			if (parameter.Type == GenericParameterType.Type && declaringType is GenericInstanceType genericType && parameter.Position < genericType.GenericArguments.Count)
			{
				return genericType.GenericArguments[parameter.Position];
			}

			return type;
		}

		private static Kind KindOf(TypeReference type, Context context)
		{
			while (type is IModifierType modified)
			{
				type = modified.ElementType;
			}

			switch (type)
			{
				case null:
				case ByReferenceType:
				case PointerType:
				case FunctionPointerType:
				case ArrayType:
					return Kind.Plain;
				case GenericParameter:
					// Tested directly only when it is a reference type; it may be an engine object.
					return Kind.Maybe;
			}

			if (type.IsValueType)
			{
				return Kind.Plain;
			}

			switch (type.MetadataType)
			{
				case MetadataType.Object:
					return Kind.Maybe;
				case MetadataType.String:
					return Kind.Plain;
			}

			TypeReference element = type is GenericInstanceType instance ? instance.ElementType : type;
			string key = element.FullName;
			if (context.TypeKinds.TryGetValue(key, out Kind cached))
			{
				return cached;
			}

			Kind kind = KindOfDefinition(element);
			context.TypeKinds[key] = kind;
			return kind;
		}

		private static Kind KindOfDefinition(TypeReference type)
		{
			TypeDefinition definition = Resolve(type);
			if (definition == null)
			{
				return Kind.Maybe;
			}

			if (definition.IsValueType)
			{
				return Kind.Plain;
			}

			if (definition.IsInterface)
			{
				return Kind.Maybe;
			}

			for (TypeDefinition current = definition; ;)
			{
				switch (current.FullName)
				{
					case "LELEngine.EngineObject":
						return Kind.Engine;
					case "System.Object":
					case "System.MulticastDelegate":
						return Kind.Plain;
				}

				if (current.BaseType == null)
				{
					return Kind.Plain;
				}

				TypeDefinition baseType = Resolve(current.BaseType);
				if (baseType == null)
				{
					// A base class out of reach: it might derive from EngineObject.
					return Kind.Maybe;
				}

				current = baseType;
			}
		}

		private static TypeDefinition Resolve(TypeReference type)
		{
			try
			{
				return type.Resolve();
			}
			catch (AssemblyResolutionException)
			{
				return null;
			}
		}

		#endregion
	}
}
