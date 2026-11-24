using System;
using LELEngine.Windowing;

namespace LELEngine.Graphics
{
	/// <summary>What the device and its driver can do; fixed once the device is made.</summary>
	public sealed class DeviceCapabilities
	{
		public GraphicsApi Api { get; internal set; }

		/// <summary>"OpenGL 4.6", "Vulkan 1.3.277".</summary>
		public string ApiVersion { get; internal set; } = "";

		public string Vendor { get; internal set; } = "";
		public string Renderer { get; internal set; } = "";

		/// <summary>The driver's own version text.</summary>
		public string DriverVersion { get; internal set; } = "";

		/// <summary>Names the device, driver and shading language: programs kept as binaries are valid for one value of it.</summary>
		public string DriverIdentity { get; internal set; } = "";

		public float MaxAnisotropy { get; internal set; } = 1f;
		public int MaxTextureSize { get; internal set; } = 4096;
		public int Max3DTextureSize { get; internal set; } = 256;

		/// <summary>Textures one program can sample.</summary>
		public int MaxSampledTextures { get; internal set; } = 16;

		public int MaxImages { get; internal set; } = 8;

		/// <summary>The most samples a render pass without attachments can have (<see cref="RenderPassDescriptor.Samples" />).</summary>
		public int MaxPassSamples { get; internal set; } = 1;

		/// <summary>Offsets of uniform buffer ranges (<see cref="CommandList.SetUniformBuffer" />) are multiples of this.</summary>
		public int UniformBufferOffsetAlignment { get; internal set; } = 256;
		public int MaxStorageBufferBindings { get; internal set; } = 8;
		public int VertexStorageBlocks { get; internal set; }
		public int FragmentStorageBlocks { get; internal set; }

		/// <summary>Vertex stages read the draw's base instance (gl_BaseInstance).</summary>
		public bool DrawParameters { get; internal set; }

		/// <summary>... through ARB_shader_draw_parameters (gl_BaseInstanceARB) rather than GLSL 4.60.</summary>
		public bool DrawParametersExtension { get; internal set; }

		/// <summary><see cref="CommandList.DrawIndexedIndirectCount" /> reads the count on the GPU.</summary>
		public bool IndirectCount { get; internal set; }

		/// <summary>Subgroup operations (basic, arithmetic) in compute stages.</summary>
		public bool ComputeSubgroups { get; internal set; }

		/// <summary>Programs build on other threads: <see cref="GpuProgram.IsReady" /> tells when one is done.</summary>
		public bool ParallelShaderCompile { get; internal set; }

		/// <summary><see cref="GpuProgram.GetBinary" /> gives something <see cref="GraphicsDevice.CreateProgramFromBinary" /> takes.</summary>
		public bool ProgramBinaries { get; internal set; }

		/// <summary>
		///     Shaders trace rays against acceleration structures (ray queries: GLSL's GL_EXT_ray_query), which the device
		///     makes (<see cref="GraphicsDevice.CreateAccelerationStructure" />) and builds.
		/// </summary>
		public bool RayQuery { get; internal set; }

		/// <summary>The driver holds every thread of the process while one compiles a program (compiling in another process helps).</summary>
		public bool CompilesBlockEveryThread { get; internal set; }

		/// <summary>
		///     Clearing a whole colour attachment costs next to nothing (the driver clears its compression, not the texels):
		///     a pass may clear what it will mostly draw over anyway. False where a clear was measured to write every texel
		///     (AMD's Vulkan driver on its integrated GPUs: a 1280 x 720 RGBA16F clear ~0.08 ms on the Radeon 780M against
		///     ~0 on OpenGL): passes that can avoid such a clear do.
		/// </summary>
		public bool FastColorClears { get; internal set; } = true;

		/// <summary>
		///     The GPU runs compute work on a queue of its own beside the graphics queue: what is given between
		///     <see cref="CommandList.BeginAsyncCompute" /> and <see cref="CommandList.EndAsyncCompute" /> may overlap the
		///     graphics work around it. False: everything runs in the order given, on one queue.
		/// </summary>
		public bool AsyncCompute { get; internal set; }
	}

	/// <summary>
	///     The GPU, through one graphics API: makes textures, buffers, samplers and programs, and takes the commands
	///     that use them (<see cref="Commands" />). One per process, made with the first window that draws
	///     (<see cref="RequestedApi" /> picks the API before that).
	///     <para>
	///         Conventions, the same on every API: clip space has y up and z from -1 (near) to 1; the first row of a
	///         texture or a render target is the lowest; triangles wound counter-clockwise face the viewer.
	///     </para>
	/// </summary>
	public abstract class GraphicsDevice
	{
		/// <summary>The API the device will be made for; changing it after the first window has no effect.</summary>
		public static GraphicsApi RequestedApi { get; set; } = GraphicsApi.OpenGL;

