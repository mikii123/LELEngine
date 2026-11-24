using System;
using System.Collections.Generic;
using System.Text;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>What a pass asks of a frame's transient texture: its size, format, and how it is sampled and used.</summary>
	public readonly struct FrameTextureDescription : IEquatable<FrameTextureDescription>
	{
		public readonly int Width;
		public readonly int Height;
		public readonly RenderTextureFormat Format;

		/// <summary>Sampled with linear filtering (else the nearest texel), clamped to the edge.</summary>
		public readonly bool LinearFilter;

		/// <summary>Shaders also read or write it as an image (<see cref="CommandList.SetImage" />).</summary>
		public readonly bool Storage;

		public FrameTextureDescription(int width, int height, RenderTextureFormat format, bool linearFilter = true, bool storage = false)
		{
			Width = Math.Max(1, width);
			Height = Math.Max(1, height);
			Format = format;
			LinearFilter = linearFilter;
			Storage = storage;
		}

		public bool Equals(FrameTextureDescription other)
		{
			return Width == other.Width && Height == other.Height && Format == other.Format && LinearFilter == other.LinearFilter && Storage == other.Storage;
		}

		public override bool Equals(object obj)
		{
			return obj is FrameTextureDescription other && Equals(other);
		}

		public override int GetHashCode()
		{
			return HashCode.Combine(Width, Height, Format, LinearFilter, Storage);
		}
	}

	/// <summary>
	///     The frame's resources by name, as the passes declare them: before a frame executes, every pass that runs in it is
	///     asked what it makes and uses (<see cref="RenderPass.Setup" />), in the order the frame runs them (the frame's
	///     passes, every camera's, the final ones). From that the graph knows when each transient texture lives (from the
	///     pass that makes it to the last pass that reads it), skips the passes whose results nothing reads, and has the
	///     device place transient textures that never live at the same time in the same memory
	///     (<see cref="GraphicsDevice.CreateTransientTextures" />), which orders each one's first use in a frame after the
	///     uses of those it shares memory with, on either queue. A pass gets its textures by name while it executes
	///     (<see cref="Texture" />, <see cref="Target" />): the same objects frame after frame while what the passes declare
	///     stays the same.
	///     <para>
	///         Adoption is per pass: a pass that declares nothing (<see cref="RenderPass.Setup" /> left empty) keeps its own
	///         targets, and the device orders its uses as before. A transient texture is only for the passes that declare
	///         it: its contents are undefined when its maker begins (write it whole before reading it), and after the last
	///         pass that reads it another texture may hold its memory. What passes or code outside the graph read later (the
	///         materials of later passes, the next frame) is declared with <see cref="Export" /> and lives to the frame's end.
	///     </para>
	/// </summary>
	public sealed class FrameGraph
	{
		#region PublicFields

		/// <summary>The transient textures of the current plan.</summary>
		public int TransientTextures => planned.Count;

		/// <summary>The memory the transient textures take, and what they would take each in its own.</summary>
		public long TransientBytes { get; private set; }

		public long UnsharedBytes { get; private set; }

		/// <summary>Passes the last frame skipped because nothing read what they make.</summary>
		public int CulledPasses { get; private set; }

		/// <summary>Plans made (the transient textures made anew because what the passes declare changed).</summary>
		public int Plans { get; private set; }

		/// <summary>LEL_FRAME_GRAPH=0: the transient textures never share memory (each has its own, as on OpenGL), to compare.</summary>
		public static readonly bool Aliasing = Environment.GetEnvironmentVariable("LEL_FRAME_GRAPH") != "0";

		#endregion

		#region PrivateFields

		/// <summary>A pass of the frame being planned, in the order the frame runs them.</summary>
		private sealed class Step
		{
			public RenderPass Pass;
			public int Camera;
			public bool Declared;
			public bool SideEffects;
			public bool Culled;
			public readonly List<int> Reads = new List<int>();
			public readonly List<int> Writes = new List<int>();

			public void Reset(RenderPass pass, int camera)
			{
				Pass = pass;
				Camera = camera;
				Declared = false;
				SideEffects = false;
				Culled = false;
				Reads.Clear();
				Writes.Clear();
			}
		}

		/// <summary>A transient texture of the frame being planned.</summary>
		private sealed class Resource
		{
			public string Name;
			public int Camera;
			public FrameTextureDescription Description;
			public int First;
			public int Last;
			public bool Exported;
			public RenderTexture Texture;
		}

		private readonly List<Step> steps = new List<Step>();
		private int stepCount;
		private readonly List<Resource> resources = new List<Resource>();
		private readonly List<Resource> resourcePool = new List<Resource>();
		private bool[] needed = new bool[8];
		private readonly List<Resource> compared = new List<Resource>();
		private readonly Dictionary<(int Camera, string Name), int> byName = new Dictionary<(int Camera, string Name), int>();

		/// <summary>The textures of the current plan, and what they were made for (to tell when the next frame's plan differs).</summary>
		private readonly List<Resource> planned = new List<Resource>();

		private readonly List<GpuTexture> plannedTextures = new List<GpuTexture>();
		private readonly Dictionary<(RenderTexture, RenderTexture, RenderTexture, RenderTexture), Framebuffer> targets = new Dictionary<(RenderTexture, RenderTexture, RenderTexture, RenderTexture), Framebuffer>();

		/// <summary>The step being declared (setup) or executed; -1 outside both.</summary>
		private int current = -1;

		private bool executing;

		#endregion

		#region PublicMethods: declaring (RenderPass.Setup)

		/// <summary>
		///     A transient texture the declaring pass makes (writes first) this frame, named for the pass's camera. Its
		///     contents are undefined when the pass begins.
		/// </summary>
		public void CreateTexture(string name, in FrameTextureDescription description)
		{
			Step step = Declaring();
			(int, string) key = (step.Camera, name);
			if (byName.ContainsKey(key))
			{
				throw new InvalidOperationException($"Frame texture '{name}' is made twice in one frame.");
			}

			int index = resources.Count;
			// The frame's declarations reuse the last frame's objects: a frame allocates nothing while its plan stays.
			if (index == resourcePool.Count)
			{
				resourcePool.Add(new Resource());
			}

			Resource resource = resourcePool[index];
			resource.Name = name;
			resource.Camera = step.Camera;
			resource.Description = description;
			resource.First = current;
			resource.Last = current;
			resource.Exported = false;
			resource.Texture = null;
			resources.Add(resource);
			byName.Add(key, index);
			step.Writes.Add(index);
		}

		/// <summary>The declaring pass reads a texture an earlier pass of the frame made (nothing when none made it this frame).</summary>
		public void Read(string name)
		{
			Use(name, false);
		}

		/// <summary>The declaring pass writes a texture an earlier pass of the frame made.</summary>
		public void Write(string name)
		{
			Use(name, true);
		}

		/// <summary>
		///     A texture is read outside the graph after the passes that declare it (by passes that do not declare it, the
		///     next frame, the CPU): it lives to the frame's end and shares memory with nothing made after it.
		/// </summary>
		public void Export(string name)
		{
			Declaring();
			if (byName.TryGetValue((steps[current].Camera, name), out int index))
			{
				resources[index].Exported = true;
			}
		}

		/// <summary>The declaring pass does something besides its declared writes (it is never skipped).</summary>
		public void SideEffects()
		{
			Declaring().SideEffects = true;
		}

		/// <summary>Whether a texture of that name was made this frame for the pass's camera (declared, not skipped).</summary>
		public bool Has(string name)
		{
			int camera = current >= 0 ? steps[current].Camera : 0;
			return byName.TryGetValue((camera, name), out int index) && resources[index].Texture != null;
		}

		#endregion

		#region PublicMethods: executing (RenderPass.Execute)

		/// <summary>A transient texture of the executing pass's camera (declared by it in its setup); null when none was made.</summary>
		public RenderTexture Texture(string name)
		{
			if (!executing || current < 0)
			{
				throw new InvalidOperationException("Frame textures are given to the passes that declared them, while they execute.");
			}

			return byName.TryGetValue((steps[current].Camera, name), out int index) ? resources[index].Texture : null;
		}

		/// <summary>
		///     A render target of transient textures of the executing pass's camera: up to three colour attachments and a depth
		///     attachment (null: none), the same object while the textures are.
		/// </summary>
		public Framebuffer Target(string color, string color2 = null, string color3 = null, string depth = null)
		{
			RenderTexture a = color != null ? Texture(color) : null;
			RenderTexture b = color2 != null ? Texture(color2) : null;
			RenderTexture c = color3 != null ? Texture(color3) : null;
			RenderTexture d = depth != null ? Texture(depth) : null;
			var key = (a, b, c, d);
			if (!targets.TryGetValue(key, out Framebuffer target))
			{
				RenderTexture any = a ?? d ?? throw new ArgumentException("A target needs an attachment.");
				target = new Framebuffer(any.Width, any.Height);
				foreach (RenderTexture attachment in new[] { a, b, c })
				{
					if (attachment != null)
					{
						target.AttachColor(attachment);
					}
				}

				if (d != null)
				{
					target.AttachSharedDepth(d);
				}

				target.Validate();
				targets.Add(key, target);
			}

			return target;
		}

		#endregion

		#region Internal: the renderer's

		/// <summary>The frame's declarations begin.</summary>
		internal void BeginSetup()
		{
			stepCount = 0;
			resources.Clear();
			byName.Clear();
			executing = false;
			current = -1;
		}

		/// <summary>The next pass of the frame (in the order it will run) declares: <paramref name="camera" /> names its textures.</summary>
		internal void BeginStep(RenderPass pass, int camera)
		{
			if (stepCount == steps.Count)
			{
				steps.Add(new Step());
			}

			steps[stepCount].Reset(pass, camera);
			current = stepCount++;
		}

		/// <summary>
		///     The declarations are complete: lifetimes, the passes nothing needs, and the textures (the plan of the last frame
		///     when nothing changed, else a new one).
		/// </summary>
		internal void Compile()
		{
			current = -1;
			// Backwards: a declared pass without other effects whose writes nothing later reads (or exports) is skipped, and
			// what it reads is not needed by it.
			if (needed.Length < resources.Count)
			{
				needed = new bool[resources.Count * 2];
			}

			for (int r = 0; r < resources.Count; r++)
			{
				needed[r] = resources[r].Exported;
			}

			CulledPasses = 0;
			for (int s = stepCount - 1; s >= 0; s--)
			{
				Step step = steps[s];
				if (step.Declared && !step.SideEffects && step.Writes.Count > 0)
				{
					bool any = false;
					foreach (int w in step.Writes)
					{
						any |= needed[w];
					}

					if (!any)
					{
						step.Culled = true;
						CulledPasses++;
						continue;
					}
				}

				foreach (int r in step.Reads)
				{
					needed[r] = true;
				}
			}

			// Lifetimes from the passes that run.
			for (int r = 0; r < resources.Count; r++)
			{
				resources[r].First = int.MaxValue;
				resources[r].Last = -1;
			}

			for (int s = 0; s < stepCount; s++)
			{
				Step step = steps[s];
				if (step.Culled)
				{
					continue;
				}

				foreach (int r in step.Writes)
				{
					Touch(resources[r], s);
				}

				foreach (int r in step.Reads)
				{
					Touch(resources[r], s);
				}
			}

			foreach (Resource resource in resources)
			{
				if (resource.Exported && resource.Last >= 0)
				{
					resource.Last = stepCount;
				}
			}

			Plan();
		}

		/// <summary>The next pass runs; true when the graph skips it (nothing reads what it makes).</summary>
		internal bool EnterStep(RenderPass pass)
		{
			executing = true;
			for (int next = current + 1; next < stepCount; next++)
			{
				if (steps[next].Pass == pass)
				{
					current = next;
					return steps[next].Culled;
				}
			}

			// Not a pass of the plan (one switched on during the frame): it runs, without frame textures.
			current = -1;
			return false;
		}

		/// <summary>The frame has executed.</summary>
		internal void EndFrame()
		{
			executing = false;
			current = -1;
		}

		/// <summary>Frees the textures (the renderer is disposed).</summary>
		internal void Release()
		{
			FreePlan();
			planned.Clear();
		}

		#endregion

		#region PrivateMethods

		private Step Declaring()
		{
			if (executing || current < 0)
			{
				throw new InvalidOperationException("Frame resources are declared in RenderPass.Setup.");
			}

			Step step = steps[current];
			step.Declared = true;
			return step;
		}

		private void Use(string name, bool write)
		{
			Step step = Declaring();
			if (!byName.TryGetValue((step.Camera, name), out int index))
			{
				return;
			}

			(write ? step.Writes : step.Reads).Add(index);
		}

		private static void Touch(Resource resource, int step)
		{
			resource.First = Math.Min(resource.First, step);
			resource.Last = Math.Max(resource.Last, step);
		}

		/// <summary>The textures for this frame's declarations: last frame's when they declare the same, else new ones.</summary>
		private void Plan()
		{
			// Only what a running pass uses gets a texture.
			int used = 0;
			foreach (Resource resource in resources)
			{
				if (resource.Last >= 0)
				{
					used++;
				}
			}

			if (SamePlan(used))
			{
				// The textures are last frame's (the same names, sizes and overlaps in time).
				foreach (Resource resource in resources)
				{
					if (resource.Last >= 0)
					{
						resource.Texture = planned[IndexOfPlanned(resource)].Texture;
					}
				}

				return;
			}

			FreePlan();
			planned.Clear();
			TextureDescriptor[] descriptors = new TextureDescriptor[used];
			(int First, int Last)[] lifetimes = new (int First, int Last)[used];
			List<Resource> made = new List<Resource>();
			foreach (Resource resource in resources)
			{
				if (resource.Last < 0)
				{
					continue;
				}

				FrameTextureDescription d = resource.Description;
				TextureUsage usage = TextureUsage.Sampled | TextureUsage.Attachment | (d.Storage ? TextureUsage.Storage : 0);
				descriptors[made.Count] = TextureDescriptor.Texture2D(RenderTexture.TextureFormatOf(d.Format), d.Width, d.Height, 1, usage, "Frame: " + resource.Name);
				// Without sharing, every texture lives the whole frame (none overlaps another's memory).
				lifetimes[made.Count] = Aliasing ? (resource.First, resource.Last) : (0, int.MaxValue);
				made.Add(resource);
			}

			long bytes = 0;
			long unshared = 0;
			GpuTexture[] textures = used > 0 ? Gfx.Device.CreateTransientTextures(descriptors, lifetimes, out bytes, out unshared) : Array.Empty<GpuTexture>();
			for (int i = 0; i < made.Count; i++)
			{
				Resource resource = made[i];
				FrameTextureDescription d = resource.Description;
				resource.Texture = new RenderTexture(textures[i], d.Format, d.LinearFilter, SamplerAddress.ClampToEdge, d.Storage);
				planned.Add(new Resource { Name = resource.Name, Camera = resource.Camera, Description = d, First = resource.First, Last = resource.Last, Texture = resource.Texture });
				plannedTextures.Add(textures[i]);
			}

			TransientBytes = bytes;
			UnsharedBytes = unshared;
			Plans++;
			if (used > 0)
			{
				StringBuilder line = new StringBuilder($"[FrameGraph] {used} transient textures: {bytes / 1048576.0:0.0} MB of memory ({unshared / 1048576.0:0.0} MB each in its own)");
				foreach (Resource resource in made)
				{
					line.Append($" | {resource.Name} {resource.Description.Width}x{resource.Description.Height} {resource.Description.Format} passes {resource.First}-{(resource.Last >= stepCount ? "end" : resource.Last.ToString())}");
				}

				Console.WriteLine(line.ToString());
			}
		}

		/// <summary>Whether this frame's textures are the planned ones: the same names, descriptions and overlaps in time.</summary>
		private bool SamePlan(int used)
		{
			if (used != planned.Count || planned.Count == 0 && used == 0)
			{
				return used == planned.Count;
			}

			List<Resource> now = compared;
			now.Clear();
			foreach (Resource resource in resources)
			{
				if (resource.Last < 0)
				{
					continue;
				}

				int index = IndexOfPlanned(resource);
				if (index < 0 || !planned[index].Description.Equals(resource.Description))
				{
					return false;
				}

				now.Add(resource);
			}

			// The same pairs live at the same time (the memory each shares stays free of the others it lives with).
			for (int i = 0; i < now.Count; i++)
			{
				Resource a = now[i];
				Resource pa = planned[IndexOfPlanned(a)];
				for (int j = i + 1; j < now.Count; j++)
				{
					Resource b = now[j];
					Resource pb = planned[IndexOfPlanned(b)];
					bool overlapNow = a.First <= b.Last && b.First <= a.Last;
					bool overlapPlanned = pa.First <= pb.Last && pb.First <= pa.Last;
					if (overlapNow != overlapPlanned)
					{
						return false;
					}
				}
			}

			return true;
		}

		private int IndexOfPlanned(Resource resource)
		{
			for (int i = 0; i < planned.Count; i++)
			{
				if (planned[i].Camera == resource.Camera && planned[i].Name == resource.Name)
				{
					return i;
				}
			}

			return -1;
		}

		/// <summary>The current plan's textures go (once the GPU has finished with them) with the render targets made of them.</summary>
		private void FreePlan()
		{
			foreach (Framebuffer target in targets.Values)
			{
				target.Delete();
			}

			targets.Clear();
			foreach (GpuTexture texture in plannedTextures)
			{
				texture.Dispose();
			}

			plannedTextures.Clear();
		}

		#endregion
	}
}
