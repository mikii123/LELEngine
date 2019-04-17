using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	public sealed class ShaderProgram
	{
		#region PrivateFields

		private readonly int handle;

		#endregion

		#region Constructors

		public ShaderProgram(params Shader[] shaders)
		{
			// create program object
			handle = GL.CreateProgram();

			// assign all shaders
			foreach (Shader shader in shaders)
			{
				GL.AttachShader(handle, shader.Handle);
			}

			// link program (effectively compiles it)
			GL.LinkProgram(handle);

			// detach shaders
			foreach (Shader shader in shaders)
			{
				GL.DetachShader(handle, shader.Handle);
			}
		}

		public ShaderProgram(string path)
		{
			List<Shader> shaders = LoadShaderFromFile(path);

			// create program object
			handle = GL.CreateProgram();

			// assign all shaders
			foreach (Shader shader in shaders)
			{
				GL.AttachShader(handle, shader.Handle);
			}

			// link program (effectively compiles it)
			GL.LinkProgram(handle);

			// detach shaders
			foreach (Shader shader in shaders)
			{
				GL.DetachShader(handle, shader.Handle);
			}
		}

		#endregion

		#region PublicMethods

		public void Use()
		{
			// activate this program to be used
			GL.UseProgram(handle);
		}

		public int GetAttributeLocation(string name)
		{
			// get the location of a vertex attribute
			return GL.GetAttribLocation(handle, name);
		}

		public int GetUniformLocation(string name)
		{
			// get the location of a uniform variable
			return GL.GetUniformLocation(handle, name);
		}

		#endregion

		#region PrivateMethods

		private void CheckCode(string code, string path)
		{
			if (code.Length < 10)
			{
				Console.WriteLine("Warning: Shader " + path + " is shorter than 10 characters!");
			}
		}

		private List<Shader> LoadShaderFromFile(string path)
		{
			var shaders = new List<Shader>();

			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + "/Shaders/" + path))
			{
				ShaderType type;
				string code = "";
				string line = "";
				while (!sr.EndOfStream)
				{
					line = sr.ReadLine();
					switch (line)
					{
						case "/////Vertex":
							type = ShaderType.VertexShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						case "/////Fragment":
							type = ShaderType.FragmentShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						case "/////Geometry":
							type = ShaderType.GeometryShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						case "/////Compute":
							type = ShaderType.ComputeShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						case "/////TessControl":
							type = ShaderType.TessControlShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						case "/////TessEvaluation":
							type = ShaderType.TessEvaluationShader;
							shaders.Add(new Shader(code, type));
							CheckCode(code, path);
							line = "";
							code = "";
							break;
						default:
							code += line + "\n";
							break;
					}
				}
			}

			return shaders;
		}

		#endregion
	}
}