		/// <summary>
		///     The device is made with a queue for asynchronous compute where the GPU has one
		///     (<see cref="DeviceCapabilities.AsyncCompute" />); set before the first window (default: LEL_ASYNC_COMPUTE=1).
		///     Off by default: on the Radeon 780M the compute queue gained at most 2 % of a frame and lost several times
		///     that when another program used the GPU at the same time (the compute queue then waits behind it).
		/// </summary>
		public static bool RequestAsyncCompute { get; set; } = Environment.GetEnvironmentVariable("LEL_ASYNC_COMPUTE") == "1";

		/// <summary>
		///     The API that was asked for and could not be had on this machine, when the device was made for another
		///     one instead (<see cref="Window.Open{T}" />); null when the device is what was asked for.
		/// </summary>
		public static GraphicsApi? FellBackFrom { get; internal set; }

		public static GraphicsDevice Current { get; private set; }

		/// <summary>The device's API, or the requested one while there is no device.</summary>
		public static GraphicsApi CurrentApi => Current != null ? Current.Api : RequestedApi;

		/// <summary>The calling thread holds a window's graphics context: it may give commands.</summary>
		public static bool HasContext => Current != null && Current.HasCurrentContext;

		public abstract GraphicsApi Api { get; }
		public DeviceCapabilities Capabilities { get; } = new DeviceCapabilities();

		/// <summary>The commands of the thread that holds the context.</summary>
		public abstract CommandList Commands { get; }

		/// <summary>The back buffer of the window whose context is current (what <see cref="GraphicsContext.SwapBuffers" /> shows).</summary>
		public abstract GpuTexture Backbuffer { get; }

		public abstract bool HasCurrentContext { get; }

		public abstract GpuTexture CreateTexture(in TextureDescriptor descriptor);
		public abstract GpuBuffer CreateBuffer(in BufferDescriptor descriptor);

		/// <summary>The sampler of a description (one object per description, kept for the device's life).</summary>
		public abstract GpuSampler GetSampler(in SamplerDescriptor descriptor);

		/// <summary>Starts building a program; <see cref="GpuProgram.Finish" /> waits for it and tells how it went.</summary>
		public abstract GpuProgram CreateProgram(ProgramDescriptor descriptor);

		/// <summary>A program from what <see cref="GpuProgram.GetBinary" /> gave on this device and driver; null when the binary is refused.</summary>
		public abstract GpuProgram CreateProgramFromBinary(ProgramDescriptor descriptor, byte[] binary);

		/// <summary>A fence that signals once every command given so far has been executed.</summary>
		public abstract GpuFence CreateFence();

		public abstract GpuTimestamp CreateTimestamp();

		/// <summary>
		///     An acceleration structure of the size its triangles or its instances need, not built yet; throws
		///     <see cref="NotSupportedException" /> on a device without ray queries (<see cref="DeviceCapabilities.RayQuery" />).
		/// </summary>
		public abstract GpuAccelerationStructure CreateAccelerationStructure(in AccelerationStructureDescriptor descriptor);

		/// <summary>Sends the commands given so far to the GPU without waiting for them.</summary>
		public abstract void Flush();

		/// <summary>Sends the commands given so far and waits until the GPU has executed them.</summary>
		public abstract void Finish();

		/// <summary>Programs build on the device's own threads from now on, or (false) on the thread that makes them.</summary>
		public virtual void SetParallelShaderCompile(bool enabled)
		{ }

		/// <summary>
		///     A context without a window of its own, for a thread that only builds programs beside the thread that
		///     draws: the thread makes it current, makes programs, and releases it. Null when the device has none to give.
		/// </summary>
		public virtual GraphicsContext CreateWorkerContext()
		{
			return null;
		}

		/// <summary>The graphics side of a window: made by the window's constructor.</summary>
		internal abstract GraphicsContext CreateWindowContext(NativeWindow window, NativeWindowSettings settings);

		/// <summary>The GLFW client API hint windows are made with for this device's API.</summary>
		internal static bool WindowsHaveOpenGLContexts => CurrentApi == GraphicsApi.OpenGL;

		/// <summary>
		///     Releases what the device holds of the system when it is given up before it ever drew (its first window
		///     could not be made); nothing made with it may be used after.
		/// </summary>
		internal virtual void Destroy()
		{ }

		internal static GraphicsContext CreateContext(NativeWindow window, NativeWindowSettings settings)
		{
			bool first = Current == null;
			if (first)
			{
				if (RequestedApi == GraphicsApi.Vulkan)
				{
					try
					{
						Current = CreateVulkanDevice(window);
					}
					catch (Exception e)
					{
						// No loader, no driver, or a GPU without what the engine needs: the caller may take another API.
						throw new GraphicsApiUnavailableException(GraphicsApi.Vulkan, e);
					}
				}
				else
				{
					Current = new OpenGL.GLDevice();
				}
			}

			try
			{
				return Current.CreateWindowContext(window, settings);
			}
			catch (Exception e) when (first && Current.Api != GraphicsApi.OpenGL)
			{
				// A device that cannot show anything in a window here (no surface, no swap chain) is given up as a
				// whole: the process has no device again and may start with another API.
				GraphicsDevice failed = Current;
				Current = null;
				failed.Destroy();
				throw new GraphicsApiUnavailableException(failed.Api, e);
			}
		}

