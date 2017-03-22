using OpenTK.Graphics.OpenGL;
using System;

namespace LELEngine.Shaders.Uniforms
{
    class Vector4 : Uniform
    {
        public OpenTK.Vector4 Vector;

        public Vector4(string name, OpenTK.Vector4 vector)
        {
            Name = name;
            Vector = vector;
        }

        public override void Set(ShaderProgram program)
        {
            // get uniform location
            var handle = program.GetUniformLocation(Name);

            // set uniform value
            GL.Uniform4(handle, ref this.Vector);
        }
    }
}