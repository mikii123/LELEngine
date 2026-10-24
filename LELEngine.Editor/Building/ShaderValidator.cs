using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using LELEngine.Shaders;
using OpenTK.Graphics.OpenGL4;
using Silk.NET.Shaderc;

namespace LELEngine.Editor
{
	/// <summary>
	///     Offline check of the shader variants a game build uses with glslang, the Khronos reference GLSL compiler
	///     (through shaderc), instead of the build machine's GPU driver: the same verdict on every machine, no GL context
	///     needed, and errors by the GLSL specification (also those a lenient driver lets through). Every stage of each
	///     variant, and of its instanced program for shaders drawn GPU-driven, is checked as the player's preamble makes
	///     it for each kind of GPU (<see cref="Profiles" />), compiled for OpenGL 4.5 into SPIR-V that is thrown away:
	///     the game still ships GLSL, which the player's driver compiles. Messages name the shader file or include and
	///     the line (#line directives in the checked copy, <see cref="ShaderProgram.ReadSources" />). Not covered: the
	///     limits and bugs of drivers, and the interface between stages (the driver links them).
	/// </summary>
	internal static unsafe class ShaderValidator
	{
		#region PublicFields

		/// <summary>What a check found.</summary>
		public sealed class Result
		{
			/// <summary>One message per failing stage source (the variant, stage, GPU kind and the compiler's errors).</summary>
			public readonly List<string> Errors = new List<string>();

			/// <summary>Stage sources compiled (identical ones once: most stages are the same for every kind of GPU).</summary>
			public int Stages;

			public int Warnings;
			public long Milliseconds;
		}

		#endregion

		#region PrivateFields

		// The kinds of GPU the preamble adapts to (ShaderFeatures): draw parameters from the ARB extension or from GLSL 4.60
		// core, with subgroups and the previous model matrices; or none of them.
		private static readonly ShaderFeatures[] Profiles =
		{
			new ShaderFeatures(DrawParametersSupport.Extension, true, true),
			new ShaderFeatures(DrawParametersSupport.Core, true, true),
			new ShaderFeatures(DrawParametersSupport.None, false, false)
		};

		private static readonly Regex VersionLine = new Regex(@"^[ \t]*#version[ \t]+(\d+)", RegexOptions.Multiline | RegexOptions.Compiled);

		#endregion

		#region PublicMethods

		/// <summary>Checks every stage of the variants (any thread); throws when the compiler library cannot be loaded.</summary>
		public static Result Validate(IEnumerable<ShaderVariant> variants)
		{
			var result = new Result();
			var watch = Stopwatch.StartNew();
			Shaderc api = Shaderc.GetApi();
			Compiler* compiler = api.CompilerInitialize();
			CompileOptions* options = api.CompileOptionsInitialize();
			try
			{
				api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
				api.CompileOptionsSetTargetEnv(options, TargetEnv.Opengl, (uint)EnvVersion.Opengl45);
				// SPIR-V for OpenGL wants the locations and bindings that GLSL for OpenGL leaves to the linker.
				api.CompileOptionsSetAutoMapLocations(options, true);
				api.CompileOptionsSetAutoBindUniforms(options, true);

				var checkedSources = new HashSet<string>(StringComparer.Ordinal);
				var reported = new HashSet<string>(StringComparer.Ordinal);
				foreach (ShaderVariant variant in variants)
				{
					bool instancing = Check(api, compiler, options, variant, variant.Keywords, false, checkedSources, reported, result);
					if (instancing)
					{
						var instanced = new List<string>(variant.Keywords) { ShaderProgram.InstancedDefine };
						Check(api, compiler, options, variant, instanced, true, checkedSources, reported, result);
					}
				}
			}
			finally
			{
				api.CompileOptionsRelease(options);
				api.CompilerRelease(compiler);
				api.Dispose();
			}

			result.Milliseconds = watch.ElapsedMilliseconds;
			return result;
		}

		#endregion

		#region PrivateMethods

