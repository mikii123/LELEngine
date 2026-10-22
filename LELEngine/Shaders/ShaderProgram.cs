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
	///     A program loaded from a file on a thread without the GL context (a scene loading in the background) reads
	///     and prepares its sources there and compiles on first use, or when the scene loader prepares it
	///     (<see cref="EnsureBuilt" />), on the thread that holds the context.
	/// </summary>
	public sealed class ShaderProgram : IAsset
	{
		#region PublicFields

		/// <summary>Define of the instanced variant: instance and material data come from the GPU scene buffers.</summary>
		public const string InstancedDefine = "LEL_INSTANCED";

		/// <summary>Closing tag of the Properties block of a .shader file (material parameters, see <see cref="ShaderProperty" />).</summary>
		public const string PropertiesTag = "/////Properties";

		public int Handle
		{
			get
			{
				EnsureBuilt();
				return handle;
			}
		}

		public string Name { get; }

		public bool IsLinked
		{
			get
			{
				EnsureBuilt();
				return linked;
			}
			private set => linked = value;
		}

		/// <summary>Compiled and linked (or failed); false while the sources wait for a thread with the GL context.</summary>
		public bool IsBuilt => pendingStages == null;

		/// <summary>The .shader file this program was loaded from (null for programs built from sources).</summary>
		public string SourcePath { get; }

		/// <summary>Root-relative path of the .shader asset, or the engine-relative path of an engine shader; null for inline programs.</summary>
		public string AssetPath { get; internal set; }

		public string SubAssetName => null;

		/// <summary>The source includes Engine/Instancing.glsl: it can be drawn GPU-driven.</summary>
		public bool SupportsInstancing { get; }

		/// <summary>This program is the instanced variant (compiled with <see cref="InstancedDefine" />).</summary>
		public bool IsInstancedVariant { get; }

		/// <summary>Material parameters declared in the file's Properties block (empty when it has none).</summary>
		public IReadOnlyList<ShaderProperty> Properties => properties;

		/// <summary>Blend, depth, cull and queue state declared in the Properties block (the defaults when it declares none).</summary>
		public ShaderRenderState RenderState => renderState;

		/// <summary>Compile and link messages of programs built from sources (<see cref="FromSources" />); empty when clean.</summary>
		public string BuildLog { get; private set; } = "";

		#endregion

		#region PrivateFields

		private const string InstancingInclude = "Engine/Instancing.glsl";

		private int handle;
		private bool linked;
		private List<KeyValuePair<ShaderType, string>> pendingStages;
		private readonly Dictionary<string, int> uniformCache = new Dictionary<string, int>();
		private readonly Dictionary<string, ActiveUniformType> uniformTypes = new Dictionary<string, ActiveUniformType>(StringComparer.Ordinal);
		private readonly List<ShaderProperty> properties = new List<ShaderProperty>();
		private readonly ShaderRenderState renderState = new ShaderRenderState();
		private ShaderProgram instancedVariant;
		private bool instancedVariantCreated;
		private string linkLog = "";

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
			AssetPath = AssetDatabase.Resolve(path, "Shaders") ?? path.Replace('\\', '/');
			IsInstancedVariant = Array.IndexOf(defines, InstancedDefine) >= 0;
			pendingStages = ReadStages(path, defines, properties, renderState, out bool includesInstancing);
			SupportsInstancing = includesInstancing;
			if (GraphicsThread.HasContext)
			{
				Build();
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>Compiles and links the prepared sources when that has not happened yet and this thread holds the GL context.</summary>
		public void EnsureBuilt()
		{
			if (pendingStages != null && GraphicsThread.HasContext)
			{
				Build();
			}
		}

		public void Use()
		{
			EnsureBuilt();
			GL.UseProgram(handle);
		}

		/// <summary>
		///     Builds a program from in-memory stage sources (generated shaders): #include lines expand against the
		///     engine shader library and every stage gets the engine preamble (stage define, variant defines, native
		///     features), as .shader files do. The compile and link messages end up in <see cref="BuildLog" />.
		/// </summary>
		public static ShaderProgram FromSources(string name, IEnumerable<KeyValuePair<ShaderType, string>> stages, params string[] defines)
		{
			var shaders = new List<Shader>();
			var log = new System.Text.StringBuilder();
			bool compiled = true;
			string virtualPath = Path.Combine(ShadersRoot, "Generated", name.Replace('/', '_') + ".glsl");
			foreach (KeyValuePair<ShaderType, string> stage in stages)
			{
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string code = ExpandIncludes(stage.Value.Replace("\r\n", "\n").Split('\n'), virtualPath, visited);
				var shader = new Shader(InsertPreamble(code, stage.Key, defines ?? Array.Empty<string>()), stage.Key, name);
				if (!Shader.LastCompiled)
				{
					compiled = false;
				}

				if (!string.IsNullOrWhiteSpace(Shader.LastLog))
				{
					log.Append(StageName(stage.Key)).Append(": ").Append(Shader.LastLog.Trim()).Append('\n');
				}

				shaders.Add(shader);
			}

			var program = new ShaderProgram(name, shaders.ToArray());
			foreach (Shader shader in shaders)
			{
				shader.Delete();
			}

			if (!compiled)
			{
				program.IsLinked = false;
			}

			if (!string.IsNullOrWhiteSpace(program.linkLog))
			{
				log.Append("link: ").Append(program.linkLog.Trim()).Append('\n');
			}

			program.BuildLog = log.ToString();
			return program;
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
			pendingStages = null;
			if (handle != 0)
			{
				GL.DeleteProgram(handle);
				handle = 0;
			}

			instancedVariant?.Delete();
			instancedVariant = null;
		}

		public int GetAttributeLocation(string name)
		{
			EnsureBuilt();
			return GL.GetAttribLocation(handle, name);
		}

		public int GetUniformLocation(string name)
		{
			EnsureBuilt();
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

		/// <summary>GLSL type of an active uniform (<see cref="ActiveUniformType.Float" /> when unknown).</summary>
		public ActiveUniformType GetUniformType(string name)
		{
			EnsureBuilt();
			ActiveUniformType type;
			return uniformTypes.TryGetValue(name, out type) ? type : ActiveUniformType.Float;
		}

		/// <summary>
		///     Active uniforms that are material parameters: not the camera / lighting / engine uniforms
		///     (<see cref="Material.IsSharedUniform" />). Materials of shaders without a Properties block edit these.
		/// </summary>
		public List<KeyValuePair<string, ActiveUniformType>> GetMaterialUniforms()
		{
			EnsureBuilt();
			var result = new List<KeyValuePair<string, ActiveUniformType>>();
			foreach (KeyValuePair<string, ActiveUniformType> uniform in uniformTypes)
			{
				if (!Material.IsSharedUniform(uniform.Key) && !uniform.Key.Contains("."))
				{
					result.Add(uniform);
				}
			}

			result.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
			return result;
		}

		/// <summary>The declared property of this name, or null.</summary>
		public ShaderProperty FindProperty(string name)
		{
			foreach (ShaderProperty property in properties)
			{
				if (property.Name == name)
				{
					return property;
				}
			}

			return null;
		}

		/// <summary>True when the path names a shader of the engine library (Shaders/Engine/...), not a project asset.</summary>
		public static bool IsEngineShader(string path)
		{
			return !string.IsNullOrEmpty(path) && File.Exists(Path.Combine(ShadersRoot, path.Replace('/', Path.DirectorySeparatorChar)));
		}

		/// <summary>Material parameters of a .shader file without compiling it (editor listings).</summary>
		public static List<ShaderProperty> ReadProperties(string absolutePath)
		{
			var block = new List<string>();
			var result = new List<ShaderProperty>();
			foreach (string line in File.ReadAllLines(absolutePath))
			{
				string trimmed = line.TrimEnd('\r');
				if (trimmed == PropertiesTag)
				{
					ParsePropertiesBlock(block, absolutePath, result, new ShaderRenderState());
					break;
				}

				ShaderType ignored;
				if (Shader.TryParseTag(trimmed, out ignored))
				{
					// A stage closed before any Properties block: the file has none.
					break;
				}

				block.Add(trimmed);
			}

			return result;
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

		/// <summary>Sets a uint uniform (an int upload into a uint uniform is a GL error and leaves it unset).</summary>
		public void SetUint(string name, uint value)
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
			linkLog = log ?? "";
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

			if (IsLinked)
			{
				CollectUniforms();
			}
		}

		/// <summary>Names and types of the active uniforms (array uniforms by their base name).</summary>
		private void CollectUniforms()
		{
			int count;
			GL.GetProgram(handle, GetProgramParameterName.ActiveUniforms, out count);
			for (int i = 0; i < count; i++)
			{
				int size;
				ActiveUniformType type;
				string name = GL.GetActiveUniform(handle, i, out size, out type);
				if (string.IsNullOrEmpty(name))
				{
					continue;
				}

				int bracket = name.IndexOf('[');
				if (bracket > 0)
				{
					name = name.Substring(0, bracket);
				}

				uniformTypes[name] = type;
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

		/// <summary>
		///     Reads a Properties block: render state keyword lines (Blend, ZWrite, ZTest, Cull, Queue) into
		///     <paramref name="state" />, every other line as a material parameter (see <see cref="ShaderProperty" />).
		/// </summary>
		private static void ParsePropertiesBlock(IEnumerable<string> lines, string source, List<ShaderProperty> properties, ShaderRenderState state)
		{
			var propertyLines = new List<string>();
			foreach (string line in lines)
			{
				if (ShaderRenderState.IsStateLine(line))
				{
					if (!state.TryApplyLine(line, out string error))
					{
						Console.WriteLine("[Shader] Cannot parse the render state line '" + line.Trim() + "' in " + source + ": " + error);
					}
				}
				else
				{
					propertyLines.Add(line);
				}
			}

			state.Finish();
			properties.AddRange(ShaderProperty.Parse(propertyLines, source));
		}

		/// <summary>Compiles and links the stages read by the constructor (on the thread holding the GL context).</summary>
		private void Build()
		{
			List<KeyValuePair<ShaderType, string>> stages = pendingStages;
			pendingStages = null;
			var shaders = new List<Shader>(stages.Count);
			foreach (KeyValuePair<ShaderType, string> stage in stages)
			{
				shaders.Add(new Shader(stage.Value, stage.Key, SourcePath));
			}

			handle = GL.CreateProgram();
			Link(shaders.ToArray());
			foreach (Shader shader in shaders)
			{
				shader.Delete();
			}
		}

		/// <summary>The stages of a .shader file with their includes expanded and the engine preamble (no GL calls: any thread).</summary>
		private static List<KeyValuePair<ShaderType, string>> ReadStages(string path, string[] defines, List<ShaderProperty> properties, ShaderRenderState state, out bool includesInstancing)
		{
			var shaders = new List<KeyValuePair<ShaderType, string>>();
			string fullPath = ResolveShaderPath(path);
			string instancing = Path.GetFullPath(Path.Combine(ShadersRoot, InstancingInclude));
			includesInstancing = false;

			var stage = new List<string>();
			foreach (string rawLine in File.ReadAllLines(fullPath))
			{
				string line = rawLine.TrimEnd('\r');
				if (line == PropertiesTag)
				{
					// The Properties block is metadata for materials (parameters and render state), not GLSL.
					ParsePropertiesBlock(stage, path, properties, state);
					stage.Clear();
					continue;
				}

				ShaderType type;
				if (!Shader.TryParseTag(line, out type))
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

				shaders.Add(new KeyValuePair<ShaderType, string>(type, InsertPreamble(code, type, defines)));
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

			// No binding left for the previous world matrices (Engine/Instancing.glsl): motion vectors see static objects.
			if (type == ShaderType.VertexShader && !Rendering.GpuScene.SupportsPreviousModels)
			{
				preamble.Append("#define LEL_NO_PREVIOUS_MODELS 1\n");
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
