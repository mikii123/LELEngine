﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using OpenTK.Input;

namespace LELEngine
{
    static class Input
    {
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
        static KeyboardState State;
        public static List<KeyController> Pressed = new List<KeyController>();
        public static List<KeyController> UnPressed = new List<KeyController>();

        public static bool GetKey(Key code)
        {
            State = Keyboard.GetState();
            return State.IsKeyDown(code);
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
    }
}
