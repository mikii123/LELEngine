using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenTK.Graphics;
using LELEngine;
using LELEngine.Shaders;

//DO NOT CALL base IN ANY OVERRIDEN FUNCTIONS
class Light : Behaviour
{
    public Color4 Color;
    public float Strength;
    public string Name;
    public void SetUniforms(ShaderProgram program)
    {

    }
}
