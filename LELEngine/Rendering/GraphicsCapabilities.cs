using System;
using LELEngine.Graphics;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What the graphics device offers beyond the baseline the engine requires (<see cref="DeviceCapabilities" />),
	///     as the rendering code asks for it. Read once after the device is made; passes use it to pick the native
	///     path and fall back otherwise.
	///     The environment variable LEL_GL_DISABLE (comma separated: drawparameters, indirectparameters, subgroups,
	///     parallelcompile, compilethread: <see cref="Shaders.ShaderCompilation.UseCompileThread" />) turns detected features
	///     off, so the fallbacks can be tested on hardware that has everything;
	///     LEL_GL_ENABLE keeps a feature a driver workaround would turn off.
	/// </summary>
	public static class GraphicsCapabilities
	{
		#region PublicFields

		/// <summary>The OpenGL version of an OpenGL device (4.6 for any other: what its shaders are written to).</summary>
		public static int Major { get; private set; }

		public static int Minor { get; private set; }

		/// <summary>Textures can be cleared without drawing (always, through the device).</summary>
		public static bool ClearTexture { get; private set; }

		/// <summary>OpenGL 4.5's direct state access, for the code that still gives OpenGL commands itself.</summary>
		public static bool DirectStateAccess { get; private set; }

		public static bool AnisotropicFiltering => MaxAnisotropy > 1f;

		public static float MaxAnisotropy { get; private set; } = 1f;

		public static int MaxImageUnits { get; private set; }

		/// <summary>Textures a program can sample (at least 16).</summary>
		public static int MaxSampledTextures { get; private set; } = 16;

		/// <summary>The largest 3D texture extent (at least 256, 2048 on Intel, 16384 on NVIDIA).</summary>
		public static int Max3DTextureSize { get; private set; } = 256;

		/// <summary>
		///     gl_BaseInstance in vertex shaders (ARB_shader_draw_parameters, core in GLSL 4.60): GPU-driven draws read
		///     their instance index directly. Fallback: a per-instance vertex attribute (see GeometryPool).
		/// </summary>
		public static bool ShaderDrawParameters { get; private set; }

		/// <summary>The draw parameters come from the ARB extension (gl_BaseInstanceARB) rather than GLSL 4.60 core.</summary>
		public static bool ShaderDrawParametersExtension { get; private set; }

		/// <summary>
		///     Indirect draws whose count the GPU reads from a buffer: the GPU writes how many draws follow. Fallback:
		///     one command per instance, culled ones with instance count 0.
		/// </summary>
		public static bool IndirectParameters { get; private set; }

		/// <summary>
		///     Subgroup basic + arithmetic operations in compute shaders (KHR_shader_subgroup). Fallback: shared memory
		///     scans.
		/// </summary>
		public static bool ComputeSubgroups { get; private set; }

		/// <summary>Storage buffer binding points, and storage blocks per vertex / fragment shader (GPU-driven rendering).</summary>
		public static int MaxShaderStorageBindings { get; private set; }

		public static int VertexShaderStorageBlocks { get; private set; }
		public static int FragmentShaderStorageBlocks { get; private set; }

		/// <summary>
		///     The device builds programs on threads of its own: programs started together compile together
		///     (<see cref="Shaders.ShaderProgram" /> asks for the results only when a program is first used). Without it the
		///     same code works, compiling in turn.
		/// </summary>
		public static bool ParallelShaderCompile { get; private set; }

		/// <summary>
		///     Shaders trace rays through acceleration structures the device builds (<see cref="DeviceCapabilities.RayQuery" />):
		///     the GI traces the scene's triangles where the project asks for it. Fallback: the distance fields.
		/// </summary>
		public static bool RayQueries { get; private set; }

		public static bool Initialized { get; private set; }

		#endregion

		#region PublicMethods

		public static void Initialize()
		{
			GraphicsDevice device = GraphicsDevice.Current;
			DeviceCapabilities capabilities = device.Capabilities;
			Major = 4;
			Minor = 6;
			if (capabilities.Api == GraphicsApi.OpenGL)
			{
				// "OpenGL 4.6"
				string[] version = capabilities.ApiVersion.Substring(capabilities.ApiVersion.LastIndexOf(' ') + 1).Split('.');
				if (version.Length >= 2 && int.TryParse(version[0], out int major) && int.TryParse(version[1], out int minor))
				{
					Major = major;
					Minor = minor;
				}
			}

			ClearTexture = true;
			DirectStateAccess = IsAtLeast(4, 5);
			MaxAnisotropy = capabilities.MaxAnisotropy;
			MaxImageUnits = capabilities.MaxImages;
			Max3DTextureSize = capabilities.Max3DTextureSize;
			MaxSampledTextures = capabilities.MaxSampledTextures;
			ShaderDrawParametersExtension = capabilities.DrawParametersExtension;
			ShaderDrawParameters = capabilities.DrawParameters;
			IndirectParameters = capabilities.IndirectCount;
			ComputeSubgroups = capabilities.ComputeSubgroups;
			MaxShaderStorageBindings = capabilities.MaxStorageBufferBindings;
			VertexShaderStorageBlocks = capabilities.VertexStorageBlocks;
			FragmentShaderStorageBlocks = capabilities.FragmentStorageBlocks;

			// Driver workaround: Intel's OpenGL driver makes the CPU wait for the GPU when a draw reads its count from a
			// buffer (measured: 3000 objects 81 fps with the count read by the GPU, 118 fps with a fixed count and empty
			// draws for the culled objects). LEL_GL_ENABLE=indirectparameters keeps the GPU count anyway.
			string enabled = Environment.GetEnvironmentVariable("LEL_GL_ENABLE") ?? "";
			if (IndirectParameters && capabilities.Api == GraphicsApi.OpenGL && capabilities.Vendor.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0
			    && enabled.IndexOf("indirectparameters", StringComparison.OrdinalIgnoreCase) < 0)
			{
				IndirectParameters = false;
				Console.WriteLine("[GL] Indirect draw count off on Intel: the driver stalls on it, fixed-count multi-draw is faster (LEL_GL_ENABLE=indirectparameters to force it)");
			}

			ParallelShaderCompile = capabilities.ParallelShaderCompile;
			RayQueries = capabilities.RayQuery;
			ApplyDisabledFeatures(Environment.GetEnvironmentVariable("LEL_GL_DISABLE"));
			// As many threads as the driver likes (measured on an RTX 5070 Ti: the 13 FORGE3D planet programs 13.8 s one by
			// one, 2.8 s started together); none when switched off: it compiles on the calling thread (the driver's
			// default is neither).
			device.SetParallelShaderCompile(ParallelShaderCompile);
			Initialized = true;
			if (capabilities.Api == GraphicsApi.OpenGL)
			{
				Console.WriteLine($"[GL] {Major}.{Minor} | clear texture {(IsAtLeast(4, 4) ? "yes" : "no")} | DSA {(DirectStateAccess ? "yes" : "no")} | anisotropy {(AnisotropicFiltering ? MaxAnisotropy.ToString("0") + "x" : "no")} | image units {MaxImageUnits}");
				Console.WriteLine($"[GL] draw parameters {Describe(ShaderDrawParameters, ShaderDrawParametersExtension ? "ARB" : "core")} | indirect count {Describe(IndirectParameters, "yes")} | compute subgroups {Describe(ComputeSubgroups, "yes")} | parallel shader compile {Describe(ParallelShaderCompile, "yes")} | storage bindings {MaxShaderStorageBindings} (vertex {VertexShaderStorageBlocks}, fragment {FragmentShaderStorageBlocks})");
			}
			else
			{
				Console.WriteLine($"[Graphics] {capabilities.ApiVersion} | {capabilities.Renderer} | anisotropy {(AnisotropicFiltering ? MaxAnisotropy.ToString("0") + "x" : "no")} | images {MaxImageUnits} | sampled textures {MaxSampledTextures}");
				Console.WriteLine($"[Graphics] indirect count {Describe(IndirectParameters, "yes")} | compute subgroups {Describe(ComputeSubgroups, "yes")} | parallel shader compile {Describe(ParallelShaderCompile, "yes")} | ray queries {(RayQueries ? "yes" : "no")} | storage bindings {MaxShaderStorageBindings}");
			}
		}

		public static bool IsAtLeast(int major, int minor)
		{
			return Major > major || (Major == major && Minor >= minor);
		}

		#endregion

		#region PrivateMethods

		private static void ApplyDisabledFeatures(string list)
		{
			if (string.IsNullOrWhiteSpace(list))
			{
				return;
			}

			foreach (string item in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
			{
				switch (item.ToLowerInvariant())
				{
					case "drawparameters":
						ShaderDrawParameters = false;
						ShaderDrawParametersExtension = false;
						break;
					case "indirectparameters":
						IndirectParameters = false;
						break;
					case "subgroups":
						ComputeSubgroups = false;
						break;
					case "parallelcompile":
						ParallelShaderCompile = false;
						break;
					case "compilethread":
						Shaders.ShaderCompilation.UseCompileThread = false;
						break;
					default:
						Console.WriteLine("[GL] LEL_GL_DISABLE: unknown feature '" + item + "'");
						continue;
				}

				Console.WriteLine("[GL] Disabled by LEL_GL_DISABLE: " + item);
			}
		}

		private static string Describe(bool available, string how)
		{
			return available ? how : "no (fallback)";
		}

		#endregion
	}
}
