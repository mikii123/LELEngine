using System;
using System.Collections.Generic;
using System.Text;
using Brutal.GlfwApi;
using Brutal.OpenGlApi;
using LELEngine.Windowing;
using static Brutal.OpenGlApi.OpenGl;

namespace LELEngine.Graphics.OpenGL
{
	/// <summary>
	///     The device on OpenGL (4.3 or later). OpenGL has no passes, pipelines or binding sets: the device lays the
	///     engine's commands over OpenGL's state machine as they are given (a pass binds a framebuffer object made
	///     for its attachments and clears it, a draw applies what differs in the state, binds the program's textures
	///     to texture units and a vertex array made for the layout). Objects belong to the windows' shared contexts.
	/// </summary>
	internal sealed unsafe class GLDevice : GraphicsDevice
	{
		[ThreadStatic]
		private static GLContext currentContext;

		private readonly object sync = new object();
		private readonly Dictionary<SamplerDescriptor, GLSampler> samplers = new Dictionary<SamplerDescriptor, GLSampler>();
		private readonly List<int> deadTextures = new List<int>();
		private readonly List<int> deadBuffers = new List<int>();
		private readonly List<int> deadPrograms = new List<int>();
		private readonly HashSet<string> extensions = new HashSet<string>(StringComparer.Ordinal);
		private bool initialized;
		private bool labels;

		/// <summary>Counts textures deleted: contexts drop the framebuffer objects made for them when it moves.</summary>
		internal int TextureDeletions;

		/// <summary>The names of the textures deleted last (read under <see cref="Sync" />), and how often the list started over.</summary>
		internal readonly List<int> DeletedTextures = new List<int>();

		internal int DeletedTexturesEpoch;
		internal object Sync => sync;

		/// <summary>OpenGL's functions (the BRUTAL framework's bindings, loaded for the first context).</summary>
		internal static OpenGLBackend Gl { get; private set; }

		internal new static GLDevice Current { get; private set; }

		internal int Major { get; private set; }
		internal int Minor { get; private set; }
		internal bool ParallelCompileKhr { get; private set; }
		internal bool ParallelCompileArb { get; private set; }

		/// <summary>The texture unit textures are bound to while they are made, written or read (never used by a draw).</summary>
		internal int ScratchUnit { get; private set; }

		public override GraphicsApi Api => GraphicsApi.OpenGL;

		public override CommandList Commands => CommandsOfThread;

		internal GLCommandList CommandsOfThread
		{
			get
			{
				GLContext context = currentContext;
				if (context == null)
				{
					throw new InvalidOperationException("No window's graphics context is current on this thread: it cannot give graphics commands.");
				}

				return context.Commands;
			}
		}

		public override GpuTexture Backbuffer => currentContext?.Backbuffer;
		public override bool HasCurrentContext => currentContext != null;

		internal bool IsAtLeast(int major, int minor)
		{
			return Major > major || (Major == major && Minor >= minor);
		}

		internal bool HasExtension(string name)
		{
			return extensions.Contains(name);
		}

		internal static void SetCurrent(GLContext context)
		{
			currentContext = context;
		}

		internal static GLContext ContextOfThread => currentContext;

		internal override GraphicsContext CreateWindowContext(NativeWindow window, NativeWindowSettings settings)
		{
			GLContext context = new GLContext(this, window);
			context.MakeCurrent();
			if (!initialized)
			{
				Initialize();
			}

			context.Start();
			return context;
		}

		/// <summary>A context for a window made outside <see cref="NativeWindow" /> (the shader compile thread's hidden window).</summary>
		internal GLContext CreateContext(GlfwWindow window)
		{
			return new GLContext(this, window);
		}

		public override GpuTexture CreateTexture(in TextureDescriptor descriptor)
		{
			return new GLTexture(descriptor);
		}

		public override GpuBuffer CreateBuffer(in BufferDescriptor descriptor)
		{
			return new GLBuffer(descriptor);
		}

		public override GpuSampler GetSampler(in SamplerDescriptor descriptor)
		{
			lock (sync)
			{
				if (!samplers.TryGetValue(descriptor, out GLSampler sampler))
				{
					sampler = new GLSampler(descriptor, Capabilities.MaxAnisotropy);
					samplers.Add(descriptor, sampler);
				}

				return sampler;
			}
		}

		public override GpuProgram CreateProgram(ProgramDescriptor descriptor)
		{
			return new GLProgram(this, descriptor);
		}

