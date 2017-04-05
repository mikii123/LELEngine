using LELEngine.Shaders;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;

namespace LELEngine
{
    public sealed class Lighting
    {
        public static LightProperties Directional = new LightProperties("LDirectional.dirColor", "LDirectional.dirStrength", "LDirectional.dirDirection");
        public static LightProperties Ambient = new LightProperties("LAmbient.ambColor", "LAmbient.ambStrength");
        public static LightProperties Specular = new LightProperties("LSpecular.viewPos");
        public class LightProperties
        {
            public string ColorName;
            public string StrengthName;
            public string ShineName;
            public string DirName;
            public Color4 Color;
            public float Strength;
            public float Shine;
            public Vector3 Direction;

            public LightProperties(string viewPos)
            {
                StrengthName = "LSpecular.specStrength";
                ShineName = "LSpecular.specShine";
                DirName = viewPos;
            }

            public LightProperties(string colorName, string strengthName)
            {
                ColorName = colorName;
                StrengthName = strengthName;
            }

            public LightProperties(string colorName, string strengthName, string posName)
            {
                ColorName = colorName;
                StrengthName = strengthName;
                DirName = posName;
            }
        }
        public static void SetUniforms(ShaderProgram program)
        {
            int ambcol = program.GetUniformLocation(Ambient.ColorName);
            int ambsth = program.GetUniformLocation(Ambient.StrengthName);

            int dircol = program.GetUniformLocation(Directional.ColorName);
            int dirsth = program.GetUniformLocation(Directional.StrengthName);
            int dirdir = program.GetUniformLocation(Directional.DirName);

            int specsth = program.GetUniformLocation(Specular.StrengthName);
            int specpos = program.GetUniformLocation(Specular.DirName);
            int specshi = program.GetUniformLocation(Specular.ShineName);

            GL.Uniform4(ambcol, Ambient.Color);
            GL.Uniform1(ambsth, Ambient.Strength);

            GL.Uniform1(dirsth, Directional.Strength);
            GL.Uniform3(dirdir, DirectionalLight.This.transform.forward);
            GL.Uniform4(dircol, Directional.Color);

            GL.Uniform1(specsth, Specular.Strength);
            GL.Uniform1(specshi, Specular.Shine);
            GL.Uniform3(specpos, Camera.main.transform.position);

            //Console.WriteLine(GL.GetError().ToString());
        }
    }
}
