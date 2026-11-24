using System;
using System.Collections.Generic;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>A member of a stage's uniform block moved into push constants: its index in the block, and its byte offset in the program's push constants.</summary>
	internal struct SpirvPushMember
	{
		public int Member;
		public int Offset;
	}

	internal sealed partial class SpirvModule
	{
		private const int PushConstantStorage = 9;
		private const int ColMajorDecoration = 5;

		/// <summary>
		///     Moves members of a uniform block (a stage's loose uniforms: <paramref name="block" />) into a block of push
		///     constants of their own, each at the offset given in the program's push constants: every access chain into
		///     such a member reads the push constants instead (its pointers' types are those of push constants, the block's
		///     index of the member that of the new block). The uniform block keeps its layout; the moved members' bytes stay
		///     in it, unread. Members the code never reaches are left where they are.
		/// </summary>
		/// <returns>
		///     False, with nothing changed, when the code uses a pointer into a moved member other than by loading through it
		///     or reaching further into it (nothing glslang writes for loose uniforms). The block's
		///     <see cref="SpirvResource.Used" /> becomes false when no code reads it any more.
		/// </returns>
		public bool MoveToPushConstants(SpirvResource block, IReadOnlyList<SpirvPushMember> moved, out bool reached)
		{
			reached = false;
			uint variable = block.VariableId;
			Dictionary<uint, uint[]> pointerTypes = new Dictionary<uint, uint[]>();
			Dictionary<uint, uint[]> constantsById = new Dictionary<uint, uint[]>();
			uint structId = 0;
			foreach (uint[] words in instructions)
			{
				switch (OpcodeOf(words))
				{
					case Op.TypePointer:
						pointerTypes[words[1]] = words;
						break;
					case Op.Constant:
						constantsById[words[2]] = words;
						break;
					case Op.Variable:
						if (words[2] == variable && pointerTypes.TryGetValue(words[1], out uint[] pointer))
						{
							structId = pointer[3];
						}

						break;
				}
			}

			uint[] structWords = null;
			foreach (uint[] words in instructions)
			{
				if (OpcodeOf(words) == Op.TypeStruct && words[1] == structId)
				{
					structWords = words;
					break;
				}
			}

			if (structWords == null || FindEntryPoint() == null)
			{
				return false;
			}

			Dictionary<int, int> offsetOf = new Dictionary<int, int>();
			foreach (SpirvPushMember member in moved)
			{
				offsetOf[member.Member] = member.Offset;
			}

			// The pointers into moved members: access chains from the block whose first index is one, and chains from those.
			HashSet<uint> derived = new HashSet<uint>();
			SortedDictionary<int, int> reachedMembers = new SortedDictionary<int, int>();
			uint indexType = 0;
			bool stillUsed = false;
			ForEachInstruction((words, opcode) =>
			{
				if ((opcode == Op.AccessChain || opcode == Op.InBoundsAccessChain) && words.Length >= 5)
				{
					if (words[3] == variable)
					{
						if (constantsById.TryGetValue(words[4], out uint[] constant) && offsetOf.ContainsKey((int)constant[3]))
						{
							derived.Add(words[2]);
							reachedMembers[offsetOf[(int)constant[3]]] = (int)constant[3];
							indexType = constant[1];
						}
						else
						{
							stillUsed = true;
						}
					}
					else if (derived.Contains(words[3]))
					{
						derived.Add(words[2]);
					}
				}
			});

			if (derived.Count == 0)
			{
				return true;
			}

			// Every use of such a pointer: loaded, or reached into; anything else is left alone (and nothing moves). A word
			// of another instruction that equals one by chance (a literal) also leaves the block as it is.
			bool followed = true;
			ForEachInstruction((words, opcode) =>
			{
				for (int w = 1; w < words.Length && followed; w++)
				{
					if (!derived.Contains(words[w]))
					{
						continue;
					}

					bool allowed = (opcode == Op.Load && w == 3) || ((opcode == Op.AccessChain || opcode == Op.InBoundsAccessChain) && (w == 2 || w == 3));
					followed = allowed;
				}

				if (!stillUsed && opcode != Op.AccessChain && opcode != Op.InBoundsAccessChain)
				{
					// The block named in some other way (a whole load, a copy): it stays used.
					for (int w = 1; w < words.Length; w++)
					{
						stillUsed |= words[w] == variable;
					}
				}
			});

			if (!followed)
			{
				return false;
			}

			// The new declarations: the push constant block (the reached members, by offset), its pointer and variable,
			// pointers of push constants to what the chains reach, and the indices of the members in the new block.
			List<uint[]> declarations = new List<uint[]>();
			List<uint[]> annotations = new List<uint[]>();
			Dictionary<int, uint> newIndex = new Dictionary<int, uint>();
			uint[] structType = new uint[2 + reachedMembers.Count];
			structType[0] = (uint)structType.Length << 16 | Op.TypeStruct;
			uint pushStruct = bound++;
			structType[1] = pushStruct;
			int position = 0;
			foreach (KeyValuePair<int, int> pair in reachedMembers)
			{
				int member = pair.Value;
				structType[2 + position] = structWords[2 + member];
				newIndex[member] = Constant(indexType, (uint)position, constantsById, declarations);
				annotations.Add(new uint[] { 5u << 16 | Op.MemberDecorate, pushStruct, (uint)position, Decoration.Offset, (uint)pair.Key });
				for (int i = annotationStart; i < annotationEnd; i++)
				{
					uint[] words = instructions[i];
					if (OpcodeOf(words) == Op.MemberDecorate && words[1] == structId && words[2] == (uint)member && words[3] != Decoration.Offset
						&& (words[3] == Decoration.MatrixStride || words[3] == Decoration.RowMajor || words[3] == ColMajorDecoration))
					{
						uint[] copy = (uint[])words.Clone();
						copy[1] = pushStruct;
						copy[2] = (uint)position;
						annotations.Add(copy);
					}
				}

				position++;
			}

			annotations.Add(new uint[] { 3u << 16 | Op.Decorate, pushStruct, Decoration.Block });
			declarations.Add(structType);
			uint pushPointer = bound++;
			declarations.Add(new uint[] { 4u << 16 | Op.TypePointer, pushPointer, PushConstantStorage, pushStruct });
			uint pushVariable = bound++;
			Dictionary<uint, uint> pushPointerOf = new Dictionary<uint, uint>();
			foreach (KeyValuePair<uint, uint[]> pair in pointerTypes)
			{
				if (pair.Value[2] == PushConstantStorage && !pushPointerOf.ContainsKey(pair.Value[3]))
				{
					pushPointerOf[pair.Value[3]] = pair.Key;
				}
			}

			ForEachInstruction((words, opcode) =>
			{
				if ((opcode == Op.AccessChain || opcode == Op.InBoundsAccessChain) && derived.Contains(words[2]) && pointerTypes.TryGetValue(words[1], out uint[] pointer))
				{
					uint pointee = pointer[3];
					if (!pushPointerOf.TryGetValue(pointee, out uint pushed))
					{
						pushed = bound++;
						declarations.Add(new uint[] { 4u << 16 | Op.TypePointer, pushed, PushConstantStorage, pointee });
						pushPointerOf[pointee] = pushed;
					}

					words[1] = pushed;
					if (words[3] == variable)
					{
						words[3] = pushVariable;
						words[4] = newIndex[(int)constantsById[words[4]][3]];
					}
				}
			});

			declarations.Add(new uint[] { 4u << 16 | Op.Variable, pushPointer, pushVariable, PushConstantStorage });
			instructions.InsertRange(instructions.Count, declarations);
			instructions.InsertRange(annotationEnd, annotations);
			annotationEnd += annotations.Count;

			// SPIR-V 1.4 on: an entry point lists every global variable it uses.
			if (version >= 0x00010400)
			{
				int at = instructions.IndexOf(FindEntryPoint());
				uint[] entry = instructions[at];
				List<uint> words = new List<uint>(entry);
				if (!stillUsed)
				{
					int interfaceStart = 3 + StringWords(entry, 3);
					for (int w = words.Count - 1; w >= interfaceStart; w--)
					{
						if (words[w] == variable)
						{
							words.RemoveAt(w);
						}
					}
				}

				words.Add(pushVariable);
				words[0] = (uint)words.Count << 16 | Op.EntryPoint;
				instructions[at] = words.ToArray();
			}

			block.Used = stillUsed;
			reached = true;
			return true;
		}

		/// <summary>The first entry point's instruction.</summary>
		private uint[] FindEntryPoint()
		{
			foreach (uint[] words in instructions)
			{
				if (OpcodeOf(words) == Op.EntryPoint)
				{
					return words;
				}
			}

			return null;
		}

		/// <summary>The words a literal string takes from a word on (with its terminating zero).</summary>
		private static int StringWords(uint[] words, int start)
		{
			for (int w = start; w < words.Length; w++)
			{
				if ((words[w] & 0xFF000000) == 0 || (words[w] & 0x00FF0000) == 0 || (words[w] & 0x0000FF00) == 0 || (words[w] & 0x000000FF) == 0)
				{
					return w - start + 1;
				}
			}

			return words.Length - start;
		}

		/// <summary>An integer constant of a type: one the module declares, or a new one.</summary>
		private uint Constant(uint type, uint value, Dictionary<uint, uint[]> constants, List<uint[]> declarations)
		{
			foreach (KeyValuePair<uint, uint[]> pair in constants)
			{
				if (pair.Value.Length == 4 && pair.Value[1] == type && pair.Value[3] == value)
				{
					return pair.Key;
				}
			}

			uint id = bound++;
			uint[] words = { 4u << 16 | Op.Constant, type, id, value };
			constants[id] = words;
			declarations.Add(words);
			return id;
		}

		/// <summary>An instruction of a function: its words (changes go into the module) and its opcode.</summary>
		private delegate void InstructionVisitor(Span<uint> words, int opcode);

		/// <summary>Calls <paramref name="visit" /> with every instruction of the functions.</summary>
		private void ForEachInstruction(InstructionVisitor visit)
		{
			int position = 0;
			while (position < functions.Length)
			{
				uint first = functions[position];
				int length = (int)(first >> 16);
				if (length == 0 || position + length > functions.Length)
				{
					throw new InvalidOperationException("Malformed SPIR-V in a function.");
				}

				visit(new Span<uint>(functions, position, length), (int)(first & 0xFFFF));
				position += length;
			}
		}
	}
}
