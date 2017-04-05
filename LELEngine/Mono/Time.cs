using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LELEngine
{
    public struct Time
    {
        public static double frameRateD
        {
            get
            {
                return 1d / deltaTimeD;
            }
        }
        public static float frameRate
        {
            get
            {
                return (float)frameRateD;
            }
        }

        public static double deltaTimeD;
        public static double fixedDeltaTimeD;
        public static double timeD;
        public static double lastFrameD;

        public static float deltaTime
        {
            get
            {
                return (float)(deltaTimeD);
            }
        }
        public static float fixedDeltaTime
        {
            get
            {
                return (float)(fixedDeltaTimeD);
            }
        }
        public static float time
        {
            get
            {
                return (float)(timeD);
            }
        }
        public static float lastFrame
        {
            get
            {
                return (float)(lastFrameD);
            }
        }

        public static double renderDeltaTimeD;
        public static float renderDeltaTime
        {
            get
            {
                return (float)renderDeltaTimeD;
            }
        }
    }
}
