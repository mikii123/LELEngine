using LELEngine.Shaders;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    class Window : GameWindow
    {
        public Window()
            :base(1920, 1080, new GraphicsMode(32, 24, 0, 4), "LELEngine",
            GameWindowFlags.FixedWindow, DisplayDevice.Default,
            4, 0, GraphicsContextFlags.ForwardCompatible)
        {
            Console.WriteLine("GL version: " + GL.GetString(StringName.Version) + "\nRenderer: " + GL.GetString(StringName.Renderer));
        }

        protected override void OnResize(EventArgs e)
        {
            GL.Viewport(0, 0, Width, Height);
        }

        protected override void OnLoad(EventArgs e)
        {
            Console.WriteLine("Success!\nExecuting logic...");
        }

        protected override void OnUpdateFrame(FrameEventArgs e)
        {
            Time.deltaTime = e.Time;
            Time.time += e.Time;
            if(Input.GetKeyDown(OpenTK.Input.Key.F12))
            {
                if(WindowState == WindowState.Normal)
                    WindowState = WindowState.Fullscreen;
                else
                    WindowState = WindowState.Normal;
            }
            if(Input.GetKey(OpenTK.Input.Key.AltLeft))
            {
                if(Input.GetKeyDown(OpenTK.Input.Key.F4))
                {
                    Close();
                }
            }
        }

        protected override void OnRenderFrame(FrameEventArgs e)
        {
            // Children loop

            // swap backbuffer
            SwapBuffers();
        }
    }
}
