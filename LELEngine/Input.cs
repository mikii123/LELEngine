using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenTK.Input;

namespace LELEngine
{
    public sealed class Input
    {
        public enum StandardInputAxis
        {
            MouseX,
            MouseY
        }
        public class KeyController
        {
            public Key key;
            public int frame = 0;

            public KeyController(Key k)
            {
                key = k;
                frame = 0;
            }
        }
        
        public static List<KeyController> Pressed = new List<KeyController>();
        public static List<KeyController> UnPressed = new List<KeyController>();

        static MouseState mouseState = Mouse.GetState();
        static KeyboardState keyboardState = Keyboard.GetState();
        static MouseState lastMouseState = Mouse.GetState();
        static KeyboardState lastKeyboardState = Keyboard.GetState();

        public static void EndFrame()
        {
            foreach (var pr in Pressed)
            {
                pr.frame++;
            }
            foreach (var upr in UnPressed)
            {
                upr.frame++;
            }

            lastMouseState = Mouse.GetState();
            lastKeyboardState = Keyboard.GetState();
        }
        public static void BeginFrame()
        {
            mouseState = Mouse.GetState();
            keyboardState = Keyboard.GetState();
        }

        static OpenTK.Vector2 _mousePosition;
        public static OpenTK.Vector2 mousePosition
        {
            get
            {
                return _mousePosition;
            }
            private set
            {
                _mousePosition = value;
            }
        }

        static OpenTK.Vector2 _relativeMousePosition;
        public static OpenTK.Vector2 relativeMousePosition
        {
            get
            {
                return _relativeMousePosition;
            }
            private set
            {
                _relativeMousePosition = value;
            }
        }

        public static float GetStandardAxis(StandardInputAxis axis, float sensitivity = 0.1f)
        {
            float ret = 0;

            switch(axis)
            {
                case StandardInputAxis.MouseX:
                    ret = (float)(lastMouseState.X - mouseState.X) * sensitivity;
                    break;
                case StandardInputAxis.MouseY:
                    ret = (float)(lastMouseState.Y - mouseState.Y) * sensitivity;
                    break;
            }

            return ret;
        }

        public static bool GetMouseButton(MouseButton button)
        {
            return mouseState.IsButtonDown(button);
        }

        public static bool GetKey(Key code)
        {
            return keyboardState.IsKeyDown(code);
        }

        public static bool GetKeyUp(Key code)
        {
            if (UnPressed.Exists(item => item.key == code))
            {
                KeyController kc = UnPressed.Find(item => item.key == code);
                if (kc.frame == 0)
                    return true;
                else
                    return false;
            }
            else
                return false;
        }

        public static bool GetKeyDown(Key code)
        {
            if (Pressed.Exists(item => item.key == code))
            {
                KeyController kc = Pressed.Find(item => item.key == code);
                if (kc.frame == 0)
                    return true;
                else
                    return false;
            }
            else
                return false;
        }

        public static void Input_KeyUp(object sender, KeyboardKeyEventArgs e)
        {
            if (!UnPressed.Exists(item => item.key == e.Key))
            {
                UnPressed.Add(new KeyController(e.Key));
            }
            Pressed.RemoveAll(item => item.key == e.Key);
        }

        public static void Input_KeyDown(object sender, KeyboardKeyEventArgs e)
        {
            if (!Pressed.Exists(item => item.key == e.Key))
            {
                Pressed.Add(new KeyController(e.Key));
            }
            UnPressed.RemoveAll(item => item.key == e.Key);
        }

        public static void Input_MouseMove(object sender, MouseMoveEventArgs e)
        {
            relativeMousePosition = new OpenTK.Vector2((float)e.X / (float)Game.MainWindow.Width, (float)e.Y / (float)Game.MainWindow.Height);
            mousePosition = new OpenTK.Vector2(e.X, e.Y);
        }
    }
}
