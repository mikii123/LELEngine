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
    public class Window : GameWindow
    {
        internal System.Diagnostics.Stopwatch renderStopwatch = new System.Diagnostics.Stopwatch();

        public Window(int width, int height, string title)
            :base(width, height, new GraphicsMode(32, 24, 0, 4), title,
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
            Time.deltaTimeD = e.Time;
            Time.timeD += e.Time;
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

            // Stop the stopwatch and assign the value to Time.renderDeltaTime
            renderStopwatch.Stop();
            Time.renderDeltaTimeD = renderStopwatch.ElapsedMilliseconds;
        }
    }
}
