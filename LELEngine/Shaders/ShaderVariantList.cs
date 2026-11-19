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
	///     The shader variants of a game build (<see cref="ShaderVariantList" />): every one, and the ones each build scene
	///     uses.
	/// </summary>
	public sealed class ShaderVariantSet
	{
		#region PublicFields

		/// <summary>Every variant of the build, each once.</summary>
		public List<ShaderVariant> Variants { get; } = new List<ShaderVariant>();

		/// <summary>
		///     Per build scene (asset path), the variants of the materials the scene uses, directly or through what it
		///     references (prefabs, ScriptableObjects, models), and the always included shaders; a subset of
		///     <see cref="Variants" />, in its order.
		/// </summary>
		public Dictionary<string, List<ShaderVariant>> Scenes { get; } = new Dictionary<string, List<ShaderVariant>>(StringComparer.OrdinalIgnoreCase);

		#endregion

		#region PublicMethods

		/// <summary>The variants of a build scene; null when the set has no list for it.</summary>
		public List<ShaderVariant> Of(string scene)
		{
			return scene != null && Scenes.TryGetValue(scene.Replace('\\', '/').TrimStart('/'), out List<ShaderVariant> variants) ? variants : null;
		}

		#endregion
	}

	/// <summary>
	///     The shader variants a game build's materials use, and the shaders the project always includes without keywords
	///     (<see cref="GraphicsSettings.AlwaysIncludedShaders" />) (<see cref="ShaderVariant" />): the build checks each one with
	///     the reference compiler (glslang, the editor's ShaderValidator; a variant that does not compile fails the build)
	///     and writes the list to Data/ShaderVariants.txt: a line per variant ("shader path|KEYWORD KEYWORD"), then a line
	///     per build scene naming the variants it uses ("scene|Scenes/Main.scene|0 3 4", the variant lines counted from 0;
	///     <see cref="ShaderVariantSet" />). The player compiles its start scene's while it loads, behind the splash screen
	///     (<see cref="Compile" />; the program binary cache makes that a load from disk after the first start); the rest
	///     compile when the game asks for them (<see cref="ShaderCompilation" />), or on first use. Variants a script makes
	///     at runtime (materials created or switched in code) compile on first use.
	/// </summary>
	public static class ShaderVariantList
	{
		#region PublicFields

		public const string FileName = "ShaderVariants.txt";

		#endregion

		#region PrivateFields

		private const string SceneTag = "scene";

		#endregion

		#region PublicMethods

		public static void Write(string file, ShaderVariantSet set)
		{
			var text = new StringBuilder();
			text.Append("# Shader variants the game's materials use, and its always included shaders (shader path|keywords), then the ones each build scene uses\n");
			text.Append("# (scene|scene path|the variant lines above, counted from 0). The player compiles its start scene's while it loads, the game the rest when it asks.\n");
			var index = new Dictionary<ShaderVariant, int>();
			foreach (ShaderVariant variant in set.Variants)
			{
				index.TryAdd(variant, index.Count);
				text.Append(variant.Shader).Append('|').Append(string.Join(" ", variant.Keywords)).Append('\n');
			}

			foreach (KeyValuePair<string, List<ShaderVariant>> scene in set.Scenes)
			{
				text.Append(SceneTag).Append('|').Append(scene.Key).Append('|');
				bool first = true;
				foreach (ShaderVariant variant in scene.Value)
				{
					if (index.TryGetValue(variant, out int line))
					{
						text.Append(first ? "" : " ").Append(line);
						first = false;
					}
				}

				text.Append('\n');
			}

			File.WriteAllText(file, text.ToString());
		}

		/// <summary>The variants of a list file (empty when there is no file).</summary>
		public static ShaderVariantSet Read(string file)
		{
			var result = new ShaderVariantSet();
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

				// A variant line has one separator (keywords hold none), a scene line three fields.
				string[] fields = line.Split('|');
				if (fields.Length == 3 && fields[0] == SceneTag)
				{
					var variants = new List<ShaderVariant>();
					foreach (string number in fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries))
					{
						if (int.TryParse(number, out int at) && at >= 0 && at < result.Variants.Count)
						{
							variants.Add(result.Variants[at]);
						}
					}

					result.Scenes[fields[1].Trim().Replace('\\', '/').TrimStart('/')] = variants;
					continue;
				}

				string shader = fields[0];
				string keywords = fields.Length > 1 ? fields[1] : "";
				result.Variants.Add(new ShaderVariant(shader.Trim(), keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
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

			// Every variant starts compiling first (the driver compiles them together with parallel shader compile), then
			// each is waited for.
			var programs = new List<(ShaderVariant variant, ShaderProgram program)>();
			foreach (ShaderVariant variant in variants)
			{
				try
				{
					ShaderProgram program = variant.GetProgram();
					program.StartBuild();
					programs.Add((variant, program));
				}
				catch (Exception e)
				{
					failures.Add(variant + ": " + e.Message);
				}
			}

			foreach ((ShaderVariant variant, ShaderProgram program) in programs)
			{
				try
				{
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
				// The programs of the passes the shader draws itself (its own shadow caster, geometry prepass) with it.
				compiled += program.BuildPassVariants();
				if (gpuDriven && program.SupportsInstancing)
				{
					ShaderProgram instanced = program.GetInstancedVariant();
					if (instanced != null)
					{
						compiled++;
						compiled += instanced.BuildPassVariants();
					}
				}
			}

			milliseconds = watch.ElapsedMilliseconds;
			return failures;
		}

		#endregion
	}
}