		public override GpuProgram CreateProgramFromBinary(ProgramDescriptor descriptor, byte[] binary)
		{
			return GLProgram.FromBinary(this, descriptor, binary);
		}

		public override GpuFence CreateFence()
		{
			return new GLFence();
		}

		public override GpuTimestamp CreateTimestamp()
		{
			return new GLTimestamp();
		}

		public override GpuAccelerationStructure CreateAccelerationStructure(in AccelerationStructureDescriptor descriptor)
		{
			throw new NotSupportedException("OpenGL has no acceleration structures (DeviceCapabilities.RayQuery is false).");
		}

		public override void Flush()
		{
			Gl.Flush();
		}

		public override void Finish()
		{
			Gl.Finish();
		}

		#region Object lifetimes

		internal void BindForEdit(GLTexture texture)
		{
			CommandsOfThread.BindForEdit(texture);
		}

		internal void ReadTexture(GLTexture texture, int mip, int x, int y, int z, int width, int height, int depth, void* destination, int size, GLBuffer packBuffer, long packOffset)
		{
			CommandsOfThread.ReadTexture(texture, mip, x, y, z, width, height, depth, destination, size, packBuffer, packOffset);
		}

		internal void Release(GLTexture texture)
		{
			lock (sync)
			{
				deadTextures.Add(texture.Handle);
				// The log of deleted names is kept short: contexts that fall behind it drop every framebuffer object.
				if (DeletedTextures.Count >= 1024)
				{
					DeletedTextures.Clear();
					DeletedTexturesEpoch++;
				}

				DeletedTextures.Add(texture.Handle);
				TextureDeletions++;
			}

			if (currentContext != null)
			{
				CollectGarbage();
			}
		}

		internal void Release(GLBuffer buffer)
		{
			ReleaseBuffer(buffer.Handle);
		}

		internal void ReleaseBuffer(int handle)
		{
			lock (sync)
			{
				deadBuffers.Add(handle);
			}

			if (currentContext != null)
			{
				CollectGarbage();
			}
		}

		internal void ReleaseProgram(int program)
		{
			lock (sync)
			{
				deadPrograms.Add(program);
			}

			if (currentContext != null)
			{
				CollectGarbage();
			}
		}

		/// <summary>Deletes what was released on threads without a context; called with a context current.</summary>
		internal void CollectGarbage()
		{
			lock (sync)
			{
				if (deadTextures.Count == 0 && deadBuffers.Count == 0 && deadPrograms.Count == 0)
				{
					return;
				}

				GLContext context = currentContext;
				foreach (int texture in deadTextures)
				{
					context.Commands.Forget(texture, false);
					Gl.DeleteTexture(texture);
				}

				foreach (int buffer in deadBuffers)
				{
					context.Commands.Forget(buffer, true);
					Gl.DeleteBuffer(buffer);
				}

				foreach (int program in deadPrograms)
				{
					context.Commands.ForgetProgram(program);
					Gl.DeleteProgram(program);
				}

				deadTextures.Clear();
				deadBuffers.Clear();
				deadPrograms.Clear();
			}
		}

		#endregion

		/// <summary>Names an object for graphics debuggers (KHR_debug); nothing without the extension.</summary>
		internal static void Label(uint identifier, int name, string label)
		{
			GLDevice device = Current;
			if (device == null || !device.labels || string.IsNullOrEmpty(label))
			{
				return;
			}

			byte[] text = Encoding.UTF8.GetBytes(label);
			fixed (byte* pointer = text)
			{
				Native.glObjectLabel(identifier, (uint)name, text.Length, pointer);
			}
		}

