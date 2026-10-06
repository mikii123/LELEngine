using System;
using System.IO;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Single compiled shader stage.
	/// </summary>
	public sealed class Shader
	{
		#region PublicFields

		public int Handle { get; }
		public ShaderType Type { get; }

		#endregion

		#region Constructors

		public Shader(string path)
		{
			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + path))
			{
				string tag = sr.ReadLine();
				Type = ParseTag(tag);
				string code = sr.ReadToEnd();

				Handle = Compile(code, Type, path);
			}
		}

		public Shader(string code, ShaderType type, string debugName = null)
		{
			Type = type;
			Handle = Compile(code, type, debugName ?? type.ToString());
		}

		#endregion

		#region PublicMethods

		public void Delete()
		{
			GL.DeleteShader(Handle);
		}

		/// <summary>
		///     Parses the "/////Vertex" style section tag used by .shader files.
		/// </summary>
		public static bool TryParseTag(string line, out ShaderType type)
		{
			switch (line)
			{
				case "/////Vertex":
					type = ShaderType.VertexShader;
					return true;
				case "/////Fragment":
					type = ShaderType.FragmentShader;
					return true;
				case "/////Geometry":
					type = ShaderType.GeometryShader;
					return true;
				case "/////Compute":
					type = ShaderType.ComputeShader;
					return true;
				case "/////TessControl":
					type = ShaderType.TessControlShader;
					return true;
				case "/////TessEvaluation":
					type = ShaderType.TessEvaluationShader;
					return true;
				default:
					type = ShaderType.VertexShader;
					return false;
			}
		}

		#endregion

		#region PrivateMethods

		private static ShaderType ParseTag(string tag)
		{
			ShaderType type;
			TryParseTag(tag, out type);
			return type;
		}

		private static int Compile(string code, ShaderType type, string debugName)
		{
			int handle = GL.CreateShader(type);
			GL.ShaderSource(handle, code);
			GL.CompileShader(handle);

			int status;
			GL.GetShader(handle, ShaderParameter.CompileStatus, out status);
			string log = GL.GetShaderInfoLog(handle);
			if (status == 0)
			{
				Console.WriteLine($"[Shader] Compile error in {debugName} ({type}):\n{log}");
			}
			else if (!string.IsNullOrWhiteSpace(log))
			{
				Console.WriteLine($"[Shader] {debugName} ({type}):\n{log}");
			}

			return handle;
		}

		#endregion
	}
}
