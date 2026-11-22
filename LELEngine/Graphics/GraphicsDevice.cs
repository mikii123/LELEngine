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

		/// <summary>Shaders can trace rays against acceleration structures (ray queries).</summary>
		public bool RayQuery { get; internal set; }

		/// <summary>The driver holds every thread of the process while one compiles a program (compiling in another process helps).</summary>
		public bool CompilesBlockEveryThread { get; internal set; }
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

		/// <summary>Sends the commands given so far to the GPU without waiting for them.</summary>
		public abstract void Flush();

		/// <summary>Sends the commands given so far and waits until the GPU has executed them.</summary>
		public abstract void Finish();

		/// <summary>The graphics side of a window: made by the window's constructor.</summary>
		internal abstract GraphicsContext CreateWindowContext(NativeWindow window, NativeWindowSettings settings);

		/// <summary>The GLFW client API hint windows are made with for this device's API.</summary>
		internal static bool WindowsHaveOpenGLContexts => CurrentApi == GraphicsApi.OpenGL;

		internal static GraphicsContext CreateContext(NativeWindow window, NativeWindowSettings settings)
		{
			if (Current == null)
			{
				Current = RequestedApi == GraphicsApi.Vulkan ? CreateVulkanDevice(window) : new OpenGL.GLDevice();
			}

			return Current.CreateWindowContext(window, settings);
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
