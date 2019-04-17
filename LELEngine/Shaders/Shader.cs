using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	public sealed class Shader
	{
		#region PublicFields

		public int Handle { get; }

		#endregion

		#region PrivateFields

		#endregion

		#region Constructors

		public Shader(string path)
		{
			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + path))
			{
				string code = "";
				string tag = sr.ReadLine();

				ShaderType type = ShaderType.VertexShader;
				switch (tag)
				{
					case "/////Vertex":
						type = ShaderType.VertexShader;
						break;
					case "/////Fragment":
						type = ShaderType.FragmentShader;
						break;
					case "/////Geometry":
						type = ShaderType.GeometryShader;
						break;
					case "/////Compute":
						type = ShaderType.ComputeShader;
						break;
					case "/////TessControl":
						type = ShaderType.TessControlShader;
						break;
					case "/////TessEvaluation":
						type = ShaderType.TessEvaluationShader;
						break;
				}

				code = sr.ReadToEnd();

				// create shader object
				Handle = GL.CreateShader(type);

				// set source and compile shader
				GL.ShaderSource(Handle, code);
				GL.CompileShader(Handle);
			}
		}

		public Shader(string code, ShaderType type)
		{
			// create shader object
			Handle = GL.CreateShader(type);

			// set source and compile shader
			GL.ShaderSource(Handle, code);
			GL.CompileShader(Handle);
			Console.WriteLine(GL.GetShaderInfoLog(Handle));
		}

		#endregion
	}
}
