using OpenTK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine.Shaders.Uniforms
{
    class Uniform
    {
        public string Name;
        public virtual void Set(ShaderProgram program)
        {

        }
    }
}
