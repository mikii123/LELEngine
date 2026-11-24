using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Brutal.VulkanApi;

namespace LELEngine.Graphics.Vulkan
{
	/// <summary>
	///     A program's push constants: loose uniforms a draw reads from push constants instead of from its stage's
	///     block in the frame's memory. Setting one costs a copy of the program's push constants into the command buffer
	///     before the next draw (<c>vkCmdPushConstants</c>), where a value of a block costs a copy of the whole block
	///     into the frame's memory and a new binding of the descriptor set with its offsets. Moved: every loose uniform
	///     of a program whose loose uniforms all fit, else those the descriptor names as set for nearly every draw
	///     (<see cref="ProgramDescriptor.PerDrawUniforms" />) as far as they fit. The stages' SPIR-V reads them from a
	///     push constant block of their own (<see cref="SpirvModule.MoveToPushConstants" />); a uniform two stages read
	///     is one value at one offset.
	/// </summary>
	internal sealed unsafe partial class VulkanProgram
	{
		/// <summary>The target of a uniform's values that is the push constants, not a stage's block.</summary>
		private const int PushTarget = -1;

		/// <summary>Bytes of push constants a program takes at most (the device's limit if lower; Vulkan guarantees 128).</summary>
		private const int MaxPushBytes = 256;

		/// <summary>LEL_VULKAN_DISABLE naming "pushconstants": every loose uniform stays in its stage's block (for comparisons).</summary>
		private static readonly bool pushDisabled = (Environment.GetEnvironmentVariable("LEL_VULKAN_DISABLE") ?? "").IndexOf("pushconstants", StringComparison.OrdinalIgnoreCase) >= 0;

		/// <summary>The uniforms the descriptor names as set for nearly every draw; null for none.</summary>
		private readonly HashSet<string> perDrawUniforms;

		/// <summary>The offset in the push constants of each moved uniform, by name; null without push constants.</summary>
		private Dictionary<string, int> pushOffsets;

		/// <summary>The push constants' values as the setters leave them.</summary>
		private byte* pushBlock;

		/// <summary>The stages that read push constants (none: the program has none).</summary>
		public VkShaderStageFlags PushStages { get; private set; }

		/// <summary>Bytes of the push constants (a multiple of 4).</summary>
		public int PushSize { get; private set; }

		/// <summary>Counts the changes of the push constants' values: a command buffer that has an older count pushes them again.</summary>
		public int PushVersion;

		public byte* PushBlock => pushBlock;

		/// <summary>
		///     Decides which loose uniforms go to push constants and rewrites the stages that read them. Returns each stage's
		///     block of loose uniforms as it was (null where it has none, or none the code reads): its members are the
		///     program's uniforms, wherever their values go now. A block nothing reads any more is no longer used.
		/// </summary>
		private SpirvResource[] MoveToPushConstants(SpirvModule[] modules, Stage[] made)
		{
			int count = modules.Length;
			SpirvResource[] blocks = new SpirvResource[count];
			for (int i = 0; i < count; i++)
			{
				foreach (SpirvResource resource in modules[i].Resources)
				{
					if (resource.IsDefaultUniformBlock && resource.Used && resource.BlockSize > 0)
					{
						blocks[i] = resource;
					}
				}
			}

			int budget = pushDisabled ? 0 : (int)Math.Min((uint)MaxPushBytes, (uint)device.Limits.MaxPushConstantsSize);
			if (budget < 16)
			{
				return blocks;
			}

			// The uniforms that can go: numbers, vectors, matrices and arrays of them; one per name over the stages.
			List<SpirvBlockMember> candidates = new List<SpirvBlockMember>();
			HashSet<string> refused = new HashSet<string>(StringComparer.Ordinal);
			bool everything = true;
			for (int i = 0; i < count; i++)
			{
				if (blocks[i] == null)
				{
					continue;
				}

				foreach (SpirvBlockMember member in blocks[i].Members)
				{
					if (!Movable(member))
					{
						everything = false;
						refused.Add(member.Name);
						continue;
					}

					SpirvBlockMember known = candidates.Find(c => c.Name == member.Name);
					if (known == null)
					{
						candidates.Add(member);
					}
					else if (!SameLayout(known, member))
					{
						everything = false;
						refused.Add(member.Name);
					}
				}
			}

			candidates.RemoveAll(c => refused.Contains(c.Name));
			List<SpirvBlockMember> chosen = new List<SpirvBlockMember>();
			if (everything && Place(candidates, null) <= budget)
			{
				chosen.AddRange(candidates);
			}
			else if (perDrawUniforms != null)
			{
				foreach (SpirvBlockMember candidate in candidates)
				{
					if (perDrawUniforms.Contains(candidate.Name))
					{
						chosen.Add(candidate);
						if (Place(chosen, null) > budget)
						{
							chosen.RemoveAt(chosen.Count - 1);
						}
					}
				}
			}

			if (chosen.Count == 0)
			{
				return blocks;
			}

			Dictionary<string, int> offsets = new Dictionary<string, int>(StringComparer.Ordinal);
			int size = Place(chosen, offsets);
			VkShaderStageFlags reading = VkShaderStageFlags.None;
			for (int i = 0; i < count; i++)
			{
				if (blocks[i] == null)
				{
					continue;
				}

				List<SpirvPushMember> moves = new List<SpirvPushMember>();
				for (int m = 0; m < blocks[i].Members.Count; m++)
				{
					if (offsets.TryGetValue(blocks[i].Members[m].Name, out int offset))
					{
						moves.Add(new SpirvPushMember { Member = m, Offset = offset });
					}
				}

				if (moves.Count == 0)
				{
					continue;
				}

				if (!modules[i].MoveToPushConstants(blocks[i], moves, out bool reached))
				{
					// The code reaches a uniform in a way not followed: the program keeps its blocks as they were.
					for (int j = 0; j < count; j++)
					{
						modules[j] = SpirvModule.Parse(made[j].Unlinked);
					}

					return MoveToPushConstantsUndone(modules);
				}

				if (reached)
				{
					reading |= made[i].Flag;
				}
			}

			if (reading == VkShaderStageFlags.None)
			{
				return blocks;
			}

			pushOffsets = offsets;
			PushStages = reading;
			PushSize = (size + 3) & ~3;
			pushBlock = (byte*)NativeMemory.AllocZeroed((nuint)PushSize);
			return blocks;
		}

