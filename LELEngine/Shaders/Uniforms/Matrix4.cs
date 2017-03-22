using OpenTK;
using OpenTK.Graphics.OpenGL4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine.Shaders.Uniforms
{
    sealed class Matrix4 : Uniform
    {
        public OpenTK.Matrix4 Matrix;

        public Matrix4(string name)
        {
            Name = name;
        }
        
        public override void Set (ShaderProgram program)
        {
            // get uniform location
            var handle = program.GetUniformLocation(Name);

            // set uniform value
            GL.UniformMatrix4(handle, false, ref this.Matrix);
        }
    }
}
