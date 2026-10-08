using System;
using System.IO;
using LELEngine.Rendering;
using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Texture2D : Uniform
	{
		#region PublicFields

		public int Handle { get; }
		public int Index;

		/// <summary>Anisotropic filtering level applied to loaded textures when the context supports it.</summary>
		public static float MaxAnisotropy = 8f;

		#endregion

		#region Constructors

		/// <param name="srgb">True for color data (albedo). False for normal/roughness/metalness/data maps.</param>
		public Texture2D(string name, string source, int index, bool srgb = true)
		{
			Index = index;
			Name = name;
			string assetPath = AssetDatabase.Resolve(source, "Textures") ?? "Textures/" + source;
			Handle = LoadImage(AssetDatabase.ToAbsolute(assetPath), srgb);
		}

		public Texture2D()
		{ }

		#endregion

		#region PublicMethods

		public override void Set(ShaderProgram program)
		{
			GL.ActiveTexture(TextureUnit.Texture0 + Index);
			GL.BindTexture(TextureTarget.Texture2D, Handle);
			GL.Uniform1(program.GetUniformLocation(Name), Index);
		}

		#endregion

		#region PrivateMethods

		private int LoadImage(string path, bool srgb)
		{
			StbImage.stbi_set_flip_vertically_on_load(1);

			ImageResult image;
			using (FileStream stream = File.OpenRead(path))
			{
				image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
			}

			int texID = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texID);

			// Color textures are authored in sRGB; storing them as sRGB makes sampling return linear values,
			// which the HDR pipeline expects (gamma is applied once, in the post-process pass).
			GL.TexImage2D(
				TextureTarget.Texture2D,
				0,
				srgb ? PixelInternalFormat.Srgb8Alpha8 : PixelInternalFormat.Rgba8,
				image.Width,
				image.Height,
				0,
				PixelFormat.Rgba,
				PixelType.UnsignedByte,
				image.Data);

			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
			GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
			if (GLCapabilities.AnisotropicFiltering)
			{
				// GL_TEXTURE_MAX_ANISOTROPY (core in 4.6, same value as the EXT extension).
				GL.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, Math.Min(MaxAnisotropy, GLCapabilities.MaxAnisotropy));
			}
			GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

			return texID;
		}

		#endregion
	}
}
