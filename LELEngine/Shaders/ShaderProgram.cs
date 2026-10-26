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
	///     Keyword variants (Unity's shader_feature, automatic): the keywords of a material's [Toggle] / [KeywordEnum]
	///     properties (<see cref="ShaderProperty.Keywords" />) are defines of a program of their own
	///     (<see cref="GetVariant" />), made from the same file on first use, with its own render state (the Properties
	///     block's #if lines see the keywords). Builds list the variants their materials use and the player compiles them
	///     while it loads (<see cref="ShaderVariantList" />).
	///     A program loaded from a file on a thread without the GL context (a scene loading in the background) reads
	///     and prepares its sources there and compiles on first use, or when the scene loader prepares it
	///     (<see cref="EnsureBuilt" />), on the thread that holds the context.
	///     Compiling is started and finished apart (<see cref="StartBuild" />, <see cref="EnsureBuilt" />): a program made on
	///     the context's thread hands its stages to the driver and links without waiting, and the results (status, logs,
	///     uniforms, the binary cache) are read when it is first used or <see cref="FinishStartedBuilds" /> runs. With
	///     parallel shader compile (<see cref="GLCapabilities.ParallelShaderCompile" />) the programs a load started
	///     compile together on the driver's threads.
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

		/// <summary>Compiled and linked (or failed); false while the sources wait for a thread with the GL context or the driver.</summary>
		public bool IsBuilt => pendingStages == null && startedShaders == null;

		/// <summary>The .shader file this program was loaded from (null for programs built from sources).</summary>
		public string SourcePath { get; }

		/// <summary>Root-relative path of the .shader asset, or the engine-relative path of an engine shader; null for inline programs.</summary>
		public string AssetPath
		{
			get => assetPath;
			internal set
			{
				assetPath = value;
				lock (variants)
				{
					foreach (ShaderProgram variant in variants.Values)
					{
						variant.AssetPath = value;
					}
				}
			}
		}

		public string SubAssetName => null;

		/// <summary>The source includes Engine/Instancing.glsl: it can be drawn GPU-driven.</summary>
		public bool SupportsInstancing { get; }

		/// <summary>This program is the instanced variant (compiled with <see cref="InstancedDefine" />).</summary>
		public bool IsInstancedVariant { get; }

		/// <summary>The keywords this program was compiled with (sorted; empty for the shader itself).</summary>
		public IReadOnlyList<string> Keywords => keywords;

		/// <summary>Material parameters declared in the file's Properties block (empty when it has none).</summary>
		public IReadOnlyList<ShaderProperty> Properties => properties;

		/// <summary>The declared properties that pick variants ([Toggle], [KeywordEnum]), in declaration order.</summary>
		public IReadOnlyList<ShaderProperty> KeywordProperties => keywordProperties;

		/// <summary>Blend, depth, cull and queue state declared in the Properties block (the defaults when it declares none).</summary>
		public ShaderRenderState RenderState => renderState;

		/// <summary>Compile and link messages per stage (files and <see cref="FromSources" />) once built; empty when clean.</summary>
		public string BuildLog
		{
			get
			{
				EnsureBuilt();
				return buildLog;
			}
			private set => buildLog = value;
		}

		#endregion

		#region PrivateFields

		private const string InstancingInclude = "Engine/Instancing.glsl";

		private int handle;
		private bool linked;
		private List<KeyValuePair<ShaderType, string>> pendingStages;
		private readonly Dictionary<string, int> uniformCache = new Dictionary<string, int>();
		private readonly Dictionary<string, ActiveUniformType> uniformTypes = new Dictionary<string, ActiveUniformType>(StringComparer.Ordinal);
		private readonly List<ShaderProperty> properties = new List<ShaderProperty>();
		private readonly List<ShaderProperty> keywordProperties = new List<ShaderProperty>();
		private readonly ShaderRenderState renderState = new ShaderRenderState();
		private readonly string[] defines = Array.Empty<string>();
		private readonly string[] keywords = Array.Empty<string>();
		private string assetPath;
		private ShaderProgram baseShader;
		private ShaderProgram instancedVariant;
		private bool instancedVariantCreated;
		private string linkLog = "";
		private string buildLog = "";

		// Started builds (StartBuild): the stage objects the driver compiles, the stages (their types name the log lines)
		// and the binary cache's key, until EnsureBuilt reads the results.
		private int[] startedShaders;
		private List<KeyValuePair<ShaderType, string>> startedStages;
		private string startedCacheKey;

		// Programs whose build was started and not finished (context thread only).
		private static readonly List<ShaderProgram> started = new List<ShaderProgram>();

		// Keyword variants by their sorted keywords joined with spaces (only on the shader itself).
		private readonly Dictionary<string, ShaderProgram> variants = new Dictionary<string, ShaderProgram>(StringComparer.Ordinal);

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

		// A program the binary cache linked (FromSources).
		private ShaderProgram(string name, int linkedProgram)
		{
			Name = name;
			handle = linkedProgram;
			IsLinked = true;
			CollectUniforms();
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
			assetPath = AssetDatabase.Resolve(path, "Shaders") ?? path.Replace('\\', '/');
			IsInstancedVariant = Array.IndexOf(defines, InstancedDefine) >= 0;
			this.defines = defines;
			keywords = defines.Where(define => define != InstancedDefine).OrderBy(define => define, StringComparer.Ordinal).ToArray();
			pendingStages = ReadStages(path, defines, properties, renderState, out bool includesInstancing);
			SupportsInstancing = includesInstancing;
			keywordProperties.AddRange(properties.Where(property => property.Keywords != null));
			StartBuild();
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Compiles and links the prepared sources when that has not happened yet and this thread holds the GL context,
		///     waiting for a build already started (<see cref="StartBuild" />).
		/// </summary>
		public void EnsureBuilt()
		{
			if ((pendingStages != null || startedShaders != null) && GraphicsThread.HasContext)
			{
				StartBuild();
				FinishBuild();
			}
		}

		/// <summary>
		///     Hands the prepared sources to the driver without waiting for the result, when this thread holds the GL
		///     context (a program made there does it itself): start every program a load needs before using one, and the
		///     driver compiles them together. <see cref="EnsureBuilt" /> (any first use) waits for it.
		/// </summary>
		public void StartBuild()
		{
			if (pendingStages != null && GraphicsThread.HasContext)
			{
				Start();
			}
		}

		/// <summary>Waits for every program whose build was started (context thread): their results and errors now.</summary>
		public static void FinishStartedBuilds()
		{
			if (started.Count == 0)
			{
				return;
			}

			foreach (ShaderProgram program in started.ToArray())
			{
				program.EnsureBuilt();
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
			var sources = new List<KeyValuePair<ShaderType, string>>();
			string virtualPath = Path.Combine(ShadersRoot, "Generated", name.Replace('/', '_') + ".glsl");
			foreach (KeyValuePair<ShaderType, string> stage in stages)
			{
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string code = ExpandIncludes(stage.Value.Replace("\r\n", "\n").Split('\n'), virtualPath, visited);
				sources.Add(new KeyValuePair<ShaderType, string>(stage.Key, InsertPreamble(code, stage.Key, defines ?? Array.Empty<string>(), ShaderFeatures.Current)));
			}

			// The driver's binary of the same sources, when an earlier start saved one (ProgramBinaryCache).
			string cacheKey = ProgramBinaryCache.Enabled ? ProgramBinaryCache.Key(sources) : null;
			if (cacheKey != null)
			{
				int cached = GL.CreateProgram();
				if (ProgramBinaryCache.TryLoad(cached, cacheKey))
				{
					return new ShaderProgram(name, cached);
				}

				GL.DeleteProgram(cached);
			}

			var shaders = new List<Shader>();
			var log = new System.Text.StringBuilder();
			bool compiled = true;
			foreach (KeyValuePair<ShaderType, string> source in sources)
			{
				var shader = new Shader(source.Value, source.Key, name);
				if (!Shader.LastCompiled)
				{
					compiled = false;
				}

				if (!string.IsNullOrWhiteSpace(Shader.LastLog))
				{
					log.Append(StageName(source.Key)).Append(": ").Append(Shader.LastLog.Trim()).Append('\n');
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
			if (cacheKey != null && program.linked)
			{
				ProgramBinaryCache.Save(program.handle, cacheKey);
			}

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
				instancedVariant = new ShaderProgram(SourcePath, defines.Append(InstancedDefine).ToArray());
				if (!instancedVariant.IsLinked)
				{
					Console.WriteLine("[Shader] The instanced variant of " + Name + " does not link; its objects draw one by one.");
					instancedVariant.Delete();
					instancedVariant = null;
				}
			}

			return instancedVariant;
		}

		/// <summary>
		///     The program of this shader compiled with <paramref name="variantKeywords" /> defined: the shader itself for
		///     none, otherwise a variant made on first use (any thread: it compiles on the thread with the GL context, see
		///     <see cref="EnsureBuilt" />) and kept with the shader. Keywords the shader never declared still define.
		/// </summary>
		public ShaderProgram GetVariant(IEnumerable<string> variantKeywords)
		{
			if (baseShader != null)
			{
				return baseShader.GetVariant(variantKeywords);
			}

			string[] sorted = variantKeywords?.Where(keyword => !string.IsNullOrEmpty(keyword)).Distinct(StringComparer.Ordinal).OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray() ?? Array.Empty<string>();
			if (sorted.Length == 0 || SourcePath == null)
			{
				return this;
			}

			string key = string.Join(" ", sorted);
			lock (variants)
			{
				if (!variants.TryGetValue(key, out ShaderProgram variant))
				{
					// The shader's own defines (an engine pass's variant of a file) stay: the keywords add to them.
					variant = new ShaderProgram(assetPath ?? SourcePath, defines.Concat(sorted).ToArray()) { baseShader = this };
					variant.assetPath = assetPath;
					variants.Add(key, variant);
				}

				return variant;
			}
		}

		public void Delete()
		{
			pendingStages = null;
			if (startedShaders != null)
			{
				foreach (int shader in startedShaders)
				{
					GL.DeleteShader(shader);
				}

				startedShaders = null;
				startedStages = null;
				started.Remove(this);
			}

			if (handle != 0)
			{
				GL.DeleteProgram(handle);
				handle = 0;
			}

			instancedVariant?.Delete();
			instancedVariant = null;
			lock (variants)
			{
				foreach (ShaderProgram variant in variants.Values)
				{
					variant.Delete();
				}

				variants.Clear();
			}
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

		/// <summary>
		///     The stages of a .shader file as a context with <paramref name="features" /> compiles them: includes
		///     expanded, the engine preamble with <paramref name="defines" /> (a variant's keywords, plus
		///     <see cref="InstancedDefine" /> for its instanced program). No GL calls. With <paramref name="lineDirectives" />
		///     the code carries #line directives naming each file (GL_GOOGLE_cpp_style_line_directive), so a compiler's
		///     messages point into the shader file and its includes: offline validation (drivers get the code without them).
		/// </summary>
		public static List<KeyValuePair<ShaderType, string>> ReadSources(string path, IEnumerable<string> defines, ShaderFeatures features, bool lineDirectives, out bool includesInstancing)
		{
			string[] all = defines?.ToArray() ?? Array.Empty<string>();
			return ReadStages(path, all, new List<ShaderProperty>(), new ShaderRenderState(), features, lineDirectives, out includesInstancing);
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
					ParsePropertiesBlock(block, absolutePath, result, new ShaderRenderState(), Array.Empty<string>());
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
			int[] handles = shaders.Select(shader => shader.Handle).ToArray();
			StartLink(handles);
			FinishLink(handles);
		}

		// Attaches the stages and links without waiting for the driver.
		private void StartLink(int[] shaders)
		{
			foreach (int shader in shaders)
			{
				GL.AttachShader(handle, shader);
			}

			// Fixed attribute slots - must happen before linking.
			VertexLayout.BindStandardLocations(handle);

			// The binary stays retrievable for the program binary cache (when it is on).
			ProgramBinaryCache.PrepareLink(handle);
			GL.LinkProgram(handle);
		}

		// Waits for the link: its status and log (printed), the stages detached, a linked program's uniforms.
		private void FinishLink(int[] shaders)
		{
			int status;
			GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out status);
			linked = status != 0;
			string log = GL.GetProgramInfoLog(handle);
			linkLog = log ?? "";
			if (!linked)
			{
				Console.WriteLine($"[Shader] Link error in {Name}:\n{log}");
			}
			else if (!string.IsNullOrWhiteSpace(log))
			{
				Console.WriteLine($"[Shader] {Name}:\n{log}");
			}

			foreach (int shader in shaders)
			{
				GL.DetachShader(handle, shader);
			}

			if (linked)
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
		///     Reads a Properties block: render state keyword lines (Blend, ZWrite, ZTest, Cull, Queue, ShadowCaster) into
		///     <paramref name="state" />, every other line as a material parameter (see <see cref="ShaderProperty" />);
		///     #if blocks are evaluated over the variant's <paramref name="defines" /> first.
		/// </summary>
		private static void ParsePropertiesBlock(IEnumerable<string> lines, string source, List<ShaderProperty> properties, ShaderRenderState state, string[] defines)
		{
			var propertyLines = new List<string>();
			foreach (string line in ShaderConditionals.Filter(lines, new HashSet<string>(defines, StringComparer.Ordinal), source))
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

		/// <summary>
		///     Starts compiling and linking the stages read by the constructor (on the thread holding the GL context) without
		///     waiting; a binary the cache has of them loads (and links) here instead.
		/// </summary>
		private void Start()
		{
			List<KeyValuePair<ShaderType, string>> stages = pendingStages;
			pendingStages = null;
			handle = GL.CreateProgram();

			// The driver's binary of the same sources, when an earlier start saved one (ProgramBinaryCache).
			string cacheKey = ProgramBinaryCache.Enabled ? ProgramBinaryCache.Key(stages) : null;
			if (cacheKey != null && ProgramBinaryCache.TryLoad(handle, cacheKey))
			{
				linked = true;
				CollectUniforms();
				BuildLog = "";
				return;
			}

			startedShaders = new int[stages.Count];
			for (int i = 0; i < stages.Count; i++)
			{
				startedShaders[i] = Shader.Submit(stages[i].Value, stages[i].Key);
			}

			startedStages = stages;
			startedCacheKey = cacheKey;
			StartLink(startedShaders);
			started.Add(this);
		}

		/// <summary>
		///     Waits for a started build: the compile and link results (printed, and in <see cref="BuildLog" />), the
		///     uniforms, the binary saved to the cache.
		/// </summary>
		private void FinishBuild()
		{
			int[] shaders = startedShaders;
			if (shaders == null)
			{
				return;
			}

			List<KeyValuePair<ShaderType, string>> stages = startedStages;
			startedShaders = null;
			startedStages = null;
			started.Remove(this);

			var log = new System.Text.StringBuilder();
			bool compiled = true;
			for (int i = 0; i < shaders.Length; i++)
			{
				compiled &= Shader.Check(shaders[i], stages[i].Key, SourcePath, out string stageLog);
				if (!string.IsNullOrWhiteSpace(stageLog))
				{
					log.Append(StageName(stages[i].Key)).Append(": ").Append(stageLog.Trim()).Append('\n');
				}
			}

			FinishLink(shaders);
			foreach (int shader in shaders)
			{
				GL.DeleteShader(shader);
			}

			if (!compiled)
			{
				linked = false;
			}

			if (!string.IsNullOrWhiteSpace(linkLog))
			{
				log.Append("link: ").Append(linkLog.Trim()).Append('\n');
			}

			BuildLog = log.ToString();
			if (startedCacheKey != null && linked)
			{
				ProgramBinaryCache.Save(handle, startedCacheKey);
			}

			startedCacheKey = null;
		}

		/// <summary>The stages of a .shader file with their includes expanded and the engine preamble (no GL calls: any thread).</summary>
		private static List<KeyValuePair<ShaderType, string>> ReadStages(string path, string[] defines, List<ShaderProperty> properties, ShaderRenderState state, out bool includesInstancing)
		{
			return ReadStages(path, defines, properties, state, ShaderFeatures.Current, false, out includesInstancing);
		}

		/// <summary>
		///     <see cref="ReadStages(string, string[], List{ShaderProperty}, ShaderRenderState, out bool)" /> for the given
		///     native features, with #line directives naming the files when <paramref name="lineDirectives" /> is set.
		/// </summary>
		private static List<KeyValuePair<ShaderType, string>> ReadStages(string path, string[] defines, List<ShaderProperty> properties, ShaderRenderState state, ShaderFeatures features, bool lineDirectives, out bool includesInstancing)
		{
			var shaders = new List<KeyValuePair<ShaderType, string>>();
			string fullPath = ResolveShaderPath(path);
			string instancing = Path.GetFullPath(Path.Combine(ShadersRoot, InstancingInclude));
			string lineName = lineDirectives ? path.Replace('\\', '/') : null;
			includesInstancing = false;

			var stage = new List<string>();
			int lineNumber = 0;
			int stageFirstLine = 1;
			foreach (string rawLine in File.ReadAllLines(fullPath))
			{
				lineNumber++;
				string line = rawLine.TrimEnd('\r');
				if (line == PropertiesTag)
				{
					// The Properties block is metadata for materials (parameters and render state), not GLSL.
					ParsePropertiesBlock(stage, path, properties, state, defines);
					stage.Clear();
					stageFirstLine = lineNumber + 1;
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
				string code = ExpandIncludes(stage, fullPath, visited, lineName, stageFirstLine);
				includesInstancing |= visited.Contains(instancing);
				stage.Clear();
				if (code.Trim().Length < 10)
				{
					Console.WriteLine("[Shader] Warning: stage " + type + " in " + path + " is shorter than 10 characters!");
				}

				shaders.Add(new KeyValuePair<ShaderType, string>(type, InsertPreamble(code, type, defines, features, lineName, stageFirstLine)));
				stageFirstLine = lineNumber + 1;
			}

			return shaders;
		}

		/// <summary>
		///     Engine defines after the #version line: the stage, the variant's defines and the native features
		///     (<paramref name="features" />: each with its extension directive, which must precede any code). With a
		///     <paramref name="lineName" /> (#line directives) the main file's numbering resumes after them.
		/// </summary>
		private static string InsertPreamble(string code, ShaderType type, string[] defines, ShaderFeatures features, string lineName = null, int firstLine = 1)
		{
			var preamble = new System.Text.StringBuilder();
			if (lineName != null)
			{
				// #line with file names: the checked copy of offline validation, never what a driver compiles.
				preamble.Append("#extension GL_GOOGLE_cpp_style_line_directive : require\n");
			}

			preamble.Append("#define LEL_STAGE_").Append(StageName(type)).Append(" 1\n");
			foreach (string define in defines)
			{
				preamble.Append("#define ").Append(define).Append(" 1\n");
			}

			bool needs460 = false;
			if (type == ShaderType.VertexShader && features.DrawParameters != DrawParametersSupport.None)
			{
				if (features.DrawParameters == DrawParametersSupport.Extension)
				{
					preamble.Append("#extension GL_ARB_shader_draw_parameters : enable\n#define LEL_BASE_INSTANCE gl_BaseInstanceARB\n");
				}
				else
				{
					needs460 = true;
					preamble.Append("#define LEL_BASE_INSTANCE gl_BaseInstance\n");
				}
			}

			if (type == ShaderType.ComputeShader && features.ComputeSubgroups)
			{
				preamble.Append("#extension GL_KHR_shader_subgroup_basic : enable\n#extension GL_KHR_shader_subgroup_arithmetic : enable\n#define LEL_SUBGROUPS 1\n");
			}

			// No binding left for the previous world matrices (Engine/Instancing.glsl): motion vectors see static objects.
			if (type == ShaderType.VertexShader && !features.PreviousModels)
			{
				preamble.Append("#define LEL_NO_PREVIOUS_MODELS 1\n");
			}

			int version = code.IndexOf("#version", StringComparison.Ordinal);
			if (version < 0)
			{
				return preamble + LineDirective(lineName, firstLine) + code;
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

			// Nothing is included before #version: the lines up to it are the main file's.
			string resume = LineDirective(lineName, firstLine + code.Take(version).Count(c => c == '\n') + 1);
			return code.Substring(0, version) + versionLine + "\n" + preamble + resume + (lineEnd < code.Length ? code.Substring(lineEnd + 1) : "");
		}

		/// <summary>"#line N \"name\"" (the next line is line N of that file), or nothing without a name.</summary>
		private static string LineDirective(string name, int line)
		{
			return name == null ? "" : "#line " + line + " \"" + name + "\"\n";
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
		private static string ReadSourceWithIncludes(string fullPath, HashSet<string> visited, string lineName = null)
		{
			visited.Add(Path.GetFullPath(fullPath));
			return LineDirective(lineName, 1) + ExpandIncludes(File.ReadAllLines(fullPath), fullPath, visited, lineName, 1);
		}

		// With a lineName (#line directives, offline validation) the lines are those of that file from firstLine on: an
		// included file names itself, the lines after it go back to this one, and a dropped #include keeps its line.
		private static string ExpandIncludes(IEnumerable<string> lines, string fullPath, HashSet<string> visited, string lineName = null, int firstLine = 1)
		{
			var builder = new System.Text.StringBuilder();
			int lineNumber = firstLine - 1;
			foreach (string rawLine in lines)
			{
				lineNumber++;
				string line = rawLine.Trim();
				if (!line.StartsWith("#include", StringComparison.Ordinal))
				{
					builder.Append(rawLine).Append('\n');
					continue;
				}

				if (lineName != null)
				{
					// Where the include's code goes (or nothing): this file's line, as numbered.
					builder.Append('\n');
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
				builder.Append(ReadSourceWithIncludes(includePath, visited, lineName != null ? includeName : null));
				builder.Append("// ---- end include: ").Append(includeName).Append('\n');
				builder.Append(LineDirective(lineName, lineNumber + 1));
			}

			return builder.ToString();
		}

		#endregion
	}
}
