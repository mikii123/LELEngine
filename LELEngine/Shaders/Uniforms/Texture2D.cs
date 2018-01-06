using OpenTK.Graphics.OpenGL;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace LELEngine.Shaders.Uniforms
{
    sealed class Texture2D : Uniform
    {
        public int Index = 0;

		public int Handle
		{
			get
			{
				return handle;
			}
		}

		private readonly int handle;

        public Texture2D(string name, string source, int index)
        {
            Index = index;
            Name = name;
            Bitmap bmp = new Bitmap(Directory.GetCurrentDirectory() + "/Textures/" + source);
            handle = loadImage(bmp);
            bmp.Dispose();
        }

        public Texture2D() { }

        int loadImage(Bitmap image)
        {
            int texID = GL.GenTexture();

            GL.BindTexture(TextureTarget.Texture2D, texID);
            BitmapData data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, data.Width, data.Height, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, data.Scan0);

            image.UnlockBits(data);
            GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

            return texID;
        }

        public override void Set(ShaderProgram program)
        {
            GL.ActiveTexture(TextureUnit.Texture0 + Index);
            GL.BindTexture(TextureTarget.Texture2D, Handle);
            GL.Uniform1(program.GetUniformLocation(Name), Index);
        }
    }
}
