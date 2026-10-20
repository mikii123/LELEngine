namespace LELEngine
{
	public struct Time
	{
		public static double frameRateD => 1d / deltaTimeD;

		public static float frameRate => (float)frameRateD;

		public static double deltaTimeD;

		/// <summary>Length of a fixed update / physics step in seconds (0.02 = 50 Hz).</summary>
		public static double fixedDeltaTimeD = 0.02;

		public static double timeD;
		public static double lastFrameD;

		/// <summary>Time at the start of the current fixed step (physics time).</summary>
		public static double fixedTimeD;

		public static float fixedTime => (float)fixedTimeD;

		/// <summary>True while FixedUpdate and the physics callbacks run.</summary>
		public static bool inFixedTimeStep;

		/// <summary>Fixed steps per frame at most: a stalled frame does not catch up forever (Unity's maximum allowed timestep).</summary>
		public static double maximumDeltaTimeD = 0.1;

		public static float deltaTime => (float)deltaTimeD;

		public static float fixedDeltaTime => (float)fixedDeltaTimeD;

		public static float time => (float)timeD;

		public static float lastFrame => (float)lastFrameD;

		public static double renderDeltaTimeD;
		public static float renderDeltaTime => (float)renderDeltaTimeD;

		/// <summary>CPU time of the last Renderer.RenderFrame (command submission), milliseconds.</summary>
		public static double cpuRenderMs;

		/// <summary>CPU time of the last scene update (Start, FixedUpdate, Update, LateUpdate), milliseconds.</summary>
		public static double cpuUpdateMs;

		/// <summary>Time the last SwapBuffers blocked for, milliseconds (GPU / display wait).</summary>
		public static double swapMs;

		/// <summary>Restarts the game clock (the editor does this when play mode starts).</summary>
		public static void ResetTime()
		{
			timeD = 0;
			lastFrameD = 0;
			fixedTimeD = 0;
		}
	}
}
