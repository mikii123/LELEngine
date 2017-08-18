﻿using OpenTK.Graphics.OpenGL4;
using System;
using System.IO;

namespace LELEngine.Shaders
{
    public sealed class Shader
    {
        private readonly int handle;

        public int Handle { get { return this.handle; } }

        public Shader(string path)
        {
            using (StreamReader sr = new StreamReader(System.IO.Directory.GetCurrentDirectory() + path))
            {
                string code = "";
                string tag = sr.ReadLine();

                ShaderType type = ShaderType.VertexShader;
                switch (tag)
                {
                    case "/////Vertex":
                        type = ShaderType.VertexShader;
                        break;
                    case "/////Fragment":
                        type = ShaderType.FragmentShader;
                        break;
                    case "/////Geometry":
                        type = ShaderType.GeometryShader;
                        break;
                    case "/////Compute":
                        type = ShaderType.ComputeShader;
                        break;
                    case "/////TessControl":
                        type = ShaderType.TessControlShader;
                        break;
                    case "/////TessEvaluation":
                        type = ShaderType.TessEvaluationShader;
                        break;
                }

                code = sr.ReadToEnd();

                // create shader object
                this.handle = GL.CreateShader(type);

                // set source and compile shader
                GL.ShaderSource(this.handle, code);
                GL.CompileShader(this.handle);
            }
        }

        public Shader(string code, ShaderType type)
        {
            // create shader object
            this.handle = GL.CreateShader(type);

            // set source and compile shader
            GL.ShaderSource(this.handle, code);
            GL.CompileShader(this.handle);
            Console.WriteLine(GL.GetShaderInfoLog(handle));
        }
    }
}
