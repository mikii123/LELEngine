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
	public sealed class Material
	{
		#region PublicFields

		public ShaderProgram UsingShader { get; private set; }
		public string ShaderPath { get; }
		public List<Uniform> Uniforms = new List<Uniform>();

		#endregion

		#region PrivateFields

		private int textureIndex;

		#endregion

		#region Constructors

		public Material(string path)
		{
			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + "/Materials/" + path))
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
			ResetStandardUniforms(UsingShader);

			foreach (Uniform ob in Uniforms)
			{
				ob.Set(UsingShader);
			}
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

		#endregion

		#region PrivateMethods

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