		private void Initialize()
		{
			Gl = new OpenGLBackend();
			Current = this;
			OpenGLBackend gl = Gl;
			Major = gl.GetInteger(GetPName.MajorVersion);
			Minor = gl.GetInteger(GetPName.MinorVersion);
			if (!IsAtLeast(4, 3))
			{
				throw new NotSupportedException($"OpenGL 4.3 is needed (compute shaders, vertex attribute bindings); this context is {Major}.{Minor}.");
			}

			int count = gl.GetInteger(GetPName.NumExtensions);
			for (int i = 0; i < count; i++)
			{
				extensions.Add(gl.GetStringi(StringNameIndexed.Extensions, i));
			}

			labels = HasExtension("GL_KHR_debug") && Environment.GetEnvironmentVariable("LEL_GRAPHICS_LABELS") == "1";
			DeviceCapabilities caps = Capabilities;
			caps.Api = GraphicsApi.OpenGL;
			caps.ApiVersion = $"OpenGL {Major}.{Minor}";
			caps.Vendor = gl.GetString(StringName.Vendor) ?? "";
			caps.Renderer = gl.GetString(StringName.Renderer) ?? "";
			caps.DriverVersion = gl.GetString(StringName.Version) ?? "";
			caps.DriverIdentity = caps.Vendor + "\n" + caps.Renderer + "\n" + caps.DriverVersion + "\n" + (gl.GetString(StringName.ShadingLanguageVersion) ?? "");
			bool anisotropic = IsAtLeast(4, 6) || HasExtension("GL_EXT_texture_filter_anisotropic") || HasExtension("GL_ARB_texture_filter_anisotropic");
			// GL_MAX_TEXTURE_MAX_ANISOTROPY (the same value in core 4.6 and the EXT extension).
			caps.MaxAnisotropy = anisotropic ? gl.GetFloat((GetPName)0x84FF) : 1f;
			caps.MaxTextureSize = gl.GetInteger(GetPName.MaxTextureSize);
			caps.Max3DTextureSize = Math.Max(256, gl.GetInteger(GetPName.Max3DTextureSize));
			// GL_MAX_IMAGE_UNITS; GL_MAX_TEXTURE_IMAGE_UNITS (fragment) and GL_MAX_COMPUTE_TEXTURE_IMAGE_UNITS.
			caps.MaxImages = gl.GetInteger((GetPName)0x8F38);
			caps.MaxSampledTextures = Math.Max(16, Math.Min(gl.GetInteger((GetPName)0x8872), gl.GetInteger((GetPName)0x91BC)));
			// GL_MAX_COMBINED_TEXTURE_IMAGE_UNITS: the last unit is the device's own.
			ScratchUnit = Math.Max(caps.MaxSampledTextures, gl.GetInteger((GetPName)0x8B4D) - 1);
			// GL_MAX_FRAMEBUFFER_SAMPLES, GL_UNIFORM_BUFFER_OFFSET_ALIGNMENT.
			caps.MaxPassSamples = Math.Max(1, gl.GetInteger((GetPName)0x9318));
			caps.UniformBufferOffsetAlignment = Math.Max(1, gl.GetInteger((GetPName)0x8A34));
			caps.DrawParametersExtension = HasExtension("GL_ARB_shader_draw_parameters");
			caps.DrawParameters = caps.DrawParametersExtension || IsAtLeast(4, 6);
			caps.IndirectCount = HasExtension("GL_ARB_indirect_parameters") || IsAtLeast(4, 6);
			// GL_SUBGROUP_SUPPORTED_STAGES_KHR (compute: 0x20), GL_SUBGROUP_SUPPORTED_FEATURES_KHR (basic 1, arithmetic 4).
			caps.ComputeSubgroups = HasExtension("GL_KHR_shader_subgroup") && (gl.GetInteger((GetPName)0x9533) & 0x20) != 0 && (gl.GetInteger((GetPName)0x9534) & 5) == 5;
			caps.MaxStorageBufferBindings = gl.GetInteger(GetPName.MaxShaderStorageBufferBindings);
			caps.VertexStorageBlocks = gl.GetInteger(GetPName.MaxVertexShaderStorageBlocks);
			caps.FragmentStorageBlocks = gl.GetInteger(GetPName.MaxFragmentShaderStorageBlocks);
			ParallelCompileKhr = HasExtension("GL_KHR_parallel_shader_compile");
			ParallelCompileArb = HasExtension("GL_ARB_parallel_shader_compile");
			caps.ParallelShaderCompile = ParallelCompileKhr || ParallelCompileArb;
			caps.ProgramBinaries = gl.GetInteger(GetPName.NumProgramBinaryFormats) > 0;
			// AMD's OpenGL driver holds every context of the process while one of them compiles (measured on a 780M).
			caps.CompilesBlockEveryThread = caps.Vendor.IndexOf("ATI", StringComparison.OrdinalIgnoreCase) >= 0 || caps.Vendor.IndexOf("AMD", StringComparison.OrdinalIgnoreCase) >= 0;
			initialized = true;
		}

		public override void SetParallelShaderCompile(bool enabled)
		{
			SetCompilerThreads(enabled);
		}

