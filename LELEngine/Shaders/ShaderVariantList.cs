using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace LELEngine.Shaders
{
	/// <summary>One shader variant: the shader's asset path and the keywords it is compiled with.</summary>
	public readonly struct ShaderVariant : IEquatable<ShaderVariant>
	{
		#region Constructors

		public ShaderVariant(string shader, IEnumerable<string> keywords)
		{
			Shader = shader;
			var sorted = new List<string>(keywords ?? Array.Empty<string>());
			sorted.Sort(StringComparer.Ordinal);
			Keywords = sorted.ToArray();
		}

		#endregion

		#region PublicFields

		public string Shader { get; }

		/// <summary>Sorted.</summary>
		public string[] Keywords { get; }

		#endregion

		#region PublicMethods

		/// <summary>The variant a material draws with (its shader and the keywords of its values).</summary>
		public static ShaderVariant Of(Material material)
		{
			return new ShaderVariant(material.UsingShader?.AssetPath, material.Keywords);
		}

		/// <summary>The program of the variant (made on first use, compiled where the GL context is).</summary>
		public ShaderProgram GetProgram()
		{
			return InternalStorage.GetOrCreateShader(Shader).GetVariant(Keywords);
		}

		public bool Equals(ShaderVariant other)
		{
			return string.Equals(Shader, other.Shader, StringComparison.OrdinalIgnoreCase) && ToString() == other.ToString();
		}

		public override bool Equals(object obj)
		{
			return obj is ShaderVariant other && Equals(other);
		}

		public override int GetHashCode()
		{
			return StringComparer.OrdinalIgnoreCase.GetHashCode(ToString());
		}

		/// <summary>"Engine/ParticlesUnlit.shader BLENDMODE_ADDITIVE RENDERFACE_BOTH" (a line of the list file without the separator).</summary>
		public override string ToString()
		{
			return Keywords.Length == 0 ? Shader ?? "" : Shader + " " + string.Join(" ", Keywords);
		}

		#endregion
	}

	/// <summary>
	///     The shader variants a game build's materials use (<see cref="ShaderVariant" />): the build checks each one with
	///     the reference compiler (glslang, the editor's ShaderValidator; a variant that does not compile fails the build)
	///     and writes the list to Data/ShaderVariants.txt (a line per variant: "shader path|KEYWORD KEYWORD"); the player
	///     compiles them while it loads, before the first frame (<see cref="Compile" />; the program binary cache makes
	///     that a load from disk after the first start), so none compiles in the middle of the game. Variants a script
	///     makes at runtime (materials created or switched in code) still compile on first use.
	/// </summary>
	public static class ShaderVariantList
	{
		#region PublicFields

		public const string FileName = "ShaderVariants.txt";

		#endregion

		#region PublicMethods

		public static void Write(string file, IEnumerable<ShaderVariant> variants)
		{
			var text = new StringBuilder();
			text.Append("# Shader variants the game's materials use (shader path|keywords); the player compiles them while it loads.\n");
			foreach (ShaderVariant variant in variants)
			{
				text.Append(variant.Shader).Append('|').Append(string.Join(" ", variant.Keywords)).Append('\n');
			}

			File.WriteAllText(file, text.ToString());
		}

		/// <summary>The variants of a list file (none when there is no file).</summary>
		public static List<ShaderVariant> Read(string file)
		{
			var result = new List<ShaderVariant>();
			if (!File.Exists(file))
			{
				return result;
			}

			foreach (string line in File.ReadAllLines(file))
			{
				if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
				{
					continue;
				}

				int separator = line.IndexOf('|');
				string shader = separator < 0 ? line : line.Substring(0, separator);
				string keywords = separator < 0 ? "" : line.Substring(separator + 1);
				result.Add(new ShaderVariant(shader.Trim(), keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
			}

			return result;
		}

		/// <summary>
		///     Compiles the variants on this thread (it must hold the GL context), with their instanced variants when
		///     <paramref name="gpuDriven" />. Returns the failures, one message each with the compiler's output.
		/// </summary>
		public static List<string> Compile(IEnumerable<ShaderVariant> variants, bool gpuDriven, out int compiled, out long milliseconds)
		{
			var failures = new List<string>();
			compiled = 0;
			var watch = Stopwatch.StartNew();
			foreach (ShaderVariant variant in variants)
			{
				ShaderProgram program;
				try
				{
					program = variant.GetProgram();
					program.EnsureBuilt();
				}
				catch (Exception e)
				{
					failures.Add(variant + ": " + e.Message);
					continue;
				}

				if (!program.IsLinked)
				{
					failures.Add(variant + " does not compile:\n" + program.BuildLog.TrimEnd());
					continue;
				}

				compiled++;
				if (gpuDriven && program.SupportsInstancing)
				{
					ShaderProgram instanced = program.GetInstancedVariant();
					if (instanced != null)
					{
						compiled++;
					}
				}
			}

			milliseconds = watch.ElapsedMilliseconds;
			return failures;
		}

		#endregion
	}
}
