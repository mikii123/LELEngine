namespace LELEngine
{
	public struct Time
	{
		public static double frameRateD => 1d / deltaTimeD;

		public static float frameRate => (float)frameRateD;

		public static double deltaTimeD;
		public static double fixedDeltaTimeD;
		public static double timeD;
		public static double lastFrameD;

		public static float deltaTime => (float)deltaTimeD;

		public static float fixedDeltaTime => (float)fixedDeltaTimeD;

		public static float time => (float)timeD;

		public static float lastFrame => (float)lastFrameD;

		public static double renderDeltaTimeD;
		public static float renderDeltaTime => (float)renderDeltaTimeD;
	}
}
