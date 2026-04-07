using System.IO;
using OpenTK.Graphics.OpenGL4;
using StbImageSharp;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Texture2D : Uniform
	{
		#region PublicFields

		public int Handle { get; }
		public int Index;

		#endregion

		#region Constructors

		public Texture2D(string name, string source, int index)
		{
			Index = index;
			Name = name;
			string path = Path.Combine(Directory.GetCurrentDirectory(), "Textures", source);
			Handle = LoadImage(path);
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

		private int LoadImage(string path)
		{
			StbImage.stbi_set_flip_vertically_on_load(1);

			ImageResult image;
			using (FileStream stream = File.OpenRead(path))
			{
				image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
			}

			int texID = GL.GenTexture();
			GL.BindTexture(TextureTarget.Texture2D, texID);

			GL.TexImage2D(
				TextureTarget.Texture2D,
				0,
				PixelInternalFormat.Rgba,
				image.Width,
				image.Height,
				0,
				PixelFormat.Rgba,
				PixelType.UnsignedByte,
				image.Data);

			GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

			return texID;
		}

		#endregion
	}
}
