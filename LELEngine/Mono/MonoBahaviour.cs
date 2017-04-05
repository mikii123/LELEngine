using System;
using System.Collections.Generic;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
    ///<summary>
    ///Handles scripts logic and behaviours
    ///</summary>
    public class MonoBahaviour : Window
    {
        public static Scene ActiveScene;
        private static bool loaded;
        public static bool Loaded
        {
            get
            {
                return loaded;
            }
        }
        private static List<Behaviour> Behaviours = new List<Behaviour>();
        public static List<Behaviour> ToInit = new List<Behaviour>();

        public MonoBahaviour(int width, int height, string title)
            :base(width, height, title)
        { }

        public void LoadDefaultScene()
        {
            Console.WriteLine("Loading default scene...");
            ActiveScene = new Scene("tri1", "tri2", "tri3", "tri4", "FramerateCounter");
        }

        public void InitBehaviour(Behaviour behaviour)
        {
            ToInit.Add(behaviour);
        }

        public void InitBehaviour(ICollection<Behaviour> behaviours)
        {
            ToInit.AddRange(behaviours);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            KeyDown += Input.Input_KeyDown;
            KeyUp += Input.Input_KeyUp;

            List<Behaviour> init = new List<Behaviour>(ToInit);
            foreach (var ob in init)
            {
                ob.Awake();
                ToInit.Remove(ob);
            }
            foreach (var ob in init)
            {
                ob.Start();
                ToInit.Remove(ob);
            }
            Behaviours.AddRange(init);

            loaded = true;
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            base.OnUpdateFrame(e);

            if(ToInit.Count > 0)
            {
                List<Behaviour> init = new List<Behaviour>(ToInit);
                foreach (var ob in init)
                {
                    ob.Awake();
                    ToInit.Remove(ob);
                }
                foreach (var ob in init)
                {
                    ob.Start();
                    ToInit.Remove(ob);
                }
                Behaviours.AddRange(init);
            }

            foreach (var ob in Behaviours)
            {
                ob.Update();
            }

            foreach (var ob in Behaviours)
            {
                ob.LateUpdate();
            }

            foreach(var pr in Input.Pressed)
            {
                pr.frame++;
            }
            foreach(var upr in Input.UnPressed)
            {
                upr.frame++;
            }
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            // Start stopwatch to measure render time
            renderStopwatch.Reset();
            renderStopwatch.Start();

            // clear the screen
            GL.ClearColor(0, 0, 0, 255);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            // Enable depth test
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
            //GL.Disable(EnableCap.DepthTest);
            //GL.Enable(EnableCap.Blend);
            
            // Cull Back
            GL.CullFace(CullFaceMode.Back);

            foreach(var sh in InternalStorage.Shaders)
            {
                // use shader
                sh.Value.Use();
                foreach (var ob in Behaviours)
                {
                    if (ob.UsingShader == sh.Value)
                    {
                        // call render if it's using this shader
                        ob.OnRender();
                    }
                }

                // reset state for potential further draw calls (optional, but good practice)
                GL.BindVertexArray(0);
                GL.BindBuffer(BufferTarget.ElementArrayBuffer, 0);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                GL.BindTexture(TextureTarget.Texture2D, 0);
                GL.UseProgram(0);
            }
            
            base.OnRenderFrame(e);
        }
    }
}