using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    struct Time
    {
        public static double deltaTime;
        public static double fixedDeltaTime;
        public static double time;
        public static double lastFrame;

        public static float deltaTimeF
        {
            get
            {
                return (float)(deltaTime);
            }
        }
        public static float fixedDeltaTimeF
        {
            get
            {
                return (float)(fixedDeltaTime);
            }
        }
        public static float timeF
        {
            get
            {
                return (float)(time);
            }
        }
        public static float lastFrameF
        {
            get
            {
                return (float)(lastFrame);
            }
        }
    }
}
