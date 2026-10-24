using System.Collections.Generic;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Rendering.Passes
{
	/// <summary>
	///     Forward shading of the transparent sub-meshes (materials in a queue above <see cref="MaterialQueue.GeometryLast" />)
	///     over the opaque image and the sky: sorted by queue, then back to front by the distance from the camera to
	///     the object's bounds centre, drawn one by one with the shader's blend, depth and cull state and no depth
	///     writes unless the shader asks (ZWrite On). Double-sided materials (Cull Off) draw their back faces before
	///     their front faces, so a convex object composites its inside behind its outside. The fragments are not in
	///     the depth buffer, so the GI comes from the volume (Lighting.SetVolumeGIUniforms, Engine/VolumeLighting.glsl)
	///     rather than the screen-space resolve. These renderers never go through the GPU-driven tables; their shadows
	///     are drawn by <see cref="TransparentShadows" />.
	///     Trails (<see cref="TrailRenderer" />, found among the frame's behaviours on the camera's layers) are built for
	///     this camera into one stream of world space triangles (<see cref="DynamicGeometry" />), uploaded once, and sort
	///     like meshes: each material of a trail is an entry at the centre of the trail's bounds (opaque materials too,
	///     before the transparent queues).
	///     Materials whose variant declares GrabPass read the scene drawn before them (<see cref="SceneGrab" />, Unity's
	///     GrabPass, e.g. refraction): the copy they read is taken after the last earlier draw that touched the part of the
	///     screen they read, and one copy serves a run of such objects whose parts nothing drawn in between overlaps.
	///     Materials whose variant declares SceneDepth (soft particles) read the depth copy of the opaque scene that the
	///     particle effects read too (<see cref="SceneDepthTexture" />), taken once before the transparent draws.
	/// </summary>
	public sealed class TransparentPass : RenderPass
	{
		#region PublicFields

		public override string Name => "Transparent";

		public override RenderPassScope Scope => RenderPassScope.EveryCamera;

		/// <summary>Transparent objects drawn last frame (stats).</summary>
		public int DrawnCount { get; private set; }

		/// <summary>The scene copies GrabPass materials read (stats: copies and pixels of the last frame).</summary>
		public SceneGrab Grab { get; } = new SceneGrab();

		#endregion

		#region PrivateFields

		// Rectangles one copy may take before it is cheaper to start another; draws remembered since a copy before they
		// merge into their bounding rectangle (cheap planning with many transparent objects, never a missed copy).
		private const int MaxCopyRects = 16;
		private const int MaxDrawnRects = 32;

		private struct Entry
		{
			public MeshRenderer Renderer;
			public int SubMesh;
			public VFX.VfxDrawEntry Vfx;

			/// <summary>A trail's material slot (<see cref="SubMesh" /> is the slot): its triangles are in the trail stream.</summary>
			public TrailRenderer Trail;

			/// <summary>The material drawn (mesh and trail entries).</summary>
			public Material Material;
			public int Queue;
			public float DistanceSquared;
			public ulong Order;

			/// <summary>The renderer's local-to-world matrix (mesh entries).</summary>
			public Matrix4 World;

			/// <summary>Trail entries: world bounds and the range of the trail stream's indices.</summary>
			public Vector3 BoundsMin;
			public Vector3 BoundsMax;
			public int FirstIndex;
			public int IndexCount;

			/// <summary>The material's variant declares GrabPass: it reads the scene drawn before it.</summary>
			public bool Grab;

			/// <summary>Index of the scene copy taken right before this entry draws, or -1.</summary>
			public int GrabCopy;

			/// <summary>Grab entries: the pixels their lookups read (kept inside by the shader).</summary>
			public PixelRect Readable;
		}

		// One scene copy: rectangles Start .. Start + Count - 1 of copyRects.
		private struct GrabCopy
		{
			public int Start;
			public int Count;
		}

		private readonly List<Entry> entries = new List<Entry>();
		private readonly List<VFX.VfxDrawEntry> vfxEntries = new List<VFX.VfxDrawEntry>();
		private readonly List<GrabCopy> grabCopies = new List<GrabCopy>();
		private readonly List<PixelRect> copyRects = new List<PixelRect>();
		private readonly List<PixelRect> drawnSinceCopy = new List<PixelRect>();

		// This camera's trails: built into one stream, uploaded once.
		private readonly LineGeometryBuilder trailBuilder = new LineGeometryBuilder();
		private readonly DynamicGeometry trailGeometry = new DynamicGeometry();

		#endregion

		#region PublicMethods

		public override void Execute(RenderContext context)
		{
			IReadOnlyList<MeshRenderer> renderers = context.TransparentRenderers;
			DrawnCount = 0;
			Grab.BeginFrame();
			bool grabs = Collect(renderers, context.CameraPosition);
			grabs |= CollectTrails(context);
			vfxEntries.Clear();
			VFX.VfxRuntime.CollectTransparent(context, vfxEntries);
			if (entries.Count == 0 && vfxEntries.Count == 0)
			{
				return;
			}

			// Materials reading the opaque scene's depth (soft particles) share the depth copy the particle effects take.
			bool readsDepth = false;
			foreach (Entry entry in entries)
			{
				readsDepth |= entry.Material.RenderState.SceneDepth;
			}

			// Particle outputs sort among the meshes by queue and distance (their bounds centre).
			foreach (VFX.VfxDrawEntry vfx in vfxEntries)
			{
				entries.Add(new Entry { Vfx = vfx, Queue = vfx.Queue, DistanceSquared = vfx.DistanceSquared, Order = (ulong)(vfx.System * 64 + vfx.Output), GrabCopy = -1 });
			}

			SortEntries();
			if (grabs)
			{
				PlanGrabs(context);
			}

			// The view block of the particle draws and the back-to-front sorts of their sorted outputs, in one batch.
			if (vfxEntries.Count > 0)
			{
				context.Renderer.Profiler.Split("Transparent.VfxSort");
				VFX.VfxRuntime.PrepareDraws(context, vfxEntries);
				context.Renderer.Profiler.Split("Transparent.Draw");
			}

			context.SceneTarget.Bind();

			// The scene depth with the opaques for soft particles (and the next simulation's depth collisions).
			VFX.VfxDepthCapture depth = null;
			if ((vfxEntries.Count > 0 && VFX.VfxRuntime.NeedsDepth) || readsDepth)
			{
				depth = VFX.VfxRuntime.CaptureDepth(context);
				context.SceneTarget.Bind();
			}

			ShaderProgram active = null;
			bool receiveShadows = false;
			bool receiveGI = false;
			bool resend = true;
			bool afterParticles = false;
			bool afterMesh = false;
			foreach (Entry entry in entries)
			{
				if (entry.Vfx != null)
				{
					// Consecutive particle draws skip the state they share; a mesh drawn in between resets that.
					if (afterMesh)
					{
						VFX.VfxRuntime.InvalidateDrawState();
						afterMesh = false;
					}

					entry.Vfx.Instance.Draw(context, entry.Vfx.System, entry.Vfx.Output, false, depth);
					afterParticles = true;
					active = null;
					resend = true;
					DrawnCount++;
					continue;
				}

				if (afterParticles)
				{
					GLState.SetDepth(true, false, DepthFunction.Lequal);
					afterParticles = false;
				}

				afterMesh = true;
				MeshRenderer renderer = entry.Renderer;
				TrailRenderer trail = entry.Trail;
				Material material = entry.Material;
				ShaderProgram program = material.Program;
				bool entryShadows = trail != null ? trail.receiveShadows : renderer.ReceiveShadows;
				bool entryGI = trail != null ? trail.receiveGI : renderer.ReceiveGI;
				bool copied = false;
				if (entry.GrabCopy >= 0)
				{
					// The scene as drawn so far, for this entry and the later ones that share the copy.
					GrabCopy copy = grabCopies[entry.GrabCopy];
					Grab.Copy(context, copyRects, copy.Start, copy.Count);
					copied = true;
				}

				if (program != active)
				{
					program.Use();
					active = program;
					resend = true;
				}
				else if (copied)
				{
					// The copy bound its own program; this one keeps its uniforms.
					program.Use();
				}

				// Camera and lighting uniforms once per program and flag combination, as the opaque pass does.
				if (resend || entryShadows != receiveShadows || entryGI != receiveGI)
				{
					context.Camera.SetUniforms(program);
					Lighting.SetUniforms(program, entryShadows, false);
					Lighting.SetVolumeGIUniforms(program, entryGI);
					receiveShadows = entryShadows;
					receiveGI = entryGI;
					resend = false;
				}

				if (trail != null)
				{
					// Trail vertices are in world space.
					program.SetMatrix4("modelMatrix", Matrix4.Identity);
				}
				else
				{
					renderer.transform.SetModelMatrix(program);
				}

				material.SetUniforms();
				if (material.OverridesSharedUniforms())
				{
					resend = true;
				}

				if (entry.Grab)
				{
					Grab.SetUniforms(program, context, entry.Readable);
				}

				ShaderRenderState state = material.RenderState;
				if (state.SceneDepth)
				{
					SceneDepthTexture.SetUniforms(program, context, depth);
				}

				state.ApplyTransparent();
				if (state.Cull == CullMode.Off)
				{
					// Back faces first, then front faces: the inside of a convex object ends up behind its outside.
					GLState.SetCull(true, TriangleFace.Front);
					DrawGeometry(entry);
					GLState.SetCull(true, TriangleFace.Back);
				}

				DrawGeometry(entry);
				DrawnCount++;
			}

			entries.Clear();
			vfxEntries.Clear();
			GLState.SetBlend(false);
			GLState.SetDepth(true, true, DepthFunction.Less);
			GLState.SetCull(true, TriangleFace.Back);
			GLState.Reset();
		}

		public override void ReleaseSceneReferences()
		{
			entries.Clear();
			vfxEntries.Clear();
			// The new scene may grab nothing: its copy texture is made again on its first grab.
			Grab.Release();
		}

		public override void Dispose()
		{
			Grab.Delete();
			trailGeometry.Delete();
		}

		#endregion

		#region PrivateMethods

		// Drawable renderers with their sort keys: queue first, then far to near (the centre of the mesh bounds in
		// the world, like Unity's default transparent sorting). True when one of them grabs the scene.
		private bool Collect(IReadOnlyList<MeshRenderer> renderers, Vector3 cameraPosition)
		{
			entries.Clear();
			if (renderers == null)
			{
				return false;
			}

			bool grabs = false;
			foreach (MeshRenderer renderer in renderers)
			{
				Mesh mesh = renderer.Mesh;
				if (mesh == null)
				{
					continue;
				}

				Matrix4 world = renderer.transform.LocalToWorld;
				Vector3 center = Vector3.TransformPosition((mesh.BoundsMin + mesh.BoundsMax) * 0.5f, world);
				float distanceSquared = (center - cameraPosition).LengthSquared;
				for (int i = 0; i < mesh.SubMeshCount; i++)
				{
					// Each transparent sub-mesh sorts on its own (same distance: the queue, then the slot, order them).
					Material material = renderer.GetMaterial(i);
					if (material?.Program == null || !material.IsTransparent || !material.Program.IsLinked || mesh.SubMeshes[i].IndexCount == 0)
					{
						continue;
					}

					bool grab = material.RenderState.GrabPass;
					grabs |= grab;
					entries.Add(new Entry { Renderer = renderer, SubMesh = i, Material = material, Queue = material.renderQueue, DistanceSquared = distanceSquared, Order = renderer.Id * 64 + (ulong)i, World = world, Grab = grab, GrabCopy = -1 });
				}
			}

			return grabs;
		}

		// The trails among the frame's behaviours on the camera's layers: each built for this camera into the trail stream
		// (uploaded here), one entry per drawable material at the centre of its bounds. True when one of them grabs.
		private bool CollectTrails(RenderContext context)
		{
			trailBuilder.Clear();
			IReadOnlyList<Behaviour> behaviours = context.Behaviours;
			if (behaviours == null)
			{
				return false;
			}

			bool grabs = false;
			// A perspective projection puts -z into w; an orthographic one keeps w = 1 (row vectors: column 3).
			bool orthographic = System.MathF.Abs(context.Projection.M34) < 1e-6f;
			foreach (Behaviour behaviour in behaviours)
			{
				if (behaviour is not TrailRenderer trail || !trail.isActiveAndEnabled || (context.CullingMask & (1 << (trail.gameObject.layer & 31))) == 0 || !HasDrawableMaterial(trail))
				{
					continue;
				}

				if (!trail.BuildGeometry(trailBuilder, context.CameraPosition, context.CameraForward, orthographic, out int firstIndex, out int indexCount, out Vector3 min, out Vector3 max))
				{
					continue;
				}

				float distanceSquared = ((min + max) * 0.5f - context.CameraPosition).LengthSquared;
				for (int i = 0; i < trail.Materials.Count; i++)
				{
					Material material = trail.Materials[i];
					if (!IsDrawable(material))
					{
						continue;
					}

					bool grab = material.RenderState.GrabPass;
					grabs |= grab;
					entries.Add(new Entry { Trail = trail, SubMesh = i, Material = material, Queue = material.renderQueue, DistanceSquared = distanceSquared, Order = trail.Id * 64 + (ulong)i, BoundsMin = min, BoundsMax = max, FirstIndex = firstIndex, IndexCount = indexCount, Grab = grab, GrabCopy = -1 });
				}
			}

			trailGeometry.Upload(trailBuilder.Vertices, trailBuilder.VertexCount, trailBuilder.Indices, trailBuilder.IndexCount);
			return grabs;
		}

		private static bool HasDrawableMaterial(TrailRenderer trail)
		{
			foreach (Material material in trail.Materials)
			{
				if (IsDrawable(material))
				{
					return true;
				}
			}

			return false;
		}

		private static bool IsDrawable(Material material)
		{
			return material?.Program != null && material.Program.IsLinked;
		}

		private void DrawGeometry(Entry entry)
		{
			if (entry.Trail != null)
			{
				trailGeometry.Draw(entry.FirstIndex, entry.IndexCount);
			}
			else
			{
				entry.Renderer.DrawSubMesh(entry.SubMesh);
			}
		}

		/// <summary>
		///     Decides where the scene copies go, in draw order: a grab entry reads the scene as drawn before it, so its copy
		///     must come after every earlier draw that touched the pixels it reads. It joins the latest copy (which then
		///     also copies its rectangle) when nothing drawn since that copy overlaps them; otherwise a new copy is taken
		///     right before it. A grab entry that covers no pixel draws nothing and takes no copy.
		/// </summary>
		private void PlanGrabs(RenderContext context)
		{
			grabCopies.Clear();
			copyRects.Clear();
			drawnSinceCopy.Clear();
			var screen = new PixelRect(0, 0, context.Width, context.Height);
			int current = -1;
			for (int i = 0; i < entries.Count; i++)
			{
				Entry entry = entries[i];
				PixelRect drawn = DrawnRect(entry, context, screen, out float nearestW);
				if (entry.Grab)
				{
					entry.Readable = ReadableRect(entry, drawn, nearestW, context, screen);
					if (entry.Readable.IsEmpty)
					{
						entry.Grab = false;
					}
					else if (current >= 0 && grabCopies[current].Count < MaxCopyRects && !OverlapsDrawn(entry.Readable))
					{
						AddCopyRect(current, entry.Readable);
					}
					else
					{
						current = grabCopies.Count;
						grabCopies.Add(new GrabCopy { Start = copyRects.Count });
						AddCopyRect(current, entry.Readable);
						entry.GrabCopy = current;
						drawnSinceCopy.Clear();
					}

					entries[i] = entry;
				}

				if (current >= 0)
				{
					AddDrawn(drawn);
				}
			}
		}

		// The pixels an entry draws: its bounds on the screen (the whole screen when they are unknown or cross the camera
		// plane), and the clip w of their nearest corner.
		private static PixelRect DrawnRect(Entry entry, RenderContext context, PixelRect screen, out float nearestW)
		{
			PixelRect rect;
			if (entry.Vfx != null)
			{
				if (entry.Vfx.Instance.TryGetSystemBounds(entry.Vfx.System, out Vector3 min, out Vector3 max) && SceneGrab.ScreenRect(context.ViewProjection, min, max, screen, out rect, out nearestW))
				{
					return rect;
				}

				nearestW = 0f;
				return screen;
			}

			if (entry.Trail != null)
			{
				return SceneGrab.ScreenRect(context.ViewProjection, entry.BoundsMin, entry.BoundsMax, screen, out rect, out nearestW) ? rect : screen;
			}

			Mesh mesh = entry.Renderer.Mesh;
			return SceneGrab.ScreenRect(entry.World * context.ViewProjection, mesh.BoundsMin, mesh.BoundsMax, screen, out rect, out nearestW) ? rect : screen;
		}

		// The pixels a grab entry reads: where it draws, widened by the largest shift its material's offset property allows
		// (the whole screen for GrabPass On: the shader may read anywhere).
		private static PixelRect ReadableRect(Entry entry, PixelRect drawn, float nearestW, RenderContext context, PixelRect screen)
		{
			if (drawn.IsEmpty)
			{
				return drawn;
			}

			Material material = entry.Material;
			ShaderProgram program = material.Program;
			string offsetProperty = program.RenderState.GrabOffsetProperty;
			if (offsetProperty == null)
			{
				return screen;
			}

			// A material saved before the shader had the property draws with its declared default (Material.SetUniforms).
			float strength = material.GetFloat(offsetProperty, program.FindProperty(offsetProperty)?.DefaultValue.X ?? 0f);
			return SceneGrab.Widen(drawn, strength, nearestW, context.Projection, screen);
		}

		private bool OverlapsDrawn(PixelRect rect)
		{
			foreach (PixelRect drawn in drawnSinceCopy)
			{
				if (drawn.Intersects(rect))
				{
					return true;
				}
			}

			return false;
		}

		// Adds a rectangle to the latest copy (its rectangles end the list), merged with the ones it overlaps so no pixel is
		// copied twice.
		private void AddCopyRect(int copyIndex, PixelRect rect)
		{
			GrabCopy copy = grabCopies[copyIndex];
			int j = copy.Start;
			while (j < copy.Start + copy.Count)
			{
				if (copyRects[j].Intersects(rect))
				{
					rect = rect.Union(copyRects[j]);
					copyRects.RemoveAt(j);
					copy.Count--;
					// The union may reach rectangles checked before.
					j = copy.Start;
					continue;
				}

				j++;
			}

			copyRects.Add(rect);
			copy.Count++;
			grabCopies[copyIndex] = copy;
		}

		private void AddDrawn(PixelRect rect)
		{
			if (rect.IsEmpty)
			{
				return;
			}

			if (drawnSinceCopy.Count >= MaxDrawnRects)
			{
				// Many draws since the copy: their bounding rectangle stands for them (later entries may take a copy of
				// their own more often, never read a stale part).
				foreach (PixelRect drawn in drawnSinceCopy)
				{
					rect = rect.Union(drawn);
				}

				drawnSinceCopy.Clear();
			}

			drawnSinceCopy.Add(rect);
		}

		private void SortEntries()
		{
			// List.Sort is not stable; the order key settles ties so the order is the same every frame.
			entries.Sort((a, b) =>
			{
				if (a.Queue != b.Queue) return a.Queue.CompareTo(b.Queue);
				int byDistance = b.DistanceSquared.CompareTo(a.DistanceSquared);
				return byDistance != 0 ? byDistance : a.Order.CompareTo(b.Order);
			});
		}

		#endregion
	}
}
