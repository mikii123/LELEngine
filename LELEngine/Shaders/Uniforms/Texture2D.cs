using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OpenTK.Graphics.OpenGL;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace LELEngine.Shaders.Uniforms
{
	internal sealed class Texture2D : Uniform
	{
		#region PublicFields

		public int Handle { get; }
		public int Index;

		#endregion

		#region PrivateFields

		private int textureID;

		#endregion

		#region Constructors

		public Texture2D(string name, string source, int index)
		{
			Index = index;
			Name = name;
			Bitmap bmp = new Bitmap(Directory.GetCurrentDirectory() + "/Textures/" + source);
			Handle = LoadImage(bmp);
			bmp.Dispose();
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

		private int LoadImage(Bitmap image)
		{
			int texID = GL.GenTexture();

			GL.BindTexture(TextureTarget.Texture2D, texID);
			BitmapData data = image.LockBits(
				new Rectangle(0, 0, image.Width, image.Height),
				ImageLockMode.ReadOnly,
				PixelFormat.Format32bppArgb);

			GL.TexImage2D(
				TextureTarget.Texture2D,
				0,
				PixelInternalFormat.Rgba,
				data.Width,
				data.Height,
				0,
				OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
				PixelType.UnsignedByte,
				data.Scan0);

			image.UnlockBits(data);
			GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

			return texID;
		}

		#endregion
	}
}
