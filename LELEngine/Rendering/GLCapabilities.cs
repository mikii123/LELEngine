using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Rendering
{
	/// <summary>
	///     What the current OpenGL context offers beyond the 4.3 baseline the engine requires. Queried once after
	///     context creation; passes use it to pick the native path and fall back to 4.3 code otherwise.
	///     The environment variable LEL_GL_DISABLE (comma separated: drawparameters, indirectparameters, subgroups)
	///     turns detected features off, so the fallbacks can be tested on hardware that has everything;
	///     LEL_GL_ENABLE keeps a feature a driver workaround would turn off.
	/// </summary>
	public static class GLCapabilities
	{
		#region PublicFields

		public static int Major { get; private set; }
		public static int Minor { get; private set; }

		/// <summary>glClearTexImage / glClearTexSubImage (OpenGL 4.4).</summary>
		public static bool ClearTexture { get; private set; }

		/// <summary>Direct state access (OpenGL 4.5).</summary>
		public static bool DirectStateAccess { get; private set; }

		/// <summary>Anisotropic texture filtering (OpenGL 4.6 core, or EXT_texture_filter_anisotropic).</summary>
		public static bool AnisotropicFiltering { get; private set; }

		public static float MaxAnisotropy { get; private set; } = 1f;

		public static int MaxImageUnits { get; private set; }

		/// <summary>
		///     gl_BaseInstance in vertex shaders (ARB_shader_draw_parameters, core in GLSL 4.60): GPU-driven draws read
		///     their instance index directly. Fallback: a per-instance vertex attribute (see GeometryPool).
		/// </summary>
		public static bool ShaderDrawParameters { get; private set; }

		/// <summary>The draw parameters come from the ARB extension (gl_BaseInstanceARB) rather than GLSL 4.60 core.</summary>
		public static bool ShaderDrawParametersExtension { get; private set; }

		/// <summary>
		///     glMultiDrawElementsIndirectCount (ARB_indirect_parameters, core in 4.6): the GPU writes how many draws
		///     follow. Fallback: one command per instance, culled ones with instance count 0.
		/// </summary>
		public static bool IndirectParameters { get; private set; }

		/// <summary>
		///     Subgroup basic + arithmetic operations in compute shaders (KHR_shader_subgroup). Fallback: shared memory
		///     scans.
		/// </summary>
		public static bool ComputeSubgroups { get; private set; }

		/// <summary>Shader storage buffer binding points, and storage blocks per vertex / fragment shader (GPU-driven rendering).</summary>
		public static int MaxShaderStorageBindings { get; private set; }

		public static int VertexShaderStorageBlocks { get; private set; }
		public static int FragmentShaderStorageBlocks { get; private set; }

		public static bool Initialized { get; private set; }

		#endregion

		#region PrivateFields

		private const GetPName SubgroupSupportedStages = (GetPName)0x9533;
		private const GetPName SubgroupSupportedFeatures = (GetPName)0x9534;
		private const int ComputeStageBit = 0x20;
		private const int SubgroupBasicBit = 0x1;
		private const int SubgroupArithmeticBit = 0x4;

		private static readonly HashSet<string> extensions = new HashSet<string>(StringComparer.Ordinal);

		#endregion

		#region PublicMethods

		public static void Initialize()
		{
			Major = GL.GetInteger(GetPName.MajorVersion);
			Minor = GL.GetInteger(GetPName.MinorVersion);
			extensions.Clear();
			int count = GL.GetInteger(GetPName.NumExtensions);
			for (int i = 0; i < count; i++)
			{
				extensions.Add(GL.GetString(StringNameIndexed.Extensions, i));
			}

			ClearTexture = IsAtLeast(4, 4);
			DirectStateAccess = IsAtLeast(4, 5);
			AnisotropicFiltering = IsAtLeast(4, 6) || HasExtension("GL_EXT_texture_filter_anisotropic") || HasExtension("GL_ARB_texture_filter_anisotropic");
			if (AnisotropicFiltering)
			{
				// GL_MAX_TEXTURE_MAX_ANISOTROPY (same value in core 4.6 and the EXT extension).
				MaxAnisotropy = GL.GetFloat((GetPName)0x84FF);
			}
			MaxImageUnits = GL.GetInteger((GetPName)0x8F38);

			ShaderDrawParametersExtension = HasExtension("GL_ARB_shader_draw_parameters");
			ShaderDrawParameters = ShaderDrawParametersExtension || IsAtLeast(4, 6);
			IndirectParameters = HasExtension("GL_ARB_indirect_parameters") || IsAtLeast(4, 6);
			ComputeSubgroups = HasExtension("GL_KHR_shader_subgroup")
				&& (GL.GetInteger(SubgroupSupportedStages) & ComputeStageBit) != 0
				&& (GL.GetInteger(SubgroupSupportedFeatures) & (SubgroupBasicBit | SubgroupArithmeticBit)) == (SubgroupBasicBit | SubgroupArithmeticBit);
			MaxShaderStorageBindings = GL.GetInteger(GetPName.MaxShaderStorageBufferBindings);
			VertexShaderStorageBlocks = GL.GetInteger(GetPName.MaxVertexShaderStorageBlocks);
			FragmentShaderStorageBlocks = GL.GetInteger(GetPName.MaxFragmentShaderStorageBlocks);
			// Driver workaround: Intel's OpenGL driver makes the CPU wait for the GPU when a draw reads its count from a
			// buffer (measured: 3000 objects 81 fps with the count read by the GPU, 118 fps with a fixed count and empty
			// draws for the culled objects). LEL_GL_ENABLE=indirectparameters keeps the GPU count anyway.
			string vendor = GL.GetString(StringName.Vendor) ?? "";
			string enabled = Environment.GetEnvironmentVariable("LEL_GL_ENABLE") ?? "";
			if (IndirectParameters && vendor.IndexOf("Intel", StringComparison.OrdinalIgnoreCase) >= 0 && enabled.IndexOf("indirectparameters", StringComparison.OrdinalIgnoreCase) < 0)
			{
				IndirectParameters = false;
				Console.WriteLine("[GL] Indirect draw count off on Intel: the driver stalls on it, fixed-count multi-draw is faster (LEL_GL_ENABLE=indirectparameters to force it)");
			}

			ApplyDisabledFeatures(Environment.GetEnvironmentVariable("LEL_GL_DISABLE"));
			Initialized = true;

			Console.WriteLine($"[GL] {Major}.{Minor} | clear texture {(ClearTexture ? "yes" : "no")} | DSA {(DirectStateAccess ? "yes" : "no")} | anisotropy {(AnisotropicFiltering ? MaxAnisotropy.ToString("0") + "x" : "no")} | image units {MaxImageUnits}");
			Console.WriteLine($"[GL] draw parameters {Describe(ShaderDrawParameters, ShaderDrawParametersExtension ? "ARB" : "core")} | indirect count {Describe(IndirectParameters, "yes")} | compute subgroups {Describe(ComputeSubgroups, "yes")} | storage bindings {MaxShaderStorageBindings} (vertex {VertexShaderStorageBlocks}, fragment {FragmentShaderStorageBlocks})");
		}

		public static bool IsAtLeast(int major, int minor)
		{
			return Major > major || (Major == major && Minor >= minor);
		}

		public static bool HasExtension(string name)
		{
			return extensions.Contains(name);
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
