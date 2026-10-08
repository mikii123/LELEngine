using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Shaders.Uniforms;
using OpenTK.Mathematics;
using Vector3 = OpenTK.Mathematics.Vector3;
using Vector4 = OpenTK.Mathematics.Vector4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Shader + uniform values loaded from a .material file in the game's Materials directory.
	///     Format: first line is the shader file, then repeated blocks of
	///     "uniform" / type / name / value (value line only for non-matrix types).
	///     Materials from InternalStorage are shared; call <see cref="Clone" /> for per-object values.
	/// </summary>
	public sealed class Material : IAsset
	{
		#region PublicFields

		/// <summary>Root-relative path of the .material file; copies made with <see cref="Clone" /> keep it.</summary>
		public string AssetPath { get; private set; }

		public ShaderProgram UsingShader { get; private set; }
		public string ShaderPath { get; }
		public List<Uniform> Uniforms = new List<Uniform>();

		#endregion

		#region PrivateFields

		private int textureIndex;

		#endregion

		#region Constructors

		/// <param name="path">Asset path or file name of the .material file (see <see cref="AssetDatabase.Resolve" />).</param>
		public Material(string path)
		{
			AssetPath = AssetDatabase.Resolve(path, "Materials") ?? path;
			using (StreamReader sr = new StreamReader(AssetDatabase.ToAbsolute(AssetPath)))
			{
				ShaderPath = sr.ReadLine().Trim();
				while (!sr.EndOfStream)
				{
					string line = sr.ReadLine().Trim();
					if (line != "uniform")
					{
						continue;
					}

					string type = sr.ReadLine().Trim();
					string name = sr.ReadLine().Trim();
					switch (type)
					{
						case "mat4":
							Uniforms.Add(new Uniforms.Matrix4(name));
							break;
						case "vec4":
						{
							string[] words = sr.ReadLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
							Uniforms.Add(new Uniforms.Vector4(name, new Vector4(
								Extensions.ParseFloat(words[0]),
								Extensions.ParseFloat(words[1]),
								Extensions.ParseFloat(words[2]),
								Extensions.ParseFloat(words[3]))));
							break;
						}
						case "vec3":
						{
							string[] words = sr.ReadLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
							Uniforms.Add(new Uniforms.Vector3(name, new Vector3(
								Extensions.ParseFloat(words[0]),
								Extensions.ParseFloat(words[1]),
								Extensions.ParseFloat(words[2]))));
							break;
						}
						case "float":
							Uniforms.Add(new Float(name, Extensions.ParseFloat(sr.ReadLine())));
							break;
						case "sampler2D":
						{
							string source = sr.ReadLine().Trim();
							bool srgb = IsColorTexture(name);
							Uniforms.Add(new Texture2D(name, source, textureIndex, srgb));
							Console.WriteLine("Creating Texture " + name + " from " + source + (srgb ? " (sRGB)" : " (linear)"));
							textureIndex++;
							break;
						}
						default:
							Console.WriteLine("[Material] Unknown uniform type '" + type + "' in " + path);
							break;
					}
				}
			}

			UsingShader = InternalStorage.GetOrCreateShader(ShaderPath);
		}

		/// <summary>
		///     Creates an empty material for the given shader (uniforms can be added in code).
		/// </summary>
		public Material(ShaderProgram shader, string shaderPath = null)
		{
			UsingShader = shader;
			ShaderPath = shaderPath;
		}

		#endregion

		#region PublicMethods

		/// <summary>
		///     Independent copy sharing the shader and texture handles. Use for per-object parameter changes.
		/// </summary>
		public Material Clone()
		{
			Material copy = new Material(UsingShader, ShaderPath);
			copy.AssetPath = AssetPath;
			copy.textureIndex = textureIndex;
			foreach (Uniform uniform in Uniforms)
			{
				copy.Uniforms.Add(uniform.Clone());
			}

			return copy;
		}

		public void SetShader(ShaderProgram program)
		{
			UsingShader = program;
		}

		public void SetShader(string shaderPath)
		{
			UsingShader = InternalStorage.GetOrCreateShader(shaderPath);
		}

		/// <summary>
		///     Uploads this material's values. Conventional material parameters are reset to defaults first,
		///     because materials sharing a program would otherwise inherit values left by the previous draw.
		/// </summary>
		public void SetUniforms()
		{
			SetUniforms(UsingShader);
		}

		/// <summary>Uploads this material's values to the given program (e.g. the instanced variant of its shader).</summary>
		public void SetUniforms(ShaderProgram program)
		{
			ResetStandardUniforms(program);

			foreach (Uniform ob in Uniforms)
			{
				ob.Set(program);
			}
		}

		/// <summary>
		///     The material sets only the standard parameters (Color, Emissive, Roughness). GPU-driven draws read those
		///     from the material table, so such materials of one shader share a multi-draw call; a material with other
		///     uniforms gets a call of its own with them set.
		/// </summary>
		public bool HasOnlyStandardUniforms()
		{
			foreach (Uniform uniform in Uniforms)
			{
				if (!IsStandardUniform(uniform.Name))
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

		public static bool IsStandardUniform(string name)
		{
			return name == "Color" || name == "Emissive" || name == "Roughness";
		}

		/// <summary>
		///     Defaults for the conventional material parameters understood by engine passes
		///     (voxelization reads the same names). A material only has to specify what differs.
		/// </summary>
		public static void ResetStandardUniforms(ShaderProgram program)
		{
			program.SetVector4("Color", Vector4.One);
			program.SetVector4("Emissive", Vector4.Zero);
			program.SetFloat("Roughness", 0f);
		}

		// ---- Typed parameter access. Setters add the uniform when it does not exist yet. ----

		public void SetFloat(string name, float value)
		{
			Float uniform = Find<Float>(name);
			if (uniform != null) uniform.Value = value;
			else Uniforms.Add(new Float(name, value));
		}

		public void SetVector3(string name, Vector3 value)
		{
			Uniforms.Vector3 uniform = Find<Uniforms.Vector3>(name);
			if (uniform != null) uniform.Vector = value;
			else Uniforms.Add(new Uniforms.Vector3(name, value));
		}

		public void SetVector4(string name, Vector4 value)
		{
			Uniforms.Vector4 uniform = Find<Uniforms.Vector4>(name);
			if (uniform != null) uniform.Vector = value;
			else Uniforms.Add(new Uniforms.Vector4(name, value));
		}

		public void SetColor(string name, Color4 value)
		{
			SetVector4(name, new Vector4(value.R, value.G, value.B, value.A));
		}

		public bool TryGetFloat(string name, out float value)
		{
			Float uniform = Find<Float>(name);
			value = uniform?.Value ?? 0f;
			return uniform != null;
		}

		public bool TryGetVector4(string name, out Vector4 value)
		{
			Uniforms.Vector4 uniform = Find<Uniforms.Vector4>(name);
			value = uniform?.Vector ?? Vector4.Zero;
			return uniform != null;
		}

		/// <summary>
		///     GL handle of a sampler2D uniform, or 0 when the material has no such texture.
		/// </summary>
		public int GetTextureHandle(string name)
		{
			Texture2D texture = Find<Texture2D>(name);
			return texture?.Handle ?? 0;
		}

		/// <summary>
		///     True when one of this material's uniforms has a name the camera or lighting passes also set (legacy
		///     materials carry e.g. "lightColor"). Draws grouped by shader send the camera and lighting uniforms once
		///     per program; after such a material they must be sent again for the next object.
		/// </summary>
		public bool OverridesSharedUniforms()
		{
			foreach (Uniform uniform in Uniforms)
			{
				if (IsSharedUniform(uniform.Name))
				{
					return true;
				}
			}

			return false;
		}

		#endregion

		#region PrivateMethods

		// Uniform names owned by Camera.SetUniforms, Transform.SetModelMatrix and Lighting.SetUniforms.
		private static bool IsSharedUniform(string name)
		{
			switch (name)
			{
				case "projectionMatrix":
				case "viewMatrix":
				case "modelMatrix":
				case "lightPosition":
				case "CamPosition":
				case "lightColor":
				case "lightSpaceMatrix":
				case "ShadowMap":
				case "VoxelRadiance":
				case "IndirectDiffuse":
				case "IndirectSpecular":
					return true;
			}

			return name.StartsWith("LAmbient.", StringComparison.Ordinal) || name.StartsWith("LDirectional.", StringComparison.Ordinal)
				|| name.StartsWith("LSpecular.", StringComparison.Ordinal) || name.StartsWith("shadow", StringComparison.Ordinal)
				|| name.StartsWith("gi", StringComparison.Ordinal) || name.StartsWith("voxel", StringComparison.Ordinal)
				|| name.StartsWith("sdf", StringComparison.Ordinal) || name.StartsWith("GlobalSdf", StringComparison.Ordinal)
				|| name.StartsWith("rc", StringComparison.Ordinal);
		}

		private T Find<T>(string name)
			where T : Uniform
		{
			foreach (Uniform uniform in Uniforms)
			{
				if (uniform is T typed && uniform.Name == name)
				{
					return typed;
				}
			}

			return null;
		}

		// Data textures (normals, roughness, metalness, specular masks) must stay linear;
		// everything else is treated as sRGB color.
		private static bool IsColorTexture(string uniformName)
		{
			string n = uniformName.ToLowerInvariant();
			return !(n.Contains("normal") || n.Contains("bump") || n.Contains("spec") || n.Contains("rough") || n.Contains("metal") || n.Contains("mask") || n.Contains("height"));
		}

		#endregion
	}
}
