using System;
using LELEngine.Graphics;
using LELEngine.Mathematics;

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
			UploadFloat(program, name, value);
		}

		/// <summary>Uploads a number to a float uniform, or truncated to an int / bool one.</summary>
		internal static void UploadFloat(ShaderProgram program, string name, float value)
		{
			int slot = program.GetSlot(name);
			if (slot < 0)
			{
				return;
			}

			UniformType type = program.GetUniformType(name);
			if (type == UniformType.Int || type == UniformType.Bool)
			{
				program.Gpu.SetInt(slot, (int)value);
			}
			else
			{
				program.Gpu.SetFloat(slot, value);
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
			int slot = program.GetSlot(name);
			if (slot < 0)
			{
				return;
			}

			UniformType type = program.GetUniformType(name);
			if (type == UniformType.Float)
			{
				program.Gpu.SetFloat(slot, value);
			}
			else
			{
				program.Gpu.SetInt(slot, value);
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
			int slot = program.GetSlot(name);
			if (slot < 0)
			{
				return;
			}

			GpuProgram gpu = program.Gpu;
			switch (program.GetUniformType(name))
			{
				case UniformType.Float: gpu.SetFloat(slot, value.X); break;
				case UniformType.Vector2: gpu.SetVector2(slot, new Vector2(value.X, value.Y)); break;
				case UniformType.Vector3: gpu.SetVector3(slot, new Vector3(value.X, value.Y, value.Z)); break;
				case UniformType.Int: gpu.SetInt(slot, (int)value.X); break;
				case UniformType.IntVector2: gpu.SetInt2(slot, (int)value.X, (int)value.Y); break;
				case UniformType.IntVector3: gpu.SetInt3(slot, (int)value.X, (int)value.Y, (int)value.Z); break;
				case UniformType.IntVector4: gpu.SetInt4(slot, (int)value.X, (int)value.Y, (int)value.Z, (int)value.W); break;
				default: gpu.SetVector4(slot, value); break;
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
			Bind(program, name, value, ref textureUnit);
		}

		/// <summary>
		///     Gives a sampler its texture (or the sampler kind's neutral default when it is null or of the wrong kind);
		///     <paramref name="textureUnit" /> counts the material's textures.
		/// </summary>
		internal static void Bind(ShaderProgram program, string name, Texture value, ref int textureUnit)
		{
			int slot = program.GetSlot(name);
			if (slot < 0)
			{
				return;
			}

			if (textureUnit >= Material.MaxTextures)
			{
				Debug.LogWarning($"[Material] More than {Material.MaxTextures} textures; '{name}' is not bound");
				return;
			}

			UniformType samplerType = program.GetUniformType(name);
			Texture texture = value;
			if (texture != null && !Matches(texture, samplerType))
			{
				texture = null;
			}

			GpuTexture gpu = texture?.Gpu;
			if (gpu == null)
			{
				// Nothing (or the wrong kind) assigned: a neutral default of the sampler's kind.
				gpu = DefaultFor(samplerType, name).Gpu;
			}

			program.Gpu.SetTexture(slot, gpu);
			textureUnit++;
		}

		/// <summary>The texture kind a sampler uniform accepts.</summary>
		internal static bool Matches(Texture texture, UniformType samplerType)
		{
			switch (samplerType)
			{
				case UniformType.SamplerCube:
				case UniformType.SamplerCubeShadow:
					return texture is Cubemap;
				case UniformType.Sampler3D:
					return texture is Texture3D;
				case UniformType.Sampler2D:
				case UniformType.Sampler2DShadow:
					return texture is Texture2D;
				default:
					return true;
			}
		}

		private static Cubemap blackCube;
		private static Texture3D whiteVolume;

		private static Texture DefaultFor(UniformType samplerType, string uniformName)
		{
			switch (samplerType)
			{
				case UniformType.SamplerCube:
				case UniformType.SamplerCubeShadow:
					return blackCube ??= new Cubemap(1, "black", false);
				case UniformType.Sampler3D:
					return whiteVolume ??= new Texture3D(1, 1, 1, new byte[] { 255, 255, 255, 255 }, "white");
				default:
					return Material.IsNormalMapName(uniformName) ? Texture2D.normalTexture : Texture2D.whiteTexture;
			}
		}
	}
}