		private static GraphicsDevice CreateVulkanDevice(NativeWindow window)
		{
			return new Vulkan.VulkanDevice(window);
		}

		public static bool TryParseApi(string text, out GraphicsApi api)
		{
			switch ((text ?? "").Trim().ToLowerInvariant())
			{
				case "vulkan":
				case "vk":
					api = GraphicsApi.Vulkan;
					return true;
				case "opengl":
				case "gl":
					api = GraphicsApi.OpenGL;
					return true;
				default:
					api = GraphicsApi.OpenGL;
					return false;
			}
		}
	}

	/// <summary>
	///     The device of the API that was asked for cannot be made on this machine (the cause is the inner exception).
	///     No device exists after it: <see cref="GraphicsDevice.RequestedApi" /> may name another API and the window be
	///     made again (<see cref="Window.Open{T}" /> does).
	/// </summary>
	public sealed class GraphicsApiUnavailableException : Exception
	{
		public GraphicsApi Api { get; }

		public GraphicsApiUnavailableException(GraphicsApi api, Exception cause)
			: base($"{api} is not available: {cause?.Message}", cause)
		{
			Api = api;
		}
	}

	/// <summary>Short names for the device and its commands: what rendering code writes all day.</summary>
	public static class Gfx
	{
		public static GraphicsDevice Device => GraphicsDevice.Current;
		public static CommandList Commands => GraphicsDevice.Current.Commands;
		public static DeviceCapabilities Capabilities => GraphicsDevice.Current.Capabilities;
	}

	/// <summary>The samplers rendering code asks for most (made on first use, shared).</summary>
	public static class Samplers
	{
		private static GraphicsDevice device;
		private static GpuSampler linearClamp;
		private static GpuSampler pointClamp;
		private static GpuSampler linearRepeat;
		private static GpuSampler pointRepeat;
		private static GpuSampler trilinearClamp;
		private static GpuSampler trilinearRepeat;
		private static GpuSampler linearMirror;
		private static GpuSampler linearBorder;
		private static GpuSampler pointBorder;

		/// <summary>Linear within the first mip level, clamped.</summary>
		public static GpuSampler LinearClamp => Get(ref linearClamp, SamplerFilter.Linear, SamplerMipFilter.None, SamplerAddress.ClampToEdge);

		/// <summary>Nearest texel of the first mip level, clamped.</summary>
		public static GpuSampler PointClamp => Get(ref pointClamp, SamplerFilter.Nearest, SamplerMipFilter.None, SamplerAddress.ClampToEdge);

		public static GpuSampler LinearRepeat => Get(ref linearRepeat, SamplerFilter.Linear, SamplerMipFilter.None, SamplerAddress.Repeat);
		public static GpuSampler PointRepeat => Get(ref pointRepeat, SamplerFilter.Nearest, SamplerMipFilter.None, SamplerAddress.Repeat);

		/// <summary>Linear within and between mip levels, clamped.</summary>
		public static GpuSampler TrilinearClamp => Get(ref trilinearClamp, SamplerFilter.Linear, SamplerMipFilter.Linear, SamplerAddress.ClampToEdge);

		public static GpuSampler TrilinearRepeat => Get(ref trilinearRepeat, SamplerFilter.Linear, SamplerMipFilter.Linear, SamplerAddress.Repeat);
		public static GpuSampler LinearMirror => Get(ref linearMirror, SamplerFilter.Linear, SamplerMipFilter.None, SamplerAddress.MirroredRepeat);

		/// <summary>Linear, transparent black outside 0..1.</summary>
		public static GpuSampler LinearBorder => Get(ref linearBorder, SamplerFilter.Linear, SamplerMipFilter.None, SamplerAddress.ClampToBorder);

		public static GpuSampler PointBorder => Get(ref pointBorder, SamplerFilter.Nearest, SamplerMipFilter.None, SamplerAddress.ClampToBorder);

		/// <summary>A sampler by filter and addressing.</summary>
		public static GpuSampler Get(SamplerFilter filter, SamplerMipFilter mip, SamplerAddress address)
		{
			return GraphicsDevice.Current.GetSampler(new SamplerDescriptor(filter, mip, address));
		}

		private static GpuSampler Get(ref GpuSampler field, SamplerFilter filter, SamplerMipFilter mip, SamplerAddress address)
		{
			GraphicsDevice current = GraphicsDevice.Current;
			if (device != current)
			{
				device = current;
				linearClamp = pointClamp = linearRepeat = pointRepeat = trilinearClamp = trilinearRepeat = linearMirror = linearBorder = pointBorder = null;
			}

			return field ??= current.GetSampler(new SamplerDescriptor(filter, mip, address));
		}
	}
}
