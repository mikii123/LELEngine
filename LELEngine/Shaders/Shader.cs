using System;
using System.IO;
using LELEngine.Graphics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     The source of one stage of a program (complete: version line, defines, includes resolved). Stages are
	///     compiled when a <see cref="ShaderProgram" /> is made of them.
	/// </summary>
	public sealed class Shader
	{
		#region PublicFields

		public ShaderType Type { get; }
		public string Source { get; }

		/// <summary>What messages about the stage call it.</summary>
		public string DebugName { get; }

		#endregion

		#region Constructors

		/// <summary>A stage file: its first line is the stage tag (/////Vertex ...), the rest the code.</summary>
		public Shader(string path)
		{
			using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + path))
			{
				string tag = sr.ReadLine();
				Type = ParseTag(tag);
				Source = sr.ReadToEnd();
				DebugName = path;
			}
		}

		public Shader(string code, ShaderType type, string debugName = null)
		{
			Type = type;
			Source = code;
			DebugName = debugName ?? type.ToString();
		}

		#endregion

		#region PublicMethods

		/// <summary>Nothing to free: a stage is its source (kept for the code that paired every stage with a delete).</summary>
		public void Delete()
		{ }

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

		/// <summary>The device's name of a stage.</summary>
		public static ShaderStage StageOf(ShaderType type)
		{
			switch (type)
			{
				case ShaderType.FragmentShader: return ShaderStage.Fragment;
				case ShaderType.GeometryShader: return ShaderStage.Geometry;
				case ShaderType.ComputeShader: return ShaderStage.Compute;
				case ShaderType.TessControlShader: return ShaderStage.TessControl;
				case ShaderType.TessEvaluationShader: return ShaderStage.TessEvaluation;
				default: return ShaderStage.Vertex;
			}
		}

		public static ShaderType TypeOf(ShaderStage stage)
		{
			switch (stage)
			{
				case ShaderStage.Fragment: return ShaderType.FragmentShader;
				case ShaderStage.Geometry: return ShaderType.GeometryShader;
				case ShaderStage.Compute: return ShaderType.ComputeShader;
				case ShaderStage.TessControl: return ShaderType.TessControlShader;
				case ShaderStage.TessEvaluation: return ShaderType.TessEvaluationShader;
				default: return ShaderType.VertexShader;
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

		#endregion
	}
}
