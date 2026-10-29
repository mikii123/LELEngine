namespace LELEngine
{
	public struct Time
	{
		public static double frameRateD => 1d / deltaTimeD;

		public static float frameRate => (float)frameRateD;

		public static double deltaTimeD;

		/// <summary>
		///     Scale of the game clock (Unity's timeScale): 0 pauses updates and physics (<see cref="deltaTime" /> is 0 and no
		///     fixed step runs), 1 is real time. The UI and <see cref="unscaledDeltaTime" /> keep running.
		/// </summary>
		public static double timeScaleD = 1;

		public static float timeScale
		{
			get => (float)timeScaleD;
			set => timeScaleD = value < 0 ? 0 : value;
		}

		/// <summary>Seconds since the last frame, not scaled by <see cref="timeScale" /> (menus, pause screens).</summary>
		public static double unscaledDeltaTimeD;

		public static float unscaledDeltaTime => (float)unscaledDeltaTimeD;

		/// <summary>Real seconds since the clock started, whatever <see cref="timeScale" /> was.</summary>
		public static double unscaledTimeD;

		public static float unscaledTime => (float)unscaledTimeD;

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

		/// <summary>Game frames run so far (<see cref="Scene.UpdateScenes" /> calls).</summary>
		public static long frameCount;

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
			unscaledTimeD = 0;
			timeScaleD = 1;
		}
	}
}