		// One program of a variant for every kind of GPU; true when the shader is drawn GPU-driven too (it includes the
		// instancing library).
		private static bool Check(Shaderc api, Compiler* compiler, CompileOptions* options, ShaderVariant variant, IReadOnlyList<string> defines, bool instanced,
			HashSet<string> checkedSources, HashSet<string> reported, Result result)
		{
			// Each stage's code per kind of GPU, the same code once with the kinds that get it (most stages: all of them).
			bool includesInstancing = false;
			var codes = new List<(ShaderType Type, string Source, List<ShaderFeatures> Profiles)>();
			foreach (ShaderFeatures profile in Profiles)
			{
				List<KeyValuePair<ShaderType, string>> stages;
				try
				{
					stages = ShaderProgram.ReadSources(variant.Shader, defines, profile, true, out includesInstancing);
				}
				catch (Exception e)
				{
					if (reported.Add(variant.Shader + e.Message))
					{
						result.Errors.Add(variant + ": cannot read the shader (" + e.Message + ")");
					}

					return false;
				}

				foreach (KeyValuePair<ShaderType, string> stage in stages)
				{
					string source = stage.Value;
					if (profile.DrawParameters == DrawParametersSupport.Extension && stage.Key == ShaderType.VertexShader)
					{
						// glslang declares gl_BaseInstanceARB from GLSL 4.50 on only (the extension asks for 1.40, drivers
						// take it in 4.30): the checked copy says 450. The profile without draw parameters checks the
						// same code with the file's own #version.
						source = VersionLine.Replace(source, match => int.Parse(match.Groups[1].Value) < 450 ? "#version 450" : match.Value, 1);
					}

					int same = codes.FindIndex(code => code.Type == stage.Key && code.Source == source);
					if (same >= 0)
					{
						codes[same].Profiles.Add(profile);
					}
					else
					{
						codes.Add((stage.Key, source, new List<ShaderFeatures> { profile }));
					}
				}
			}

			foreach ((ShaderType type, string source, List<ShaderFeatures> profiles) in codes)
			{
				// Code another variant has (stages a keyword does not touch) was checked already.
				if (!checkedSources.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((int)type + "\n" + source)))))
				{
					continue;
				}

				result.Stages++;
				string errors = Compile(api, compiler, options, type, source, variant.Shader, out int warnings);
				result.Warnings += warnings;
				if (errors != null && reported.Add(errors))
				{
					string gpus = profiles.Count == Profiles.Length ? "every kind of GPU" : string.Join("; ", profiles);
					result.Errors.Add($"{variant}{(instanced ? " (instanced)" : "")}, {type.ToString().Replace("Shader", "").ToLowerInvariant()} stage ({gpus}):\n{errors}");
				}
			}

			return includesInstancing;
		}

		// The compiler's errors (null when the stage compiles).
		private static string Compile(Shaderc api, Compiler* compiler, CompileOptions* options, ShaderType type, string source, string name, out int warnings)
		{
			byte[] text = Encoding.UTF8.GetBytes(source);
			CompilationResult* compiled;
			fixed (byte* bytes = text)
			{
				compiled = api.CompileIntoSpv(compiler, bytes, (nuint)text.Length, Kind(type), name, "main", options);
			}

			try
			{
				warnings = (int)api.ResultGetNumWarnings(compiled);
				CompilationStatus status = api.ResultGetCompilationStatus(compiled);
				if (status == CompilationStatus.Success)
				{
					return null;
				}

				string message = api.ResultGetErrorMessageS(compiled);
				return string.IsNullOrWhiteSpace(message) ? status.ToString() : message.Trim();
			}
			finally
			{
				api.ResultRelease(compiled);
			}
		}

		private static ShaderKind Kind(ShaderType type)
		{
			switch (type)
			{
				case ShaderType.FragmentShader: return ShaderKind.FragmentShader;
				case ShaderType.GeometryShader: return ShaderKind.GeometryShader;
				case ShaderType.ComputeShader: return ShaderKind.ComputeShader;
				case ShaderType.TessControlShader: return ShaderKind.TessControlShader;
				case ShaderType.TessEvaluationShader: return ShaderKind.TessEvaluationShader;
				default: return ShaderKind.VertexShader;
			}
		}

		#endregion
	}
}
