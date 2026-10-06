using System;
using System.Collections.Generic;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	/// <summary>
	///     Linked GPU program. Attribute locations follow <see cref="VertexLayout" />,
	///     uniform locations are cached by name.
	/// </summary>
	public sealed class ShaderProgram
	{
		#region PublicFields

		public int Handle => handle;
		public string Name { get; }
		public bool IsLinked { get; private set; }

		#endregion

		#region PrivateFields

		private readonly int handle;
		private readonly Dictionary<string, int> uniformCache = new Dictionary<string, int>();

		#endregion

		#region Constructors

		public ShaderProgram(params Shader[] shaders)
			: this("<inline>", shaders)
		{ }

		public ShaderProgram(string name, params Shader[] shaders)
		{
			Name = name;
			handle = GL.CreateProgram();
			Link(shaders);
		}

		/// <summary>
		///     Loads a multi-stage .shader file from the game's Shaders directory.
		/// </summary>
		public ShaderProgram(string path)
		{
			Name = path;
			List<Shader> shaders = LoadShaderFromFile(path);

			handle = GL.CreateProgram();
			Link(shaders.ToArray());

			foreach (Shader shader in shaders)
			{
				shader.Delete();
			}
		}

		#endregion

		#region PublicMethods

		public void Use()
		{
			GL.UseProgram(handle);
		}

		public void Delete()
		{
			GL.DeleteProgram(handle);
		}

		public int GetAttributeLocation(string name)
		{
			return GL.GetAttribLocation(handle, name);
		}

		public int GetUniformLocation(string name)
		{
			int location;
			if (!uniformCache.TryGetValue(name, out location))
			{
				location = GL.GetUniformLocation(handle, name);
				uniformCache[name] = location;
			}

			return location;
		}

		public bool HasUniform(string name)
		{
			return GetUniformLocation(name) >= 0;
		}

		// Convenience setters. Every one of them is a no-op when the uniform does not exist
		// (or was optimized away), so passes can set shared uniforms blindly.

		public void SetInt(string name, int value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform1(loc, value);
		}

		public void SetFloat(string name, float value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform1(loc, value);
		}

		public void SetVector2(string name, Vector2 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform2(loc, value);
		}

		public void SetVector3(string name, Vector3 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform3(loc, value);
		}

		public void SetVector4(string name, Vector4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform4(loc, value);
		}

		public void SetColor(string name, Color4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.Uniform4(loc, value);
		}

		public void SetMatrix4(string name, Matrix4 value)
		{
			int loc = GetUniformLocation(name);
			if (loc >= 0) GL.UniformMatrix4(loc, false, ref value);
		}

		/// <summary>
		///     Binds a texture to the given unit and points the sampler uniform at it.
		/// </summary>
		public void SetTexture(string name, TextureTarget target, int textureHandle, int unit)
		{
			int loc = GetUniformLocation(name);
			if (loc < 0) return;

			GL.ActiveTexture(TextureUnit.Texture0 + unit);
			GL.BindTexture(target, textureHandle);
			GL.Uniform1(loc, unit);
		}

		#endregion

		#region PrivateMethods

		private void Link(Shader[] shaders)
		{
			foreach (Shader shader in shaders)
			{
				GL.AttachShader(handle, shader.Handle);
			}

			// Fixed attribute slots - must happen before linking.
			VertexLayout.BindStandardLocations(handle);

			GL.LinkProgram(handle);

			int status;
			GL.GetProgram(handle, GetProgramParameterName.LinkStatus, out status);
			IsLinked = status != 0;
			string log = GL.GetProgramInfoLog(handle);
			if (!IsLinked)
			{
				Console.WriteLine($"[Shader] Link error in {Name}:\n{log}");
			}
			else if (!string.IsNullOrWhiteSpace(log))
			{
				Console.WriteLine($"[Shader] {Name}:\n{log}");
			}

			foreach (Shader shader in shaders)
			{
				GL.DetachShader(handle, shader.Handle);
			}
		}

		private static List<Shader> LoadShaderFromFile(string path)
		{
			var shaders = new List<Shader>();
			string fullPath = Path.Combine(Directory.GetCurrentDirectory(), "Shaders", path);

			using (StreamReader sr = new StreamReader(fullPath))
			{
				string code = "";
				while (!sr.EndOfStream)
				{
					string line = sr.ReadLine();
					ShaderType type;
					if (Shader.TryParseTag(line, out type))
					{
						if (code.Trim().Length < 10)
						{
							Console.WriteLine("[Shader] Warning: stage " + type + " in " + path + " is shorter than 10 characters!");
						}

						shaders.Add(new Shader(code, type, path));
						code = "";
					}
					else
					{
						code += line + "\n";
					}
				}
			}

			return shaders;
		}

		#endregion
	}
}
