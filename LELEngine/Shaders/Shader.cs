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

		public ShaderStage Type { get; }
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

		public Shader(string code, ShaderStage type, string debugName = null)
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

		public static bool TryParseTag(string line, out ShaderStage type)
		{
			switch (line)
			{
				case "/////Vertex":
					type = ShaderStage.Vertex;
					return true;
				case "/////Fragment":
					type = ShaderStage.Fragment;
					return true;
				case "/////Geometry":
					type = ShaderStage.Geometry;
					return true;
				case "/////Compute":
					type = ShaderStage.Compute;
					return true;
				case "/////TessControl":
					type = ShaderStage.TessControl;
					return true;
				case "/////TessEvaluation":
					type = ShaderStage.TessEvaluation;
					return true;
				default:
					type = ShaderStage.Vertex;
					return false;
			}
		}

		#endregion

		#region PrivateMethods

		private static ShaderStage ParseTag(string tag)
		{
			ShaderStage type;
			TryParseTag(tag, out type);
			return type;
		}

		#endregion
	}
}
