using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Linked GPU program. Attribute locations follow <see cref="VertexLayout" />,
	///     uniform locations are cached by name.
	///     Programs loaded from .shader files get an engine preamble after each stage's #version line: LEL_STAGE_VERTEX
	///     (FRAGMENT, GEOMETRY, COMPUTE...), the variant's defines, and the native features of the context
	///     (LEL_BASE_INSTANCE with the draw parameters extension in vertex stages, LEL_SUBGROUPS with the subgroup
	///     extensions in compute stages). A shader that includes "Engine/Instancing.glsl" also has an instanced
	///     variant (<see cref="GetInstancedVariant" />) for GPU-driven draws.
	/// </summary>
	public sealed class ShaderProgram
	{
		#region PublicFields

		/// <summary>Define of the instanced variant: instance and material data come from the GPU scene buffers.</summary>
		public const string InstancedDefine = "LEL_INSTANCED";

		public int Handle => handle;
		public string Name { get; }
		public bool IsLinked { get; private set; }

		/// <summary>The .shader file this program was loaded from (null for programs built from sources).</summary>
		public string SourcePath { get; }

		/// <summary>The source includes Engine/Instancing.glsl: it can be drawn GPU-driven.</summary>
		public bool SupportsInstancing { get; }

		/// <summary>This program is the instanced variant (compiled with <see cref="InstancedDefine" />).</summary>
		public bool IsInstancedVariant { get; }

		#endregion

		#region PrivateFields

		private const string InstancingInclude = "Engine/Instancing.glsl";

		private readonly int handle;
		private readonly Dictionary<string, int> uniformCache = new Dictionary<string, int>();
		private ShaderProgram instancedVariant;
		private bool instancedVariantCreated;

		#endregion

		#region Constructors

		public ShaderProgram(params Shader[] shaders)
			: this("<inline>", shaders)
		{ }

		public ShaderProgram(string name, params Shader[] shaders)
		{
			Name = name;
			handle = GL.CreateProgram();
			Link(shaders);
		}

		/// <summary>
		///     Loads a multi-stage .shader file from the game's Shaders directory.
		/// </summary>
		public ShaderProgram(string path)
			: this(path, Array.Empty<string>())
		{ }

		/// <summary>Loads a .shader file with preprocessor symbols defined in every stage (variants of one source).</summary>
		public ShaderProgram(string path, string[] defines)
		{
			Name = defines.Length == 0 ? path : path + " [" + string.Join(", ", defines) + "]";
			SourcePath = path;
			IsInstancedVariant = Array.IndexOf(defines, InstancedDefine) >= 0;
			List<Shader> shaders = LoadShaderFromFile(path, defines, out bool includesInstancing);
			SupportsInstancing = includesInstancing;

			handle = GL.CreateProgram();
			Link(shaders.ToArray());

			foreach (Shader shader in shaders)
			{
				shader.Delete();
			}
		}

		#endregion

		#region PublicMethods

		public void Use()
		{
			GL.UseProgram(handle);
		}

		/// <summary>
		///     The variant of this program for GPU-driven (instanced, multi-draw indirect) rendering, compiled on first
		///     use; null when the shader does not support it or the variant does not link.
		/// </summary>
		public ShaderProgram GetInstancedVariant()
		{
			if (IsInstancedVariant)
			{
				return this;
			}

			if (!SupportsInstancing || SourcePath == null)
			{
				return null;
			}

			if (!instancedVariantCreated)
			{
				instancedVariantCreated = true;
				instancedVariant = new ShaderProgram(SourcePath, new[] { InstancedDefine });
				if (!instancedVariant.IsLinked)
				{
					Console.WriteLine("[Shader] The instanced variant of " + Name + " does not link; its objects draw one by one.");
					instancedVariant.Delete();
					instancedVariant = null;
				}
			}

			return instancedVariant;
		}

		public void Delete()
		{
			GL.DeleteProgram(handle);
			instancedVariant?.Delete();
			instancedVariant = null;
		}

		public int GetAttributeLocation(string name)
		{
			return GL.GetAttribLocation(handle, name);
		}

		public int GetUniformLocation(string name)
		{
			int location;
			if (!uniformCache.TryGetValue(name, out location))
			{
				location = GL.GetUniformLocation(handle, name);
				uniformCache[name] = location;
			}

			return location;
		}

		public bool HasUniform(string name)
		{
			return GetUniformLocation(name) >= 0;
		}

		// Convenience setters. Every one of them is a no-op when the uniform does not exist
		// (or was optimized away), so passes can set shared uniforms blindly.

		public void SetInt(string name, int value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform1(loc, value);
		}

		public void SetFloat(string name, float value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform1(loc, value);
		}

		public void SetInt2(string name, int x, int y)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform2(loc, x, y);
		}

		public void SetInt3(string name, int x, int y, int z)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform3(loc, x, y, z);
		}

		public void SetVector2(string name, Vector2 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform2(loc, value);
		}

		public void SetVector3(string name, Vector3 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform3(loc, value);
		}

		public void SetVector4(string name, Vector4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform4(loc, value);
		}

		public void SetColor(string name, Color4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform4(loc, value);
		}

		public void SetMatrix4(string name, Matrix4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.UniformMatrix4(loc, false, ref value);
		}

		/// <summary>Sets a vec4 array uniform ("name" addresses its first element).</summary>
		public void SetVector4Array(string name, Vector4[] values)
		{
			int loc = GetUniformLocation(name);
			if (loc < 0 || values.Length == 0)
			{
				return;
			}

			var data = new float[values.Length * 4];
			for (int i = 0; i < values.Length; i++)
			{
				data[i * 4] = values[i].X;
				data[i * 4 + 1] = values[i].Y;
				data[i * 4 + 2] = values[i].Z;
				data[i * 4 + 3] = values[i].W;
			}

			GL.Uniform4(loc, values.Length, data);
		}

		/// <summary>
		///     Binds a texture to the given unit and points the sampler uniform at it.
		/// </summary>
		public void SetTexture(string name, TextureTarget target, int textureHandle, int unit)
		{
			int loc = GetUniformLocation(name);
			if (loc < 0) return;

			GL.ActiveTexture(TextureUnit.Texture0 + unit);
			GL.BindTexture(target, textureHandle);
			GL.Uniform1(loc, unit);
		}

		#endregion

		#region PrivateMethods

		private void Link(Shader[] shaders)
		{
			foreach (Shader shader in shaders)
			{
				GL.AttachShader(handle, shader.Handle);
			}

			// Fixed attribute slots - must happen before linking.
			VertexLayout.BindStandardLocations(handle);

			GL.LinkProgram(handle);

			int status;
			GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out status);
			IsLinked = status != 0;
			string log = GL.GetProgramInfoLog(handle);
			if (!IsLinked)
			{
				Console.WriteLine($"[Shader] Link error in {Name}:\n{log}");
			}
			else if (!string.IsNullOrWhiteSpace(log))
			{
				Console.WriteLine($"[Shader] {Name}:\n{log}");
			}

			foreach (Shader shader in shaders)
			{
				GL.DetachShader(handle, shader.Handle);
			}
		}

		/// <summary>Engine shader library (engine passes and includes such as "Engine/DistanceField.glsl").</summary>
		public static string ShadersRoot => AssetDatabase.EngineShadersRoot;

		/// <summary>
		///     Absolute path of a shader: an engine shader under <see cref="ShadersRoot" />, otherwise a project
		///     asset (asset path, path relative to the legacy Shaders/ folder, or unique file name).
		/// </summary>
		public static string ResolveShaderPath(string path)
		{
			string engine = Path.Combine(ShadersRoot, path);
			if (File.Exists(engine))
			{
				return engine;
			}

			string asset = AssetDatabase.Resolve(path, "Shaders");
			return asset != null ? AssetDatabase.ToAbsolute(asset) : engine;
		}

		private static List<Shader> LoadShaderFromFile(string path, string[] defines, out bool includesInstancing)
		{
			var shaders = new List<Shader>();
			string fullPath = ResolveShaderPath(path);
			string instancing = Path.GetFullPath(Path.Combine(ShadersRoot, InstancingInclude));
			includesInstancing = false;

			var stage = new List<string>();
			foreach (string rawLine in File.ReadAllLines(fullPath))
			{
				ShaderType type;
				if (!Shader.TryParseTag(rawLine.TrimEnd('\r'), out type))
				{
					stage.Add(rawLine);
					continue;
				}

				// Includes expand per stage: a file included by two stages is in both.
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(fullPath) };
				string code = ExpandIncludes(stage, fullPath, visited);
				includesInstancing |= visited.Contains(instancing);
				stage.Clear();
				if (code.Trim().Length < 10)
				{
					Console.WriteLine("[Shader] Warning: stage " + type + " in " + path + " is shorter than 10 characters!");
				}

				shaders.Add(new Shader(InsertPreamble(code, type, defines), type, path));
			}

			return shaders;
		}

		/// <summary>
		///     Engine defines after the #version line: the stage, the variant's defines and the native features the
		///     context has (each with its extension directive, which must precede any code).
		/// </summary>
		private static string InsertPreamble(string code, ShaderType type, string[] defines)
		{
			var preamble = new System.Text.StringBuilder();
			preamble.Append("#define LEL_STAGE_").Append(StageName(type)).Append(" 1\n");
			foreach (string define in defines)
			{
				preamble.Append("#define ").Append(define).Append(" 1\n");
			}

			bool needs460 = false;
			if (type == ShaderType.VertexShader && GLCapabilities.ShaderDrawParameters)
			{
				if (GLCapabilities.ShaderDrawParametersExtension)
				{
					preamble.Append("#extension GL_ARB_shader_draw_parameters : enable\n#define LEL_BASE_INSTANCE gl_BaseInstanceARB\n");
				}
				else
				{
					needs460 = true;
					preamble.Append("#define LEL_BASE_INSTANCE gl_BaseInstance\n");
				}
			}

			if (type == ShaderType.ComputeShader && GLCapabilities.ComputeSubgroups)
			{
				preamble.Append("#extension GL_KHR_shader_subgroup_basic : enable\n#extension GL_KHR_shader_subgroup_arithmetic : enable\n#define LEL_SUBGROUPS 1\n");
			}

			int version = code.IndexOf("#version", StringComparison.Ordinal);
			if (version < 0)
			{
				return preamble + code;
			}

			int lineEnd = code.IndexOf('\n', version);
			if (lineEnd < 0)
			{
				lineEnd = code.Length;
			}

			string versionLine = code.Substring(version, lineEnd - version);
			if (needs460 && int.TryParse(new string(versionLine.Substring(8).Trim().TakeWhile(char.IsDigit).ToArray()), out int number) && number < 460)
			{
				// gl_BaseInstance is core in GLSL 4.60 only.
				versionLine = "#version 460";
			}

			return code.Substring(0, version) + versionLine + "\n" + preamble + (lineEnd < code.Length ? code.Substring(lineEnd + 1) : "");
		}

		private static string StageName(ShaderType type)
		{
			switch (type)
			{
				case ShaderType.FragmentShader: return "FRAGMENT";
				case ShaderType.GeometryShader: return "GEOMETRY";
				case ShaderType.ComputeShader: return "COMPUTE";
				case ShaderType.TessControlShader: return "TESS_CONTROL";
				case ShaderType.TessEvaluationShader: return "TESS_EVALUATION";
				default: return "VERTEX";
			}
		}

		/// <summary>
		///     Reads a GLSL file and expands lines of the form: #include "relative/path.glsl"
		///     Paths resolve against the Shaders root first, then relative to the including file.
		///     Each file is included at most once per stage.
		/// </summary>
		private static string ReadSourceWithIncludes(string fullPath, HashSet<string> visited)
		{
			visited.Add(Path.GetFullPath(fullPath));
			return ExpandIncludes(File.ReadAllLines(fullPath), fullPath, visited);
		}

		private static string ExpandIncludes(IEnumerable<string> lines, string fullPath, HashSet<string> visited)
		{
			var builder = new System.Text.StringBuilder();

			foreach (string rawLine in lines)
			{
				string line = rawLine.Trim();
				if (!line.StartsWith("#include", StringComparison.Ordinal))
				{
					builder.Append(rawLine).Append('\n');
					continue;
				}

				int start = line.IndexOf('"');
				int end = line.LastIndexOf('"');
				if (start < 0 || end <= start)
				{
					Console.WriteLine("[Shader] Malformed include in " + fullPath + ": " + rawLine);
					continue;
				}

				string includeName = line.Substring(start + 1, end - start - 1);
				string includePath = Path.Combine(ShadersRoot, includeName);
				if (!File.Exists(includePath))
				{
					includePath = Path.Combine(Path.GetDirectoryName(fullPath) ?? ShadersRoot, includeName);
				}
				if (!File.Exists(includePath))
				{
					includePath = ResolveShaderPath(includeName);
				}

				if (!File.Exists(includePath))
				{
					Console.WriteLine("[Shader] Include not found: " + includeName + " (from " + fullPath + ")");
					continue;
				}

				if (visited.Contains(Path.GetFullPath(includePath)))
				{
					continue;
				}

				builder.Append("// ---- begin include: ").Append(includeName).Append('\n');
				builder.Append(ReadSourceWithIncludes(includePath, visited));
				builder.Append("// ---- end include: ").Append(includeName).Append('\n');
			}

			return builder.ToString();
		}

		#endregion
	}
}
