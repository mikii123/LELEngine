using System;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;

namespace LELEngine.Shaders
{
	public enum MaterialPropertyType
	{
		Float,
		Int,
		Vector,
		Color,
		Texture
	}

	/// <summary>A named parameter value of a <see cref="Material" /> (saved with it; a shader uniform at draw time).</summary>
	[Serializable]
	public abstract class MaterialProperty
	{
		#region PublicFields

		public string name;
		public abstract MaterialPropertyType Type { get; }

		#endregion

		#region PublicMethods

		public abstract MaterialProperty Clone();

		/// <summary>Uploads the value to the program's uniform of this name (no-op when the shader has none).</summary>
		internal abstract void Apply(ShaderProgram program, ref int textureUnit);

		#endregion
	}

	[Serializable]
	public sealed class FloatProperty : MaterialProperty
	{
		public float value;

		public override MaterialPropertyType Type => MaterialPropertyType.Float;

		public override MaterialProperty Clone()
		{
			return new FloatProperty { name = name, value = value };
		}

		internal override void Apply(ShaderProgram program, ref int textureUnit)
		{
			int location = program.GetUniformLocation(name);
			if (location < 0)
			{
				return;
			}

			ActiveUniformType type = program.GetUniformType(name);
			if (type == ActiveUniformType.Int || type == ActiveUniformType.Bool)
			{
				GL.Uniform1(location, (int)value);
			}
			else
			{
				GL.Uniform1(location, value);
			}
		}
	}

	[Serializable]
	public sealed class IntProperty : MaterialProperty
	{
		public int value;

		public override MaterialPropertyType Type => MaterialPropertyType.Int;

		public override MaterialProperty Clone()
		{
			return new IntProperty { name = name, value = value };
		}

		internal override void Apply(ShaderProgram program, ref int textureUnit)
		{
			int location = program.GetUniformLocation(name);
			if (location < 0)
			{
				return;
			}

			ActiveUniformType type = program.GetUniformType(name);
			if (type == ActiveUniformType.Float)
			{
				GL.Uniform1(location, (float)value);
			}
			else
			{
				GL.Uniform1(location, value);
			}
		}
	}

	[Serializable]
	public sealed class VectorProperty : MaterialProperty
	{
		public Vector4 value;

		public override MaterialPropertyType Type => MaterialPropertyType.Vector;

		public override MaterialProperty Clone()
		{
			return new VectorProperty { name = name, value = value };
		}

		internal override void Apply(ShaderProgram program, ref int textureUnit)
		{
			UploadVector(program, name, value);
		}

		/// <summary>Uploads as many components as the uniform has (vec2 / vec3 / vec4, float, ivec).</summary>
		internal static void UploadVector(ShaderProgram program, string name, Vector4 value)
		{
			int location = program.GetUniformLocation(name);
			if (location < 0)
			{
				return;
			}

			switch (program.GetUniformType(name))
			{
				case ActiveUniformType.Float: GL.Uniform1(location, value.X); break;
				case ActiveUniformType.FloatVec2: GL.Uniform2(location, value.X, value.Y); break;
				case ActiveUniformType.FloatVec3: GL.Uniform3(location, value.X, value.Y, value.Z); break;
				case ActiveUniformType.Int: GL.Uniform1(location, (int)value.X); break;
				case ActiveUniformType.IntVec2: GL.Uniform2(location, (int)value.X, (int)value.Y); break;
				case ActiveUniformType.IntVec3: GL.Uniform3(location, (int)value.X, (int)value.Y, (int)value.Z); break;
				case ActiveUniformType.IntVec4: GL.Uniform4(location, (int)value.X, (int)value.Y, (int)value.Z, (int)value.W); break;
				default: GL.Uniform4(location, value); break;
			}
		}
	}

	[Serializable]
	public sealed class ColorProperty : MaterialProperty
	{
		public Color4 value = Color4.White;

		public override MaterialPropertyType Type => MaterialPropertyType.Color;

		public override MaterialProperty Clone()
		{
			return new ColorProperty { name = name, value = value };
		}

		internal override void Apply(ShaderProgram program, ref int textureUnit)
		{
			VectorProperty.UploadVector(program, name, new Vector4(value.R, value.G, value.B, value.A));
		}
	}

	[Serializable]
	public sealed class TextureProperty : MaterialProperty
	{
		public Texture value;

		public override MaterialPropertyType Type => MaterialPropertyType.Texture;

		public override MaterialProperty Clone()
		{
			return new TextureProperty { name = name, value = value };
		}

		internal override void Apply(ShaderProgram program, ref int textureUnit)
		{
			int location = program.GetUniformLocation(name);
			if (location < 0)
			{
				return;
			}

			// textureUnit counts the material's textures; past the first 8 they go above the engine's units.
			int unit = Material.TextureUnitAt(textureUnit);
			if (unit < 0)
			{
				Debug.LogWarning($"[Material] More than {Material.MaxTextures} textures; '{name}' is not bound");
				return;
			}

			ActiveUniformType samplerType = program.GetUniformType(name);
			Texture texture = value;
			if (texture != null && !Matches(texture, samplerType))
			{
				texture = null;
			}

			TextureTarget target;
			int handle;
			if (texture != null && texture.Handle != 0)
			{
				target = texture.Target;
				handle = texture.Handle;
			}
			else
			{
				// Nothing (or the wrong kind) assigned: a neutral default of the sampler's kind.
				Texture fallback = DefaultFor(samplerType, name);
				target = fallback.Target;
				handle = fallback.Handle;
			}

			GL.ActiveTexture(TextureUnit.Texture0 + unit);
			GL.BindTexture(target, handle);
			GL.Uniform1(location, unit);
			textureUnit++;
		}

		/// <summary>The texture kind a sampler uniform accepts.</summary>
		internal static bool Matches(Texture texture, ActiveUniformType samplerType)
		{
			switch (samplerType)
			{
				case ActiveUniformType.SamplerCube:
				case ActiveUniformType.SamplerCubeShadow:
					return texture is Cubemap;
				case ActiveUniformType.Sampler3D:
					return texture is Texture3D;
				case ActiveUniformType.Sampler2D:
				case ActiveUniformType.Sampler2DShadow:
					return texture is Texture2D;
				default:
					return true;
			}
		}

		private static Cubemap blackCube;
		private static Texture3D whiteVolume;

		private static Texture DefaultFor(ActiveUniformType samplerType, string uniformName)
		{
			switch (samplerType)
			{
				case ActiveUniformType.SamplerCube:
				case ActiveUniformType.SamplerCubeShadow:
					return blackCube ??= new Cubemap(1, "black", false);
				case ActiveUniformType.Sampler3D:
					return whiteVolume ??= new Texture3D(1, 1, 1, new byte[] { 255, 255, 255, 255 }, "white");
				default:
					return Material.IsNormalMapName(uniformName) ? Texture2D.normalTexture : Texture2D.whiteTexture;
			}
		}
	}
}
