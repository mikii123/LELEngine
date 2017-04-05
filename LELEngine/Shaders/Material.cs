using System;
using System.Collections.Generic;
using System.IO;
using OpenTK;
using LELEngine.Shaders.Uniforms;

namespace LELEngine.Shaders
{
    public class Material
    {
        public string Shader = "Standard.shader";
        public List<Uniforms.Uniform> Uniforms = new List<Uniforms.Uniform>();
        private int index = 0;

        public Material(string path)
        {
            using (StreamReader sr = new StreamReader(Directory.GetCurrentDirectory() + "/Materials/" + path))
            {
                string line = "";
                line = sr.ReadLine();
                Shader = line.Trim();
                while (!sr.EndOfStream)
                {
                    line = sr.ReadLine().Trim();
                    switch (line)
                    {
                        case "uniform":
                            line = sr.ReadLine().Trim();
                            switch (line)
                            {
                                case "mat4":
                                    Uniforms.Add(new Uniforms.Matrix4(sr.ReadLine().Trim()));
                                    Console.WriteLine("Added uniform to material " + path + " shader: " + Shader);
                                    break;
                                case "vec4":
                                    string Name = sr.ReadLine().Trim();
                                    string[] words = sr.ReadLine().Split(' ');
                                    Uniforms.Add(new Uniforms.Vector4(Name, new OpenTK.Vector4(float.Parse(words[0].Trim().Replace('.', ',')), float.Parse(words[1].Trim().Replace('.', ',')), float.Parse(words[2].Trim().Replace('.', ',')), float.Parse(words[3].Trim().Replace('.', ',')))));
                                    break;
                                case "vec3":
   
                                    break;
                                case "float":

                                    break;
                                case "sampler2D":
                                    string name = sr.ReadLine().Trim();
                                    string source = sr.ReadLine().Trim();
                                    Uniforms.Add(new Texture2D(name, source, index));
                                    Console.WriteLine("Creating Texture " + name + " from " + source);
                                    index++;
                                    break;
                            }
                            break;
                    }
                }
            }
        }

        public void SetUniforms(ShaderProgram program)
        {
            foreach(var ob in Uniforms)
            {
                ob.Set(program);
            }
        }
    }
}
