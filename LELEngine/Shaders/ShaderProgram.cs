using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LELEngine.Rendering;
using LELEngine.Graphics;
using LELEngine.Mathematics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Linked GPU program. Attribute locations follow <see cref="VertexLayout" />,
	///     uniform locations are cached by name.
	///     Programs loaded from .shader files get an engine preamble after each stage's #version line: LEL_STAGE_VERTEX
	///     (FRAGMENT, GEOMETRY, COMPUTE...), the variant's defines, and the native features of the context
	///     (LEL_BASE_INSTANCE with the draw parameters extension in vertex stages, LEL_SUBGROUPS with the subgroup
	///     extensions in compute stages, GL_EXT_ray_query in the <see cref="RayQueryDefine" /> variants of a device with ray
	///     queries). A shader that includes "Engine/Instancing.glsl" also has an instanced
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
	///     parallel shader compile (<see cref="GraphicsCapabilities.ParallelShaderCompile" />) the programs a load started
	///     compile together on the driver's threads.
	/// </summary>
	public sealed class ShaderProgram : IAsset
	{
		#region PublicFields

		/// <summary>Define of the instanced variant: instance and material data come from the GPU scene buffers.</summary>
		public const string InstancedDefine = "LEL_INSTANCED";

		/// <summary>
		///     Define of a variant that traces rays with ray queries (the GI's passes while they trace the scene's triangles):
		///     with it the preamble enables GL_EXT_ray_query; on a device without ray queries it is left out
		///     (<see cref="ShaderFeatures.RayQueries" />).
		/// </summary>
		public const string RayQueryDefine = "LEL_RAY_QUERY";

		/// <summary>Closing tag of the Properties block of a .shader file (material parameters, see <see cref="ShaderProperty" />).</summary>
		public const string PropertiesTag = "/////Properties";

		/// <summary>The device's program (null while not built on a thread with the graphics context, or released).</summary>
		public GpuProgram Gpu
		{
			get
			{
				EnsureBuilt();
				return gpu;
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

		/// <summary>
		///     Compiled and linked (or failed), with its results read: any thread with a GL context may use it. False while
		///     the sources wait for a thread with the GL context, the driver, or the shader compile thread.
		/// </summary>
		public bool IsBuilt => built;

		/// <summary>
		///     <see cref="EnsureBuilt" /> would not wait for the driver (context thread): built, or started and finished on the
		///     driver's threads (GL_COMPLETION_STATUS_KHR). Without parallel shader compile a started build counts as ready
		///     (the driver compiles when asked, so there is nothing to wait for apart). False while not started.
		/// </summary>
		public bool IsBuildReady
		{
			get
			{
				if (built)
				{
					return true;
				}

				if (pendingStages != null)
				{
					return false;
				}

				GpuProgram building = gpu;
				if (!buildStarted || building == null || !GraphicsCapabilities.ParallelShaderCompile || !GraphicsThread.HasContext)
				{
					return true;
				}

				return building.IsReady;
			}
		}

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

					foreach (ShaderProgram variant in passVariants)
					{
						if (variant != null)
						{
							variant.AssetPath = value;
						}
					}
				}
			}
		}

		public string SubAssetName => null;

		/// <summary>The source includes Engine/Instancing.glsl: it can be drawn GPU-driven.</summary>
		public bool SupportsInstancing { get; private set; }

		/// <summary>This program is the instanced variant (compiled with <see cref="InstancedDefine" />).</summary>
		public bool IsInstancedVariant { get; }

		/// <summary>
		///     The keywords this program was compiled with (sorted; empty for the shader itself). A pass variant
		///     (<see cref="GetPassVariant" />) has the keywords of the program it was made from: its pass's define is not one.
		/// </summary>
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

		private GpuProgram gpu;
		private bool linked;
		private List<KeyValuePair<ShaderStage, string>> pendingStages;

		// Slots of the uniforms asked for by name (-1: the program has none of that name).
		private readonly Dictionary<string, int> uniformCache = new Dictionary<string, int>();
		private readonly Dictionary<string, UniformType> uniformTypes = new Dictionary<string, UniformType>(StringComparer.Ordinal);
		private readonly List<ShaderProperty> properties = new List<ShaderProperty>();
		private readonly List<ShaderProperty> keywordProperties = new List<ShaderProperty>();
		private readonly ShaderRenderState renderState = new ShaderRenderState();
		private readonly string[] defines = Array.Empty<string>();
		private readonly string[] keywords = Array.Empty<string>();
		private string assetPath;
		private ShaderProgram baseShader;
		private ShaderProgram instancedVariant;
		private volatile bool instancedVariantCreated;

		// Built (its results read; set last, after the compile thread's glFinish): the lock-free check of every use.
		private volatile bool built;

		// One build at a time per program (the main thread and the shader compile thread may both want it).
		private readonly object buildLock = new object();

		// The instanced variant made by StartInstancedVariant, its build started, until GetInstancedVariant checks it.
		private ShaderProgram startedInstancedVariant;
		private string linkLog = "";
		private string buildLog = "";

		// The files the source was read from (the .shader and every include) and their stamp then (Reload).
		private string[] sourceFiles = Array.Empty<string>();
		private long sourceStamp;

		// A started build (StartBuild): the device compiles the program, until EnsureBuilt reads the results; and the
		// binary cache's key to save it under.
		private bool buildStarted;
		private string startedCacheKey;

		// Programs whose build was started and not finished (context thread only).
		private static readonly List<ShaderProgram> started = new List<ShaderProgram>();

		// Builds this thread started (compiled, or loaded from the binary cache): what a step of ShaderCompilation built.
		[ThreadStatic]
		private static int startedOnThisThread;

		/// <summary>Program builds the calling thread started since the start (compiled or loaded from the binary cache).</summary>
		internal static int StartedOnThisThread => startedOnThisThread;

		/// <summary>Its build was started on a thread and not finished (the compile thread leaves those to that thread's context).</summary>
		internal bool BuildStartedElsewhere => !built && buildStarted;

		/// <summary>
		///     The binary cache holds every program the variant needs: its shader's own program, the variant's, and its
		///     instanced one when <paramref name="instanced" /> and the shader has one; loading them compiles nothing. Reads
		///     the sources (no GL calls; any thread, once the cache has started). False when unsure.
		/// </summary>
		internal static bool BinariesCached(ShaderVariant variant, bool instanced)
		{
			if (!ProgramBinaryCache.Enabled || variant.Shader == null)
			{
				return false;
			}

			try
			{
				string path = InternalStorage.ShaderKey(variant.Shader);
				string[] keywords = variant.Keywords;
				// The render state of the variant itself: which passes it draws with programs of its own.
				var state = new ShaderRenderState();
				if (!Cached(path, Array.Empty<string>(), out bool includesInstancing, keywords.Length == 0 ? state : null))
				{
					return false;
				}

				if (keywords.Length > 0 && !Cached(path, keywords, out _, state))
				{
					return false;
				}

				bool instancedToo = instanced && includesInstancing;
				if (instancedToo && !Cached(path, keywords.Append(InstancedDefine).ToArray(), out _))
				{
					return false;
				}

				foreach (ShaderPass pass in ShaderPasses.Drawn)
				{
					if (!ShaderPasses.Declares(state, pass))
					{
						continue;
					}

					string define = ShaderPasses.DefineOf(pass);
					if (!Cached(path, keywords.Append(define).ToArray(), out _)
					    || (instancedToo && !Cached(path, keywords.Append(InstancedDefine).Append(define).ToArray(), out _)))
					{
						return false;
					}
				}

				return true;
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				return false;
			}
		}

		// The binary cache has the program of these sources (as the constructor reads them); state gets their render state.
		private static bool Cached(string path, string[] defines, out bool includesInstancing, ShaderRenderState state = null)
		{
			List<KeyValuePair<ShaderStage, string>> stages = ReadStages(path, defines, new List<ShaderProperty>(), state ?? new ShaderRenderState(), ShaderFeatures.Current, false, out includesInstancing);
			return ProgramBinaryCache.Has(ProgramBinaryCache.Key(stages));
		}

		// Keyword variants by their sorted keywords joined with spaces (only on the shader itself).
		private readonly Dictionary<string, ShaderProgram> variants = new Dictionary<string, ShaderProgram>(StringComparer.Ordinal);

		// This program's variants for the engine's passes (GetPassVariant; index = the pass), made on first use.
		private readonly ShaderProgram[] passVariants = new ShaderProgram[ShaderPasses.Count];

		#endregion

		#region Constructors

		public ShaderProgram(params Shader[] shaders)
			: this("<inline>", shaders)
		{ }

		public ShaderProgram(string name, params Shader[] shaders)
		{
			Name = name;
			var stages = new List<KeyValuePair<ShaderStage, string>>();
			foreach (Shader shader in shaders)
			{
				stages.Add(new KeyValuePair<ShaderStage, string>(shader.Type, shader.Source));
			}

			gpu = GraphicsDevice.Current.CreateProgram(Describe(name, stages, false));
			buildStarted = true;
			FinishBuild();
			built = true;
		}

		// A program the binary cache gave (FromSources).
		private ShaderProgram(string name, GpuProgram linkedProgram)
		{
			Name = name;
			gpu = linkedProgram;
			linked = true;
			CollectUniforms();
			built = true;
		}

		// A generated program whose build is started and finished apart, as a file program's (StartFromSources).
		private ShaderProgram(string name, List<KeyValuePair<ShaderStage, string>> stages)
		{
			Name = name;
			pendingStages = stages;
			StartBuild();
		}

		/// <summary>
		///     Loads a multi-stage .shader file from the game's Shaders directory.
		/// </summary>
		public ShaderProgram(string path)
			: this(path, Array.Empty<string>())
		{ }

		/// <summary>Loads a .shader file with preprocessor symbols defined in every stage (variants of one source).</summary>
		public ShaderProgram(string path, string[] defines)
			: this(path, defines, true)
		{ }

		/// <param name="start">
		///     Start the build now (<see cref="StartBuild" />); false for programs made inside a lock others wait on (the
		///     shader caches): the caller starts it after leaving the lock, since on the compile thread starting is building.
		/// </param>
		internal ShaderProgram(string path, string[] defines, bool start)
		{
			Name = defines.Length == 0 ? path : path + " [" + string.Join(", ", defines) + "]";
			SourcePath = path;
			assetPath = AssetDatabase.Resolve(path, "Shaders") ?? path.Replace('\\', '/');
			IsInstancedVariant = Array.IndexOf(defines, InstancedDefine) >= 0;
			this.defines = defines;
			keywords = defines.Where(define => define != InstancedDefine && !ShaderPasses.IsPassDefine(define)).OrderBy(define => define, StringComparer.Ordinal).ToArray();
			long readFrom = FrameProfiler.Timestamp;
			ReadSource();
			FrameProfiler.EventSince(readFrom, "shader", Name, "source read and expanded (file and includes)");
			if (start)
			{
				StartBuild();
			}
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Compiles and links the prepared sources when that has not happened yet and this thread holds the GL context,
		///     waiting for a build already started (<see cref="StartBuild" />), or for the shader compile thread building it
		///     (<see cref="ShaderCompilation" />).
		/// </summary>
		public void EnsureBuilt()
		{
			if (built || !GraphicsThread.HasContext)
			{
				return;
			}

			lock (buildLock)
			{
				if (built)
				{
					return;
				}

				if (pendingStages != null)
				{
					Start();
				}

				FinishBuild();
				MarkBuilt();
			}
		}

		/// <summary>
		///     Hands the prepared sources to the driver without waiting for the result, when this thread holds the GL
		///     context (a program made there does it itself): start every program a load needs before using one, and the
		///     driver compiles them together. <see cref="EnsureBuilt" /> (any first use) waits for it. On the shader compile
		///     thread (<see cref="ShaderCompilation" />) the program is built whole here: nothing half built leaves it.
		/// </summary>
		public void StartBuild()
		{
			if (built || pendingStages == null || !GraphicsThread.HasContext)
			{
				return;
			}

			lock (buildLock)
			{
				if (pendingStages == null)
				{
					return;
				}

				Start();
				if (!buildStarted || ShaderCompilation.OnCompileThread)
				{
					// Loaded from the binary cache, or the compile thread: done here.
					FinishBuild();
					MarkBuilt();
				}
			}
		}

		/// <summary>Waits for every program whose build was started (context thread): their results and errors now.</summary>
		public static void FinishStartedBuilds()
		{
			ShaderProgram[] waiting;
			lock (started)
			{
				if (started.Count == 0)
				{
					return;
				}

				waiting = started.ToArray();
			}

			foreach (ShaderProgram program in waiting)
			{
				program.EnsureBuilt();
			}
		}

		/// <summary>Makes this the program of the next draws or dispatches.</summary>
		public void Use()
		{
			EnsureBuilt();
			GraphicsDevice.Current.Commands.SetProgram(gpu);
		}

		/// <summary>
		///     Builds a program from in-memory stage sources (generated shaders): #include lines expand against the
		///     engine shader library and every stage gets the engine preamble (stage define, variant defines, native
		///     features), as .shader files do. The compile and link messages end up in <see cref="BuildLog" />.
		/// </summary>
		public static ShaderProgram FromSources(string name, IEnumerable<KeyValuePair<ShaderStage, string>> stages, params string[] defines)
		{
			List<KeyValuePair<ShaderStage, string>> sources = GeneratedSources(name, stages, defines);

			// The driver's binary of the same sources, when an earlier start saved one (ProgramBinaryCache).
			long startedAt = FrameProfiler.Timestamp;
			string cacheKey = ProgramBinaryCache.Enabled ? ProgramBinaryCache.Key(sources) : null;
			if (cacheKey != null)
			{
				GpuProgram cached = ProgramBinaryCache.TryLoad(Describe(name, sources, false), cacheKey);
				if (cached != null)
				{
					FrameProfiler.EventSince(startedAt, "shader", name, "generated program loaded from the binary cache");
					return new ShaderProgram(name, cached);
				}
			}

			var shaders = new Shader[sources.Count];
			for (int i = 0; i < sources.Count; i++)
			{
				shaders[i] = new Shader(sources[i].Value, sources[i].Key, name);
			}

			var program = new ShaderProgram(name, shaders);
			if (cacheKey != null && program.linked)
			{
				ProgramBinaryCache.Save(program.gpu, cacheKey);
			}

			FrameProfiler.EventSince(startedAt, "shader", name, "generated program compiled and linked (not in the binary cache)");
			return program;
		}

		/// <summary>
		///     <see cref="FromSources" /> with the build started and not waited for (on the thread holding the GL context:
		///     loaded from the binary cache, or handed to the driver; elsewhere the first use builds it): start every
		///     generated program a load needs, then use them, and the driver compiles them together. The first use of the
		///     program (<see cref="IsLinked" />, a uniform, <see cref="Use" />) waits for it; its messages are in
		///     <see cref="BuildLog" /> then.
		/// </summary>
		public static ShaderProgram StartFromSources(string name, IEnumerable<KeyValuePair<ShaderStage, string>> stages, params string[] defines)
		{
			return new ShaderProgram(name, GeneratedSources(name, stages, defines));
		}

		// The stages of a generated program as they are compiled: includes expanded, the engine preamble in front.
		private static List<KeyValuePair<ShaderStage, string>> GeneratedSources(string name, IEnumerable<KeyValuePair<ShaderStage, string>> stages, string[] defines)
		{
			var sources = new List<KeyValuePair<ShaderStage, string>>();
			string virtualPath = Path.Combine(ShadersRoot, "Generated", name.Replace('/', '_') + ".glsl");
			foreach (KeyValuePair<ShaderStage, string> stage in stages)
			{
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				string code = ExpandIncludes(stage.Value.Replace("\r\n", "\n").Split('\n'), virtualPath, visited);
				sources.Add(new KeyValuePair<ShaderStage, string>(stage.Key, InsertPreamble(code, stage.Key, defines ?? Array.Empty<string>(), ShaderFeatures.Current)));
			}

			return sources;
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

			if (instancedVariantCreated)
			{
				return instancedVariant;
			}

			// Made under the lock, built outside it (the compile thread may be building it: this waits for that).
			ShaderProgram made;
			lock (variants)
			{
				if (instancedVariantCreated)
				{
					return instancedVariant;
				}

				made = startedInstancedVariant ??= new ShaderProgram(SourcePath, defines.Append(InstancedDefine).ToArray(), false);
			}

			made.StartBuild();
			if (made.BuildStartedElsewhere && ShaderCompilation.OnCompileThread)
			{
				// The main thread started it (StartInstancedVariant): it finishes it, in its own context.
				return null;
			}

			bool madeLinked = made.IsLinked;
			lock (variants)
			{
				if (!instancedVariantCreated && startedInstancedVariant == made)
				{
					startedInstancedVariant = null;
					if (madeLinked)
					{
						instancedVariant = made;
					}
					else
					{
						Console.WriteLine("[Shader] The instanced variant of " + Name + " does not link; its objects draw one by one.");
						made.Delete();
						instancedVariant = null;
					}

					instancedVariantCreated = true;
				}

				return instancedVariant;
			}
		}

		/// <summary>
		///     Makes the instanced variant (<see cref="GetInstancedVariant" />) and starts its build without waiting for it:
		///     the program to wait for (<see cref="IsBuildReady" />), then <see cref="GetInstancedVariant" /> checks it. Null
		///     when the shader has none, or when it was made already (then <see cref="GetInstancedVariant" /> is done).
		/// </summary>
		internal ShaderProgram StartInstancedVariant()
		{
			if (IsInstancedVariant || !SupportsInstancing || SourcePath == null || instancedVariantCreated)
			{
				return null;
			}

			ShaderProgram made;
			lock (variants)
			{
				if (instancedVariantCreated)
				{
					return null;
				}

				made = startedInstancedVariant ??= new ShaderProgram(SourcePath, defines.Append(InstancedDefine).ToArray(), false);
			}

			made.StartBuild();
			return made;
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
			ShaderProgram made;
			lock (variants)
			{
				if (variants.TryGetValue(key, out ShaderProgram existing))
				{
					return existing;
				}

				// The shader's own defines (an engine pass's variant of a file) stay: the keywords add to them.
				made = new ShaderProgram(assetPath ?? SourcePath, defines.Concat(sorted).ToArray(), false) { baseShader = this };
				made.assetPath = assetPath;
				variants.Add(key, made);
			}

			// Outside the lock: on the compile thread this builds it whole, and other threads may want other variants.
			made.StartBuild();
			return made;
		}

		/// <summary>
		///     This program compiled for one of the engine's passes (<see cref="ShaderPass" />): the same file with this
		///     program's defines (a variant's keywords, the instanced define) and the pass's, made on first use (any thread;
		///     it compiles on the thread with the GL context) and kept with this program. A material's variant gives the
		///     per-object program of the pass, its instanced variant the GPU-driven one. Whether the shader has such a
		///     variant is its render state's word (<see cref="ShaderPasses.Declares" />): asking for one it does not declare
		///     compiles the file with a define it ignores. Null for a program that is not from a file.
		/// </summary>
		public ShaderProgram GetPassVariant(ShaderPass pass)
		{
			ShaderProgram made = passVariants[(int)pass];
			if (made != null || SourcePath == null)
			{
				return made;
			}

			lock (variants)
			{
				made = passVariants[(int)pass];
				if (made != null)
				{
					return made;
				}

				made = new ShaderProgram(SourcePath, defines.Append(ShaderPasses.DefineOf(pass)).ToArray(), false);
				made.assetPath = assetPath;
				passVariants[(int)pass] = made;
			}

			// Outside the lock, as the keyword variants: on the compile thread this builds it whole.
			made.StartBuild();
			return made;
		}

		/// <summary>
		///     Builds this program's variants for the passes a frame draws its shader with (<see cref="ShaderPasses.Drawn" />,
		///     those its render state declares: its own shadow caster and geometry prepass), so the pass's first draw
		///     compiles nothing. On the thread with the GL context; nothing for most shaders. Returns how many programs this
		///     call built.
		/// </summary>
		public int BuildPassVariants()
		{
			if (SourcePath == null || !IsLinked)
			{
				return 0;
			}

			int builtNow = 0;
			foreach (ShaderPass pass in ShaderPasses.Drawn)
			{
				if (!ShaderPasses.Declares(RenderState, pass))
				{
					continue;
				}

				// Counted when this call made it or finished its build (a binary from the cache is built as it starts).
				ShaderProgram known = passVariants[(int)pass];
				bool done = known != null && known.built;
				ShaderProgram variant = GetPassVariant(pass);
				if (variant == null || done)
				{
					continue;
				}

				variant.EnsureBuilt();
				if (variant.built)
				{
					builtNow++;
				}
			}

			return builtNow;
		}

		/// <summary>
		///     The defines of the programs the engine's passes draw a variant of a .shader file with: one per pass of
		///     <see cref="ShaderPasses.Drawn" /> the file's render state declares for these keywords (empty for most
		///     shaders). Each is compiled next to the keywords, and next to <see cref="InstancedDefine" /> for GPU-driven
		///     draws. No GL calls.
		/// </summary>
		public static List<string> ReadPassDefines(string path, IEnumerable<string> defines)
		{
			var state = new ShaderRenderState();
			ReadStages(path, defines?.ToArray() ?? Array.Empty<string>(), new List<ShaderProperty>(), state, ShaderFeatures.Current, false, out _);
			var passes = new List<string>();
			foreach (ShaderPass pass in ShaderPasses.Drawn)
			{
				if (ShaderPasses.Declares(state, pass))
				{
					passes.Add(ShaderPasses.DefineOf(pass));
				}
			}

			return passes;
		}

		/// <summary>The pass variants made so far (<see cref="GetPassVariant" />), for what follows the program: reloads, deletion.</summary>
		private ShaderProgram[] MadePassVariants()
		{
			lock (variants)
			{
				return passVariants.Where(variant => variant != null).ToArray();
			}
		}

		/// <summary>A file the source was read from (the .shader or one of its includes) changed since, or is gone.</summary>
		public bool SourceChanged => SourcePath != null && sourceFiles.Length > 0 && StampOf(sourceFiles) != sourceStamp;

		/// <summary>
		///     Reads the .shader file again and rebuilds the program in place (materials and draws keep this object), its
		///     keyword and instanced variants too; on the thread with the GL context. False when the file cannot be read: the
		///     program stays as it was.
		/// </summary>
		public bool Reload()
		{
			if (SourcePath == null)
			{
				return false;
			}

			var readProperties = new List<ShaderProperty>();
			var readState = new ShaderRenderState();
			var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			List<KeyValuePair<ShaderStage, string>> stages;
			bool includesInstancing;
			try
			{
				stages = ReadStages(SourcePath, defines, readProperties, readState, ShaderFeatures.Current, false, out includesInstancing, files);
			}
			catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
			{
				Console.WriteLine("[Shader] Cannot read " + Name + " again (" + e.Message + "); it stays as it was");
				return false;
			}

			lock (buildLock)
			{
				built = false;
				ReleaseProgram();
				properties.Clear();
				properties.AddRange(readProperties);
				keywordProperties.Clear();
				keywordProperties.AddRange(properties.Where(property => property.Keywords != null));
				renderState.CopyFrom(readState);
				SupportsInstancing = includesInstancing;
				sourceFiles = files.ToArray();
				sourceStamp = StampOf(sourceFiles);
				uniformCache.Clear();
				uniformTypes.Clear();
				linked = false;
				linkLog = "";
				buildLog = "";
				pendingStages = stages;
			}

			StartBuild();

			if (instancedVariant != null && !SupportsInstancing)
			{
				instancedVariant.Delete();
				instancedVariant = null;
			}

			instancedVariant?.Reload();
			// A variant that did not link may now.
			instancedVariantCreated = instancedVariant != null;
			startedInstancedVariant?.Delete();
			startedInstancedVariant = null;
			lock (variants)
			{
				foreach (ShaderProgram variant in variants.Values)
				{
					variant.Reload();
				}
			}

			foreach (ShaderProgram variant in MadePassVariants())
			{
				variant.Reload();
			}

			return true;
		}

		public void Delete()
		{
			lock (buildLock)
			{
				ReleaseProgram();
			}

			instancedVariant?.Delete();
			instancedVariant = null;
			startedInstancedVariant?.Delete();
			startedInstancedVariant = null;
			lock (variants)
			{
				foreach (ShaderProgram variant in variants.Values)
				{
					variant.Delete();
				}

				variants.Clear();
				for (int i = 0; i < passVariants.Length; i++)
				{
					passVariants[i]?.Delete();
					passVariants[i] = null;
				}
			}
		}

		public int GetAttributeLocation(string name)
		{
			EnsureBuilt();
			return gpu != null ? gpu.GetAttributeLocation(name) : -1;
		}

		/// <summary>
		///     The slot of a uniform by name ("name", "array[3]", "struct.member"), -1 when the program has none of that
		///     name: what the device's program takes values and textures by (<see cref="Gpu" />).
		/// </summary>
		public int GetSlot(string name)
		{
			EnsureBuilt();
			int slot;
			if (!uniformCache.TryGetValue(name, out slot))
			{
				slot = gpu != null ? gpu.GetUniformSlot(name) : -1;
				uniformCache[name] = slot;
			}

			return slot;
		}

		public bool HasUniform(string name)
		{
			return GetSlot(name) >= 0;
		}

		/// <summary>Type of a uniform (<see cref="UniformType.Float" /> when unknown).</summary>
		public UniformType GetUniformType(string name)
		{
			EnsureBuilt();
			UniformType type;
			return uniformTypes.TryGetValue(name, out type) ? type : UniformType.Float;
		}

		/// <summary>
		///     Active uniforms that are material parameters: not the camera / lighting / engine uniforms
		///     (<see cref="Material.IsSharedUniform" />). Materials of shaders without a Properties block edit these.
		/// </summary>
		public List<KeyValuePair<string, UniformType>> GetMaterialUniforms()
		{
			EnsureBuilt();
			var result = new List<KeyValuePair<string, UniformType>>();
			foreach (KeyValuePair<string, UniformType> uniform in uniformTypes)
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
		public static List<KeyValuePair<ShaderStage, string>> ReadSources(string path, IEnumerable<string> defines, ShaderFeatures features, bool lineDirectives, out bool includesInstancing)
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

				ShaderStage ignored;
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
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetInt(slot, value);
		}

		public void SetFloat(string name, float value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetFloat(slot, value);
		}

		/// <summary>Sets a uint uniform (an int given to a uint uniform leaves it unset).</summary>
		public void SetUint(string name, uint value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetUInt(slot, value);
		}

		public void SetInt2(string name, int x, int y)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetInt2(slot, x, y);
		}

		public void SetInt3(string name, int x, int y, int z)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetInt3(slot, x, y, z);
		}

		public void SetInt4(string name, int x, int y, int z, int w)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetInt4(slot, x, y, z, w);
		}

		public void SetUint4(string name, uint x, uint y, uint z, uint w)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetUInt4(slot, x, y, z, w);
		}

		public void SetVector2(string name, Vector2 value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetVector2(slot, value);
		}

		public void SetVector3(string name, Vector3 value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetVector3(slot, value);
		}

		public void SetVector4(string name, Vector4 value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetVector4(slot, value);
		}

		public void SetColor(string name, Color4 value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetColor(slot, value);
		}

		public void SetMatrix4(string name, Matrix4 value)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetMatrix4(slot, value);
		}

		/// <summary>Sets a vec4 array uniform ("name" addresses its first element).</summary>
		public void SetVector4Array(string name, Vector4[] values)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetVector4Array(slot, values);
		}

		/// <summary>Sets a float array uniform ("name" addresses its first element).</summary>
		public void SetFloatArray(string name, ReadOnlySpan<float> values)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetFloatArray(slot, values);
		}

		/// <summary>Sets an int array uniform ("name" addresses its first element).</summary>
		public void SetIntArray(string name, ReadOnlySpan<int> values)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetIntArray(slot, values);
		}

		/// <summary>
		///     The texture a sampler reads from now on (null: none), and how: a null sampler takes the texture's own
		///     (<see cref="GpuTexture.Sampler" />).
		/// </summary>
		public void SetTexture(string name, GpuTexture texture, GpuSampler sampler = null)
		{
			int slot = GetSlot(name);
			if (slot >= 0) gpu.SetTexture(slot, texture, sampler);
		}

		public void SetTexture(string name, RenderTexture texture, GpuSampler sampler = null)
		{
			SetTexture(name, texture?.Gpu, sampler);
		}

		public void SetTexture(string name, Texture texture, GpuSampler sampler = null)
		{
			SetTexture(name, texture?.Gpu, sampler);
		}

		#endregion

		#region PrivateMethods

		/// <summary>What the device builds a program from: the stages' sources and the fixed attribute locations.</summary>
		private static ProgramDescriptor Describe(string name, List<KeyValuePair<ShaderStage, string>> stages, bool asynchronous)
		{
			var descriptor = new ProgramDescriptor { Name = name, Asynchronous = asynchronous };
			foreach (KeyValuePair<ShaderStage, string> stage in stages)
			{
				descriptor.Stages.Add(stage);
			}

			// Fixed attribute slots: a mesh's buffers are laid out once for every shader.
			VertexLayout.AddStandardLocations(descriptor);
			return descriptor;
		}

		/// <summary>Names and types of the uniforms (array uniforms by their base name).</summary>
		private void CollectUniforms()
		{
			foreach (UniformInfo uniform in gpu.Uniforms)
			{
				string name = uniform.Name;
				int bracket = name.IndexOf('[');
				if (bracket > 0)
				{
					name = name.Substring(0, bracket);
				}

				uniformTypes[name] = uniform.Type;
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
			string lowResolution = state.LowResolutionProblem();
			if (lowResolution != null)
			{
				Console.WriteLine("[Shader] LowResolution On in " + source + " draws at full resolution: " + lowResolution);
			}

			properties.AddRange(ShaderProperty.Parse(propertyLines, source));
		}

		/// <summary>Reads the .shader file (stages, properties, render state) and notes the files it came from.</summary>
		private void ReadSource()
		{
			var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			pendingStages = ReadStages(SourcePath, defines, properties, renderState, ShaderFeatures.Current, false, out bool includesInstancing, files);
			SupportsInstancing = includesInstancing;
			keywordProperties.AddRange(properties.Where(property => property.Keywords != null));
			sourceFiles = files.ToArray();
			sourceStamp = StampOf(sourceFiles);
		}

		private static long StampOf(string[] files)
		{
			long stamp = 17;
			foreach (string file in files)
			{
				var info = new FileInfo(file);
				stamp = stamp * 31 + (info.Exists ? info.LastWriteTimeUtc.Ticks ^ info.Length : 0);
			}

			return stamp;
		}

		/// <summary>Deletes the GL program and a build in flight (the sources waiting for one are dropped too).</summary>
		private void ReleaseProgram()
		{
			pendingStages = null;
			if (buildStarted)
			{
				buildStarted = false;
				lock (started)
				{
					started.Remove(this);
				}
			}

			gpu?.Dispose();
			gpu = null;
		}

		/// <summary>
		///     Starts compiling and linking the stages read by the constructor (on the thread holding the GL context) without
		///     waiting; a binary the cache has of them loads (and links) here instead.
		/// </summary>
		private void Start()
		{
			List<KeyValuePair<ShaderStage, string>> stages = pendingStages;
			pendingStages = null;
			startedOnThisThread++;
			long startedAt = FrameProfiler.Timestamp;
			ProgramDescriptor descriptor = Describe(Name, stages, true);

			// The device's binary of the same sources, when an earlier start saved one (ProgramBinaryCache).
			string cacheKey = ProgramBinaryCache.Enabled ? ProgramBinaryCache.Key(stages) : null;
			GpuProgram cached = cacheKey != null ? ProgramBinaryCache.TryLoad(descriptor, cacheKey) : null;
			if (cached != null)
			{
				gpu = cached;
				linked = true;
				CollectUniforms();
				BuildLog = "";
				FrameProfiler.EventSince(startedAt, "shader", Name, "loaded from the binary cache");
				return;
			}

			gpu = GraphicsDevice.Current.CreateProgram(descriptor);
			buildStarted = true;
			startedCacheKey = cacheKey;
			FrameProfiler.EventSince(startedAt, "shader", Name, ProgramBinaryCache.Enabled ? "not in the binary cache: compile submitted to the driver" : "no binary cache: compile submitted to the driver");
			if (!ShaderCompilation.OnCompileThread)
			{
				// The compile thread finishes its own at once (StartBuild): never one for another context to wait on.
				lock (started)
				{
					started.Add(this);
				}
			}
		}

		// The results are read: from now on any thread uses it without the lock. A program the compile thread built is
		// finished on the GPU first (glFinish), so the other contexts see it whole.
		private void MarkBuilt()
		{
			if (ShaderCompilation.OnCompileThread)
			{
				GraphicsDevice.Current.Finish();
			}

			built = true;
		}

		/// <summary>
		///     Waits for a started build: the compile and link results (printed, and in <see cref="BuildLog" />), the
		///     uniforms, the binary saved to the cache.
		/// </summary>
		private void FinishBuild()
		{
			if (!buildStarted)
			{
				return;
			}

			buildStarted = false;
			lock (started)
			{
				started.Remove(this);
			}

			long waitedFrom = FrameProfiler.Timestamp;
			var log = new System.Text.StringBuilder();
			gpu.Finish();
			string debugName = SourcePath ?? Name;
			foreach (KeyValuePair<ShaderStage, string> error in gpu.StageErrors)
			{
				Console.WriteLine($"[Shader] Compile error in {debugName} ({error.Key}):\n{error.Value}");
				log.Append(StageName(error.Key)).Append(": ").Append((error.Value ?? "").Trim()).Append('\n');
			}

			linked = gpu.IsLinked && gpu.StageErrors.Count == 0;
			linkLog = gpu.Log ?? "";
			if (!gpu.IsLinked)
			{
				Console.WriteLine($"[Shader] Link error in {Name}:\n{linkLog}");
			}
			else if (!string.IsNullOrWhiteSpace(linkLog))
			{
				Console.WriteLine($"[Shader] {Name}:\n{linkLog}");
			}

			if (linked)
			{
				CollectUniforms();
			}

			if (!string.IsNullOrWhiteSpace(linkLog))
			{
				log.Append("link: ").Append(linkLog.Trim()).Append('\n');
			}

			BuildLog = log.ToString();
			FrameProfiler.EventSince(waitedFrom, "shader", Name, ShaderCompilation.OnCompileThread ? "compiled and linked on the compile thread" : "waited for the driver's compile and link");
			if (startedCacheKey != null && linked)
			{
				long savedFrom = FrameProfiler.Timestamp;
				ProgramBinaryCache.Save(gpu, startedCacheKey);
				FrameProfiler.EventSince(savedFrom, "shader", Name, "binary read back and saved to the cache");
			}

			startedCacheKey = null;
		}

		/// <summary>The stages of a .shader file with their includes expanded and the engine preamble (no GL calls: any thread).</summary>
		private static List<KeyValuePair<ShaderStage, string>> ReadStages(string path, string[] defines, List<ShaderProperty> properties, ShaderRenderState state, out bool includesInstancing)
		{
			return ReadStages(path, defines, properties, state, ShaderFeatures.Current, false, out includesInstancing);
		}

		/// <summary>
		///     <see cref="ReadStages(string, string[], List{ShaderProperty}, ShaderRenderState, out bool)" /> for the given
		///     native features, with #line directives naming the files when <paramref name="lineDirectives" /> is set;
		///     <paramref name="files" /> gets every file read (the .shader and its includes).
		/// </summary>
		private static List<KeyValuePair<ShaderStage, string>> ReadStages(string path, string[] defines, List<ShaderProperty> properties, ShaderRenderState state, ShaderFeatures features, bool lineDirectives, out bool includesInstancing,
			HashSet<string> files = null)
		{
			var shaders = new List<KeyValuePair<ShaderStage, string>>();
			string fullPath = ResolveShaderPath(path);
			files?.Add(Path.GetFullPath(fullPath));
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

				ShaderStage type;
				if (!Shader.TryParseTag(line, out type))
				{
					stage.Add(rawLine);
					continue;
				}

				// Includes expand per stage: a file included by two stages is in both.
				var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(fullPath) };
				string code = ExpandIncludes(stage, fullPath, visited, lineName, stageFirstLine);
				includesInstancing |= visited.Contains(instancing);
				files?.UnionWith(visited);
				stage.Clear();
				if (code.Trim().Length < 10)
				{
					Console.WriteLine("[Shader] Warning: stage " + type + " in " + path + " is shorter than 10 characters!");
				}

				shaders.Add(new KeyValuePair<ShaderStage, string>(type, InsertPreamble(code, type, defines, features, lineName, stageFirstLine)));
				stageFirstLine = lineNumber + 1;
			}

			return shaders;
		}

		/// <summary>
		///     Engine defines after the #version line: the stage, the variant's defines and the native features
		///     (<paramref name="features" />: each with its extension directive, which must precede any code). With a
		///     <paramref name="lineName" /> (#line directives) the main file's numbering resumes after them.
		/// </summary>
		private static string InsertPreamble(string code, ShaderStage type, string[] defines, ShaderFeatures features, string lineName = null, int firstLine = 1)
		{
			var preamble = new System.Text.StringBuilder();
			if (lineName != null)
			{
				// #line with file names: the checked copy of offline validation, never what a driver compiles.
				preamble.Append("#extension GL_GOOGLE_cpp_style_line_directive : require\n");
			}

			preamble.Append("#define LEL_STAGE_").Append(StageName(type)).Append(" 1\n");
			bool rayQueries = false;
			foreach (string define in defines)
			{
				if (define == RayQueryDefine)
				{
					// Only where the device traces rays: without, the stage compiles its other path.
					if (!features.RayQueries)
					{
						continue;
					}

					rayQueries = true;
				}

				preamble.Append("#define ").Append(define).Append(" 1\n");
			}

			if (rayQueries)
			{
				preamble.Append("#extension GL_EXT_ray_query : require\n");
				if (features.PositionFetch)
				{
					preamble.Append("#extension GL_EXT_ray_tracing_position_fetch : require\n#define LEL_RAY_POSITION_FETCH 1\n");
				}
			}

			bool needs460 = false;
			if (type == ShaderStage.Vertex && features.DrawParameters != DrawParametersSupport.None)
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

			if (type == ShaderStage.Compute && features.ComputeSubgroups)
			{
				preamble.Append("#extension GL_KHR_shader_subgroup_basic : enable\n#extension GL_KHR_shader_subgroup_arithmetic : enable\n#define LEL_SUBGROUPS 1\n");
			}

			// No binding left for the previous world matrices (Engine/Instancing.glsl): motion vectors see static objects.
			if (type == ShaderStage.Vertex && !features.PreviousModels)
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

		private static string StageName(ShaderStage type)
		{
			switch (type)
			{
				case ShaderStage.Fragment: return "FRAGMENT";
				case ShaderStage.Geometry: return "GEOMETRY";
				case ShaderStage.Compute: return "COMPUTE";
				case ShaderStage.TessControl: return "TESS_CONTROL";
				case ShaderStage.TessEvaluation: return "TESS_EVALUATION";
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
