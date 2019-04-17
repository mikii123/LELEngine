using System;
using System.Collections.Generic;
using System.IO;
using LELEngine.Shaders.Uniforms;
using Vector4 = OpenTK.Vector4;

namespace LELEngine.Shaders
{
	public sealed class Material
	{
		#region PublicFields

		public ShaderProgram UsingShader { get; private set; }
		public string ShaderPath { get; }
		public List<Uniform> Uniforms = new List<Uniform>();

		#endregion

		#region PrivateFields

		private int index;

		#endregion

		#region Constructors

		public Material(string path)
		{
			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + "/Materials/" + path))
			{
				string line = "";
				line = sr.ReadLine();
				ShaderPath = line.Trim();
				while (!sr.EndOfStream)
				{
					line = sr.ReadLine().Trim();
					switch (line)
					{
						case "uniform":
							line = sr.ReadLine().Trim();
							switch (line)
							{
								case "mat4":
									Uniforms.Add(new Matrix4(sr.ReadLine().Trim()));
									Console.WriteLine("Added uniform to material " + path + " shader: " + ShaderPath);
									break;
								case "vec4":
									string Name = sr.ReadLine().Trim();
									string[] words = sr.ReadLine().Split(' ');
									Uniforms.Add
									(
										new Uniforms.Vector4(
											Name,
											new Vector4(
												float.Parse(words[0].Trim().Replace('.', ',')),
												float.Parse(words[1].Trim().Replace('.', ',')),
												float.Parse(words[2].Trim().Replace('.', ',')),
												float.Parse(words[3].Trim().Replace('.', ','))
											)
										));
									break;
								case "vec3":
									// TODO
									break;
								case "float":
									// TODO
									break;
								case "sampler2D":
									string name = sr.ReadLine().Trim();
									string source = sr.ReadLine().Trim();
									Uniforms.Add(new Texture2D(name, source, index));
									Console.WriteLine("Creating Texture " + name + " from " + source);
									index++;
									break;
							}

							break;
					}
				}
			}

			UsingShader = InternalStorage.GetOrCreateShader(ShaderPath);
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
	}
}
