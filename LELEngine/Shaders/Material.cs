using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Serialization;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     A shader plus the values of its parameters: an asset (.material document, JSON in the editor, binary in
	///     builds) referenced by renderers, or an instance created in code. Properties are typed values
	///     (<see cref="MaterialProperty" />) uploaded as uniforms of the same name at draw time; the shader's Properties
	///     block (<see cref="ShaderProperty" />) declares which exist and how the editor shows them. Materials from
	///     <see cref="InternalStorage" /> are shared; use <see cref="Clone" /> for per-object values.
	/// </summary>
	public sealed class Material : IAsset, ISerializationCallbackReceiver
	{
		#region PublicFields

		public const string Extension = ".material";

		/// <summary>
		///     The texture units below the engine's own (it binds its maps on the units from here up to
		///     <see cref="ExtraTextureUnitStart" />): a material's first textures. Further ones go on the units from
		///     <see cref="ExtraTextureUnitStart" /> the context has (<see cref="TextureUnitAt" />).
		/// </summary>
		public const int MaxTextureUnits = 8;

		/// <summary>The first texture unit above the engine's reserved ones: a material's ninth texture and on.</summary>
		public const int ExtraTextureUnitStart = 23;

		/// <summary>How many textures a material can bind on this context (8 below the engine's units, the rest above them).</summary>
		public static int MaxTextures => MaxTextureUnits + Math.Max(0, Rendering.GLCapabilities.MaxTextureUnits - ExtraTextureUnitStart);

		/// <summary>The engine's lit shader: new materials and unreadable material files use it.</summary>
		public const string DefaultShaderPath = "Engine/Lit.shader";

		/// <summary>Root-relative path of the .material file; copies made with <see cref="Clone" /> keep it.</summary>
		public string AssetPath { get; internal set; }

		public string SubAssetName => null;

		public string name;

		public ShaderProgram shader
		{
			get => m_Shader;
			set
			{
				m_Shader = value;
				ResetToShaderDefaults(false);
			}
		}

		/// <summary>The material's shader (the asset; draws use its variant, <see cref="Program" />).</summary>
		public ShaderProgram UsingShader => m_Shader;

		public string ShaderPath => m_Shader?.AssetPath;
		public IReadOnlyList<MaterialProperty> Properties => m_Properties;

		/// <summary>
		///     The program draws use: the shader's variant for the keywords the material's [Toggle] / [KeywordEnum]
		///     values and its set map textures enable (<see cref="Keywords" />), the shader itself when it has none. Follows
		///     value and texture changes (scripts and the editor set them; the variant compiles on first use).
		/// </summary>
		public ShaderProgram Program
		{
			get
			{
				ShaderProgram shader = m_Shader;
				if (shader == null || shader.KeywordProperties.Count == 0)
				{
					return shader;
				}

				long key = KeywordState(shader);
				VariantChoice choice = variantChoice;
				if (choice != null && choice.Shader == shader && choice.Key == key && key >= 0)
				{
					return choice.Variant;
				}

				ShaderProgram variant = shader.GetVariant(GetKeywords(shader));
				variantChoice = new VariantChoice { Shader = shader, Key = key, Variant = variant };
				return variant;
			}
		}

		/// <summary>The keywords the material's [Toggle] / [KeywordEnum] values enable (sorted).</summary>
		public IReadOnlyList<string> Keywords => Program?.Keywords ?? Array.Empty<string>();

		/// <summary>Blend, depth, cull, queue and shadow state of the material's variant (the defaults without a shader).</summary>
		public ShaderRenderState RenderState => Program?.RenderState ?? ShaderRenderState.Default;

		/// <summary>
		///     Render queue (<see cref="MaterialQueue" />): the shader's unless overridden (set -1 to follow the shader
		///     again). Queues above <see cref="MaterialQueue.GeometryLast" /> draw in the transparent pass. A variant with
		///     GrabPass or SceneDepth stays in a transparent queue whatever the override: the scene copy it reads exists
		///     only there.
		/// </summary>
		public int renderQueue
		{
			get
			{
				ShaderRenderState state = RenderState;
				int queue = m_RenderQueue >= 0 ? m_RenderQueue : state.Queue;
				return (state.GrabPass || state.SceneDepth) && !MaterialQueue.IsTransparent(queue) ? MaterialQueue.GeometryLast + 1 : queue;
			}
			set => m_RenderQueue = value;
		}

		/// <summary>The queue is overridden on this material (not the shader's).</summary>
		public bool OverridesRenderQueue => m_RenderQueue >= 0;

		/// <summary>
		///     Drawn by the transparent pass: after the opaques and the sky, sorted back to front, with the shader's blend
		///     state, and lit by the GI volume (cones or radiance cache probes) instead of the screen-space resolve. Such
		///     renderers cast translucent shadows (the light through them is mix(1, Color.rgb, Color.a)), are not
		///     voxelized and get no distance field or surface cache cards.
		/// </summary>
		public bool IsTransparent => MaterialQueue.IsTransparent(renderQueue);

		/// <summary>The main color ("Color"), white when the material has none.</summary>
		public Color4 color
		{
			get => GetColor("Color", Color4.White);
			set => SetColor("Color", value);
		}

		/// <summary>The first 2D texture (albedo): "DiffuseMap", "MainTex", "AlbedoMap" or the first texture property.</summary>
		public Texture mainTexture
		{
			get
			{
				foreach (string candidate in MainTextureNames)
				{
					Texture texture = GetTexture(candidate);
					if (texture != null)
					{
						return texture;
					}
				}

				foreach (MaterialProperty property in m_Properties)
				{
					if (property is TextureProperty textureProperty && textureProperty.value is Texture2D)
					{
						return textureProperty.value;
					}
				}

				return null;
			}
			set
			{
				foreach (string candidate in MainTextureNames)
				{
					if (Program != null && Program.HasUniform(candidate))
					{
						SetTexture(candidate, value);
						return;
					}
				}

				SetTexture(MainTextureNames[0], value);
			}
		}

		#endregion

		#region PrivateFields

		private static readonly string[] MainTextureNames = { "DiffuseMap", "MainTex", "AlbedoMap", "Albedo", "BaseMap" };

		// Empty texture properties by name: they bind the sampler kind's default (see ApplyMissingTextures).
		private static readonly Dictionary<string, TextureProperty> DefaultTextures = new Dictionary<string, TextureProperty>();

		[SerializeField] private ShaderProgram m_Shader;
		[SerializeField] private List<MaterialProperty> m_Properties = new List<MaterialProperty>();
		[SerializeField] private int m_RenderQueue = -1;

		// The variant of the last keyword state (Program is read for every draw and sort), replaced as a whole: a scene
		// loading in the background reads it too.
		private sealed class VariantChoice
		{
			public ShaderProgram Shader;
			public long Key;
			public ShaderProgram Variant;
		}

		private VariantChoice variantChoice;

		#endregion

		#region Constructors

		/// <summary>Creates a material for a shader with the defaults of the shader's properties.</summary>
		public Material(ShaderProgram shader)
		{
			m_Shader = shader;
			name = shader?.Name ?? "Material";
			ResetToShaderDefaults(false);
		}

		/// <summary>Creates a material for the shader at a path (asset path, file name or engine shader).</summary>
		public Material(ShaderProgram shader, string shaderPath)
			: this(shader ?? (shaderPath != null ? InternalStorage.GetOrCreateShader(shaderPath) : null))
		{ }

		/// <summary>Independent copy of another material (same shader and texture objects, own values).</summary>
		public Material(Material source)
		{
			m_Shader = source.m_Shader;
			name = source.name;
			AssetPath = source.AssetPath;
			m_RenderQueue = source.m_RenderQueue;
			foreach (MaterialProperty property in source.m_Properties)
			{
				m_Properties.Add(property.Clone());
			}
		}

		private Material()
		{ }

		#endregion

		#region PublicMethods

		// ---------------------------------------------------------------- files

		/// <summary>Loads a .material document (either format). Unreadable files give a warning material so the scene still loads.</summary>
		public static Material Load(string assetPath)
		{
			string absolute = AssetDatabase.ToAbsolute(assetPath);
			Material material = null;
			try
			{
				byte[] data = File.ReadAllBytes(absolute);
				if (Documents.IsDocument(data))
				{
					material = Documents.ReadObject(data, typeof(Material), null, new SerializationContext { Location = assetPath }) as Material;
				}
				else
				{
					Debug.LogError($"[Material] {assetPath} is not a material document (the old text format is no longer read): recreate it in the editor");
				}
			}
			catch (Exception e)
			{
				Debug.LogError($"[Material] Cannot load {assetPath}: {e.Message}");
			}

			if (material == null)
			{
				material = CreateFallback();
			}

			material.AssetPath = assetPath;
			material.name = Path.GetFileNameWithoutExtension(assetPath);
			return material;
		}

		/// <summary>Writes the material to its asset file (or a new one) in <see cref="Documents.FileFormat" />.</summary>
		public void Save(string assetPath = null)
		{
			assetPath = assetPath ?? AssetPath;
			if (string.IsNullOrEmpty(assetPath))
			{
				throw new InvalidOperationException("The material has no asset path");
			}

			if (AssetDatabase.IsEngineResource(assetPath))
			{
				throw new InvalidOperationException(assetPath + " is a built-in material (read-only): save a copy in the project instead");
			}

			AssetPath = assetPath.Replace('\\', '/');
			name = Path.GetFileNameWithoutExtension(AssetPath);
			Documents.WriteObjectFile(AssetDatabase.ToAbsolute(AssetPath), this);
		}

		/// <summary>The material as a document (undo snapshots, copies).</summary>
		public byte[] ToBytes(DocumentFormat format = DocumentFormat.Binary)
		{
			return Documents.WriteObject(this, format, new SerializationContext { Location = name });
		}

		/// <summary>Replaces shader and values from a document written by <see cref="ToBytes" />.</summary>
		public void ApplyBytes(byte[] data)
		{
			Documents.ReadObject(data, typeof(Material), this, new SerializationContext { Location = name });
		}

		/// <summary>Independent copy sharing the shader and texture objects. Use for per-object parameter changes.</summary>
		public Material Clone()
		{
			return new Material(this);
		}

		// ---------------------------------------------------------------- shader

		public void SetShader(ShaderProgram program)
		{
			shader = program;
		}

		public void SetShader(string shaderPath)
		{
			shader = InternalStorage.GetOrCreateShader(shaderPath);
		}

		/// <summary>
		///     Adds the shader's declared properties that the material lacks (with their defaults); with
		///     <paramref name="dropUnknown" /> also removes values the shader does not declare.
		/// </summary>
		public void ResetToShaderDefaults(bool dropUnknown)
		{
			if (m_Shader == null)
			{
				return;
			}

			foreach (ShaderProperty declared in m_Shader.Properties)
			{
				if (Find(declared.Name) == null)
				{
					m_Properties.Add(CreateDefault(declared));
				}
			}

			if (dropUnknown && m_Shader.Properties.Count > 0)
			{
				m_Properties.RemoveAll(property => m_Shader.FindProperty(property.name) == null);
			}
		}

		// ---------------------------------------------------------------- values

		public bool HasProperty(string propertyName)
		{
			return Find(propertyName) != null;
		}

		public IEnumerable<string> GetPropertyNames()
		{
			foreach (MaterialProperty property in m_Properties)
			{
				yield return property.name;
			}
		}

		public void RemoveProperty(string propertyName)
		{
			m_Properties.RemoveAll(property => property.name == propertyName);
		}

		public void SetFloat(string propertyName, float value)
		{
			if (Find(propertyName) is FloatProperty existing)
			{
				existing.value = value;
				return;
			}

			Replace(new FloatProperty { name = propertyName, value = value });
		}

		public float GetFloat(string propertyName, float fallback = 0f)
		{
			switch (Find(propertyName))
			{
				case FloatProperty f: return f.value;
				case IntProperty i: return i.value;
				case VectorProperty v: return v.value.X;
				default: return fallback;
			}
		}

		public void SetInt(string propertyName, int value)
		{
			if (Find(propertyName) is IntProperty existing)
			{
				existing.value = value;
				return;
			}

			Replace(new IntProperty { name = propertyName, value = value });
		}

		public int GetInt(string propertyName, int fallback = 0)
		{
			switch (Find(propertyName))
			{
				case IntProperty i: return i.value;
				case FloatProperty f: return (int)f.value;
				default: return fallback;
			}
		}

		public void SetColor(string propertyName, Color4 value)
		{
			if (Find(propertyName) is ColorProperty existing)
			{
				existing.value = value;
				return;
			}

			Replace(new ColorProperty { name = propertyName, value = value });
		}

		public Color4 GetColor(string propertyName, Color4 fallback = default)
		{
			switch (Find(propertyName))
			{
				case ColorProperty c: return c.value;
				case VectorProperty v: return new Color4(v.value.X, v.value.Y, v.value.Z, v.value.W);
				default: return fallback;
			}
		}

		public void SetVector(string propertyName, Vector4 value)
		{
			switch (Find(propertyName))
			{
				case VectorProperty existing:
					existing.value = value;
					return;
				case ColorProperty color:
					// Colors keep their kind (the editor shows a picker); callers set them as vectors in legacy code.
					color.value = new Color4(value.X, value.Y, value.Z, value.W);
					return;
			}

			Replace(new VectorProperty { name = propertyName, value = value });
		}

		public Vector4 GetVector(string propertyName, Vector4 fallback = default)
		{
			switch (Find(propertyName))
			{
				case VectorProperty v: return v.value;
				case ColorProperty c: return new Vector4(c.value.R, c.value.G, c.value.B, c.value.A);
				case FloatProperty f: return new Vector4(f.value, 0f, 0f, 0f);
				default: return fallback;
			}
		}

		public void SetTexture(string propertyName, Texture value)
		{
			if (Find(propertyName) is TextureProperty existing)
			{
				existing.value = value;
				return;
			}

			Replace(new TextureProperty { name = propertyName, value = value });
		}

		public Texture GetTexture(string propertyName)
		{
			return (Find(propertyName) as TextureProperty)?.value;
		}

		public MaterialProperty GetProperty(string propertyName)
		{
			return Find(propertyName);
		}

		// Legacy names kept for engine passes and existing scripts.

		public void SetVector3(string propertyName, Vector3 value)
		{
			SetVector(propertyName, new Vector4(value, 0f));
		}

		public void SetVector4(string propertyName, Vector4 value)
		{
			SetVector(propertyName, value);
		}

		public bool TryGetFloat(string propertyName, out float value)
		{
			MaterialProperty property = Find(propertyName);
			value = property != null ? GetFloat(propertyName) : 0f;
			return property is FloatProperty || property is IntProperty;
		}

		public bool TryGetVector4(string propertyName, out Vector4 value)
		{
			MaterialProperty property = Find(propertyName);
			value = property != null ? GetVector(propertyName) : Vector4.Zero;
			return property is VectorProperty || property is ColorProperty;
		}

		/// <summary>GL handle of a 2D texture property, or 0 when the material has no such texture.</summary>
		public int GetTextureHandle(string propertyName)
		{
			Texture texture = GetTexture(propertyName);
			return texture is Texture2D ? texture.Handle : 0;
		}

		// ---------------------------------------------------------------- drawing

		/// <summary>
		///     Uploads this material's values. Conventional material parameters are reset to defaults first, because
		///     materials sharing a program would otherwise inherit values left by the previous draw.
		/// </summary>
		public void SetUniforms()
		{
			ShaderProgram program = Program;
			if (program != null)
			{
				SetUniforms(program);
			}
		}

		/// <summary>Uploads this material's values to the given program (e.g. the instanced variant of its shader).</summary>
		public void SetUniforms(ShaderProgram program)
		{
			ResetStandardUniforms(program);
			int textureUnit = 0;
			foreach (MaterialProperty property in m_Properties)
			{
				if (property != null && property.name != null)
				{
					property.Apply(program, ref textureUnit);
				}
			}

			ApplyMissingProperties(program, this, ref textureUnit);
		}

		/// <summary>
		///     Binds the neutral default of every texture the program declares and uploads its other declared values'
		///     defaults (tiling, scales), for draws without a material of their own (GPU-driven buckets of materials with only
		///     the standard values, which come from the material table).
		/// </summary>
		public static void SetDefaultTextures(ShaderProgram program)
		{
			int textureUnit = 0;
			ApplyMissingProperties(program, null, ref textureUnit);
		}

		/// <summary>
		///     The material sets only the standard parameters (Color, Emissive, Roughness). GPU-driven draws read those
		///     from the material table, so such materials of one shader share a multi-draw call; a material with other
		///     values gets a call of its own with them set.
		/// </summary>
		public bool HasOnlyStandardUniforms()
		{
			foreach (MaterialProperty property in m_Properties)
			{
				if (property != null && !IsStandardUniform(property.name))
				{
					return false;
				}
			}

			return true;
		}

		/// <summary>The standard parameters with the defaults of <see cref="ResetStandardUniforms" />.</summary>
		public void GetStandardParameters(out Vector4 color, out Vector4 emissive, out float roughness)
		{
			if (!TryGetVector4("Color", out color))
			{
				color = Vector4.One;
			}

			if (!TryGetVector4("Emissive", out emissive))
			{
				emissive = Vector4.Zero;
			}

			TryGetFloat("Roughness", out roughness);
		}

		public static bool IsStandardUniform(string uniformName)
		{
			return uniformName == "Color" || uniformName == "Emissive" || uniformName == "Roughness";
		}

		/// <summary>
		///     Defaults for the conventional material parameters understood by engine passes (voxelization reads the
		///     same names). A material only has to specify what differs.
		/// </summary>
		public static void ResetStandardUniforms(ShaderProgram program)
		{
			program.SetVector4("Color", Vector4.One);
			program.SetVector4("Emissive", Vector4.Zero);
			program.SetFloat("Roughness", 0f);
		}

		/// <summary>
		///     True when one of this material's values has a name the camera or lighting passes also set. Draws grouped
		///     by shader send the camera and lighting uniforms once per program; after such a material they must be sent
		///     again for the next object.
		/// </summary>
		public bool OverridesSharedUniforms()
		{
			foreach (MaterialProperty property in m_Properties)
			{
				if (property != null && IsSharedUniform(property.name))
				{
					return true;
				}
			}

			return false;
		}

		/// <summary>Uniform names owned by Camera.SetUniforms, Transform.SetModelMatrix and Lighting.SetUniforms (not material parameters).</summary>
		public static bool IsSharedUniform(string uniformName)
		{
			if (uniformName == null)
			{
				return false;
			}

			switch (uniformName)
			{
				case "projectionMatrix":
				case "viewMatrix":
				case "modelMatrix":
				case "invViewProjection":
				case "lightPosition":
				case "CamPosition":
				case "lightColor":
				case "ShadowMap":
				case "VoxelRadiance":
				case "VoxelRadianceAniso":
				case "VoxelRadianceSHX":
				case "VoxelRadianceSHY":
				case "VoxelRadianceSHZ":
				case "IndirectDiffuse":
				case "IndirectSpecular":
				case "vInstanceIndex":
					return true;
			}

			return uniformName.StartsWith("LAmbient.", StringComparison.Ordinal) || uniformName.StartsWith("LDirectional.", StringComparison.Ordinal)
				|| uniformName.StartsWith("LSpecular.", StringComparison.Ordinal) || uniformName.StartsWith("shadow", StringComparison.Ordinal)
				|| uniformName.StartsWith("gi", StringComparison.Ordinal) || uniformName.StartsWith("voxel", StringComparison.Ordinal)
				|| uniformName.StartsWith("sdf", StringComparison.Ordinal) || uniformName.StartsWith("GlobalSdf", StringComparison.Ordinal)
				|| uniformName.StartsWith("rc", StringComparison.Ordinal) || uniformName.StartsWith("lel", StringComparison.Ordinal)
				|| uniformName.StartsWith("Lel", StringComparison.Ordinal) || uniformName.StartsWith("Sdf", StringComparison.Ordinal)
				|| uniformName.StartsWith("Probe", StringComparison.Ordinal) || uniformName.StartsWith("Radiance", StringComparison.Ordinal)
				|| uniformName.StartsWith("Surface", StringComparison.Ordinal);
		}

		/// <summary>Data textures (normals, roughness, metalness, specular masks) must stay linear; everything else is treated as sRGB color.</summary>
		public static bool IsColorTexture(string uniformName)
		{
			string n = uniformName.ToLowerInvariant();
			return !(n.Contains("normal") || n.Contains("bump") || n.Contains("spec") || n.Contains("rough") || n.Contains("metal") || n.Contains("mask") || n.Contains("height"));
		}

		/// <summary>
		///     The texture unit of a material's texture number <paramref name="index" /> (in the order they are bound): 0..7
		///     below the engine's units, then <see cref="ExtraTextureUnitStart" /> and up; -1 past what the context has.
		/// </summary>
		public static int TextureUnitAt(int index)
		{
			if (index < MaxTextureUnits)
			{
				return index;
			}

			int unit = ExtraTextureUnitStart + index - MaxTextureUnits;
			return unit < Rendering.GLCapabilities.MaxTextureUnits ? unit : -1;
		}

		public static bool IsNormalMapName(string uniformName)
		{
			string n = uniformName.ToLowerInvariant();
			return n.Contains("normal") || n.Contains("bump");
		}

		/// <summary>A typed property with the declared default (textures resolve "white" / "black" / "normal" to the built-in textures).</summary>
		public static MaterialProperty CreateDefault(ShaderProperty declared)
		{
			switch (declared.Type)
			{
				case ShaderPropertyType.Float:
				case ShaderPropertyType.Range:
					return new FloatProperty { name = declared.Name, value = declared.DefaultValue.X };
				case ShaderPropertyType.Int:
					return new IntProperty { name = declared.Name, value = (int)declared.DefaultValue.X };
				case ShaderPropertyType.Color:
					return new ColorProperty { name = declared.Name, value = new Color4(declared.DefaultValue.X, declared.DefaultValue.Y, declared.DefaultValue.Z, declared.DefaultValue.W) };
				case ShaderPropertyType.Vector:
					return new VectorProperty { name = declared.Name, value = declared.DefaultValue };
				default:
					return new TextureProperty { name = declared.Name, value = null };
			}
		}

		public void OnBeforeSerialize()
		{
			m_Properties.RemoveAll(property => property == null || string.IsNullOrEmpty(property.name));
		}

		public void OnAfterDeserialize()
		{
			m_Properties.RemoveAll(property => property == null || string.IsNullOrEmpty(property.name));
		}

		public override string ToString()
		{
			return "Material(" + (AssetPath ?? name) + ")";
		}

		#endregion

		#region PrivateMethods

		/// <summary>
		///     Textures the program declares that the material does not set (a material saved before the shader gained the
		///     property, or no material at all) get the declared kind's neutral default: an unbound sampler would read
		///     whatever an earlier draw left on its texture unit. Missing numbers get their declared defaults (the standard
		///     parameters have theirs from <see cref="ResetStandardUniforms" /> or the material table): a program keeps its
		///     uniform values between draws, so they would show the last material drawn with it.
		/// </summary>
		private static void ApplyMissingProperties(ShaderProgram program, Material material, ref int textureUnit)
		{
			foreach (ShaderProperty declared in program.Properties)
			{
				if (material != null && material.Find(declared.Name) != null)
				{
					continue;
				}

				if (!declared.IsTexture)
				{
					if (!IsStandardUniform(declared.Name))
					{
						declared.DefaultValueProperty.Apply(program, ref textureUnit);
					}

					continue;
				}

				TextureProperty fallback;
				lock (DefaultTextures)
				{
					if (!DefaultTextures.TryGetValue(declared.Name, out fallback))
					{
						fallback = new TextureProperty { name = declared.Name };
						DefaultTextures[declared.Name] = fallback;
					}
				}

				fallback.Apply(program, ref textureUnit);
			}
		}

		// The keyword each [Toggle] / [KeywordEnum] property picks, packed 6 bits a property (0: none, else option + 1);
		// -1 (not cached) for more properties or options than that holds. A map keyword is on while its texture is set.
		private long KeywordState(ShaderProgram shader)
		{
			long key = 0;
			IReadOnlyList<ShaderProperty> declared = shader.KeywordProperties;
			if (declared.Count > 10)
			{
				return -1;
			}

			for (int i = 0; i < declared.Count; i++)
			{
				ShaderProperty property = declared[i];
				long state;
				if (property.IsMapKeyword)
				{
					state = GetTexture(property.Name) != null ? 1 : 0;
				}
				else
				{
					float value = GetFloat(property.Name, property.DefaultValue.X);
					state = property.IsKeywordEnum ? property.OptionIndex(value) + 1 : value != 0f ? 1 : 0;
				}

				if (state > 63)
				{
					return -1;
				}

				key |= state << (i * 6);
			}

			return key;
		}

		private List<string> GetKeywords(ShaderProgram shader)
		{
			var result = new List<string>();
			foreach (ShaderProperty property in shader.KeywordProperties)
			{
				string keyword = property.IsMapKeyword
					? GetTexture(property.Name) != null ? property.Keywords[0] : null
					: property.KeywordFor(GetFloat(property.Name, property.DefaultValue.X));
				if (keyword != null)
				{
					result.Add(keyword);
				}
			}

			return result;
		}

		private MaterialProperty Find(string propertyName)
		{
			foreach (MaterialProperty property in m_Properties)
			{
				if (property != null && property.name == propertyName)
				{
					return property;
				}
			}

			return null;
		}

		private void Replace(MaterialProperty property)
		{
			for (int i = 0; i < m_Properties.Count; i++)
			{
				if (m_Properties[i] != null && m_Properties[i].name == property.name)
				{
					m_Properties[i] = property;
					return;
				}
			}

			m_Properties.Add(property);
		}

		/// <summary>Magenta on the default lit shader: the material file could not be read.</summary>
		private static Material CreateFallback()
		{
			ShaderProgram program = null;
			try
			{
				if (AssetDatabase.IsEngineResource(DefaultShaderPath))
				{
					program = InternalStorage.GetOrCreateShader(DefaultShaderPath);
				}
			}
			catch (Exception)
			{
				// No engine shader library next to the binaries: an empty material.
			}

			var material = new Material(program);
			material.SetColor("Color", new Color4(1f, 0f, 1f, 1f));
			return material;
		}

		#endregion
	}
}
