using System;
using System.Collections.Generic;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Rendering.Lumen
{
	/// <summary>
	///     A <see cref="LumenScene" />'s objects as a top level acceleration structure, which Lumen's rays trace with ray
	///     queries (<see cref="Lighting.HardwareRayTracingActive" />, Engine/SceneRayQuery.glsl): each object is an instance
	///     of its mesh's bottom level (<see cref="MeshAccelerationStructures" />) placed as the GI sees it (scaled-local
	///     space: the object's scale, then its rotation and position), its custom index the object's row of the object
	///     table. The structure is built, updated or kept as the instances change (<see cref="SceneTopLevel" />). An object
	///     whose mesh's bottom level waits for a later frame's build (a mesh that appeared in play) is left out until then. A
	///     table of each object's range in the geometry pool lets a hit read its triangle.
	/// </summary>
	public sealed class SceneRayTracing
	{
		#region PublicFields

		/// <summary>The acceleration structure binding the shaders trace (Engine/SceneRayQuery.glsl).</summary>
		public const int SceneBinding = 0;

		/// <summary>The storage bindings of the geometry pool's vertices and indices, and of the objects' ranges in them.</summary>
		public const int VertexBinding = 19;

		public const int IndexBinding = 20;
		public const int RangeBinding = 21;

		/// <summary>
		///     How far a point of the depth buffer is looked for a triangle under (world units, Engine/SceneRayQuery.glsl's
		///     SceneObjectAtSurface): the object at a screen trace's hit, in the surface cache's debug view.
		/// </summary>
		public const float SurfaceReach = 0.1f;

		/// <summary>The top level of this frame (null before the first update, or without objects).</summary>
		public GpuAccelerationStructure TopLevel => topLevel.TopLevel;

		/// <summary>Objects placed in the top level this frame.</summary>
		public int InstanceCount => topLevel.Count;

		#endregion

		#region PrivateFields

		private readonly SceneTopLevel topLevel = new SceneTopLevel("Lumen scene top level");

		// Per object: first index and base vertex of its mesh in the pool (uvec2), never empty.
		private uint[] ranges = new uint[2];
		private uint[] uploadedRanges = Array.Empty<uint>();
		private GpuBuffer rangeBuffer;
		private int rangeBufferBytes;

		#endregion

		#region PublicMethods

		/// <summary>The top level of the objects as they are this frame: built, refitted or kept (with the bottom levels it needs built first).</summary>
		public void Update(IReadOnlyList<LumenScene.SceneObject> objects)
		{
			MeshAccelerationStructures.Compact();
			int objectCount = objects.Count;
			if (ranges.Length < Math.Max(1, objectCount) * 2)
			{
				Array.Resize(ref ranges, Math.Max(objectCount * 2, ranges.Length * 2));
			}

			// Every mesh asked for first, so this frame's bottom level builds go in one command; then the objects whose
			// meshes are built.
			for (int i = 0; i < objectCount; i++)
			{
				MeshAccelerationStructures.Get(objects[i].Renderer.Mesh);
			}

			MeshAccelerationStructures.BuildPending();
			topLevel.Begin();
			for (int i = 0; i < objectCount; i++)
			{
				LumenScene.SceneObject o = objects[i];
				Mesh mesh = o.Renderer.Mesh;
				GpuAccelerationStructure bottom = MeshAccelerationStructures.GetBuilt(mesh);
				if (bottom == null)
				{
					ranges[i * 2] = 0;
					ranges[i * 2 + 1] = 0;
					continue;
				}

				GeometryPool.GetRange(mesh, out int baseVertex, out _, out int firstIndex, out _);
				ranges[i * 2] = (uint)firstIndex;
				ranges[i * 2 + 1] = (uint)baseVertex;
				// A mirroring placement needs no flag: the rays tell a triangle's front in the mesh's own space, where the
				// instance's mirror does not turn its winding.
				topLevel.Add(bottom, Matrix4.CreateScale(o.Scale) * o.LocalToWorld(), o.Index);
			}

			topLevel.Build();
			UploadRanges(objectCount);
		}

		/// <summary>Binds the top level, the geometry pool's buffers and the objects' ranges for Engine/SceneRayQuery.glsl.</summary>
		public void Bind()
		{
			CommandList commands = Gfx.Commands;
			commands.SetAccelerationStructure(SceneBinding, TopLevel);
			commands.SetStorageBuffer(VertexBinding, GeometryPool.VertexBuffer);
			commands.SetStorageBuffer(IndexBinding, GeometryPool.IndexBuffer);
			commands.SetStorageBuffer(RangeBinding, rangeBuffer);
		}

		public void Delete()
		{
			topLevel.Delete();
			rangeBuffer?.Dispose();
			rangeBuffer = null;
			rangeBufferBytes = 0;
			uploadedRanges = Array.Empty<uint>();
		}

		#endregion

		#region PrivateMethods

		// The ranges only when they changed (most frames they do not).
		private void UploadRanges(int objectCount)
		{
			rangeBuffer ??= Gfx.Device.CreateBuffer(new BufferDescriptor(BufferUsage.Storage, 0, true, "Lumen scene mesh ranges"));
			int words = Math.Max(1, objectCount) * 2;
			int bytes = words * sizeof(uint);
			if (bytes > rangeBufferBytes)
			{
				rangeBuffer.SetData(ranges);
				rangeBufferBytes = ranges.Length * sizeof(uint);
			}
			else if (uploadedRanges.Length >= words && new ReadOnlySpan<uint>(ranges, 0, words).SequenceEqual(new ReadOnlySpan<uint>(uploadedRanges, 0, words)))
			{
				return;
			}
			else
			{
				rangeBuffer.SetSubData(0, new ReadOnlySpan<uint>(ranges, 0, words));
			}

			if (uploadedRanges.Length < ranges.Length)
			{
				uploadedRanges = new uint[ranges.Length];
			}

			Array.Copy(ranges, uploadedRanges, words);
		}

		#endregion
	}
}
