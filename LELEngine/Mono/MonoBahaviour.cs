using System;
using System.Collections.Generic;
using OpenTK;
using OpenTK.Graphics.OpenGL4;

namespace LELEngine
{
    ///<summary>
    ///Handles scripts logic and behaviours
    ///</summary>
    class MonoBahaviour : Window
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

        public void LoadDefaultScene()
        {
            Console.WriteLine("Loading default scene...");
            ActiveScene = new Scene("tri1", "tri2", "FramerateCounter");
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
            KeyDown += MonoBahaviour_KeyDown;
            KeyUp += MonoBahaviour_KeyUp;

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

        private void MonoBahaviour_KeyUp(object sender, OpenTK.Input.KeyboardKeyEventArgs e)
        {
            if(!Input.UnPressed.Exists(item => item.key == e.Key))
            {
                Input.UnPressed.Add(new Input.KeyController(e.Key));
            }
            Input.Pressed.RemoveAll(item => item.key == e.Key);
        }

        private void MonoBahaviour_KeyDown(object sender, OpenTK.Input.KeyboardKeyEventArgs e)
        {
            if (!Input.Pressed.Exists(item => item.key == e.Key))
            {
                Input.Pressed.Add(new Input.KeyController(e.Key));
            }
            Input.UnPressed.RemoveAll(item => item.key == e.Key);
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
                GL.UseProgram(0);
            }
            

            base.OnRenderFrame(e);
        }
    }
}