		/// <summary>The stages' blocks of loose uniforms of modules read again from their unlinked words.</summary>
		private static SpirvResource[] MoveToPushConstantsUndone(SpirvModule[] modules)
		{
			SpirvResource[] blocks = new SpirvResource[modules.Length];
			for (int i = 0; i < modules.Length; i++)
			{
				foreach (SpirvResource resource in modules[i].Resources)
				{
					if (resource.IsDefaultUniformBlock && resource.Used && resource.BlockSize > 0)
					{
						blocks[i] = resource;
					}
				}
			}

			return blocks;
		}

		/// <summary>
		///     Lays the uniforms out one after the other, each at its alignment (std430: 16 bytes for vectors of three or
		///     four, matrices and arrays, 8 for two, 4 for one), noting their offsets when asked; the bytes they take.
		/// </summary>
		private static int Place(List<SpirvBlockMember> members, Dictionary<string, int> offsets)
		{
			int end = 0;
			foreach (SpirvBlockMember member in members)
			{
				int alignment = member.ArrayLength > 0 || member.Type.Columns > 1 || member.Type.VectorSize >= 3 ? 16 : member.Type.VectorSize == 2 ? 8 : 4;
				int offset = (end + alignment - 1) / alignment * alignment;
				offsets?.Add(member.Name, offset);
				end = offset + SizeOf(member);
			}

			return end;
		}

		/// <summary>The bytes a member takes in its block as laid out there (arrays and matrices by their strides).</summary>
		private static int SizeOf(SpirvBlockMember member)
		{
			if (member.ArrayLength > 0)
			{
				return member.ArrayLength * member.ArrayStride;
			}

			if (member.Type.Columns > 1)
			{
				return (member.RowMajor ? member.Type.VectorSize : member.Type.Columns) * member.MatrixStride;
			}

			return Math.Max(1, member.Type.VectorSize) * 4;
		}

		/// <summary>Whether a uniform can be read from push constants: 32-bit numbers, vectors, matrices, arrays of them.</summary>
		private static bool Movable(SpirvBlockMember member)
		{
			SpirvBaseType type = member.Type.BaseType;
			bool numeric = type == SpirvBaseType.Float || type == SpirvBaseType.Int || type == SpirvBaseType.UInt || type == SpirvBaseType.Bool;
			return member.Members == null && numeric && member.Type.Bits == 32 && !member.Name.Contains("/*", StringComparison.Ordinal)
				&& (member.ArrayLength == 0 || member.ArrayStride > 0) && (member.Type.Columns <= 1 || member.MatrixStride > 0);
		}

		/// <summary>Two stages' uniforms of one name are laid out alike (then one copy of the values serves both).</summary>
		private static bool SameLayout(SpirvBlockMember a, SpirvBlockMember b)
		{
			return a.Type.BaseType == b.Type.BaseType && a.Type.VectorSize == b.Type.VectorSize && a.Type.Columns == b.Type.Columns && a.ArrayLength == b.ArrayLength
				&& a.ArrayStride == b.ArrayStride && a.MatrixStride == b.MatrixStride && a.RowMajor == b.RowMajor;
		}

		/// <summary>The program is given up: its push constants' values go.</summary>
		private void ReleasePushBlock()
		{
			if (pushBlock != null)
			{
				NativeMemory.Free(pushBlock);
				pushBlock = null;
			}
		}
	}
}