		/// <summary>A hidden 1 x 1 window whose context shares objects with the current one (main thread: GLFW makes windows only there).</summary>
		public override GraphicsContext CreateWorkerContext()
		{
			GLContext current = currentContext;
			if (current == null)
			{
				return null;
			}

			Glfw.WindowHint(GlfwWindowHint.Visible, 0);
			Glfw.WindowHint(GlfwWindowHint.ClientApi, Glfw.Constants.GLFW_OPENGL_API);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, Major);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, Minor);
			Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_CORE_PROFILE);
			Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 1);
			GlfwWindow hidden;
			try
			{
				hidden = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = "LELEngine shader compiler", Size = new Brutal.Numerics.int2(1, 1), Share = current.Window });
			}
			catch (InvalidOperationException)
			{
				// The window could not be made (GLFW's reason is in the log).
				hidden = null;
			}

			// GLFW's defaults back for the windows made later (the engine's windows set their own context hints anyway).
			Glfw.WindowHint(GlfwWindowHint.Visible, 1);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMajor, 1);
			Glfw.WindowHint(GlfwWindowHint.ContextVersionMinor, 0);
			Glfw.WindowHint(GlfwWindowHint.OpenGlProfile, Glfw.Constants.GLFW_OPENGL_ANY_PROFILE);
			Glfw.WindowHint(GlfwWindowHint.OpenGlForwardCompat, 0);

			// glfwCreateWindow leaves the current context alone; checked all the same.
			if (Glfw.GetCurrentContext() != current.WindowPtr)
			{
				current.MakeCurrent();
			}

			return hidden != null ? new GLContext(this, hidden) : null;
		}

		/// <summary>How many driver threads compile programs: all it likes, or none (the calling thread compiles).</summary>
		internal void SetCompilerThreads(bool parallel)
		{
			uint threads = parallel ? 0xFFFFFFFF : 0u;
			if (ParallelCompileKhr)
			{
				Gl.MaxShaderCompilerThreads(threads, true);
			}
			else if (ParallelCompileArb)
			{
				Gl.MaxShaderCompilerThreads(threads, false);
			}
		}
	}

	/// <summary>A window's OpenGL context (GLFW's), with what OpenGL keeps per context: the state and the container objects.</summary>
	internal sealed class GLContext : GraphicsContext
	{
		private readonly GLDevice device;
		private readonly NativeWindow nativeWindow;
		private GLTexture backbuffer;
		private int swapInterval;
		private int width;
		private int height;

		internal GLContext(GLDevice device, NativeWindow window)
			: base(window.Handle)
		{
			this.device = device;
			nativeWindow = window;
			window.FramebufferResize += OnResize;
			Glfw.PInvoke.GetFramebufferSize(window.Handle, out width, out height);
		}

		internal GLContext(GLDevice device, GlfwWindow window)
			: base(window)
		{
			this.device = device;
			width = 1;
			height = 1;
		}

		internal GLCommandList Commands { get; private set; }

		public override GpuTexture Backbuffer
		{
			get
			{
				int w = Math.Max(1, width);
				int h = Math.Max(1, height);
				if (backbuffer == null || backbuffer.Width != w || backbuffer.Height != h)
				{
					backbuffer = new GLTexture(w, h);
				}

				return backbuffer;
			}
		}

		public override bool IsCurrent => Glfw.GetCurrentContext() == Window.NativeHandle;

		public override int SwapInterval
		{
			get => swapInterval;
			set
			{
				Glfw.SwapInterval(value);
				swapInterval = value;
			}
		}

		/// <summary>The context's own state, set once (the context is current).</summary>
		internal void Start()
		{
			Commands ??= new GLCommandList(device, this);
		}

		public override void MakeCurrent()
		{
			Window.MakeContextCurrent();
			GLDevice.SetCurrent(this);
			SetCurrent(this);
			if (GLDevice.Gl != null)
			{
				Start();
				device.CollectGarbage();
			}
		}

		public override void MakeNoneCurrent()
		{
			unsafe
			{
				Glfw.PInvoke.MakeContextCurrent(null);
			}

			GLDevice.SetCurrent(null);
			SetCurrent(null);
		}

		public override void SwapBuffers()
		{
			Commands?.EndRenderPass();
			Window.SwapBuffers();
			device.CollectGarbage();
		}

		public override void Dispose()
		{
			if (nativeWindow != null)
			{
				nativeWindow.FramebufferResize -= OnResize;
			}

			if (GLDevice.ContextOfThread == this)
			{
				Commands?.DeleteContainers();
			}
		}

		private void OnResize(FramebufferResizeEventArgs e)
		{
			width = e.Width;
			height = e.Height;
		}
	}
}
