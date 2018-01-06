using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using System;

namespace LELEngine
{
    public class Window : GameWindow
    {
        internal System.Diagnostics.Stopwatch renderStopwatch;

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

			renderStopwatch = new System.Diagnostics.Stopwatch();
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
            // Child loop

            // swap backbuffer
            SwapBuffers();

            // Stop the stopwatch
            renderStopwatch.Stop();
            Time.renderDeltaTimeD = renderStopwatch.ElapsedMilliseconds;
        }
    }
}
