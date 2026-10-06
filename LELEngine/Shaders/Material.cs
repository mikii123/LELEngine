using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Shaders.Uniforms;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Shader + uniform values loaded from a .material file in the game's Materials directory.
	///     Format: first line is the shader file, then repeated blocks of
	///     "uniform" / type / name / value (value line only for non-matrix types).
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
							Uniforms.Add(new Matrix4(name));
							break;
						case "vec4":
						{
							string[] words = sr.ReadLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
							Uniforms.Add(new Uniforms.Vector4(name, new OpenTK.Mathematics.Vector4(
								Extensions.ParseFloat(words[0]),
								Extensions.ParseFloat(words[1]),
								Extensions.ParseFloat(words[2]),
								Extensions.ParseFloat(words[3]))));
							break;
						}
						case "vec3":
						{
							string[] words = sr.ReadLine().Split(' ', StringSplitOptions.RemoveEmptyEntries);
							Uniforms.Add(new Uniforms.Vector3(name, new OpenTK.Mathematics.Vector3(
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

		public void SetShader(ShaderProgram program)
		{
			UsingShader = program;
		}

		public void SetShader(string shaderPath)
		{
			UsingShader = InternalStorage.GetOrCreateShader(shaderPath);
		}

		public void SetUniforms()
		{
			foreach (Uniform ob in Uniforms)
			{
				ob.Set(UsingShader);
			}
		}

		#endregion

		#region PrivateMethods

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
