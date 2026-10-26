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

		/// <summary>The driver's compile log of the last compiled stage (empty when clean).</summary>
		public static string LastLog { get; private set; } = "";

		/// <summary>The last compiled stage compiled without errors.</summary>
		public static bool LastCompiled { get; private set; } = true;

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
			int handle = Submit(code, type);
			LastCompiled = Check(handle, type, debugName, out string log);
			LastLog = log;
			return handle;
		}

		#endregion

		#region InternalMethods

		/// <summary>
		///     A stage object with its source handed to the compiler, without waiting for the result (with parallel shader
		///     compile the driver's threads work on it until <see cref="Check" />).
		/// </summary>
		internal static int Submit(string code, ShaderType type)
		{
			int handle = GL.CreateShader(type);
			GL.ShaderSource(handle, code);
			GL.CompileShader(handle);
			return handle;
		}

		/// <summary>Waits for a submitted stage: true when it compiled; its log (empty when clean) is printed as Compile prints it.</summary>
		internal static bool Check(int handle, ShaderType type, string debugName, out string log)
		{
			GL.GetShader(handle, ShaderParameter.CompileStatus, out int status);
			log = GL.GetShaderInfoLog(handle) ?? "";
			if (status == 0)
			{
				Console.WriteLine($"[Shader] Compile error in {debugName} ({type}):\n{log}");
			}
			else if (!string.IsNullOrWhiteSpace(log))
			{
				Console.WriteLine($"[Shader] {debugName} ({type}):\n{log}");
			}

			return status != 0;
		}

		#endregion
	}
}
