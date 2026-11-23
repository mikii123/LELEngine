using System;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     A top level acceleration structure of instances given again every frame (<see cref="Begin" />, <see cref="Add" />,
	///     <see cref="Build" />), kept as cheap as the changes allow: built in full when the number of instances changed (or
	///     the structure had to grow), updated in place (refitted) when only what the instances hold changed (placements,
	///     bottom levels, custom indices, masks), built in full after <see cref="RefitsBeforeRebuild" /> updates in a row
	///     (an update keeps the tree of the last build, which fits worse the further things moved), and left as it is when
	///     nothing changed. Graphics thread only.
	/// </summary>
	public sealed class SceneTopLevel
	{
		#region PublicFields

		/// <summary>Updates in a row before the structure is built in full again.</summary>
		public const int RefitsBeforeRebuild = 60;

		/// <summary>The structure of this frame's instances (null without instances, or before the first build).</summary>
		public GpuAccelerationStructure TopLevel => count > 0 && builtCount == count ? topLevel : null;

		/// <summary>Instances given since <see cref="Begin" />.</summary>
		public int Count => count;

		/// <summary>Full builds and updates since the structure was made (statistics).</summary>
		public int Builds { get; private set; }

		public int Updates { get; private set; }

		/// <summary>Bytes of the structure on the GPU.</summary>
		public long Bytes => topLevel?.Size ?? 0;

		#endregion

		#region PrivateFields

		private readonly string name;
		private GpuAccelerationStructure topLevel;
		private AccelerationStructureInstance[] instances = new AccelerationStructureInstance[16];
		private AccelerationStructureInstance[] built = new AccelerationStructureInstance[16];
		private int count;
		private int builtCount = -1;
		private int refits;

		#endregion

		#region Constructors

		public SceneTopLevel(string name)
		{
			this.name = name;
		}

		#endregion

		#region PublicMethods

		/// <summary>Starts this frame's instances.</summary>
		public void Begin()
		{
			count = 0;
		}

		/// <summary>An instance of a built bottom level (<see cref="MeshAccelerationStructures.GetBuilt" />).</summary>
		public void Add(GpuAccelerationStructure bottomLevel, in Matrix4 transform, int customIndex, byte mask = 0xFF, AccelerationStructureInstanceFlags flags = AccelerationStructureInstanceFlags.None)
		{
			if (count == instances.Length)
			{
				Array.Resize(ref instances, count * 2);
			}

			instances[count++] = new AccelerationStructureInstance(bottomLevel, transform, customIndex, mask, flags);
		}

		/// <summary>
		///     Records what this frame's instances need: a full build, an update or nothing (it ends an open pass when it
		///     records). The bottom levels the instances place must be built (recorded) before.
		/// </summary>
		public void Build()
		{
			if (count == 0)
			{
				builtCount = 0;
				return;
			}

			bool rebuild = count != builtCount;
			if (topLevel == null || topLevel.MaxInstances < count)
			{
				topLevel?.Dispose();
				int capacity = 64;
				while (capacity < count)
				{
					capacity *= 2;
				}

				topLevel = Gfx.Device.CreateAccelerationStructure(AccelerationStructureDescriptor.TopLevel(capacity, AccelerationStructureFlags.PreferFastTrace | AccelerationStructureFlags.AllowUpdate, name));
				rebuild = true;
			}

			bool changed = rebuild;
			if (!rebuild)
			{
				for (int i = 0; i < count; i++)
				{
					if (!Same(instances[i], built[i]))
					{
						changed = true;
						break;
					}
				}
			}

			if (!changed)
			{
				return;
			}

			bool update = !rebuild && refits < RefitsBeforeRebuild;
			Gfx.Commands.BuildTopLevel(topLevel, new ReadOnlySpan<AccelerationStructureInstance>(instances, 0, count), update);
			if (update)
			{
				refits++;
				Updates++;
			}
			else
			{
				refits = 0;
				Builds++;
			}

			if (built.Length < count)
			{
				built = new AccelerationStructureInstance[instances.Length];
			}

			Array.Copy(instances, built, count);
			builtCount = count;
		}

		/// <summary>Frees the structure.</summary>
		public void Delete()
		{
			topLevel?.Dispose();
			topLevel = null;
			count = 0;
			builtCount = -1;
			refits = 0;
			Array.Clear(instances);
			Array.Clear(built);
		}

		#endregion

		#region PrivateMethods

		private static bool Same(in AccelerationStructureInstance a, in AccelerationStructureInstance b)
		{
			return a.BottomLevel == b.BottomLevel && a.CustomIndex == b.CustomIndex && a.Mask == b.Mask && a.Flags == b.Flags && a.Transform == b.Transform;
		}

		#endregion
	}
}
