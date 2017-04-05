using System;
using System.Collections.Generic;
using LELEngine.Shaders;

namespace LELEngine
{
    public sealed class InternalStorage
    {
        public static Dictionary<string, ShaderProgram> Shaders = new Dictionary<string, ShaderProgram>();
        public static Dictionary<string, Material> Materials = new Dictionary<string, Material>();
        public static Dictionary<string, Mesh> Meshes = new Dictionary<string, Mesh>();

        /// <summary>
        /// Gets the shader specified by name. Returns null if it doesn't exist
        /// </summary>
        /// <param name="name">Name of the shader (with .*)</param>
        /// <returns></returns>
        public static ShaderProgram GetShader(string name)
        {
            ShaderProgram sp = null;
            Shaders.TryGetValue(name, out sp);

            return sp;
        }

        /// <summary>
        /// Gets a shader specified by name. Creates new one if it doesn't exist.
        /// </summary>
        /// <param name="name">Name of the shader (with .*)</param>
        /// <returns></returns>
        public static ShaderProgram GetOrCreateShader(string name)
        {
            ShaderProgram sp = null;
            Shaders.TryGetValue(name, out sp);
            if(sp == null)
            {
                Console.WriteLine("Creating Shader " + name);
                sp = new ShaderProgram(name);
                Shaders.Add(name, sp);
            }
            
            return sp;
        }

        /// <summary>
        /// Gets a material specified by name. Creates new one if it doesn't exist.
        /// </summary>
        /// <param name="name">Name of the material (with .*)</param>
        /// <returns></returns>
        public static Material GetOrCreateMaterial(string name)
        {
            Material mat = null;
            Materials.TryGetValue(name, out mat);
            if (mat == null)
            {
                Console.WriteLine("Creating Material " + name);
                mat = new Material(name);
                Materials.Add(name, mat);
            }

            return mat;
        }

        /// <summary>
        /// Gets a mesh specified by name. Creates new one if it doesn't exist.
        /// </summary>
        /// <param name="name">Name of the mesh (with .*)</param>
        /// <returns></returns>
        public static Mesh GetOrCreateMesh(string name)
        {
            Mesh mesh = null;
            Meshes.TryGetValue(name, out mesh);
            if (mesh == null)
            {
                Console.WriteLine("Creating Mesh " + name);
                mesh = new Mesh(name);
                Meshes.Add(name, mesh);
            }

            return mesh;
        }
    }
}
